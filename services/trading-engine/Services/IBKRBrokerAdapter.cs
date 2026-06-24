using Microsoft.EntityFrameworkCore;
using RoniAT.TradingEngine.Data;
using RoniAT.TradingEngine.Models;

namespace RoniAT.TradingEngine.Services;

public sealed class IBKRBrokerAdapter(
    BrokerSettingsStore settingsStore,
    RiskSettingsStore riskSettingsStore,
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

    public async Task<MarketOrderResult> PlaceMarketOrderAsync(string symbol, string direction, int contracts, decimal? referencePrice, TradingDbContext db, bool attachProtection = false, decimal? protectionDistance = null, decimal? stopLoss = null, decimal? takeProfit = null)
    {
        var brokerSettings = settingsStore.Get();
        var settings = settingsStore.GetActiveIBKRSettings();
        var normalizedSymbol = symbol.Trim().ToUpperInvariant();
        var normalizedDirection = direction.Trim().ToUpperInvariant();

        if (!brokerSettings.IbkrEnvironment.Equals("Paper", StringComparison.OrdinalIgnoreCase))
        {
            return BlockMarketOrder(db, normalizedSymbol, normalizedDirection, contracts, "IBKR live order placement is not enabled");
        }

        if (settings.ReadOnly)
        {
            return BlockMarketOrder(db, normalizedSymbol, normalizedDirection, contracts, "IBKR is configured as read-only");
        }

        try
        {
            var submittedOrder = await connectionSession.PlaceMarketOrderAsync(
                normalizedSymbol,
                normalizedDirection,
                contracts);

            var now = DateTimeOffset.UtcNow;
            var localStatus = MapOrderStatus(submittedOrder.Status);
            var order = new OrderRecord
            {
                BrokerOrderId = submittedOrder.OrderId.ToString(),
                Symbol = normalizedSymbol,
                Direction = normalizedDirection,
                OrderType = "ibkr_market",
                Quantity = contracts,
                Price = submittedOrder.AverageFillPrice > 0 ? submittedOrder.AverageFillPrice : referencePrice,
                Status = localStatus,
                CreatedAt = now,
                UpdatedAt = now
            };

            db.Orders.Add(order);

            PositionRecord? position = null;
            if (submittedOrder.FilledQuantity > 0 && submittedOrder.AverageFillPrice > 0)
            {
                db.Executions.Add(new ExecutionRecord
                {
                    OrderId = submittedOrder.OrderId,
                    BrokerExecutionId = $"IBKR-{submittedOrder.OrderId}",
                    Symbol = normalizedSymbol,
                    Direction = normalizedDirection,
                    Quantity = decimal.ToInt32(decimal.Round(submittedOrder.FilledQuantity, 0, MidpointRounding.AwayFromZero)),
                    Price = submittedOrder.AverageFillPrice,
                    ExecutedAt = now
                });

                position = await UpsertMarketPositionAsync(normalizedSymbol, normalizedDirection, contracts, submittedOrder.AverageFillPrice, db, now);

                if (attachProtection && position is not null)
                {
                    var distance = protectionDistance is > 0 ? protectionDistance.Value : 100m;
                    var (calculatedStopLoss, calculatedTakeProfit) = CalculateProtectionLevels(normalizedDirection, submittedOrder.AverageFillPrice, distance);
                    var effectiveStopLoss = stopLoss ?? calculatedStopLoss;
                    var effectiveTakeProfit = takeProfit ?? calculatedTakeProfit;
                    var protectiveOrders = await connectionSession.PlaceProtectiveExitOrdersAsync(
                        normalizedSymbol,
                        normalizedDirection,
                        contracts,
                        effectiveStopLoss,
                        effectiveTakeProfit);

                    position.StopLoss = effectiveStopLoss;
                    position.TakeProfit1 = effectiveTakeProfit;
                    position.TakeProfit2 = null;
                    position.UpdatedAt = now;

                    foreach (var protectiveOrder in protectiveOrders)
                    {
                        var isStop = protectiveOrder.OrderId == protectiveOrders[0].OrderId;
                        db.Orders.Add(new OrderRecord
                        {
                            BrokerOrderId = protectiveOrder.OrderId.ToString(),
                            Symbol = normalizedSymbol,
                            Direction = normalizedDirection == "LONG" ? "SHORT" : "LONG",
                            OrderType = isStop ? "ibkr_stop_loss" : "ibkr_take_profit",
                            Quantity = contracts,
                            Price = isStop ? null : effectiveTakeProfit,
                            StopPrice = isStop ? effectiveStopLoss : null,
                            Status = MapOrderStatus(protectiveOrder.Status),
                            CreatedAt = now,
                            UpdatedAt = now
                        });
                    }
                }
            }

            db.AuditLogs.Add(AuditLogRecord.BrokerAction(
                "ibkr.market_order_submitted",
                $"IBKR Paper market order {submittedOrder.OrderId} {normalizedSymbol} {normalizedDirection} {contracts} status={submittedOrder.Status} filled={submittedOrder.FilledQuantity} avg={submittedOrder.AverageFillPrice}"));

            var ok = localStatus != "rejected" && localStatus != "cancelled";
            var status = submittedOrder.FilledQuantity > 0 ? "filled" : localStatus;
            return new MarketOrderResult(ok, status, $"IBKR market order {submittedOrder.OrderId} {status}", order, position);
        }
        catch (Exception error)
        {
            RecordBlockedAction(db, "ibkr.market_order_failed", $$"""
            {"symbol":"{{normalizedSymbol}}","direction":"{{normalizedDirection}}","contracts":{{contracts}},"reason":"{{EscapeJson(error.Message)}}"}
            """);

            db.AuditLogs.Add(AuditLogRecord.BrokerAction(
                "ibkr.market_order_failed",
                $"IBKR Paper market order failed for {normalizedSymbol}: {error.Message}"));

            return new MarketOrderResult(false, "broker_blocked", error.Message, null, null);
        }
    }

    private static MarketOrderResult BlockMarketOrder(TradingDbContext db, string symbol, string direction, int contracts, string reason)
    {
        RecordBlockedAction(db, "ibkr.market_order_blocked", $$"""
        {"symbol":"{{symbol}}","direction":"{{direction}}","contracts":{{contracts}},"reason":"{{EscapeJson(reason)}}"}
        """);

        db.AuditLogs.Add(AuditLogRecord.BrokerAction(
            "ibkr.market_order_blocked",
            $"IBKR blocked market order {symbol} {direction} {contracts}: {reason}"));

        return new MarketOrderResult(false, "broker_blocked", reason, null, null);
    }

    private static (decimal StopLoss, decimal TakeProfit) CalculateProtectionLevels(string direction, decimal averageFillPrice, decimal distance)
    {
        return direction == "LONG"
            ? (averageFillPrice - distance, averageFillPrice + distance)
            : (averageFillPrice + distance, averageFillPrice - distance);
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

    public async Task<BrokerActionResult> CancelWorkingOrdersAsync(string? symbol, TradingDbContext db)
    {
        var normalizedSymbol = string.IsNullOrWhiteSpace(symbol) ? null : symbol.Trim().ToUpperInvariant();
        var query = db.Orders.Where(order => order.Status == "working");
        if (normalizedSymbol is not null)
        {
            query = query.Where(order => order.Symbol == normalizedSymbol);
        }

        var orders = await query.ToListAsync();
        var orderIds = orders
            .Select(order => int.TryParse(order.BrokerOrderId, out var orderId) ? orderId : (int?)null)
            .Where(orderId => orderId is not null)
            .Select(orderId => orderId!.Value)
            .ToArray();

        if (orderIds.Length > 0)
        {
            await connectionSession.CancelOrdersAsync(orderIds);
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var order in orders)
        {
            order.Status = "cancelled";
            order.UpdatedAt = now;
        }

        db.AuditLogs.Add(AuditLogRecord.BrokerAction(
            "ibkr.orders_cancelled",
            normalizedSymbol is null
                ? $"Cancelled {orders.Count} system-owned IBKR working orders"
                : $"Cancelled {orders.Count} system-owned IBKR working orders for {normalizedSymbol}"));

        return new BrokerActionResult(orders.Count, 0);
    }

    public async Task<BrokerActionResult> ClosePositionAsync(string symbol, TradingDbContext db)
    {
        var normalizedSymbol = symbol.Trim().ToUpperInvariant();
        var position = await db.Positions.SingleOrDefaultAsync(item => item.Symbol == normalizedSymbol);
        if (position is null)
        {
            db.AuditLogs.Add(AuditLogRecord.BrokerAction(
                "ibkr.close_position_skipped",
                $"No system-owned IBKR position found for {normalizedSymbol}"));

            return new BrokerActionResult(0, 0);
        }

        await CancelWorkingOrdersAsync(normalizedSymbol, db);

        var closeDirection = position.Direction == "LONG" ? "SHORT" : "LONG";
        var result = await PlaceMarketOrderAsync(
            normalizedSymbol,
            closeDirection,
            position.Quantity,
            position.AveragePrice,
            db);

        if (!result.Ok)
        {
            db.AuditLogs.Add(AuditLogRecord.BrokerAction(
                "ibkr.close_position_failed",
                $"IBKR close position failed for {normalizedSymbol}: {result.Message}"));

            return new BrokerActionResult(0, 0);
        }

        db.AuditLogs.Add(AuditLogRecord.BrokerAction(
            "ibkr.position_closed",
            $"IBKR position close submitted for {normalizedSymbol} {position.Direction} {position.Quantity}"));

        return new BrokerActionResult(0, 1);
    }

    public async Task<BrokerActionResult> FlattenAsync(TradingDbContext db)
    {
        var symbols = await db.Positions
            .Select(position => position.Symbol)
            .ToListAsync();

        var closedPositions = 0;
        foreach (var symbol in symbols)
        {
            var result = await ClosePositionAsync(symbol, db);
            closedPositions += result.ClosedPositions;
        }

        db.AuditLogs.Add(AuditLogRecord.BrokerAction(
            "ibkr.flatten",
            $"IBKR flatten submitted for {closedPositions} system-owned positions"));

        return new BrokerActionResult(0, closedPositions);
    }

    public async Task<ProtectionUpdateResult> UpdateProtectionAsync(string symbol, decimal? stopLoss, decimal? takeProfit, TradingDbContext db)
    {
        var normalizedSymbol = symbol.Trim().ToUpperInvariant();
        var position = await db.Positions.SingleOrDefaultAsync(item => item.Symbol == normalizedSymbol);
        if (position is null)
        {
            return new ProtectionUpdateResult(false, "not_found", $"No system-owned IBKR position found for {normalizedSymbol}", null);
        }

        var nextStopLoss = stopLoss ?? position.StopLoss;
        var nextTakeProfit = takeProfit ?? position.TakeProfit1;
        if (nextStopLoss is null || nextTakeProfit is null)
        {
            return new ProtectionUpdateResult(false, "missing_protection", "Both stop loss and take profit are required to rebuild OCA protection", position);
        }

        var validationError = ValidateProtection(position, nextStopLoss, nextTakeProfit);
        if (validationError is not null)
        {
            return new ProtectionUpdateResult(false, "invalid_protection", validationError, position);
        }

        await CancelWorkingOrdersAsync(normalizedSymbol, db);
        var protectiveOrders = await connectionSession.PlaceProtectiveExitOrdersAsync(
            normalizedSymbol,
            position.Direction,
            position.Quantity,
            nextStopLoss.Value,
            nextTakeProfit.Value);

        var now = DateTimeOffset.UtcNow;
        position.StopLoss = nextStopLoss;
        position.TakeProfit1 = nextTakeProfit;
        position.TakeProfit2 = null;
        position.UpdatedAt = now;

        foreach (var protectiveOrder in protectiveOrders)
        {
            var isStop = protectiveOrder.OrderId == protectiveOrders[0].OrderId;
            db.Orders.Add(new OrderRecord
            {
                BrokerOrderId = protectiveOrder.OrderId.ToString(),
                Symbol = normalizedSymbol,
                Direction = position.Direction == "LONG" ? "SHORT" : "LONG",
                OrderType = isStop ? "ibkr_stop_loss" : "ibkr_take_profit",
                Quantity = position.Quantity,
                Price = isStop ? null : nextTakeProfit,
                StopPrice = isStop ? nextStopLoss : null,
                Status = "working",
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        db.AuditLogs.Add(AuditLogRecord.BrokerAction(
            "ibkr.protection_updated",
            $"Updated IBKR protection for {normalizedSymbol}: SL={position.StopLoss}, TP={position.TakeProfit1}"));

        return new ProtectionUpdateResult(true, "updated", "IBKR protection updated", position);
    }

    private static string? ValidateProtection(PositionRecord position, decimal? stopLoss, decimal? takeProfit)
    {
        if (position.Direction == "LONG")
        {
            if (stopLoss is not null && stopLoss >= position.AveragePrice) return "LONG stop loss must be below average price";
            if (takeProfit is not null && takeProfit <= position.AveragePrice) return "LONG take profit must be above average price";
        }

        if (position.Direction == "SHORT")
        {
            if (stopLoss is not null && stopLoss <= position.AveragePrice) return "SHORT stop loss must be above average price";
            if (takeProfit is not null && takeProfit >= position.AveragePrice) return "SHORT take profit must be below average price";
        }

        return null;
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

    private async Task UpsertPositionAsync(TradingSignalRecord signal, decimal fillPrice, TradingDbContext db, DateTimeOffset now)
    {
        var ignoreTakeProfit2 = riskSettingsStore.Get().IgnoreTakeProfit2;
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
                TakeProfit2 = ignoreTakeProfit2 ? null : signal.TakeProfit2,
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
        existing.TakeProfit2 = ignoreTakeProfit2 ? null : signal.TakeProfit2;
        existing.UpdatedAt = now;
    }

    private static async Task<PositionRecord?> UpsertMarketPositionAsync(string symbol, string direction, int contracts, decimal fillPrice, TradingDbContext db, DateTimeOffset now)
    {
        var existing = await db.Positions.SingleOrDefaultAsync(position => position.Symbol == symbol);
        if (existing is null)
        {
            var created = new PositionRecord
            {
                Symbol = symbol,
                Direction = direction,
                Quantity = contracts,
                AveragePrice = fillPrice,
                OpenedAt = now,
                UpdatedAt = now
            };
            db.Positions.Add(created);
            return created;
        }

        if (existing.Direction == direction)
        {
            var totalQuantity = existing.Quantity + contracts;
            existing.AveragePrice = ((existing.AveragePrice * existing.Quantity) + (fillPrice * contracts)) / totalQuantity;
            existing.Quantity = totalQuantity;
        }
        else if (contracts < existing.Quantity)
        {
            existing.Quantity -= contracts;
        }
        else if (contracts == existing.Quantity)
        {
            db.Positions.Remove(existing);
            return null;
        }
        else
        {
            existing.Quantity = contracts - existing.Quantity;
            existing.Direction = direction;
            existing.AveragePrice = fillPrice;
            existing.OpenedAt = now;
        }

        existing.StopLoss = null;
        existing.TakeProfit1 = null;
        existing.TakeProfit2 = null;
        existing.UpdatedAt = now;
        return existing;
    }

    private static string EscapeJson(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
