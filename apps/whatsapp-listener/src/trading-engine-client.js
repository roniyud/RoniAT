const DEFAULT_TRADING_ENGINE_URL = 'http://localhost:5066';
const DEFAULT_TIMEOUT_MS = 10000;

function getTradingEngineBaseUrl() {
    return (process.env.TRADING_ENGINE_URL || DEFAULT_TRADING_ENGINE_URL).replace(/\/+$/, '');
}

async function submitTradingSignal(signal) {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), DEFAULT_TIMEOUT_MS);

    try {
        const response = await fetch(`${getTradingEngineBaseUrl()}/api/signals`, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify(signal),
            signal: controller.signal
        });

        const bodyText = await response.text();
        const body = parseJsonBody(bodyText);

        if (!response.ok) {
            const details = body ? JSON.stringify(body) : bodyText;
            throw new Error(`Trading Engine rejected signal with HTTP ${response.status}: ${details}`);
        }

        return body;
    } catch (error) {
        if (error.name === 'AbortError') {
            throw new Error(`Trading Engine request timed out after ${DEFAULT_TIMEOUT_MS}ms`);
        }

        throw error;
    } finally {
        clearTimeout(timeout);
    }
}

function parseJsonBody(bodyText) {
    if (!bodyText) return null;

    try {
        return JSON.parse(bodyText);
    } catch {
        return null;
    }
}

module.exports = {
    submitTradingSignal,
    getTradingEngineBaseUrl
};
