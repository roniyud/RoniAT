namespace RoniAT.TradingEngine.Services;

public sealed class RiskSettings
{
    public int MaxContractsPerSignal { get; set; } = 7;
    public string[] AllowedSymbols { get; set; } = [];
    public bool EnableAutoTrading { get; set; } = true;
    public bool RejectDuplicateSignals { get; set; } = true;
    public int DuplicateWindowSeconds { get; set; } = 120;
    public bool AllowPositionStacking { get; set; } = false;
    public bool TradingLocked { get; set; } = false;
    public bool EmergencyStopActive { get; set; } = false;
}
