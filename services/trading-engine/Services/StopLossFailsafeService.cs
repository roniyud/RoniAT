using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RoniAT.TradingEngine.Data;
using RoniAT.TradingEngine.Hubs;
using RoniAT.TradingEngine.Models;

namespace RoniAT.TradingEngine.Services;

public sealed class StopLossFailsafeService(
    IServiceScopeFactory scopeFactory,
    BrokerSettingsStore brokerSettingsStore,
    RiskSettingsStore riskSettingsStore,
    IBKRConnectionSession ibkrConnectionSession,
    IMarketDataProvider marketDataProvider,
    IHubContext<TradingHub> hub,
    ILogger<StopLossFailsafeService> logger) : BackgroundService
{
    private readonly Dictionary<string, DateTimeOffset> breachStartedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> lastTriggeredAt = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = riskSettingsStore.Get();
            try
            {
                if (settings.StopLossFailsafeEnabled)
                {
                    await CheckOpenPositionsAsync(settings, stoppingToken);
                }
                else
                {
                    breachStartedAt.Clear();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "Stop loss failsafe check failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(settings.StopLossFailsafePollSeconds, 1, 30)), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task CheckOpenPositionsAsync(RiskSettings settings, CancellationToken cancellationToken)
    {
        var brokerSettings = brokerSettingsStore.Get();
        if (!brokerSettings.Mode.Equals("IBKR", StringComparison.OrdinalIgnoreCase))
        {
            breachStartedAt.Clear();
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var positions = await db.Positions
            .Where(position => position.StopLoss != null)
            .OrderBy(position => position.Symbol)
            .ToListAsync(cancellationToken);

        if (positions.Count == 0)
        {
            breachStartedAt.Clear();
            return;
        }

        var openSymbols = positions.Select(position => position.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in breachStartedAt.Keys.Where(symbol => !openSymbols.Contains(symbol)).ToArray())
        {
            breachStartedAt.Remove(symbol);
        }

        foreach (var position in positions)
        {
            await CheckPositionAsync(position, db, scope.ServiceProvider, settings, cancellationToken);
        }
    }

    private async Task CheckPositionAsync(PositionRecord position, TradingDbContext db, IServiceProvider serviceProvider, RiskSettings settings, CancellationToken cancellationToken)
    {
        await ibkrConnectionSession.EnsureStreamingMarketDataAsync(position.Symbol, cancellationToken);
        var breach = await DetectStopBreachAsync(position, cancellationToken);
        if (breach is null)
        {
            breachStartedAt.Remove(position.Symbol);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (lastTriggeredAt.TryGetValue(position.Symbol, out var lastTrigger)
            && now - lastTrigger < TimeSpan.FromSeconds(Math.Clamp(settings.StopLossFailsafeCooldownSeconds, 5, 300)))
        {
            return;
        }

        if (!breachStartedAt.TryGetValue(position.Symbol, out var firstBreach))
        {
            breachStartedAt[position.Symbol] = now;
            db.AuditLogs.Add(AuditLogRecord.BrokerAction(
                "failsafe.stop_loss_breached",
                $"Failsafe detected {position.Symbol} {position.Direction} stop breach: price={breach.Price}, SL={position.StopLoss}, source={breach.Source}"));
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (now - firstBreach < TimeSpan.FromSeconds(Math.Clamp(settings.StopLossFailsafeConfirmSeconds, 0, 60)))
        {
            return;
        }

        await ibkrConnectionSession.SyncPositionsAsync(cancellationToken);
        db.ChangeTracker.Clear();

        var syncedPosition = await db.Positions
            .SingleOrDefaultAsync(item => item.Id == position.Id, cancellationToken);
        if (syncedPosition is null)
        {
            breachStartedAt.Remove(position.Symbol);
            db.AuditLogs.Add(AuditLogRecord.BrokerAction(
                "failsafe.forced_flatten_skipped",
                $"Failsafe skipped forced close for {position.Symbol}: broker sync shows the position is already closed"));
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var brokerAdapter = serviceProvider.GetRequiredService<IBrokerAdapter>();
        var result = await brokerAdapter.ClosePositionAsync(syncedPosition.Symbol, db);
        lastTriggeredAt[position.Symbol] = now;
        breachStartedAt.Remove(position.Symbol);

        db.AuditLogs.Add(AuditLogRecord.BrokerAction(
            result.ClosedPositions > 0 ? "failsafe.forced_flatten_sent" : "failsafe.forced_flatten_failed",
            $"Failsafe forced close for {position.Symbol}: price={breach.Price}, SL={position.StopLoss}, source={breach.Source}, closedPositions={result.ClosedPositions}"));
        await db.SaveChangesAsync(cancellationToken);

        await hub.Clients.All.SendAsync("trading.updated", new
        {
            event_type = result.ClosedPositions > 0 ? "failsafe.forced_flatten_sent" : "failsafe.forced_flatten_failed",
            symbol = position.Symbol,
            occurred_at = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    private async Task<StopBreach?> DetectStopBreachAsync(PositionRecord position, CancellationToken cancellationToken)
    {
        var latestPrice = ibkrConnectionSession.GetLatestMarketPrice(position.Symbol);
        if (latestPrice is not null && IsStopBreached(position, latestPrice.Price))
        {
            return new StopBreach(latestPrice.Price, latestPrice.Source);
        }

        try
        {
            var openedAt = position.OpenedAt.ToUnixTimeSeconds();
            var candles = await marketDataProvider.GetCandlesAsync(position.Symbol, "1m", cancellationToken);
            var breachedCandle = position.Direction.Equals("LONG", StringComparison.OrdinalIgnoreCase)
                ? candles
                    .Where(candle => candle.Time >= openedAt && position.StopLoss is not null && candle.Low <= position.StopLoss.Value)
                    .OrderByDescending(candle => candle.Time)
                    .FirstOrDefault()
                : candles
                    .Where(candle => candle.Time >= openedAt && position.StopLoss is not null && candle.High >= position.StopLoss.Value)
                    .OrderByDescending(candle => candle.Time)
                    .FirstOrDefault();

            if (breachedCandle is null)
            {
                return null;
            }

            var breachPrice = position.Direction.Equals("LONG", StringComparison.OrdinalIgnoreCase)
                ? breachedCandle.Low
                : breachedCandle.High;
            return new StopBreach(breachPrice, $"IBKR 1m candle {DateTimeOffset.FromUnixTimeSeconds(breachedCandle.Time):O}");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "Stop loss failsafe could not read fallback candles for {Symbol}", position.Symbol);
            return null;
        }
    }

    private static bool IsStopBreached(PositionRecord position, decimal price)
    {
        if (position.StopLoss is null)
        {
            return false;
        }

        return position.Direction.Equals("LONG", StringComparison.OrdinalIgnoreCase)
            ? price <= position.StopLoss.Value
            : price >= position.StopLoss.Value;
    }

    private sealed record StopBreach(decimal Price, string Source);
}
