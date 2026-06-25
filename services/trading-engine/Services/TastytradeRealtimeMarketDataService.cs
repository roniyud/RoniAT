using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using RoniAT.TradingEngine.Hubs;

namespace RoniAT.TradingEngine.Services;

public sealed class TastytradeRealtimeMarketDataService(
    BrokerSettingsStore settingsStore,
    TastytradeQuoteTokenClient quoteTokenClient,
    TastytradeInstrumentClient instrumentClient,
    IHubContext<TradingHub> hub,
    ILogger<TastytradeRealtimeMarketDataService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, string> watchedSymbols = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> streamerToDisplaySymbol = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? reconnectSignal;

    public async Task EnsureStreamingMarketDataAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var displaySymbol = string.IsNullOrWhiteSpace(symbol) ? "MNQ1!" : symbol.Trim().ToUpperInvariant();
        var streamerSymbol = await instrumentClient.GetStreamerSymbolAsync(displaySymbol, cancellationToken);
        watchedSymbols[displaySymbol] = streamerSymbol;
        streamerToDisplaySymbol[streamerSymbol] = displaySymbol;
        SignalReconnect();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!settingsStore.Get().Mode.Equals("Tastytrade", StringComparison.OrdinalIgnoreCase)
                    || watchedSymbols.IsEmpty)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }

                using var reconnect = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                reconnectSignal = reconnect;
                await RunStreamAsync(reconnect.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Expected when a new symbol is added and the stream needs to be rebuilt.
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "Tastytrade realtime market data stream failed");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            finally
            {
                reconnectSignal = null;
            }
        }
    }

    private async Task RunStreamAsync(CancellationToken cancellationToken)
    {
        var quoteToken = await quoteTokenClient.GetQuoteTokenAsync(cancellationToken);
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await socket.ConnectAsync(NormalizeWebSocketUri(quoteToken.DxLinkUrl), cancellationToken);

        await SendAsync(socket, new Dictionary<string, object?>
        {
            ["type"] = "SETUP",
            ["channel"] = 0,
            ["keepaliveTimeout"] = 60,
            ["acceptKeepaliveTimeout"] = 60,
            ["version"] = "roniat/1.0.0"
        }, cancellationToken);
        await WaitForTypeAsync(socket, "SETUP", 0, cancellationToken);

        await SendAsync(socket, new Dictionary<string, object?>
        {
            ["type"] = "AUTH",
            ["channel"] = 0,
            ["token"] = quoteToken.Token
        }, cancellationToken);
        await WaitForAuthAsync(socket, cancellationToken);

        const int channel = 5;
        await SendAsync(socket, new Dictionary<string, object?>
        {
            ["type"] = "CHANNEL_REQUEST",
            ["channel"] = channel,
            ["service"] = "FEED",
            ["parameters"] = new Dictionary<string, object?>
            {
                ["contract"] = "AUTO"
            }
        }, cancellationToken);
        await WaitForTypeAsync(socket, "CHANNEL_OPENED", channel, cancellationToken);

        await SendAsync(socket, new Dictionary<string, object?>
        {
            ["type"] = "FEED_SETUP",
            ["channel"] = channel,
            ["acceptAggregationPeriod"] = 1,
            ["acceptDataFormat"] = "FULL",
            ["acceptEventFields"] = new Dictionary<string, string[]>
            {
                ["Quote"] = ["eventSymbol", "eventType", "eventTime", "bidPrice", "askPrice"],
                ["Trade"] = ["eventSymbol", "eventType", "eventTime", "price"]
            }
        }, cancellationToken);

        var subscriptions = watchedSymbols.Values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(streamerSymbol => new Dictionary<string, object?>
            {
                ["type"] = "Quote",
                ["symbol"] = streamerSymbol
            })
            .ToArray();

        await SendAsync(socket, new Dictionary<string, object?>
        {
            ["type"] = "FEED_SUBSCRIPTION",
            ["channel"] = channel,
            ["reset"] = true,
            ["add"] = subscriptions
        }, cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            using var message = await ReceiveJsonAsync(socket, cancellationToken);
            if (message is null)
            {
                throw new InvalidOperationException("Tastytrade DXLink realtime stream closed");
            }

            var root = message.RootElement;
            var type = ReadString(root, "type");
            if (type.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Tastytrade DXLink error: {ReadString(root, "error")} {ReadString(root, "message")}".Trim());
            }

            if (type.Equals("FEED_DATA", StringComparison.OrdinalIgnoreCase)
                && ReadInt(root, "channel") == channel
                && root.TryGetProperty("data", out var data))
            {
                await PublishTicksAsync(data, cancellationToken);
            }
        }
    }

    private async Task PublishTicksAsync(JsonElement data, CancellationToken cancellationToken)
    {
        if (data.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var eventType = ReadString(item, "eventType", "event-type");
            if (!eventType.Equals("Quote", StringComparison.OrdinalIgnoreCase)
                && !eventType.Equals("Trade", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var streamerSymbol = ReadString(item, "eventSymbol", "event-symbol");
            if (string.IsNullOrWhiteSpace(streamerSymbol))
            {
                continue;
            }

            var price = eventType.Equals("Trade", StringComparison.OrdinalIgnoreCase)
                ? ReadDecimal(item, "price")
                : ReadQuoteMidpoint(item);
            if (price is null || price <= 0)
            {
                continue;
            }

            var displaySymbol = streamerToDisplaySymbol.TryGetValue(streamerSymbol, out var mappedSymbol)
                ? mappedSymbol
                : streamerSymbol;
            var eventTime = ReadLong(item, "eventTime", "event-time", "time");
            var unixSeconds = eventTime > 0
                ? eventTime / 1000
                : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            await hub.Clients.All.SendAsync("market.tick", new
            {
                symbol = displaySymbol,
                price = price.Value,
                time = unixSeconds,
                source = "tastytrade"
            }, cancellationToken);
        }
    }

    private static decimal? ReadQuoteMidpoint(JsonElement item)
    {
        var bid = ReadDecimal(item, "bidPrice", "bid-price");
        var ask = ReadDecimal(item, "askPrice", "ask-price");
        if (bid is > 0 && ask is > 0)
        {
            return (bid.Value + ask.Value) / 2m;
        }

        return bid is > 0 ? bid : ask;
    }

    private void SignalReconnect()
    {
        try
        {
            reconnectSignal?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task WaitForTypeAsync(ClientWebSocket socket, string expectedType, int channel, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var message = await ReceiveJsonAsync(socket, cancellationToken)
                ?? throw new InvalidOperationException($"Tastytrade DXLink closed before {expectedType}");
            var root = message.RootElement;
            var type = ReadString(root, "type");
            if (type.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Tastytrade DXLink error: {ReadString(root, "error")} {ReadString(root, "message")}".Trim());
            }

            if (type.Equals(expectedType, StringComparison.OrdinalIgnoreCase)
                && ReadInt(root, "channel") == channel)
            {
                return;
            }
        }
    }

    private static async Task WaitForAuthAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (true)
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new InvalidOperationException("Tastytrade DXLink authentication timed out");
            }

            using var message = await ReceiveJsonAsync(socket, cancellationToken)
                ?? throw new InvalidOperationException("Tastytrade DXLink closed before auth completed");
            var root = message.RootElement;
            var type = ReadString(root, "type");
            if (type.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Tastytrade DXLink error: {ReadString(root, "error")} {ReadString(root, "message")}".Trim());
            }

            if (!type.Equals("AUTH_STATE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ReadString(root, "state").Equals("AUTHORIZED", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
    }

    private static async Task<JsonDocument?> ReceiveJsonAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return JsonDocument.Parse(Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static async Task SendAsync(ClientWebSocket socket, object payload, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static Uri NormalizeWebSocketUri(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Tastytrade DXLink URL is missing");
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = $"ws://{trimmed[7..]}";
        }
        else if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = $"wss://{trimmed[8..]}";
        }

        return new Uri(trimmed);
    }

    private static string ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(name, out var value))
            {
                return value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString() ?? "",
                    JsonValueKind.Number => value.GetRawText(),
                    _ => ""
                };
            }
        }

        return "";
    }

    private static int ReadInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var parsed) => parsed,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => 0
        };
    }

    private static long ReadLong(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed))
            {
                return parsed;
            }

            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out parsed))
            {
                return parsed;
            }
        }

        return 0;
    }

    private static decimal? ReadDecimal(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var parsed))
            {
                return parsed;
            }

            if (value.ValueKind == JsonValueKind.String
                && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }
        }

        return null;
    }
}
