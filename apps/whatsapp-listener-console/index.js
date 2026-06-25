const { Client, LocalAuth } = require('whatsapp-web.js');
const qrcode = require('qrcode-terminal');

const targetId = (process.env.TARGET_ID || '').trim();

console.log('[i] Starting standalone WhatsApp ID listener.');
console.log('[i] Trading signal parsing is disabled.');
console.log('[i] Trading Engine integration is disabled.');
console.log(targetId
    ? `[i] Listening only to TARGET_ID: ${targetId}`
    : '[i] TARGET_ID is empty. Listening to all chats.');

const client = new Client({
    authStrategy: new LocalAuth({
        clientId: 'console-id-listener'
    }),
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
    console.log('\n======================================================');
    console.log('Scan this QR code with WhatsApp on your phone:');
    console.log('WhatsApp > Linked devices > Link a device');
    console.log('======================================================\n');
    qrcode.generate(qr, { small: true });
});

client.on('ready', async () => {
    console.log('\n======================================================');
    console.log('WhatsApp ID listener is connected and ready.');
    console.log('Every received message will print its chat id.');
    console.log('======================================================\n');

    await printKnownChats();
});

client.on('message_create', async (message) => {
    try {
        if (targetId && message.from !== targetId && message.to !== targetId) {
            return;
        }

        const chat = await message.getChat().catch(() => null);
        const contact = await message.getContact().catch(() => null);

        printMessageInfo(message, chat, contact);
    } catch (error) {
        console.error('[!] Failed to print message info:', error);
    }
});

async function printKnownChats() {
    try {
        const chats = await client.getChats();
        console.log('\n================ KNOWN CHATS ================');
        console.log(`Total chats: ${chats.length}`);

        chats
            .slice()
            .sort((a, b) => String(a.name || '').localeCompare(String(b.name || '')))
            .forEach((chat) => {
                console.log(JSON.stringify({
                    id: chat.id?._serialized || null,
                    name: chat.name || null,
                    isGroup: Boolean(chat.isGroup),
                    unreadCount: chat.unreadCount || 0
                }, null, 2));
            });

        console.log('=============================================\n');
    } catch (error) {
        console.error('[!] Failed to list known chats:', error.message || error);
    }
}

function printMessageInfo(message, chat, contact) {
    const payload = {
        timestamp: new Date().toISOString(),
        chatId: chat?.id?._serialized || message.from || null,
        chatName: chat?.name || null,
        isGroup: Boolean(chat?.isGroup),
        from: message.from || null,
        to: message.to || null,
        author: message.author || null,
        contactId: contact?.id?._serialized || null,
        contactName: contact?.pushname || contact?.name || contact?.number || null,
        fromMe: Boolean(message.fromMe),
        messageType: message.type || null,
        bodyPreview: preview(message.body)
    };

    console.log('\n================ MESSAGE ID INFO ================');
    console.log(JSON.stringify(payload, null, 2));
    if (targetId) {
        console.log('\n================ MESSAGE BODY ===================');
        console.log(message.body || '');
    }
    console.log('=================================================\n');
}

function preview(value) {
    if (!value) return '';
    const normalized = String(value).replace(/\s+/g, ' ').trim();
    return normalized.length <= 160 ? normalized : `${normalized.slice(0, 160)}...`;
}

client.initialize();
