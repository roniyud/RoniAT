using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RoniAT.TradingEngine.Data;
using RoniAT.TradingEngine.Hubs;
using RoniAT.TradingEngine.Models;

namespace RoniAT.TradingEngine.Services;

public sealed class UnmanagedPositionGuardService(
    IServiceScopeFactory scopeFactory,
    RiskSettingsStore riskSettingsStore,
    BrokerSettingsStore brokerSettingsStore,
    IBKRConnectionSession ibkrConnectionSession,
    IHubContext<TradingHub> hub,
    ILogger<UnmanagedPositionGuardService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ActionCooldown = TimeSpan.FromSeconds(30);
    private readonly Dictionary<string, DateTimeOffset> lastCloseAttempts = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "Unmanaged position guard failed");
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
        var riskSettings = riskSettingsStore.Get();
        if (!riskSettings.CloseUnmanagedBrokerPositions)
        {
            return;
        }

        var brokerSettings = brokerSettingsStore.Get();
        if (brokerSettings.Mode.Equals("Paper", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (brokerSettings.Mode.Equals("IBKR", StringComparison.OrdinalIgnoreCase))
        {
            await CloseUnmanagedIbkrPositionsAsync(riskSettings, cancellationToken);
            return;
        }

        if (brokerSettings.Mode.Equals("Tastytrade", StringComparison.OrdinalIgnoreCase))
        {
            await CloseUnmanagedTastytradePositionsAsync(riskSettings, cancellationToken);
        }
    }

    private async Task CloseUnmanagedIbkrPositionsAsync(RiskSettings riskSettings, CancellationToken cancellationToken)
    {
        var brokerPositions = await ibkrConnectionSession.GetAccountPositionsSnapshotAsync(cancellationToken);
        if (brokerPositions.Count == 0)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var managedKeys = await GetManagedIbkrPositionKeysAsync(db, cancellationToken);
        var protectedKeys = GetProtectedSymbolKeys(riskSettings);
        var now = DateTimeOffset.UtcNow;
        var changed = false;

        foreach (var position in brokerPositions.Where(position =>
        {
            var key = ToOwnershipKey(position.Symbol);
            return protectedKeys.Contains(key) && !managedKeys.Contains(key);
        }))
        {
            var cooldownKey = $"IBKR:{ToOwnershipKey(position.Symbol)}";
            if (IsCoolingDown(cooldownKey, now))
            {
                continue;
            }

            lastCloseAttempts[cooldownKey] = now;
            var closeDirection = position.Direction.Equals("LONG", StringComparison.OrdinalIgnoreCase) ? "SHORT" : "LONG";
            db.AuditLogs.Add(AuditLogRecord.BrokerAction(
                "unmanaged_position_guard.detected",
                $"Detected unmanaged IBKR position {position.Symbol} {position.Direction} {position.Quantity}; submitting forced market close"));
            changed = true;

            try
            {
                var submitted = await ibkrConnectionSession.PlaceMarketOrderAsync(
                    position.Symbol,
                    closeDirection,
                    position.Quantity,
                    cancellationToken);

                db.Orders.Add(new OrderRecord
                {
                    BrokerOrderId = submitted.OrderId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Symbol = position.Symbol,
                    Direction = closeDirection,
                    OrderType = "ibkr_unmanaged_close_market",
                    Quantity = position.Quantity,
                    Status = submitted.Status.Contains("fill", StringComparison.OrdinalIgnoreCase) ? "filled" : "working",
                    CreatedAt = now,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
                db.AuditLogs.Add(AuditLogRecord.BrokerAction(
                    "unmanaged_position_guard.close_submitted",
                    $"Submitted IBKR unmanaged position close order {submitted.OrderId} for {position.Symbol} qty={position.Quantity} status={submitted.Status}"));
            }
            catch (Exception error)
            {
                db.AuditLogs.Add(AuditLogRecord.BrokerAction(
                    "unmanaged_position_guard.close_failed",
                    $"Failed to close unmanaged IBKR position {position.Symbol}: {error.Message}"));
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
            await NotifyUpdatedAsync(cancellationToken);
        }
    }

    private async Task CloseUnmanagedTastytradePositionsAsync(RiskSettings riskSettings, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var accountClient = scope.ServiceProvider.GetRequiredService<TastytradeAccountClient>();
        var orderClient = scope.ServiceProvider.GetRequiredService<TastytradeOrderClient>();
        var brokerAdapter = scope.ServiceProvider.GetRequiredService<TastytradeBrokerAdapter>();
        var brokerPositions = await accountClient.GetPositionsAsync(cancellationToken);
        if (brokerPositions.Count == 0)
        {
            return;
        }

        var managedKeys = await GetManagedPositionKeysAsync(db, cancellationToken);
        var protectedKeys = GetProtectedSymbolKeys(riskSettings);
        var now = DateTimeOffset.UtcNow;
        var changed = false;

        foreach (var position in brokerPositions.Where(position =>
        {
            var key = ToOwnershipKey(position.Symbol);
            return protectedKeys.Contains(key) && !managedKeys.Contains(key);
        }))
        {
            var cooldownKey = $"TASTYTRADE:{ToOwnershipKey(position.Symbol)}";
            if (IsCoolingDown(cooldownKey, now))
            {
                continue;
            }

            lastCloseAttempts[cooldownKey] = now;
            db.AuditLogs.Add(AuditLogRecord.BrokerAction(
                "unmanaged_position_guard.detected",
                $"Detected unmanaged Tastytrade position {position.Symbol} {position.Direction} {position.Quantity}; submitting forced market close"));
            changed = true;

            try
            {
                await brokerAdapter.CancelWorkingOrdersAsync(position.Symbol, db);
                var submitted = await orderClient.SubmitClosingMarketOrderAsync(position);
                db.Orders.Add(new OrderRecord
                {
                    BrokerOrderId = submitted.OrderId,
                    Symbol = position.Symbol,
                    Direction = position.Direction.Equals("LONG", StringComparison.OrdinalIgnoreCase) ? "SHORT" : "LONG",
                    OrderType = "tastytrade_unmanaged_close_market",
                    Quantity = position.Quantity,
                    Status = submitted.Status.Contains("fill", StringComparison.OrdinalIgnoreCase) ? "filled" : "working",
                    CreatedAt = now,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
                db.AuditLogs.Add(AuditLogRecord.BrokerAction(
                    "unmanaged_position_guard.close_submitted",
                    $"Submitted Tastytrade unmanaged position close order {submitted.OrderId} for {position.Symbol} qty={position.Quantity} status={submitted.Status}"));
            }
            catch (Exception error)
            {
                db.AuditLogs.Add(AuditLogRecord.BrokerAction(
                    "unmanaged_position_guard.close_failed",
                    $"Failed to close unmanaged Tastytrade position {position.Symbol}: {error.Message}"));
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
            await NotifyUpdatedAsync(cancellationToken);
        }
    }

    private static async Task<HashSet<string>> GetManagedIbkrPositionKeysAsync(TradingDbContext db, CancellationToken cancellationToken)
    {
        var positionSymbols = await db.Positions
            .AsNoTracking()
            .Where(position => position.IsManaged)
            .Select(position => position.Symbol)
            .ToListAsync(cancellationToken);

        return positionSymbols
            .Select(ToOwnershipKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<HashSet<string>> GetManagedPositionKeysAsync(TradingDbContext db, CancellationToken cancellationToken)
    {
        var positionSymbols = await db.Positions
            .AsNoTracking()
            .Where(position => position.IsManaged)
            .Select(position => position.Symbol)
            .ToListAsync(cancellationToken);

        var activeOrderSymbols = await db.Orders
            .AsNoTracking()
            .Where(order => order.Status != "rejected" && order.Status != "cancelled")
            .Where(order => !order.OrderType.Contains("close") && !order.OrderType.Contains("flatten") && !order.OrderType.Contains("unmanaged"))
            .Where(order => order.OrderType.Contains("market") || order.OrderType.Contains("protection") || order.SignalId != null)
            .Select(order => order.Symbol)
            .Distinct()
            .ToListAsync(cancellationToken);

        return positionSymbols
            .Concat(activeOrderSymbols)
            .Select(ToOwnershipKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> GetProtectedSymbolKeys(RiskSettings settings)
    {
        return settings.AllowedSymbols
            .Select(ToOwnershipKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private bool IsCoolingDown(string key, DateTimeOffset now)
    {
        return lastCloseAttempts.TryGetValue(key, out var previous)
            && now - previous < ActionCooldown;
    }

    private Task NotifyUpdatedAsync(CancellationToken cancellationToken)
    {
        return hub.Clients.All.SendAsync("trading.updated", new
        {
            event_type = "positions.updated",
            symbol = (string?)null,
            occurred_at = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    private static string ToOwnershipKey(string symbol)
    {
        var normalized = (symbol ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "";
        }

        if (normalized.Contains(':'))
        {
            normalized = normalized[(normalized.LastIndexOf(':') + 1)..];
        }

        normalized = normalized.TrimStart('/').Replace(" ", "", StringComparison.Ordinal);
        if (normalized.EndsWith("1!", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^2];
        }

        foreach (var root in new[] { "MNQ", "MES", "NQ", "ES" })
        {
            if (normalized.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }

            if (normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && normalized.Length > root.Length + 1
                && "FGHJKMNQUVXZ".Contains(normalized[root.Length], StringComparison.Ordinal)
                && char.IsDigit(normalized[^1]))
            {
                return root;
            }
        }

        return normalized;
    }
}
