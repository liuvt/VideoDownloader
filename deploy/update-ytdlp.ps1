$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$tools = Join-Path $projectRoot "Tools"
$ytDlp = Join-Path $tools "yt-dlp.exe"

New-Item -ItemType Directory -Force $tools | Out-Null

Write-Host "Updating yt-dlp..."
Invoke-WebRequest `
  -Uri "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe" `
  -OutFile $ytDlp

& $ytDlp --version

$deno = Get-Command deno -ErrorAction SilentlyContinue
$node = Get-Command node -ErrorAction SilentlyContinue

if ($deno) {
    Write-Host "Deno detected:"
    & deno --version
} elseif ($node) {
    Write-Host "Node detected:"
    & node --version
    Write-Host "Clip2Down will automatically pass --js-runtimes node to yt-dlp."
} else {
    Write-Warning "No supported JS runtime found. Install Deno 2.3+ or Node 22+ for current YouTube extraction."
}
