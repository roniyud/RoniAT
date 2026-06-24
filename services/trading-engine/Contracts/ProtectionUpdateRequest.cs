using System.Text.Json.Serialization;

namespace RoniAT.TradingEngine.Contracts;

public sealed record ProtectionUpdateRequest(
    [property: JsonPropertyName("symbol")] string? Symbol,
    [property: JsonPropertyName("stop_loss")] decimal? StopLoss,
    [property: JsonPropertyName("take_profit")] decimal? TakeProfit
);

