using Microsoft.Extensions.Options;
using RoniAT.TradingEngine.Data;
using RoniAT.TradingEngine.Models;

namespace RoniAT.TradingEngine.Services;

public sealed class IBKRBrokerAdapter(IOptions<IBKRSettings> options) : IBrokerAdapter
{
    private readonly IBKRSettings settings = options.Value;

    public string Name => "IBKR";

    public BrokerStatus GetStatus()
    {
        var configured = !string.IsNullOrWhiteSpace(settings.Host)
            && settings.Port > 0
            && !string.IsNullOrWhiteSpace(settings.Account);

        var message = configured
            ? settings.Enabled ? "IBKR skeleton configured; live connection not implemented" : "IBKR configured but disabled"
            : "IBKR not configured";

        return new BrokerStatus(
            Mode: Name,
            Configured: configured,
            Enabled: settings.Enabled,
            Connected: false,
            ReadOnly: settings.ReadOnly,
            Message: message);
    }

    public Task ApplyEntrySignalAsync(TradingSignalRecord signal, TradingDbContext db)
    {
        signal.Status = "broker_blocked";
        RecordBlockedAction(db, "ibkr.entry_blocked", $$"""
        {"signal_id":{{signal.Id}},"symbol":"{{signal.Symbol}}","direction":"{{signal.Direction}}","contracts":{{signal.Contracts}},"reason":"IBKR adapter skeleton does not place live orders"}
        """);

        db.AuditLogs.Add(AuditLogRecord.BrokerAction(
            "ibkr.entry_blocked",
            $"IBKR skeleton blocked entry signal {signal.Id} {signal.Symbol} {signal.Direction} {signal.Contracts}"));

        return Task.CompletedTask;
    }

    public Task<BrokerActionResult> CancelWorkingOrdersAsync(string? symbol, TradingDbContext db)
    {
        RecordBlockedAction(db, "ibkr.cancel_orders_blocked", $$"""
        {"symbol":"{{symbol ?? ""}}","reason":"IBKR adapter skeleton does not cancel live orders"}
        """);

        db.AuditLogs.Add(AuditLogRecord.BrokerAction(
            "ibkr.cancel_orders_blocked",
            "IBKR skeleton blocked cancel working orders"));

        return Task.FromResult(new BrokerActionResult(0, 0));
    }

    public Task<BrokerActionResult> ClosePositionAsync(string symbol, TradingDbContext db)
    {
        RecordBlockedAction(db, "ibkr.close_position_blocked", $$"""
        {"symbol":"{{symbol}}","reason":"IBKR adapter skeleton does not close live positions"}
        """);

        db.AuditLogs.Add(AuditLogRecord.BrokerAction(
            "ibkr.close_position_blocked",
            $"IBKR skeleton blocked close position for {symbol}"));

        return Task.FromResult(new BrokerActionResult(0, 0));
    }

    public Task<BrokerActionResult> FlattenAsync(TradingDbContext db)
    {
        RecordBlockedAction(db, "ibkr.flatten_blocked", """
        {"reason":"IBKR adapter skeleton does not flatten live positions"}
        """);

        db.AuditLogs.Add(AuditLogRecord.BrokerAction(
            "ibkr.flatten_blocked",
            "IBKR skeleton blocked flatten"));

        return Task.FromResult(new BrokerActionResult(0, 0));
    }

    private static void RecordBlockedAction(TradingDbContext db, string eventType, string payloadJson)
    {
        db.BrokerEvents.Add(new BrokerEventRecord
        {
            EventType = eventType,
            PayloadJson = payloadJson,
            CreatedAt = DateTimeOffset.UtcNow
        });
    }
}
