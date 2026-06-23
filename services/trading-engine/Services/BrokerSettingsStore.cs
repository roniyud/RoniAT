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

    public BrokerSettingsUpdateResult Update(BrokerSettings next)
    {
        var normalized = Normalize(next);
        var errors = Validate(normalized);
        if (errors.Count > 0)
        {
            return new BrokerSettingsUpdateResult(false, Get(), errors);
        }

        lock (syncRoot)
        {
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
            Mode = NormalizeOption(settings.Mode, "Paper", ["Paper", "IBKR"]),
            IbkrEnvironment = NormalizeOption(settings.IbkrEnvironment, "Paper", ["Paper", "Live"]),
            IbkrPaper = NormalizeIBKR(settings.IbkrPaper, paperDefaults: true),
            IbkrLive = NormalizeIBKR(settings.IbkrLive, paperDefaults: false)
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

    private static BrokerSettings Clone(BrokerSettings settings)
    {
        return new BrokerSettings
        {
            Mode = settings.Mode,
            IbkrEnvironment = settings.IbkrEnvironment,
            IbkrPaper = CloneIBKR(settings.IbkrPaper),
            IbkrLive = CloneIBKR(settings.IbkrLive)
        };
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
}

public sealed record BrokerSettingsUpdateResult(bool Ok, BrokerSettings Settings, IReadOnlyList<string> Errors);
