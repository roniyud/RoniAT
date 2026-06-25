using Microsoft.EntityFrameworkCore;
using RoniAT.TradingEngine.Data;
using RoniAT.TradingEngine.Models;

namespace RoniAT.TradingEngine.Services;

public sealed class PaperBrokerAdapter(
    RiskSettingsStore riskSettingsStore,
    DailyPerformanceStore dailyPerformanceStore) : IBrokerAdapter
{
    public string Name => "Paper";

    public BrokerStatus GetStatus()
    {
        return new BrokerStatus(
            Mode: Name,
            Environment: "Paper",
            Configured: true,
            Enabled: true,
            Connected: true,
            ReadOnly: false,
            Message: "Paper broker active");
    }

    public async Task ApplyEntrySignalAsync(TradingSignalRecord signal, TradingDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var exitDirection = signal.Direction == "LONG" ? "SHORT" : "LONG";
        var ignoreTakeProfit2 = riskSettingsStore.Get().IgnoreTakeProfit2;
        var targetAllocation = ignoreTakeProfit2
            ? new TargetAllocation(signal.Contracts, 0)
            : SplitTargets(signal.Contracts);

        db.Orders.Add(new OrderRecord
        {
            SignalId = signal.Id,
            BrokerOrderId = BuildBrokerOrderId(signal.Id, "ENTRY"),
            Symbol = signal.Symbol,
            Direction = signal.Direction,
            OrderType = "paper_entry_market",
            Quantity = signal.Contracts,
            Price = signal.EntryPrice,
            Status = "filled",
            CreatedAt = now,
            UpdatedAt = now
        });

        db.Orders.Add(new OrderRecord
        {
            SignalId = signal.Id,
            BrokerOrderId = BuildBrokerOrderId(signal.Id, "SL"),
            Symbol = signal.Symbol,
            Direction = exitDirection,
            OrderType = "paper_stop_loss",
            Quantity = signal.Contracts,
            StopPrice = signal.StopLoss,
            Status = "working",
            CreatedAt = now,
            UpdatedAt = now
        });

        db.Orders.Add(new OrderRecord
        {
            SignalId = signal.Id,
            BrokerOrderId = BuildBrokerOrderId(signal.Id, "TP1"),
            Symbol = signal.Symbol,
            Direction = exitDirection,
            OrderType = "paper_take_profit_1",
            Quantity = targetAllocation.TakeProfit1,
            Price = signal.TakeProfit1,
            Status = "working",
            CreatedAt = now,
            UpdatedAt = now
        });

        if (!ignoreTakeProfit2)
        {
            db.Orders.Add(new OrderRecord
            {
                SignalId = signal.Id,
                BrokerOrderId = BuildBrokerOrderId(signal.Id, "TP2"),
                Symbol = signal.Symbol,
                Direction = exitDirection,
                OrderType = "paper_take_profit_2",
                Quantity = targetAllocation.TakeProfit2,
                Price = signal.TakeProfit2,
                Status = "working",
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        db.Executions.Add(new ExecutionRecord
        {
            BrokerExecutionId = BuildBrokerOrderId(signal.Id, "EXEC"),
            Symbol = signal.Symbol,
            Direction = signal.Direction,
            Quantity = signal.Contracts,
            Price = signal.EntryPrice,
            ExecutedAt = now
        });

        await UpsertPositionAsync(signal, db, now);

        signal.Status = "paper_position_opened";
        db.BrokerEvents.Add(new BrokerEventRecord
        {
            EventType = "paper.signal_applied",
            PayloadJson = $$"""
            {"signal_id":{{signal.Id}},"symbol":"{{signal.Symbol}}","direction":"{{signal.Direction}}","contracts":{{signal.Contracts}}}
            """,
            CreatedAt = now
        });
        db.AuditLogs.Add(AuditLogRecord.PaperPositionOpened(signal));
    }

    public async Task<MarketOrderResult> PlaceMarketOrderAsync(string symbol, string direction, int contracts, decimal? referencePrice, TradingDbContext db, bool attachProtection = false, decimal? protectionDistance = null, decimal? stopLoss = null, decimal? takeProfit = null)
    {
        var normalizedSymbol = NormalizeSymbol(symbol);
        if (normalizedSymbol is null)
        {
            throw new ArgumentException("symbol is required", nameof(symbol));
        }

        var normalizedDirection = direction.Trim().ToUpperInvariant();
        var now = DateTimeOffset.UtcNow;
        var fillPrice = referencePrice is > 0 ? referencePrice.Value : 0m;
        var brokerOrderId = $"PAPER-MKT-{normalizedSymbol}-{now.ToUnixTimeMilliseconds()}";

        var order = new OrderRecord
        {
            BrokerOrderId = brokerOrderId,
            Symbol = normalizedSymbol,
            Direction = normalizedDirection,
            OrderType = "paper_market",
            Quantity = contracts,
            Price = fillPrice > 0 ? fillPrice : null,
            Status = "filled",
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Orders.Add(order);
        db.Executions.Add(new ExecutionRecord
        {
            BrokerExecutionId = $"{brokerOrderId}-EXEC",
            Symbol = normalizedSymbol,
            Direction = normalizedDirection,
            Quantity = contracts,
            Price = fillPrice,
            ExecutedAt = now
        });

        var position = await UpsertMarketPositionAsync(normalizedSymbol, normalizedDirection, contracts, fillPrice, db, now);
        if (attachProtection && position is not null)
        {
            var distance = protectionDistance is > 0 ? protectionDistance.Value : 100m;
            var (calculatedStopLoss, calculatedTakeProfit) = normalizedDirection == "LONG"
                ? (fillPrice - distance, fillPrice + distance)
                : (fillPrice + distance, fillPrice - distance);
            var effectiveStopLoss = stopLoss ?? calculatedStopLoss;
            var effectiveTakeProfit = takeProfit ?? calculatedTakeProfit;
            var exitDirection = normalizedDirection == "LONG" ? "SHORT" : "LONG";

            position.StopLoss = effectiveStopLoss;
            position.TakeProfit1 = effectiveTakeProfit;
            position.TakeProfit2 = null;
            position.UpdatedAt = now;

            db.Orders.Add(new OrderRecord
            {
                BrokerOrderId = $"{brokerOrderId}-SL",
                Symbol = normalizedSymbol,
                Direction = exitDirection,
                OrderType = "paper_stop_loss",
                Quantity = contracts,
                StopPrice = effectiveStopLoss,
                Status = "working",
                CreatedAt = now,
                UpdatedAt = now
            });

            db.Orders.Add(new OrderRecord
            {
                BrokerOrderId = $"{brokerOrderId}-TP",
                Symbol = normalizedSymbol,
                Direction = exitDirection,
                OrderType = "paper_take_profit",
                Quantity = contracts,
                Price = effectiveTakeProfit,
                Status = "working",
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        db.AuditLogs.Add(AuditLogRecord.PaperAction(
            "paper.market_order_filled",
            $"Paper market order {normalizedSymbol} {normalizedDirection} {contracts} filled"));

        return new MarketOrderResult(true, "filled", "Paper market order filled", order, position);
    }

    public async Task<BrokerActionResult> CancelWorkingOrdersAsync(string? symbol, TradingDbContext db)
    {
        var normalizedSymbol = NormalizeSymbol(symbol);
        var query = db.Orders.Where(order => order.Status == "working");

        if (normalizedSymbol is not null)
        {
            query = query.Where(order => order.Symbol == normalizedSymbol);
        }

        var orders = await query.ToListAsync();
        var now = DateTimeOffset.UtcNow;

        foreach (var order in orders)
        {
            order.Status = "cancelled";
            order.UpdatedAt = now;
        }

        db.AuditLogs.Add(AuditLogRecord.PaperAction(
            "paper.orders_cancelled",
            normalizedSymbol is null
                ? $"Cancelled {orders.Count} working paper orders"
                : $"Cancelled {orders.Count} working paper orders for {normalizedSymbol}"));

        return new BrokerActionResult(orders.Count, 0);
    }

    public async Task<BrokerActionResult> ClosePositionAsync(string symbol, TradingDbContext db)
    {
        var normalizedSymbol = NormalizeSymbol(symbol);
        if (normalizedSymbol is null)
        {
            throw new ArgumentException("symbol is required", nameof(symbol));
        }

        var position = await db.Positions.SingleOrDefaultAsync(item => item.Symbol == normalizedSymbol);
        if (position is null)
        {
            return new BrokerActionResult(0, 0);
        }

        var now = DateTimeOffset.UtcNow;
        var closeDirection = position.Direction == "LONG" ? "SHORT" : "LONG";
        var closePrice = position.AveragePrice;

        db.Orders.Add(new OrderRecord
        {
            BrokerOrderId = $"PAPER-CLOSE-{normalizedSymbol}-{now.ToUnixTimeMilliseconds()}",
            Symbol = normalizedSymbol,
            Direction = closeDirection,
            OrderType = "paper_close_market",
            Quantity = position.Quantity,
            Price = closePrice,
            Status = "filled",
            CreatedAt = now,
            UpdatedAt = now
        });

        db.Executions.Add(new ExecutionRecord
        {
            BrokerExecutionId = $"PAPER-CLOSE-EXEC-{normalizedSymbol}-{now.ToUnixTimeMilliseconds()}",
            Symbol = normalizedSymbol,
            Direction = closeDirection,
            Quantity = position.Quantity,
            Price = closePrice,
            ExecutedAt = now
        });

        var cancelled = await CancelWorkingOrdersAsync(normalizedSymbol, db);
        RecordClosedPosition(db, position, position.Quantity, closePrice, now, "manual_close");
        dailyPerformanceStore.AddRealizedPnl(CalculateRealizedPnl(position, closePrice, position.Quantity));
        db.Positions.Remove(position);
        db.AuditLogs.Add(AuditLogRecord.PaperAction(
            "paper.position_closed",
            $"Closed paper position {normalizedSymbol} {position.Direction} {position.Quantity}"));

        return new BrokerActionResult(cancelled.CancelledOrders, 1);
    }

    public async Task<BrokerActionResult> FlattenAsync(TradingDbContext db)
    {
        var symbols = await db.Positions
            .Select(position => position.Symbol)
            .ToListAsync();

        var cancelledOrders = 0;
        var closedPositions = 0;

        foreach (var symbol in symbols)
        {
            var result = await ClosePositionAsync(symbol, db);
            cancelledOrders += result.CancelledOrders;
            closedPositions += result.ClosedPositions;
        }

        var remainingCancelled = await CancelWorkingOrdersAsync(null, db);
        cancelledOrders += remainingCancelled.CancelledOrders;

        db.AuditLogs.Add(AuditLogRecord.PaperAction(
            "paper.flatten",
            $"Flatten completed: {closedPositions} positions closed, {cancelledOrders} orders cancelled"));

        return new BrokerActionResult(cancelledOrders, closedPositions);
    }

    public async Task<ProtectionUpdateResult> UpdateProtectionAsync(string symbol, decimal? stopLoss, decimal? takeProfit, TradingDbContext db)
    {
        var normalizedSymbol = NormalizeSymbol(symbol);
        if (normalizedSymbol is null)
        {
            throw new ArgumentException("symbol is required", nameof(symbol));
        }

        var position = await db.Positions.SingleOrDefaultAsync(item => item.Symbol == normalizedSymbol);
        if (position is null)
        {
            return new ProtectionUpdateResult(false, "not_found", $"No position found for {normalizedSymbol}", null);
        }

        var validationError = ValidateProtection(position, stopLoss ?? position.StopLoss, takeProfit ?? position.TakeProfit1);
        if (validationError is not null)
        {
            return new ProtectionUpdateResult(false, "invalid_protection", validationError, position);
        }

        position.StopLoss = stopLoss ?? position.StopLoss;
        position.TakeProfit1 = takeProfit ?? position.TakeProfit1;
        position.TakeProfit2 = null;
        position.UpdatedAt = DateTimeOffset.UtcNow;

        var workingOrders = await db.Orders
            .Where(order => order.Symbol == normalizedSymbol && order.Status == "working")
            .ToListAsync();

        foreach (var order in workingOrders)
        {
            if (order.OrderType.Contains("stop_loss", StringComparison.OrdinalIgnoreCase) && position.StopLoss is not null)
            {
                order.StopPrice = position.StopLoss;
                order.UpdatedAt = position.UpdatedAt;
            }

            if (order.OrderType.Contains("take_profit", StringComparison.OrdinalIgnoreCase) && position.TakeProfit1 is not null)
            {
                order.Price = position.TakeProfit1;
                order.UpdatedAt = position.UpdatedAt;
            }
        }

        db.AuditLogs.Add(AuditLogRecord.PaperAction(
            "paper.protection_updated",
            $"Updated paper protection for {normalizedSymbol}: SL={position.StopLoss}, TP={position.TakeProfit1}"));

        return new ProtectionUpdateResult(true, "updated", "Paper protection updated", position);
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

    private async Task UpsertPositionAsync(TradingSignalRecord signal, TradingDbContext db, DateTimeOffset now)
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
                AveragePrice = signal.EntryPrice,
                StopLoss = signal.StopLoss,
                TakeProfit1 = signal.TakeProfit1,
                TakeProfit2 = ignoreTakeProfit2 ? null : signal.TakeProfit2,
                IsManaged = true,
                OpenedAt = now,
                UpdatedAt = now
            });
            return;
        }

        if (existing.Direction == signal.Direction)
        {
            var totalQuantity = existing.Quantity + signal.Contracts;
            existing.AveragePrice = ((existing.AveragePrice * existing.Quantity) + (signal.EntryPrice * signal.Contracts)) / totalQuantity;
            existing.Quantity = totalQuantity;
        }
        else
        {
            existing.Direction = signal.Direction;
            existing.Quantity = signal.Contracts;
            existing.AveragePrice = signal.EntryPrice;
            existing.OpenedAt = now;
        }

        existing.StopLoss = signal.StopLoss;
        existing.TakeProfit1 = signal.TakeProfit1;
        existing.TakeProfit2 = ignoreTakeProfit2 ? null : signal.TakeProfit2;
        existing.IsManaged = true;
        existing.UpdatedAt = now;
    }

    private async Task<PositionRecord?> UpsertMarketPositionAsync(string symbol, string direction, int contracts, decimal fillPrice, TradingDbContext db, DateTimeOffset now)
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
                IsManaged = true,
                OpenedAt = now,
                UpdatedAt = now
            };
            db.Positions.Add(created);
            return created;
        }

        if (existing.Direction == direction)
        {
            var totalQuantity = existing.Quantity + contracts;
            existing.AveragePrice = totalQuantity > 0
                ? ((existing.AveragePrice * existing.Quantity) + (fillPrice * contracts)) / totalQuantity
                : fillPrice;
            existing.Quantity = totalQuantity;
        }
        else if (contracts < existing.Quantity)
        {
            var realizedPnl = CalculateRealizedPnl(existing, fillPrice, contracts);
            dailyPerformanceStore.AddRealizedPnl(realizedPnl);
            RecordClosedPosition(db, existing, contracts, fillPrice, now, "partial_close");
            existing.Quantity -= contracts;
        }
        else if (contracts == existing.Quantity)
        {
            var realizedPnl = CalculateRealizedPnl(existing, fillPrice, contracts);
            dailyPerformanceStore.AddRealizedPnl(realizedPnl);
            RecordClosedPosition(db, existing, contracts, fillPrice, now, "market_close");
            db.Positions.Remove(existing);
            return null;
        }
        else
        {
            var closedQuantity = existing.Quantity;
            var realizedPnl = CalculateRealizedPnl(existing, fillPrice, closedQuantity);
            dailyPerformanceStore.AddRealizedPnl(realizedPnl);
            RecordClosedPosition(db, existing, closedQuantity, fillPrice, now, "reverse_close");
            existing.Quantity = contracts - existing.Quantity;
            existing.Direction = direction;
            existing.AveragePrice = fillPrice;
            existing.IsManaged = true;
            existing.OpenedAt = now;
        }

        existing.StopLoss = null;
        existing.TakeProfit1 = null;
        existing.TakeProfit2 = null;
        existing.IsManaged = true;
        existing.UpdatedAt = now;
        return existing;
    }

    private static TargetAllocation SplitTargets(int contracts)
    {
        var takeProfit1 = (int)Math.Ceiling(contracts / 2m);
        return new TargetAllocation(takeProfit1, contracts - takeProfit1);
    }

    private static string BuildBrokerOrderId(long signalId, string suffix)
    {
        return $"PAPER-{signalId}-{suffix}";
    }

    private static string? NormalizeSymbol(string? symbol)
    {
        var normalized = symbol?.Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private sealed record TargetAllocation(int TakeProfit1, int TakeProfit2);

    private static decimal CalculateRealizedPnl(PositionRecord position, decimal exitPrice, int closedQuantity)
    {
        var directionMultiplier = position.Direction == "LONG" ? 1m : -1m;
        return (exitPrice - position.AveragePrice) * directionMultiplier * closedQuantity * GetPointValue(position.Symbol);
    }

    private static void RecordClosedPosition(TradingDbContext db, PositionRecord position, int closedQuantity, decimal exitPrice, DateTimeOffset closedAt, string closeReason)
    {
        db.ClosedPositions.Add(new ClosedPositionRecord
        {
            Symbol = position.Symbol,
            Direction = position.Direction,
            Quantity = closedQuantity,
            AveragePrice = position.AveragePrice,
            ExitPrice = exitPrice,
            StopLoss = position.StopLoss,
            TakeProfit1 = position.TakeProfit1,
            TakeProfit2 = position.TakeProfit2,
            RealizedPnl = CalculateRealizedPnl(position, exitPrice, closedQuantity),
            CloseReason = closeReason,
            OpenedAt = position.OpenedAt,
            ClosedAt = closedAt
        });
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
}
