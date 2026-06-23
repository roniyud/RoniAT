using RoniAT.TradingEngine.Data;
using RoniAT.TradingEngine.Models;

namespace RoniAT.TradingEngine.Services;

public interface IBrokerAdapter
{
    string Name { get; }

    Task ApplyEntrySignalAsync(TradingSignalRecord signal, TradingDbContext db);

    Task<BrokerActionResult> CancelWorkingOrdersAsync(string? symbol, TradingDbContext db);

    Task<BrokerActionResult> ClosePositionAsync(string symbol, TradingDbContext db);

    Task<BrokerActionResult> FlattenAsync(TradingDbContext db);
}

public sealed record BrokerActionResult(int CancelledOrders, int ClosedPositions);
