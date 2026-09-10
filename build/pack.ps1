<#
.SYNOPSIS
    Publishes Muster and packs it into a Velopack installer and update package.

.DESCRIPTION
    Produces, in .\releases:
      Muster-win-Setup.exe      the installer people download once
      Muster-<version>-full.nupkg   the package the installed app updates itself from
      RELEASES-win                  the index the updater reads

    Upload the whole of .\releases to a GitHub release tagged with the same version. The app
    reads that release list through Velopack's GithubSource, so a release that is still a draft
    is invisible to it, which is the intended way to stage one.

    Publishes framework-dependent rather than self-contained, and lets the installer bring the
    dependencies: --framework tells Velopack to install the .NET desktop runtime and the WebView2
    runtime during setup if the machine does not already have them. That keeps the download small
    and, more usefully, means the WebView2 dependency is handled rather than being a support
    question the first time somebody installs on a fresh machine.

.PARAMETER Version
    Overrides the version. Defaults to the <Version> in Directory.Build.props, which is the one
    place it should be edited.

.EXAMPLE
    ./build/pack.ps1
    ./build/pack.ps1 -Version 1.1.0
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

try {
    if (-not $Version) {
        $props = [xml](Get-Content (Join-Path $root 'Directory.Build.props'))
        $Version = $props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    }

    if (-not $Version) {
        throw 'No <Version> found in Directory.Build.props, and none was passed.'
    }

    $publish = Join-Path $root 'artifacts/publish'
    $releases = Join-Path $root 'releases'

    Write-Host "Packing Muster $Version for $Runtime" -ForegroundColor Cyan

    # The tests are cheap and the whole point of the packaging step is that what ships was green.
    dotnet test (Join-Path $root 'Muster.Core.Tests/Muster.Core.Tests.csproj') --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed; nothing was packed.' }

    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

    dotnet publish (Join-Path $root 'Muster.App/Muster.App.csproj') `
        -c Release -r $Runtime --self-contained false `
        -p:Version=$Version -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

    dotnet vpk pack `
        --packId Muster `
        --packVersion $Version `
        --packTitle Muster `
        --packAuthors JamTheRadar `
        --packDir $publish `
        --mainExe Muster.exe `
        --icon (Join-Path $root 'assets/muster.ico') `
        --framework "net10-x64-desktop,webview2" `
        --outputDir $releases
    if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed.' }

    Write-Host ''
    Write-Host "Done. Upload the contents of $releases to a GitHub release tagged v$Version." -ForegroundColor Green
    Get-ChildItem $releases | Select-Object Name, @{n = 'Size'; e = { '{0:N1} MB' -f ($_.Length / 1MB) } }
}
finally {
    Pop-Location
}
