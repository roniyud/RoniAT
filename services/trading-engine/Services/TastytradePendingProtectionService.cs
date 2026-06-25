using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using RoniAT.TradingEngine.Data;
using RoniAT.TradingEngine.Models;

namespace RoniAT.TradingEngine.Services;

public sealed class TastytradePendingProtectionService(
    IServiceScopeFactory scopeFactory,
    BrokerSettingsStore settingsStore,
    ILogger<TastytradePendingProtectionService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, PendingProtectionRequest> pending = new(StringComparer.OrdinalIgnoreCase);

    public void Enqueue(
        string symbol,
        string routedSymbol,
        string direction,
        int quantity,
        decimal stopLoss,
        decimal takeProfit,
        string entryOrderId)
    {
        var key = $"{entryOrderId}:{symbol}:{direction}";
        pending[key] = new PendingProtectionRequest(
            Key: key,
            Symbol: symbol.Trim().ToUpperInvariant(),
            RoutedSymbol: routedSymbol.Trim().ToUpperInvariant(),
            Direction: direction.Trim().ToUpperInvariant(),
            Quantity: quantity,
            StopLoss: stopLoss,
            TakeProfit: takeProfit,
            EntryOrderId: entryOrderId,
            CreatedAt: DateTimeOffset.UtcNow,
            LastAttemptAt: null,
            Attempts: 0);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (settingsStore.Get().Mode.Equals("Tastytrade", StringComparison.OrdinalIgnoreCase))
                {
                    await ProcessPendingAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "Tastytrade pending protection loop failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private async Task ProcessPendingAsync(CancellationToken cancellationToken)
    {
        if (pending.IsEmpty)
        {
            return;
        }

        foreach (var item in pending.Values)
        {
            if (DateTimeOffset.UtcNow - item.CreatedAt > TimeSpan.FromMinutes(3))
            {
                pending.TryRemove(item.Key, out _);
                await RecordFailureAsync(item, "Timed out waiting for Tastytrade position to appear", cancellationToken);
                continue;
            }

            if (item.LastAttemptAt is not null && DateTimeOffset.UtcNow - item.LastAttemptAt < TimeSpan.FromSeconds(3))
            {
                continue;
            }

            var next = item with
            {
                Attempts = item.Attempts + 1,
                LastAttemptAt = DateTimeOffset.UtcNow
            };
            pending[next.Key] = next;
            await TryAttachProtectionAsync(next, cancellationToken);
        }
    }

    private async Task TryAttachProtectionAsync(PendingProtectionRequest item, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var accountClient = scope.ServiceProvider.GetRequiredService<TastytradeAccountClient>();
        var orderClient = scope.ServiceProvider.GetRequiredService<TastytradeOrderClient>();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var positions = await accountClient.GetPositionsAsync(cancellationToken);
        var position = positions.FirstOrDefault(candidate =>
            (candidate.Symbol.Equals(item.Symbol, StringComparison.OrdinalIgnoreCase)
                || candidate.Symbol.Equals(item.RoutedSymbol, StringComparison.OrdinalIgnoreCase))
            && candidate.Direction.Equals(item.Direction, StringComparison.OrdinalIgnoreCase)
            && candidate.Quantity >= item.Quantity);

        if (position is null)
        {
            return;
        }

        var oco = await orderClient.SubmitOcoProtectionAsync(
            position,
            item.StopLoss,
            item.TakeProfit,
            cancellationToken: cancellationToken);

        var now = DateTimeOffset.UtcNow;
        db.Orders.Add(new OrderRecord
        {
            BrokerOrderId = oco.OrderId,
            Symbol = item.Symbol,
            Direction = item.Direction == "LONG" ? "SHORT" : "LONG",
            OrderType = "tastytrade_oco_protection",
            Quantity = position.Quantity,
            Price = item.TakeProfit,
            StopPrice = item.StopLoss,
            Status = MapOrderStatus(oco.Status),
            CreatedAt = now,
            UpdatedAt = now
        });
        db.AuditLogs.Add(AuditLogRecord.BrokerAction(
            "tastytrade.oco_protection_attached",
            $"Attached delayed Tastytrade OCO protection {oco.OrderId} for {item.Symbol}->{position.Symbol}: SL={item.StopLoss}, TP={item.TakeProfit}, qty={position.Quantity}, entryOrder={item.EntryOrderId}, attempts={item.Attempts}"));

        var entryOrder = await db.Orders
            .Where(order => order.BrokerOrderId == item.EntryOrderId)
            .OrderByDescending(order => order.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (entryOrder is not null && entryOrder.Status == "protection_pending")
        {
            entryOrder.Status = "working";
            entryOrder.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        pending.TryRemove(item.Key, out _);
    }

    private async Task RecordFailureAsync(PendingProtectionRequest item, string reason, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        db.AuditLogs.Add(AuditLogRecord.BrokerAction(
            "tastytrade.oco_protection_failed",
            $"Tastytrade pending OCO protection failed for {item.Symbol} entryOrder={item.EntryOrderId}: {reason}"));

        var entryOrder = await db.Orders
            .Where(order => order.BrokerOrderId == item.EntryOrderId)
            .OrderByDescending(order => order.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (entryOrder is not null)
        {
            entryOrder.Status = "protection_failed";
            entryOrder.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static string MapOrderStatus(string status)
    {
        if (status.Contains("reject", StringComparison.OrdinalIgnoreCase)) return "rejected";
        if (status.Contains("cancel", StringComparison.OrdinalIgnoreCase)) return "cancelled";
        if (status.Contains("fill", StringComparison.OrdinalIgnoreCase)) return "filled";
        if (status.Contains("live", StringComparison.OrdinalIgnoreCase)
            || status.Contains("routed", StringComparison.OrdinalIgnoreCase)
            || status.Contains("received", StringComparison.OrdinalIgnoreCase)
            || status.Contains("submitted", StringComparison.OrdinalIgnoreCase))
        {
            return "working";
        }

        return string.IsNullOrWhiteSpace(status) ? "working" : status.Trim().ToLowerInvariant();
    }

    private sealed record PendingProtectionRequest(
        string Key,
        string Symbol,
        string RoutedSymbol,
        string Direction,
        int Quantity,
        decimal StopLoss,
        decimal TakeProfit,
        string EntryOrderId,
        DateTimeOffset CreatedAt,
        DateTimeOffset? LastAttemptAt,
        int Attempts);
}
