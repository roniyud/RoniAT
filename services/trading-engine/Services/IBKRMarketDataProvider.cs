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

            logger.LogWarning("IBKR historical data returned no candles for {Symbol} {Timeframe}; using mock fallback", symbol, timeframe);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "IBKR historical data failed for {Symbol} {Timeframe}; using mock fallback", symbol, timeframe);
        }

        return fallbackProvider.GetCandles(symbol, timeframe);
    }
}
