using System.Text.Json.Serialization;
using RoniAT.TradingEngine.Models;

namespace RoniAT.TradingEngine.Contracts;

public sealed record TradingSignalResponse(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("contracts")] int Contracts,
    [property: JsonPropertyName("stop_loss")] decimal StopLoss,
    [property: JsonPropertyName("take_profit_1")] decimal TakeProfit1,
    [property: JsonPropertyName("take_profit_2")] decimal TakeProfit2,
    [property: JsonPropertyName("entry_price")] decimal EntryPrice,
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt
)
{
    public static TradingSignalResponse FromRecord(TradingSignalRecord record)
    {
        return new TradingSignalResponse(
            record.Id,
            record.Type,
            record.Direction,
            record.Contracts,
            record.StopLoss,
            record.TakeProfit1,
            record.TakeProfit2,
            record.EntryPrice,
            record.Symbol,
            record.Status,
            record.CreatedAt);
    }
}
