using System;

namespace VenueRaffle.Models;

/// <summary>
/// One raffle ticket purchase entry.
/// </summary>
[Serializable]
/// <summary>
/// One visible raffle entry row. Ranges are rebuilt after deletes so tickets remain contiguous.
/// </summary>
public sealed class RaffleSaleRecord
{
    public string PlayerName { get; set; } = string.Empty;

    public int Tickets { get; set; }

    public int StartTicket { get; set; }

    public int EndTicket { get; set; }

    public long Gil { get; set; }

    public DateTime CreatedAtLocal { get; set; } = DateTime.Now;

    public string TicketRange => this.StartTicket == this.EndTicket
        ? this.StartTicket.ToString()
        : $"{this.StartTicket} - {this.EndTicket}";
}
