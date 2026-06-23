namespace RoniAT.TradingEngine.Services;

public sealed class IBKRSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7497;
    public int ClientId { get; set; } = 10;
    public string Account { get; set; } = "";
    public bool Enabled { get; set; } = false;
    public bool ReadOnly { get; set; } = true;
}
