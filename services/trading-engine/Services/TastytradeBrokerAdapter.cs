using Microsoft.EntityFrameworkCore;
using RoniAT.TradingEngine.Data;
using RoniAT.TradingEngine.Models;

namespace RoniAT.TradingEngine.Services;

public sealed class TastytradeBrokerAdapter(
    BrokerSettingsStore settingsStore,
    RiskSettingsStore riskSettingsStore,
    TastytradeOrderClient orderClient,
    TastytradeAccountClient accountClient,
    TastytradePendingProtectionService pendingProtectionService,
    SystemOwnedPositionTracker systemOwnedPositionTracker) : IBrokerAdapter
{
    public string Name => "Tastytrade";

    public BrokerStatus GetStatus()
    {
        var brokerSettings = settingsStore.Get();
        var settings = settingsStore.GetActiveTastytradeSettings();
        var configured = !string.IsNullOrWhiteSpace(settings.ApiBaseUrl)
            && !string.IsNullOrWhiteSpace(settings.StreamerBaseUrl)
            && (!settings.Enabled
                || (!string.IsNullOrWhiteSpace(settings.AccessToken)
                    && !string.IsNullOrWhiteSpace(settings.AccountNumber)));
        var connected = settings.Enabled && !string.IsNullOrWhiteSpace(settings.AccessToken);

        return new BrokerStatus(
            Mode: Name,
            Environment: brokerSettings.TastytradeEnvironment,
            Configured: configured,
            Enabled: settings.Enabled,
            Connected: connected,
            ReadOnly: settings.ReadOnly,
            Message: connected
                ? $"Tastytrade {brokerSettings.TastytradeEnvironment} access token is active"
                : settings.Enabled
                    ? $"Tastytrade {brokerSettings.TastytradeEnvironment} is enabled but access token is missing"
                : $"Tastytrade {brokerSettings.TastytradeEnvironment} is configured but disabled");
    }

    public async Task ApplyEntrySignalAsync(TradingSignalRecord signal, TradingDbContext db)
    {
        var result = await PlaceMarketOrderAsync(
            signal.Symbol,
            signal.Direction,
            signal.Contracts,
            signal.EntryPrice,
            db,
            attachProtection: true,
            protectionDistance: null,
            stopLoss: signal.StopLoss,
            takeProfit: signal.TakeProfit1);

        signal.Status = result.Ok
            ? "tastytrade_order_submitted"
            : result.Status == "blocked" ? "broker_blocked" : "tastytrade_order_failed";
    }

    public async Task<MarketOrderResult> PlaceMarketOrderAsync(
        string symbol,
        string direction,
        int contracts,
        decimal? referencePrice,
        TradingDbContext db,
        bool attachProtection = false,
        decimal? protectionDistance = null,
        decimal? stopLoss = null,
        decimal? takeProfit = null)
    {
        var brokerSettings = settingsStore.Get();
        var settings = settingsStore.GetActiveTastytradeSettings();
        var normalizedSymbol = symbol.Trim().ToUpperInvariant();
        var normalizedDirection = direction.Trim().ToUpperInvariant();

        if (!brokerSettings.TastytradeEnvironment.Equals("Sandbox", StringComparison.OrdinalIgnoreCase))
        {
            return BlockMarketOrder(db, normalizedSymbol, normalizedDirection, contracts, "Tastytrade live order placement is not enabled");
        }

        if (!settings.Enabled)
        {
            return BlockMarketOrder(db, normalizedSymbol, normalizedDirection, contracts, "Tastytrade Sandbox is disabled");
        }

        if (settings.ReadOnly)
        {
            return BlockMarketOrder(db, normalizedSymbol, normalizedDirection, contracts, "Tastytrade Sandbox is configured as read-only");
        }

        try
        {
            var (effectiveStopLoss, effectiveTakeProfit) = ResolveProtectionLevels(
                normalizedDirection,
                referencePrice,
                attachProtection,
                protectionDistance,
                stopLoss,
                takeProfit);
            var systemManagedProtection = riskSettingsStore.Get().SystemManagedProtectionEnabled;
            var shouldAttachProtection = attachProtection && effectiveStopLoss is not null && effectiveTakeProfit is not null && !systemManagedProtection;
            var shouldCreateSystemProtection = attachProtection && effectiveStopLoss is not null && effectiveTakeProfit is not null && systemManagedProtection;
            systemOwnedPositionTracker.Mark(Name, normalizedSymbol);
            var submittedOrder = await orderClient.SubmitMarketOrderAsync(
                normalizedSymbol,
                normalizedDirection,
                contracts);

            var now = DateTimeOffset.UtcNow;
            var order = new OrderRecord
            {
                BrokerOrderId = submittedOrder.OrderId,
                Symbol = normalizedSymbol,
                Direction = normalizedDirection,
                OrderType = shouldCreateSystemProtection ? "tastytrade_market_system_managed" : shouldAttachProtection ? "tastytrade_market_with_oco" : "tastytrade_market",
                Quantity = contracts,
                Price = referencePrice,
                Status = MapOrderStatus(submittedOrder.Status),
                CreatedAt = now,
                UpdatedAt = now
            };

            db.Orders.Add(order);
            db.BrokerEvents.Add(new BrokerEventRecord
            {
                EventType = "tastytrade.market_order_submitted",
                PayloadJson = $$"""
                {"symbol":"{{normalizedSymbol}}","routed_symbol":"{{submittedOrder.RoutedSymbol}}","direction":"{{normalizedDirection}}","contracts":{{contracts}},"order_id":"{{submittedOrder.OrderId}}","status":"{{submittedOrder.Status}}","requested_protection":{{shouldAttachProtection.ToString().ToLowerInvariant()}},"stop_loss":{{JsonNumber(effectiveStopLoss)}},"take_profit":{{JsonNumber(effectiveTakeProfit)}}}
                """,
                CreatedAt = now
            });
            db.AuditLogs.Add(AuditLogRecord.BrokerAction(
                "tastytrade.market_order_submitted",
                $"Tastytrade Sandbox market order {submittedOrder.OrderId} {normalizedSymbol}->{submittedOrder.RoutedSymbol} {normalizedDirection} {contracts} status={submittedOrder.Status}"));

            if (!submittedOrder.Ok || MapOrderStatus(submittedOrder.Status) == "rejected")
            {
                order.Status = "rejected";
                order.UpdatedAt = DateTimeOffset.UtcNow;
                db.AuditLogs.Add(AuditLogRecord.BrokerAction(
                    "tastytrade.market_order_rejected",
                    $"Tastytrade Sandbox market order {submittedOrder.OrderId} rejected for {normalizedSymbol}->{submittedOrder.RoutedSymbol}: {submittedOrder.Message}"));

                return new MarketOrderResult(
                    false,
                    "rejected",
                    $"Tastytrade market order {submittedOrder.OrderId} was rejected by the broker: {submittedOrder.Message}",
                    order,
                    null);
            }

            var protectionStatus = "";
            PositionRecord? localPosition = null;
            if (shouldCreateSystemProtection)
            {
                var positionPrice = referencePrice is > 0 ? referencePrice.Value : 0m;
                localPosition = await UpsertLocalPositionAsync(normalizedSymbol, normalizedDirection, contracts, positionPrice, db, now);
                await SystemManagedProtectionOrders.ReplaceAsync(
                    db,
                    localPosition,
                    effectiveStopLoss!.Value,
                    effectiveTakeProfit!.Value,
                    now);

                protectionStatus = $" protected by system SL={effectiveStopLoss} TP={effectiveTakeProfit}";
                db.AuditLogs.Add(AuditLogRecord.BrokerAction(
                    "system.protection_created",
                    $"System-managed Tastytrade protection created for {normalizedSymbol}: SL={effectiveStopLoss}, TP={effectiveTakeProfit}, qty={localPosition.Quantity}"));
            }

            if (shouldAttachProtection)
            {
                var pendingStopLoss = effectiveStopLoss.GetValueOrDefault();
                var pendingTakeProfit = effectiveTakeProfit.GetValueOrDefault();
                try
                {
                    var brokerPosition = await WaitForPositionAsync(
                        normalizedSymbol,
                        submittedOrder.RoutedSymbol,
                        normalizedDirection,
                        contracts);

                    if (brokerPosition is null)
                    {
                        order.Status = "protection_pending";
                        order.UpdatedAt = DateTimeOffset.UtcNow;
                        pendingProtectionService.Enqueue(
                            normalizedSymbol,
                            submittedOrder.RoutedSymbol,
                            normalizedDirection,
                            contracts,
                            pendingStopLoss,
                            pendingTakeProfit,
                            submittedOrder.OrderId);

                        db.AuditLogs.Add(AuditLogRecord.BrokerAction(
                            "tastytrade.oco_protection_pending",
                            $"Tastytrade market order {submittedOrder.OrderId} was submitted, but no matching position was visible yet for {normalizedSymbol}->{submittedOrder.RoutedSymbol}; TP/SL will be attached when the broker position appears"));

                        return new MarketOrderResult(
                            true,
                            "protection_pending",
                            $"Tastytrade market order {submittedOrder.OrderId} submitted. TP/SL are pending and will be attached when the position appears in Tastytrade.",
                            order,
                            null);
                    }

                    var ocoStopLoss = effectiveStopLoss.GetValueOrDefault();
                    var ocoTakeProfit = effectiveTakeProfit.GetValueOrDefault();
                    var oco = await orderClient.SubmitOcoProtectionAsync(
                        brokerPosition,
                        ocoStopLoss,
                        ocoTakeProfit);

                    db.Orders.Add(new OrderRecord
                    {
                        BrokerOrderId = oco.OrderId,
                        Symbol = normalizedSymbol,
                        Direction = normalizedDirection == "LONG" ? "SHORT" : "LONG",
                        OrderType = "tastytrade_oco_protection",
                        Quantity = brokerPosition.Quantity,
                        Price = effectiveTakeProfit,
                        StopPrice = effectiveStopLoss,
                        Status = MapOrderStatus(oco.Status),
                        CreatedAt = now,
                        UpdatedAt = now
                    });

                    protectionStatus = $" protected with OCO SL={effectiveStopLoss} TP={effectiveTakeProfit}";
                    db.AuditLogs.Add(AuditLogRecord.BrokerAction(
                        "tastytrade.oco_protection_attached",
                        $"Attached Tastytrade OCO protection {oco.OrderId} for {normalizedSymbol}->{brokerPosition.Symbol}: SL={effectiveStopLoss}, TP={effectiveTakeProfit}, qty={brokerPosition.Quantity}, status={oco.Status}"));
                }
                catch (Exception protectionError)
                {
                    order.Status = "protection_pending";
                    order.UpdatedAt = DateTimeOffset.UtcNow;
                    pendingProtectionService.Enqueue(
                        normalizedSymbol,
                        submittedOrder.RoutedSymbol,
                        normalizedDirection,
                        contracts,
                        pendingStopLoss,
                        pendingTakeProfit,
                        submittedOrder.OrderId);

                    db.AuditLogs.Add(AuditLogRecord.BrokerAction(
                        "tastytrade.oco_protection_pending",
                        $"Tastytrade market order {submittedOrder.OrderId} was submitted, but TP/SL were not attached yet: {protectionError.Message}. Retry is pending."));

                    return new MarketOrderResult(
                        true,
                        "protection_pending",
                        $"Tastytrade market order {submittedOrder.OrderId} submitted. TP/SL are pending after a temporary attach failure: {protectionError.Message}",
                        order,
                        null);
                }
            }

            return new MarketOrderResult(
                submittedOrder.Ok,
                MapOrderStatus(submittedOrder.Status),
                $"Tastytrade Sandbox market order {submittedOrder.OrderId} submitted for {submittedOrder.RoutedSymbol}: {submittedOrder.Message}{protectionStatus}",
                order,
                localPosition);
        }
        catch (Exception error)
        {
            db.AuditLogs.Add(AuditLogRecord.BrokerAction(
                "tastytrade.market_order_failed",
                $"Tastytrade Sandbox market order failed for {normalizedSymbol} {normalizedDirection} {contracts}: {error.Message}"));

            return new MarketOrderResult(
                false,
                "rejected",
                $"Tastytrade Sandbox market order failed: {error.Message}",
                null,
                null);
        }
    }

    public async Task<BrokerActionResult> CancelWorkingOrdersAsync(string? symbol, TradingDbContext db)
    {
        var brokerSettings = settingsStore.Get();
        var settings = settingsStore.GetActiveTastytradeSettings();
        if (!brokerSettings.TastytradeEnvironment.Equals("Sandbox", StringComparison.OrdinalIgnoreCase))
        {
            db.AuditLogs.Add(AuditLogRecord.BrokerAction(
                "tastytrade.cancel_orders_blocked",
                "Tastytrade live order cancellation is not enabled"));
            return new BrokerActionResult(0, 0);
        }

        if (!settings.Enabled || settings.ReadOnly)
        {
            db.AuditLogs.Add(AuditLogRecord.BrokerAction(
                "tastytrade.cancel_orders_blocked",
                settings.ReadOnly ? "Tastytrade is read-only" : "Tastytrade Sandbox is disabled"));
            return new BrokerActionResult(0, 0);
        }

        var normalizedSymbol = symbol?.Trim().ToUpperInvariant();
        var liveOrders = await accountClient.GetLiveOrdersAsync();
        var candidates = liveOrders
            .Where(order => string.IsNullOrWhiteSpace(normalizedSymbol) || order.Symbol.Equals(normalizedSymbol, StringComparison.OrdinalIgnoreCase))
            .Where(order => !string.IsNullOrWhiteSpace(order.BrokerOrderId))
            .ToArray();

        var cancelled = 0;
        foreach (var order in candidates)
        {
            try
            {
                await orderClient.CancelOrderAsync(order.BrokerOrderId!);
                cancelled++;
            }
            catch (Exception error)
            {
                db.AuditLogs.Add(AuditLogRecord.BrokerAction(
                    "tastytrade.cancel_order_failed",
                    $"Failed to cancel Tastytrade order {order.BrokerOrderId} {order.Symbol}: {error.Message}"));
            }
        }

        db.AuditLogs.Add(AuditLogRecord.BrokerAction(
            "tastytrade.cancel_orders_completed",
            $"Tastytrade cancel working orders completed: requested={candidates.Length}, cancelled={cancelled}, symbol={normalizedSymbol ?? "all"}"));
        return new BrokerActionResult(cancelled, 0);
    }

    public async Task<BrokerActionResult> ClosePositionAsync(string symbol, TradingDbContext db)
    {
        var normalizedSymbol = symbol.Trim().ToUpperInvariant();
        var brokerSettings = settingsStore.Get();
        var settings = settingsStore.GetActiveTastytradeSettings();
        if (!brokerSettings.TastytradeEnvironment.Equals("Sandbox", StringComparison.OrdinalIgnoreCase))
        {
            db.AuditLogs.Add(AuditLogRecord.BrokerAction(
                "tastytrade.close_position_blocked",
                $"Tastytrade live close position is not enabled for {normalizedSymbol}"));
            return new BrokerActionResult(0, 0);
        }

        if (!settings.Enabled || settings.ReadOnly)
        {
            db.AuditLogs.Add(AuditLogRecord.BrokerAction(
                "tastytrade.close_position_blocked",
                settings.ReadOnly ? $"Tastytrade is read-only for {normalizedSymbol}" : $"Tastytrade Sandbox is disabled for {normalizedSymbol}"));
            return new BrokerActionResult(0, 0);
        }

        var position = (await accountClient.GetPositionsAsync())
            .FirstOrDefault(item => IsSamePositionSymbol(item.Symbol, normalizedSymbol));
        if (position is null)
        {
            db.AuditLogs.Add(AuditLogRecord.BrokerAction(
                "tastytrade.close_position_skipped",
                $"No Tastytrade open position found for {normalizedSymbol}"));
            return new BrokerActionResult(0, 0);
        }

        await CancelWorkingOrdersAsync(normalizedSymbol, db);
        var submitted = await orderClient.SubmitClosingMarketOrderAsync(position);
        db.Orders.Add(new OrderRecord
        {
            BrokerOrderId = submitted.OrderId,
            Symbol = normalizedSymbol,
            Direction = position.Direction.Equals("LONG", StringComparison.OrdinalIgnoreCase) ? "SHORT" : "LONG",
            OrderType = "tastytrade_close_market",
            Quantity = position.Quantity,
            Status = MapOrderStatus(submitted.Status),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        db.AuditLogs.Add(AuditLogRecord.BrokerAction(
            "tastytrade.close_position_submitted",
            $"Tastytrade close position submitted for {normalizedSymbol}: order={submitted.OrderId}, qty={position.Quantity}, status={submitted.Status}"));

        return new BrokerActionResult(0, 1);
    }

    public async Task<BrokerActionResult> FlattenAsync(TradingDbContext db)
    {
        var brokerSettings = settingsStore.Get();
        var settings = settingsStore.GetActiveTastytradeSettings();
        if (!brokerSettings.TastytradeEnvironment.Equals("Sandbox", StringComparison.OrdinalIgnoreCase))
        {
            db.AuditLogs.Add(AuditLogRecord.BrokerAction(
                "tastytrade.flatten_blocked",
                "Tastytrade live flatten is not enabled"));
            return new BrokerActionResult(0, 0);
        }

        if (!settings.Enabled || settings.ReadOnly)
        {
            db.AuditLogs.Add(AuditLogRecord.BrokerAction(
                "tastytrade.flatten_blocked",
                settings.ReadOnly ? "Tastytrade is read-only" : "Tastytrade Sandbox is disabled"));
            return new BrokerActionResult(0, 0);
        }

        var cancelled = await CancelWorkingOrdersAsync(null, db);
        var positions = await accountClient.GetPositionsAsync();
        var closed = 0;
        foreach (var position in positions)
        {
            try
            {
                var submitted = await orderClient.SubmitClosingMarketOrderAsync(position);
                closed++;
                db.Orders.Add(new OrderRecord
                {
                    BrokerOrderId = submitted.OrderId,
                    Symbol = position.Symbol,
                    Direction = position.Direction.Equals("LONG", StringComparison.OrdinalIgnoreCase) ? "SHORT" : "LONG",
                    OrderType = "tastytrade_flatten_market",
                    Quantity = position.Quantity,
                    Status = MapOrderStatus(submitted.Status),
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
            catch (Exception error)
            {
                db.AuditLogs.Add(AuditLogRecord.BrokerAction(
                    "tastytrade.flatten_position_failed",
                    $"Failed to flatten Tastytrade position {position.Symbol}: {error.Message}"));
            }
        }

        db.AuditLogs.Add(AuditLogRecord.BrokerAction(
            "tastytrade.flatten_completed",
            $"Tastytrade flatten completed: closedPositions={closed}, cancelledOrders={cancelled.CancelledOrders}"));
        return new BrokerActionResult(cancelled.CancelledOrders, closed);
    }

    public Task<ProtectionUpdateResult> UpdateProtectionAsync(string symbol, decimal? stopLoss, decimal? takeProfit, TradingDbContext db)
    {
        return Task.FromResult(new ProtectionUpdateResult(
            false,
            "not_implemented",
            "Tastytrade protection updates are not implemented yet",
            null));
    }

    private static void BlockSignal(TradingSignalRecord signal, TradingDbContext db, string reason)
    {
        signal.Status = "broker_blocked";
        db.AuditLogs.Add(AuditLogRecord.BrokerAction(
            "tastytrade.entry_blocked",
            $"Tastytrade blocked entry signal {signal.Id} {signal.Symbol} {signal.Direction} {signal.Contracts}: {reason}"));
    }

    private static MarketOrderResult BlockMarketOrder(TradingDbContext db, string symbol, string direction, int contracts, string reason)
    {
        db.AuditLogs.Add(AuditLogRecord.BrokerAction(
            "tastytrade.market_order_blocked",
            $"Tastytrade market order blocked for {symbol} {direction} {contracts}: {reason}"));

        return new MarketOrderResult(false, "blocked", reason, null, null);
    }

    private static (decimal? StopLoss, decimal? TakeProfit) ResolveProtectionLevels(
        string direction,
        decimal? referencePrice,
        bool attachProtection,
        decimal? protectionDistance,
        decimal? stopLoss,
        decimal? takeProfit)
    {
        if (!attachProtection)
        {
            return (null, null);
        }

        if (stopLoss is not null && takeProfit is not null)
        {
            return (stopLoss, takeProfit);
        }

        if (referencePrice is null)
        {
            throw new InvalidOperationException("Reference price is required to calculate Tastytrade TP/SL");
        }

        var distance = protectionDistance is > 0 ? protectionDistance.Value : 100m;
        return direction == "LONG"
            ? (referencePrice.Value - distance, referencePrice.Value + distance)
            : (referencePrice.Value + distance, referencePrice.Value - distance);
    }

    private async Task<PositionRecord?> WaitForPositionAsync(string displaySymbol, string routedSymbol, string direction, int minQuantity)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var positions = await accountClient.GetPositionsAsync();
            var match = positions.FirstOrDefault(position =>
                (position.Symbol.Equals(displaySymbol, StringComparison.OrdinalIgnoreCase)
                    || position.Symbol.Equals(routedSymbol, StringComparison.OrdinalIgnoreCase))
                && position.Direction.Equals(direction, StringComparison.OrdinalIgnoreCase)
                && position.Quantity >= minQuantity);

            if (match is not null)
            {
                return match;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        return null;
    }

    private static async Task<PositionRecord> UpsertLocalPositionAsync(string symbol, string direction, int quantity, decimal averagePrice, TradingDbContext db, DateTimeOffset now)
    {
        var existing = await db.Positions.SingleOrDefaultAsync(position => position.Symbol == symbol);
        if (existing is null)
        {
            var created = new PositionRecord
            {
                Symbol = symbol,
                Direction = direction,
                Quantity = quantity,
                AveragePrice = averagePrice,
                IsManaged = true,
                OpenedAt = now,
                UpdatedAt = now
            };
            db.Positions.Add(created);
            return created;
        }

        if (existing.Direction.Equals(direction, StringComparison.OrdinalIgnoreCase))
        {
            var totalQuantity = existing.Quantity + quantity;
            existing.AveragePrice = totalQuantity > 0
                ? ((existing.AveragePrice * existing.Quantity) + (averagePrice * quantity)) / totalQuantity
                : averagePrice;
            existing.Quantity = totalQuantity;
        }
        else
        {
            existing.Direction = direction;
            existing.Quantity = quantity;
            existing.AveragePrice = averagePrice;
            existing.OpenedAt = now;
        }

        existing.IsManaged = true;
        existing.UpdatedAt = now;
        return existing;
    }

    private static bool IsSamePositionSymbol(string left, string right)
    {
        return ToSymbolKey(left).Equals(ToSymbolKey(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string ToSymbolKey(string symbol)
    {
        var normalized = (symbol ?? "").Trim().ToUpperInvariant().TrimStart('/');
        if (normalized.EndsWith("1!", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^2];
        }

        foreach (var root in new[] { "MNQ", "MES", "NQ", "ES" })
        {
            if (normalized == root)
            {
                return root;
            }

            if (normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && normalized.Length > root.Length + 1
                && "FGHJKMNQUVXZ".Contains(normalized[root.Length], StringComparison.Ordinal)
                && char.IsDigit(normalized[^1]))
            {
                return root;
            }
        }

        return normalized;
    }

    private static string JsonNumber(decimal? value)
    {
        return value is null
            ? "null"
            : value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string MapOrderStatus(string status)
    {
        if (status.Contains("reject", StringComparison.OrdinalIgnoreCase))
        {
            return "rejected";
        }

        if (status.Contains("cancel", StringComparison.OrdinalIgnoreCase))
        {
            return "cancelled";
        }

        if (status.Contains("fill", StringComparison.OrdinalIgnoreCase))
        {
            return "filled";
        }

        if (status.Contains("live", StringComparison.OrdinalIgnoreCase)
            || status.Contains("routed", StringComparison.OrdinalIgnoreCase)
            || status.Contains("received", StringComparison.OrdinalIgnoreCase)
            || status.Contains("submitted", StringComparison.OrdinalIgnoreCase))
        {
            return "working";
        }

        return string.IsNullOrWhiteSpace(status) ? "working" : status.Trim().ToLowerInvariant();
    }
}
