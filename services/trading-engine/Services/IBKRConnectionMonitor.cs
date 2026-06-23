using Microsoft.AspNetCore.SignalR;
using RoniAT.TradingEngine.Data;
using RoniAT.TradingEngine.Hubs;
using RoniAT.TradingEngine.Models;

namespace RoniAT.TradingEngine.Services;

public sealed class IBKRConnectionMonitor(
    IServiceScopeFactory scopeFactory,
    BrokerSettingsStore settingsStore,
    BrokerConnectionStateStore stateStore,
    IHubContext<TradingHub> hub,
    ILogger<IBKRConnectionMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RetryInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckConnectionAsync(stoppingToken);

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

    private async Task CheckConnectionAsync(CancellationToken stoppingToken)
    {
        var settings = settingsStore.Get();
        var activeIBKR = settingsStore.GetActiveIBKRSettings();

        if (!settings.Mode.Equals("IBKR", StringComparison.OrdinalIgnoreCase) || !activeIBKR.Enabled)
        {
            return;
        }

        var previous = stateStore.GetLastResult();

        try
        {
            using var scope = scopeFactory.CreateScope();
            var tester = scope.ServiceProvider.GetRequiredService<IBKRConnectionTester>();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

            var result = await tester.TestAsync(stoppingToken);
            var action = GetTransitionAction(previous, result);

            if (action is null)
            {
                return;
            }

            db.AuditLogs.Add(AuditLogRecord.BrokerAction(action, BuildDetails(result)));
            await db.SaveChangesAsync(stoppingToken);
            await hub.Clients.All.SendAsync("trading.updated", new
            {
                event_type = action,
                symbol = (string?)null,
                occurred_at = DateTimeOffset.UtcNow
            }, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "IBKR connection monitor failed");
        }
    }

    private static string? GetTransitionAction(BrokerConnectionTestResult? previous, BrokerConnectionTestResult current)
    {
        if (!current.Mode.Equals("IBKR", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var wasConnected = previous?.Mode.Equals("IBKR", StringComparison.OrdinalIgnoreCase) == true
            && previous.HandshakeOk;

        if (wasConnected && !current.HandshakeOk)
        {
            return "ibkr.connection_lost";
        }

        if (!wasConnected && current.HandshakeOk)
        {
            return "ibkr.reconnected";
        }

        if (previous?.AccountVerified != true && current.AccountVerified)
        {
            return "ibkr.account_verified";
        }

        return null;
    }

    private static string BuildDetails(BrokerConnectionTestResult result)
    {
        return $"IBKR {result.Environment} monitor {result.Host}:{result.Port} - {result.Message}";
    }
}
