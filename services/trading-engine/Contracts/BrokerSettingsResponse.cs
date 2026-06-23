using System.Text.Json.Serialization;
using RoniAT.TradingEngine.Services;

namespace RoniAT.TradingEngine.Contracts;

public sealed record BrokerSettingsResponse(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("ibkr_environment")] string IbkrEnvironment,
    [property: JsonPropertyName("ibkr_paper")] IBKRSettingsResponse IbkrPaper,
    [property: JsonPropertyName("ibkr_live")] IBKRSettingsResponse IbkrLive
)
{
    public static BrokerSettingsResponse FromSettings(BrokerSettings settings)
    {
        return new BrokerSettingsResponse(
            settings.Mode,
            settings.IbkrEnvironment,
            IBKRSettingsResponse.FromSettings(settings.IbkrPaper),
            IBKRSettingsResponse.FromSettings(settings.IbkrLive));
    }
}

public sealed record IBKRSettingsResponse(
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("client_id")] int ClientId,
    [property: JsonPropertyName("account")] string Account,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("read_only")] bool ReadOnly
)
{
    public static IBKRSettingsResponse FromSettings(IBKRSettings settings)
    {
        return new IBKRSettingsResponse(
            settings.Host,
            settings.Port,
            settings.ClientId,
            settings.Account,
            settings.Enabled,
            settings.ReadOnly);
    }
}
