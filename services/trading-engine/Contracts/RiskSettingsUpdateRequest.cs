using System.Text.Json.Serialization;

namespace RoniAT.TradingEngine.Contracts;

public sealed record RiskSettingsUpdateRequest(
    [property: JsonPropertyName("max_contracts_per_signal")] int MaxContractsPerSignal,
    [property: JsonPropertyName("max_loss_per_trade")] decimal MaxLossPerTrade,
    [property: JsonPropertyName("max_daily_loss")] decimal MaxDailyLoss,
    [property: JsonPropertyName("allowed_symbols")] IReadOnlyList<string> AllowedSymbols,
    [property: JsonPropertyName("test_mode")] bool TestMode,
    [property: JsonPropertyName("ignore_tp2")] bool IgnoreTakeProfit2,
    [property: JsonPropertyName("enable_auto_trading")] bool EnableAutoTrading,
    [property: JsonPropertyName("reject_duplicate_signals")] bool RejectDuplicateSignals,
    [property: JsonPropertyName("duplicate_window_seconds")] int DuplicateWindowSeconds,
    [property: JsonPropertyName("allow_position_stacking")] bool AllowPositionStacking,
    [property: JsonPropertyName("trading_locked")] bool TradingLocked,
    [property: JsonPropertyName("emergency_stop_active")] bool EmergencyStopActive
);
