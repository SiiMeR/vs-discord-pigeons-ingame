using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Vintagestory.API.Common;

namespace DiscordPigeonToIngame;

public class DiscordBotService
{
    private readonly ModConfig _config;
    private readonly PigeonDeliveryService _delivery;
    private readonly ILogger _vsLogger;
    private DiscordSocketClient? _client;
    private bool _recovered;

    public DiscordBotService(ModConfig config, PigeonDeliveryService delivery, ILogger vsLogger)
    {
        _config = config;
        _delivery = delivery;
        _vsLogger = vsLogger;
    }

    public async Task StartAsync()
    {
        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds
        });

        _client.Log += msg =>
        {
            if (msg.Severity <= LogSeverity.Warning)
                _vsLogger.Warning($"{msg.Message}");
            return Task.CompletedTask;
        };

        _client.Ready += OnReady;
        _client.SlashCommandExecuted += OnSlashCommand;
        _client.ModalSubmitted += OnModalSubmitted;

        await _client.LoginAsync(TokenType.Bot, _config.BotToken);
        await _client.StartAsync();
    }

    public async Task StopAsync()
    {
        if (_client != null)
            await _client.StopAsync();
    }

    private async Task OnReady()
    {
        if (!_recovered)
        {
            _recovered = true;
            _delivery.RecoverDmFallbacks((discordId, title, message) => _ = DmPigeonContentAsync(discordId, title, message));
        }

        try
        {
            var guild = _client!.GetGuild(_config.GuildId);
            if (guild == null)
            {
                _vsLogger.Error($"Guild {_config.GuildId} not found.");
                return;
            }

            var command = new SlashCommandBuilder
            {
                Name = "pigeon",
                Description = "Send an in-game pigeon message",
                Options = new List<SlashCommandOptionBuilder>
                {
                    new()
                    {
                        Name = "send",
                        Description = "Send a pigeon to a Vintage Story player",
                        Type = ApplicationCommandOptionType.SubCommand,
                        Options = new List<SlashCommandOptionBuilder>
                        {
                            new() { Name = "recipient", Description = "Discord user to send a pigeon to", Type = ApplicationCommandOptionType.User, IsRequired = false },
                            new() { Name = "vs_username", Description = "Vintage Story username to send a pigeon to", Type = ApplicationCommandOptionType.String, IsRequired = false }
                        }
                    },
                    new()
                    {
                        Name = "list",
                        Description = "List your sent pigeons",
                        Type = ApplicationCommandOptionType.SubCommand,
                        Options = new List<SlashCommandOptionBuilder>
                        {
                            new() { Name = "user", Description = "Moderators only: view another user's sent pigeons", Type = ApplicationCommandOptionType.User, IsRequired = false },
                            new() { Name = "page", Description = "Page number", Type = ApplicationCommandOptionType.Integer, IsRequired = false, MinValue = 1 }
                        }
                    },
                    new()
                    {
                        Name = "link",
                        Description = "Link your Discord account to a Vintage Story player name",
                        Type = ApplicationCommandOptionType.SubCommand,
                        Options = new List<SlashCommandOptionBuilder>
                        {
                            new() { Name = "username", Description = "Vintage Story player name", Type = ApplicationCommandOptionType.String, IsRequired = true },
                            new() { Name = "user", Description = "Moderators only: Discord user to link", Type = ApplicationCommandOptionType.User, IsRequired = false }
                        }
                    }
                }
            }.Build();

            await guild.BulkOverwriteApplicationCommandAsync(new[] { command });
        }
        catch (Exception ex)
        {
            _vsLogger.Error($"Failed to register slash commands: {ex.Message}");
        }
    }

    private async Task OnSlashCommand(SocketSlashCommand command)
    {
        try
        {
            if (command.CommandName != "pigeon") return;

            var subCmd = command.Data.Options.FirstOrDefault();
            if (subCmd?.Name == "link")
            {
                await HandleLinkCommand(command, subCmd);
                return;
            }
            if (subCmd?.Name == "list")
            {
                await HandleListCommand(command);
                return;
            }
            if (subCmd?.Name != "send") return;

            var senderId = command.User.Id.ToString();
            if (!_config.PlayerMappings.TryGetValue(senderId, out var senderUid))
            {
                await command.RespondAsync("Your Discord account is not linked to a VS player. Run /pigeon link <your vs username> to link your VS username.", ephemeral: true);
                return;
            }

            var cooldownMinutes = ResolveCooldownMinutes(command.User);
            var remaining = _delivery.GetCooldownRemaining(senderUid, cooldownMinutes);
            if (remaining.HasValue)
            {
                await command.RespondAsync($"You can only send a pigeon once every {cooldownMinutes} minutes! Try again in {(int)remaining.Value.TotalMinutes}m {remaining.Value.Seconds}s.", ephemeral: true);
                return;
            }

            var recipientOption = subCmd.Options?.FirstOrDefault(o => o.Name == "recipient");
            var vsUsernameOption = subCmd.Options?.FirstOrDefault(o => o.Name == "vs_username");

            if (recipientOption == null && vsUsernameOption == null)
            {
                await command.RespondAsync("Provide either a Discord user or a VS username.", ephemeral: true);
                return;
            }

            if (recipientOption != null && vsUsernameOption != null)
            {
                await command.RespondAsync("Provide either a Discord user or a VS username, not both.", ephemeral: true);
                return;
            }

            if (vsUsernameOption != null)
            {
                var vsUsername = (string)vsUsernameOption.Value;
                var modal = new ModalBuilder()
                    .WithTitle("Send Pigeon")
                    .WithCustomId($"pigeon_modal_vs:{senderId}")
                    .AddTextInput("VS Username", "vs_username", value: vsUsername, required: true)
                    .AddTextInput("Title", "title", TextInputStyle.Short, maxLength: 80, required: true)
                    .AddTextInput($"Message (max {_config.PigeonMessageCharacterLimit} characters)", "message", TextInputStyle.Paragraph, maxLength: _config.PigeonMessageCharacterLimit, required: true)
                    .Build();
                await command.RespondWithModalAsync(modal);
                return;
            }

            var recipientUser = (SocketUser)recipientOption!.Value;
            var recipientId = recipientUser.Id.ToString();

            if (!_config.PlayerMappings.ContainsKey(recipientId))
            {
                await command.RespondAsync($"{recipientUser.Mention} is not linked to a VS player.", ephemeral: true);
                return;
            }

            if (!_config.CanPigeonSelf && recipientId == senderId)
            {
                await command.RespondAsync("You cannot send a pigeon to yourself.", ephemeral: true);
                return;
            }

            var discordModal = new ModalBuilder()
                .WithTitle($"Send pigeon to {recipientUser.Username}")
                .WithCustomId($"pigeon_modal:{recipientId}:{senderId}")
                .AddTextInput("Title", "title", TextInputStyle.Short, maxLength: 80, required: true)
                .AddTextInput($"Message (max {_config.PigeonMessageCharacterLimit} characters)", "message", TextInputStyle.Paragraph, maxLength: _config.PigeonMessageCharacterLimit, required: true)
                .Build();

            await command.RespondWithModalAsync(discordModal);
        }
        catch (Exception ex)
        {
            _vsLogger.Error($"Slash command error: {ex.Message}");
        }
    }

    private async Task HandleListCommand(SocketSlashCommand command)
    {
        var subCmd = command.Data.Options.First(o => o.Name == "list");
        var targetUserOption = subCmd.Options?.FirstOrDefault(o => o.Name == "user");

        string targetDiscordId;
        string displayName;

        if (targetUserOption != null)
        {
            if (_config.ModerationRoleId == 0 ||
                command.User is not SocketGuildUser caller ||
                !caller.Roles.Any(r => r.Id == _config.ModerationRoleId))
            {
                await command.RespondAsync("You don't have permission to view other users' pigeons.", ephemeral: true);
                return;
            }

            var targetUser = (SocketUser)targetUserOption.Value;
            targetDiscordId = targetUser.Id.ToString();
            displayName = targetUser.Username;
        }
        else
        {
            targetDiscordId = command.User.Id.ToString();
            displayName = "your";
        }

        if (!_config.PlayerMappings.TryGetValue(targetDiscordId, out var playerUid))
        {
            var msg = targetUserOption != null
                ? "That user is not linked to a VS player."
                : "Your Discord account is not linked to a VS player. Run /pigeon link <your vs username> to link your VS username.";
            await command.RespondAsync(msg, ephemeral: true);
            return;
        }

        const int PageSize = 10;
        var pageOption = subCmd.Options?.FirstOrDefault(o => o.Name == "page");
        var page = pageOption != null ? (int)(long)pageOption.Value : 1;

        var isMod = targetUserOption != null;
        var sent = _delivery.GetSentPigeons(playerUid);
        var received = _delivery.GetReceivedPigeons(playerUid)
            .Where(p => isMod || p.Delivered)
            .ToList();

        if (sent.Count == 0 && received.Count == 0)
        {
            await command.RespondAsync(isMod ? $"{displayName} has no pigeon history." : "You have no pigeon history.", ephemeral: true);
            return;
        }

        var sentTotalPages = Math.Max(1, (int)Math.Ceiling(sent.Count / (double)PageSize));
        var receivedTotalPages = Math.Max(1, (int)Math.Ceiling(received.Count / (double)PageSize));

        if (page > sentTotalPages && page > receivedTotalPages)
        {
            await command.RespondAsync($"Page {page} does not exist.", ephemeral: true);
            return;
        }

        var sentPage = sent.Skip((page - 1) * PageSize).Take(PageSize).ToList();
        var receivedPage = received.Skip((page - 1) * PageSize).Take(PageSize).ToList();

        var overallHeader = isMod ? $"Pigeon history for **{displayName}**:\n" : "";

        var sentEmbeds = sentPage.Select(p => new EmbedBuilder()
            .WithTitle(p.Title)
            .WithDescription(p.Message)
            .WithColor(Color.Gold)
            .WithFooter($"To: {p.RecipientName} - sent {AgeString(p.SentAt)}{(isMod ? p.Delivered ? " - delivered" : " - undelivered" : "")}")
            .Build()).ToArray();

        var receivedEmbeds = receivedPage.Select(p => new EmbedBuilder()
            .WithTitle(p.Title)
            .WithDescription(p.Message)
            .WithColor(Color.Blue)
            .WithFooter($"From: {p.SenderName} - sent {AgeString(p.SentAt)}{(isMod ? p.Delivered ? " - delivered" : " - undelivered" : "")}")
            .Build()).ToArray();

        var sentHeader = $"{overallHeader}**Sent pigeons** (page {page}/{sentTotalPages}):";
        var receivedHeader = $"**Received pigeons** (page {page}/{receivedTotalPages}):";

        if (sentEmbeds.Length > 0)
            await command.RespondAsync(sentHeader, embeds: sentEmbeds, ephemeral: true);
        else
            await command.RespondAsync($"{sentHeader}\nNone.", ephemeral: true);

        if (receivedEmbeds.Length > 0)
            await command.FollowupAsync(receivedHeader, embeds: receivedEmbeds, ephemeral: true);
        else
            await command.FollowupAsync($"{receivedHeader}\nNone.", ephemeral: true);
    }

    private static string AgeString(long unixSeconds)
    {
        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }

    private async Task HandleLinkCommand(SocketSlashCommand command, SocketSlashCommandDataOption subCmd)
    {
        var targetUserOption = subCmd.Options?.FirstOrDefault(o => o.Name == "user");

        if (targetUserOption != null)
        {
            if (_config.ModerationRoleId == 0 ||
                command.User is not SocketGuildUser caller ||
                !caller.Roles.Any(r => r.Id == _config.ModerationRoleId))
            {
                await command.RespondAsync("You don't have permission to link other users' accounts.", ephemeral: true);
                return;
            }
        }
        else if (_config.LinkRoleId != 0 && command.User is SocketGuildUser guildUser &&
                 !guildUser.Roles.Any(r => r.Id == _config.LinkRoleId))
        {
            await command.RespondAsync("You don't have permission to link accounts.", ephemeral: true);
            return;
        }

        var vsName = (string)subCmd.Options.First(o => o.Name == "username").Value;
        var targetUser = targetUserOption != null ? (SocketUser)targetUserOption.Value : command.User;
        var discordId = targetUser.Id.ToString();

        await command.DeferAsync(ephemeral: true);

        _delivery.ResolveVsPlayerUid(vsName, async (uid, error) =>
        {
            if (error != null)
            {
                await command.FollowupAsync(error, ephemeral: true);
                return;
            }
            _delivery.AddMapping(discordId, uid!);
            var msg = targetUserOption != null
                ? $"Linked {targetUser.Mention} to Vintage Story player **{vsName}**."
                : $"Linked to Vintage Story player **{vsName}**.";
            await command.FollowupAsync(msg, ephemeral: true);
        });
    }

    private async Task OnModalSubmitted(SocketModal modal)
    {
        try
        {
            if (modal.Data.CustomId.StartsWith("pigeon_modal_vs:"))
            {
                await HandleVsModalSubmit(modal);
                return;
            }

            if (!modal.Data.CustomId.StartsWith("pigeon_modal:")) return;

            var parts = modal.Data.CustomId.Split(':');
            if (parts.Length != 3) return;

            var recipientId = parts[1];
            var senderId = parts[2];

            if (!_config.PlayerMappings.TryGetValue(senderId, out var senderUid) ||
                !_config.PlayerMappings.TryGetValue(recipientId, out var recipientUid))
            {
                await modal.RespondAsync("Player mapping error. Contact a server admin.", ephemeral: true);
                return;
            }

            var components = modal.Data.Components.ToList();
            var title = components.First(c => c.CustomId == "title").Value;
            var message = components.First(c => c.CustomId == "message").Value;

            var cooldownMinutes = ResolveCooldownMinutes(modal.User);
            var remaining = _delivery.GetCooldownRemaining(senderUid, cooldownMinutes);
            if (remaining.HasValue)
            {
                await modal.RespondAsync($"You can only send a pigeon once every {cooldownMinutes} minutes! Try again in {(int)remaining.Value.TotalMinutes}m {remaining.Value.Seconds}s.", ephemeral: true);
                return;
            }

            _delivery.SendPigeon(senderUid, recipientUid, title, message,
                () => _ = NotifyRecipientAsync(recipientId),
                () => _ = DmPigeonContentAsync(recipientId, title, message));
            await modal.RespondAsync($"Pigeon sent to {MentionUtils.MentionUser(ulong.Parse(recipientId))}!", ephemeral: true);

            await SendAuditEmbed(title, message, modal.User.Mention, MentionUtils.MentionUser(ulong.Parse(recipientId)));
        }
        catch (Exception ex)
        {
            _vsLogger.Error($"Modal submit error: {ex.Message}");
        }
    }

    private async Task HandleVsModalSubmit(SocketModal modal)
    {
        var parts = modal.Data.CustomId.Split(':', 2);
        if (parts.Length != 2) return;

        var senderId = parts[1];

        if (!_config.PlayerMappings.TryGetValue(senderId, out var senderUid))
        {
            await modal.RespondAsync("Player mapping error. Contact a server admin.", ephemeral: true);
            return;
        }

        var components = modal.Data.Components.ToList();
        var vsUsername = components.First(c => c.CustomId == "vs_username").Value;
        var title = components.First(c => c.CustomId == "title").Value;
        var message = components.First(c => c.CustomId == "message").Value;

        var cooldownMinutes = ResolveCooldownMinutes(modal.User);
        var remaining = _delivery.GetCooldownRemaining(senderUid, cooldownMinutes);
        if (remaining.HasValue)
        {
            await modal.RespondAsync($"You can only send a pigeon once every {cooldownMinutes} minutes! Try again in {(int)remaining.Value.TotalMinutes}m {remaining.Value.Seconds}s.", ephemeral: true);
            return;
        }

        await modal.DeferAsync(ephemeral: true);

        _delivery.ResolveVsPlayerUid(vsUsername, async (uid, error) =>
        {
            if (error != null)
            {
                await modal.FollowupAsync(error, ephemeral: true);
                return;
            }

            if (!_config.CanPigeonSelf && uid == senderUid)
            {
                await modal.FollowupAsync("You cannot send a pigeon to yourself.", ephemeral: true);
                return;
            }

            var recipientDiscordId = _config.PlayerMappings.FirstOrDefault(kv => kv.Value == uid).Key;
            Action? onEnRoute = !string.IsNullOrEmpty(recipientDiscordId)
                ? () => _ = NotifyRecipientAsync(recipientDiscordId)
                : null;
            Action? onDmFallback = !string.IsNullOrEmpty(recipientDiscordId)
                ? () => _ = DmPigeonContentAsync(recipientDiscordId, title, message)
                : null;
            _delivery.SendPigeon(senderUid, uid!, title, message, onEnRoute, onDmFallback);
            await modal.FollowupAsync($"Pigeon sent to **{vsUsername}**!", ephemeral: true);

            await SendAuditEmbed(title, message, modal.User.Mention, vsUsername);
        });
    }

    private int ResolveCooldownMinutes(IUser user)
    {
        var cooldownMinutes = _config.PigeonCooldownMinutes;
        if (user is SocketGuildUser guildUser && _config.PigeonCooldownMinutesPerRoleOverride.Count > 0)
        {
            foreach (var role in guildUser.Roles)
            {
                if (_config.PigeonCooldownMinutesPerRoleOverride.TryGetValue(role.Id, out var roleCooldown))
                    cooldownMinutes = Math.Min(cooldownMinutes, roleCooldown);
            }
        }
        return cooldownMinutes;
    }

    private async Task NotifyRecipientAsync(string discordId)
    {
        if (_client == null || !ulong.TryParse(discordId, out var userId)) return;
        try
        {
            var user = await _client.GetUserAsync(userId);
            if (user == null) return;
            var dm = await user.CreateDMChannelAsync();
            await dm.SendMessageAsync("A pigeon carrying a message is looking for you. Log into the server to receive it.");
        }
        catch (Exception ex)
        {
            _vsLogger.Error($"Failed to DM {discordId}: {ex.Message}");
        }
    }

    private async Task DmPigeonContentAsync(string discordId, string title, string message)
    {
        if (_client == null || !ulong.TryParse(discordId, out var userId)) return;
        try
        {
            var user = await _client.GetUserAsync(userId);
            if (user == null) return;
            var dm = await user.CreateDMChannelAsync();
            var embed = new EmbedBuilder()
                .WithTitle(title)
                .WithDescription(message)
                .WithColor(Color.Gold)
                .WithFooter("This is a late delivery for a pigeon you weren't around to receive for an extended time. You can still receive it ingame once you log on.")
                .Build();
            await dm.SendMessageAsync(embed: embed);
        }
        catch (Exception ex)
        {
            _vsLogger.Error($"Failed to DM pigeon content to {discordId}: {ex.Message}");
        }
    }

    private async Task SendAuditEmbed(string title, string message, string fromMention, string toDisplay)
    {
        if (_config.AuditChannelId == 0 || _client?.GetChannel(_config.AuditChannelId) is not IMessageChannel auditChannel)
            return;
        var embed = new EmbedBuilder()
            .WithTitle("A pigeon has just been sent!")
            .WithDescription($"**{title}**\n\n{message}")
            .WithColor(Color.Gold)
            .AddField("From", fromMention, true)
            .AddField("To", toDisplay, true)
            .WithCurrentTimestamp()
            .Build();
        await auditChannel.SendMessageAsync(embed: embed);
    }
}
