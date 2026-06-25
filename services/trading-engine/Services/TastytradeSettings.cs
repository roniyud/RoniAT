namespace RoniAT.TradingEngine.Services;

public sealed class TastytradeSettings
{
    public string ApiBaseUrl { get; set; } = "";
    public string StreamerBaseUrl { get; set; } = "";
    public string AuthorizationUrl { get; set; } = "";
    public string TokenUrl { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTimeOffset? AccessTokenExpiresAt { get; set; }
    public string AccountNumber { get; set; } = "";
    public bool Enabled { get; set; } = false;
    public bool ReadOnly { get; set; } = true;
}
