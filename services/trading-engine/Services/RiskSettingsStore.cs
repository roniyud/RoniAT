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
            MaxEntryPriceDeviationPoints = settings.MaxEntryPriceDeviationPoints,
            ChartMarketProtectionDistancePoints = settings.ChartMarketProtectionDistancePoints <= 0 ? 100m : settings.ChartMarketProtectionDistancePoints,
            AllowedSymbols = allowedSymbols,
            TestMode = settings.TestMode,
            IgnoreTakeProfit2 = settings.IgnoreTakeProfit2,
            EnableAutoTrading = settings.EnableAutoTrading,
            RejectDuplicateSignals = settings.RejectDuplicateSignals,
            DuplicateWindowSeconds = settings.DuplicateWindowSeconds,
            AllowPositionStacking = settings.AllowPositionStacking,
            TradingLocked = settings.TradingLocked,
            EmergencyStopActive = settings.EmergencyStopActive,
            CloseUnmanagedBrokerPositions = settings.CloseUnmanagedBrokerPositions,
            SystemManagedProtectionEnabled = settings.SystemManagedProtectionEnabled,
            StopLossFailsafeEnabled = settings.StopLossFailsafeEnabled,
            StopLossFailsafePollSeconds = Math.Clamp(settings.StopLossFailsafePollSeconds, 1, 30),
            StopLossFailsafeConfirmSeconds = Math.Clamp(settings.StopLossFailsafeConfirmSeconds, 0, 60),
            StopLossFailsafeCooldownSeconds = Math.Clamp(settings.StopLossFailsafeCooldownSeconds, 5, 300)
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

        if (settings.MaxEntryPriceDeviationPoints is < 0 or > 10_000)
        {
            errors.Add("max_entry_price_deviation_points must be between 0 and 10000");
        }

        if (settings.ChartMarketProtectionDistancePoints is <= 0 or > 10_000)
        {
            errors.Add("chart_market_protection_distance_points must be between 0.01 and 10000");
        }

        if (settings.AllowedSymbols.Length == 0)
        {
            errors.Add("allowed_symbols must include at least one symbol");
        }

        if (settings.DuplicateWindowSeconds is < 1 or > 3600)
        {
            errors.Add("duplicate_window_seconds must be between 1 and 3600");
        }

        if (settings.StopLossFailsafePollSeconds is < 1 or > 30)
        {
            errors.Add("stop_loss_failsafe_poll_seconds must be between 1 and 30");
        }

        if (settings.StopLossFailsafeConfirmSeconds is < 0 or > 60)
        {
            errors.Add("stop_loss_failsafe_confirm_seconds must be between 0 and 60");
        }

        if (settings.StopLossFailsafeCooldownSeconds is < 5 or > 300)
        {
            errors.Add("stop_loss_failsafe_cooldown_seconds must be between 5 and 300");
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
            MaxEntryPriceDeviationPoints = settings.MaxEntryPriceDeviationPoints,
            ChartMarketProtectionDistancePoints = settings.ChartMarketProtectionDistancePoints,
            AllowedSymbols = settings.AllowedSymbols.ToArray(),
            TestMode = settings.TestMode,
            IgnoreTakeProfit2 = settings.IgnoreTakeProfit2,
            EnableAutoTrading = settings.EnableAutoTrading,
            RejectDuplicateSignals = settings.RejectDuplicateSignals,
            DuplicateWindowSeconds = settings.DuplicateWindowSeconds,
            AllowPositionStacking = settings.AllowPositionStacking,
            TradingLocked = settings.TradingLocked,
            EmergencyStopActive = settings.EmergencyStopActive,
            CloseUnmanagedBrokerPositions = settings.CloseUnmanagedBrokerPositions,
            SystemManagedProtectionEnabled = settings.SystemManagedProtectionEnabled,
            StopLossFailsafeEnabled = settings.StopLossFailsafeEnabled,
            StopLossFailsafePollSeconds = settings.StopLossFailsafePollSeconds,
            StopLossFailsafeConfirmSeconds = settings.StopLossFailsafeConfirmSeconds,
            StopLossFailsafeCooldownSeconds = settings.StopLossFailsafeCooldownSeconds
        };
    }
}

public sealed record RiskSettingsUpdateResult(bool Ok, RiskSettings Settings, IReadOnlyList<string> Errors);
