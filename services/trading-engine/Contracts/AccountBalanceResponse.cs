using System.Text.Json.Serialization;

namespace RoniAT.TradingEngine.Contracts;

public sealed record AccountBalanceResponse(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("environment")] string Environment,
    [property: JsonPropertyName("account_number")] string AccountNumber,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("cash_balance")] decimal? CashBalance,
    [property: JsonPropertyName("net_liquidating_value")] decimal? NetLiquidatingValue,
    [property: JsonPropertyName("equity_buying_power")] decimal? EquityBuyingPower,
    [property: JsonPropertyName("derivative_buying_power")] decimal? DerivativeBuyingPower,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt
);
