using System.Text.Json.Serialization;

namespace RoniAT.TradingEngine.Contracts;

public sealed record LoginResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("token")] string? Token,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("message")] string Message
);
