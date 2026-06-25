namespace RoniAT.TradingEngine.Services;

public sealed class TastytradeTokenRefreshService(
    BrokerSettingsStore settingsStore,
    IServiceScopeFactory scopeFactory,
    ILogger<TastytradeTokenRefreshService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RefreshBeforeExpiry = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshIfNeededAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "Tastytrade automatic token refresh check failed");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task RefreshIfNeededAsync(CancellationToken cancellationToken)
    {
        var brokerSettings = settingsStore.Get();
        if (!brokerSettings.Mode.Equals("Tastytrade", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var settings = brokerSettings.TastytradeEnvironment.Equals("Live", StringComparison.OrdinalIgnoreCase)
            ? brokerSettings.TastytradeLive
            : brokerSettings.TastytradeSandbox;

        if (!settings.Enabled
            || string.IsNullOrWhiteSpace(settings.RefreshToken)
            || string.IsNullOrWhiteSpace(settings.ClientSecret))
        {
            return;
        }

        var refreshAt = settings.AccessTokenExpiresAt?.Subtract(RefreshBeforeExpiry);
        var shouldRefresh = string.IsNullOrWhiteSpace(settings.AccessToken)
            || refreshAt is null
            || refreshAt <= DateTimeOffset.UtcNow;

        if (!shouldRefresh)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var oauthService = scope.ServiceProvider.GetRequiredService<TastytradeOAuthService>();
        var result = await oauthService.RefreshAccessTokenAsync(cancellationToken);
        if (result.Ok)
        {
            logger.LogInformation(
                "Tastytrade {Environment} access token refreshed automatically; expires at {ExpiresAt}",
                result.Environment,
                result.AccessTokenExpiresAt);
        }
        else
        {
            logger.LogWarning("Tastytrade automatic token refresh failed: {Message}", result.Message);
        }
    }
}
