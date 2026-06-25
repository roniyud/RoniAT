using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace RoniAT.TradingEngine.Services;

public sealed class TastytradeOAuthStateStore
{
    private readonly ConcurrentDictionary<string, TastytradeOAuthState> states = new(StringComparer.Ordinal);

    public TastytradeOAuthState Create(string environment)
    {
        var state = new TastytradeOAuthState(
            Value: Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            Environment: environment,
            CreatedAt: DateTimeOffset.UtcNow);

        states[state.Value] = state;
        Cleanup();
        return state;
    }

    public bool TryTake(string value, out TastytradeOAuthState state)
    {
        Cleanup();
        return states.TryRemove(value, out state!);
    }

    private void Cleanup()
    {
        var expiresBefore = DateTimeOffset.UtcNow.AddMinutes(-10);
        foreach (var item in states)
        {
            if (item.Value.CreatedAt < expiresBefore)
            {
                states.TryRemove(item.Key, out _);
            }
        }
    }
}

public sealed record TastytradeOAuthState(string Value, string Environment, DateTimeOffset CreatedAt);
