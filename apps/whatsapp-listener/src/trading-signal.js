const ENTRY_SIGNAL_TYPE = 'entry';
const DIRECTIONS = new Set(['LONG', 'SHORT']);

function normalizeTradingSignal(signal) {
    return {
        type: typeof signal.type === 'string' ? signal.type.trim().toLowerCase() : signal.type,
        direction: typeof signal.direction === 'string' ? signal.direction.trim().toUpperCase() : signal.direction,
        contracts: toInteger(signal.contracts),
        stop_loss: toNumber(signal.stop_loss),
        take_profit_1: toNumber(signal.take_profit_1),
        take_profit_2: toNumber(signal.take_profit_2),
        entry_price: toNumber(signal.entry_price),
        symbol: typeof signal.symbol === 'string' ? signal.symbol.trim().toUpperCase() : signal.symbol
    };
}

function validateTradingSignal(signal) {
    const normalized = normalizeTradingSignal(signal);
    const errors = [];

    if (normalized.type !== ENTRY_SIGNAL_TYPE) {
        errors.push('type must be "entry"');
    }

    if (!DIRECTIONS.has(normalized.direction)) {
        errors.push('direction must be LONG or SHORT');
    }

    if (!Number.isInteger(normalized.contracts) || normalized.contracts <= 0) {
        errors.push('contracts must be a positive integer');
    }

    requireFiniteNumber(normalized.entry_price, 'entry_price', errors);
    requireFiniteNumber(normalized.stop_loss, 'stop_loss', errors);
    requireFiniteNumber(normalized.take_profit_1, 'take_profit_1', errors);
    requireFiniteNumber(normalized.take_profit_2, 'take_profit_2', errors);

    if (typeof normalized.symbol !== 'string' || normalized.symbol.length === 0) {
        errors.push('symbol is required');
    }

    if (errors.length === 0) {
        validatePriceLayout(normalized, errors);
    }

    return {
        ok: errors.length === 0,
        signal: normalized,
        errors
    };
}

function getDefaultTargetAllocation(contracts) {
    const tp1Contracts = Math.ceil(contracts / 2);
    return {
        take_profit_1: tp1Contracts,
        take_profit_2: contracts - tp1Contracts
    };
}

function validatePriceLayout(signal, errors) {
    if (signal.direction === 'LONG') {
        if (signal.stop_loss >= signal.entry_price) {
            errors.push('LONG stop_loss must be below entry_price');
        }
        if (signal.take_profit_1 <= signal.entry_price) {
            errors.push('LONG take_profit_1 must be above entry_price');
        }
        if (signal.take_profit_2 < signal.take_profit_1) {
            errors.push('LONG take_profit_2 must be greater than or equal to take_profit_1');
        }
    }

    if (signal.direction === 'SHORT') {
        if (signal.stop_loss <= signal.entry_price) {
            errors.push('SHORT stop_loss must be above entry_price');
        }
        if (signal.take_profit_1 >= signal.entry_price) {
            errors.push('SHORT take_profit_1 must be below entry_price');
        }
        if (signal.take_profit_2 > signal.take_profit_1) {
            errors.push('SHORT take_profit_2 must be less than or equal to take_profit_1');
        }
    }
}

function requireFiniteNumber(value, fieldName, errors) {
    if (!Number.isFinite(value)) {
        errors.push(`${fieldName} must be a valid number`);
    }
}

function toNumber(value) {
    if (typeof value === 'number') return value;
    if (typeof value === 'string' && value.trim() !== '') return Number(value);
    return null;
}

function toInteger(value) {
    const numberValue = toNumber(value);
    return Number.isInteger(numberValue) ? numberValue : null;
}

module.exports = {
    normalizeTradingSignal,
    validateTradingSignal,
    getDefaultTargetAllocation
};
