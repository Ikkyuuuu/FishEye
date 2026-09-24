param([switch]$LiveApi, [switch]$Gui)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'tests\FishEyes.Tests\FishEyes.Tests.csproj'
$reportName = if ($Gui) { 'gui.json' } elseif ($LiveApi) { 'live-api.json' } else { 'tests.json' }
$testArguments = @('--output', (Join-Path $repo "artifacts\test-results\$reportName"))
if ($Gui) { $testArguments += '--gui-test' }
if ($LiveApi) { $testArguments += '--live-api' }
if ($Gui -and $LiveApi) { throw 'Run -Gui and -LiveApi separately; the GUI test uses a deterministic engine.' }
dotnet run --project $project -c Release -- @testArguments
if ($LASTEXITCODE -ne 0) { throw 'FishEyes tests failed.' }
