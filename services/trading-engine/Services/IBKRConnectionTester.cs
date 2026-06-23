using System.Net.Sockets;

namespace RoniAT.TradingEngine.Services;

public sealed class IBKRConnectionTester(BrokerSettingsStore settingsStore, BrokerConnectionStateStore stateStore)
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
                Message: "IBKR host or port is not configured",
                TestedAt: testedAt);
            stateStore.SetLastResult(invalidResult);
            return invalidResult;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(settings.Host, settings.Port, timeout.Token);

            var result = new BrokerConnectionTestResult(
                Ok: true,
                Mode: "IBKR",
                Environment: brokerSettings.IbkrEnvironment,
                Host: settings.Host,
                Port: settings.Port,
                Message: $"TCP connection to IBKR {brokerSettings.IbkrEnvironment} Gateway succeeded",
                TestedAt: DateTimeOffset.UtcNow);
            stateStore.SetLastResult(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            return SaveFailure(settings, brokerSettings.IbkrEnvironment, "Connection timed out after 3 seconds");
        }
        catch (SocketException error)
        {
            return SaveFailure(settings, brokerSettings.IbkrEnvironment, error.Message);
        }
        catch (Exception error)
        {
            return SaveFailure(settings, brokerSettings.IbkrEnvironment, error.Message);
        }
    }

    private BrokerConnectionTestResult SaveFailure(IBKRSettings settings, string environment, string message)
    {
        var result = new BrokerConnectionTestResult(
            Ok: false,
            Mode: "IBKR",
            Environment: environment,
            Host: settings.Host,
            Port: settings.Port,
            Message: message,
            TestedAt: DateTimeOffset.UtcNow);
        stateStore.SetLastResult(result);
        return result;
    }
}
