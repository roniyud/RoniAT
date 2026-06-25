namespace RoniAT.TradingEngine.Services;

public sealed class BrokerSettings
{
    public string Mode { get; set; } = "Paper";
    public string IbkrEnvironment { get; set; } = "Paper";
    public string TastytradeEnvironment { get; set; } = "Sandbox";
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
    public TastytradeSettings TastytradeSandbox { get; set; } = new()
    {
        ApiBaseUrl = "https://api.cert.tastyworks.com",
        StreamerBaseUrl = "wss://streamer.cert.tastyworks.com",
        Enabled = false,
        ReadOnly = true
    };
    public TastytradeSettings TastytradeLive { get; set; } = new()
    {
        ApiBaseUrl = "https://api.tastyworks.com",
        StreamerBaseUrl = "wss://streamer.tastyworks.com",
        Enabled = false,
        ReadOnly = true
    };
}
