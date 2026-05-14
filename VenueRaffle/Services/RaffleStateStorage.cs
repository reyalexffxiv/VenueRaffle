using System;
using System.IO;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace VenueRaffle.Services;

/// <summary>
/// Saves and loads the live raffle ledger through Dalamud's reliable file storage service.
///
/// This gives the raffle state its own JSON file while using Dalamud's atomic/reliable
/// storage layer, including the backup copy that IReliableFileStorage maintains.
/// </summary>
public sealed class RaffleStateStorage
{
    private const string StateFileName = "raffle-state.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IReliableFileStorage reliableFileStorage;
    private readonly IPluginLog log;
    private readonly string stateFilePath;

    /// <summary>Builds the storage path under Dalamud's plugin configuration directory.</summary>
    public RaffleStateStorage(
        IDalamudPluginInterface pluginInterface,
        IReliableFileStorage reliableFileStorage,
        IPluginLog log)
    {
        this.reliableFileStorage = reliableFileStorage;
        this.log = log;
        Directory.CreateDirectory(pluginInterface.ConfigDirectory.FullName);
        this.stateFilePath = Path.Combine(pluginInterface.ConfigDirectory.FullName, StateFileName);
    }

    /// <summary>Loads the latest saved raffle state into the provided configuration object.</summary>
    public void LoadInto(Configuration configuration)
    {
        if (!this.reliableFileStorage.Exists(this.stateFilePath))
        {
            // First run after upgrade: preserve the old config-based ledger by writing it
            // to the separate reliable state file.
            if (configuration.SalesLedger.Count > 0 || configuration.ActiveSessionWasRunning || !string.IsNullOrWhiteSpace(configuration.ActiveSessionLastNotice))
            {
                this.log.Information("Migrating legacy VenueRaffle state from plugin configuration to IReliableFileStorage.");
                this.Save(configuration);
            }

            return;
        }

        try
        {
            var contents = this.reliableFileStorage
                .ReadAllTextAsync(this.stateFilePath)
                .GetAwaiter()
                .GetResult();

            var state = JsonSerializer.Deserialize<RafflePersistentState>(contents, JsonOptions)
                ?? throw new InvalidDataException("VenueRaffle state file was empty or invalid.");

            configuration.SalesLedger = state.SalesLedger ?? new();
            configuration.ActiveSessionWasRunning = state.ActiveSessionWasRunning;
            configuration.ActiveSessionLastNotice = state.ActiveSessionLastNotice ?? string.Empty;

            this.log.Information("Loaded VenueRaffle state from IReliableFileStorage: {Count} sale record(s).", configuration.SalesLedger.Count);
        }
        catch (FileNotFoundException)
        {
            this.log.Information("No VenueRaffle reliable state file found yet.");
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "Could not load VenueRaffle state from IReliableFileStorage. Keeping config/default state.");
        }
    }

    /// <summary>Persists the raffle state through Dalamud's reliable file storage API.</summary>
    public void Save(Configuration configuration)
    {
        try
        {
            var state = new RafflePersistentState
            {
                SalesLedger = configuration.SalesLedger,
                ActiveSessionWasRunning = configuration.ActiveSessionWasRunning,
                ActiveSessionLastNotice = configuration.ActiveSessionLastNotice ?? string.Empty,
            };

            var json = JsonSerializer.Serialize(state, JsonOptions);
            this.reliableFileStorage
                .WriteAllTextAsync(this.stateFilePath, json)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "Could not save VenueRaffle state to IReliableFileStorage.");
        }
    }
}
