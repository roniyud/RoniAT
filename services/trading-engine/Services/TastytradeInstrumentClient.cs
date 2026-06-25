using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace RoniAT.TradingEngine.Services;

public sealed class TastytradeInstrumentClient(
    BrokerSettingsStore settingsStore,
    IHttpClientFactory httpClientFactory,
    TastytradeOAuthService oauthService)
{
    public async Task<string> GetStreamerSymbolAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var routedSymbol = ResolveFutureSymbol(symbol);
        if (!routedSymbol.StartsWith('/'))
        {
            return routedSymbol;
        }

        var settings = settingsStore.GetActiveTastytradeSettings();
        var streamerSymbol = await TryGetFutureStreamerSymbolAsync(settings, routedSymbol, cancellationToken);
        if (!string.IsNullOrWhiteSpace(streamerSymbol))
        {
            return streamerSymbol;
        }

        var refresh = await oauthService.RefreshAccessTokenAsync(cancellationToken);
        if (refresh.Ok)
        {
            streamerSymbol = await TryGetFutureStreamerSymbolAsync(settingsStore.GetActiveTastytradeSettings(), routedSymbol, cancellationToken);
            if (!string.IsNullOrWhiteSpace(streamerSymbol))
            {
                return streamerSymbol;
            }
        }

        return routedSymbol;
    }

    private async Task<string> TryGetFutureStreamerSymbolAsync(TastytradeSettings settings, string routedSymbol, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.AccessToken))
        {
            return "";
        }

        var baseUrl = settings.ApiBaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/instruments/futures?symbol[]={Uri.EscapeDataString(routedSymbol)}";
        var client = httpClientFactory.CreateClient("tastytrade");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return "";
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Tastytrade futures instrument lookup returned HTTP {(int)response.StatusCode}: {TrimForLog(body)}");
        }

        using var document = JsonDocument.Parse(body);
        return ReadString(document.RootElement, "streamer-symbol", "streamer_symbol");
    }

    private static string ResolveFutureSymbol(string symbol)
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
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (names.Any(name => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : "";
                }

                var nested = ReadString(property.Value, names);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = ReadString(item, names);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return "";
    }

    private static string TrimForLog(string value)
    {
        return value.Length <= 500 ? value : $"{value[..500]}...";
    }
}
