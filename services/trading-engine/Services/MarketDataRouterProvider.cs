using RoniAT.TradingEngine.Contracts;

namespace RoniAT.TradingEngine.Services;

public sealed class MarketDataRouterProvider(
    BrokerSettingsStore settingsStore,
    IBKRMarketDataProvider ibkrMarketDataProvider,
    TastytradeMarketDataProvider tastytradeMarketDataProvider,
    MockMarketDataProvider fallbackProvider) : IMarketDataProvider
{
    public string ActiveSource
    {
        get
        {
            var settings = settingsStore.Get();
            if (settings.Mode.Equals("IBKR", StringComparison.OrdinalIgnoreCase))
            {
                return "ibkr";
            }

            if (settings.Mode.Equals("Tastytrade", StringComparison.OrdinalIgnoreCase))
            {
                return "tastytrade";
            }

            return "fallback";
        }
    }

    public Task<IReadOnlyList<CandleResponse>> GetCandlesAsync(
        string symbol,
        string timeframe,
        CancellationToken cancellationToken = default)
    {
        return ActiveSource switch
        {
            "ibkr" => ibkrMarketDataProvider.GetCandlesAsync(symbol, timeframe, cancellationToken),
            "tastytrade" => tastytradeMarketDataProvider.GetCandlesAsync(symbol, timeframe, cancellationToken),
            _ => fallbackProvider.GetCandlesAsync(symbol, timeframe, cancellationToken)
        };
    }
}
