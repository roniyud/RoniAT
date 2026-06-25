# WhatsApp ID Listener Console

Standalone WhatsApp listener for discovering chat IDs.

This copy does not connect to RoniAT Trading Engine, does not parse trading signals, and does not send anything anywhere.
It only prints WhatsApp chat/message IDs to the console.

## Run

```powershell
cd D:\RONI\RoniAT\apps\whatsapp-listener-console
npm install
npm start
```

## Listen To All Chats

Default behavior uses an empty `TARGET_ID`, so every incoming/outgoing message prints ID information.

## Filter A Specific Chat

Set `TARGET_ID` only if you want to listen to one specific WhatsApp contact or group:

```powershell
$env:TARGET_ID='972546507978@c.us'
npm start
```

When `TARGET_ID` is set, the full message body is printed too.
