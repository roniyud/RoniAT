using System.Text.Json.Serialization;

namespace RoniAT.TradingEngine.Contracts;

public sealed record MarketOrderRequest(
    [property: JsonPropertyName("symbol")] string? Symbol,
    [property: JsonPropertyName("direction")] string? Direction,
    [property: JsonPropertyName("contracts")] int? Contracts,
    [property: JsonPropertyName("reference_price")] decimal? ReferencePrice,
    [property: JsonPropertyName("attach_protection")] bool? AttachProtection,
    [property: JsonPropertyName("protection_distance")] decimal? ProtectionDistance
);
