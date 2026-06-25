using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace RoniAT.TradingEngine.Services;

public sealed class TastytradeQuoteTokenClient(
    BrokerSettingsStore settingsStore,
    IHttpClientFactory httpClientFactory,
    TastytradeOAuthService oauthService)
{
    public async Task<TastytradeQuoteToken> GetQuoteTokenAsync(CancellationToken cancellationToken = default)
    {
        var settings = settingsStore.GetActiveTastytradeSettings();
        if (string.IsNullOrWhiteSpace(settings.AccessToken))
        {
            throw new InvalidOperationException("Tastytrade access token is required for market data");
        }

        var token = await TryGetQuoteTokenAsync(settings, cancellationToken);
        if (token is not null)
        {
            return token;
        }

        var refresh = await oauthService.RefreshAccessTokenAsync(cancellationToken);
        if (!refresh.Ok)
        {
            throw new InvalidOperationException($"Tastytrade access token expired and refresh failed: {refresh.Message}");
        }

        token = await TryGetQuoteTokenAsync(settingsStore.GetActiveTastytradeSettings(), cancellationToken);
        return token ?? throw new InvalidOperationException("Tastytrade quote token response did not include a DXLink token");
    }

    private async Task<TastytradeQuoteToken?> TryGetQuoteTokenAsync(TastytradeSettings settings, CancellationToken cancellationToken)
    {
        var baseUrl = settings.ApiBaseUrl.TrimEnd('/');
        var client = httpClientFactory.CreateClient("tastytrade");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api-quote-tokens");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("Accept-Version", "20240501");

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Tastytrade quote token returned HTTP {(int)response.StatusCode}: {TrimForLog(body)}");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var token = ReadString(root, "token", "dxlink-token", "dxlink_token", "quote-token", "quote_token");
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var dxLinkUrl = ReadString(root, "dxlink-url", "dxlink_url", "websocket-url", "websocket_url", "url");
        if (string.IsNullOrWhiteSpace(dxLinkUrl))
        {
            dxLinkUrl = settings.StreamerBaseUrl;
        }

        return new TastytradeQuoteToken(token.Trim(), dxLinkUrl.Trim());
    }

    private static string ReadString(JsonElement element, params string[] names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (names.Any(name => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    return property.Value.ValueKind switch
                    {
                        JsonValueKind.String => property.Value.GetString() ?? "",
                        JsonValueKind.Number => property.Value.GetRawText(),
                        _ => ""
                    };
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

public sealed record TastytradeQuoteToken(string Token, string DxLinkUrl);
