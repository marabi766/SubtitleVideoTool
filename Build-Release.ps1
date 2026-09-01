$ErrorActionPreference = 'Stop'

$projectRoot = $PSScriptRoot
$version = '1.2.4'
$releaseRoot = Join-Path $projectRoot 'release'
$portableZip = Join-Path $releaseRoot "SubtitleVideoTool-Portable-v$version.zip"
$required = @(
    'SubtitleVideoTool.ps1',
    'SubtitleVideoTool.vbs',
    'assets\SubtitleVideoTool.ico',
    'tools\ffmpeg.exe',
    'tools\ffprobe.exe',
    'tools\yt-dlp.exe',
    'tools\deno.exe'
)

foreach ($relativePath in $required) {
    $fullPath = Join-Path $projectRoot $relativePath
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { throw "Missing required file: $relativePath" }
}

[void][System.IO.Directory]::CreateDirectory($releaseRoot)
if (Test-Path -LiteralPath $portableZip) { Remove-Item -LiteralPath $portableZip -Force }

$portableItems = Get-ChildItem -LiteralPath $projectRoot -Force | Where-Object { $_.Name -notin @('release', 'installer', 'Build-Release.ps1') }
Compress-Archive -Path $portableItems.FullName -DestinationPath $portableZip -CompressionLevel Optimal
Write-Host "Portable package: $portableZip"

$isccCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) }

if ($isccCandidates.Count -gt 0) {
    & $isccCandidates[0] (Join-Path $projectRoot 'installer\SubtitleVideoTool.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE" }
}
else {
    Write-Warning 'Inno Setup 6 was not found. The portable ZIP was created; install Inno Setup and rerun this script to also build the Setup EXE.'
}
