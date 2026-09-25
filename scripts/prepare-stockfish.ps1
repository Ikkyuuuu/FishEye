$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content (Join-Path $repo 'assets/engine/stockfish.json') -Raw | ConvertFrom-Json
$destination = Join-Path $repo 'assets/engine/stockfish.zip'
if ((Test-Path -LiteralPath $destination) -and (Get-FileHash -LiteralPath $destination).Hash -eq $manifest.sha256) { return }
$temporary = "$destination.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    Write-Host "Downloading Stockfish $($manifest.version) for the offline build..."
    Invoke-WebRequest -Uri $manifest.url -OutFile $temporary
    if ((Get-FileHash -LiteralPath $temporary).Hash -ne $manifest.sha256) { throw 'Stockfish archive checksum mismatch.' }
    Move-Item -LiteralPath $temporary -Destination $destination -Force
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
}
