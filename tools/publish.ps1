# Builds the portable single-file DiskScope.exe into ./publish (no .NET installation needed to run it).
#   powershell -File tools/publish.ps1 [-Version 0.2.0]
param([string]$Version)

$root = Split-Path -Parent $PSScriptRoot
$extra = @()
if ($Version) { $extra += "-p:Version=$Version" }
dotnet publish (Join-Path $root 'src\DiskScope\DiskScope.csproj') -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o (Join-Path $root 'publish') @extra
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
