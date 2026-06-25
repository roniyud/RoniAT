using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RoniAT.TradingEngine.Data;
using RoniAT.TradingEngine.Hubs;
using RoniAT.TradingEngine.Models;

namespace RoniAT.TradingEngine.Services;

public sealed class SystemManagedProtectionService(
    IServiceScopeFactory scopeFactory,
    BrokerSettingsStore brokerSettingsStore,
    RiskSettingsStore riskSettingsStore,
    IBKRConnectionSession ibkrConnectionSession,
    IMarketDataProvider marketDataProvider,
    IHubContext<TradingHub> hub,
    ILogger<SystemManagedProtectionService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ActionCooldown = TimeSpan.FromSeconds(15);
    private readonly Dictionary<string, DateTimeOffset> lastTriggeredAt = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (riskSettingsStore.Get().SystemManagedProtectionEnabled)
                {
                    await CheckAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "System-managed protection check failed");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var positions = await db.Positions
            .Where(position => position.IsManaged)
            .OrderBy(position => position.Symbol)
            .ToListAsync(cancellationToken);

        if (positions.Count == 0)
        {
            return;
        }

        var workingSystemOrders = await db.Orders
            .Where(order => order.Status == "working")
            .Where(order => order.OrderType == SystemManagedProtectionOrders.StopLossOrderType
                || order.OrderType == SystemManagedProtectionOrders.TakeProfitOrderType)
            .ToListAsync(cancellationToken);

        if (workingSystemOrders.Count == 0)
        {
            return;
        }

        foreach (var position in positions)
        {
            var protection = BuildProtection(position, workingSystemOrders);
            if (protection is null)
            {
                continue;
            }

            await CheckPositionAsync(position, protection, db, scope.ServiceProvider, cancellationToken);
        }
    }

    private async Task CheckPositionAsync(PositionRecord position, SystemProtection protection, TradingDbContext db, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var cooldownKey = $"{position.Symbol}:{position.Id}";
        var now = DateTimeOffset.UtcNow;
        if (lastTriggeredAt.TryGetValue(cooldownKey, out var previous) && now - previous < ActionCooldown)
        {
            return;
        }

        var price = await TryGetLatestPriceAsync(position.Symbol, cancellationToken);
        if (price is null)
        {
            return;
        }

        var trigger = DetectTrigger(position, protection, price.Value);
        if (trigger is null)
        {
            return;
        }

        lastTriggeredAt[cooldownKey] = now;
        trigger.Order.Status = "triggered";
        trigger.Order.UpdatedAt = now;
        db.AuditLogs.Add(AuditLogRecord.BrokerAction(
            "system.protection_triggered",
            $"System-managed {trigger.Kind} triggered for {position.Symbol} {position.Direction} {position.Quantity}: price={price.Value}, level={trigger.Level}"));
        await db.SaveChangesAsync(cancellationToken);

        db.ChangeTracker.Clear();
        var brokerAdapter = serviceProvider.GetRequiredService<IBrokerAdapter>();
        var result = await brokerAdapter.ClosePositionAsync(position.Symbol, db);
        db.AuditLogs.Add(AuditLogRecord.BrokerAction(
            result.ClosedPositions > 0 ? "system.protection_close_sent" : "system.protection_close_failed",
            $"System-managed {trigger.Kind} close for {position.Symbol}: price={price.Value}, level={trigger.Level}, closedPositions={result.ClosedPositions}"));
        await db.SaveChangesAsync(cancellationToken);

        await hub.Clients.All.SendAsync("trading.updated", new
        {
            event_type = result.ClosedPositions > 0 ? "system.protection_close_sent" : "system.protection_close_failed",
            symbol = position.Symbol,
            occurred_at = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    private async Task<decimal?> TryGetLatestPriceAsync(string symbol, CancellationToken cancellationToken)
    {
        if (brokerSettingsStore.Get().Mode.Equals("IBKR", StringComparison.OrdinalIgnoreCase))
        {
            await ibkrConnectionSession.EnsureStreamingMarketDataAsync(symbol, cancellationToken);
            var latestPrice = ibkrConnectionSession.GetLatestMarketPrice(symbol);
            if (latestPrice is not null && DateTimeOffset.UtcNow - latestPrice.Time <= TimeSpan.FromMinutes(2))
            {
                return latestPrice.Price;
            }
        }

        var candles = await marketDataProvider.GetCandlesAsync(symbol, "1m", cancellationToken);
        return candles.LastOrDefault()?.Close;
    }

    private static SystemProtection? BuildProtection(PositionRecord position, IReadOnlyList<OrderRecord> orders)
    {
        var positionOrders = orders
            .Where(order => order.Symbol.Equals(position.Symbol, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var stopOrder = positionOrders.FirstOrDefault(order => order.OrderType == SystemManagedProtectionOrders.StopLossOrderType);
        var takeProfitOrder = positionOrders.FirstOrDefault(order => order.OrderType == SystemManagedProtectionOrders.TakeProfitOrderType);
        var stopLoss = stopOrder?.StopPrice ?? position.StopLoss;
        var takeProfit = takeProfitOrder?.Price ?? position.TakeProfit1;

        if (stopOrder is null && takeProfitOrder is null)
        {
            return null;
        }

        return new SystemProtection(stopOrder, stopLoss, takeProfitOrder, takeProfit);
    }

    private static TriggeredProtection? DetectTrigger(PositionRecord position, SystemProtection protection, decimal price)
    {
        if (position.Direction.Equals("LONG", StringComparison.OrdinalIgnoreCase))
        {
            if (protection.StopOrder is not null && protection.StopLoss is not null && price <= protection.StopLoss.Value)
            {
                return new TriggeredProtection("stop_loss", protection.StopOrder, protection.StopLoss.Value);
            }

            if (protection.TakeProfitOrder is not null && protection.TakeProfit is not null && price >= protection.TakeProfit.Value)
            {
                return new TriggeredProtection("take_profit", protection.TakeProfitOrder, protection.TakeProfit.Value);
            }
        }
        else
        {
            if (protection.StopOrder is not null && protection.StopLoss is not null && price >= protection.StopLoss.Value)
            {
                return new TriggeredProtection("stop_loss", protection.StopOrder, protection.StopLoss.Value);
            }

            if (protection.TakeProfitOrder is not null && protection.TakeProfit is not null && price <= protection.TakeProfit.Value)
            {
                return new TriggeredProtection("take_profit", protection.TakeProfitOrder, protection.TakeProfit.Value);
            }
        }

        return null;
    }

    private sealed record SystemProtection(OrderRecord? StopOrder, decimal? StopLoss, OrderRecord? TakeProfitOrder, decimal? TakeProfit);

    private sealed record TriggeredProtection(string Kind, OrderRecord Order, decimal Level);
}
