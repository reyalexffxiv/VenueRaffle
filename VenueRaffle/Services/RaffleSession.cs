using System;
using System.Linq;
using VenueRaffle.Models;

namespace VenueRaffle.Services;

/// <summary>
/// Contains the raffle ticket business logic.
///
/// UI code should call this service for any operation that changes ticket entries,
/// totals, or ticket ranges. This keeps calculations in one place and prevents
/// different windows from drifting out of sync.
/// </summary>
public sealed class RaffleSession
{
    /// <summary>FFXIV /random cannot roll above 999, so the whole raffle is capped here.</summary>
    public const int MaxTotalTickets = 999;

    private readonly Configuration configuration;
    private readonly RaffleStateStorage stateStorage;

    /// <summary>Creates the raffle service from saved configuration and persistent state storage.</summary>
    public RaffleSession(Configuration configuration, RaffleStateStorage stateStorage)
    {
        this.configuration = configuration;
        this.stateStorage = stateStorage;
        this.IsRunning = configuration.ActiveSessionWasRunning;
        this.LastNotice = configuration.ActiveSessionLastNotice ?? string.Empty;
    }

    /// <summary>Whether the current raffle session is marked as active.</summary>
    public bool IsRunning { get; private set; }

    public string LastNotice { get; private set; } = string.Empty;

    public bool HasTargetPlayer => !string.IsNullOrWhiteSpace(this.configuration.TargetPlayerName);

    /// <summary>Number of visible purchase rows in the raffle entries table.</summary>
    public int EntryCount => this.configuration.SalesLedger.Count;

    /// <summary>Total tickets currently sold across all entries.</summary>
    public int TotalTickets => this.configuration.SalesLedger.Sum(sale => Math.Max(0, sale.Tickets));

    public long GrossTicketSalesTotalGil => this.configuration.SalesLedger.Sum(sale => Math.Max(0, sale.Gil));

    public float VenueHostSalesPercent => Math.Clamp(this.configuration.VenueHostSalesPercent, 0f, 100f);

    public long VenueHostShareGil => CalculatePercentShare(this.GrossTicketSalesTotalGil, this.VenueHostSalesPercent);

    public long PrizeTicketSalesGil => Math.Max(0, this.GrossTicketSalesTotalGil - this.VenueHostShareGil);

    // Backwards-compatible name used by older UI/macro code. This is now the ticket-sales amount that contributes to the prize.
    public long TicketSalesTotalGil => this.PrizeTicketSalesGil;

    /// <summary>Full prize pot after base pot, ticket sales, and host share are applied.</summary>
    public long TotalPot => this.configuration.BasePot + this.PrizeTicketSalesGil;

    public int RemainingTickets => Math.Max(0, MaxTotalTickets - this.TotalTickets);

    public RaffleSaleRecord? LastSale => this.configuration.SalesLedger.LastOrDefault();

    /// <summary>Marks the raffle as running without changing entries.</summary>
    public void Start()
    {
        this.IsRunning = true;
        this.LastNotice = "Raffle session started.";
        this.SaveActiveSessionState();
    }

    /// <summary>Marks the raffle as stopped without changing entries.</summary>
    public void Stop()
    {
        this.IsRunning = false;
        this.LastNotice = "Raffle session stopped.";
        this.SaveActiveSessionState();
    }

    /// <summary>
    /// Starts a fresh raffle by clearing all ticket sales.
    /// </summary>
    public void Reset()
    {
        this.IsRunning = false;
        this.configuration.SalesLedger.Clear();
        this.LastNotice = "Raffle session reset. Raffle entries cleared.";
        this.SaveActiveSessionState();
    }

    /// <summary>
    /// Adds one purchase to the raffle entries and assigns the next continuous ticket range.
    /// The raffle is hard-capped at 999 tickets because FFXIV /random cannot roll higher.
    /// </summary>
    public bool TryAddTicketSale(string playerName, int tickets, out RaffleSaleRecord? sale, out string error)
    {
        sale = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(playerName))
        {
            error = "Select or type a target player first.";
            this.LastNotice = error;
            this.SaveActiveSessionState();
            return false;
        }

        if (tickets <= 0)
        {
            error = "Ticket amount must be at least 1.";
            this.LastNotice = error;
            this.SaveActiveSessionState();
            return false;
        }

        if (this.configuration.MaxTicketsPerPurchase > 0 && tickets > this.configuration.MaxTicketsPerPurchase)
        {
            error = $"Ticket amount cannot exceed {this.configuration.MaxTicketsPerPurchase:N0}.";
            this.LastNotice = error;
            this.SaveActiveSessionState();
            return false;
        }

        var currentTotalTickets = this.TotalTickets;
        var remainingTickets = MaxTotalTickets - currentTotalTickets;

        if (remainingTickets <= 0)
        {
            error = $"Raffle is full. The hard ticket limit is {MaxTotalTickets:N0} because FFXIV /random only supports up to 999.";
            this.LastNotice = error;
            this.SaveActiveSessionState();
            return false;
        }

        if (tickets > remainingTickets)
        {
            error = $"Only {remainingTickets:N0} ticket(s) are available before the raffle reaches the {MaxTotalTickets:N0} ticket limit.";
            this.LastNotice = error;
            this.SaveActiveSessionState();
            return false;
        }

        var startTicket = currentTotalTickets + 1;
        var endTicket = startTicket + tickets - 1;
        var cleanPlayerName = playerName.Trim();
        var gil = this.configuration.PricePerTicket * tickets;

        sale = new RaffleSaleRecord
        {
            PlayerName = cleanPlayerName,
            Tickets = tickets,
            StartTicket = startTicket,
            EndTicket = endTicket,
            Gil = gil,
            CreatedAtLocal = DateTime.Now
        };

        this.configuration.SalesLedger.Add(sale);
        this.LastNotice = $"{cleanPlayerName} received tickets {sale.TicketRange}. Cost: {gil:N0} gil.";
        this.SaveActiveSessionState();
        return true;
    }

    /// <summary>Removes the most recent sale entry, used by the Undo Last Sale button.</summary>
    public void RemoveLastSale()
    {
        if (this.configuration.SalesLedger.Count <= 0)
        {
            this.LastNotice = "No ticket sales to remove.";
            this.SaveActiveSessionState();
            return;
        }

        this.RemoveSaleAt(this.configuration.SalesLedger.Count - 1);
    }

    /// <summary>Removes one sale entry by table index and then rebuilds ticket ranges.</summary>
    public void RemoveSaleAt(int index)
    {
        if (index < 0 || index >= this.configuration.SalesLedger.Count)
        {
            this.LastNotice = "Ticket sale could not be removed because the row no longer exists.";
            this.SaveActiveSessionState();
            return;
        }

        var sale = this.configuration.SalesLedger[index];
        this.configuration.SalesLedger.RemoveAt(index);
        this.RebuildTicketRanges();

        this.LastNotice = $"Removed ticket sale: {sale.Tickets:N0} ticket(s) for {sale.PlayerName}.";
        this.SaveActiveSessionState();
    }

    /// <summary>Deletes every raffle entry after user confirmation.</summary>
    public void ClearSalesLedger()
    {
        this.configuration.SalesLedger.Clear();
        this.LastNotice = "Raffle entries cleared.";
        this.SaveActiveSessionState();
    }

    /// <summary>Finds the entry that owns a specific ticket number.</summary>
    public RaffleSaleRecord? FindTicket(int ticketNumber)
    {
        if (ticketNumber <= 0)
            return null;

        return this.configuration.SalesLedger.FirstOrDefault(
            sale => ticketNumber >= sale.StartTicket && ticketNumber <= sale.EndTicket);
    }

    /// <summary>Calculates a rounded gil share for venue/host cuts.</summary>
    private static long CalculatePercentShare(long grossGil, float percent)
    {
        if (grossGil <= 0 || percent <= 0f)
            return 0;

        if (percent >= 100f)
            return grossGil;

        return (long)Math.Round(grossGil * (percent / 100.0), MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Rebuilds ranges after deleting an entry so every remaining ticket number stays continuous.
    /// Example: deleting 41-80 makes the next entry become 41-80 instead of leaving a gap.
    /// </summary>
    private void RebuildTicketRanges()
    {
        var nextTicket = 1;

        foreach (var sale in this.configuration.SalesLedger)
        {
            var ticketCount = Math.Max(0, sale.Tickets);
            sale.StartTicket = nextTicket;
            sale.EndTicket = ticketCount <= 0 ? nextTicket : nextTicket + ticketCount - 1;
            sale.Gil = this.configuration.PricePerTicket * ticketCount;
            nextTicket += ticketCount;
        }
    }

    private void SaveActiveSessionState()
    {
        this.configuration.ActiveSessionWasRunning = this.IsRunning;
        this.configuration.ActiveSessionLastNotice = this.LastNotice ?? string.Empty;
        this.configuration.Save();
        this.stateStorage.Save(this.configuration);
    }
}
