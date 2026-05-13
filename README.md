# Venue Raffle

Venue Raffle is a Dalamud plugin for running simple FFXIV venue raffle ticket sales.

## Command

```text
/venueraffle
```

## Core workflow

1. Target a player in-game.
2. Click **Use Current Target**.
3. Enter how many tickets they bought.
4. Click **Add Tickets**.
5. Venue Raffle assigns the next ticket range, updates sales, venue/host share, and prize pot.
6. Use the tell/ad/draw buttons during the event.

## Current limits

- Total raffle tickets are hard-capped at **999** because FFXIV `/random` is capped at 999.
- Ticket price, base pot, macros, and venue/host share are configurable in Settings.

## Offline licensing

Venue Raffle uses offline install-bound license text. The plugin generates a random local Install ID and validates signed license text against that Install ID.

Activation flow:

1. User copies **Your Install ID** from Settings > License.
2. Developer generates signed license text for that Install ID.
3. User pastes the license text in Settings > License and clicks **Activate**.

No online server is used, and no FFXIV character name, world, raffle data, chat messages, targets, or hardware fingerprint is sent anywhere.

## Build

Open `VenueRaffle.sln` in Visual Studio or Rider, or run:

```powershell
dotnet build .\VenueRaffle.sln
```

Debug output is normally under:

```text
VenueRaffle/bin/x64/Debug/
```
