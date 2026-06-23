using RoniAT.TradingEngine.Contracts;

namespace RoniAT.TradingEngine.Services;

public sealed class MockMarketDataProvider : IMarketDataProvider
{
    private static readonly IReadOnlyDictionary<string, int> TimeframeMinutes = new Dictionary<string, int>
    {
        ["1m"] = 1,
        ["5m"] = 5,
        ["15m"] = 15,
        ["1h"] = 60
    };

    public IReadOnlyList<CandleResponse> GetCandles(string symbol, string timeframe)
    {
        var normalizedSymbol = string.IsNullOrWhiteSpace(symbol) ? "MNQ1!" : symbol.Trim().ToUpperInvariant();
        var normalizedTimeframe = string.IsNullOrWhiteSpace(timeframe) ? "5m" : timeframe.Trim().ToLowerInvariant();

        if (!TimeframeMinutes.TryGetValue(normalizedTimeframe, out var intervalMinutes))
        {
            throw new ArgumentException("Unsupported timeframe. Use 1m, 5m, 15m, or 1h.", nameof(timeframe));
        }

        var intervalSeconds = intervalMinutes * 60;
        var candleCount = normalizedTimeframe == "1h" ? 160 : 220;
        var nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var alignedEnd = nowSeconds - (nowSeconds % intervalSeconds);
        var seed = GetSymbolSeed(normalizedSymbol) + intervalMinutes * 17;
        var basePrice = normalizedSymbol.Contains("MNQ", StringComparison.OrdinalIgnoreCase)
            ? 30740m
            : 100m + seed % 80;

        var close = basePrice;
        var candles = new List<CandleResponse>(candleCount);

        for (var index = candleCount - 1; index >= 0; index--)
        {
            var time = alignedEnd - index * intervalSeconds;
            var wave = Math.Sin((candleCount - index + seed) / 9d) * 8;
            var drift = Math.Cos((candleCount - index + seed) / 17d) * 3;
            var move = (decimal)(wave * 0.14 + drift * 0.18 + PseudoRandom(seed + index) * 2.6);
            var open = close;
            close = Math.Max(1, open + move);
            var wick = 1.4m + Math.Abs((decimal)PseudoRandom(seed * 3 + index) * 3.8m);
            var high = Math.Max(open, close) + wick;
            var low = Math.Min(open, close) - wick;

            candles.Add(new CandleResponse(
                time,
                RoundPrice(open),
                RoundPrice(high),
                RoundPrice(low),
                RoundPrice(close)));
        }

        return candles;
    }

    private static int GetSymbolSeed(string symbol)
    {
        return symbol.Sum(character => character);
    }

    private static double PseudoRandom(int input)
    {
        var x = Math.Sin(input * 999) * 10000;
        return x - Math.Floor(x) - 0.5;
    }

    private static decimal RoundPrice(decimal value)
    {
        return Math.Round(value * 4, MidpointRounding.AwayFromZero) / 4;
    }
}
