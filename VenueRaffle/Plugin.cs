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
/// local reliable storage, and offline license validation. Keep heavy logic in services
/// so the plugin entry point remains easy to scan.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IReliableFileStorage ReliableFileStorage { get; private set; } = null!;

    private static readonly string[] CommandNames = ["/venueraffle", "/raffle"];

    private static readonly Regex OwnRandomRollRegex = new(
        @"(?:Random!\s*)?You\s+roll\s+an?\D*(?<roll>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private bool waitingForWinnerRandom;

    public Configuration Configuration { get; }

    public RaffleSession RaffleSession { get; }

    public RaffleStateStorage RaffleStateStorage { get; }

    public GameChatService GameChatService { get; }

    public OfflineLicenseService LicenseService { get; }

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

        this.LicenseService = new OfflineLicenseService(this.Configuration, Log);

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

    private bool EnsureLicensed()
    {
        if (this.LicenseService.IsLicensed)
            return true;

        ChatGui.PrintError("[VenueRaffle] License required. Open Settings > License and paste your install-bound license text.");
        this.ConfigWindow.IsOpen = true;
        return false;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        this.GameChatService.FlushOnePendingCommand();
    }

    private void OnCommand(string command, string args)
    {
        var trimmedArgs = args.Trim().ToLowerInvariant();

        switch (trimmedArgs)
        {
            case "reset":
                if (!this.EnsureLicensed()) break;
                this.ResetRaffle();
                break;

            case "status":
                this.PrintStatus();
                break;

            case "tell":
                if (!this.EnsureLicensed()) break;
                this.TellLastBuyerTickets();
                break;

            case "announce":
                if (!this.EnsureLicensed()) break;
                this.ShoutRaffleAnnouncement();
                break;

            case "draw":
                if (!this.EnsureLicensed()) break;
                this.QueueWinnerDraw();
                break;

            case "target":
                if (!this.EnsureLicensed()) break;
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

    public void SetTargetFromCurrentGameTarget()
    {
        if (!this.EnsureLicensed())
            return;

        var targetName = GetCurrentGameTargetName();

        if (string.IsNullOrWhiteSpace(targetName))
        {
            ChatGui.PrintError("[VenueRaffle] No valid target selected.");
            return;
        }

        this.Configuration.TargetPlayerName = targetName;
        this.Configuration.Save();

        ChatGui.Print($"[VenueRaffle] Target player set to {targetName}.");
    }

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

    public void AddTicketsForConfiguredTarget(int tickets)
    {
        if (!this.EnsureLicensed())
            return;

        if (this.RaffleSession.TryAddTicketSale(this.Configuration.TargetPlayerName, tickets, out var sale, out var error))
        {
            ChatGui.Print(
                $"[VenueRaffle] {sale!.PlayerName}: tickets {sale.TicketRange}. " +
                $"Cost: {sale.Gil:N0} gil. Pot: {this.RaffleSession.TotalPot:N0} gil.");

            ChatGui.Print("[VenueRaffle] Use the /tell Target button when you want to message the current target their tickets.");
            return;
        }

        ChatGui.PrintError($"[VenueRaffle] {error}");
        this.MainWindow.IsOpen = true;
    }

    public void TellLastBuyerTickets()
    {
        if (!this.EnsureLicensed())
            return;

        var sale = this.RaffleSession.LastSale;

        if (sale is null)
        {
            ChatGui.PrintError("[VenueRaffle] There is no last sale to tell yet.");
            return;
        }

        this.TellTicketSale(sale);
    }

    public void TellLastBuyerTicketsWithName()
    {
        if (!this.EnsureLicensed())
            return;

        var sale = this.RaffleSession.LastSale;

        if (sale is null)
        {
            ChatGui.PrintError("[VenueRaffle] There is no last sale to tell yet.");
            return;
        }

        this.TellTicketSale(sale, sale.PlayerName);
    }

    public void TellLastBuyerTicketsForNamedPerson(string playerName)
    {
        if (!this.EnsureLicensed())
            return;

        var sale = this.RaffleSession.LastSale;

        if (sale is null)
        {
            ChatGui.PrintError("[VenueRaffle] There is no last sale to tell yet.");
            return;
        }

        if (string.IsNullOrWhiteSpace(playerName))
        {
            ChatGui.PrintError("[VenueRaffle] Enter a name before using the named tickets tell button.");
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
        if (!this.EnsureLicensed())
            return;

        this.TellTicketSale(sale);
    }

    private void TellTicketSale(Models.RaffleSaleRecord sale, string? namedTicketOwner = null)
    {
        var template = string.IsNullOrWhiteSpace(namedTicketOwner)
            ? this.Configuration.TellTargetMessageTemplate
            : this.Configuration.TellOtherNameMessageTemplate;

        var message = this.RenderMacro(template, sale, namedTicketOwner);

        this.GameChatService.QueueTellCurrentTarget(message);
        ChatGui.Print($"[VenueRaffle] Queued /tell <t>: {message}");
    }

    public void ShoutRaffleAnnouncement()
    {
        if (!this.EnsureLicensed())
            return;

        var macro = this.RenderMacro(this.Configuration.RaffleAdMacro);

        this.GameChatService.QueueMacroCommands(macro, TimeSpan.FromSeconds(this.Configuration.RaffleAdDelaySeconds));
        ChatGui.Print($"[VenueRaffle] Queued raffle ad macro. Price: {FormatGilFull(this.Configuration.PricePerTicket)} per ticket. Prize: {FormatGilFull(this.RaffleSession.TotalPot)}.");
    }

    public void QueueWinnerDraw()
    {
        if (!this.EnsureLicensed())
            return;

        var totalTickets = this.RaffleSession.TotalTickets;

        if (totalTickets <= 0)
        {
            ChatGui.PrintError("[VenueRaffle] Cannot draw a winner because there are no tickets yet.");
            return;
        }

        if (totalTickets > Services.RaffleSession.MaxTotalTickets)
        {
            ChatGui.PrintError($"[VenueRaffle] Cannot draw winner: total tickets are {totalTickets:N0}, but FFXIV /random is capped at {Services.RaffleSession.MaxTotalTickets:N0}.");
            return;
        }

        this.waitingForWinnerRandom = true;

        var macro = this.RenderMacro(this.Configuration.WinnerDrawMacro);
        if (!macro.Contains("/random", StringComparison.OrdinalIgnoreCase))
            macro += $"\n/random {totalTickets}";

        this.GameChatService.QueueMacroCommands(macro, TimeSpan.FromSeconds(this.Configuration.WinnerDrawDelaySeconds));

        ChatGui.Print($"[VenueRaffle] Queued winner draw macro and /random {totalTickets:N0}.");
    }

    private void AnnounceWinner(int winningTicket)
    {
        var sale = this.RaffleSession.FindTicket(winningTicket);

        if (sale is null)
        {
            var notFound = $"Winning ticket {winningTicket:N0} was not found in the raffle entries.";
            ChatGui.PrintError($"[VenueRaffle] {notFound}");
            this.GameChatService.QueueShout(notFound);
            return;
        }

        var prizeText = FormatGilFull(this.RaffleSession.TotalPot);
        var macro = this.RenderMacro(this.Configuration.WinnerResultMacro, sale, winningTicket: winningTicket);

        ChatGui.Print($"[VenueRaffle] Winner is {sale.PlayerName} with ticket number {winningTicket:N0}. Range: {sale.TicketRange}. Prize: {prizeText} Gil.");
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

    private static string FormatGilFull(long gil)
    {
        return gil.ToString("N0", CultureInfo.InvariantCulture);
    }

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

    public void StartRaffle()
    {
        if (!this.EnsureLicensed())
            return;

        this.RaffleSession.Start();
        ChatGui.Print("[VenueRaffle] Raffle tracking started.");
    }

    public void StopRaffle()
    {
        if (!this.EnsureLicensed())
            return;

        this.RaffleSession.Stop();
        ChatGui.Print("[VenueRaffle] Raffle tracking stopped.");
    }

    public void ResetRaffle()
    {
        if (!this.EnsureLicensed())
            return;

        this.RaffleSession.Reset();
        ChatGui.Print("[VenueRaffle] Raffle entries cleared.");
    }

    public void PrintStatus()
    {
        ChatGui.Print($"[VenueRaffle] Target: {this.Configuration.TargetPlayerName}");
        ChatGui.Print($"[VenueRaffle] Entries: {this.RaffleSession.EntryCount:N0}, Tickets: {this.RaffleSession.TotalTickets:N0}");
        ChatGui.Print($"[VenueRaffle] Pot: {this.RaffleSession.TotalPot:N0} gil, Ticket sales: {this.RaffleSession.GrossTicketSalesTotalGil:N0} gil, Venue/host share: {this.RaffleSession.VenueHostShareGil:N0} gil.");
    }

    public void ToggleConfigUi() => this.ConfigWindow.Toggle();

    public void ToggleMainUi() => this.MainWindow.Toggle();
}
