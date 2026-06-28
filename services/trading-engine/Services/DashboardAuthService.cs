using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace RoniAT.TradingEngine.Services;

public sealed class DashboardAuthService(IConfiguration configuration, IWebHostEnvironment environment)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> tokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LoginAttemptState> loginAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan tokenLifetime = TimeSpan.FromHours(
        Math.Clamp(configuration.GetValue<int?>("DashboardAuth:TokenLifetimeHours") ?? 12, 1, 168));
    private readonly TimeSpan loginAttemptWindow = TimeSpan.FromMinutes(
        Math.Clamp(configuration.GetValue<int?>("DashboardAuth:RateLimitWindowMinutes") ?? 10, 1, 120));
    private readonly TimeSpan loginLockout = TimeSpan.FromMinutes(
        Math.Clamp(configuration.GetValue<int?>("DashboardAuth:RateLimitLockoutMinutes") ?? 15, 1, 240));
    private readonly int maxFailedLoginAttempts =
        Math.Clamp(configuration.GetValue<int?>("DashboardAuth:MaxFailedLoginAttempts") ?? 5, 2, 50);

    public bool IsEnabled { get; } = configuration.GetValue<bool?>("DashboardAuth:Enabled") ?? true;

    public string Username { get; } = configuration.GetValue<string>("DashboardAuth:Username") ?? "admin";

    private string Password { get; } =
        configuration.GetValue<string>("DashboardAuth:Password")
        ?? (environment.IsDevelopment() ? "ChangeMe123!" : "");

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Password);

    public (bool Ok, bool RateLimited, string? Token, DateTimeOffset? ExpiresAt, string Message) Login(string? username, string? password, string? clientId)
    {
        if (!IsEnabled)
        {
            return (true, false, CreateToken(out var disabledExpiry), disabledExpiry, "Authentication is disabled");
        }

        if (!IsConfigured)
        {
            return (false, false, null, null, "Dashboard password is not configured");
        }

        var attemptKey = BuildAttemptKey(username, clientId);
        var now = DateTimeOffset.UtcNow;
        if (IsLoginBlocked(attemptKey, now, out var blockedUntil))
        {
            var remainingSeconds = Math.Max(1, (int)Math.Ceiling((blockedUntil - now).TotalSeconds));
            return (false, true, null, null, $"Too many failed login attempts. Try again in {remainingSeconds} seconds.");
        }

        if (!SecureEquals(username ?? "", Username) || !SecureEquals(password ?? "", Password))
        {
            RegisterFailedLogin(attemptKey, now);
            return (false, false, null, null, "Invalid username or password");
        }

        loginAttempts.TryRemove(attemptKey, out _);
        var token = CreateToken(out var expiresAt);
        return (true, false, token, expiresAt, "Authenticated");
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

    private bool IsLoginBlocked(string attemptKey, DateTimeOffset now, out DateTimeOffset blockedUntil)
    {
        blockedUntil = default;
        if (!loginAttempts.TryGetValue(attemptKey, out var attempt))
        {
            return false;
        }

        if (attempt.BlockedUntil is null)
        {
            return false;
        }

        if (attempt.BlockedUntil <= now)
        {
            loginAttempts.TryRemove(attemptKey, out _);
            return false;
        }

        blockedUntil = attempt.BlockedUntil.Value;
        return true;
    }

    private void RegisterFailedLogin(string attemptKey, DateTimeOffset now)
    {
        loginAttempts.AddOrUpdate(
            attemptKey,
            _ => new LoginAttemptState(1, now, null),
            (_, current) =>
            {
                var failedCount = current.WindowStartedAt.Add(loginAttemptWindow) <= now
                    ? 1
                    : current.FailedCount + 1;

                var windowStartedAt = current.WindowStartedAt.Add(loginAttemptWindow) <= now
                    ? now
                    : current.WindowStartedAt;

                var blockedUntil = failedCount >= maxFailedLoginAttempts
                    ? now.Add(loginLockout)
                    : (DateTimeOffset?)null;

                return new LoginAttemptState(failedCount, windowStartedAt, blockedUntil);
            });
    }

    private static string BuildAttemptKey(string? username, string? clientId)
    {
        var normalizedUser = string.IsNullOrWhiteSpace(username) ? "(empty)" : username.Trim().ToLowerInvariant();
        var normalizedClient = string.IsNullOrWhiteSpace(clientId) ? "unknown" : clientId.Trim();
        return $"{normalizedClient}:{normalizedUser}";
    }

    private static bool SecureEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private sealed record LoginAttemptState(int FailedCount, DateTimeOffset WindowStartedAt, DateTimeOffset? BlockedUntil);
}
