using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RoniAT.TradingEngine.Services;

public sealed class TastytradeOAuthService(
    BrokerSettingsStore settingsStore,
    TastytradeOAuthStateStore stateStore,
    IHttpClientFactory httpClientFactory,
    ILogger<TastytradeOAuthService> logger)
{
    public TastytradeOAuthStartResult BuildAuthorizationUrl()
    {
        var brokerSettings = settingsStore.Get();
        if (!brokerSettings.Mode.Equals("Tastytrade", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Broker mode must be Tastytrade before starting OAuth");
        }

        var settings = settingsStore.GetActiveTastytradeSettings();
        if (string.IsNullOrWhiteSpace(settings.ClientId))
        {
            throw new InvalidOperationException("Tastytrade Client ID is required before starting OAuth");
        }

        if (string.IsNullOrWhiteSpace(settings.RedirectUri))
        {
            throw new InvalidOperationException("Tastytrade Redirect URI is required before starting OAuth");
        }

        var state = stateStore.Create(brokerSettings.TastytradeEnvironment);
        var authorizationUrl = AddQueryString(settings.AuthorizationUrl, new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = settings.ClientId,
            ["redirect_uri"] = settings.RedirectUri,
            ["state"] = state.Value
        });

        return new TastytradeOAuthStartResult(authorizationUrl, brokerSettings.TastytradeEnvironment, settings.RedirectUri);
    }

    public async Task<TastytradeOAuthCallbackResult> HandleCallbackAsync(string? code, string? state, string? error, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            return new TastytradeOAuthCallbackResult(false, $"Tastytrade OAuth failed: {error}", null, null);
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return new TastytradeOAuthCallbackResult(false, "Tastytrade OAuth callback is missing code", null, null);
        }

        if (string.IsNullOrWhiteSpace(state) || !stateStore.TryTake(state, out var savedState))
        {
            return new TastytradeOAuthCallbackResult(false, "Tastytrade OAuth state is missing or expired", null, null);
        }

        var settings = settingsStore.GetTastytradeSettings(savedState.Environment);
        if (string.IsNullOrWhiteSpace(settings.ClientId) || string.IsNullOrWhiteSpace(settings.ClientSecret))
        {
            return new TastytradeOAuthCallbackResult(false, "Tastytrade Client ID and Client Secret are required to exchange the OAuth code", savedState.Environment, null);
        }

        try
        {
            var client = httpClientFactory.CreateClient("tastytrade");
            var (response, body) = await SendTokenRequestAsync(
                client,
                settings.TokenUrl,
                settings.ClientId,
                settings.ClientSecret,
                new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = settings.RedirectUri,
                    ["client_id"] = settings.ClientId,
                    ["client_secret"] = settings.ClientSecret
                },
                new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = settings.RedirectUri
                },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new TastytradeOAuthCallbackResult(
                    false,
                    $"Tastytrade token exchange failed with HTTP {(int)response.StatusCode}: {TrimForLog(body)}",
                    savedState.Environment,
                    null);
            }

            using var document = JsonDocument.Parse(body);
            var accessToken = ReadString(document.RootElement, "access_token");
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return new TastytradeOAuthCallbackResult(false, "Tastytrade token exchange did not return access_token", savedState.Environment, null);
            }

            var refreshToken = ReadString(document.RootElement, "refresh_token");
            var expiresIn = ReadInt(document.RootElement, "expires_in");
            var expiresAt = expiresIn is > 0 ? DateTimeOffset.UtcNow.AddSeconds(expiresIn.Value) : (DateTimeOffset?)null;
            settingsStore.UpdateTastytradeTokens(savedState.Environment, accessToken, refreshToken, expiresAt);

            return new TastytradeOAuthCallbackResult(true, $"Tastytrade {savedState.Environment} OAuth connected", savedState.Environment, expiresAt);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Tastytrade OAuth callback failed");
            return new TastytradeOAuthCallbackResult(false, $"Tastytrade OAuth callback failed: {exception.Message}", savedState.Environment, null);
        }
    }

    public async Task<TastytradeOAuthCallbackResult> RefreshAccessTokenAsync(CancellationToken cancellationToken)
    {
        var brokerSettings = settingsStore.Get();
        if (!brokerSettings.Mode.Equals("Tastytrade", StringComparison.OrdinalIgnoreCase))
        {
            return new TastytradeOAuthCallbackResult(false, "Broker mode must be Tastytrade before refreshing a token", null, null);
        }

        var settings = settingsStore.GetActiveTastytradeSettings();
        if (string.IsNullOrWhiteSpace(settings.ClientSecret))
        {
            return new TastytradeOAuthCallbackResult(false, "Tastytrade Client Secret is required", brokerSettings.TastytradeEnvironment, null);
        }

        if (string.IsNullOrWhiteSpace(settings.RefreshToken))
        {
            return new TastytradeOAuthCallbackResult(false, "Tastytrade Refresh Token is required. Create a Grant in tastytrade first, then paste the refresh token here.", brokerSettings.TastytradeEnvironment, null);
        }

        try
        {
            var client = httpClientFactory.CreateClient("tastytrade");
            var (response, body) = await SendTokenRequestAsync(
                client,
                settings.TokenUrl,
                settings.ClientId,
                settings.ClientSecret,
                new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = settings.RefreshToken,
                    ["client_id"] = settings.ClientId,
                    ["client_secret"] = settings.ClientSecret
                },
                new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = settings.RefreshToken
                },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new TastytradeOAuthCallbackResult(
                    false,
                    $"Tastytrade token refresh failed with HTTP {(int)response.StatusCode}: {TrimForLog(body)}",
                    brokerSettings.TastytradeEnvironment,
                    null);
            }

            using var document = JsonDocument.Parse(body);
            var accessToken = ReadString(document.RootElement, "access_token");
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return new TastytradeOAuthCallbackResult(false, "Tastytrade token refresh did not return access_token", brokerSettings.TastytradeEnvironment, null);
            }

            var refreshToken = ReadString(document.RootElement, "refresh_token");
            var expiresIn = ReadInt(document.RootElement, "expires_in");
            var expiresAt = expiresIn is > 0 ? DateTimeOffset.UtcNow.AddSeconds(expiresIn.Value) : (DateTimeOffset?)null;
            settingsStore.UpdateTastytradeTokens(brokerSettings.TastytradeEnvironment, accessToken, refreshToken, expiresAt);

            return new TastytradeOAuthCallbackResult(true, $"Tastytrade {brokerSettings.TastytradeEnvironment} access token refreshed", brokerSettings.TastytradeEnvironment, expiresAt);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Tastytrade token refresh failed");
            return new TastytradeOAuthCallbackResult(false, $"Tastytrade token refresh failed: {exception.Message}", brokerSettings.TastytradeEnvironment, null);
        }
    }

    private static string AddQueryString(string baseUrl, IReadOnlyDictionary<string, string> values)
    {
        var separator = baseUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return baseUrl
            + separator
            + string.Join("&", values.Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"));
    }

    private static async Task<(HttpResponseMessage Response, string Body)> SendTokenRequestAsync(
        HttpClient client,
        string tokenUrl,
        string clientId,
        string clientSecret,
        IReadOnlyDictionary<string, string> formCredentialsFields,
        IReadOnlyDictionary<string, string> basicAuthFields,
        CancellationToken cancellationToken)
    {
        var first = await SendTokenRequestOnceAsync(
            client,
            tokenUrl,
            formCredentialsFields,
            authorization: null,
            cancellationToken);

        if (first.Response.IsSuccessStatusCode || first.Response.StatusCode != System.Net.HttpStatusCode.Unauthorized)
        {
            return first;
        }

        first.Response.Dispose();
        var basicCredential = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
        return await SendTokenRequestOnceAsync(
            client,
            tokenUrl,
            basicAuthFields,
            new AuthenticationHeaderValue("Basic", basicCredential),
            cancellationToken);
    }

    private static async Task<(HttpResponseMessage Response, string Body)> SendTokenRequestOnceAsync(
        HttpClient client,
        string tokenUrl,
        IReadOnlyDictionary<string, string> fields,
        AuthenticationHeaderValue? authorization,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(fields);
        var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = content
        };
        request.Headers.Accept.ParseAdd("application/json");
        if (authorization is not null)
        {
            request.Headers.Authorization = authorization;
        }

        var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return (response, body);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static string TrimForLog(string value)
    {
        return value.Length <= 400 ? value : $"{value[..400]}...";
    }
}

public sealed record TastytradeOAuthStartResult(string AuthorizationUrl, string Environment, string RedirectUri);

public sealed record TastytradeOAuthCallbackResult(bool Ok, string Message, string? Environment, DateTimeOffset? AccessTokenExpiresAt);
