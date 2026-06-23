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
