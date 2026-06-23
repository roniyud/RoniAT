using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RoniAT.TradingEngine.Contracts;
using RoniAT.TradingEngine.Data;
using RoniAT.TradingEngine.Hubs;
using RoniAT.TradingEngine.Models;
using IBApi;
using System.Globalization;

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
    private static readonly TimeSpan PositionsTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HistoricalDataTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan OrderStatusTimeout = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim connectionLock = new(1, 1);
    private int nextMarketDataRequestId = 7000;
    private int nextStreamingRequestId = 9000;
    private EClientSocket? client;
    private EReaderSignal? signal;
    private CancellationTokenSource? sessionCts;
    private Task? messagePump;
    private SessionWrapper? wrapper;
    private IBKRConnectionKey? connectionKey;
    private readonly Dictionary<string, int> marketDataSubscriptions = new(StringComparer.OrdinalIgnoreCase);

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
                var result = await EnsureConnectedAsync(stoppingToken);
                if (result.Mode.Equals("IBKR", StringComparison.OrdinalIgnoreCase) && result.HandshakeOk)
                {
                    await SyncPositionsAsync(stoppingToken);
                }
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

        var nextWrapper = new SessionWrapper(PublishMarketTick);
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

            SaveResult(result);
            await SyncPositionsLockedAsync(cancellationToken);
            return result;
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

    public async Task SyncPositionsAsync(CancellationToken cancellationToken = default)
    {
        await connectionLock.WaitAsync(cancellationToken);

        try
        {
            if (client?.IsConnected() != true || wrapper is null)
            {
                return;
            }

            await SyncPositionsLockedAsync(cancellationToken);
        }
        finally
        {
            connectionLock.Release();
        }
    }

    public async Task<IReadOnlyList<CandleResponse>> GetHistoricalCandlesAsync(
        string symbol,
        string timeframe,
        CancellationToken cancellationToken = default)
    {
        await connectionLock.WaitAsync(cancellationToken);

        try
        {
            var result = await EnsureConnectedLockedAsync(cancellationToken);
            if (!result.HandshakeOk || client?.IsConnected() != true || wrapper is null)
            {
                throw new InvalidOperationException(result.Message);
            }

            var requestId = Interlocked.Increment(ref nextMarketDataRequestId);
            var request = HistoricalDataRequest.From(symbol, timeframe);

            wrapper.ResetHistoricalData(requestId);
            EnsureMarketDataSubscriptionLocked(request.Symbol, request.Contract);
            client.reqHistoricalData(
                requestId,
                request.Contract,
                endDateTime: "",
                durationStr: request.Duration,
                barSizeSetting: request.BarSize,
                whatToShow: "TRADES",
                useRTH: 0,
                formatDate: 2,
                keepUpToDate: false,
                chartOptions: []);

            try
            {
                var candles = await wrapper.WaitForHistoricalDataAsync(requestId, HistoricalDataTimeout, cancellationToken);
                return candles ?? [];
            }
            finally
            {
                client.cancelHistoricalData(requestId);
            }
        }
        finally
        {
            connectionLock.Release();
        }
    }

    public async Task<IBKRSubmittedOrder> PlaceMarketOrderAsync(
        string symbol,
        string direction,
        int quantity,
        CancellationToken cancellationToken = default)
    {
        await connectionLock.WaitAsync(cancellationToken);

        try
        {
            var brokerSettings = settingsStore.Get();
            var settings = settingsStore.GetActiveIBKRSettings();

            if (!brokerSettings.Mode.Equals("IBKR", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Broker mode is not IBKR");
            }

            if (!brokerSettings.IbkrEnvironment.Equals("Paper", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Live IBKR order placement is not enabled. Switch to Paper.");
            }

            if (settings.ReadOnly)
            {
                throw new InvalidOperationException("IBKR is configured as read-only. Disable read-only before placing Paper orders.");
            }

            if (quantity <= 0)
            {
                throw new ArgumentException("quantity must be greater than zero", nameof(quantity));
            }

            var result = await EnsureConnectedLockedAsync(cancellationToken);
            if (!result.HandshakeOk || client?.IsConnected() != true || wrapper is null)
            {
                throw new InvalidOperationException(result.Message);
            }

            var normalizedDirection = direction.Trim().ToUpperInvariant();
            var action = normalizedDirection switch
            {
                "LONG" => "BUY",
                "SHORT" => "SELL",
                _ => throw new ArgumentException("direction must be LONG or SHORT", nameof(direction))
            };
            var normalizedSymbol = NormalizeDisplaySymbol(symbol);
            var contract = BuildTradableContract(normalizedSymbol);
            var orderId = wrapper.TakeNextOrderId();
            var order = new Order
            {
                OrderId = orderId,
                Action = action,
                TotalQuantity = quantity,
                OrderType = "MKT",
                Tif = "DAY",
                Account = settings.Account,
                Transmit = true
            };

            wrapper.TrackSubmittedOrder(orderId);
            client.placeOrder(orderId, contract, order);

            var status = await wrapper.WaitForOrderStatusAsync(orderId, OrderStatusTimeout, cancellationToken);
            return new IBKRSubmittedOrder(
                OrderId: orderId,
                Symbol: normalizedSymbol,
                Direction: normalizedDirection,
                Quantity: quantity,
                Status: status.Status,
                FilledQuantity: status.FilledQuantity,
                RemainingQuantity: status.RemainingQuantity,
                AverageFillPrice: status.AverageFillPrice,
                LastFillPrice: status.LastFillPrice,
                Message: status.Message);
        }
        finally
        {
            connectionLock.Release();
        }
    }

    private void EnsureMarketDataSubscriptionLocked(string symbol, Contract contract)
    {
        if (client?.IsConnected() != true || wrapper is null || marketDataSubscriptions.ContainsKey(symbol))
        {
            return;
        }

        var requestId = Interlocked.Increment(ref nextStreamingRequestId);
        marketDataSubscriptions[symbol] = requestId;
        wrapper.TrackMarketDataSubscription(requestId, symbol);
        client.reqMktData(
            requestId,
            contract,
            genericTickList: "",
            snapshot: false,
            regulatorySnaphsot: false,
            mktDataOptions: []);
    }

    private void PublishMarketTick(IBKRMarketTick tick)
    {
        _ = hub.Clients.All.SendAsync("market.tick", new
        {
            symbol = tick.Symbol,
            price = tick.Price,
            time = tick.Time,
            source = tick.Source
        });
    }

    private async Task<BrokerConnectionTestResult> EnsureConnectedLockedAsync(CancellationToken cancellationToken)
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

    private async Task SyncPositionsLockedAsync(CancellationToken cancellationToken)
    {
        if (client?.IsConnected() != true || wrapper is null)
        {
            return;
        }

        wrapper.ResetPositions();
        client.reqPositions();
        var positions = await wrapper.WaitForPositionsAsync(PositionsTimeout, cancellationToken);
        client.cancelPositions();

        if (positions is null)
        {
            logger.LogWarning("IBKR positions sync timed out before positionEnd");
            return;
        }

        await PersistPositionsAsync(positions, cancellationToken);
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
        marketDataSubscriptions.Clear();
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

    private async Task PersistPositionsAsync(IReadOnlyList<IBKRPositionSnapshot> ibkrPositions, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var existing = await db.Positions.ToListAsync(cancellationToken);
        var systemOwnedSymbols = await db.Orders
            .Select(order => order.Symbol)
            .Distinct()
            .ToListAsync(cancellationToken);
        var systemOwnedSymbolSet = systemOwnedSymbols.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;
        var changed = false;

        var activeSymbols = ibkrPositions
            .Where(position => systemOwnedSymbolSet.Contains(position.Symbol))
            .Select(position => position.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var stale in existing
            .Where(position => systemOwnedSymbolSet.Contains(position.Symbol))
            .Where(position => !activeSymbols.Contains(position.Symbol))
            .ToList())
        {
            db.Positions.Remove(stale);
            changed = true;
        }

        foreach (var ibkrPosition in ibkrPositions)
        {
            if (!systemOwnedSymbolSet.Contains(ibkrPosition.Symbol))
            {
                continue;
            }

            var quantity = (int)Math.Abs(decimal.ToInt32(decimal.Round(ibkrPosition.Quantity, 0, MidpointRounding.AwayFromZero)));
            if (quantity == 0)
            {
                continue;
            }

            var direction = ibkrPosition.Quantity > 0 ? "LONG" : "SHORT";
            var existingPosition = existing.SingleOrDefault(position =>
                position.Symbol.Equals(ibkrPosition.Symbol, StringComparison.OrdinalIgnoreCase));

            if (existingPosition is null)
            {
                db.Positions.Add(new PositionRecord
                {
                    Symbol = ibkrPosition.Symbol,
                    Direction = direction,
                    Quantity = quantity,
                    AveragePrice = ibkrPosition.AveragePrice,
                    StopLoss = null,
                    TakeProfit1 = null,
                    TakeProfit2 = null,
                    OpenedAt = now,
                    UpdatedAt = now
                });
                changed = true;
                continue;
            }

            if (existingPosition.Direction != direction
                || existingPosition.Quantity != quantity
                || existingPosition.AveragePrice != ibkrPosition.AveragePrice)
            {
                existingPosition.Direction = direction;
                existingPosition.Quantity = quantity;
                existingPosition.AveragePrice = ibkrPosition.AveragePrice;
                existingPosition.StopLoss = null;
                existingPosition.TakeProfit1 = null;
                existingPosition.TakeProfit2 = null;
                existingPosition.UpdatedAt = now;
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        db.AuditLogs.Add(AuditLogRecord.BrokerAction(
            "ibkr.positions_synced",
            $"IBKR positions synced: {activeSymbols.Count} system-owned open positions from {ibkrPositions.Count} account positions"));
        await db.SaveChangesAsync(cancellationToken);
        await hub.Clients.All.SendAsync("trading.updated", new
        {
            event_type = "positions.updated",
            symbol = (string?)null,
            occurred_at = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    private sealed class SessionWrapper(Action<IBKRMarketTick> onMarketTick) : DefaultEWrapper
    {
        private readonly TaskCompletionSource<int> handshakeSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IReadOnlyList<string>> accountsSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object diagnosticsLock = new();
        private readonly object positionsLock = new();
        private readonly object historicalDataLock = new();
        private readonly object marketDataLock = new();
        private readonly object ordersLock = new();
        private readonly List<string> diagnostics = [];
        private readonly Dictionary<int, string> marketDataSymbols = [];
        private readonly Dictionary<int, OrderStatusSnapshot> orderStatuses = [];
        private readonly Dictionary<int, TaskCompletionSource<OrderStatusSnapshot>> orderStatusSources = [];
        private List<IBKRPositionSnapshot> positions = [];
        private TaskCompletionSource<IReadOnlyList<IBKRPositionSnapshot>> positionsSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int historicalDataRequestId;
        private List<CandleResponse> historicalCandles = [];
        private TaskCompletionSource<IReadOnlyList<CandleResponse>> historicalDataSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int nextOrderId;

        public override void nextValidId(int orderId)
        {
            AddDiagnostic($"nextValidId {orderId} received");
            lock (ordersLock)
            {
                nextOrderId = Math.Max(nextOrderId, orderId);
            }
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

        public override void position(string account, Contract contract, decimal pos, double avgCost)
        {
            if (pos == 0)
            {
                return;
            }

            var symbol = NormalizeSymbol(contract);
            if (string.IsNullOrWhiteSpace(symbol))
            {
                return;
            }

            lock (positionsLock)
            {
                positions.Add(new IBKRPositionSnapshot(symbol, pos, Convert.ToDecimal(avgCost)));
            }
        }

        public override void positionEnd()
        {
            lock (positionsLock)
            {
                positionsSource.TrySetResult(positions.ToArray());
            }
        }

        public override void tickPrice(int tickerId, int field, double price, TickAttrib attribs)
        {
            if (price <= 0 || !IsTradePriceTick(field))
            {
                return;
            }

            string? symbol;
            lock (marketDataLock)
            {
                marketDataSymbols.TryGetValue(tickerId, out symbol);
            }

            if (string.IsNullOrWhiteSpace(symbol))
            {
                return;
            }

            onMarketTick(new IBKRMarketTick(
                symbol,
                Convert.ToDecimal(price),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                IsDelayedTick(field) ? "IBKR delayed tick" : "IBKR realtime tick"));
        }

        public override void orderStatus(
            int orderId,
            string status,
            decimal filled,
            decimal remaining,
            double avgFillPrice,
            int permId,
            int parentId,
            double lastFillPrice,
            int clientId,
            string whyHeld,
            double mktCapPrice)
        {
            var snapshot = new OrderStatusSnapshot(
                orderId,
                status,
                filled,
                remaining,
                Convert.ToDecimal(avgFillPrice),
                Convert.ToDecimal(lastFillPrice),
                whyHeld);

            lock (ordersLock)
            {
                orderStatuses[orderId] = snapshot;
                if (IsTerminalOrderStatus(status) && orderStatusSources.TryGetValue(orderId, out var source))
                {
                    source.TrySetResult(snapshot);
                }
            }
        }

        public override void historicalData(int reqId, Bar bar)
        {
            lock (historicalDataLock)
            {
                if (reqId != historicalDataRequestId)
                {
                    return;
                }

                if (!TryParseUnixTime(bar.Time, out var time))
                {
                    AddDiagnostic($"historicalData {reqId} skipped unsupported bar time {bar.Time}");
                    return;
                }

                historicalCandles.Add(new CandleResponse(
                    time,
                    Convert.ToDecimal(bar.Open),
                    Convert.ToDecimal(bar.High),
                    Convert.ToDecimal(bar.Low),
                    Convert.ToDecimal(bar.Close)));
            }
        }

        public override void historicalDataEnd(int reqId, string start, string end)
        {
            lock (historicalDataLock)
            {
                if (reqId != historicalDataRequestId)
                {
                    return;
                }

                historicalDataSource.TrySetResult(historicalCandles
                    .OrderBy(candle => candle.Time)
                    .TakeLast(300)
                    .ToArray());
            }
        }

        public override void connectionClosed()
        {
            AddDiagnostic("connectionClosed received");
            handshakeSource.TrySetException(new InvalidOperationException("IBKR API connection closed before handshake completed"));
            accountsSource.TrySetException(new InvalidOperationException("IBKR API connection closed before managed accounts were received"));
            historicalDataSource.TrySetException(new InvalidOperationException("IBKR API connection closed before historical data was received"));
            FailPendingOrders(new InvalidOperationException("IBKR API connection closed before order status was received"));
        }

        public override void error(Exception e)
        {
            AddDiagnostic($"error exception: {e.Message}");
            handshakeSource.TrySetException(e);
            accountsSource.TrySetException(e);
            historicalDataSource.TrySetException(e);
            FailPendingOrders(e);
        }

        public override void error(string str)
        {
            AddDiagnostic($"error: {str}");
        }

        public override void error(int id, int errorCode, string errorMsg, string advancedOrderRejectJson)
        {
            AddDiagnostic($"error {errorCode}: {errorMsg}");

            if (id == historicalDataRequestId && IsHistoricalDataError(errorCode))
            {
                historicalDataSource.TrySetException(new InvalidOperationException($"IBKR historical data error {errorCode}: {errorMsg}"));
            }

            lock (ordersLock)
            {
                if (orderStatusSources.TryGetValue(id, out var source))
                {
                    source.TrySetException(new InvalidOperationException($"IBKR order error {errorCode}: {errorMsg}"));
                }
            }
        }

        public void CaptureReaderError(Exception error)
        {
            AddDiagnostic($"reader error: {error.Message}");
            handshakeSource.TrySetException(error);
            accountsSource.TrySetException(error);
            positionsSource.TrySetException(error);
            historicalDataSource.TrySetException(error);
            FailPendingOrders(error);
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

        public void ResetPositions()
        {
            lock (positionsLock)
            {
                positions = [];
                positionsSource = new TaskCompletionSource<IReadOnlyList<IBKRPositionSnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void ResetHistoricalData(int requestId)
        {
            lock (historicalDataLock)
            {
                historicalDataRequestId = requestId;
                historicalCandles = [];
                historicalDataSource = new TaskCompletionSource<IReadOnlyList<CandleResponse>>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void TrackMarketDataSubscription(int requestId, string symbol)
        {
            lock (marketDataLock)
            {
                marketDataSymbols[requestId] = symbol;
            }
        }

        public int TakeNextOrderId()
        {
            lock (ordersLock)
            {
                if (nextOrderId <= 0)
                {
                    throw new InvalidOperationException("IBKR nextValidId was not received");
                }

                return nextOrderId++;
            }
        }

        public void TrackSubmittedOrder(int orderId)
        {
            lock (ordersLock)
            {
                orderStatusSources[orderId] = new TaskCompletionSource<OrderStatusSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                orderStatuses[orderId] = new OrderStatusSnapshot(orderId, "Submitted", 0, 0, 0, 0, "");
            }
        }

        public async Task<OrderStatusSnapshot> WaitForOrderStatusAsync(int orderId, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Task<OrderStatusSnapshot>? task;
            lock (ordersLock)
            {
                orderStatusSources.TryGetValue(orderId, out var source);
                task = source?.Task;
            }

            if (task is null)
            {
                throw new InvalidOperationException($"IBKR order {orderId} is not tracked");
            }

            using var orderTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            orderTimeout.CancelAfter(timeout);

            try
            {
                return await task.WaitAsync(orderTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lock (ordersLock)
                {
                    return orderStatuses.TryGetValue(orderId, out var snapshot)
                        ? snapshot with { Message = "Timed out waiting for final IBKR order status" }
                        : new OrderStatusSnapshot(orderId, "Submitted", 0, 0, 0, 0, "Timed out waiting for IBKR order status");
                }
            }
        }

        public async Task<IReadOnlyList<CandleResponse>?> WaitForHistoricalDataAsync(int requestId, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Task<IReadOnlyList<CandleResponse>> task;
            lock (historicalDataLock)
            {
                if (requestId != historicalDataRequestId)
                {
                    return [];
                }

                task = historicalDataSource.Task;
            }

            using var historicalTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            historicalTimeout.CancelAfter(timeout);

            try
            {
                return await task.WaitAsync(historicalTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }

        public async Task<IReadOnlyList<IBKRPositionSnapshot>?> WaitForPositionsAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var positionsTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            positionsTimeout.CancelAfter(timeout);

            try
            {
                return await positionsSource.Task.WaitAsync(positionsTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }

        private void AddDiagnostic(string message)
        {
            lock (diagnosticsLock)
            {
                diagnostics.Add(message);
            }
        }

        private static string NormalizeSymbol(Contract contract)
        {
            if (contract.SecType.Equals("FUT", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(contract.Symbol))
            {
                var root = contract.Symbol.Trim().ToUpperInvariant();
                if (root is "MNQ" or "NQ" or "MES" or "ES")
                {
                    return $"{root}1!";
                }
            }

            if (!string.IsNullOrWhiteSpace(contract.LocalSymbol)) return contract.LocalSymbol.Trim();
            if (!string.IsNullOrWhiteSpace(contract.Symbol)) return contract.Symbol.Trim();
            return "";
        }

        private static bool TryParseUnixTime(string value, out long unixTime)
        {
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out unixTime))
            {
                return true;
            }

            unixTime = 0;
            return false;
        }

        private static bool IsHistoricalDataError(int errorCode)
        {
            return errorCode is 162 or 165 or 200 or 321 or 354 or 366 or 420;
        }

        private static bool IsTradePriceTick(int field)
        {
            return field is TickType.LAST or TickType.DELAYED_LAST;
        }

        private static bool IsDelayedTick(int field)
        {
            return field is TickType.DELAYED_LAST;
        }

        private void FailPendingOrders(Exception error)
        {
            lock (ordersLock)
            {
                foreach (var source in orderStatusSources.Values)
                {
                    source.TrySetException(error);
                }
            }
        }

        private static bool IsTerminalOrderStatus(string status)
        {
            return status.Equals("Filled", StringComparison.OrdinalIgnoreCase)
                || status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)
                || status.Equals("ApiCancelled", StringComparison.OrdinalIgnoreCase)
                || status.Equals("Inactive", StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed record HistoricalDataRequest(string Symbol, Contract Contract, string Duration, string BarSize)
    {
        public static HistoricalDataRequest From(string symbol, string timeframe)
        {
            var normalizedSymbol = NormalizeTradingViewSymbol(symbol);
            var normalizedTimeframe = string.IsNullOrWhiteSpace(timeframe) ? "5m" : timeframe.Trim().ToLowerInvariant();
            var (duration, barSize) = normalizedTimeframe switch
            {
                "1m" => ("2 D", "1 min"),
                "5m" => ("5 D", "5 mins"),
                "15m" => ("10 D", "15 mins"),
                "1h" => ("30 D", "1 hour"),
                _ => throw new ArgumentException("Unsupported timeframe. Use 1m, 5m, 15m, or 1h.", nameof(timeframe))
            };

            return new HistoricalDataRequest(symbol.Trim().ToUpperInvariant(), BuildContract(normalizedSymbol), duration, barSize);
        }

        private static string NormalizeTradingViewSymbol(string symbol)
        {
            return (string.IsNullOrWhiteSpace(symbol) ? "MNQ1!" : symbol.Trim().ToUpperInvariant())
                .Replace("1!", "", StringComparison.OrdinalIgnoreCase);
        }

        private static Contract BuildContract(string symbol)
        {
            if (symbol is "MNQ" or "NQ" or "MES" or "ES")
            {
                return new Contract
                {
                    Symbol = symbol,
                    SecType = "CONTFUT",
                    Exchange = "CME",
                    Currency = "USD"
                };
            }

            return new Contract
            {
                Symbol = symbol,
                SecType = "STK",
                Exchange = "SMART",
                Currency = "USD"
            };
        }
    }

    private static string NormalizeDisplaySymbol(string symbol)
    {
        return (string.IsNullOrWhiteSpace(symbol) ? "MNQ1!" : symbol.Trim().ToUpperInvariant());
    }

    private static Contract BuildTradableContract(string symbol)
    {
        var root = symbol.Replace("1!", "", StringComparison.OrdinalIgnoreCase);
        if (root is "MNQ" or "NQ" or "MES" or "ES")
        {
            return new Contract
            {
                Symbol = root,
                SecType = "FUT",
                LastTradeDateOrContractMonth = GetNextQuarterlyFuturesMonth(DateTimeOffset.UtcNow),
                Exchange = "CME",
                Currency = "USD"
            };
        }

        return new Contract
        {
            Symbol = root,
            SecType = "STK",
            Exchange = "SMART",
            Currency = "USD"
        };
    }

    private static string GetNextQuarterlyFuturesMonth(DateTimeOffset now)
    {
        var year = now.Year;
        var month = now.Month;
        var quarterlyMonths = new[] { 3, 6, 9, 12 };
        var targetMonth = quarterlyMonths.FirstOrDefault(candidate => candidate > month || (candidate == month && now.Day <= 15));
        if (targetMonth == 0)
        {
            year++;
            targetMonth = 3;
        }

        return $"{year:0000}{targetMonth:00}";
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

    private sealed record OrderStatusSnapshot(
        int OrderId,
        string Status,
        decimal FilledQuantity,
        decimal RemainingQuantity,
        decimal AverageFillPrice,
        decimal LastFillPrice,
        string Message);

    private sealed record IBKRPositionSnapshot(string Symbol, decimal Quantity, decimal AveragePrice);

    private sealed record IBKRMarketTick(string Symbol, decimal Price, long Time, string Source);
}

public sealed record IBKRSubmittedOrder(
    int OrderId,
    string Symbol,
    string Direction,
    int Quantity,
    string Status,
    decimal FilledQuantity,
    decimal RemainingQuantity,
    decimal AverageFillPrice,
    decimal LastFillPrice,
    string Message);
