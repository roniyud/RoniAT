const { Client, LocalAuth } = require('whatsapp-web.js');
const fs = require('fs');
const http = require('http');
const path = require('path');
const qrcode = require('qrcode-terminal');
const { validateTradingSignal, getDefaultTargetAllocation } = require('./src/trading-signal');
const { submitTradingSignal, getTradingEngineBaseUrl } = require('./src/trading-engine-client');

const LOG_DIRECTORY = path.join(__dirname, 'logs');
const CONFIG_DIRECTORY = path.join(__dirname, 'config');
const SETTINGS_FILE = path.join(CONFIG_DIRECTORY, 'settings.json');
const ADMIN_HOST = process.env.WHATSAPP_ADMIN_HOST || '127.0.0.1';
const ADMIN_PORT = Number(process.env.WHATSAPP_ADMIN_PORT || 3011);

function ensureSettingsFile() {
    if (!fs.existsSync(SETTINGS_FILE)) {
        saveSettings({
            trackMode: TARGET_ID ? 'specific' : 'all',
            targetIds: TARGET_ID ? [TARGET_ID] : [],
            signalChats: []
        });
    }
}

function loadSettings() {
    try {
        return normalizeSettings(JSON.parse(fs.readFileSync(SETTINGS_FILE, 'utf8')));
    } catch (error) {
        logEvent('settings.load_failed', {
            message: error.message
        });
        return {
            trackMode: TARGET_ID ? 'specific' : 'all',
            targetIds: TARGET_ID ? [TARGET_ID] : [],
            signalChats: []
        };
    }
}

function saveSettings(settings) {
    const normalized = normalizeSettings(settings);
    fs.mkdirSync(CONFIG_DIRECTORY, { recursive: true });
    fs.writeFileSync(SETTINGS_FILE, JSON.stringify(normalized, null, 2), 'utf8');
    return normalized;
}

function normalizeSettings(settings) {
    const trackMode = String(settings?.trackMode || 'all').trim().toLowerCase();
    const rawTargetIds = Array.isArray(settings?.targetIds)
        ? settings.targetIds
        : String(settings?.targetIds || '').split(',');

    return {
        trackMode: trackMode === 'specific' ? 'specific' : 'all',
        targetIds: rawTargetIds.map((value) => String(value).trim()).filter(Boolean),
        signalChats: normalizeSignalChats(settings?.signalChats)
    };
}

function normalizeSignalChats(signalChats) {
    if (!Array.isArray(signalChats)) {
        return [];
    }

    const byId = new Map();
    for (const chat of signalChats) {
        const id = String(chat?.id || '').trim();
        if (!id) continue;

        byId.set(id, {
            id,
            name: String(chat?.name || '').trim(),
            isGroup: Boolean(chat?.isGroup),
            contactId: String(chat?.contactId || '').trim(),
            contactName: String(chat?.contactName || '').trim(),
            lastSignalAt: String(chat?.lastSignalAt || '').trim()
        });
    }

    return [...byId.values()]
        .sort((left, right) => String(right.lastSignalAt || '').localeCompare(String(left.lastSignalAt || '')));
}

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

        const traffic = {
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
        };

        appendJsonLine(traffic);
        return traffic;
    } catch (error) {
        logEvent('traffic.log_failed', {
            message: error.message,
            stack: error.stack
        });
        return null;
    }
}

function shouldProcessMessage(msg, traffic) {
    const settings = loadSettings();

    if (settings.trackMode === 'all') {
        return true;
    }

    if (settings.targetIds.length === 0) {
        return false;
    }

    return settings.targetIds.includes(msg.from)
        || settings.targetIds.includes(msg.to)
        || (msg.author && settings.targetIds.includes(msg.author))
        || (traffic?.chat_id && settings.targetIds.includes(traffic.chat_id))
        || (traffic?.contact_id && settings.targetIds.includes(traffic.contact_id));
}

function rememberSignalChat(traffic) {
    if (!traffic) {
        return;
    }

    const settings = loadSettings();
    if (settings.trackMode !== 'all') {
        return;
    }

    const chatId = traffic.chat_id || traffic.from;
    if (!chatId) {
        return;
    }

    const signalChats = settings.signalChats.filter((chat) => chat.id !== chatId);
    signalChats.unshift({
        id: chatId,
        name: traffic.chat_name || '',
        isGroup: Boolean(traffic.is_group),
        contactId: traffic.contact_id || '',
        contactName: traffic.contact_name || '',
        lastSignalAt: new Date().toISOString()
    });

    saveSettings({
        ...settings,
        signalChats
    });

    logEvent('settings.signal_chat_remembered', {
        chat_id: chatId,
        chat_name: traffic.chat_name || null,
        is_group: Boolean(traffic.is_group)
    });
}

function startSettingsServer() {
    const server = http.createServer(async (req, res) => {
        try {
            if (req.method === 'GET' && req.url === '/') {
                sendHtml(res, renderSettingsPage(loadSettings()));
                return;
            }

            if (req.method === 'GET' && req.url === '/api/settings') {
                sendJson(res, loadSettings());
                return;
            }

            if (req.method === 'POST' && req.url === '/api/settings') {
                const body = await readRequestBody(req);
                const saved = saveSettings({
                    ...loadSettings(),
                    ...JSON.parse(body || '{}')
                });
                logEvent('settings.updated', saved);
                sendJson(res, saved);
                return;
            }

            res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
            res.end('Not found');
        } catch (error) {
            res.writeHead(500, { 'Content-Type': 'application/json; charset=utf-8' });
            res.end(JSON.stringify({ error: error.message }));
        }
    });

    server.listen(ADMIN_PORT, ADMIN_HOST);
}

function readRequestBody(req) {
    return new Promise((resolve, reject) => {
        let body = '';
        req.on('data', (chunk) => {
            body += chunk;
            if (body.length > 1024 * 1024) {
                reject(new Error('Request body too large'));
                req.destroy();
            }
        });
        req.on('end', () => resolve(body));
        req.on('error', reject);
    });
}

function sendJson(res, payload) {
    res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
    res.end(JSON.stringify(payload));
}

function sendHtml(res, html) {
    res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
    res.end(html);
}

function renderSettingsPage(settings) {
    const targetIds = escapeHtml(settings.targetIds.join('\n'));
    const allChecked = settings.trackMode === 'all' ? 'checked' : '';
    const specificChecked = settings.trackMode === 'specific' ? 'checked' : '';
    const signalChats = renderSignalChats(settings.signalChats);

    return `<!doctype html>
<html lang="he" dir="rtl">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>RoniAT WhatsApp</title>
  <style>
    body { margin: 0; font-family: Arial, sans-serif; background: #f6f8f9; color: #14211f; }
    main { max-width: 760px; margin: 0 auto; padding: 28px 16px; }
    section { border: 1px solid #dfe5e7; border-radius: 8px; background: #fff; padding: 18px; }
    h1 { margin: 0 0 14px; font-size: 24px; }
    p { color: #60706d; line-height: 1.5; }
    label { display: block; margin: 14px 0; font-weight: 700; }
    .option { display: flex; align-items: center; gap: 8px; }
    textarea { width: 100%; min-height: 150px; box-sizing: border-box; border: 1px solid #cfd8dc; border-radius: 8px; padding: 10px; direction: ltr; font-family: Consolas, monospace; font-size: 14px; }
    button { height: 40px; border: 0; border-radius: 8px; padding: 0 16px; background: #0f766e; color: #fff; cursor: pointer; font-weight: 800; }
    .chat-list { display: grid; gap: 8px; margin-top: 18px; }
    .chat-card { display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: 10px; align-items: center; border: 1px solid #e1e7e9; border-radius: 8px; padding: 10px; direction: ltr; }
    .chat-card strong { display: block; color: #17211f; direction: rtl; text-align: right; }
    .chat-card code { display: block; margin-top: 4px; color: #60706d; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .chat-card small { display: block; margin-top: 4px; color: #60706d; direction: rtl; text-align: right; }
    .chat-card button { height: 34px; background: #344054; }
    .empty-list { margin-top: 12px; color: #60706d; }
    .status { min-height: 22px; margin-top: 12px; color: #13795b; font-weight: 700; }
  </style>
</head>
<body>
  <main>
    <section>
      <h1>ניהול WhatsApp Listener</h1>
      <p>השינויים נשמרים לקובץ ונכנסים לתוקף מיד, בלי אתחול.</p>
      <form id="settingsForm">
        <label class="option"><input type="radio" name="trackMode" value="all" ${allChecked}> בדוק בכל הצ'אטים</label>
        <label class="option"><input type="radio" name="trackMode" value="specific" ${specificChecked}> בדוק רק צ'אטים ספציפיים</label>
        <label>
          מזהים ספציפיים, אחד בכל שורה
          <textarea id="targetIds" placeholder="972546507978@c.us&#10;972799230744-1602684959@g.us">${targetIds}</textarea>
        </label>
        <button type="submit">שמור</button>
        <div id="status" class="status"></div>
      </form>
      <div class="chat-list">
        <h2>צ'אטים שבהם זוהתה כניסה לעסקה</h2>
        ${signalChats}
      </div>
    </section>
  </main>
  <script>
    function addTargetId(id) {
      const textarea = document.getElementById('targetIds');
      const values = textarea.value.split(/\\r?\\n|,/).map((value) => value.trim()).filter(Boolean);
      if (!values.includes(id)) values.push(id);
      textarea.value = values.join('\\n');
      document.querySelector('input[name="trackMode"][value="specific"]').checked = true;
    }

    document.getElementById('settingsForm').addEventListener('submit', async (event) => {
      event.preventDefault();
      const trackMode = document.querySelector('input[name="trackMode"]:checked').value;
      const targetIds = document.getElementById('targetIds').value.split(/\\r?\\n|,/).map((value) => value.trim()).filter(Boolean);
      const response = await fetch('/api/settings', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ trackMode, targetIds })
      });
      if (!response.ok) throw new Error(await response.text());
      const saved = await response.json();
      document.getElementById('status').textContent = 'נשמר: ' + saved.trackMode + ' (' + saved.targetIds.length + ' מזהים)';
    });
  </script>
</body>
</html>`;
}

function renderSignalChats(signalChats) {
    if (!signalChats.length) {
        return '<div class="empty-list">עדיין לא זוהו צ׳אטים עם כניסה לעסקה.</div>';
    }

    return signalChats.map((chat) => {
        const title = escapeHtml(chat.name || chat.contactName || chat.id);
        const id = escapeHtml(chat.id);
        const type = chat.isGroup ? 'קבוצה' : 'איש קשר';
        const contact = chat.contactName || chat.contactId
            ? ` / ${escapeHtml(chat.contactName || chat.contactId)}`
            : '';
        const lastSignalAt = chat.lastSignalAt
            ? new Date(chat.lastSignalAt).toLocaleString('he-IL')
            : '-';

        return `<div class="chat-card">
          <div>
            <strong>${title}</strong>
            <small>ID לשימוש ב-specific</small>
            <code>${id}</code>
            <small>${type}${contact} / זוהה לאחרונה: ${escapeHtml(lastSignalAt)}</small>
          </div>
          <button type="button" onclick="addTargetId('${escapeAttribute(chat.id)}')">הוסף לספציפי</button>
        </div>`;
    }).join('');
}

function escapeHtml(value) {
    return String(value)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#039;');
}

function escapeAttribute(value) {
    return escapeHtml(value).replace(/`/g, '&#096;');
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

ensureSettingsFile();
startSettingsServer();

console.log(fixHebrew('מפעיל את הדפדפן ברקע, אנא המתן מספר שניות...'));

console.log(`[i] Trading Engine URL: ${getTradingEngineBaseUrl()}`);
console.log(`[i] WhatsApp settings page: http://${ADMIN_HOST}:${ADMIN_PORT}`);
logEvent('listener.starting', {
    trading_engine_url: getTradingEngineBaseUrl(),
    settings_file: SETTINGS_FILE
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
        const traffic = await logTraffic(msg);

        if (!shouldProcessMessage(msg, traffic)) return;

        if (msg.body.includes('כניסה לעסקה') || msg.body.includes('הקסעל הסינכ')) {
            rememberSignalChat(traffic);

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
