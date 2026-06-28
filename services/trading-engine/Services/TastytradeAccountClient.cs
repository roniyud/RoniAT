using System.Net.Http.Headers;
using System.Net;
using System.Text.Json;
using RoniAT.TradingEngine.Contracts;
using RoniAT.TradingEngine.Models;

namespace RoniAT.TradingEngine.Services;

public sealed class TastytradeAccountClient(
    BrokerSettingsStore settingsStore,
    IHttpClientFactory httpClientFactory,
    TastytradeOAuthService oauthService)
{
    public async Task<IReadOnlyList<string>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        var json = await GetStringAsync("/customers/me/accounts", cancellationToken);
        var accounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(json);
        CollectStringsByProperty(document.RootElement, accounts, "account-number", "account_number", "accountNumber");
        return accounts.OrderBy(account => account, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<AccountBalanceResponse> GetBalanceAsync(CancellationToken cancellationToken = default)
    {
        var brokerSettings = settingsStore.Get();
        var settings = settingsStore.GetActiveTastytradeSettings();
        var account = RequireAccount(settings);
        var json = await GetStringAsync($"/accounts/{Uri.EscapeDataString(account)}/balances", cancellationToken);

        using var document = JsonDocument.Parse(json);
        return new AccountBalanceResponse(
            Mode: "Tastytrade",
            Environment: brokerSettings.TastytradeEnvironment,
            AccountNumber: ReadStringDeep(document.RootElement, "account-number", "account_number", "accountNumber") is { Length: > 0 } accountNumber
                ? accountNumber
                : account,
            Currency: ReadStringDeep(document.RootElement, "currency", "currency-code", "currency_code") is { Length: > 0 } currency
                ? currency
                : "USD",
            CashBalance: ReadNullableDecimalDeep(document.RootElement, "cash-balance", "cash_balance", "cashBalance"),
            NetLiquidatingValue: ReadNullableDecimalDeep(document.RootElement, "net-liquidating-value", "net_liquidating_value", "netLiquidatingValue", "net-liq", "net_liq"),
            EquityBuyingPower: ReadNullableDecimalDeep(document.RootElement, "equity-buying-power", "equity_buying_power", "equityBuyingPower"),
            DerivativeBuyingPower: ReadNullableDecimalDeep(document.RootElement, "derivative-buying-power", "derivative_buying_power", "derivativeBuyingPower"),
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<PositionRecord>> GetPositionsAsync(CancellationToken cancellationToken = default)
    {
        var settings = settingsStore.GetActiveTastytradeSettings();
        var account = RequireAccount(settings);
        var json = await GetStringAsync($"/accounts/{Uri.EscapeDataString(account)}/positions", cancellationToken);

        using var document = JsonDocument.Parse(json);
        var items = FindItemsArray(document.RootElement);
        var now = DateTimeOffset.UtcNow;
        var positions = new List<PositionRecord>();

        foreach (var item in items)
        {
            var symbol = ReadString(item, "symbol", "underlying-symbol", "underlying_symbol");
            var quantity = ReadDecimal(item, "quantity");
            if (string.IsNullOrWhiteSpace(symbol) || quantity == 0)
            {
                continue;
            }

            var quantityDirection = ReadString(item, "quantity-direction", "quantity_direction");
            var direction = quantity < 0 || quantityDirection.Equals("Short", StringComparison.OrdinalIgnoreCase)
                ? "SHORT"
                : "LONG";
            var absQuantity = Math.Max(1, (int)Math.Abs(quantity));

            positions.Add(new PositionRecord
            {
                Symbol = NormalizeSymbol(symbol),
                Direction = direction,
                Quantity = absQuantity,
                AveragePrice = ReadDecimal(item, "average-open-price", "average_open_price", "average-price", "average_price"),
                OpenedAt = ReadDateTime(item, now, "opened-at", "opened_at", "created-at", "created_at"),
                UpdatedAt = now
            });
        }

        return positions
            .OrderBy(position => position.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<OrderRecord>> GetLiveOrdersAsync(CancellationToken cancellationToken = default)
    {
        var settings = settingsStore.GetActiveTastytradeSettings();
        var account = RequireAccount(settings);
        var json = await GetStringAsync($"/accounts/{Uri.EscapeDataString(account)}/orders/live", cancellationToken);

        using var document = JsonDocument.Parse(json);
        var items = FindItemsArray(document.RootElement);
        var now = DateTimeOffset.UtcNow;
        var orders = new List<OrderRecord>();

        foreach (var item in items)
        {
            var orderId = ReadString(item, "id", "order-id", "order_id");
            var status = NormalizeOrderStatus(ReadString(item, "status"));
            var orderType = ReadString(item, "order-type", "order_type", "type");
            var price = ReadNullableDecimal(item, "price", "limit-price", "limit_price");
            var stopPrice = ReadNullableDecimal(item, "stop-trigger", "stop_trigger", "stop-price", "stop_price");
            var createdAt = ReadDateTime(item, now, "received-at", "received_at", "created-at", "created_at", "updated-at", "updated_at");

            foreach (var leg in FindLegs(item))
            {
                var symbol = ReadString(leg, "symbol");
                var action = ReadString(leg, "action");
                var quantity = ReadDecimal(leg, "quantity");
                if (string.IsNullOrWhiteSpace(symbol))
                {
                    continue;
                }

                orders.Add(new OrderRecord
                {
                    BrokerOrderId = orderId,
                    Symbol = NormalizeSymbol(symbol),
                    Direction = NormalizeOrderDirection(action),
                    OrderType = string.IsNullOrWhiteSpace(orderType) ? "unknown" : orderType,
                    Quantity = Math.Max(1, (int)Math.Abs(quantity)),
                    Price = price,
                    StopPrice = stopPrice,
                    Status = status,
                    CreatedAt = createdAt,
                    UpdatedAt = now
                });
            }
        }

        return orders
            .OrderByDescending(order => order.CreatedAt)
            .ToArray();
    }

    private async Task<string> GetStringAsync(string path, CancellationToken cancellationToken)
    {
        var settings = settingsStore.GetActiveTastytradeSettings();
        if (string.IsNullOrWhiteSpace(settings.AccessToken))
        {
            throw new InvalidOperationException("Tastytrade access token is required");
        }

        var baseUrl = settings.ApiBaseUrl.TrimEnd('/');
        var client = httpClientFactory.CreateClient("tastytrade");
        using var response = await SendGetAsync(client, $"{baseUrl}{path}", settings.AccessToken, includeVersionHeader: true, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var refresh = await oauthService.RefreshAccessTokenAsync(cancellationToken);
            if (!refresh.Ok)
            {
                throw new InvalidOperationException($"Tastytrade access token expired and refresh failed: {refresh.Message}");
            }

            var refreshedSettings = settingsStore.GetActiveTastytradeSettings();
            using var retryResponse = await SendGetAsync(client, $"{baseUrl}{path}", refreshedSettings.AccessToken, includeVersionHeader: true, cancellationToken);
            var retryBody = await retryResponse.Content.ReadAsStringAsync(cancellationToken);
            if (retryResponse.StatusCode == HttpStatusCode.NotAcceptable && retryBody.Contains("version", StringComparison.OrdinalIgnoreCase))
            {
                using var retryFallbackResponse = await SendGetAsync(client, $"{baseUrl}{path}", refreshedSettings.AccessToken, includeVersionHeader: false, cancellationToken);
                var retryFallbackBody = await retryFallbackResponse.Content.ReadAsStringAsync(cancellationToken);
                if (!retryFallbackResponse.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Tastytrade returned HTTP {(int)retryFallbackResponse.StatusCode}: {TrimForLog(retryFallbackBody)}");
                }

                return retryFallbackBody;
            }

            if (!retryResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Tastytrade returned HTTP {(int)retryResponse.StatusCode}: {TrimForLog(retryBody)}");
            }

            return retryBody;
        }

        if (response.StatusCode == HttpStatusCode.NotAcceptable && body.Contains("version", StringComparison.OrdinalIgnoreCase))
        {
            using var fallbackResponse = await SendGetAsync(client, $"{baseUrl}{path}", settings.AccessToken, includeVersionHeader: false, cancellationToken);
            var fallbackBody = await fallbackResponse.Content.ReadAsStringAsync(cancellationToken);
            if (!fallbackResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Tastytrade returned HTTP {(int)fallbackResponse.StatusCode}: {TrimForLog(fallbackBody)}");
            }

            return fallbackBody;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Tastytrade returned HTTP {(int)response.StatusCode}: {TrimForLog(body)}");
        }

        return body;
    }

    private static Task<HttpResponseMessage> SendGetAsync(HttpClient client, string url, string accessToken, bool includeVersionHeader, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (includeVersionHeader)
        {
            request.Headers.TryAddWithoutValidation("Accept-Version", "20240501");
        }

        return client.SendAsync(request, cancellationToken);
    }

    private static string RequireAccount(TastytradeSettings settings)
    {
        return string.IsNullOrWhiteSpace(settings.AccountNumber)
            ? throw new InvalidOperationException("Tastytrade account number is required")
            : settings.AccountNumber.Trim();
    }

    private static IReadOnlyList<JsonElement> FindItemsArray(JsonElement root)
    {
        if (TryFindProperty(root, out var items, "items") && items.ValueKind == JsonValueKind.Array)
        {
            return items.EnumerateArray().ToArray();
        }

        if (TryFindProperty(root, out var data, "data") && data.ValueKind == JsonValueKind.Array)
        {
            return data.EnumerateArray().ToArray();
        }

        return root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToArray()
            : [];
    }

    private static IReadOnlyList<JsonElement> FindLegs(JsonElement order)
    {
        if (TryFindProperty(order, out var legs, "legs") && legs.ValueKind == JsonValueKind.Array)
        {
            return legs.EnumerateArray().ToArray();
        }

        return [order];
    }

    private static void CollectStringsByProperty(JsonElement element, HashSet<string> values, params string[] propertyNames)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (propertyNames.Any(name => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        && property.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = property.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            values.Add(value.Trim());
                        }
                    }

                    CollectStringsByProperty(property.Value, values, propertyNames);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectStringsByProperty(item, values, propertyNames);
                }
                break;
        }
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
            }
        }

        value = default;
        return false;
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

    private static string ReadStringDeep(JsonElement element, params string[] names)
    {
        if (TryFindPropertyDeep(element, out var value, names))
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Number => value.GetRawText(),
                _ => ""
            };
        }

        return "";
    }

    private static decimal ReadDecimal(JsonElement element, params string[] names)
    {
        return ReadNullableDecimal(element, names) ?? 0m;
    }

    private static decimal? ReadNullableDecimal(JsonElement element, params string[] names)
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

    private static decimal? ReadNullableDecimalDeep(JsonElement element, params string[] names)
    {
        if (!TryFindPropertyDeep(element, out var value, names))
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

    private static bool TryFindPropertyDeep(JsonElement element, out JsonElement value, params string[] names)
    {
        if (TryFindProperty(element, out value, names))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (TryFindPropertyDeep(property.Value, out value, names))
                {
                    return true;
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindPropertyDeep(item, out value, names))
                {
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static DateTimeOffset ReadDateTime(JsonElement element, DateTimeOffset fallback, params string[] names)
    {
        var value = ReadString(element, names);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static string NormalizeSymbol(string symbol)
    {
        return symbol.Trim().ToUpperInvariant();
    }

    private static string NormalizeOrderDirection(string action)
    {
        return action.Contains("sell", StringComparison.OrdinalIgnoreCase) ? "SHORT" : "LONG";
    }

    private static string NormalizeOrderStatus(string status)
    {
        if (status.Contains("live", StringComparison.OrdinalIgnoreCase)
            || status.Contains("received", StringComparison.OrdinalIgnoreCase)
            || status.Contains("routed", StringComparison.OrdinalIgnoreCase))
        {
            return "working";
        }

        if (status.Contains("cancel", StringComparison.OrdinalIgnoreCase))
        {
            return "cancelled";
        }

        if (status.Contains("fill", StringComparison.OrdinalIgnoreCase))
        {
            return "filled";
        }

        return string.IsNullOrWhiteSpace(status) ? "working" : status.Trim().ToLowerInvariant();
    }

    private static string TrimForLog(string value)
    {
        return value.Length <= 400 ? value : $"{value[..400]}...";
    }
}
