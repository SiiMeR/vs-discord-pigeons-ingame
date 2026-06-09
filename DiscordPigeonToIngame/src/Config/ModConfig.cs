using System.Collections.Generic;

namespace DiscordPigeonToIngame;

public class ModConfig
{
    public string BotToken { get; set; } = "";
    public ulong GuildId { get; set; } = 0;
    public ulong AuditChannelId { get; set; } = 0;
    public ulong LinkRoleId { get; set; } = 0;
    public ulong ModerationRoleId { get; set; } = 0;
    public int DeliveryDelayMinutes { get; set; } = 5;
    public bool DmFallbackEnabled { get; set; } = false;
    public int DmFallbackDelayMinutes { get; set; } = 240;
    public int PigeonCooldownMinutes { get; set; } = 30;
    public int PigeonMessageCharacterLimit { get; set; } = 500;
    public bool CanPigeonSelf { get; set; } = false;
    public Dictionary<ulong, int> PigeonCooldownMinutesPerRoleOverride { get; set; } = new();
    public Dictionary<string, string> PlayerMappings { get; set; } = new();
}
