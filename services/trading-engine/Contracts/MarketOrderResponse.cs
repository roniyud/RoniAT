using System.Text.Json.Serialization;
using RoniAT.TradingEngine.Models;

namespace RoniAT.TradingEngine.Contracts;

public sealed record MarketOrderResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("order")] OrderRecord? Order,
    [property: JsonPropertyName("position")] PositionRecord? Position
);

