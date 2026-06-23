using IBApi;

namespace RoniAT.TradingEngine.Services;

public sealed class IBKRReadOnlyHandshakeClient
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan AccountsTimeout = TimeSpan.FromSeconds(3);

    public async Task<BrokerConnectionTestResult> TestAsync(
        IBKRSettings settings,
        string environment,
        DateTimeOffset testedAt,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HandshakeTimeout);

        var wrapper = new ReadOnlyWrapper();
        var signal = new EReaderMonitorSignal();
        var client = new EClientSocket(wrapper, signal);
        Task? messagePump = null;

        try
        {
            await Task.Run(() => client.eConnect(settings.Host, settings.Port, settings.ClientId), CancellationToken.None)
                .WaitAsync(timeout.Token);

            if (!client.IsConnected())
            {
                return Failure(settings, environment, "IBKR API socket did not connect", testedAt);
            }

            var reader = new EReader(client, signal);
            reader.Start();

            messagePump = Task.Run(() =>
            {
                while (client.IsConnected() && !timeout.IsCancellationRequested)
                {
                    signal.waitForSignal();
                    reader.processMsgs();
                }
            }, CancellationToken.None);

            await wrapper.WaitForHandshakeAsync(timeout.Token);
            client.reqManagedAccts();

            var accounts = await wrapper.WaitForManagedAccountsAsync(AccountsTimeout, timeout.Token);
            var selectedAccount = string.IsNullOrWhiteSpace(settings.Account) ? null : settings.Account.Trim();
            var accountVerified = selectedAccount is not null
                && accounts.Any(account => account.Equals(selectedAccount, StringComparison.OrdinalIgnoreCase));

            var ok = selectedAccount is not null && accountVerified;
            var message = BuildMessage(environment, selectedAccount, accountVerified, accounts);

            return new BrokerConnectionTestResult(
                Ok: ok,
                Mode: "IBKR",
                Environment: environment,
                Host: settings.Host,
                Port: settings.Port,
                HandshakeOk: true,
                AccountVerified: accountVerified,
                ManagedAccounts: accounts,
                SelectedAccount: selectedAccount,
                ServerVersion: client.ServerVersion,
                Message: message,
                TestedAt: DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            return Failure(settings, environment, "IBKR API handshake timed out after 6 seconds", testedAt);
        }
        catch (Exception error)
        {
            return Failure(settings, environment, $"IBKR API handshake failed: {error.Message}", testedAt);
        }
        finally
        {
            if (client.IsConnected())
            {
                client.eDisconnect(false);
            }

            timeout.Cancel();
            signal.issueSignal();

            if (messagePump is not null)
            {
                try
                {
                    await messagePump.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
                }
                catch (TimeoutException)
                {
                    // The socket is already disconnected; the background reader will exit when the signal is processed.
                }
            }
        }
    }

    private static BrokerConnectionTestResult Failure(
        IBKRSettings settings,
        string environment,
        string message,
        DateTimeOffset testedAt)
    {
        return new BrokerConnectionTestResult(
            Ok: false,
            Mode: "IBKR",
            Environment: environment,
            Host: settings.Host,
            Port: settings.Port,
            HandshakeOk: false,
            AccountVerified: false,
            ManagedAccounts: [],
            SelectedAccount: string.IsNullOrWhiteSpace(settings.Account) ? null : settings.Account.Trim(),
            ServerVersion: null,
            Message: message,
            TestedAt: testedAt);
    }

    private static string BuildMessage(
        string environment,
        string? selectedAccount,
        bool accountVerified,
        IReadOnlyList<string> accounts)
    {
        var accountList = accounts.Count == 0 ? "none" : string.Join(", ", accounts);

        if (selectedAccount is null)
        {
            return $"IBKR {environment} API handshake succeeded. Managed accounts: {accountList}. Configure an account to verify.";
        }

        return accountVerified
            ? $"IBKR {environment} API handshake succeeded and account {selectedAccount} was verified"
            : $"IBKR {environment} API handshake succeeded, but account {selectedAccount} was not found. Managed accounts: {accountList}";
    }

    private sealed class ReadOnlyWrapper : DefaultEWrapper
    {
        private readonly TaskCompletionSource<int> handshakeSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IReadOnlyList<string>> accountsSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void nextValidId(int orderId)
        {
            handshakeSource.TrySetResult(orderId);
        }

        public override void managedAccounts(string accountsList)
        {
            var accounts = accountsList
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(account => !string.IsNullOrWhiteSpace(account))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            accountsSource.TrySetResult(accounts);
        }

        public override void connectionClosed()
        {
            handshakeSource.TrySetException(new InvalidOperationException("IBKR API connection closed before handshake completed"));
            accountsSource.TrySetException(new InvalidOperationException("IBKR API connection closed before managed accounts were received"));
        }

        public override void error(Exception e)
        {
            handshakeSource.TrySetException(e);
            accountsSource.TrySetException(e);
        }

        public Task WaitForHandshakeAsync(CancellationToken cancellationToken)
        {
            return handshakeSource.Task.WaitAsync(cancellationToken);
        }

        public async Task<IReadOnlyList<string>> WaitForManagedAccountsAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var accountsTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            accountsTimeout.CancelAfter(timeout);

            try
            {
                return await accountsSource.Task.WaitAsync(accountsTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return [];
            }
        }
    }
}
