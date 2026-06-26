using System.Text.Json;
using Microsoft.Extensions.Options;

namespace RoniAT.TradingEngine.Services;

public sealed class BrokerSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object syncRoot = new();
    private readonly string filePath;
    private BrokerSettings current;

    public BrokerSettingsStore(IOptions<BrokerSettings> defaults, IWebHostEnvironment environment)
    {
        filePath = Path.Combine(environment.ContentRootPath, "storage", "broker-settings.json");
        current = LoadOrCreate(defaults.Value);
    }

    public BrokerSettings Get()
    {
        lock (syncRoot)
        {
            return Clone(current);
        }
    }

    public IBKRSettings GetActiveIBKRSettings()
    {
        var settings = Get();
        return settings.IbkrEnvironment.Equals("Live", StringComparison.OrdinalIgnoreCase)
            ? settings.IbkrLive
            : settings.IbkrPaper;
    }

    public TastytradeSettings GetActiveTastytradeSettings()
    {
        var settings = Get();
        return settings.TastytradeEnvironment.Equals("Live", StringComparison.OrdinalIgnoreCase)
            ? settings.TastytradeLive
            : settings.TastytradeSandbox;
    }

    public TastytradeSettings GetTastytradeSettings(string environment)
    {
        var settings = Get();
        return environment.Equals("Live", StringComparison.OrdinalIgnoreCase)
            ? settings.TastytradeLive
            : settings.TastytradeSandbox;
    }

    public BrokerSettingsUpdateResult UpdateTastytradeTokens(string environment, string accessToken, string? refreshToken, DateTimeOffset? accessTokenExpiresAt)
    {
        lock (syncRoot)
        {
            var next = Clone(current);
            var target = environment.Equals("Live", StringComparison.OrdinalIgnoreCase)
                ? next.TastytradeLive
                : next.TastytradeSandbox;

            target.AccessToken = accessToken.Trim();
            if (!string.IsNullOrWhiteSpace(refreshToken))
            {
                target.RefreshToken = refreshToken.Trim();
            }

            target.AccessTokenExpiresAt = accessTokenExpiresAt;
            current = Normalize(next);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, JsonSerializer.Serialize(current, JsonOptions));
            return new BrokerSettingsUpdateResult(true, Clone(current), []);
        }
    }

    public BrokerSettingsUpdateResult Update(BrokerSettings next)
    {
        lock (syncRoot)
        {
            var normalized = Normalize(PreserveRuntimeSecrets(next, current));
            var errors = Validate(normalized);
            if (errors.Count > 0)
            {
                return new BrokerSettingsUpdateResult(false, Clone(current), errors);
            }

            current = normalized;
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, JsonSerializer.Serialize(current, JsonOptions));
            return new BrokerSettingsUpdateResult(true, Clone(current), []);
        }
    }

    private BrokerSettings LoadOrCreate(BrokerSettings defaults)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        if (!File.Exists(filePath))
        {
            var normalizedDefaults = Normalize(defaults);
            File.WriteAllText(filePath, JsonSerializer.Serialize(normalizedDefaults, JsonOptions));
            return normalizedDefaults;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<BrokerSettings>(File.ReadAllText(filePath), JsonOptions);
            return Normalize(loaded ?? defaults);
        }
        catch (JsonException)
        {
            return Normalize(defaults);
        }
    }

    private static BrokerSettings Normalize(BrokerSettings settings)
    {
        return new BrokerSettings
        {
            Mode = NormalizeOption(settings.Mode, "Paper", ["Paper", "IBKR", "Tastytrade"]),
            IbkrEnvironment = NormalizeOption(settings.IbkrEnvironment, "Paper", ["Paper", "Live"]),
            TastytradeEnvironment = NormalizeOption(settings.TastytradeEnvironment, "Sandbox", ["Sandbox", "Live"]),
            IbkrPaper = NormalizeIBKR(settings.IbkrPaper, paperDefaults: true),
            IbkrLive = NormalizeIBKR(settings.IbkrLive, paperDefaults: false),
            TastytradeSandbox = NormalizeTastytrade(settings.TastytradeSandbox, sandboxDefaults: true),
            TastytradeLive = NormalizeTastytrade(settings.TastytradeLive, sandboxDefaults: false)
        };
    }

    private static IBKRSettings NormalizeIBKR(IBKRSettings settings, bool paperDefaults)
    {
        return new IBKRSettings
        {
            Host = string.IsNullOrWhiteSpace(settings.Host) ? "127.0.0.1" : settings.Host.Trim(),
            Port = settings.Port > 0 ? settings.Port : paperDefaults ? 4002 : 4001,
            ClientId = settings.ClientId > 0 ? settings.ClientId : paperDefaults ? 10 : 11,
            Account = settings.Account?.Trim() ?? "",
            Enabled = settings.Enabled,
            ReadOnly = settings.ReadOnly
        };
    }

    private static TastytradeSettings NormalizeTastytrade(TastytradeSettings settings, bool sandboxDefaults)
    {
        var apiBaseUrl = string.IsNullOrWhiteSpace(settings.ApiBaseUrl)
            ? sandboxDefaults ? "https://api.cert.tastyworks.com" : "https://api.tastyworks.com"
            : settings.ApiBaseUrl.Trim().TrimEnd('/');

        return new TastytradeSettings
        {
            ApiBaseUrl = apiBaseUrl,
            StreamerBaseUrl = string.IsNullOrWhiteSpace(settings.StreamerBaseUrl)
                ? sandboxDefaults ? "wss://streamer.cert.tastyworks.com" : "wss://streamer.tastyworks.com"
                : settings.StreamerBaseUrl.Trim().TrimEnd('/'),
            AuthorizationUrl = NormalizeTastytradeAuthorizationUrl(settings.AuthorizationUrl, sandboxDefaults),
            TokenUrl = string.IsNullOrWhiteSpace(settings.TokenUrl)
                ? $"{apiBaseUrl}/oauth/token"
                : settings.TokenUrl.Trim(),
            ClientId = settings.ClientId?.Trim() ?? "",
            ClientSecret = settings.ClientSecret ?? "",
            RedirectUri = string.IsNullOrWhiteSpace(settings.RedirectUri)
                ? "http://localhost:3001/api/tastytrade/oauth/callback"
                : settings.RedirectUri.Trim(),
            Username = settings.Username?.Trim() ?? "",
            Password = settings.Password ?? "",
            AccessToken = settings.AccessToken?.Trim() ?? "",
            RefreshToken = settings.RefreshToken?.Trim() ?? "",
            AccessTokenExpiresAt = settings.AccessTokenExpiresAt,
            AccountNumber = settings.AccountNumber?.Trim() ?? "",
            Enabled = settings.Enabled,
            ReadOnly = settings.ReadOnly
        };
    }

    private static string NormalizeOption(string value, string fallback, IReadOnlyCollection<string> allowed)
    {
        var match = allowed.FirstOrDefault(item => item.Equals(value, StringComparison.OrdinalIgnoreCase));
        return match ?? fallback;
    }

    private static List<string> Validate(BrokerSettings settings)
    {
        var errors = new List<string>();

        ValidateIBKR(settings.IbkrPaper, "ibkr_paper", errors);
        ValidateIBKR(settings.IbkrLive, "ibkr_live", errors);
        ValidateTastytrade(settings.TastytradeSandbox, "tastytrade_sandbox", errors);
        ValidateTastytrade(settings.TastytradeLive, "tastytrade_live", errors);

        return errors;
    }

    private static void ValidateIBKR(IBKRSettings settings, string prefix, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(settings.Host))
        {
            errors.Add($"{prefix}.host is required");
        }

        if (settings.Port is < 1 or > 65535)
        {
            errors.Add($"{prefix}.port must be between 1 and 65535");
        }

        if (settings.ClientId < 1)
        {
            errors.Add($"{prefix}.client_id must be greater than zero");
        }
    }

    private static void ValidateTastytrade(TastytradeSettings settings, string prefix, List<string> errors)
    {
        if (!Uri.TryCreate(settings.ApiBaseUrl, UriKind.Absolute, out var apiUri)
            || apiUri.Scheme is not ("http" or "https"))
        {
            errors.Add($"{prefix}.api_base_url must be a valid http/https URL");
        }

        if (!Uri.TryCreate(settings.StreamerBaseUrl, UriKind.Absolute, out var streamerUri)
            || streamerUri.Scheme is not ("ws" or "wss"))
        {
            errors.Add($"{prefix}.streamer_base_url must be a valid ws/wss URL");
        }

        if (!Uri.TryCreate(settings.AuthorizationUrl, UriKind.Absolute, out var authorizationUri)
            || authorizationUri.Scheme is not ("http" or "https"))
        {
            errors.Add($"{prefix}.authorization_url must be a valid http/https URL");
        }

        if (!Uri.TryCreate(settings.TokenUrl, UriKind.Absolute, out var tokenUri)
            || tokenUri.Scheme is not ("http" or "https"))
        {
            errors.Add($"{prefix}.token_url must be a valid http/https URL");
        }

        if (!Uri.TryCreate(settings.RedirectUri, UriKind.Absolute, out var redirectUri)
            || redirectUri.Scheme is not ("http" or "https"))
        {
            errors.Add($"{prefix}.redirect_uri must be a valid http/https URL");
        }

        if (!settings.Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.AccessToken))
        {
            if (string.IsNullOrWhiteSpace(settings.ClientId) || string.IsNullOrWhiteSpace(settings.ClientSecret))
            {
                errors.Add($"{prefix}.access_token or OAuth client credentials are required when enabled");
            }
        }

        if (string.IsNullOrWhiteSpace(settings.AccountNumber))
        {
            errors.Add($"{prefix}.account_number is required when enabled");
        }
    }

    private static string NormalizeTastytradeAuthorizationUrl(string authorizationUrl, bool sandboxDefaults)
    {
        if (string.IsNullOrWhiteSpace(authorizationUrl)
            || authorizationUrl.EndsWith("/oauth/authorize", StringComparison.OrdinalIgnoreCase))
        {
            return sandboxDefaults
                ? "https://my.cert.tastytrade.com/auth.html"
                : "https://my.tastytrade.com/auth.html";
        }

        return authorizationUrl.Trim();
    }

    private static BrokerSettings Clone(BrokerSettings settings)
    {
        return new BrokerSettings
        {
            Mode = settings.Mode,
            IbkrEnvironment = settings.IbkrEnvironment,
            TastytradeEnvironment = settings.TastytradeEnvironment,
            IbkrPaper = CloneIBKR(settings.IbkrPaper),
            IbkrLive = CloneIBKR(settings.IbkrLive),
            TastytradeSandbox = CloneTastytrade(settings.TastytradeSandbox),
            TastytradeLive = CloneTastytrade(settings.TastytradeLive)
        };
    }

    private static BrokerSettings PreserveRuntimeSecrets(BrokerSettings next, BrokerSettings existing)
    {
        var merged = Clone(next);
        PreserveTastytradeRuntimeSecrets(merged.TastytradeSandbox, existing.TastytradeSandbox);
        PreserveTastytradeRuntimeSecrets(merged.TastytradeLive, existing.TastytradeLive);
        return merged;
    }

    private static void PreserveTastytradeRuntimeSecrets(TastytradeSettings next, TastytradeSettings existing)
    {
        if (string.IsNullOrWhiteSpace(next.ClientSecret) && !string.IsNullOrWhiteSpace(existing.ClientSecret))
        {
            next.ClientSecret = existing.ClientSecret;
        }

        if (string.IsNullOrWhiteSpace(next.AccessToken) && !string.IsNullOrWhiteSpace(existing.AccessToken))
        {
            next.AccessToken = existing.AccessToken;
            next.AccessTokenExpiresAt = existing.AccessTokenExpiresAt;
        }

        if (string.IsNullOrWhiteSpace(next.RefreshToken) && !string.IsNullOrWhiteSpace(existing.RefreshToken))
        {
            next.RefreshToken = existing.RefreshToken;
        }
    }

    private static IBKRSettings CloneIBKR(IBKRSettings settings)
    {
        return new IBKRSettings
        {
            Host = settings.Host,
            Port = settings.Port,
            ClientId = settings.ClientId,
            Account = settings.Account,
            Enabled = settings.Enabled,
            ReadOnly = settings.ReadOnly
        };
    }

    private static TastytradeSettings CloneTastytrade(TastytradeSettings settings)
    {
        return new TastytradeSettings
        {
            ApiBaseUrl = settings.ApiBaseUrl,
            StreamerBaseUrl = settings.StreamerBaseUrl,
            AuthorizationUrl = settings.AuthorizationUrl,
            TokenUrl = settings.TokenUrl,
            ClientId = settings.ClientId,
            ClientSecret = settings.ClientSecret,
            RedirectUri = settings.RedirectUri,
            Username = settings.Username,
            Password = settings.Password,
            AccessToken = settings.AccessToken,
            RefreshToken = settings.RefreshToken,
            AccessTokenExpiresAt = settings.AccessTokenExpiresAt,
            AccountNumber = settings.AccountNumber,
            Enabled = settings.Enabled,
            ReadOnly = settings.ReadOnly
        };
    }
}

public sealed record BrokerSettingsUpdateResult(bool Ok, BrokerSettings Settings, IReadOnlyList<string> Errors);
