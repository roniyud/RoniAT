using Microsoft.AspNetCore.SignalR;
using RoniAT.TradingEngine.Data;
using RoniAT.TradingEngine.Hubs;
using RoniAT.TradingEngine.Models;
using IBApi;

namespace RoniAT.TradingEngine.Services;

public sealed class IBKRConnectionSession(
    IServiceScopeFactory scopeFactory,
    BrokerSettingsStore settingsStore,
    BrokerConnectionStateStore stateStore,
    IHubContext<TradingHub> hub,
    ILogger<IBKRConnectionSession> logger) : BackgroundService
{
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan AccountsTimeout = TimeSpan.FromSeconds(3);

    private readonly SemaphoreSlim connectionLock = new(1, 1);
    private EClientSocket? client;
    private EReaderSignal? signal;
    private CancellationTokenSource? sessionCts;
    private Task? messagePump;
    private SessionWrapper? wrapper;
    private IBKRConnectionKey? connectionKey;

    public async Task<BrokerConnectionTestResult> EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        await connectionLock.WaitAsync(cancellationToken);

        try
        {
            var brokerSettings = settingsStore.Get();
            var settings = settingsStore.GetActiveIBKRSettings();
            var key = IBKRConnectionKey.From(brokerSettings, settings);

            if (!brokerSettings.Mode.Equals("IBKR", StringComparison.OrdinalIgnoreCase))
            {
                await DisconnectCurrentSessionAsync();
                return SaveResult(CreatePaperResult(DateTimeOffset.UtcNow));
            }

            if (string.IsNullOrWhiteSpace(settings.Host) || settings.Port <= 0)
            {
                await DisconnectCurrentSessionAsync();
                return SaveResult(CreateFailure(settings, brokerSettings.IbkrEnvironment, "IBKR host or port is not configured", DateTimeOffset.UtcNow));
            }

            if (!settings.Enabled)
            {
                await DisconnectCurrentSessionAsync();
                return SaveResult(CreateFailure(settings, brokerSettings.IbkrEnvironment, $"IBKR {brokerSettings.IbkrEnvironment} is disabled", DateTimeOffset.UtcNow));
            }

            if (IsConnectedFor(key))
            {
                return stateStore.GetLastResult() ?? BuildSuccessResult(settings, brokerSettings.IbkrEnvironment, [], client!.ServerVersion, DateTimeOffset.UtcNow);
            }

            var previous = stateStore.GetLastResult();
            var result = await ConnectLockedAsync(settings, brokerSettings.IbkrEnvironment, key, cancellationToken);
            await WriteTransitionAuditAsync(previous, result, cancellationToken);
            return result;
        }
        finally
        {
            connectionLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ReconnectInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureConnectedAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "IBKR persistent session monitor failed");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        await connectionLock.WaitAsync(CancellationToken.None);
        try
        {
            await DisconnectCurrentSessionAsync();
        }
        finally
        {
            connectionLock.Release();
        }
    }

    private async Task<BrokerConnectionTestResult> ConnectLockedAsync(
        IBKRSettings settings,
        string environment,
        IBKRConnectionKey key,
        CancellationToken cancellationToken)
    {
        await DisconnectCurrentSessionAsync();

        var testedAt = DateTimeOffset.UtcNow;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HandshakeTimeout);

        var nextWrapper = new SessionWrapper();
        var nextSignal = new EReaderMonitorSignal();
        var nextClient = new EClientSocket(nextWrapper, nextSignal);
        var nextSessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            await Task.Run(() => nextClient.eConnect(settings.Host, settings.Port, settings.ClientId), CancellationToken.None)
                .WaitAsync(timeout.Token);

            var reader = new EReader(nextClient, nextSignal);
            reader.Start();

            var nextPump = Task.Run(() =>
            {
                while (!nextSessionCts.IsCancellationRequested && nextClient.IsConnected())
                {
                    try
                    {
                        nextSignal.waitForSignal();
                        reader.processMsgs();
                    }
                    catch (Exception error)
                    {
                        nextWrapper.CaptureReaderError(error);
                        break;
                    }
                }
            }, CancellationToken.None);

            await nextWrapper.WaitForHandshakeAsync(timeout.Token);
            nextClient.reqManagedAccts();

            var accounts = await nextWrapper.WaitForManagedAccountsAsync(AccountsTimeout, timeout.Token);
            var result = BuildSuccessResult(settings, environment, accounts, nextClient.ServerVersion, testedAt);

            client = nextClient;
            signal = nextSignal;
            wrapper = nextWrapper;
            sessionCts = nextSessionCts;
            messagePump = nextPump;
            connectionKey = key;

            return SaveResult(result);
        }
        catch (OperationCanceledException)
        {
            nextClient.eDisconnect();
            nextSessionCts.Cancel();
            nextSignal.issueSignal();

            var details = nextWrapper.GetDiagnosticDetails();
            var message = string.IsNullOrWhiteSpace(details)
                ? "IBKR persistent session handshake timed out after 6 seconds"
                : $"IBKR persistent session handshake timed out after 6 seconds. {details}";

            return SaveResult(CreateFailure(settings, environment, message, testedAt));
        }
        catch (Exception error)
        {
            nextClient.eDisconnect();
            nextSessionCts.Cancel();
            nextSignal.issueSignal();

            return SaveResult(CreateFailure(settings, environment, $"IBKR persistent session failed: {error.Message}", testedAt));
        }
    }

    private bool IsConnectedFor(IBKRConnectionKey key)
    {
        return client?.IsConnected() == true
            && connectionKey == key
            && stateStore.GetLastResult()?.HandshakeOk == true;
    }

    private async Task DisconnectCurrentSessionAsync()
    {
        var currentSignal = signal;
        var currentCts = sessionCts;
        var currentPump = messagePump;

        currentCts?.Cancel();
        currentSignal?.issueSignal();
        client?.eDisconnect();

        if (currentPump is not null)
        {
            try
            {
                await currentPump.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
            }
            catch (TimeoutException)
            {
                logger.LogDebug("IBKR message pump did not stop within 1 second");
            }
        }

        client = null;
        signal = null;
        wrapper = null;
        sessionCts?.Dispose();
        sessionCts = null;
        messagePump = null;
        connectionKey = null;
    }

    private BrokerConnectionTestResult SaveResult(BrokerConnectionTestResult result)
    {
        stateStore.SetLastResult(result);
        return result;
    }

    private static BrokerConnectionTestResult CreatePaperResult(DateTimeOffset testedAt)
    {
        return new BrokerConnectionTestResult(
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
    }

    private static BrokerConnectionTestResult BuildSuccessResult(
        IBKRSettings settings,
        string environment,
        IReadOnlyList<string> accounts,
        int? serverVersion,
        DateTimeOffset testedAt)
    {
        var selectedAccount = string.IsNullOrWhiteSpace(settings.Account) ? null : settings.Account.Trim();
        var accountVerified = selectedAccount is not null
            && accounts.Any(account => account.Equals(selectedAccount, StringComparison.OrdinalIgnoreCase));
        var accountList = accounts.Count == 0 ? "none" : string.Join(", ", accounts);
        var message = selectedAccount is null
            ? $"IBKR {environment} persistent session connected. Managed accounts: {accountList}. Configure an account to verify."
            : accountVerified
                ? $"IBKR {environment} persistent session connected and account {selectedAccount} was verified"
                : $"IBKR {environment} persistent session connected, but account {selectedAccount} was not found. Managed accounts: {accountList}";

        return new BrokerConnectionTestResult(
            Ok: selectedAccount is not null && accountVerified,
            Mode: "IBKR",
            Environment: environment,
            Host: settings.Host,
            Port: settings.Port,
            HandshakeOk: true,
            AccountVerified: accountVerified,
            ManagedAccounts: accounts,
            SelectedAccount: selectedAccount,
            ServerVersion: serverVersion,
            Message: message,
            TestedAt: testedAt);
    }

    private static BrokerConnectionTestResult CreateFailure(
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

    private async Task WriteTransitionAuditAsync(BrokerConnectionTestResult? previous, BrokerConnectionTestResult current, CancellationToken cancellationToken)
    {
        var action = GetTransitionAction(previous, current);
        if (action is null)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        db.AuditLogs.Add(AuditLogRecord.BrokerAction(
            action,
            $"IBKR {current.Environment} persistent session {current.Host}:{current.Port} - {current.Message}"));
        await db.SaveChangesAsync(cancellationToken);
        await hub.Clients.All.SendAsync("trading.updated", new
        {
            event_type = action,
            symbol = (string?)null,
            occurred_at = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    private static string? GetTransitionAction(BrokerConnectionTestResult? previous, BrokerConnectionTestResult current)
    {
        var wasConnected = previous?.Mode.Equals("IBKR", StringComparison.OrdinalIgnoreCase) == true && previous.HandshakeOk;

        if (wasConnected && !current.HandshakeOk)
        {
            return "ibkr.connection_lost";
        }

        if (!wasConnected && current.HandshakeOk)
        {
            return "ibkr.reconnected";
        }

        if (previous?.AccountVerified != true && current.AccountVerified)
        {
            return "ibkr.account_verified";
        }

        return null;
    }

    private sealed class SessionWrapper : DefaultEWrapper
    {
        private readonly TaskCompletionSource<int> handshakeSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IReadOnlyList<string>> accountsSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object diagnosticsLock = new();
        private readonly List<string> diagnostics = [];

        public override void nextValidId(int orderId)
        {
            AddDiagnostic($"nextValidId {orderId} received");
            handshakeSource.TrySetResult(orderId);
        }

        public override void managedAccounts(string accountsList)
        {
            AddDiagnostic($"managedAccounts received: {accountsList}");
            var accounts = accountsList
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(account => !string.IsNullOrWhiteSpace(account))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            accountsSource.TrySetResult(accounts);
        }

        public override void connectionClosed()
        {
            AddDiagnostic("connectionClosed received");
            handshakeSource.TrySetException(new InvalidOperationException("IBKR API connection closed before handshake completed"));
            accountsSource.TrySetException(new InvalidOperationException("IBKR API connection closed before managed accounts were received"));
        }

        public override void error(Exception e)
        {
            AddDiagnostic($"error exception: {e.Message}");
            handshakeSource.TrySetException(e);
            accountsSource.TrySetException(e);
        }

        public override void error(string str)
        {
            AddDiagnostic($"error: {str}");
        }

        public override void error(int id, int errorCode, string errorMsg, string advancedOrderRejectJson)
        {
            AddDiagnostic($"error {errorCode}: {errorMsg}");
        }

        public void CaptureReaderError(Exception error)
        {
            AddDiagnostic($"reader error: {error.Message}");
            handshakeSource.TrySetException(error);
            accountsSource.TrySetException(error);
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

        public string GetDiagnosticDetails()
        {
            lock (diagnosticsLock)
            {
                return diagnostics.Count == 0 ? "" : string.Join("; ", diagnostics);
            }
        }

        private void AddDiagnostic(string message)
        {
            lock (diagnosticsLock)
            {
                diagnostics.Add(message);
            }
        }
    }

    private sealed record IBKRConnectionKey(
        string Environment,
        string Host,
        int Port,
        int ClientId,
        string Account)
    {
        public static IBKRConnectionKey From(BrokerSettings brokerSettings, IBKRSettings settings)
        {
            return new IBKRConnectionKey(
                brokerSettings.IbkrEnvironment,
                settings.Host,
                settings.Port,
                settings.ClientId,
                settings.Account);
        }
    }
}
