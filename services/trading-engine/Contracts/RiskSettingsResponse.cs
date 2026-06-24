using System.Text.Json.Serialization;
using RoniAT.TradingEngine.Services;

namespace RoniAT.TradingEngine.Contracts;

public sealed record RiskSettingsResponse(
    [property: JsonPropertyName("max_contracts_per_signal")] int MaxContractsPerSignal,
    [property: JsonPropertyName("max_loss_per_trade")] decimal MaxLossPerTrade,
    [property: JsonPropertyName("max_daily_loss")] decimal MaxDailyLoss,
    [property: JsonPropertyName("max_entry_price_deviation_points")] decimal MaxEntryPriceDeviationPoints,
    [property: JsonPropertyName("chart_market_protection_distance_points")] decimal ChartMarketProtectionDistancePoints,
    [property: JsonPropertyName("allowed_symbols")] IReadOnlyList<string> AllowedSymbols,
    [property: JsonPropertyName("test_mode")] bool TestMode,
    [property: JsonPropertyName("ignore_tp2")] bool IgnoreTakeProfit2,
    [property: JsonPropertyName("enable_auto_trading")] bool EnableAutoTrading,
    [property: JsonPropertyName("reject_duplicate_signals")] bool RejectDuplicateSignals,
    [property: JsonPropertyName("duplicate_window_seconds")] int DuplicateWindowSeconds,
    [property: JsonPropertyName("allow_position_stacking")] bool AllowPositionStacking,
    [property: JsonPropertyName("trading_locked")] bool TradingLocked,
    [property: JsonPropertyName("emergency_stop_active")] bool EmergencyStopActive,
    [property: JsonPropertyName("stop_loss_failsafe_enabled")] bool StopLossFailsafeEnabled,
    [property: JsonPropertyName("stop_loss_failsafe_poll_seconds")] int StopLossFailsafePollSeconds,
    [property: JsonPropertyName("stop_loss_failsafe_confirm_seconds")] int StopLossFailsafeConfirmSeconds,
    [property: JsonPropertyName("stop_loss_failsafe_cooldown_seconds")] int StopLossFailsafeCooldownSeconds
)
{
    public static RiskSettingsResponse FromSettings(RiskSettings settings)
    {
        return new RiskSettingsResponse(
            settings.MaxContractsPerSignal,
            settings.MaxLossPerTrade,
            settings.MaxDailyLoss,
            settings.MaxEntryPriceDeviationPoints,
            settings.ChartMarketProtectionDistancePoints,
            settings.AllowedSymbols.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            settings.TestMode,
            settings.IgnoreTakeProfit2,
            settings.EnableAutoTrading,
            settings.RejectDuplicateSignals,
            settings.DuplicateWindowSeconds,
            settings.AllowPositionStacking,
            settings.TradingLocked,
            settings.EmergencyStopActive,
            settings.StopLossFailsafeEnabled,
            settings.StopLossFailsafePollSeconds,
            settings.StopLossFailsafeConfirmSeconds,
            settings.StopLossFailsafeCooldownSeconds);
    }
}
