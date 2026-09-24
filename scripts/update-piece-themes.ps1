# Requires PowerShell 7. Downloads only the public catalog and public image URLs.
param([switch]$UseCachedCatalog)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$cache = Join-Path $repo 'artifacts/themes'
New-Item -ItemType Directory -Force -Path $cache | Out-Null
$catalog = Join-Path $cache 'catalog.json'
if (!$UseCachedCatalog -or !(Test-Path $catalog)) {
    Invoke-WebRequest 'https://www.chess.com/rpc/chesscom.themes.v2.ThemesService/ListAllThemeElements' -Method Post -ContentType 'application/json' -Headers @{'Connect-Protocol-Version'='1'} -Body '{"boardSize":200,"piecesSize":150}' -OutFile $catalog
}
$sets = (Get-Content $catalog -Raw | ConvertFrom-Json).pieceSets
$jobs = foreach ($set in $sets) {
    $identifier = [Guid]::Empty
    if (![Guid]::TryParse($set.id, [ref]$identifier)) { throw 'Invalid theme ID in catalog.' }
    $directory = Join-Path $cache $set.id
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    foreach ($piece in $set.images.psobject.Properties) {
        if ($piece.Name -notmatch '^(white|black)(Pawn|Knight|Bishop|Rook|Queen|King)$') { throw 'Unexpected piece name in catalog.' }
        $uri = [Uri]$piece.Value
        if ($uri.Scheme -ne 'https' -or $uri.Host -notin @('images.chesscomfiles.com', 'assets-themes.chess.com')) { throw "Unexpected image host: $uri" }
        [pscustomobject]@{ Url=$piece.Value; Path=(Join-Path $directory ($piece.Name + '.png')) }
    }
}
$failures = @($jobs | ForEach-Object -Parallel {
    $job = $_
    if (!(Test-Path -LiteralPath $job.Path)) {
        try {
            Invoke-WebRequest $job.Url -OutFile ($job.Path + '.part') -TimeoutSec 30 -ErrorAction Stop
            Move-Item -LiteralPath ($job.Path + '.part') -Destination $job.Path -Force
        }
        catch { [pscustomobject]@{Url=$job.Url; Error=$_.Exception.Message} }
    }
} -ThrottleLimit 6)
if ($failures.Count) { $failures | ConvertTo-Json | Set-Content (Join-Path $cache 'download-errors.json'); throw "$($failures.Count) image downloads failed. Run again to retry." }
dotnet run --project (Join-Path $repo 'tests/FishEyes.Tests') -c Release -- --build-themes --sample $cache --output (Join-Path $repo 'assets/themes/build-report.json')
if ($LASTEXITCODE -ne 0) { throw 'Theme pack generation failed.' }
dotnet run --project (Join-Path $repo 'tests/FishEyes.Tests') -c Release -- --theme-tests --sample $cache --output (Join-Path $cache 'verification.json')
if ($LASTEXITCODE -ne 0) { throw 'Theme verification failed; inspect artifacts/themes/theme-results.json before publishing the updated pack.' }
