# Builds the portable single-file DiskScope.exe into ./publish (no .NET installation needed to run it).
$root = Split-Path -Parent $PSScriptRoot
dotnet publish (Join-Path $root 'src\DiskScope\DiskScope.csproj') -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o (Join-Path $root 'publish')
