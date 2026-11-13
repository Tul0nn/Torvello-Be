[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9_.-]+@[a-zA-Z0-9.-]+$')][string]$Server,
    [Parameter(Mandatory=$true)][string]$SitesRepo
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Invoke-Native {
    param([string]$Command, [string[]]$Arguments)
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE" }
}
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$sites = (Resolve-Path $SitesRepo).Path
$stage = Join-Path $repo 'deploy/build'
$publish = Join-Path $stage 'api'
# Clear only known build output under this repository.
$stage = [IO.Path]::GetFullPath($stage)
if (-not $stage.StartsWith($repo.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Build output must stay inside the repository' }
if ((Get-Item -LiteralPath (Join-Path $repo 'deploy')).Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'Deployment directory must not be a junction or symbolic link'
}
if ((Test-Path $stage) -and ((Get-Item -LiteralPath $stage).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Build output must not be a junction or symbolic link'
}
if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $publish -Force | Out-Null
Invoke-Native 'dotnet' @('publish', (Join-Path $repo 'src/Torvello.Api'), '-c', 'Release', '-r', 'linux-x64', '--self-contained', 'false', '-o', $publish)
$previousApi = $env:VITE_API_URL
try {
    $env:VITE_API_URL = 'https://api.torvellobd.com'
    foreach ($site in @('owner-web', 'cafe-site', 'landing')) {
        Push-Location (Join-Path $sites $site)
        try {
            # Reuse an existing install; npm ci fails on Windows while a dev server holds node_modules.
            if (-not (Test-Path 'node_modules')) { Invoke-Native 'npm.cmd' @('ci') }
            Invoke-Native 'npm.cmd' @('run', 'build')
            Copy-Item -LiteralPath 'dist' -Destination (Join-Path $stage $site) -Recurse
        } finally { Pop-Location }
    }
} finally { $env:VITE_API_URL = $previousApi }
# Archive only explicit deployment files, never .env or unrelated source.
$bundle = Join-Path $stage 'bundle'
New-Item -ItemType Directory -Path $bundle | Out-Null
foreach ($file in @('Dockerfile.api','docker-compose.yml','Caddyfile','.env.example','.dockerignore','README.md','init','scripts')) {
    Copy-Item -LiteralPath (Join-Path $repo "deploy/$file") -Destination $bundle -Recurse
}
New-Item -ItemType Directory -Path (Join-Path $bundle 'build') | Out-Null
Copy-Item -LiteralPath $publish -Destination (Join-Path $bundle 'build/api') -Recurse
$archive = Join-Path $stage 'release.tar.gz'
Invoke-Native 'tar.exe' @('-czf', $archive, '-C', $bundle, '.', '-C', $stage, 'owner-web', 'cafe-site', 'landing')
Invoke-Native 'scp.exe' @($archive, "${Server}:/srv/torvello/release.tar.gz")
$remote = @'
set -eu
cd /srv/torvello
release="releases/$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -p "$release"
tar -xzf release.tar.gz -C "$release"
# Preserve previous artifacts for manual rollback. Database rollback is separate.
if [ -d app/build ]; then
    previous="releases/previous-$(date -u +%Y%m%dT%H%M%SZ)"
    mkdir -p "$previous"
    cp -a app "$previous/app"
    cp -a sites "$previous/sites"
fi
# Replace published API output so removed assemblies cannot survive an update.
rm -rf -- /srv/torvello/app/build
cp -a "$release/." app/
for pair in 'owner-web app' 'cafe-site cafe' 'landing landing'; do
    set -- $pair
    mkdir -p "sites/$2"
    # Remove only old site content, including stale hashed assets.
    find "sites/$2" -mindepth 1 -maxdepth 1 -exec rm -rf -- {} +
    cp -a "$release/$1/." "sites/$2/"
done
chmod 600 app/.env
cd app
docker compose up -d --build
'@
# Avoid CRLF in the remote shell script.
Invoke-Native 'ssh.exe' @($Server, ($remote -replace "`r", ''))
$healthy = $false
for ($attempt = 0; $attempt -lt 60; $attempt++) {
    try {
        $result = Invoke-RestMethod -Uri 'https://api.torvellobd.com/health' -TimeoutSec 10
        if ($result.status -eq 'ok') { $healthy = $true; break }
    } catch { }
    Start-Sleep -Seconds 5
}
if (-not $healthy) { throw 'API health check did not pass. Inspect compose logs and rollback instructions.' }
Write-Host 'Deployment healthy.'
