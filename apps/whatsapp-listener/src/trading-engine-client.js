const DEFAULT_TRADING_ENGINE_URL = 'http://localhost:3001';
const DEFAULT_TIMEOUT_MS = 10000;
let authToken = null;
let authTokenExpiresAt = 0;

function getTradingEngineBaseUrl() {
    return (process.env.TRADING_ENGINE_URL || DEFAULT_TRADING_ENGINE_URL).replace(/\/+$/, '');
}

async function submitTradingSignal(signal) {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), DEFAULT_TIMEOUT_MS);

    try {
        const token = await getTradingEngineToken();
        const response = await fetch(`${getTradingEngineBaseUrl()}/api/signals`, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                ...(token ? { Authorization: `Bearer ${token}` } : {})
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

async function getTradingEngineToken() {
    const username = process.env.TRADING_ENGINE_USERNAME || 'admin';
    const password = process.env.TRADING_ENGINE_PASSWORD || 'ChangeMe123!';

    if (authToken && Date.now() < authTokenExpiresAt - 60000) {
        return authToken;
    }

    const response = await fetch(`${getTradingEngineBaseUrl()}/api/auth/login`, {
        method: 'POST',
        headers: {
            'Content-Type': 'application/json'
        },
        body: JSON.stringify({ username, password })
    });

    const bodyText = await response.text();
    const body = parseJsonBody(bodyText);

    if (!response.ok || !body?.token) {
        const details = body ? JSON.stringify(body) : bodyText;
        throw new Error(`Trading Engine login failed with HTTP ${response.status}: ${details}`);
    }

    authToken = body.token;
    authTokenExpiresAt = body.expires_at ? Date.parse(body.expires_at) : Date.now() + (11 * 60 * 60 * 1000);
    return authToken;
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
