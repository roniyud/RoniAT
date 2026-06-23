# Trading Signal Contract

The WhatsApp listener parses incoming messages into this canonical signal shape before sending them to the trading engine.

## Entry Signal

```json
{
  "type": "entry",
  "direction": "SHORT",
  "contracts": 7,
  "stop_loss": 30755.5,
  "take_profit_1": 30709.5,
  "take_profit_2": 30686.25,
  "entry_price": 30736.25,
  "symbol": "MNQ1!"
}
```

## Validation Rules

- `type` must be `entry`.
- `direction` must be `LONG` or `SHORT`.
- `contracts` must be a positive integer.
- `entry_price`, `stop_loss`, `take_profit_1`, and `take_profit_2` must be valid numbers.
- `symbol` is required and is normalized to uppercase.
- For `LONG`: `stop_loss < entry_price <= take_profit_1 <= take_profit_2`.
- For `SHORT`: `stop_loss > entry_price >= take_profit_1 >= take_profit_2`.

## Default Target Allocation

When the signal has two take-profit levels but no explicit quantity per target, the default split is:

- `take_profit_1`: `ceil(contracts / 2)`
- `take_profit_2`: remaining contracts

For example, `7` contracts becomes `4` contracts at TP1 and `3` contracts at TP2.
