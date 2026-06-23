namespace RoniAT.TradingEngine.Models;

public sealed class BrokerEventRecord
{
    public long Id { get; set; }
    public string EventType { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
