using System.Text.Json.Serialization;

namespace RoniAT.TradingEngine.Contracts;

public sealed record ValidationErrorResponse(
    [property: JsonPropertyName("errors")] IReadOnlyList<string> Errors
);
