# Discord Pigeon To Ingame — Setup Guide

## 1. Create a Discord Application & Bot

1. Go to https://discord.com/developers/applications and click **New Application**.
2. Name it (e.g. "VS Pigeons") and click **Create**.
3. In the left sidebar, click **Bot**.
4. Click **Reset Token**, confirm, and copy the token — you'll need it shortly.
5. Under **Privileged Gateway Intents**, leave everything **off** (no privileged intents are needed).

## 2. Invite the Bot to Your Server

1. In the left sidebar, click **OAuth2 → URL Generator**.
2. Under **Scopes**, tick **bot** and **applications.commands**.
3. Under **Bot Permissions**, tick **Send Messages** (needed for ephemeral responses).
4. Copy the generated URL, open it in a browser, and invite the bot to your server.

## 3. Get Your Server (Guild) ID

1. In Discord, open **User Settings → Advanced** and enable **Developer Mode**.
2. Right-click your server name in the sidebar and click **Copy Server ID**.

## 4. Get Discord User IDs for Player Mappings

For each player who will send or receive pigeons, you need their Discord user ID:

1. Right-click their username in Discord and click **Copy User ID**.

Note: **both senders and recipients** must be in the mapping — senders need it so their VS username appears as the parchment author.

## 5. Install the Mod

Place the built `discordpigeontoingame_1.0.0.zip` (from `Releases/`) into your VS server's `Mods/` folder.

## 6. Generate the Config File

Start the VS server once with the mod installed. It will create the config file at:

```
{VS data folder}/ModConfig/discordpigeontoingame.json
```

The default content will be:

```json
{
  "BotToken": "",
  "GuildId": 0,
  "PlayerMappings": {}
}
```

## 7. Fill In the Config

Stop the server, then edit the config file:

```json
{
  "BotToken": "YOUR_BOT_TOKEN_HERE",
  "GuildId": 123456789012345678,
  "PlayerMappings": {
    "111222333444555666": "Steve",
    "777888999000111222": "Alex"
  }
}
```

- **BotToken** — the token from step 1.
- **GuildId** — the server ID from step 3.
- **PlayerMappings** — Discord user ID (string key) → exact VS player name (case-sensitive).

## 8. Start the Server

Start the VS server. In the logs you should see the bot connect. If there's an error it will appear prefixed with `[DiscordPigeon]`.

## 9. Test It

1. In Discord, type `/pigeon send` and select a recipient from the user picker.
2. Fill in the **Title** and **Message** fields in the modal and submit.
3. You should see an ephemeral "Pigeon sent to PlayerName!" confirmation.
4. Log in as the recipient in VS — the parchment will appear in your inventory with a chat notification.
5. If your inventory is full, free up a slot and run `/discordpigeon receive` in game chat.

## Troubleshooting

| Problem | Fix |
|---|---|
| `/pigeon` command doesn't appear in Discord | Wait a few seconds after the server starts for command registration, then try again. Ensure the bot has the `applications.commands` scope. |
| "Your Discord account is not linked" | Your Discord user ID is missing from `PlayerMappings` in the config. |
| "That Discord user is not linked" | The recipient's Discord user ID is missing from `PlayerMappings`. |
| Bot fails to start | Check the VS server log for `[DiscordPigeon]` errors. Most likely the `BotToken` is wrong or empty. |
| Parchment not appearing on login | Inventory may be full — run `/discordpigeon receive` after freeing a slot. |
| Guild not found error in log | Double-check `GuildId` in the config matches your server ID. |
