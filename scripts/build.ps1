param([switch]$Test)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if ($Test) { & (Join-Path $PSScriptRoot 'test.ps1') }
$project = Join-Path $repo 'src\FishEyes\FishEyes.csproj'
$destination = Join-Path $repo 'artifacts\publish\win-x64'
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $destination
if ($LASTEXITCODE -ne 0) { throw 'FishEyes publish failed.' }
Write-Host "Ready: $destination\FishEyes.exe"
