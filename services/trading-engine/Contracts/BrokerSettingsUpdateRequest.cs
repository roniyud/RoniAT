using System.Text.Json.Serialization;

namespace RoniAT.TradingEngine.Contracts;

public sealed record BrokerSettingsUpdateRequest(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("ibkr_environment")] string IbkrEnvironment,
    [property: JsonPropertyName("ibkr_paper")] IBKRSettingsUpdateRequest IbkrPaper,
    [property: JsonPropertyName("ibkr_live")] IBKRSettingsUpdateRequest IbkrLive
);

public sealed record IBKRSettingsUpdateRequest(
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("client_id")] int ClientId,
    [property: JsonPropertyName("account")] string Account,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("read_only")] bool ReadOnly
);
