using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RoniAT.TradingEngine.Models;

namespace RoniAT.TradingEngine.Services;

public sealed class TastytradeOrderClient(
    BrokerSettingsStore settingsStore,
    IHttpClientFactory httpClientFactory,
    TastytradeOAuthService oauthService)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<TastytradeSubmittedOrder> SubmitMarketOrderAsync(
        string symbol,
        string direction,
        int quantity,
        bool dryRunOnly = false,
        CancellationToken cancellationToken = default)
    {
        var settings = ValidateOrderPlacementSettings(quantity);

        var request = BuildMarketOrderRequest(symbol, direction, quantity);
        await EnsureAccountCanTradeInstrumentAsync(settings, request.InstrumentType, cancellationToken);
        var dryRun = await PostOrderAsync(settings, request, dryRun: true, cancellationToken);
        if (!dryRun.Ok || dryRun.Status.Equals("rejected", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Tastytrade dry-run rejected the order: {dryRun.Message}");
        }

        if (dryRunOnly)
        {
            return dryRun with { DryRun = true };
        }

        var submitted = await PostOrderAsync(settingsStore.GetActiveTastytradeSettings(), request, dryRun: false, cancellationToken);
        return submitted with { DryRun = false };
    }

    public async Task<TastytradeSubmittedOrder> SubmitOtocoMarketOrderAsync(
        string symbol,
        string direction,
        int quantity,
        decimal stopLoss,
        decimal takeProfit,
        bool dryRunOnly = false,
        CancellationToken cancellationToken = default)
    {
        var settings = ValidateOrderPlacementSettings(quantity);
        var request = BuildOtocoMarketOrderRequest(symbol, direction, quantity, stopLoss, takeProfit);
        await EnsureAccountCanTradeInstrumentAsync(settings, request.InstrumentType, cancellationToken);
        var dryRun = await PostComplexOrderAsync(settings, request, dryRun: true, cancellationToken);
        if (!dryRun.Ok || dryRun.Status.Equals("rejected", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Tastytrade dry-run rejected the bracket order: {dryRun.Message}");
        }

        if (dryRunOnly)
        {
            return dryRun with { DryRun = true };
        }

        var submitted = await PostComplexOrderAsync(settingsStore.GetActiveTastytradeSettings(), request, dryRun: false, cancellationToken);
        return submitted with { DryRun = false };
    }

    public async Task<TastytradeSubmittedOrder> SubmitClosingMarketOrderAsync(
        PositionRecord position,
        bool dryRunOnly = false,
        CancellationToken cancellationToken = default)
    {
        var closingDirection = position.Direction.Equals("LONG", StringComparison.OrdinalIgnoreCase)
            ? "SHORT"
            : "LONG";
        var request = BuildMarketOrderRequest(position.Symbol, closingDirection, position.Quantity, closing: true);
        var settings = settingsStore.GetActiveTastytradeSettings();
        await EnsureAccountCanTradeInstrumentAsync(settings, request.InstrumentType, cancellationToken);
        var dryRun = await PostOrderAsync(settings, request, dryRun: true, cancellationToken);
        if (!dryRun.Ok || dryRun.Status.Equals("rejected", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Tastytrade dry-run rejected the close order: {dryRun.Message}");
        }

        if (dryRunOnly)
        {
            return dryRun with { DryRun = true };
        }

        var submitted = await PostOrderAsync(settingsStore.GetActiveTastytradeSettings(), request, dryRun: false, cancellationToken);
        return submitted with { DryRun = false };
    }

    public async Task<TastytradeSubmittedOrder> SubmitOcoProtectionAsync(
        PositionRecord position,
        decimal stopLoss,
        decimal takeProfit,
        bool dryRunOnly = false,
        CancellationToken cancellationToken = default)
    {
        var settings = ValidateOrderPlacementSettings(position.Quantity);
        var request = BuildOcoProtectionRequest(position.Symbol, position.Direction, position.Quantity, stopLoss, takeProfit);
        await EnsureAccountCanTradeInstrumentAsync(settings, request.InstrumentType, cancellationToken);
        var dryRun = await PostComplexOrderAsync(settings, request, dryRun: true, cancellationToken);
        if (!dryRun.Ok || dryRun.Status.Equals("rejected", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Tastytrade dry-run rejected the OCO protection: {dryRun.Message}");
        }

        if (dryRunOnly)
        {
            return dryRun with { DryRun = true };
        }

        var submitted = await PostComplexOrderAsync(settingsStore.GetActiveTastytradeSettings(), request, dryRun: false, cancellationToken);
        return submitted with { DryRun = false };
    }

    public async Task<TastytradeCancelResult> CancelOrderAsync(string orderId, CancellationToken cancellationToken = default)
    {
        var settings = settingsStore.GetActiveTastytradeSettings();
        if (string.IsNullOrWhiteSpace(settings.AccountNumber))
        {
            throw new InvalidOperationException("Tastytrade account number is required");
        }

        var baseUrl = settings.ApiBaseUrl.TrimEnd('/');
        var account = Uri.EscapeDataString(settings.AccountNumber.Trim());
        var url = $"{baseUrl}/accounts/{account}/orders/{Uri.EscapeDataString(orderId)}";
        using var response = await SendDeleteAsync(url, settings.AccessToken, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var refresh = await oauthService.RefreshAccessTokenAsync(cancellationToken);
            if (!refresh.Ok)
            {
                throw new InvalidOperationException($"Tastytrade access token expired and refresh failed: {refresh.Message}");
            }

            var refreshedSettings = settingsStore.GetActiveTastytradeSettings();
            using var retryResponse = await SendDeleteAsync(url, refreshedSettings.AccessToken, cancellationToken);
            var retryBody = await retryResponse.Content.ReadAsStringAsync(cancellationToken);
            if (!retryResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Tastytrade cancel returned HTTP {(int)retryResponse.StatusCode}: {TrimForLog(retryBody)}");
            }

            return new TastytradeCancelResult(orderId, true, retryBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Tastytrade cancel returned HTTP {(int)response.StatusCode}: {TrimForLog(body)}");
        }

        return new TastytradeCancelResult(orderId, true, body);
    }

    public static TastytradeOrderRequest BuildMarketOrderRequest(string symbol, string direction, int quantity, bool closing = false)
    {
        var normalizedDirection = direction.Trim().ToUpperInvariant();
        var instrument = ResolveInstrument(symbol);
        var action = (normalizedDirection, closing) switch
        {
            ("LONG", false) => "Buy to Open",
            ("SHORT", false) => "Sell to Open",
            ("LONG", true) => "Buy to Close",
            ("SHORT", true) => "Sell to Close",
            _ => throw new InvalidOperationException("Direction must be LONG or SHORT")
        };

        return new TastytradeOrderRequest(
            DisplaySymbol: symbol.Trim().ToUpperInvariant(),
            RoutedSymbol: instrument.Symbol,
            InstrumentType: instrument.InstrumentType,
            Direction: normalizedDirection,
            Payload: new Dictionary<string, object?>
            {
                ["time-in-force"] = "Day",
                ["order-type"] = "Market",
                ["price-effect"] = normalizedDirection == "LONG" ? "Debit" : "Credit",
                ["legs"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["instrument-type"] = instrument.InstrumentType,
                        ["symbol"] = instrument.Symbol,
                        ["quantity"] = quantity,
                        ["action"] = action
                    }
                }
            });
    }

    public static TastytradeOrderRequest BuildOtocoMarketOrderRequest(
        string symbol,
        string direction,
        int quantity,
        decimal stopLoss,
        decimal takeProfit)
    {
        var normalizedDirection = direction.Trim().ToUpperInvariant();
        if (normalizedDirection is not ("LONG" or "SHORT"))
        {
            throw new InvalidOperationException("Direction must be LONG or SHORT");
        }

        ValidateProtectionLevels(normalizedDirection, stopLoss, takeProfit);

        var instrument = ResolveInstrument(symbol);
        var openingAction = normalizedDirection == "LONG" ? "Buy to Open" : "Sell to Open";
        var closingAction = normalizedDirection == "LONG" ? "Sell to Close" : "Buy to Close";
        var closingPriceEffect = normalizedDirection == "LONG" ? "Credit" : "Debit";
        var leg = new Dictionary<string, object?>
        {
            ["instrument-type"] = instrument.InstrumentType,
            ["symbol"] = instrument.Symbol,
            ["quantity"] = quantity,
            ["action"] = closingAction
        };

        return new TastytradeOrderRequest(
            DisplaySymbol: symbol.Trim().ToUpperInvariant(),
            RoutedSymbol: instrument.Symbol,
            InstrumentType: instrument.InstrumentType,
            Direction: normalizedDirection,
            Payload: new Dictionary<string, object?>
            {
                ["type"] = "OTOCO",
                ["trigger-order"] = new Dictionary<string, object?>
                {
                    ["time-in-force"] = "Day",
                    ["order-type"] = "Market",
                    ["price-effect"] = normalizedDirection == "LONG" ? "Debit" : "Credit",
                    ["legs"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["instrument-type"] = instrument.InstrumentType,
                            ["symbol"] = instrument.Symbol,
                            ["quantity"] = quantity,
                            ["action"] = openingAction
                        }
                    }
                },
                ["orders"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["time-in-force"] = "GTC",
                        ["order-type"] = "Limit",
                        ["price"] = takeProfit,
                        ["price-effect"] = closingPriceEffect,
                        ["legs"] = new[] { new Dictionary<string, object?>(leg) },
                        ["advanced-instructions"] = StrictPositionEffectValidation()
                    },
                    new Dictionary<string, object?>
                    {
                        ["time-in-force"] = "GTC",
                        ["order-type"] = "Stop",
                        ["stop-trigger"] = stopLoss,
                        ["price-effect"] = closingPriceEffect,
                        ["legs"] = new[] { new Dictionary<string, object?>(leg) },
                        ["advanced-instructions"] = StrictPositionEffectValidation()
                    }
                }
            });
    }

    public static TastytradeOrderRequest BuildOcoProtectionRequest(
        string symbol,
        string positionDirection,
        int quantity,
        decimal stopLoss,
        decimal takeProfit)
    {
        var normalizedDirection = positionDirection.Trim().ToUpperInvariant();
        if (normalizedDirection is not ("LONG" or "SHORT"))
        {
            throw new InvalidOperationException("Position direction must be LONG or SHORT");
        }

        ValidateProtectionLevels(normalizedDirection, stopLoss, takeProfit);

        var instrument = ResolveInstrument(symbol);
        var closingAction = normalizedDirection == "LONG" ? "Sell to Close" : "Buy to Close";
        var closingPriceEffect = normalizedDirection == "LONG" ? "Credit" : "Debit";
        var leg = new Dictionary<string, object?>
        {
            ["instrument-type"] = instrument.InstrumentType,
            ["symbol"] = instrument.Symbol,
            ["quantity"] = quantity,
            ["action"] = closingAction
        };

        return new TastytradeOrderRequest(
            DisplaySymbol: symbol.Trim().ToUpperInvariant(),
            RoutedSymbol: instrument.Symbol,
            InstrumentType: instrument.InstrumentType,
            Direction: normalizedDirection,
            Payload: new Dictionary<string, object?>
            {
                ["type"] = "OCO",
                ["orders"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["time-in-force"] = "GTC",
                        ["order-type"] = "Limit",
                        ["price"] = takeProfit,
                        ["price-effect"] = closingPriceEffect,
                        ["legs"] = new[] { new Dictionary<string, object?>(leg) },
                        ["advanced-instructions"] = StrictPositionEffectValidation()
                    },
                    new Dictionary<string, object?>
                    {
                        ["time-in-force"] = "GTC",
                        ["order-type"] = "Stop",
                        ["stop-trigger"] = stopLoss,
                        ["price-effect"] = closingPriceEffect,
                        ["legs"] = new[] { new Dictionary<string, object?>(leg) },
                        ["advanced-instructions"] = StrictPositionEffectValidation()
                    }
                }
            });
    }

    private async Task<TastytradeSubmittedOrder> PostOrderAsync(
        TastytradeSettings settings,
        TastytradeOrderRequest orderRequest,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var baseUrl = settings.ApiBaseUrl.TrimEnd('/');
        var account = Uri.EscapeDataString(settings.AccountNumber.Trim());
        var path = dryRun
            ? $"/accounts/{account}/orders/dry-run"
            : $"/accounts/{account}/orders";
        var url = $"{baseUrl}{path}";

        var response = await SendPostAsync(url, settings.AccessToken, orderRequest.Payload, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var refresh = await oauthService.RefreshAccessTokenAsync(cancellationToken);
            if (!refresh.Ok)
            {
                throw new InvalidOperationException($"Tastytrade access token expired and refresh failed: {refresh.Message}");
            }

            var refreshedSettings = settingsStore.GetActiveTastytradeSettings();
            response = await SendPostAsync(url, refreshedSettings.AccessToken, orderRequest.Payload, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Tastytrade returned HTTP {(int)response.StatusCode}: {TrimForLog(body)}");
        }

        return ParseOrderResponse(body, orderRequest, dryRun);
    }

    private async Task EnsureAccountCanTradeInstrumentAsync(
        TastytradeSettings settings,
        string instrumentType,
        CancellationToken cancellationToken)
    {
        if (!instrumentType.Equals("Future", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var accountNumber = settings.AccountNumber.Trim();
        var body = await GetAccountsBodyAsync(settings, cancellationToken);
        using var document = JsonDocument.Parse(body);
        var account = FindAccount(document.RootElement, accountNumber);
        if (account is null)
        {
            throw new InvalidOperationException($"Tastytrade account {accountNumber} was not found in authorized accounts");
        }

        if (!ReadBool(account.Value, "is-futures-approved", "is_futures_approved"))
        {
            throw new InvalidOperationException($"Tastytrade account {accountNumber} is not futures approved. MNQ/NQ/MES/ES orders cannot be submitted until futures trading is enabled for this account.");
        }
    }

    private async Task<string> GetAccountsBodyAsync(TastytradeSettings settings, CancellationToken cancellationToken)
    {
        var baseUrl = settings.ApiBaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/customers/me/accounts";
        using var response = await SendGetAsync(url, settings.AccessToken, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var refresh = await oauthService.RefreshAccessTokenAsync(cancellationToken);
            if (!refresh.Ok)
            {
                throw new InvalidOperationException($"Tastytrade access token expired and refresh failed: {refresh.Message}");
            }

            var refreshedSettings = settingsStore.GetActiveTastytradeSettings();
            using var retryResponse = await SendGetAsync(url, refreshedSettings.AccessToken, cancellationToken);
            var retryBody = await retryResponse.Content.ReadAsStringAsync(cancellationToken);
            if (!retryResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Tastytrade accounts lookup returned HTTP {(int)retryResponse.StatusCode}: {TrimForLog(retryBody)}");
            }

            return retryBody;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Tastytrade accounts lookup returned HTTP {(int)response.StatusCode}: {TrimForLog(body)}");
        }

        return body;
    }

    private async Task<TastytradeSubmittedOrder> PostComplexOrderAsync(
        TastytradeSettings settings,
        TastytradeOrderRequest orderRequest,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var baseUrl = settings.ApiBaseUrl.TrimEnd('/');
        var account = Uri.EscapeDataString(settings.AccountNumber.Trim());
        var path = dryRun
            ? $"/accounts/{account}/complex-orders/dry-run"
            : $"/accounts/{account}/complex-orders";
        var url = $"{baseUrl}{path}";

        var response = await SendPostAsync(url, settings.AccessToken, orderRequest.Payload, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var refresh = await oauthService.RefreshAccessTokenAsync(cancellationToken);
            if (!refresh.Ok)
            {
                throw new InvalidOperationException($"Tastytrade access token expired and refresh failed: {refresh.Message}");
            }

            var refreshedSettings = settingsStore.GetActiveTastytradeSettings();
            response = await SendPostAsync(url, refreshedSettings.AccessToken, orderRequest.Payload, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Tastytrade complex order returned HTTP {(int)response.StatusCode}: {TrimForLog(body)}");
        }

        return ParseOrderResponse(body, orderRequest, dryRun);
    }

    private TastytradeSettings ValidateOrderPlacementSettings(int quantity)
    {
        var brokerSettings = settingsStore.Get();
        if (!brokerSettings.Mode.Equals("Tastytrade", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Broker mode is not Tastytrade");
        }

        if (!brokerSettings.TastytradeEnvironment.Equals("Sandbox", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Tastytrade live order placement is not enabled");
        }

        var settings = settingsStore.GetActiveTastytradeSettings();
        if (!settings.Enabled)
        {
            throw new InvalidOperationException("Tastytrade Sandbox is disabled");
        }

        if (settings.ReadOnly)
        {
            throw new InvalidOperationException("Tastytrade Sandbox is configured as read-only");
        }

        if (string.IsNullOrWhiteSpace(settings.AccountNumber))
        {
            throw new InvalidOperationException("Tastytrade account number is required");
        }

        if (string.IsNullOrWhiteSpace(settings.AccessToken))
        {
            throw new InvalidOperationException("Tastytrade access token is required");
        }

        if (quantity <= 0)
        {
            throw new InvalidOperationException("Quantity must be greater than zero");
        }

        return settings;
    }

    private async Task<HttpResponseMessage> SendPostAsync(string url, string accessToken, object payload, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("tastytrade");
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("Accept-Version", "20260427");
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        return await client.SendAsync(request, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendGetAsync(string url, string accessToken, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("tastytrade");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("Accept-Version", "20240501");
        return await client.SendAsync(request, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendDeleteAsync(string url, string accessToken, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("tastytrade");
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("Accept-Version", "20260427");
        return await client.SendAsync(request, cancellationToken);
    }

    private static TastytradeSubmittedOrder ParseOrderResponse(string json, TastytradeOrderRequest request, bool dryRun)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var id = ReadString(root, "id", "order-id", "order_id");
        var status = ReadString(root, "status", "order-status", "order_status");
        var message = ReadString(root, "message", "reason", "reject-reason", "reject_reason");
        var parsedStatus = string.IsNullOrWhiteSpace(status)
            ? dryRun ? "dry_run_accepted" : "submitted"
            : status.Trim();

        return new TastytradeSubmittedOrder(
            OrderId: string.IsNullOrWhiteSpace(id) ? $"TASTY-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}" : id,
            Symbol: request.DisplaySymbol,
            RoutedSymbol: request.RoutedSymbol,
            InstrumentType: request.InstrumentType,
            Direction: request.Direction,
            Quantity: ReadQuantity(root) ?? 0,
            Status: parsedStatus,
            Message: string.IsNullOrWhiteSpace(message) ? parsedStatus : message,
            RawJson: json,
            DryRun: dryRun,
            Ok: !parsedStatus.Contains("reject", StringComparison.OrdinalIgnoreCase));
    }

    private static decimal? ReadQuantity(JsonElement root)
    {
        if (!TryFindProperty(root, out var legs, "legs") || legs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var leg in legs.EnumerateArray())
        {
            var quantity = ReadDecimal(leg, "quantity");
            if (quantity is not null)
            {
                return quantity;
            }
        }

        return null;
    }

    private static TastytradeInstrument ResolveInstrument(string symbol)
    {
        var normalized = symbol.Trim().ToUpperInvariant();
        if (normalized.StartsWith('/'))
        {
            return new TastytradeInstrument(normalized, "Future");
        }

        var root = normalized.Replace("1!", "", StringComparison.OrdinalIgnoreCase);
        if (root is "MNQ" or "NQ" or "MES" or "ES")
        {
            return new TastytradeInstrument($"/{root}{GetActiveQuarterlyFuturesCode(DateTime.UtcNow)}", "Future");
        }

        return new TastytradeInstrument(normalized, "Equity");
    }

    private static string GetActiveQuarterlyFuturesCode(DateTime utcNow)
    {
        var year = utcNow.Year;
        var monthCodes = new (int Month, char Code)[] { (3, 'H'), (6, 'M'), (9, 'U'), (12, 'Z') };
        foreach (var (month, code) in monthCodes)
        {
            var rollDate = GetThirdFriday(year, month).Date;
            if (utcNow.Date <= rollDate)
            {
                return $"{code}{year % 10}";
            }
        }

        return $"H{(year + 1) % 10}";
    }

    private static DateTime GetThirdFriday(int year, int month)
    {
        var date = new DateTime(year, month, 1);
        while (date.DayOfWeek != DayOfWeek.Friday)
        {
            date = date.AddDays(1);
        }

        return date.AddDays(14);
    }

    private static bool TryFindProperty(JsonElement element, out JsonElement value, params string[] names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (names.Any(name => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    value = property.Value;
                    return true;
                }

                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                    && TryFindProperty(property.Value, out value, names))
                {
                    return true;
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindProperty(item, out value, names))
                {
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static JsonElement? FindAccount(JsonElement element, string accountNumber)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var currentAccountNumber = ReadString(element, "account-number", "account_number", "accountNumber");
            if (currentAccountNumber.Equals(accountNumber, StringComparison.OrdinalIgnoreCase))
            {
                return element;
            }

            foreach (var property in element.EnumerateObject())
            {
                var found = FindAccount(property.Value, accountNumber);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var found = FindAccount(item, accountNumber);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static string ReadString(JsonElement element, params string[] names)
    {
        return TryFindProperty(element, out var value, names)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Number => value.GetRawText(),
                _ => ""
            }
            : "";
    }

    private static decimal? ReadDecimal(JsonElement element, params string[] names)
    {
        if (!TryFindProperty(element, out var value, names))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var parsed))
        {
            return parsed;
        }

        if (value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), out parsed))
        {
            return parsed;
        }

        return null;
    }

    private static bool ReadBool(JsonElement element, params string[] names)
    {
        if (!TryFindProperty(element, out var value, names))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
            _ => false
        };
    }

    private static string TrimForLog(string value)
    {
        return value.Length <= 800 ? value : $"{value[..800]}...";
    }

    private static IReadOnlyDictionary<string, object?> StrictPositionEffectValidation()
    {
        return new Dictionary<string, object?>
        {
            ["strict-position-effect-validation"] = true
        };
    }

    private static void ValidateProtectionLevels(string direction, decimal stopLoss, decimal takeProfit)
    {
        if (stopLoss <= 0 || takeProfit <= 0)
        {
            throw new InvalidOperationException("Stop loss and take profit must be greater than zero");
        }

        if (direction == "LONG" && stopLoss >= takeProfit)
        {
            throw new InvalidOperationException("LONG bracket requires stop loss below take profit");
        }

        if (direction == "SHORT" && takeProfit >= stopLoss)
        {
            throw new InvalidOperationException("SHORT bracket requires take profit below stop loss");
        }
    }
}

public sealed record TastytradeOrderRequest(
    string DisplaySymbol,
    string RoutedSymbol,
    string InstrumentType,
    string Direction,
    IReadOnlyDictionary<string, object?> Payload);

public sealed record TastytradeSubmittedOrder(
    string OrderId,
    string Symbol,
    string RoutedSymbol,
    string InstrumentType,
    string Direction,
    decimal Quantity,
    string Status,
    string Message,
    string RawJson,
    bool DryRun,
    bool Ok);

public sealed record TastytradeInstrument(string Symbol, string InstrumentType);

public sealed record TastytradeCancelResult(string OrderId, bool Ok, string RawJson);
