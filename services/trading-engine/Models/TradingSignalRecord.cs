using RoniAT.TradingEngine.Contracts;

namespace RoniAT.TradingEngine.Models;

public sealed class TradingSignalRecord
{
    public long Id { get; set; }
    public string Type { get; set; } = "entry";
    public string Direction { get; set; } = "";
    public int Contracts { get; set; }
    public decimal StopLoss { get; set; }
    public decimal TakeProfit1 { get; set; }
    public decimal TakeProfit2 { get; set; }
    public decimal EntryPrice { get; set; }
    public string Symbol { get; set; } = "";
    public string Status { get; set; } = "accepted";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public static TradingSignalRecord FromRequest(TradingSignalRequest request)
    {
        return new TradingSignalRecord
        {
            Type = request.Type!,
            Direction = request.Direction!,
            Contracts = request.Contracts!.Value,
            StopLoss = request.StopLoss!.Value,
            TakeProfit1 = request.TakeProfit1!.Value,
            TakeProfit2 = request.TakeProfit2!.Value,
            EntryPrice = request.EntryPrice!.Value,
            Symbol = request.Symbol!,
            Status = "accepted",
            CreatedAt = DateTimeOffset.UtcNow
        };
    }
}
