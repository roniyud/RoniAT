namespace RoniAT.TradingEngine.Models;

public sealed class ExecutionRecord
{
    public long Id { get; set; }
    public long? OrderId { get; set; }
    public string? BrokerExecutionId { get; set; }
    public string Symbol { get; set; } = "";
    public string Direction { get; set; } = "";
    public int Quantity { get; set; }
    public decimal Price { get; set; }
    public DateTimeOffset ExecutedAt { get; set; } = DateTimeOffset.UtcNow;
}
