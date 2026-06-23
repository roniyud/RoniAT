using System.Text.Json.Serialization;

namespace RoniAT.TradingEngine.Contracts;

public sealed record CandleResponse(
    [property: JsonPropertyName("time")] long Time,
    [property: JsonPropertyName("open")] decimal Open,
    [property: JsonPropertyName("high")] decimal High,
    [property: JsonPropertyName("low")] decimal Low,
    [property: JsonPropertyName("close")] decimal Close
);
