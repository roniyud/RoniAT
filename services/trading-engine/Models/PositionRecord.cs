namespace RoniAT.TradingEngine.Models;

public sealed class PositionRecord
{
    public long Id { get; set; }
    public string Symbol { get; set; } = "";
    public string Direction { get; set; } = "";
    public int Quantity { get; set; }
    public decimal AveragePrice { get; set; }
    public decimal? StopLoss { get; set; }
    public decimal? TakeProfit1 { get; set; }
    public decimal? TakeProfit2 { get; set; }
    public DateTimeOffset OpenedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}
