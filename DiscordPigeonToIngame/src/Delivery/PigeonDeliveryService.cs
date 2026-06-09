using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace DiscordPigeonToIngame;

public class PigeonDeliveryService
{
    private readonly ICoreServerAPI _api;
    private readonly PigeonDatabase _db;
    private readonly ModConfig _config;

    public PigeonDeliveryService(ICoreServerAPI api, PigeonDatabase db, ModConfig config)
    {
        _api = api;
        _db = db;
        _config = config;
    }

    public string? GetPlayerName(string uid) =>
        _api.PlayerData.GetPlayerDataByUid(uid)?.LastKnownPlayername;

    public void ResolveVsPlayerUid(string vsName, Action<string?, string?> onComplete)
    {
        _api.PlayerData.ResolvePlayerName(vsName, (response, uid) =>
        {
            if (response == EnumServerResponse.Offline)
                onComplete(null, "Could not reach the Vintage Story auth server. Try again later.");
            else if (response != EnumServerResponse.Good || uid == null)
                onComplete(null, $"No Vintage Story player found with the name '{vsName}'.");
            else
                onComplete(uid, null);
        });
    }

    public void AddMapping(string discordId, string playerUid)
    {
        _config.PlayerMappings[discordId] = playerUid;
        _api.StoreModConfig(_config, "discordpigeontoingame.json");
    }

    public List<(string RecipientName, string Title, string Message, long SentAt, bool Delivered)> GetSentPigeons(string senderUid) =>
        _db.GetSentPigeons(senderUid)
            .Select(p => (RecipientName: GetPlayerName(p.RecipientUid) ?? p.RecipientUid, Title: p.Title, Message: p.Message, SentAt: p.SentAt, p.Delivered))
            .ToList();

    public List<(string SenderName, string Title, string Message, long SentAt, bool Delivered)> GetReceivedPigeons(string recipientUid) =>
        _db.GetReceivedPigeons(recipientUid)
            .Select(p => (SenderName: GetPlayerName(p.SenderUid) ?? p.SenderUid, Title: p.Title, Message: p.Message, SentAt: p.SentAt, p.Delivered))
            .ToList();

    public TimeSpan? GetCooldownRemaining(string senderUid, int cooldownMinutes)
    {
        var lastSent = _db.GetLastSentAt(senderUid);
        if (lastSent == 0) return null;
        var remaining = TimeSpan.FromMinutes(cooldownMinutes)
            - TimeSpan.FromSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - lastSent);
        return remaining > TimeSpan.Zero ? remaining : null;
    }

    public void SendPigeon(string senderUid, string recipientUid, string title, string message, Action? onEnRoute = null)
    {
        _db.Add(recipientUid, senderUid, title, message);

        _api.Event.RegisterCallback(_ =>
        {
            var player = _api.World.AllOnlinePlayers
                .OfType<IServerPlayer>()
                .FirstOrDefault(p => p.PlayerUID == recipientUid);
            if (player != null)
                AttemptDelivery(player);
            else
                onEnRoute?.Invoke();
        }, _config.DeliveryDelayMinutes * 60 * 1000);
    }

    public void AttemptDelivery(IServerPlayer player)
    {
        var pending = _db.GetPending(player.PlayerUID, _config.DeliveryDelayMinutes * 60);
        if (pending.Count == 0) return;

        if (!HasSkyAccess(player))
        {
            player.SendMessage(GlobalConstants.GeneralChatGroup,
                Lang.Get("discordpigeontoingame:pigeon-blocked-sky"),
                EnumChatType.Notification);
            return;
        }

        var deliveredTitles = new List<string>();
        var failed = new List<(long Id, string SenderUid, string Title, string Message)>();

        foreach (var pigeon in pending)
        {
            var stack = BuildParchment(pigeon.SenderUid, pigeon.Title, pigeon.Message);
            if (player.InventoryManager.TryGiveItemstack(stack))
            {
                _db.MarkDelivered(pigeon.Id);
                deliveredTitles.Add(pigeon.Title);
                var senderName = GetPlayerName(pigeon.SenderUid) ?? pigeon.SenderUid;
                _api.Logger.Audit($"{player.PlayerName} received pigeon \"{pigeon.Title}\" from {senderName}");
            }
            else
            {
                failed.Add(pigeon);
            }
        }

        if (deliveredTitles.Count > 0)
        {
            var titles = string.Join(", ", deliveredTitles.Select(t => $"\"{t}\""));
            var deliveredMsg = deliveredTitles.Count == 1
                ? Lang.Get("discordpigeontoingame:pigeon-delivered-one", titles)
                : Lang.Get("discordpigeontoingame:pigeon-delivered-many", deliveredTitles.Count, titles);
            player.SendMessage(GlobalConstants.GeneralChatGroup, deliveredMsg, EnumChatType.Notification);
            _api.World.PlaySoundAt(new AssetLocation("discordpigeontoingame:sounds/pigeon"), player.Entity, null, true, 32f, 0.75f);
        }

        if (failed.Count > 0)
        {
            var fullMsg = failed.Count == 1
                ? Lang.Get("discordpigeontoingame:pigeon-inventory-full-one")
                : Lang.Get("discordpigeontoingame:pigeon-inventory-full-many", failed.Count);
            player.SendMessage(GlobalConstants.GeneralChatGroup, fullMsg, EnumChatType.Notification);
        }
    }

    private bool HasSkyAccess(IServerPlayer player)
    {
        var pos = player.Entity.Pos.AsBlockPos;
        return pos.Y >= _api.World.BlockAccessor.GetRainMapHeightAt(pos);
    }

    private ItemStack BuildParchment(string senderUid, string title, string message)
    {
        var item = _api.World.GetItem(new AssetLocation("game:paper-parchment"));
        var stack = new ItemStack(item);
        stack.Attributes.SetString("signedby", GetPlayerName(senderUid) ?? senderUid);
        stack.Attributes.SetString("signedbyuid", senderUid);
        stack.Attributes.SetString("title", title);
        stack.Attributes.SetString("text", message);
        return stack;
    }
}
