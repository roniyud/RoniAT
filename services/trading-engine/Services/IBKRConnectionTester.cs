namespace RoniAT.TradingEngine.Services;

public sealed class IBKRConnectionTester(
    BrokerSettingsStore settingsStore,
    BrokerConnectionStateStore stateStore,
    IBKRReadOnlyHandshakeClient handshakeClient)
{
    public async Task<BrokerConnectionTestResult> TestAsync(CancellationToken cancellationToken = default)
    {
        var brokerSettings = settingsStore.Get();
        var settings = settingsStore.GetActiveIBKRSettings();
        var testedAt = DateTimeOffset.UtcNow;

        if (!brokerSettings.Mode.Equals("IBKR", StringComparison.OrdinalIgnoreCase))
        {
            var paperResult = new BrokerConnectionTestResult(
                Ok: true,
                Mode: "Paper",
                Environment: "Paper",
                Host: "",
                Port: 0,
                HandshakeOk: true,
                AccountVerified: true,
                ManagedAccounts: [],
                SelectedAccount: null,
                ServerVersion: null,
                Message: "Paper broker does not require an external connection",
                TestedAt: testedAt);
            stateStore.SetLastResult(paperResult);
            return paperResult;
        }

        if (string.IsNullOrWhiteSpace(settings.Host) || settings.Port <= 0)
        {
            var invalidResult = new BrokerConnectionTestResult(
                Ok: false,
                Mode: "IBKR",
                Environment: brokerSettings.IbkrEnvironment,
                Host: settings.Host,
                Port: settings.Port,
                HandshakeOk: false,
                AccountVerified: false,
                ManagedAccounts: [],
                SelectedAccount: string.IsNullOrWhiteSpace(settings.Account) ? null : settings.Account.Trim(),
                ServerVersion: null,
                Message: "IBKR host or port is not configured",
                TestedAt: testedAt);
            stateStore.SetLastResult(invalidResult);
            return invalidResult;
        }

        var result = await handshakeClient.TestAsync(settings, brokerSettings.IbkrEnvironment, testedAt, cancellationToken);
        stateStore.SetLastResult(result);
        return result;
    }
}
