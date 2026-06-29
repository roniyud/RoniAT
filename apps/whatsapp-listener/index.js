const { Client, LocalAuth } = require('whatsapp-web.js');
const fs = require('fs');
const path = require('path');
const qrcode = require('qrcode-terminal');
const { validateTradingSignal, getDefaultTargetAllocation } = require('./src/trading-signal');
const { submitTradingSignal, getTradingEngineBaseUrl } = require('./src/trading-engine-client');

const LOG_DIRECTORY = path.join(__dirname, 'logs');

function logEvent(event, details = {}) {
    const record = {
        timestamp: new Date().toISOString(),
        event,
        ...details
    };

    appendJsonLine(record);
}

function appendJsonLine(record) {
    try {
        fs.mkdirSync(LOG_DIRECTORY, { recursive: true });
        fs.appendFileSync(getDailyLogFile(), `${JSON.stringify(record)}\n`, 'utf8');
    } catch (error) {
        console.error('Failed writing WhatsApp log file:', error.message);
    }
}

function getDailyLogFile(date = new Date()) {
    const pad = (value) => String(value).padStart(2, '0');
    const datePart = `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
    return path.join(LOG_DIRECTORY, `whatsapp-${datePart}.log`);
}

async function logTraffic(msg) {
    try {
        const [chat, contact] = await Promise.all([
            msg.getChat().catch(() => null),
            msg.getContact().catch(() => null)
        ]);

        appendJsonLine({
            timestamp: new Date().toISOString(),
            event: 'whatsapp.traffic',
            whatsapp_timestamp: msg.timestamp ? new Date(msg.timestamp * 1000).toISOString() : null,
            direction: msg.fromMe ? 'outgoing' : 'incoming',
            from: msg.from,
            to: msg.to,
            author: msg.author || null,
            chat_id: chat?.id?._serialized || msg.from,
            chat_name: chat?.name || null,
            is_group: Boolean(chat?.isGroup),
            contact_id: contact?.id?._serialized || null,
            contact_name: contact?.pushname || contact?.name || contact?.number || null,
            type: msg.type,
            has_media: Boolean(msg.hasMedia),
            body: msg.body || ''
        });
    } catch (error) {
        logEvent('traffic.log_failed', {
            message: error.message,
            stack: error.stack
        });
    }
}

// פונקציה שמסדרת את כיוון העברית לקונסול
function fixHebrew(text) {
    if (!text) return '';
    const hasHebrew = /[\u0590-\u05FF]/.test(text);
    if (hasHebrew) {
        return text.split('\n').map(line => {
            return line.split(' ').map(word => {
                return /[\u0590-\u05FF]/.test(word) ? word.split('').reverse().join('') : word;
            }).reverse().join(' ');
        }).join('\n');
    }
    return text;
}

// פונקציה שמפרקת את הטקסט ומחזירה אובייקט JSON באנגלית
function parseSignalToJSON(text) {
    const jsonResult = {
        type: "entry",
        direction: null,
        contracts: null,
        stop_loss: null,
        take_profit_1: null,
        take_profit_2: null,
        entry_price: null,
        symbol: null
    };

    const lines = text.split('\n');

    lines.forEach(line => {
        const cleanLine = line.trim();

        // 1. כיוון עסקה
        if (cleanLine.includes('כיוון') || cleanLine.includes('ןוויכ')) {
            jsonResult.direction = cleanLine.includes('שורט') || cleanLine.includes('טרוש') ? 'SHORT' : 'LONG';
        }
        // 2. כמות חוזים
        else if (cleanLine.includes('אחוזים') || cleanLine.includes('חוזים') || cleanLine.includes('םיזוח')) {
            const match = cleanLine.match(/\d+/);
            jsonResult.contracts = match ? parseInt(match[0]) : null;
        }
        // 3. סטופ לוס
        else if (cleanLine.includes('סטופ') || cleanLine.includes('פוטס')) {
            const match = cleanLine.match(/[\d.]+/);
            jsonResult.stop_loss = match ? parseFloat(match[0]) : null;
        }
        // 4. יעד 1 - הסרת סימני הדירוג "1:" או ":1" לפני שליפת המחיר
        else if ((cleanLine.includes('יעד') || cleanLine.includes('דעי')) && cleanLine.includes('1')) {
            const lineWithoutTargetNumber = cleanLine.replace(/1\s*:/, '').replace(/:\s*1/, '');
            const match = lineWithoutTargetNumber.match(/[\d.]+/);
            jsonResult.take_profit_1 = match ? parseFloat(match[0]) : null;
        }
        // 5. יעד 2 - הסרת סימני הדירוג "2:" או ":2" לפני שליפת המחיר
        else if ((cleanLine.includes('יעד') || cleanLine.includes('דעי')) && cleanLine.includes('2')) {
            const lineWithoutTargetNumber = cleanLine.replace(/2\s*:/, '').replace(/:\s*2/, '');
            const match = lineWithoutTargetNumber.match(/[\d.]+/);
            jsonResult.take_profit_2 = match ? parseFloat(match[0]) : null;
        }
        // 6. מחיר כניסה
        else if (cleanLine.includes('כניסה') || cleanLine.includes('הסינכ')) {
            const match = cleanLine.match(/[\d.]+/);
            jsonResult.entry_price = match ? parseFloat(match[0]) : null;
        }
        // 7. סימול נכס
        else if (cleanLine.includes('/') || cleanLine.includes('MNQ') || cleanLine.includes('סימול')) {
            const parts = cleanLine.split(':');
            const symbolPart = parts[1] || parts[0];
            const match = symbolPart.match(/[A-Z0-9!]+/i);
            jsonResult.symbol = match ? match[0] : symbolPart.trim();
        }
    });

    return jsonResult;
}

// =========================================================================
// הגדר כאן את המזהה שאתה רוצה לעקוב אחריו (קבוצה או איש קשר פרטי)
const TARGET_ID = '972546507978@c.us';
// =========================================================================

console.log(fixHebrew('מפעיל את הדפדפן ברקע, אנא המתן מספר שניות...'));

console.log(`[i] Trading Engine URL: ${getTradingEngineBaseUrl()}`);
logEvent('listener.starting', {
    trading_engine_url: getTradingEngineBaseUrl(),
    target_id: TARGET_ID || null
});

const client = new Client({
    authStrategy: new LocalAuth(),
    puppeteer: {
        headless: true,
        args: [
            '--no-sandbox',
            '--disable-setuid-sandbox',
            '--disable-dev-shm-usage',
            '--disable-accelerated-2d-canvas',
            '--no-first-run',
            '--no-zygote',
            '--disable-gpu'
        ]
    }
});

client.on('qr', (qr) => {
    logEvent('whatsapp.qr_received');
    console.log('\n======================================================');
    console.log(fixHebrew('סרוק את קוד ה-QR הבא באמצעות האפליקציה בטלפון:'));
    console.log('======================================================\n');
    qrcode.generate(qr, { small: true });
});

client.on('ready', () => {
    logEvent('whatsapp.ready');
    console.log('\n======================================================');
    console.log(fixHebrew('הבוט מחובר ומוכן לפענח אותות מסחר בקבוצה!'));
    console.log('======================================================\n');
});

client.on('message_create', async (msg) => {
    try {
        await logTraffic(msg);

        if (TARGET_ID && msg.from !== TARGET_ID) return;

        if (msg.body.includes('כניסה לעסקה') || msg.body.includes('הקסעל הסינכ')) {
            logEvent('signal.message_detected', {
                from: msg.from,
                to: msg.to,
                author: msg.author || null,
                message_type: msg.type,
                body_length: msg.body.length
            });

            const tradingDataJSON = parseSignalToJSON(msg.body);
            const validation = validateTradingSignal(tradingDataJSON);

            if (!validation.ok) {
                logEvent('signal.invalid', {
                    signal: validation.signal,
                    errors: validation.errors
                });

                console.log(`\n====================================`);
                console.log(`[!] INVALID TRADING SIGNAL:`);
                console.log(`====================================`);
                console.log(JSON.stringify(validation.signal, null, 4));
                console.log(`Errors:`);
                validation.errors.forEach(error => console.log(`- ${error}`));
                console.log(`====================================\n`);
                return;
            }

            const targetAllocation = getDefaultTargetAllocation(validation.signal.contracts);
            logEvent('signal.valid', {
                signal: validation.signal,
                target_allocation: targetAllocation
            });

            console.log(`\n====================================`);
            console.log(`[+] DETECTED TRADING SIGNAL JSON:`);
            console.log(`====================================`);
            console.log(JSON.stringify(validation.signal, null, 4));
            console.log(`[+] DEFAULT TARGET ALLOCATION:`);
            console.log(JSON.stringify(targetAllocation, null, 4));
            console.log(`====================================\n`);

            const savedSignal = await submitTradingSignal(validation.signal);
            logEvent('signal.sent_to_trading_engine', {
                signal_id: savedSignal?.id ?? null,
                status: savedSignal?.status ?? null,
                symbol: savedSignal?.symbol ?? validation.signal.symbol
            });

            console.log(`\n====================================`);
            console.log(`[+] SIGNAL SENT TO TRADING ENGINE:`);
            console.log(`====================================`);
            console.log(JSON.stringify(savedSignal, null, 4));
            console.log(`====================================\n`);
        }

    } catch (error) {
        logEvent('listener.error', {
            message: error.message,
            stack: error.stack
        });
        console.error('Error parsing trading message:', error);
    }
});

client.initialize();
