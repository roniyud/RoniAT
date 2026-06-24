using RoniAT.TradingEngine.Contracts;

namespace RoniAT.TradingEngine.Services;

public sealed class IBKRMarketDataProvider(
    IBKRConnectionSession connectionSession,
    MockMarketDataProvider fallbackProvider,
    BrokerSettingsStore settingsStore,
    ILogger<IBKRMarketDataProvider> logger) : IMarketDataProvider
{
    public async Task<IReadOnlyList<CandleResponse>> GetCandlesAsync(
        string symbol,
        string timeframe,
        CancellationToken cancellationToken = default)
    {
        var brokerSettings = settingsStore.Get();
        if (!brokerSettings.Mode.Equals("IBKR", StringComparison.OrdinalIgnoreCase))
        {
            return fallbackProvider.GetCandles(symbol, timeframe);
        }

        try
        {
            var candles = await connectionSession.GetHistoricalCandlesAsync(symbol, timeframe, cancellationToken);
            if (candles.Count > 0)
            {
                return candles;
            }

            var message = $"IBKR historical data returned no candles for {symbol} {timeframe}";
            logger.LogWarning("{Message}", message);
            throw new InvalidOperationException(message);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "IBKR historical data failed for {Symbol} {Timeframe}", symbol, timeframe);
            throw new InvalidOperationException($"IBKR historical data failed for {symbol} {timeframe}: {error.Message}", error);
        }
    }
}
