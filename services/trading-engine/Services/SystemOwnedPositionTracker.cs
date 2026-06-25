namespace RoniAT.TradingEngine.Services;

public sealed class SystemOwnedPositionTracker
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(30);
    private readonly object gate = new();
    private readonly Dictionary<string, DateTimeOffset> recentlyOwnedUntil = new(StringComparer.OrdinalIgnoreCase);

    public void Mark(string broker, string symbol, TimeSpan? ttl = null)
    {
        var key = ToKey(broker, symbol);
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        lock (gate)
        {
            recentlyOwnedUntil[key] = DateTimeOffset.UtcNow.Add(ttl ?? DefaultTtl);
        }
    }

    public bool IsRecentlyOwned(string broker, string symbol)
    {
        var key = ToKey(broker, symbol);
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        lock (gate)
        {
            if (!recentlyOwnedUntil.TryGetValue(key, out var expiresAt))
            {
                return false;
            }

            if (expiresAt >= DateTimeOffset.UtcNow)
            {
                return true;
            }

            recentlyOwnedUntil.Remove(key);
            return false;
        }
    }

    private static string ToKey(string broker, string symbol)
    {
        var normalizedBroker = (broker ?? "").Trim().ToUpperInvariant();
        var normalizedSymbol = ToSymbolKey(symbol);
        return string.IsNullOrWhiteSpace(normalizedBroker) || string.IsNullOrWhiteSpace(normalizedSymbol)
            ? ""
            : $"{normalizedBroker}:{normalizedSymbol}";
    }

    private static string ToSymbolKey(string symbol)
    {
        var normalized = (symbol ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "";
        }

        if (normalized.Contains(':'))
        {
            normalized = normalized[(normalized.LastIndexOf(':') + 1)..];
        }

        normalized = normalized.TrimStart('/').Replace(" ", "", StringComparison.Ordinal);
        if (normalized.EndsWith("1!", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^2];
        }

        foreach (var root in new[] { "MNQ", "MES", "NQ", "ES" })
        {
            if (normalized.Equals(root, StringComparison.OrdinalIgnoreCase))
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
}
