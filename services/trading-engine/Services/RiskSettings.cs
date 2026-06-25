namespace RoniAT.TradingEngine.Services;

public sealed class RiskSettings
{
    public int MaxContractsPerSignal { get; set; } = 7;
    public decimal MaxLossPerTrade { get; set; } = 0;
    public decimal MaxDailyLoss { get; set; } = 0;
    public decimal MaxEntryPriceDeviationPoints { get; set; } = 0;
    public decimal ChartMarketProtectionDistancePoints { get; set; } = 100;
    public string[] AllowedSymbols { get; set; } = [];
    public bool TestMode { get; set; } = false;
    public bool IgnoreTakeProfit2 { get; set; } = true;
    public bool EnableAutoTrading { get; set; } = true;
    public bool RejectDuplicateSignals { get; set; } = true;
    public int DuplicateWindowSeconds { get; set; } = 120;
    public bool AllowPositionStacking { get; set; } = false;
    public bool TradingLocked { get; set; } = false;
    public bool EmergencyStopActive { get; set; } = false;
    public bool CloseUnmanagedBrokerPositions { get; set; } = false;
    public bool StopLossFailsafeEnabled { get; set; } = true;
    public int StopLossFailsafePollSeconds { get; set; } = 1;
    public int StopLossFailsafeConfirmSeconds { get; set; } = 2;
    public int StopLossFailsafeCooldownSeconds { get; set; } = 15;
}
