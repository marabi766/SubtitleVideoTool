$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms
[System.Windows.Forms.Application]::EnableVisualStyles()

$appName = 'Subtitle & Video Compressor'
$installRoot = $PSScriptRoot
$startMenuRoot = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Microsoft\Windows\Start Menu\Programs\Subtitle & Video Compressor'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Subtitle & Video Compressor.lnk'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\SubtitleVideoTool'

$answer = [System.Windows.Forms.MessageBox]::Show("برنامه و میانبرهای آن حذف شوند؟", "حذف $appName", [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Question)
if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) { exit 0 }

try {
    if (Test-Path -LiteralPath $startMenuRoot) { Remove-Item -LiteralPath $startMenuRoot -Recurse -Force }
    if (Test-Path -LiteralPath $desktopShortcut) { Remove-Item -LiteralPath $desktopShortcut -Force }
    if (Test-Path -LiteralPath $uninstallKey) { Remove-Item -LiteralPath $uninstallKey -Recurse -Force }

    $cleanupScript = Join-Path ([System.IO.Path]::GetTempPath()) ('SubtitleVideoTool_cleanup_' + [guid]::NewGuid().ToString('N') + '.cmd')
    $cleanupLines = @(
        '@echo off',
        'timeout /t 2 /nobreak >nul',
        ('rmdir /s /q "' + $installRoot + '"'),
        'del /q "%~f0"'
    )
    [System.IO.File]::WriteAllLines($cleanupScript, $cleanupLines, [System.Text.Encoding]::ASCII)
    Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\cmd.exe') -ArgumentList ('/c "' + $cleanupScript + '"') -WindowStyle Hidden

    [System.Windows.Forms.MessageBox]::Show('برنامه حذف شد.', 'حذف موفق', [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
}
catch {
    [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'خطای حذف', [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
    exit 1
}

