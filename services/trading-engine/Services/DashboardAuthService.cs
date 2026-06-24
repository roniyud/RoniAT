using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace RoniAT.TradingEngine.Services;

public sealed class DashboardAuthService(IConfiguration configuration, IWebHostEnvironment environment)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> tokens = new(StringComparer.Ordinal);
    private readonly TimeSpan tokenLifetime = TimeSpan.FromHours(
        Math.Clamp(configuration.GetValue<int?>("DashboardAuth:TokenLifetimeHours") ?? 12, 1, 168));

    public bool IsEnabled { get; } = configuration.GetValue<bool?>("DashboardAuth:Enabled") ?? true;

    public string Username { get; } = configuration.GetValue<string>("DashboardAuth:Username") ?? "admin";

    private string Password { get; } =
        configuration.GetValue<string>("DashboardAuth:Password")
        ?? (environment.IsDevelopment() ? "ChangeMe123!" : "");

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Password);

    public (bool Ok, string? Token, DateTimeOffset? ExpiresAt, string Message) Login(string? username, string? password)
    {
        if (!IsEnabled)
        {
            return (true, CreateToken(out var disabledExpiry), disabledExpiry, "Authentication is disabled");
        }

        if (!IsConfigured)
        {
            return (false, null, null, "Dashboard password is not configured");
        }

        if (!SecureEquals(username ?? "", Username) || !SecureEquals(password ?? "", Password))
        {
            return (false, null, null, "Invalid username or password");
        }

        var token = CreateToken(out var expiresAt);
        return (true, token, expiresAt, "Authenticated");
    }

    public bool IsValidToken(string? token)
    {
        if (!IsEnabled)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        if (!tokens.TryGetValue(token, out var expiresAt))
        {
            return false;
        }

        if (expiresAt <= DateTimeOffset.UtcNow)
        {
            tokens.TryRemove(token, out _);
            return false;
        }

        return true;
    }

    public void Logout(string? token)
    {
        if (!string.IsNullOrWhiteSpace(token))
        {
            tokens.TryRemove(token, out _);
        }
    }

    private string CreateToken(out DateTimeOffset expiresAt)
    {
        expiresAt = DateTimeOffset.UtcNow.Add(tokenLifetime);
        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(bytes).Replace("+", "-", StringComparison.Ordinal).Replace("/", "_", StringComparison.Ordinal).TrimEnd('=');
        tokens[token] = expiresAt;
        return token;
    }

    private static bool SecureEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
