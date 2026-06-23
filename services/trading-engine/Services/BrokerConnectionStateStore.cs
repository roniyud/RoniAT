namespace RoniAT.TradingEngine.Services;

public sealed class BrokerConnectionStateStore
{
    private readonly object syncRoot = new();
    private BrokerConnectionTestResult? lastResult;

    public BrokerConnectionTestResult? GetLastResult()
    {
        lock (syncRoot)
        {
            return lastResult;
        }
    }

    public void SetLastResult(BrokerConnectionTestResult result)
    {
        lock (syncRoot)
        {
            lastResult = result;
        }
    }

    public void Clear()
    {
        lock (syncRoot)
        {
            lastResult = null;
        }
    }
}

public sealed record BrokerConnectionTestResult(
    bool Ok,
    string Mode,
    string Environment,
    string Host,
    int Port,
    string Message,
    DateTimeOffset TestedAt
);
