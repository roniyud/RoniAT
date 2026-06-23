namespace RoniAT.TradingEngine.Models;

public sealed class OrderRecord
{
    public long Id { get; set; }
    public long? SignalId { get; set; }
    public string? BrokerOrderId { get; set; }
    public string Symbol { get; set; } = "";
    public string Direction { get; set; } = "";
    public string OrderType { get; set; } = "";
    public int Quantity { get; set; }
    public decimal? Price { get; set; }
    public decimal? StopPrice { get; set; }
    public string Status { get; set; } = "created";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}
