using System.Text.Json.Serialization;

namespace RoniAT.TradingEngine.Contracts;

public sealed record BrokerSettingsUpdateRequest(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("ibkr_environment")] string IbkrEnvironment,
    [property: JsonPropertyName("tastytrade_environment")] string TastytradeEnvironment,
    [property: JsonPropertyName("ibkr_paper")] IBKRSettingsUpdateRequest IbkrPaper,
    [property: JsonPropertyName("ibkr_live")] IBKRSettingsUpdateRequest IbkrLive,
    [property: JsonPropertyName("tastytrade_sandbox")] TastytradeSettingsUpdateRequest TastytradeSandbox,
    [property: JsonPropertyName("tastytrade_live")] TastytradeSettingsUpdateRequest TastytradeLive
);

public sealed record IBKRSettingsUpdateRequest(
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("client_id")] int ClientId,
    [property: JsonPropertyName("account")] string Account,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("read_only")] bool ReadOnly
);

public sealed record TastytradeSettingsUpdateRequest(
    [property: JsonPropertyName("api_base_url")] string ApiBaseUrl,
    [property: JsonPropertyName("streamer_base_url")] string StreamerBaseUrl,
    [property: JsonPropertyName("authorization_url")] string AuthorizationUrl,
    [property: JsonPropertyName("token_url")] string TokenUrl,
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("client_secret")] string ClientSecret,
    [property: JsonPropertyName("redirect_uri")] string RedirectUri,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("access_token_expires_at")] DateTimeOffset? AccessTokenExpiresAt,
    [property: JsonPropertyName("account_number")] string AccountNumber,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("read_only")] bool ReadOnly
);
