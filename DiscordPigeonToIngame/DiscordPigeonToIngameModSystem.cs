using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace DiscordPigeonToIngame;

public class DiscordPigeonToIngameModSystem : ModSystem
{
    private DiscordBotService? _bot;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        var config = api.LoadModConfig<ModConfig>("discordpigeontoingame.json") ?? new ModConfig();
        api.StoreModConfig(config, "discordpigeontoingame.json");

        if (string.IsNullOrWhiteSpace(config.BotToken))
        {
            Mod.Logger.Warning("BotToken not set in ModConfig/discordpigeontoingame.json - bot disabled.");
            return;
        }

        var db = new PigeonDatabase(api);
        var delivery = new PigeonDeliveryService(api, db, config);

        api.ChatCommands.Create("discordpigeon")
            .RequiresPlayer()
            .RequiresPrivilege(Privilege.chat)
            .WithDescription("Receive pending Discord pigeons")
            .BeginSubCommand("receive")
                .WithDescription("Receive any pending pigeons into your inventory")
                .HandleWith(args =>
                {
                    delivery.AttemptDelivery((IServerPlayer)args.Caller.Player!);
                    return TextCommandResult.Success();
                })
            .EndSubCommand();

        api.Event.PlayerNowPlaying += delivery.AttemptDelivery;

        _bot = new DiscordBotService(config, delivery, Mod.Logger);
        _ = _bot.StartAsync().ContinueWith(t =>
        {
            if (t.IsFaulted)
                Mod.Logger.Error($"Bot failed to start: {t.Exception?.GetBaseException().Message}");
        });
    }

    public override void Dispose()
    {
        _bot?.StopAsync().GetAwaiter().GetResult();
    }
}
