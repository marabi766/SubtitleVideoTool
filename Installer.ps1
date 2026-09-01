$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms
[System.Windows.Forms.Application]::EnableVisualStyles()

$appName = 'Subtitle & Video Compressor'
$appVersion = '1.2.4'
$sourceRoot = $PSScriptRoot
$installRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\SubtitleVideoTool'
$startMenuRoot = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Microsoft\Windows\Start Menu\Programs\Subtitle & Video Compressor'
$desktopRoot = [Environment]::GetFolderPath('DesktopDirectory')
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\SubtitleVideoTool'

function New-AppShortcut {
    param(
        [Parameter(Mandatory)][string]$ShortcutPath,
        [Parameter(Mandatory)][string]$ScriptPath,
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)][string]$IconPath
    )

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = Join-Path $env:SystemRoot 'System32\wscript.exe'
    $shortcut.Arguments = '"' + $ScriptPath + '"'
    $shortcut.WorkingDirectory = Split-Path -Parent $ScriptPath
    $shortcut.Description = $Description
    $shortcut.IconLocation = $IconPath + ',0'
    $shortcut.Save()
}

try {
    $requiredFiles = @(
        (Join-Path $sourceRoot 'SubtitleVideoTool.ps1'),
        (Join-Path $sourceRoot 'SubtitleVideoTool.vbs'),
        (Join-Path $sourceRoot 'assets\SubtitleVideoTool.ico'),
        (Join-Path $sourceRoot 'tools\ffmpeg.exe'),
        (Join-Path $sourceRoot 'tools\ffprobe.exe'),
        (Join-Path $sourceRoot 'tools\yt-dlp.exe'),
        (Join-Path $sourceRoot 'tools\deno.exe')
    )
    foreach ($requiredFile in $requiredFiles) {
        if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
            throw "فایل ضروری بسته پیدا نشد:`r`n$requiredFile"
        }
    }

    $message = "نسخهٔ $appVersion در مسیر زیر نصب می‌شود:`r`n`r`n$installRoot`r`n`r`nمیانبر منوی Start و Desktop نیز ساخته می‌شود. ادامه می‌دهید؟"
    $answer = [System.Windows.Forms.MessageBox]::Show($message, "نصب $appName", [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Question)
    if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) { exit 0 }

    [void][System.IO.Directory]::CreateDirectory($installRoot)
    Get-ChildItem -LiteralPath $sourceRoot -Force | Where-Object { $_.Name -notin @('installer', 'Build-Release.ps1') } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $installRoot -Recurse -Force
    }

    [void][System.IO.Directory]::CreateDirectory($startMenuRoot)
    $installedLauncher = Join-Path $installRoot 'SubtitleVideoTool.vbs'
    $installedUninstaller = Join-Path $installRoot 'Uninstall.vbs'
    $installedIcon = Join-Path $installRoot 'assets\SubtitleVideoTool.ico'
    New-AppShortcut -ShortcutPath (Join-Path $startMenuRoot 'Subtitle & Video Compressor.lnk') -ScriptPath $installedLauncher -Description $appName -IconPath $installedIcon
    New-AppShortcut -ShortcutPath (Join-Path $startMenuRoot 'Uninstall Subtitle & Video Compressor.lnk') -ScriptPath $installedUninstaller -Description "حذف $appName" -IconPath $installedIcon
    New-AppShortcut -ShortcutPath (Join-Path $desktopRoot 'Subtitle & Video Compressor.lnk') -ScriptPath $installedLauncher -Description $appName -IconPath $installedIcon

    if (-not (Test-Path -LiteralPath $uninstallKey)) { [void](New-Item -Path $uninstallKey -Force) }
    Set-ItemProperty -LiteralPath $uninstallKey -Name 'DisplayName' -Value $appName
    Set-ItemProperty -LiteralPath $uninstallKey -Name 'DisplayVersion' -Value $appVersion
    Set-ItemProperty -LiteralPath $uninstallKey -Name 'Publisher' -Value 'Subtitle Video Tool'
    Set-ItemProperty -LiteralPath $uninstallKey -Name 'InstallLocation' -Value $installRoot
    Set-ItemProperty -LiteralPath $uninstallKey -Name 'UninstallString' -Value ('"' + (Join-Path $env:SystemRoot 'System32\wscript.exe') + '" "' + $installedUninstaller + '"')
    New-ItemProperty -LiteralPath $uninstallKey -Name 'NoModify' -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -LiteralPath $uninstallKey -Name 'NoRepair' -Value 1 -PropertyType DWord -Force | Out-Null

    [System.Windows.Forms.MessageBox]::Show("نصب کامل شد.`r`n`r`nبرنامه را از Desktop یا منوی Start اجرا کنید.", 'نصب موفق', [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
}
catch {
    [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'خطای نصب', [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
    exit 1
}
