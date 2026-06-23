using System.Text.Json.Serialization;
using RoniAT.TradingEngine.Services;

namespace RoniAT.TradingEngine.Contracts;

public sealed record BrokerConnectionTestResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("environment")] string Environment,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("handshake_ok")] bool HandshakeOk,
    [property: JsonPropertyName("account_verified")] bool AccountVerified,
    [property: JsonPropertyName("managed_accounts")] IReadOnlyList<string> ManagedAccounts,
    [property: JsonPropertyName("selected_account")] string? SelectedAccount,
    [property: JsonPropertyName("server_version")] int? ServerVersion,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("tested_at")] DateTimeOffset TestedAt
)
{
    public static BrokerConnectionTestResponse FromResult(BrokerConnectionTestResult result)
    {
        return new BrokerConnectionTestResponse(
            result.Ok,
            result.Mode,
            result.Environment,
            result.Host,
            result.Port,
            result.HandshakeOk,
            result.AccountVerified,
            result.ManagedAccounts,
            result.SelectedAccount,
            result.ServerVersion,
            result.Message,
            result.TestedAt);
    }
}
