using Microsoft.EntityFrameworkCore;
using RoniAT.TradingEngine.Data;
using RoniAT.TradingEngine.Models;

namespace RoniAT.TradingEngine.Services;

public sealed class IBKRBrokerAdapter(
    BrokerSettingsStore settingsStore,
    BrokerConnectionStateStore connectionStateStore,
    IBKRConnectionSession connectionSession) : IBrokerAdapter
{
    public string Name => "IBKR";

    public BrokerStatus GetStatus()
    {
        var brokerSettings = settingsStore.Get();
        var settings = settingsStore.GetActiveIBKRSettings();
        var configured = !string.IsNullOrWhiteSpace(settings.Host)
            && settings.Port > 0
            && !string.IsNullOrWhiteSpace(settings.Account);

        var lastConnection = connectionStateStore.GetLastResult();
        var connectionMatchesSettings = lastConnection is not null
            && lastConnection.Mode == "IBKR"
            && lastConnection.Environment.Equals(brokerSettings.IbkrEnvironment, StringComparison.OrdinalIgnoreCase)
            && lastConnection.Host.Equals(settings.Host, StringComparison.OrdinalIgnoreCase)
            && lastConnection.Port == settings.Port;

        var connected = connectionMatchesSettings && lastConnection!.HandshakeOk;

        var message = connectionMatchesSettings
            ? lastConnection!.Message
            : configured
            ? settings.Enabled ? $"IBKR {brokerSettings.IbkrEnvironment} configured; run Test Connection to verify API handshake" : $"IBKR {brokerSettings.IbkrEnvironment} configured but disabled"
            : $"IBKR {brokerSettings.IbkrEnvironment} not configured";

        return new BrokerStatus(
            Mode: Name,
            Environment: brokerSettings.IbkrEnvironment,
            Configured: configured,
            Enabled: settings.Enabled,
            Connected: connected,
            ReadOnly: settings.ReadOnly,
            Message: message);
    }

    public async Task ApplyEntrySignalAsync(TradingSignalRecord signal, TradingDbContext db)
    {
        var brokerSettings = settingsStore.Get();
        var settings = settingsStore.GetActiveIBKRSettings();

        if (!brokerSettings.IbkrEnvironment.Equals("Paper", StringComparison.OrdinalIgnoreCase))
        {
            BlockEntry(signal, db, "IBKR live order placement is not enabled");
            return;
        }

        if (settings.ReadOnly)
        {
            BlockEntry(signal, db, "IBKR is configured as read-only");
            return;
        }

        try
        {
            var submittedOrder = await connectionSession.PlaceMarketOrderAsync(
                signal.Symbol,
                signal.Direction,
                signal.Contracts);

            var now = DateTimeOffset.UtcNow;
            var localStatus = MapOrderStatus(submittedOrder.Status);
            db.Orders.Add(new OrderRecord
            {
                SignalId = signal.Id,
                BrokerOrderId = submittedOrder.OrderId.ToString(),
                Symbol = signal.Symbol,
                Direction = signal.Direction,
                OrderType = "ibkr_entry_market",
                Quantity = signal.Contracts,
                Price = submittedOrder.AverageFillPrice > 0 ? submittedOrder.AverageFillPrice : signal.EntryPrice,
                Status = localStatus,
                CreatedAt = now,
                UpdatedAt = now
            });

            if (submittedOrder.FilledQuantity > 0 && submittedOrder.AverageFillPrice > 0)
            {
                db.Executions.Add(new ExecutionRecord
                {
                    OrderId = submittedOrder.OrderId,
                    BrokerExecutionId = $"IBKR-{submittedOrder.OrderId}",
                    Symbol = signal.Symbol,
                    Direction = signal.Direction,
                    Quantity = decimal.ToInt32(decimal.Round(submittedOrder.FilledQuantity, 0, MidpointRounding.AwayFromZero)),
                    Price = submittedOrder.AverageFillPrice,
                    ExecutedAt = now
                });

                await UpsertPositionAsync(signal, submittedOrder.AverageFillPrice, db, now);
                signal.Status = "ibkr_position_opened";
            }
            else
            {
                signal.Status = localStatus == "rejected" ? "ibkr_order_rejected" : "ibkr_order_submitted";
            }

            db.BrokerEvents.Add(new BrokerEventRecord
            {
                EventType = "ibkr.entry_order_submitted",
                PayloadJson = $$"""
                {"signal_id":{{signal.Id}},"symbol":"{{signal.Symbol}}","direction":"{{signal.Direction}}","contracts":{{signal.Contracts}},"order_id":{{submittedOrder.OrderId}},"status":"{{submittedOrder.Status}}","filled":{{submittedOrder.FilledQuantity}},"avg_fill":{{submittedOrder.AverageFillPrice}}}
                """,
                CreatedAt = now
            });

            db.AuditLogs.Add(AuditLogRecord.BrokerAction(
                "ibkr.entry_order_submitted",
                $"IBKR Paper market order {submittedOrder.OrderId} {signal.Symbol} {signal.Direction} {signal.Contracts} status={submittedOrder.Status} filled={submittedOrder.FilledQuantity} avg={submittedOrder.AverageFillPrice}"));
        }
        catch (Exception error)
        {
            signal.Status = "broker_blocked";
            RecordBlockedAction(db, "ibkr.entry_failed", $$"""
            {"signal_id":{{signal.Id}},"symbol":"{{signal.Symbol}}","direction":"{{signal.Direction}}","contracts":{{signal.Contracts}},"reason":"{{EscapeJson(error.Message)}}"}
            """);

            db.AuditLogs.Add(AuditLogRecord.BrokerAction(
                "ibkr.entry_failed",
                $"IBKR Paper market order failed for signal {signal.Id} {signal.Symbol}: {error.Message}"));
        }
    }

    private static void BlockEntry(TradingSignalRecord signal, TradingDbContext db, string reason)
    {
        signal.Status = "broker_blocked";
        RecordBlockedAction(db, "ibkr.entry_blocked", $$"""
        {"signal_id":{{signal.Id}},"symbol":"{{signal.Symbol}}","direction":"{{signal.Direction}}","contracts":{{signal.Contracts}},"reason":"{{EscapeJson(reason)}}"}
        """);

        db.AuditLogs.Add(AuditLogRecord.BrokerAction(
            "ibkr.entry_blocked",
            $"IBKR blocked entry signal {signal.Id} {signal.Symbol} {signal.Direction} {signal.Contracts}: {reason}"));
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

    private static string MapOrderStatus(string status)
    {
        if (status.Equals("Filled", StringComparison.OrdinalIgnoreCase)) return "filled";
        if (status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("ApiCancelled", StringComparison.OrdinalIgnoreCase)) return "cancelled";
        if (status.Equals("Inactive", StringComparison.OrdinalIgnoreCase)) return "rejected";
        return "working";
    }

    private static async Task UpsertPositionAsync(TradingSignalRecord signal, decimal fillPrice, TradingDbContext db, DateTimeOffset now)
    {
        var existing = await db.Positions.SingleOrDefaultAsync(position => position.Symbol == signal.Symbol);
        if (existing is null)
        {
            db.Positions.Add(new PositionRecord
            {
                Symbol = signal.Symbol,
                Direction = signal.Direction,
                Quantity = signal.Contracts,
                AveragePrice = fillPrice,
                StopLoss = signal.StopLoss,
                TakeProfit1 = signal.TakeProfit1,
                TakeProfit2 = signal.TakeProfit2,
                OpenedAt = now,
                UpdatedAt = now
            });
            return;
        }

        if (existing.Direction == signal.Direction)
        {
            var totalQuantity = existing.Quantity + signal.Contracts;
            existing.AveragePrice = ((existing.AveragePrice * existing.Quantity) + (fillPrice * signal.Contracts)) / totalQuantity;
            existing.Quantity = totalQuantity;
        }
        else
        {
            existing.Direction = signal.Direction;
            existing.Quantity = signal.Contracts;
            existing.AveragePrice = fillPrice;
            existing.OpenedAt = now;
        }

        existing.StopLoss = signal.StopLoss;
        existing.TakeProfit1 = signal.TakeProfit1;
        existing.TakeProfit2 = signal.TakeProfit2;
        existing.UpdatedAt = now;
    }

    private static string EscapeJson(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
