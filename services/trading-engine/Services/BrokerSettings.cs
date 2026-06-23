namespace RoniAT.TradingEngine.Services;

public sealed class BrokerSettings
{
    public string Mode { get; set; } = "Paper";
    public string IbkrEnvironment { get; set; } = "Paper";
    public IBKRSettings IbkrPaper { get; set; } = new()
    {
        Host = "127.0.0.1",
        Port = 4002,
        ClientId = 10,
        ReadOnly = true,
        Enabled = false
    };
    public IBKRSettings IbkrLive { get; set; } = new()
    {
        Host = "127.0.0.1",
        Port = 4001,
        ClientId = 11,
        ReadOnly = true,
        Enabled = false
    };
}
