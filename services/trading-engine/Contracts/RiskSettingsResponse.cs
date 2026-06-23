using System.Text.Json.Serialization;
using RoniAT.TradingEngine.Services;

namespace RoniAT.TradingEngine.Contracts;

public sealed record RiskSettingsResponse(
    [property: JsonPropertyName("max_contracts_per_signal")] int MaxContractsPerSignal,
    [property: JsonPropertyName("allowed_symbols")] IReadOnlyList<string> AllowedSymbols,
    [property: JsonPropertyName("enable_auto_trading")] bool EnableAutoTrading,
    [property: JsonPropertyName("reject_duplicate_signals")] bool RejectDuplicateSignals,
    [property: JsonPropertyName("duplicate_window_seconds")] int DuplicateWindowSeconds,
    [property: JsonPropertyName("allow_position_stacking")] bool AllowPositionStacking
)
{
    public static RiskSettingsResponse FromSettings(RiskSettings settings)
    {
        return new RiskSettingsResponse(
            settings.MaxContractsPerSignal,
            settings.AllowedSymbols.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            settings.EnableAutoTrading,
            settings.RejectDuplicateSignals,
            settings.DuplicateWindowSeconds,
            settings.AllowPositionStacking);
    }
}
