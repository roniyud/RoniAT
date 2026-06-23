using System.Text.Json.Serialization;
using RoniAT.TradingEngine.Services;

namespace RoniAT.TradingEngine.Contracts;

public sealed record BrokerStatusResponse(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("environment")] string Environment,
    [property: JsonPropertyName("configured")] bool Configured,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("connected")] bool Connected,
    [property: JsonPropertyName("read_only")] bool ReadOnly,
    [property: JsonPropertyName("message")] string Message
)
{
    public static BrokerStatusResponse FromStatus(BrokerStatus status)
    {
        return new BrokerStatusResponse(
            status.Mode,
            status.Environment,
            status.Configured,
            status.Enabled,
            status.Connected,
            status.ReadOnly,
            status.Message);
    }
}
