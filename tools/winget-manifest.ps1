# Writes the winget manifests (portable package) for a released version.
#   powershell -File tools/winget-manifest.ps1 -Version 0.2.0 -Sha256 <hash of DiskScope.exe> [-OutDir packaging/winget]
# The result goes into a pull request to https://github.com/microsoft/winget-pkgs (see docs/development.md).
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Sha256,
    [string]$OutDir
)

# $PSScriptRoot is not available in parameter defaults in Windows PowerShell 5.1.
if (-not $OutDir) { $OutDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'packaging\winget' }
$id = 'RichardLipka.DiskScope'
# A full path: the .NET file calls below do not know PowerShell's current location.
$dir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath((Join-Path $OutDir "manifests\r\RichardLipka\DiskScope\$Version"))
New-Item -ItemType Directory -Force $dir | Out-Null
$schema = '1.9.0'
$url = "https://github.com/richardLipka/DiskScope/releases/download/v$Version/DiskScope.exe"
# UTF-8 without a byte order mark (Windows PowerShell's -Encoding utf8 would add one).
function Write-Manifest([string]$Text, [string]$Name) {
    [IO.File]::WriteAllText((Join-Path $dir $Name), $Text.Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding($false)))
}

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $schema
"@ | ForEach-Object { Write-Manifest $_ "$id.yaml" }

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
InstallerType: portable
Commands:
- diskscope
MinimumOSVersion: 10.0.17763.0
Installers:
- Architecture: x64
  InstallerUrl: $url
  InstallerSha256: $($Sha256.ToUpperInvariant())
ManifestType: installer
ManifestVersion: $schema
"@ | ForEach-Object { Write-Manifest $_ "$id.installer.yaml" }

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: Richard Lipka
PublisherUrl: https://github.com/richardLipka
PublisherSupportUrl: https://github.com/richardLipka/DiskScope/issues
Author: Richard Lipka
PackageName: DiskScope
PackageUrl: https://github.com/richardLipka/DiskScope
License: MIT
LicenseUrl: https://github.com/richardLipka/DiskScope/blob/main/LICENSE
ShortDescription: Fast, portable disk space analyzer and cleanup tool for Windows.
Description: |-
  DiskScope scans a drive or folder and shows recursive sizes in a tree you can browse while the scan runs
  (NTFS MFT fast mode when run as administrator). It finds what to delete or move: search by size, name and age,
  a cleanup catalog of caches and temporary files, file types, a treemap, duplicates, abandoned app data and
  growth since an earlier scan. Deleting, moving and compressing run in a background queue with safety checks.
Moniker: diskscope
Tags:
- disk
- disk-space
- disk-usage
- cleanup
- treemap
- duplicates
- ntfs
- portable
ReleaseNotesUrl: https://github.com/richardLipka/DiskScope/releases/tag/v$Version
ManifestType: defaultLocale
ManifestVersion: $schema
"@ | ForEach-Object { Write-Manifest $_ "$id.locale.en-US.yaml" }

Write-Output "Manifests written to $dir"
