using RoniAT.TradingEngine.Data;
using RoniAT.TradingEngine.Models;

namespace RoniAT.TradingEngine.Services;

public sealed class BrokerRouterAdapter(
    BrokerSettingsStore settingsStore,
    PaperBrokerAdapter paperBrokerAdapter,
    IBKRBrokerAdapter ibkrBrokerAdapter) : IBrokerAdapter
{
    public string Name => ActiveAdapter.Name;

    public BrokerStatus GetStatus()
    {
        return ActiveAdapter.GetStatus();
    }

    public Task ApplyEntrySignalAsync(TradingSignalRecord signal, TradingDbContext db)
    {
        return ActiveAdapter.ApplyEntrySignalAsync(signal, db);
    }

    public Task<MarketOrderResult> PlaceMarketOrderAsync(string symbol, string direction, int contracts, decimal? referencePrice, TradingDbContext db, bool attachProtection = false, decimal? protectionDistance = null)
    {
        return ActiveAdapter.PlaceMarketOrderAsync(symbol, direction, contracts, referencePrice, db, attachProtection, protectionDistance);
    }

    public Task<BrokerActionResult> CancelWorkingOrdersAsync(string? symbol, TradingDbContext db)
    {
        return ActiveAdapter.CancelWorkingOrdersAsync(symbol, db);
    }

    public Task<BrokerActionResult> ClosePositionAsync(string symbol, TradingDbContext db)
    {
        return ActiveAdapter.ClosePositionAsync(symbol, db);
    }

    public Task<BrokerActionResult> FlattenAsync(TradingDbContext db)
    {
        return ActiveAdapter.FlattenAsync(db);
    }

    public Task<ProtectionUpdateResult> UpdateProtectionAsync(string symbol, decimal? stopLoss, decimal? takeProfit, TradingDbContext db)
    {
        return ActiveAdapter.UpdateProtectionAsync(symbol, stopLoss, takeProfit, db);
    }

    private IBrokerAdapter ActiveAdapter
    {
        get
        {
            var settings = settingsStore.Get();
            return settings.Mode.Equals("IBKR", StringComparison.OrdinalIgnoreCase)
                ? ibkrBrokerAdapter
                : paperBrokerAdapter;
        }
    }
}
