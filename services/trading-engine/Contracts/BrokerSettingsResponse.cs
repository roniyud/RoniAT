using System.Text.Json.Serialization;
using RoniAT.TradingEngine.Services;

namespace RoniAT.TradingEngine.Contracts;

public sealed record BrokerSettingsResponse(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("ibkr_environment")] string IbkrEnvironment,
    [property: JsonPropertyName("tastytrade_environment")] string TastytradeEnvironment,
    [property: JsonPropertyName("ibkr_paper")] IBKRSettingsResponse IbkrPaper,
    [property: JsonPropertyName("ibkr_live")] IBKRSettingsResponse IbkrLive,
    [property: JsonPropertyName("tastytrade_sandbox")] TastytradeSettingsResponse TastytradeSandbox,
    [property: JsonPropertyName("tastytrade_live")] TastytradeSettingsResponse TastytradeLive
)
{
    public static BrokerSettingsResponse FromSettings(BrokerSettings settings)
    {
        return new BrokerSettingsResponse(
            settings.Mode,
            settings.IbkrEnvironment,
            settings.TastytradeEnvironment,
            IBKRSettingsResponse.FromSettings(settings.IbkrPaper),
            IBKRSettingsResponse.FromSettings(settings.IbkrLive),
            TastytradeSettingsResponse.FromSettings(settings.TastytradeSandbox),
            TastytradeSettingsResponse.FromSettings(settings.TastytradeLive));
    }
}

public sealed record IBKRSettingsResponse(
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("client_id")] int ClientId,
    [property: JsonPropertyName("account")] string Account,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("read_only")] bool ReadOnly
)
{
    public static IBKRSettingsResponse FromSettings(IBKRSettings settings)
    {
        return new IBKRSettingsResponse(
            settings.Host,
            settings.Port,
            settings.ClientId,
            settings.Account,
            settings.Enabled,
            settings.ReadOnly);
    }
}

public sealed record TastytradeSettingsResponse(
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
)
{
    public static TastytradeSettingsResponse FromSettings(TastytradeSettings settings)
    {
        return new TastytradeSettingsResponse(
            settings.ApiBaseUrl,
            settings.StreamerBaseUrl,
            settings.AuthorizationUrl,
            settings.TokenUrl,
            settings.ClientId,
            settings.ClientSecret,
            settings.RedirectUri,
            settings.Username,
            settings.Password,
            settings.AccessToken,
            settings.RefreshToken,
            settings.AccessTokenExpiresAt,
            settings.AccountNumber,
            settings.Enabled,
            settings.ReadOnly);
    }
}
