# Release commands

Use these commands from the official repository folder:

```powershell
cd G:\AmberDev\VenueRaffleRepo

# Build, package, and validate the Dalamud zip.
powershell -ExecutionPolicy Bypass -File .\scripts\Build-DalamudPackage.ps1

# Optional verification: confirm the package manifest from the final dist zip.
Remove-Item -Recurse -Force .\zipcheck -ErrorAction SilentlyContinue
Expand-Archive .\dist\VenueRaffle-latest.zip .\zipcheck -Force
Get-ChildItem .\zipcheck
Get-Content .\zipcheck\VenueRaffle.json

# Commit and push.
git status
git add .
git add -f .\dist\VenueRaffle-latest.zip
git commit -m "Publish Venue Raffle 0.1.1.3"
git push
```

Custom plugin repository URL:

```text
https://raw.githubusercontent.com/reyalexffxiv/VenueRaffle/master/pluginmaster.json
```

Package URL used by `pluginmaster.json`:

```text
https://raw.githubusercontent.com/reyalexffxiv/VenueRaffle/master/dist/VenueRaffle-latest.zip
```
