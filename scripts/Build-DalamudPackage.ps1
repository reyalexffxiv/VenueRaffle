param(
    [string]$Configuration = "Release",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$projectPath = Join-Path $repo "VenueRaffle\VenueRaffle.csproj"
$pluginMasterPath = Join-Path $repo "pluginmaster.json"

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$projectXml = Get-Content $projectPath
    $Version = $projectXml.Project.PropertyGroup.Version | Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    throw "Could not determine VenueRaffle version from $projectPath."
}

$pluginMaster = Get-Content $pluginMasterPath -Raw | ConvertFrom-Json
$pluginMasterVersion = @($pluginMaster)[0].AssemblyVersion
if ($pluginMasterVersion -ne $Version) {
    throw "Version mismatch: csproj Version is '$Version' but pluginmaster.json AssemblyVersion is '$pluginMasterVersion'."
}

Write-Host "Building VenueRaffle $Version..."
dotnet build .\VenueRaffle.sln -c $Configuration /p:Version=$Version
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE. Package was not created."
}

$dist = Join-Path $repo "dist"
New-Item -ItemType Directory -Force $dist | Out-Null

$packagerZip = Join-Path $repo "VenueRaffle\bin\x64\$Configuration\VenueRaffle\latest.zip"
if (-not (Test-Path $packagerZip)) {
    throw "DalamudPackager output was not found: $packagerZip"
}

$zipPath = Join-Path $dist "VenueRaffle-latest.zip"
Remove-Item -Force $zipPath -ErrorAction SilentlyContinue
Copy-Item $packagerZip $zipPath -Force

Write-Host "Created $zipPath"
Write-Host "Zip contents:"

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $requiredFileNames = @(
        "VenueRaffle.dll",
        "VenueRaffle.deps.json",
        "VenueRaffle.json"
    )

    $entries = $zip.Entries
    $entryNames = $entries | ForEach-Object { $_.FullName }

    foreach ($required in $requiredFileNames) {
        if ($entryNames -notcontains $required) {
            throw "Zip validation failed: '$required' is missing from the package root."
        }
    }

    foreach ($entryName in $entryNames) {
        if ($entryName -match "/") {
            throw "Zip validation failed: '$entryName' is nested. Plugin files must be at the zip root."
        }
    }

    $manifestEntry = $entries | Where-Object { $_.FullName -eq "VenueRaffle.json" } | Select-Object -First 1
    $reader = New-Object System.IO.StreamReader($manifestEntry.Open())
    try {
        $stagedManifest = $reader.ReadToEnd() | ConvertFrom-Json
    }
    finally {
        $reader.Dispose()
    }

    if ($stagedManifest.InternalName -ne "VenueRaffle") {
        throw "Staged manifest InternalName mismatch: expected 'VenueRaffle', got '$($stagedManifest.InternalName)'."
    }

    if ($stagedManifest.AssemblyVersion -ne $Version) {
        throw "Staged manifest AssemblyVersion mismatch: expected '$Version', got '$($stagedManifest.AssemblyVersion)'."
    }

    if ($stagedManifest.DalamudApiLevel -ne 15) {
        throw "Staged manifest DalamudApiLevel mismatch: expected '15', got '$($stagedManifest.DalamudApiLevel)'."
    }

    foreach ($entry in $entries) {
        " - $($entry.FullName)"
    }
}
finally {
    $zip.Dispose()
}
