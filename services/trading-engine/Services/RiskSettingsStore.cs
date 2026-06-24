using System.Text.Json;
using Microsoft.Extensions.Options;

namespace RoniAT.TradingEngine.Services;

public sealed class RiskSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object syncRoot = new();
    private readonly string filePath;
    private RiskSettings current;

    public RiskSettingsStore(IOptions<RiskSettings> defaults, IWebHostEnvironment environment)
    {
        filePath = Path.Combine(environment.ContentRootPath, "storage", "risk-settings.json");
        current = LoadOrCreate(defaults.Value);
    }

    public RiskSettings Get()
    {
        lock (syncRoot)
        {
            return Clone(current);
        }
    }

    public RiskSettingsUpdateResult Update(RiskSettings next)
    {
        var normalized = Normalize(next);
        var errors = Validate(normalized);
        if (errors.Count > 0)
        {
            return new RiskSettingsUpdateResult(false, Get(), errors);
        }

        lock (syncRoot)
        {
            current = normalized;
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, JsonSerializer.Serialize(current, JsonOptions));
            return new RiskSettingsUpdateResult(true, Clone(current), []);
        }
    }

    private RiskSettings LoadOrCreate(RiskSettings defaults)
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
            var loaded = JsonSerializer.Deserialize<RiskSettings>(File.ReadAllText(filePath), JsonOptions);
            return Normalize(loaded ?? defaults);
        }
        catch (JsonException)
        {
            return Normalize(defaults);
        }
    }

    private static RiskSettings Normalize(RiskSettings settings)
    {
        var allowedSymbols = settings.AllowedSymbols
            .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
            .Select(symbol => symbol.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (allowedSymbols.Length == 0)
        {
            allowedSymbols = ["MNQ1!"];
        }

        return new RiskSettings
        {
            MaxContractsPerSignal = settings.MaxContractsPerSignal,
            MaxLossPerTrade = settings.MaxLossPerTrade,
            MaxDailyLoss = settings.MaxDailyLoss,
            AllowedSymbols = allowedSymbols,
            TestMode = settings.TestMode,
            IgnoreTakeProfit2 = settings.IgnoreTakeProfit2,
            EnableAutoTrading = settings.EnableAutoTrading,
            RejectDuplicateSignals = settings.RejectDuplicateSignals,
            DuplicateWindowSeconds = settings.DuplicateWindowSeconds,
            AllowPositionStacking = settings.AllowPositionStacking,
            TradingLocked = settings.TradingLocked,
            EmergencyStopActive = settings.EmergencyStopActive
        };
    }

    private static List<string> Validate(RiskSettings settings)
    {
        var errors = new List<string>();

        if (settings.MaxContractsPerSignal is < 1 or > 100)
        {
            errors.Add("max_contracts_per_signal must be between 1 and 100");
        }

        if (settings.MaxLossPerTrade is < 0 or > 1_000_000)
        {
            errors.Add("max_loss_per_trade must be between 0 and 1000000");
        }

        if (settings.MaxDailyLoss is < 0 or > 1_000_000)
        {
            errors.Add("max_daily_loss must be between 0 and 1000000");
        }

        if (settings.AllowedSymbols.Length == 0)
        {
            errors.Add("allowed_symbols must include at least one symbol");
        }

        if (settings.DuplicateWindowSeconds is < 1 or > 3600)
        {
            errors.Add("duplicate_window_seconds must be between 1 and 3600");
        }

        return errors;
    }

    private static RiskSettings Clone(RiskSettings settings)
    {
        return new RiskSettings
        {
            MaxContractsPerSignal = settings.MaxContractsPerSignal,
            MaxLossPerTrade = settings.MaxLossPerTrade,
            MaxDailyLoss = settings.MaxDailyLoss,
            AllowedSymbols = settings.AllowedSymbols.ToArray(),
            TestMode = settings.TestMode,
            IgnoreTakeProfit2 = settings.IgnoreTakeProfit2,
            EnableAutoTrading = settings.EnableAutoTrading,
            RejectDuplicateSignals = settings.RejectDuplicateSignals,
            DuplicateWindowSeconds = settings.DuplicateWindowSeconds,
            AllowPositionStacking = settings.AllowPositionStacking,
            TradingLocked = settings.TradingLocked,
            EmergencyStopActive = settings.EmergencyStopActive
        };
    }
}

public sealed record RiskSettingsUpdateResult(bool Ok, RiskSettings Settings, IReadOnlyList<string> Errors);
