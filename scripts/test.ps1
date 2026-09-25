param([switch]$LocalEngine, [switch]$Gui)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'tests\FishEyes.Tests\FishEyes.Tests.csproj'
$reportName = if ($Gui) { 'gui.json' } elseif ($LocalEngine) { 'local-engine.json' } else { 'tests.json' }
$testArguments = @('--output', (Join-Path $repo "artifacts\test-results\$reportName"))
if ($Gui) { $testArguments += '--gui-test' }
if ($LocalEngine) { $testArguments += '--local-engine' }
if ($Gui -and $LocalEngine) { throw 'Run -Gui and -LocalEngine separately; the GUI test uses a deterministic engine.' }
dotnet run --project $project -c Release -- @testArguments
if ($LASTEXITCODE -ne 0) { throw 'FishEyes tests failed.' }
