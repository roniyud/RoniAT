using RoniAT.TradingEngine.Contracts;

namespace RoniAT.TradingEngine.Validation;

public static class TradingSignalValidator
{
    public static TradingSignalValidationResult Validate(TradingSignalRequest request)
    {
        var signal = Normalize(request);
        var errors = new List<string>();

        if (signal.Type != "entry")
        {
            errors.Add("type must be \"entry\"");
        }

        if (signal.Direction is not ("LONG" or "SHORT"))
        {
            errors.Add("direction must be LONG or SHORT");
        }

        if (signal.Contracts is null or <= 0)
        {
            errors.Add("contracts must be a positive integer");
        }

        RequireNumber(signal.EntryPrice, "entry_price", errors);
        RequireNumber(signal.StopLoss, "stop_loss", errors);
        RequireNumber(signal.TakeProfit1, "take_profit_1", errors);
        RequireNumber(signal.TakeProfit2, "take_profit_2", errors);

        if (string.IsNullOrWhiteSpace(signal.Symbol))
        {
            errors.Add("symbol is required");
        }

        if (errors.Count == 0)
        {
            ValidatePriceLayout(signal, errors);
        }

        return new TradingSignalValidationResult(errors.Count == 0, signal, errors);
    }

    private static TradingSignalRequest Normalize(TradingSignalRequest request)
    {
        return request with
        {
            Type = request.Type?.Trim().ToLowerInvariant(),
            Direction = request.Direction?.Trim().ToUpperInvariant(),
            Symbol = request.Symbol?.Trim().ToUpperInvariant()
        };
    }

    private static void ValidatePriceLayout(TradingSignalRequest signal, List<string> errors)
    {
        var entryPrice = signal.EntryPrice!.Value;
        var stopLoss = signal.StopLoss!.Value;
        var takeProfit1 = signal.TakeProfit1!.Value;
        var takeProfit2 = signal.TakeProfit2!.Value;

        if (signal.Direction == "LONG")
        {
            if (stopLoss >= entryPrice) errors.Add("LONG stop_loss must be below entry_price");
            if (takeProfit1 <= entryPrice) errors.Add("LONG take_profit_1 must be above entry_price");
            if (takeProfit2 < takeProfit1) errors.Add("LONG take_profit_2 must be greater than or equal to take_profit_1");
        }

        if (signal.Direction == "SHORT")
        {
            if (stopLoss <= entryPrice) errors.Add("SHORT stop_loss must be above entry_price");
            if (takeProfit1 >= entryPrice) errors.Add("SHORT take_profit_1 must be below entry_price");
            if (takeProfit2 > takeProfit1) errors.Add("SHORT take_profit_2 must be less than or equal to take_profit_1");
        }
    }

    private static void RequireNumber(decimal? value, string fieldName, List<string> errors)
    {
        if (value is null)
        {
            errors.Add($"{fieldName} must be a valid number");
        }
    }
}

public sealed record TradingSignalValidationResult(
    bool Ok,
    TradingSignalRequest? Signal,
    IReadOnlyList<string> Errors
);
