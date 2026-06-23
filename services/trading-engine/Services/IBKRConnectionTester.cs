namespace RoniAT.TradingEngine.Services;

public sealed class IBKRConnectionTester(
    IBKRConnectionSession connectionSession)
{
    public async Task<BrokerConnectionTestResult> TestAsync(CancellationToken cancellationToken = default)
    {
        return await connectionSession.EnsureConnectedAsync(cancellationToken);
    }
}
