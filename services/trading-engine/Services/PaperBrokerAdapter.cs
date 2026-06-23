using Microsoft.EntityFrameworkCore;
using RoniAT.TradingEngine.Data;
using RoniAT.TradingEngine.Models;

namespace RoniAT.TradingEngine.Services;

public sealed class PaperBrokerAdapter : IBrokerAdapter
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
        var targetAllocation = SplitTargets(signal.Contracts);

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

    public async Task<MarketOrderResult> PlaceMarketOrderAsync(string symbol, string direction, int contracts, decimal? referencePrice, TradingDbContext db)
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

    private static async Task UpsertPositionAsync(TradingSignalRecord signal, TradingDbContext db, DateTimeOffset now)
    {
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
                TakeProfit2 = signal.TakeProfit2,
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
        existing.TakeProfit2 = signal.TakeProfit2;
        existing.UpdatedAt = now;
    }

    private static async Task<PositionRecord> UpsertMarketPositionAsync(string symbol, string direction, int contracts, decimal fillPrice, TradingDbContext db, DateTimeOffset now)
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
            existing.AveragePrice = totalQuantity > 0
                ? ((existing.AveragePrice * existing.Quantity) + (fillPrice * contracts)) / totalQuantity
                : fillPrice;
            existing.Quantity = totalQuantity;
        }
        else
        {
            existing.Direction = direction;
            existing.Quantity = contracts;
            existing.AveragePrice = fillPrice;
            existing.OpenedAt = now;
        }

        existing.StopLoss = null;
        existing.TakeProfit1 = null;
        existing.TakeProfit2 = null;
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
}
