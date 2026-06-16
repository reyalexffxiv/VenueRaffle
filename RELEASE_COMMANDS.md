# Release commands

Use these commands from the official repository folder:

```powershell
cd G:\AmberDev\VenueRaffleRepo

# Build the plugin and let DalamudPackager create latest.zip.
dotnet build .\VenueRaffle.sln -c Release

# Copy the packaged plugin to the custom-repository dist folder.
New-Item -ItemType Directory -Force .\dist
Copy-Item .\VenueRaffle\bin\x64\Release\VenueRaffle\latest.zip .\dist\VenueRaffle-latest.zip -Force

# Optional verification: confirm the package contains the plugin manifest.
Remove-Item -Recurse -Force .\zipcheck -ErrorAction SilentlyContinue
Expand-Archive .\dist\VenueRaffle-latest.zip .\zipcheck -Force
Get-ChildItem .\zipcheck
Get-Content .\zipcheck\VenueRaffle.json

# Commit and push.
git status
git add .
git commit -m "Publish Venue Raffle 0.1.1.2"
git push
```

If Git says `dist/VenueRaffle-latest.zip` is ignored, use:

```powershell
git add -f dist/VenueRaffle-latest.zip
```

Custom plugin repository URL:

```text
https://raw.githubusercontent.com/reyalexffxiv/VenueRaffle/master/pluginmaster.json
```

Package URL used by `pluginmaster.json`:

```text
https://raw.githubusercontent.com/reyalexffxiv/VenueRaffle/master/dist/VenueRaffle-latest.zip
```
