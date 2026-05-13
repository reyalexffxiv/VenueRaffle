using System.Collections.Generic;
using VenueRaffle.Models;

namespace VenueRaffle.Services;

/// <summary>
/// Persistent raffle state saved outside the normal plugin settings file.
/// </summary>
public sealed class RafflePersistentState
{
    public int Version { get; set; } = 1;

    public List<RaffleSaleRecord> SalesLedger { get; set; } = new();

    public bool ActiveSessionWasRunning { get; set; }

    public string ActiveSessionLastNotice { get; set; } = string.Empty;
}
