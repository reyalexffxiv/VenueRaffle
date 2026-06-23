# Venue Raffle

A lightweight raffle ticket tracker for FFXIV venues and events.

Current version: `0.1.1.3`

## Features

- Track raffle ticket purchases and ticket ranges
- Keep the raffle capped at 999 tickets for FFXIV `/random`
- Calculate ticket sales and prize pot automatically
- Send ticket tells to the current target
- Manage raffle entries from the Statistics tab
- Export raffle entries to CSV
- Use custom shout/yell macros for raffle ads and winner announcements

## Commands

```text
/venueraffle
/raffle
```

## Release

Build and package from the official repo folder with:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build-DalamudPackage.ps1
```

The script validates the project version, repository manifest version, produced package contents, and staged package manifest before writing `dist\VenueRaffle-latest.zip`.

## Notes

Venue Raffle is a third-party plugin project and is not affiliated with or endorsed by Square Enix, XIVLauncher, Dalamud, or goatcorp.
