using RoniAT.TradingEngine.Contracts;

namespace RoniAT.TradingEngine.Services;

public interface IMarketDataProvider
{
    Task<IReadOnlyList<CandleResponse>> GetCandlesAsync(string symbol, string timeframe, CancellationToken cancellationToken = default);
}
