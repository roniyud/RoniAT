using Microsoft.EntityFrameworkCore;
using RoniAT.TradingEngine.Data;
using RoniAT.TradingEngine.Models;

namespace RoniAT.TradingEngine.Services;

public sealed class RiskValidator(
    RiskSettingsStore settingsStore,
    DailyPerformanceStore dailyPerformanceStore)
{
    public async Task<RiskValidationResult> ValidateEntrySignalAsync(TradingSignalRecord signal, TradingDbContext db)
    {
        var settings = settingsStore.Get();
        var reasons = new List<string>();

        if (!settings.EnableAutoTrading)
        {
            reasons.Add("Auto trading is disabled");
        }

        if (settings.TradingLocked)
        {
            reasons.Add("Trading is locked");
        }

        if (settings.EmergencyStopActive)
        {
            reasons.Add("Emergency stop is active");
        }

        if (settings.MaxContractsPerSignal <= 0)
        {
            reasons.Add("Risk setting MaxContractsPerSignal must be greater than zero");
        }
        else if (signal.Contracts > settings.MaxContractsPerSignal)
        {
            reasons.Add($"Contracts {signal.Contracts} exceeds max {settings.MaxContractsPerSignal}");
        }

        var projectedLoss = settings.TestMode
            ? 100m * signal.Contracts * GetPointValue(signal.Symbol)
            : Math.Abs(signal.EntryPrice - signal.StopLoss) * signal.Contracts * GetPointValue(signal.Symbol);
        AddDailyLossReasons(settings, projectedLoss, reasons);

        var allowedSymbols = settings.AllowedSymbols
            .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
            .Select(symbol => symbol.Trim().ToUpperInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (allowedSymbols.Count > 0 && !allowedSymbols.Contains(signal.Symbol.Trim().ToUpperInvariant()))
        {
            reasons.Add($"Symbol {signal.Symbol} is not allowed");
        }

        if (!settings.AllowPositionStacking)
        {
            var hasOpenPosition = await db.Positions.AnyAsync(position => position.Symbol == signal.Symbol);
            if (hasOpenPosition)
            {
                reasons.Add($"Open position already exists for {signal.Symbol}");
            }
        }

        if (settings.RejectDuplicateSignals)
        {
            var duplicateWindow = TimeSpan.FromSeconds(Math.Max(1, settings.DuplicateWindowSeconds));
            var cutoff = DateTimeOffset.UtcNow.Subtract(duplicateWindow);
            var recentSignals = await db.Signals
                .Where(candidate => candidate.Id != signal.Id)
                .OrderByDescending(candidate => candidate.Id)
                .Take(50)
                .ToListAsync();

            var duplicate = recentSignals.Any(candidate =>
                candidate.CreatedAt >= cutoff &&
                candidate.Status != "rejected_by_risk" &&
                candidate.Type == signal.Type &&
                candidate.Direction == signal.Direction &&
                candidate.Symbol == signal.Symbol &&
                candidate.Contracts == signal.Contracts &&
                candidate.EntryPrice == signal.EntryPrice &&
                candidate.StopLoss == signal.StopLoss &&
                candidate.TakeProfit1 == signal.TakeProfit1 &&
                candidate.TakeProfit2 == signal.TakeProfit2);

            if (duplicate)
            {
                reasons.Add($"Duplicate signal within {duplicateWindow.TotalSeconds:0} seconds");
            }
        }

        return reasons.Count == 0
            ? RiskValidationResult.Approved
            : new RiskValidationResult(false, reasons);
    }

    private static decimal GetPointValue(string symbol)
    {
        var normalized = symbol.Trim().ToUpperInvariant().Replace("1!", "", StringComparison.OrdinalIgnoreCase);
        return normalized switch
        {
            "MNQ" => 2m,
            "NQ" => 20m,
            "MES" => 5m,
            "ES" => 50m,
            _ => 1m
        };
    }

    private void AddDailyLossReasons(RiskSettings settings, decimal projectedLoss, List<string> reasons)
    {
        if (settings.MaxDailyLoss <= 0)
        {
            return;
        }

        var today = dailyPerformanceStore.GetToday();
        var currentLoss = Math.Max(0m, -today.RealizedPnl);
        if (currentLoss >= settings.MaxDailyLoss)
        {
            reasons.Add($"Daily loss {currentLoss:0.##} reached max daily loss {settings.MaxDailyLoss:0.##}");
            return;
        }

        if (currentLoss + projectedLoss > settings.MaxDailyLoss)
        {
            reasons.Add($"Projected daily loss {(currentLoss + projectedLoss):0.##} exceeds max daily loss {settings.MaxDailyLoss:0.##}");
        }
    }
}

public sealed record RiskValidationResult(bool IsApproved, IReadOnlyList<string> Reasons)
{
    public static RiskValidationResult Approved { get; } = new(true, []);
}
