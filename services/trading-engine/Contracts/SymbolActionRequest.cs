using System.Text.Json.Serialization;

namespace RoniAT.TradingEngine.Contracts;

public sealed record SymbolActionRequest(
    [property: JsonPropertyName("symbol")] string? Symbol
);
