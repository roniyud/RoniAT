using RoniAT.TradingEngine.Contracts;

namespace RoniAT.TradingEngine.Services;

public interface IMarketDataProvider
{
    IReadOnlyList<CandleResponse> GetCandles(string symbol, string timeframe);
}
