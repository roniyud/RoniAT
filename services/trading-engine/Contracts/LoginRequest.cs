using System.Text.Json.Serialization;

namespace RoniAT.TradingEngine.Contracts;

public sealed record LoginRequest(
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("password")] string? Password
);
