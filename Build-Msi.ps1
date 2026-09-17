<#
    Build-Msi.ps1

    Publishes the app and packages it, with the four bundled tools, into a
    single MSI that installs like any other Windows program.

        powershell -ExecutionPolicy Bypass -File Build-Msi.ps1

    Needs the .NET 10 SDK, the WiX CLI, and Node.js (only to install the
    PO-token provider's npm dependencies — the app itself ships Deno, not
    Node):

        winget install Microsoft.DotNet.SDK.10
        dotnet tool install --global wix
        winget install OpenJS.NodeJS.LTS

    The result lands in build\SubtitleVideoTool-<version>.msi.
#>

[CmdletBinding()]
param(
    [string]$Version = '2.1.3',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$publishFolder = Join-Path $root 'build\app'
$toolsFolder = Join-Path $root 'tools'
$output = Join-Path $root ("build\SubtitleVideoTool-$Version.msi")

function Assert-Command {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Hint)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "$Name was not found. $Hint"
    }
}

Assert-Command -Name 'dotnet' -Hint 'Install it with: winget install Microsoft.DotNet.SDK.10'
Assert-Command -Name 'wix' -Hint 'Install it with: dotnet tool install --global wix'
Assert-Command -Name 'npm' -Hint 'Install Node.js with: winget install OpenJS.NodeJS.LTS'

# The tools are deliberately not in Git — they are large and are verified
# against CHECKSUMS-SHA256.txt instead — so their absence is a clear error
# rather than an installer that silently ships without them.
$expected = @('ffmpeg.exe', 'ffprobe.exe', 'yt-dlp.exe', 'deno.exe')
$missing = @($expected | Where-Object { -not (Test-Path -LiteralPath (Join-Path $toolsFolder $_)) })
if ($missing.Count -gt 0) {
    throw ("These bundled tools are missing from tools\: " + ($missing -join ', '))
}

Write-Host 'Installing the PO-token provider''s npm dependencies…' -ForegroundColor Cyan
$potProviderServer = Join-Path $toolsFolder 'pot-provider\server'
Push-Location $potProviderServer
try {
    # --omit=dev: only what generate_once.ts actually imports at runtime —
    # the http server, linter and TypeScript toolchain are not shipped.
    & npm install --omit=dev
    if ($LASTEXITCODE -ne 0) { throw 'npm install failed.' }
}
finally {
    Pop-Location
}

Write-Host 'Publishing the application…' -ForegroundColor Cyan
if (Test-Path -LiteralPath $publishFolder) {
    Remove-Item -LiteralPath $publishFolder -Recurse -Force
}

# Self-contained: the target machine needs no .NET installed.
& dotnet publish (Join-Path $root 'src\SubtitleVideoTool.App\SubtitleVideoTool.App.csproj') `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:Version=$Version `
    -o $publishFolder `
    --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

Write-Host 'Building the installer…' -ForegroundColor Cyan
# WiX resolves relative source paths against the working directory rather than
# the .wxs, so the root is passed in explicitly.
& wix build (Join-Path $root 'installer\SubtitleVideoTool.wxs') `
    -arch x64 `
    -d "Version=$Version" `
    -d "SourceRoot=$root" `
    -o $output
if ($LASTEXITCODE -ne 0) { throw 'wix build failed.' }

$size = (Get-Item -LiteralPath $output).Length / 1MB
Write-Host ''
Write-Host ("Built {0} ({1:N1} MB)" -f $output, $size) -ForegroundColor Green
