using RoniAT.TradingEngine.Services;

namespace RoniAT.TradingEngine.Models;

public sealed class AuditLogRecord
{
    public long Id { get; set; }
    public string Action { get; set; } = "";
    public string Details { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public static AuditLogRecord SignalAccepted(TradingSignalRecord signal)
    {
        return new AuditLogRecord
        {
            Action = "signal.accepted",
            Details = $"Signal {signal.Symbol} {signal.Direction} {signal.Contracts} accepted",
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public static AuditLogRecord ManualTradeSubmitted(TradingSignalRecord signal)
    {
        return new AuditLogRecord
        {
            Action = "manual_trade.submitted",
            Details = $"Manual trade {signal.Symbol} {signal.Direction} {signal.Contracts} submitted as signal {signal.Id}",
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public static AuditLogRecord RiskApproved(TradingSignalRecord signal)
    {
        return new AuditLogRecord
        {
            Action = "risk.approved",
            Details = $"Risk approved signal {signal.Id} {signal.Symbol} {signal.Direction} {signal.Contracts}",
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public static AuditLogRecord RiskRejected(TradingSignalRecord signal, IReadOnlyList<string> reasons)
    {
        return new AuditLogRecord
        {
            Action = "risk.rejected",
            Details = $"Risk rejected signal {signal.Id} {signal.Symbol}: {string.Join("; ", reasons)}",
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public static AuditLogRecord RiskSettingsUpdated(RiskSettings settings)
    {
        return new AuditLogRecord
        {
            Action = "risk.settings_updated",
            Details = $"Risk settings updated: auto={settings.EnableAutoTrading}, maxContracts={settings.MaxContractsPerSignal}, symbols={string.Join(",", settings.AllowedSymbols)}",
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public static AuditLogRecord PaperPositionOpened(TradingSignalRecord signal)
    {
        return new AuditLogRecord
        {
            Action = "paper.position_opened",
            Details = $"Paper position {signal.Symbol} {signal.Direction} {signal.Contracts} opened from signal {signal.Id}",
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public static AuditLogRecord PaperAction(string action, string details)
    {
        return new AuditLogRecord
        {
            Action = action,
            Details = details,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }
}
