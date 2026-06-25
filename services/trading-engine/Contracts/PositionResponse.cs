using System.Text.Json.Serialization;
using RoniAT.TradingEngine.Models;

namespace RoniAT.TradingEngine.Contracts;

public sealed record PositionResponse(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("quantity")] int Quantity,
    [property: JsonPropertyName("averagePrice")] decimal AveragePrice,
    [property: JsonPropertyName("stopLoss")] decimal? StopLoss,
    [property: JsonPropertyName("takeProfit1")] decimal? TakeProfit1,
    [property: JsonPropertyName("takeProfit2")] decimal? TakeProfit2,
    [property: JsonPropertyName("isManaged")] bool IsManaged,
    [property: JsonPropertyName("openedAt")] DateTimeOffset OpenedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset? UpdatedAt)
{
    public static PositionResponse FromRecord(PositionRecord position, bool? isManaged = null)
    {
        return new PositionResponse(
            position.Id,
            position.Symbol,
            position.Direction,
            position.Quantity,
            position.AveragePrice,
            position.StopLoss,
            position.TakeProfit1,
            position.TakeProfit2,
            isManaged ?? position.IsManaged,
            position.OpenedAt,
            position.UpdatedAt);
    }
}
