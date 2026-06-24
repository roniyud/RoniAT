using RoniAT.TradingEngine.Data;
using RoniAT.TradingEngine.Models;

namespace RoniAT.TradingEngine.Services;

public interface IBrokerAdapter
{
    string Name { get; }

    BrokerStatus GetStatus();

    Task ApplyEntrySignalAsync(TradingSignalRecord signal, TradingDbContext db);

    Task<MarketOrderResult> PlaceMarketOrderAsync(string symbol, string direction, int contracts, decimal? referencePrice, TradingDbContext db, bool attachProtection = false, decimal? protectionDistance = null);

    Task<BrokerActionResult> CancelWorkingOrdersAsync(string? symbol, TradingDbContext db);

    Task<BrokerActionResult> ClosePositionAsync(string symbol, TradingDbContext db);

    Task<BrokerActionResult> FlattenAsync(TradingDbContext db);
}

public sealed record BrokerActionResult(int CancelledOrders, int ClosedPositions);

public sealed record MarketOrderResult(
    bool Ok,
    string Status,
    string Message,
    OrderRecord? Order,
    PositionRecord? Position
);

public sealed record BrokerStatus(
    string Mode,
    string Environment,
    bool Configured,
    bool Enabled,
    bool Connected,
    bool ReadOnly,
    string Message
);
