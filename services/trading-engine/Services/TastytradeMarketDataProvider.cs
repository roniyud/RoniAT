using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RoniAT.TradingEngine.Contracts;

namespace RoniAT.TradingEngine.Services;

public sealed class TastytradeMarketDataProvider(
    BrokerSettingsStore settingsStore,
    TastytradeQuoteTokenClient quoteTokenClient,
    TastytradeInstrumentClient instrumentClient,
    ILogger<TastytradeMarketDataProvider> logger) : IMarketDataProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlyDictionary<string, (string DxPeriod, int Seconds, int Count)> Timeframes =
        new Dictionary<string, (string DxPeriod, int Seconds, int Count)>(StringComparer.OrdinalIgnoreCase)
        {
            ["1m"] = ("m", 60, 300),
            ["5m"] = ("5m", 300, 300),
            ["15m"] = ("15m", 900, 300),
            ["1h"] = ("h", 3600, 240)
        };

    public async Task<IReadOnlyList<CandleResponse>> GetCandlesAsync(
        string symbol,
        string timeframe,
        CancellationToken cancellationToken = default)
    {
        var brokerSettings = settingsStore.Get();
        if (!brokerSettings.Mode.Equals("Tastytrade", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Broker mode is not Tastytrade");
        }

        var settings = settingsStore.GetActiveTastytradeSettings();
        if (!settings.Enabled)
        {
            throw new InvalidOperationException($"Tastytrade {brokerSettings.TastytradeEnvironment} is disabled");
        }

        if (!Timeframes.TryGetValue(timeframe.Trim().ToLowerInvariant(), out var frame))
        {
            throw new ArgumentException("Unsupported timeframe. Use 1m, 5m, 15m, or 1h.", nameof(timeframe));
        }

        var quoteToken = await quoteTokenClient.GetQuoteTokenAsync(cancellationToken);
        var streamerSymbol = await instrumentClient.GetStreamerSymbolAsync(symbol, cancellationToken);
        var candleSymbol = $"{streamerSymbol}{{={frame.DxPeriod}}}";
        var fromTime = GetFromTime(frame);

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

        await WaitForTypeAsync(socket, "SETUP", channel: 0, cancellationToken);
        await SendAsync(socket, new Dictionary<string, object?>
        {
            ["type"] = "AUTH",
            ["channel"] = 0,
            ["token"] = quoteToken.Token
        }, cancellationToken);
        await WaitForAuthAsync(socket, cancellationToken);

        const int channel = 1;
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
                ["Candle"] = ["eventSymbol", "eventType", "time", "eventTime", "open", "high", "low", "close"]
            }
        }, cancellationToken);

        await SendAsync(socket, new Dictionary<string, object?>
        {
            ["type"] = "FEED_SUBSCRIPTION",
            ["channel"] = channel,
            ["add"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "Candle",
                    ["symbol"] = candleSymbol,
                    ["fromTime"] = fromTime
                }
            }
        }, cancellationToken);

        var candles = await ReadCandlesAsync(socket, channel, candleSymbol, frame.Count, cancellationToken);
        await TryCloseChannelAsync(socket, channel, CancellationToken.None);

        if (candles.Count == 0)
        {
            throw new InvalidOperationException($"Tastytrade DXLink returned no candles for {candleSymbol}");
        }

        return candles
            .GroupBy(candle => candle.Time)
            .Select(group => group.Last())
            .OrderBy(candle => candle.Time)
            .TakeLast(frame.Count)
            .ToArray();
    }

    private static long GetFromTime((string DxPeriod, int Seconds, int Count) frame)
    {
        var lookbackMultiplier = frame.Seconds >= 3600 ? 6 : 4;
        var lookbackSeconds = frame.Seconds * frame.Count * lookbackMultiplier;
        return DateTimeOffset.UtcNow.AddSeconds(-lookbackSeconds).ToUnixTimeMilliseconds();
    }

    private async Task<IReadOnlyList<CandleResponse>> ReadCandlesAsync(
        ClientWebSocket socket,
        int channel,
        string candleSymbol,
        int targetCount,
        CancellationToken cancellationToken)
    {
        var candles = new List<CandleResponse>(targetCount);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(7);
        while (DateTimeOffset.UtcNow < deadline && candles.Count < targetCount)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var message = await ReceiveJsonAsync(socket, timeout.Token);
            if (message is null)
            {
                continue;
            }

            var root = message.RootElement;
            var type = ReadString(root, "type");
            if (type.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Tastytrade DXLink error: {ReadString(root, "error")} {ReadString(root, "message")}".Trim());
            }

            if (!type.Equals("FEED_DATA", StringComparison.OrdinalIgnoreCase)
                || ReadInt(root, "channel") != channel
                || !root.TryGetProperty("data", out var data))
            {
                continue;
            }

            foreach (var candle in ParseFullCandles(data, candleSymbol))
            {
                candles.Add(candle);
            }
        }

        return candles;
    }

    private static IEnumerable<CandleResponse> ParseFullCandles(JsonElement data, string candleSymbol)
    {
        if (data.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var eventType = ReadString(item, "eventType", "event-type");
            if (!eventType.Equals("Candle", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var eventSymbol = ReadString(item, "eventSymbol", "event-symbol");
            if (!string.IsNullOrWhiteSpace(eventSymbol)
                && !eventSymbol.Equals(candleSymbol, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var timeMs = ReadLong(item, "time", "eventTime", "event-time");
            var open = ReadDecimal(item, "open");
            var high = ReadDecimal(item, "high");
            var low = ReadDecimal(item, "low");
            var close = ReadDecimal(item, "close");
            if (timeMs <= 0 || open is null || high is null || low is null || close is null)
            {
                continue;
            }

            yield return new CandleResponse(
                timeMs / 1000,
                open.Value,
                high.Value,
                low.Value,
                close.Value);
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

            var state = ReadString(root, "state");
            if (state.Equals("AUTHORIZED", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // The server may send UNAUTHORIZED immediately after SETUP to indicate
            // that AUTH is required. Keep waiting for the AUTH response.
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

        var json = Encoding.UTF8.GetString(stream.ToArray());
        return JsonDocument.Parse(json);
    }

    private static async Task SendAsync(ClientWebSocket socket, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private async Task TryCloseChannelAsync(ClientWebSocket socket, int channel, CancellationToken cancellationToken)
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await SendAsync(socket, new Dictionary<string, object?>
                {
                    ["type"] = "CHANNEL_CANCEL",
                    ["channel"] = channel
                }, cancellationToken);
            }
        }
        catch (Exception error)
        {
            logger.LogDebug(error, "Failed to close Tastytrade DXLink channel {Channel}", channel);
        }
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

    private static string ResolveStreamerSymbol(string symbol)
    {
        var normalized = symbol.Trim().ToUpperInvariant();
        if (normalized.StartsWith('/'))
        {
            return normalized;
        }

        var root = normalized.Replace("1!", "", StringComparison.OrdinalIgnoreCase);
        if (root is "MNQ" or "NQ" or "MES" or "ES")
        {
            return $"/{root}{GetActiveQuarterlyFuturesCode(DateTime.UtcNow)}";
        }

        return normalized;
    }

    private static string GetActiveQuarterlyFuturesCode(DateTime utcNow)
    {
        var year = utcNow.Year;
        var monthCodes = new (int Month, char Code)[] { (3, 'H'), (6, 'M'), (9, 'U'), (12, 'Z') };
        foreach (var (month, code) in monthCodes)
        {
            var rollDate = GetThirdFriday(year, month).Date;
            if (utcNow.Date <= rollDate)
            {
                return $"{code}{year % 10}";
            }
        }

        return $"H{(year + 1) % 10}";
    }

    private static DateTime GetThirdFriday(int year, int month)
    {
        var date = new DateTime(year, month, 1);
        while (date.DayOfWeek != DayOfWeek.Friday)
        {
            date = date.AddDays(1);
        }

        return date.AddDays(14);
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

    private static decimal? ReadDecimal(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
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

        return null;
    }
}
