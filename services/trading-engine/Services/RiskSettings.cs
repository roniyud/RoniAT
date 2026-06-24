namespace RoniAT.TradingEngine.Services;

public sealed class RiskSettings
{
    public int MaxContractsPerSignal { get; set; } = 7;
    public decimal MaxLossPerTrade { get; set; } = 0;
    public decimal MaxDailyLoss { get; set; } = 0;
    public string[] AllowedSymbols { get; set; } = [];
    public bool TestMode { get; set; } = false;
    public bool IgnoreTakeProfit2 { get; set; } = true;
    public bool EnableAutoTrading { get; set; } = true;
    public bool RejectDuplicateSignals { get; set; } = true;
    public int DuplicateWindowSeconds { get; set; } = 120;
    public bool AllowPositionStacking { get; set; } = false;
    public bool TradingLocked { get; set; } = false;
    public bool EmergencyStopActive { get; set; } = false;
}
