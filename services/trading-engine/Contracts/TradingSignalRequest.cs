using System.Text.Json.Serialization;

namespace RoniAT.TradingEngine.Contracts;

public sealed record TradingSignalRequest(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("direction")] string? Direction,
    [property: JsonPropertyName("contracts")] int? Contracts,
    [property: JsonPropertyName("stop_loss")] decimal? StopLoss,
    [property: JsonPropertyName("take_profit_1")] decimal? TakeProfit1,
    [property: JsonPropertyName("take_profit_2")] decimal? TakeProfit2,
    [property: JsonPropertyName("entry_price")] decimal? EntryPrice,
    [property: JsonPropertyName("symbol")] string? Symbol
);
