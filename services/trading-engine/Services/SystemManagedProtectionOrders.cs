using Microsoft.EntityFrameworkCore;
using RoniAT.TradingEngine.Data;
using RoniAT.TradingEngine.Models;

namespace RoniAT.TradingEngine.Services;

public static class SystemManagedProtectionOrders
{
    public const string StopLossOrderType = "system_stop_loss";
    public const string TakeProfitOrderType = "system_take_profit";

    public static bool IsSystemManagedProtection(string orderType)
    {
        return orderType.Equals(StopLossOrderType, StringComparison.OrdinalIgnoreCase)
            || orderType.Equals(TakeProfitOrderType, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<int> CancelAsync(TradingDbContext db, string? symbol, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var normalizedSymbol = string.IsNullOrWhiteSpace(symbol) ? null : symbol.Trim().ToUpperInvariant();
        var query = db.Orders
            .Where(order => order.Status == "working")
            .Where(order => order.OrderType == StopLossOrderType || order.OrderType == TakeProfitOrderType);

        if (normalizedSymbol is not null)
        {
            query = query.Where(order => order.Symbol == normalizedSymbol);
        }

        var orders = await query.ToListAsync(cancellationToken);
        foreach (var order in orders)
        {
            order.Status = "cancelled";
            order.UpdatedAt = now;
        }

        var symbols = orders
            .Select(order => order.Symbol)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (symbols.Length > 0)
        {
            var positions = await db.Positions
                .Where(position => symbols.Contains(position.Symbol))
                .ToListAsync(cancellationToken);

            foreach (var position in positions)
            {
                position.StopLoss = null;
                position.TakeProfit1 = null;
                position.TakeProfit2 = null;
                position.UpdatedAt = now;
            }
        }

        return orders.Count;
    }

    public static async Task ReplaceAsync(TradingDbContext db, PositionRecord position, decimal stopLoss, decimal takeProfit, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await CancelAsync(db, position.Symbol, now, cancellationToken);

        position.StopLoss = stopLoss;
        position.TakeProfit1 = takeProfit;
        position.TakeProfit2 = null;
        position.IsManaged = true;
        position.UpdatedAt = now;

        db.Orders.Add(new OrderRecord
        {
            Symbol = position.Symbol,
            Direction = position.Direction == "LONG" ? "SHORT" : "LONG",
            OrderType = StopLossOrderType,
            Quantity = position.Quantity,
            StopPrice = stopLoss,
            Status = "working",
            CreatedAt = now,
            UpdatedAt = now
        });

        db.Orders.Add(new OrderRecord
        {
            Symbol = position.Symbol,
            Direction = position.Direction == "LONG" ? "SHORT" : "LONG",
            OrderType = TakeProfitOrderType,
            Quantity = position.Quantity,
            Price = takeProfit,
            Status = "working",
            CreatedAt = now,
            UpdatedAt = now
        });
    }
}
