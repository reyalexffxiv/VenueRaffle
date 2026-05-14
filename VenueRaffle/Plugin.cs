using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Dalamud.Game.Chat;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using VenueRaffle.Services;
using VenueRaffle.Windows;

namespace VenueRaffle;

/// <summary>
/// Dalamud plugin entry point.
///
/// This class wires together UI windows, chat command handling, raffle business logic,
/// local reliable storage, and chat command handling. Keep heavy logic in services
/// so the plugin entry point remains easy to scan.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    // Dalamud services are injected once at plugin startup and reused by the UI/services.
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IReliableFileStorage ReliableFileStorage { get; private set; } = null!;

    // Keep both commands registered so existing users can keep using /venueraffle while /raffle works as a short alias.
    private static readonly string[] CommandNames = ["/venueraffle", "/raffle"];

    private static readonly Regex OwnRandomRollRegex = new(
        @"(?:Random!\s*)?You\s+roll\s+an?\D*(?<roll>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private bool waitingForWinnerRandom;

    /// <summary>Saved plugin configuration, including UI state and raffle settings.</summary>
    public Configuration Configuration { get; }

    /// <summary>In-memory raffle totals and entry operations.</summary>
    public RaffleSession RaffleSession { get; }

    /// <summary>Persistent storage wrapper used to reload entries after restarts.</summary>
    public RaffleStateStorage RaffleStateStorage { get; }

    /// <summary>Queues safe, rate-limited FFXIV chat commands.</summary>
    public GameChatService GameChatService { get; }


    public readonly WindowSystem WindowSystem = new("VenueRaffle");

    private ConfigWindow ConfigWindow { get; }

    private MainWindow MainWindow { get; }

    public Plugin()
    {
        this.Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.Configuration.Initialize(PluginInterface);
        if (this.Configuration.CleanLegacyManualWaitLinesOnce())
        {
            this.Configuration.Save();
            Log.Information("Cleaned legacy manual <wait.N> lines from VenueRaffle macros. Macro wait boxes now handle spacing.");
        }


        this.RaffleStateStorage = new RaffleStateStorage(PluginInterface, ReliableFileStorage, Log);
        this.RaffleStateStorage.LoadInto(this.Configuration);

        this.GameChatService = new GameChatService(Log);
        this.RaffleSession = new RaffleSession(this.Configuration, this.RaffleStateStorage);

        this.ConfigWindow = new ConfigWindow(this);
        this.MainWindow = new MainWindow(this);

        this.WindowSystem.AddWindow(this.ConfigWindow);
        this.WindowSystem.AddWindow(this.MainWindow);

        foreach (var commandName in CommandNames)
        {
            CommandManager.AddHandler(commandName, new CommandInfo(this.OnCommand)
            {
                HelpMessage = "Open the Venue Raffle ticket tracker."
            });
        }

        Framework.Update += this.OnFrameworkUpdate;

        PluginInterface.UiBuilder.Draw += this.WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += this.ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += this.ToggleMainUi;

        ChatGui.ChatMessage += this.OnChatMessage;

        Log.Information("VenueRaffle loaded.");
    }

    public void Dispose()
    {
        ChatGui.ChatMessage -= this.OnChatMessage;

        PluginInterface.UiBuilder.Draw -= this.WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= this.ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= this.ToggleMainUi;

        Framework.Update -= this.OnFrameworkUpdate;

        foreach (var commandName in CommandNames)
        {
            CommandManager.RemoveHandler(commandName);
        }

        this.WindowSystem.RemoveAllWindows();

        this.ConfigWindow.Dispose();
        this.MainWindow.Dispose();
    }


    /// <summary>Flushes at most one queued chat command per framework tick.</summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        this.GameChatService.FlushOnePendingCommand();
    }

    /// <summary>Handles /venueraffle and /raffle subcommands without printing chat spam.</summary>
    private void OnCommand(string command, string args)
    {
        var trimmedArgs = args.Trim().ToLowerInvariant();

        switch (trimmedArgs)
        {
            case "reset":
                this.ResetRaffle();
                break;

            case "tell":
                this.TellLastBuyerTickets();
                break;

            case "announce":
                this.ShoutRaffleAnnouncement();
                break;

            case "draw":
                this.QueueWinnerDraw();
                break;

            case "target":
                this.SetTargetFromCurrentGameTarget();
                break;

            default:
                this.MainWindow.Toggle();
                break;
        }
    }

    /// <summary>
    /// Watches chat only while a winner draw is pending. When our /random result appears,
    /// the rolled ticket number is matched against the current raffle entries.
    /// </summary>
    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (!this.waitingForWinnerRandom)
            return;

        var messageText = CleanChatText(message.Message.TextValue);
        var ownRollMatch = OwnRandomRollRegex.Match(messageText);

        if (!ownRollMatch.Success || !int.TryParse(ownRollMatch.Groups["roll"].Value, out var winningTicket))
            return;

        this.waitingForWinnerRandom = false;
        this.AnnounceWinner(winningTicket);
    }

    /// <summary>Copies the current in-game target name into the main sale name field.</summary>
    public void SetTargetFromCurrentGameTarget()
    {
        var targetName = GetCurrentGameTargetName();

        if (string.IsNullOrWhiteSpace(targetName))
        {
            return;
        }

        this.Configuration.TargetPlayerName = targetName;
        this.Configuration.Save();
    }

    /// <summary>Reads the current target name defensively because Dalamud target data can be null between frames.</summary>
    private static string GetCurrentGameTargetName()
    {
        try
        {
            return TargetManager.Target?.Name.TextValue.Trim() ?? string.Empty;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read current game target name.");
            return string.Empty;
        }
    }

    /// <summary>Adds tickets for the name currently typed in the main window.</summary>
    public void AddTicketsForConfiguredTarget(int tickets)
    {
        if (this.RaffleSession.TryAddTicketSale(this.Configuration.TargetPlayerName, tickets, out var sale, out var error))
        {
            return;
        }
        this.MainWindow.IsOpen = true;
    }

    /// <summary>Sends the most recent sale's ticket range to the current in-game target.</summary>
    public void TellLastBuyerTickets()
    {
        var sale = this.RaffleSession.LastSale;

        if (sale is null)
        {
            return;
        }

        this.TellTicketSale(sale);
    }

    /// <summary>Sends the most recent sale using the saved buyer name in the tell template.</summary>
    public void TellLastBuyerTicketsWithName()
    {
        var sale = this.RaffleSession.LastSale;

        if (sale is null)
        {
            return;
        }

        this.TellTicketSale(sale, sale.PlayerName);
    }

    /// <summary>Sends the most recent sale with a manually supplied ticket owner name.</summary>
    public void TellLastBuyerTicketsForNamedPerson(string playerName)
    {
        var sale = this.RaffleSession.LastSale;

        if (sale is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(playerName))
        {
            return;
        }

        this.TellTicketSale(sale, playerName.Trim());
    }

    /// <summary>
    /// Resends the selected raffle entry ticket range to the current in-game target.
    /// Used by the Statistics tab when staff need to resend an older purchase later.
    /// </summary>
    public void TellTicketSaleToCurrentTarget(Models.RaffleSaleRecord sale)
    {

        this.TellTicketSale(sale);
    }

    private void TellTicketSale(Models.RaffleSaleRecord sale, string? namedTicketOwner = null)
    {
        var template = string.IsNullOrWhiteSpace(namedTicketOwner)
            ? this.Configuration.TellTargetMessageTemplate
            : this.Configuration.TellOtherNameMessageTemplate;

        var message = this.RenderMacro(template, sale, namedTicketOwner);

        this.GameChatService.QueueTellCurrentTarget(message);
    }

    /// <summary>Queues the configured raffle advertisement macro.</summary>
    public void ShoutRaffleAnnouncement()
    {
        var macro = this.RenderMacro(this.Configuration.RaffleAdMacro);

        this.GameChatService.QueueMacroCommands(macro, TimeSpan.FromSeconds(this.Configuration.RaffleAdDelaySeconds));
    }

    /// <summary>Queues the countdown/random macro and starts listening for the resulting roll.</summary>
    public void QueueWinnerDraw()
    {
        var totalTickets = this.RaffleSession.TotalTickets;

        if (totalTickets <= 0)
        {
            return;
        }

        if (totalTickets > Services.RaffleSession.MaxTotalTickets)
        {
            return;
        }

        this.waitingForWinnerRandom = true;

        var macro = this.RenderMacro(this.Configuration.WinnerDrawMacro);
        if (!macro.Contains("/random", StringComparison.OrdinalIgnoreCase))
            macro += $"\n/random {totalTickets}";

        this.GameChatService.QueueMacroCommands(macro, TimeSpan.FromSeconds(this.Configuration.WinnerDrawDelaySeconds));
    }

    private void AnnounceWinner(int winningTicket)
    {
        var sale = this.RaffleSession.FindTicket(winningTicket);

        if (sale is null)
        {
            var notFound = $"Winning ticket {winningTicket:N0} was not found in the raffle entries.";
            this.GameChatService.QueueShout(notFound);
            return;
        }

        var macro = this.RenderMacro(this.Configuration.WinnerResultMacro, sale, winningTicket: winningTicket);
        this.GameChatService.QueueMacroCommands(macro, TimeSpan.FromSeconds(this.Configuration.WinnerResultDelaySeconds));
    }

    /// <summary>
    /// Replaces user-facing macro variables such as {Pot} and {WinningTicket}.
    /// Unknown text is left untouched so venue owners can freely write their own messages.
    /// </summary>
    private string RenderMacro(string? template, Models.RaffleSaleRecord? sale = null, string? otherName = null, int? winningTicket = null)
    {
        var maxTicketsText = this.Configuration.MaxTicketsPerPurchase > 0
            ? this.Configuration.MaxTicketsPerPurchase.ToString(CultureInfo.InvariantCulture)
            : "unlimited";

        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{Pot}"] = FormatGilFull(this.RaffleSession.TotalPot),
            ["{PricePerTicket}"] = FormatGilFull(this.Configuration.PricePerTicket),
            ["{MaxTickets}"] = maxTicketsText,
            ["{TotalTickets}"] = this.RaffleSession.TotalTickets.ToString(CultureInfo.InvariantCulture),
            ["{TicketSales}"] = FormatGilFull(this.RaffleSession.GrossTicketSalesTotalGil),
            ["{GrossTicketSales}"] = FormatGilFull(this.RaffleSession.GrossTicketSalesTotalGil),
            ["{PrizeTicketSales}"] = FormatGilFull(this.RaffleSession.PrizeTicketSalesGil),
            ["{VenueHostPercent}"] = this.RaffleSession.VenueHostSalesPercent.ToString("0.#", CultureInfo.InvariantCulture),
            ["{VenueHostShare}"] = FormatGilFull(this.RaffleSession.VenueHostShareGil),
            ["{Entries}"] = this.RaffleSession.EntryCount.ToString(CultureInfo.InvariantCulture),
            ["{TargetName}"] = this.Configuration.TargetPlayerName ?? string.Empty,
            ["{OtherName}"] = otherName ?? string.Empty,
            ["{WinnerName}"] = sale?.PlayerName ?? string.Empty,
            ["{WinningTicket}"] = winningTicket?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            ["{TicketRange}"] = sale?.TicketRange ?? string.Empty
        };

        var result = template ?? string.Empty;

        foreach (var replacement in replacements)
            result = result.Replace(replacement.Key, replacement.Value, StringComparison.OrdinalIgnoreCase);

        return result;
    }

    /// <summary>Formats gil values with full thousands separators, avoiding compact rounding surprises.</summary>
    private static string FormatGilFull(long gil)
    {
        return gil.ToString("N0", CultureInfo.InvariantCulture);
    }

    /// <summary>Normalizes FFXIV chat glyph noise before parsing /random output.</summary>
    private static string CleanChatText(string text)
    {
        return text
            .Replace('\u00A0', ' ')
            .Replace("", " ")
            .Replace("", " ")
            .Replace("", " ")
            .Replace("", " ")
            .Replace("", " ")
            .Replace("", " ")
            .Trim();
    }

    /// <summary>Marks the current raffle as active.</summary>
    public void StartRaffle()
    {
        this.RaffleSession.Start();
    }

    /// <summary>Marks the current raffle as inactive.</summary>
    public void StopRaffle()
    {
        this.RaffleSession.Stop();
    }

    /// <summary>Clears all current raffle entries and resets totals.</summary>
    public void ResetRaffle()
    {
        this.RaffleSession.Reset();
    }

    /// <summary>Opens or closes the settings window.</summary>
    public void ToggleConfigUi() => this.ConfigWindow.Toggle();

    /// <summary>Opens or closes the main raffle window.</summary>
    public void ToggleMainUi() => this.MainWindow.Toggle();
}
