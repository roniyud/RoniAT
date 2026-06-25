using System.Net.Http.Headers;
using System.Text.Json;

namespace RoniAT.TradingEngine.Services;

public sealed class TastytradeConnectionTester(
    BrokerSettingsStore settingsStore,
    BrokerConnectionStateStore stateStore,
    IHttpClientFactory httpClientFactory,
    TastytradeOAuthService oauthService,
    ILogger<TastytradeConnectionTester> logger)
{
    public async Task<BrokerConnectionTestResult> TestAsync(CancellationToken cancellationToken = default)
    {
        var brokerSettings = settingsStore.Get();
        var settings = settingsStore.GetActiveTastytradeSettings();
        var testedAt = DateTimeOffset.UtcNow;
        var baseUrl = settings.ApiBaseUrl.TrimEnd('/');
        var selectedAccount = string.IsNullOrWhiteSpace(settings.AccountNumber) ? null : settings.AccountNumber.Trim();

        if (!brokerSettings.Mode.Equals("Tastytrade", StringComparison.OrdinalIgnoreCase))
        {
            return Save(CreateFailure(brokerSettings, settings, "Broker mode is not Tastytrade", testedAt));
        }

        if (!settings.Enabled)
        {
            return Save(CreateFailure(brokerSettings, settings, $"Tastytrade {brokerSettings.TastytradeEnvironment} is disabled", testedAt));
        }

        if (string.IsNullOrWhiteSpace(settings.AccessToken))
        {
            return Save(CreateFailure(brokerSettings, settings, "Tastytrade access token is required for connection test", testedAt));
        }

        if (string.IsNullOrWhiteSpace(selectedAccount))
        {
            return Save(CreateFailure(brokerSettings, settings, "Tastytrade account number is required for connection test", testedAt));
        }

        try
        {
            var client = httpClientFactory.CreateClient("tastytrade");
            using var response = await SendAccountsRequestAsync(client, baseUrl, settings.AccessToken, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                var refresh = await oauthService.RefreshAccessTokenAsync(cancellationToken);
                if (!refresh.Ok)
                {
                    return Save(CreateFailure(brokerSettings, settings, $"Tastytrade access token expired and refresh failed: {refresh.Message}", testedAt));
                }

                settings = settingsStore.GetActiveTastytradeSettings();
                using var retryResponse = await SendAccountsRequestAsync(client, baseUrl, settings.AccessToken, cancellationToken);
                body = await retryResponse.Content.ReadAsStringAsync(cancellationToken);
                if (!retryResponse.IsSuccessStatusCode)
                {
                    var retryMessage = string.IsNullOrWhiteSpace(body)
                        ? $"Tastytrade returned HTTP {(int)retryResponse.StatusCode}"
                        : $"Tastytrade returned HTTP {(int)retryResponse.StatusCode}: {TrimForLog(body)}";
                    return Save(CreateFailure(brokerSettings, settings, retryMessage, testedAt));
                }

                return Save(CreateSuccessResult(brokerSettings, settings, body, selectedAccount, baseUrl, testedAt));
            }

            if (!response.IsSuccessStatusCode)
            {
                var message = string.IsNullOrWhiteSpace(body)
                    ? $"Tastytrade returned HTTP {(int)response.StatusCode}"
                    : $"Tastytrade returned HTTP {(int)response.StatusCode}: {TrimForLog(body)}";
                return Save(CreateFailure(brokerSettings, settings, message, testedAt));
            }

            return Save(CreateSuccessResult(brokerSettings, settings, body, selectedAccount, baseUrl, testedAt));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "Tastytrade connection test failed");
            return Save(CreateFailure(brokerSettings, settings, $"Tastytrade connection test failed: {error.Message}", testedAt));
        }
    }

    private BrokerConnectionTestResult Save(BrokerConnectionTestResult result)
    {
        stateStore.SetLastResult(result);
        return result;
    }

    private static async Task<HttpResponseMessage> SendAccountsRequestAsync(HttpClient client, string baseUrl, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/customers/me/accounts");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("Accept-Version", "20240501");
        return await client.SendAsync(request, cancellationToken);
    }

    private static BrokerConnectionTestResult CreateSuccessResult(BrokerSettings brokerSettings, TastytradeSettings settings, string body, string? selectedAccount, string baseUrl, DateTimeOffset testedAt)
    {
        var accounts = ExtractAccountNumbers(body);
        var accountVerified = !string.IsNullOrWhiteSpace(selectedAccount)
            && accounts.Any(account => account.Equals(selectedAccount, StringComparison.OrdinalIgnoreCase));
        var accountList = accounts.Count == 0 ? "none" : string.Join(", ", accounts);
        return new BrokerConnectionTestResult(
            Ok: accountVerified,
            Mode: "Tastytrade",
            Environment: brokerSettings.TastytradeEnvironment,
            Host: baseUrl,
            Port: 443,
            HandshakeOk: true,
            AccountVerified: accountVerified,
            ManagedAccounts: accounts,
            SelectedAccount: selectedAccount,
            ServerVersion: null,
            Message: accountVerified
                ? $"Tastytrade {brokerSettings.TastytradeEnvironment} access token is valid and account {selectedAccount} was verified"
                : $"Tastytrade {brokerSettings.TastytradeEnvironment} access token is valid, but account {selectedAccount} was not found. Accounts: {accountList}",
            TestedAt: testedAt);
    }

    private static BrokerConnectionTestResult CreateFailure(BrokerSettings brokerSettings, TastytradeSettings settings, string message, DateTimeOffset testedAt)
    {
        return new BrokerConnectionTestResult(
            Ok: false,
            Mode: "Tastytrade",
            Environment: brokerSettings.TastytradeEnvironment,
            Host: settings.ApiBaseUrl,
            Port: 443,
            HandshakeOk: false,
            AccountVerified: false,
            ManagedAccounts: [],
            SelectedAccount: string.IsNullOrWhiteSpace(settings.AccountNumber) ? null : settings.AccountNumber.Trim(),
            ServerVersion: null,
            Message: message,
            TestedAt: testedAt);
    }

    private static IReadOnlyList<string> ExtractAccountNumbers(string json)
    {
        using var document = JsonDocument.Parse(json);
        var accounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectAccountNumbers(document.RootElement, accounts);
        return accounts.OrderBy(account => account, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void CollectAccountNumbers(JsonElement element, HashSet<string> accounts)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (IsAccountNumberProperty(property.Name) && property.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = property.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            accounts.Add(value.Trim());
                        }
                    }

                    CollectAccountNumbers(property.Value, accounts);
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectAccountNumbers(item, accounts);
                }
                break;
        }
    }

    private static bool IsAccountNumberProperty(string name)
    {
        return name.Equals("account-number", StringComparison.OrdinalIgnoreCase)
            || name.Equals("account_number", StringComparison.OrdinalIgnoreCase)
            || name.Equals("accountNumber", StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimForLog(string value)
    {
        return value.Length <= 400 ? value : $"{value[..400]}...";
    }
}
