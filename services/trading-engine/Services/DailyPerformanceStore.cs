using System.Text.Json;

namespace RoniAT.TradingEngine.Services;

public sealed class DailyPerformanceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object syncRoot = new();
    private readonly string filePath;

    public DailyPerformanceStore(IWebHostEnvironment environment)
    {
        filePath = Path.Combine(environment.ContentRootPath, "storage", "daily-performance.json");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
    }

    public DailyPerformanceSnapshot GetToday()
    {
        var date = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        lock (syncRoot)
        {
            var state = Load();
            return state.TryGetValue(date.ToString("yyyy-MM-dd"), out var snapshot)
                ? snapshot
                : new DailyPerformanceSnapshot(date, 0m, 0);
        }
    }

    public DailyPerformanceSnapshot AddRealizedPnl(decimal realizedPnl)
    {
        var date = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        lock (syncRoot)
        {
            var state = Load();
            var key = date.ToString("yyyy-MM-dd");
            var current = state.TryGetValue(key, out var existing)
                ? existing
                : new DailyPerformanceSnapshot(date, 0m, 0);

            var next = current with
            {
                RealizedPnl = current.RealizedPnl + realizedPnl,
                ClosedTrades = current.ClosedTrades + 1
            };

            state[key] = next;
            File.WriteAllText(filePath, JsonSerializer.Serialize(state, JsonOptions));
            return next;
        }
    }

    private Dictionary<string, DailyPerformanceSnapshot> Load()
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, DailyPerformanceSnapshot>>(File.ReadAllText(filePath), JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

public sealed record DailyPerformanceSnapshot(DateOnly Date, decimal RealizedPnl, int ClosedTrades);
