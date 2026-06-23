using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RoniAT.TradingEngine.Data;
using RoniAT.TradingEngine.Models;

namespace RoniAT.TradingEngine.Services;

public sealed class RiskValidator(IOptions<RiskSettings> options)
{
    private readonly RiskSettings settings = options.Value;

    public async Task<RiskValidationResult> ValidateEntrySignalAsync(TradingSignalRecord signal, TradingDbContext db)
    {
        var reasons = new List<string>();

        if (!settings.EnableAutoTrading)
        {
            reasons.Add("Auto trading is disabled");
        }

        if (settings.MaxContractsPerSignal <= 0)
        {
            reasons.Add("Risk setting MaxContractsPerSignal must be greater than zero");
        }
        else if (signal.Contracts > settings.MaxContractsPerSignal)
        {
            reasons.Add($"Contracts {signal.Contracts} exceeds max {settings.MaxContractsPerSignal}");
        }

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
}

public sealed record RiskValidationResult(bool IsApproved, IReadOnlyList<string> Reasons)
{
    public static RiskValidationResult Approved { get; } = new(true, []);
}
