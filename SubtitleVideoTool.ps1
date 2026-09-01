$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

[System.Windows.Forms.Application]::EnableVisualStyles()

$script:AppRoot = if ($PSScriptRoot) { $PSScriptRoot } elseif ($global:LauncherRoot) { $global:LauncherRoot.TrimEnd('\') } else { (Get-Location).Path }
$script:FfmpegPath = $null
$script:FfprobePath = $null
$script:YtDlpPath = $null
$script:DenoPath = $null
$script:CurrentProcess = $null
$script:CurrentToolName = $null
$script:LastToolOutput = New-Object 'System.Collections.Generic.List[string]'
$script:OutputTask = $null
$script:ErrorTask = $null
$script:StageQueue = New-Object System.Collections.Queue
$script:CleanupPaths = @()
$script:FinalOutput = $null
$script:SuccessMessage = $null
$script:ExpectedMaximumBytes = 0L
$script:Cancelled = $false
$script:OperationKind = 'media'
$script:DownloadFolder = $null
$script:DownloadStarted = $null
$script:YoutubeFallbackStage = $null
$script:YoutubeFallbackAttempted = $false
$script:SubtitleColor = [System.Drawing.Color]::White
$script:BackgroundColor = [System.Drawing.Color]::Black

function Resolve-Executable {
    param([Parameter(Mandatory)][string]$Name)

    $bundled = Join-Path (Join-Path $script:AppRoot 'tools') ($Name + '.exe')
    if (Test-Path -LiteralPath $bundled -PathType Leaf) {
        return (Get-Item -LiteralPath $bundled).FullName
    }

    $besideApp = Join-Path $script:AppRoot ($Name + '.exe')
    if (Test-Path -LiteralPath $besideApp -PathType Leaf) {
        return (Get-Item -LiteralPath $besideApp).FullName
    }

    $command = Get-Command ($Name + '.exe') -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $command) {
        $command = Get-Command $Name -ErrorAction SilentlyContinue | Select-Object -First 1
    }
    if ($command) { return $command.Source }
    return $null
}

function Quote-WindowsArgument {
    param([AllowEmptyString()][string]$Value)

    if ($null -eq $Value -or $Value.Length -eq 0) { return '""' }
    if ($Value -notmatch '[\s"]') { return $Value }

    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append('"')
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $backslashes++
            continue
        }
        if ($character -eq '"') {
            [void]$builder.Append(('\' * (($backslashes * 2) + 1)))
            [void]$builder.Append('"')
            $backslashes = 0
            continue
        }
        if ($backslashes -gt 0) {
            [void]$builder.Append(('\' * $backslashes))
            $backslashes = 0
        }
        [void]$builder.Append($character)
    }
    if ($backslashes -gt 0) { [void]$builder.Append(('\' * ($backslashes * 2))) }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Join-ProcessArguments {
    param([Parameter(Mandatory)][object[]]$Values)
    return (($Values | ForEach-Object { Quote-WindowsArgument ([string]$_) }) -join ' ')
}

function Add-Log {
    param([string]$Message)
    if ([string]::IsNullOrWhiteSpace($Message)) { return }
    $timestamp = Get-Date -Format 'HH:mm:ss'
    $txtLog.AppendText("[$timestamp] $Message`r`n")
    $txtLog.SelectionStart = $txtLog.TextLength
    $txtLog.ScrollToCaret()
}

function Collect-ProcessOutput {
    $capturedText = @()
    try {
        if ($script:OutputTask) { $capturedText += [string]$script:OutputTask.Result }
        if ($script:ErrorTask) { $capturedText += [string]$script:ErrorTask.Result }
    }
    catch {
        $capturedText += ('خطا در خواندن گزارش ابزار: ' + $_.Exception.Message)
    }
    foreach ($text in $capturedText) {
        foreach ($line in ($text -split "`r?`n")) {
            if (-not [string]::IsNullOrWhiteSpace($line)) {
                $script:LastToolOutput.Add($line)
                while ($script:LastToolOutput.Count -gt 200) { $script:LastToolOutput.RemoveAt(0) }
                Add-Log $line
            }
        }
    }
    $script:OutputTask = $null
    $script:ErrorTask = $null
}

function Select-InputFile {
    param(
        [Parameter(Mandatory)][System.Windows.Forms.TextBox]$Target,
        [Parameter(Mandatory)][string]$Filter
    )
    $dialog = New-Object System.Windows.Forms.OpenFileDialog
    $dialog.Filter = $Filter
    $dialog.CheckFileExists = $true
    $dialog.Multiselect = $false
    if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        $Target.Text = $dialog.FileName
    }
    $dialog.Dispose()
}

function Select-OutputFile {
    param(
        [Parameter(Mandatory)][string]$SuggestedName
    )
    $dialog = New-Object System.Windows.Forms.SaveFileDialog
    $dialog.Filter = 'ویدیوی ام‌پی‌فور (*.mp4)|*.mp4|همهٔ فایل‌ها (*.*)|*.*'
    $dialog.FileName = $SuggestedName
    $dialog.OverwritePrompt = $true
    $value = $null
    if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        $value = $dialog.FileName
    }
    $dialog.Dispose()
    return $value
}

function Select-OutputFolder {
    param([string]$InitialPath)

    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = 'پوشهٔ ذخیرهٔ ویدیو و زیرنویس را انتخاب کنید'
    $dialog.ShowNewFolderButton = $true
    if ($InitialPath -and (Test-Path -LiteralPath $InitialPath -PathType Container)) {
        $dialog.SelectedPath = $InitialPath
    }
    $value = $null
    if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        $value = $dialog.SelectedPath
    }
    $dialog.Dispose()
    return $value
}

function Test-Tools {
    $script:FfmpegPath = Resolve-Executable 'ffmpeg'
    $script:FfprobePath = Resolve-Executable 'ffprobe'
    $script:YtDlpPath = Resolve-Executable 'yt-dlp'
    $script:DenoPath = Resolve-Executable 'deno'
    $ready = $script:FfmpegPath -and $script:FfprobePath
    if ($ready -and $script:YtDlpPath -and $script:DenoPath) {
        $lblTools.Text = 'FFmpeg، FFprobe، yt-dlp و Deno آماده‌اند'
        $lblTools.ForeColor = [System.Drawing.Color]::FromArgb(20, 120, 75)
        $lblTools.Tag = $true
    }
    elseif ($ready) {
        $lblTools.Text = 'ابزارهای ویدیو آماده‌اند؛ yt-dlp یا Deno پیدا نشد'
        $lblTools.ForeColor = [System.Drawing.Color]::DarkOrange
        $lblTools.Tag = $true
    }
    else {
        $lblTools.Text = 'FFmpeg یا FFprobe پیدا نشد؛ پوشهٔ tools را بررسی کنید'
        $lblTools.ForeColor = [System.Drawing.Color]::Firebrick
        $lblTools.Tag = $false
    }
    return [bool]$ready
}

function Get-ProbeData {
    param([Parameter(Mandatory)][string]$VideoPath)

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $script:FfprobePath
    $startInfo.Arguments = Join-ProcessArguments @(
        '-v', 'error',
        '-show_entries', 'format=duration,size,bit_rate:stream=index,codec_type,width,height,r_frame_rate,bit_rate',
        '-of', 'json',
        $VideoPath
    )
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    [void]$process.Start()
    $json = $process.StandardOutput.ReadToEnd()
    $errorText = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($json)) {
        throw "خواندن مشخصات ویدیو ناموفق بود`r`n$errorText"
    }
    return ($json | ConvertFrom-Json)
}

function Get-FrameRate {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return 25.0 }
    $parts = $Value.Split('/')
    if ($parts.Count -eq 2 -and [double]$parts[1] -ne 0) {
        return ([double]$parts[0] / [double]$parts[1])
    }
    $parsed = 0.0
    if ([double]::TryParse($Value, [ref]$parsed)) { return $parsed }
    return 25.0
}

function Convert-ColorToAss {
    param(
        [Parameter(Mandatory)][System.Drawing.Color]$Color,
        [ValidateRange(0, 255)][int]$Alpha = 0
    )
    return ('&H{0:X2}{1:X2}{2:X2}{3:X2}' -f $Alpha, $Color.B, $Color.G, $Color.R)
}

function Set-ColorButtonAppearance {
    param(
        [Parameter(Mandatory)][System.Windows.Forms.Button]$Button,
        [Parameter(Mandatory)][System.Drawing.Color]$Color
    )
    $Button.BackColor = $Color
    $brightness = (($Color.R * 299) + ($Color.G * 587) + ($Color.B * 114)) / 1000
    $Button.ForeColor = if ($brightness -lt 128) { [System.Drawing.Color]::White } else { [System.Drawing.Color]::Black }
    $Button.Text = ('#{0:X2}{1:X2}{2:X2}' -f $Color.R, $Color.G, $Color.B)
}

function Select-Color {
    param(
        [Parameter(Mandatory)][System.Drawing.Color]$InitialColor
    )
    $dialog = New-Object System.Windows.Forms.ColorDialog
    $dialog.Color = $InitialColor
    $dialog.FullOpen = $true
    $selected = $null
    if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        $selected = $dialog.Color
    }
    $dialog.Dispose()
    return $selected
}

function Set-UiBusy {
    param([bool]$Busy)
    $tabs.Enabled = -not $Busy
    $btnLocateTools.Enabled = -not $Busy
    $btnCancel.Enabled = $Busy
    if ($Busy) {
        $progress.Style = [System.Windows.Forms.ProgressBarStyle]::Marquee
        $progress.MarqueeAnimationSpeed = 25
    }
    else {
        $progress.Style = [System.Windows.Forms.ProgressBarStyle]::Blocks
        $progress.Value = 0
    }
}

function Remove-TemporaryFiles {
    foreach ($path in $script:CleanupPaths) {
        try {
            if (Test-Path -LiteralPath $path) {
                Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
        catch { }
    }
    $script:CleanupPaths = @()
}

function Complete-Operation {
    param(
        [bool]$Succeeded,
        [string]$FailureMessage
    )
    $timer.Stop()
    Set-UiBusy $false
    Remove-TemporaryFiles

    if ($script:Cancelled) {
        if ($script:OperationKind -ne 'youtube' -and $script:FinalOutput -and (Test-Path -LiteralPath $script:FinalOutput -PathType Leaf)) {
            Remove-Item -LiteralPath $script:FinalOutput -Force -ErrorAction SilentlyContinue
        }
        Add-Log 'عملیات لغو شد'
        $lblState.Text = 'لغو شد'
        $script:Cancelled = $false
        return
    }

    if (-not $Succeeded) {
        if ($script:OperationKind -ne 'youtube' -and $script:FinalOutput -and (Test-Path -LiteralPath $script:FinalOutput -PathType Leaf)) {
            Remove-Item -LiteralPath $script:FinalOutput -Force -ErrorAction SilentlyContinue
        }
        Add-Log ("خطا: " + $FailureMessage)
        $lblState.Text = 'عملیات ناموفق بود'
        [System.Windows.Forms.MessageBox]::Show(
            $FailureMessage,
            'خطا',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error
        ) | Out-Null
        return
    }

    $extra = ''
    if ($script:OperationKind -eq 'youtube') {
        $minimumTime = if ($script:DownloadStarted) { $script:DownloadStarted.AddSeconds(-5) } else { (Get-Date).AddHours(-1) }
        $downloaded = @(Get-ChildItem -LiteralPath $script:DownloadFolder -File -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $minimumTime })
        $videoFile = $downloaded | Where-Object { $_.Extension.ToLowerInvariant() -in @('.mp4', '.mkv', '.webm', '.mov', '.m4v') } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        $subtitleFile = $downloaded | Where-Object { $_.Extension.ToLowerInvariant() -eq '.srt' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1

        if (-not $videoFile) {
            Add-Log 'دانلود پایان یافت، اما فایل ویدیویی جدید شناسایی نشد'
            $lblState.Text = 'فایل خروجی پیدا نشد'
            [System.Windows.Forms.MessageBox]::Show('yt-dlp بدون خطا پایان یافت، اما فایل ویدیویی جدید در پوشهٔ خروجی پیدا نشد.', 'خروجی پیدا نشد', [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Warning) | Out-Null
            return
        }

        $txtBurnVideo.Text = $videoFile.FullName
        $txtCompressVideo.Text = $videoFile.FullName
        $extra = "`r`nویدیو: $($videoFile.FullName)"
        if ($subtitleFile) {
            $txtBurnSubtitle.Text = $subtitleFile.FullName
            $extra += "`r`nزیرنویس: $($subtitleFile.FullName)"
            $tabs.SelectedTab = $tabBurn
        }
        else {
            $extra += "`r`nزیرنویس فارسی/انگلیسی برای این ویدیو پیدا نشد"
        }
        $script:FinalOutput = $script:DownloadFolder
    }
    elseif ($script:FinalOutput -and (Test-Path -LiteralPath $script:FinalOutput -PathType Leaf)) {
        $sizeBytes = (Get-Item -LiteralPath $script:FinalOutput).Length
        $sizeMb = [math]::Round($sizeBytes / 1MB, 2)
        $extra = "`r`nحجم خروجی: $sizeMb مگابایت"
        if ($script:ExpectedMaximumBytes -gt 0 -and $sizeBytes -gt $script:ExpectedMaximumBytes) {
            $extra += "`r`nهشدار: خروجی کمی بیشتر از سقف درخواستی شده است"
        }
    }
    Add-Log ($script:SuccessMessage + $(if ($extra) { ' — ' + $extra.Trim() } else { '' }))
    $lblState.Text = 'انجام شد'
    [System.Windows.Forms.MessageBox]::Show(
        ($script:SuccessMessage + $extra + "`r`n`r`n" + $script:FinalOutput),
        'عملیات موفق',
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Information
    ) | Out-Null
}

function Start-NextStage {
    if ($script:StageQueue.Count -eq 0) {
        Complete-Operation $true ''
        return
    }

    $stage = $script:StageQueue.Dequeue()
    $script:CurrentToolName = if ($stage.PSObject.Properties.Name -contains 'ToolName') { $stage.ToolName } else { [System.IO.Path]::GetFileNameWithoutExtension([string]$stage.Executable) }
    $lblState.Text = $stage.Name
    Add-Log $stage.Name

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $stage.Executable
    $startInfo.Arguments = Join-ProcessArguments $stage.Arguments
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $captureOutput = ($stage.PSObject.Properties.Name -contains 'CaptureOutput') -and [bool]$stage.CaptureOutput
    $startInfo.RedirectStandardError = $captureOutput
    $startInfo.RedirectStandardOutput = $captureOutput

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo

    try {
        [void]$process.Start()
        $script:CurrentProcess = $process
        if ($captureOutput) {
            $script:OutputTask = $process.StandardOutput.ReadToEndAsync()
            $script:ErrorTask = $process.StandardError.ReadToEndAsync()
        }
    }
    catch {
        Complete-Operation $false $_.Exception.Message
    }
}

function Start-OperationQueue {
    param(
        [Parameter(Mandatory)][object[]]$Stages,
        [Parameter(Mandatory)][string]$SuccessMessage,
        [Parameter(Mandatory)][string]$OutputPath,
        [string[]]$CleanupPaths = @(),
        [long]$ExpectedMaximumBytes = 0,
        [ValidateSet('media', 'youtube')][string]$OperationKind = 'media'
    )

    $script:StageQueue.Clear()
    foreach ($stage in $Stages) { $script:StageQueue.Enqueue($stage) }
    $script:SuccessMessage = $SuccessMessage
    $script:FinalOutput = $OutputPath
    $script:CleanupPaths = $CleanupPaths
    $script:ExpectedMaximumBytes = $ExpectedMaximumBytes
    $script:OperationKind = $OperationKind
    $script:Cancelled = $false
    $script:LastToolOutput.Clear()
    $script:OutputTask = $null
    $script:ErrorTask = $null
    Set-UiBusy $true
    $timer.Start()
    Start-NextStage
}

function Get-SafeSubtitleFilterPath {
    param([Parameter(Mandatory)][string]$Path)
    $normalized = $Path.Replace('\', '/')
    $normalized = $normalized.Replace("'", "\'")
    return ($normalized -replace ':', '\:')
}

$form = New-Object System.Windows.Forms.Form
$form.Text = 'Subtitle & Video Compressor'
$appIconPath = Join-Path $script:AppRoot 'assets\SubtitleVideoTool.ico'
if (Test-Path -LiteralPath $appIconPath -PathType Leaf) {
    $script:AppIcon = New-Object System.Drawing.Icon($appIconPath)
    $form.Icon = $script:AppIcon
}
$form.StartPosition = 'CenterScreen'
$form.Size = New-Object System.Drawing.Size(900, 720)
$form.MinimumSize = New-Object System.Drawing.Size(900, 720)
$form.Font = New-Object System.Drawing.Font('Segoe UI', 10)
$form.RightToLeft = [System.Windows.Forms.RightToLeft]::Yes
$form.RightToLeftLayout = $true
$form.BackColor = [System.Drawing.Color]::FromArgb(246, 248, 251)

$title = New-Object System.Windows.Forms.Label
$title.Text = 'Subtitle & Video Compressor'
$title.Font = New-Object System.Drawing.Font('Segoe UI', 17, [System.Drawing.FontStyle]::Bold)
$title.AutoSize = $true
$title.Location = New-Object System.Drawing.Point(565, 18)
$form.Controls.Add($title)

$subtitle = New-Object System.Windows.Forms.Label
$subtitle.Text = 'چسباندن دائمی زیرنویس و رساندن هوشمند حجم ویدیو به سقف دلخواه'
$subtitle.AutoSize = $true
$subtitle.ForeColor = [System.Drawing.Color]::DimGray
$subtitle.Location = New-Object System.Drawing.Point(401, 56)
$form.Controls.Add($subtitle)

$tabs = New-Object System.Windows.Forms.TabControl
$tabs.Location = New-Object System.Drawing.Point(18, 88)
$tabs.Size = New-Object System.Drawing.Size(848, 390)
$tabs.Anchor = 'Top,Left,Right'
$form.Controls.Add($tabs)

$tabBurn = New-Object System.Windows.Forms.TabPage
$tabBurn.Text = 'چسباندن زیرنویس'
$tabBurn.BackColor = [System.Drawing.Color]::White
$tabs.TabPages.Add($tabBurn)

$tabCompress = New-Object System.Windows.Forms.TabPage
$tabCompress.Text = 'فشرده‌سازی تا ۱۲۰ مگابایت'
$tabCompress.BackColor = [System.Drawing.Color]::White
$tabs.TabPages.Add($tabCompress)

$tabYoutube = New-Object System.Windows.Forms.TabPage
$tabYoutube.Text = 'دانلود از یوتیوب'
$tabYoutube.BackColor = [System.Drawing.Color]::White
$tabs.TabPages.Add($tabYoutube)

function Add-PathRow {
    param(
        [System.Windows.Forms.Control]$Parent,
        [int]$Y,
        [string]$LabelText,
        [string]$Filter
    )
    $label = New-Object System.Windows.Forms.Label
    $label.Text = $LabelText
    $label.Location = New-Object System.Drawing.Point(704, ($Y + 5))
    $label.Size = New-Object System.Drawing.Size(105, 28)
    $Parent.Controls.Add($label)

    $textBox = New-Object System.Windows.Forms.TextBox
    $textBox.Location = New-Object System.Drawing.Point(118, $Y)
    $textBox.Size = New-Object System.Drawing.Size(578, 28)
    $textBox.RightToLeft = [System.Windows.Forms.RightToLeft]::No
    $Parent.Controls.Add($textBox)

    $button = New-Object System.Windows.Forms.Button
    $button.Text = 'انتخاب'
    $button.Location = New-Object System.Drawing.Point(26, ($Y - 1))
    $button.Size = New-Object System.Drawing.Size(82, 31)
    $button.Add_Click({ Select-InputFile -Target $textBox -Filter $Filter }.GetNewClosure())
    $Parent.Controls.Add($button)
    return $textBox
}

$videoFilter = 'فایل‌های ویدیویی|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.ts|همهٔ فایل‌ها|*.*'
$subtitleFilter = 'فایل زیرنویس|*.srt;*.ass;*.ssa;*.vtt|همهٔ فایل‌ها|*.*'

$txtBurnVideo = Add-PathRow -Parent $tabBurn -Y 35 -LabelText 'فایل ویدیو' -Filter $videoFilter
$txtBurnSubtitle = Add-PathRow -Parent $tabBurn -Y 82 -LabelText 'فایل زیرنویس' -Filter $subtitleFilter

$lblFont = New-Object System.Windows.Forms.Label
$lblFont.Text = 'نام فونت'
$lblFont.Location = New-Object System.Drawing.Point(704, 136)
$lblFont.Size = New-Object System.Drawing.Size(105, 28)
$tabBurn.Controls.Add($lblFont)

$txtFont = New-Object System.Windows.Forms.TextBox
$txtFont.Text = 'Peyda Black  [ @mimvid ]'
$txtFont.Location = New-Object System.Drawing.Point(430, 132)
$txtFont.Size = New-Object System.Drawing.Size(266, 28)
$txtFont.RightToLeft = [System.Windows.Forms.RightToLeft]::No
$tabBurn.Controls.Add($txtFont)

$btnChooseFont = New-Object System.Windows.Forms.Button
$btnChooseFont.Text = 'انتخاب فونت'
$btnChooseFont.Location = New-Object System.Drawing.Point(320, 131)
$btnChooseFont.Size = New-Object System.Drawing.Size(102, 31)
$tabBurn.Controls.Add($btnChooseFont)

$lblFontSize = New-Object System.Windows.Forms.Label
$lblFontSize.Text = 'اندازهٔ فونت'
$lblFontSize.Location = New-Object System.Drawing.Point(197, 136)
$lblFontSize.Size = New-Object System.Drawing.Size(115, 28)
$tabBurn.Controls.Add($lblFontSize)

$numFontSize = New-Object System.Windows.Forms.NumericUpDown
$numFontSize.Minimum = 8
$numFontSize.Maximum = 72
$numFontSize.Value = 18
$numFontSize.Location = New-Object System.Drawing.Point(112, 132)
$numFontSize.Size = New-Object System.Drawing.Size(75, 28)
$tabBurn.Controls.Add($numFontSize)

$lblAlignment = New-Object System.Windows.Forms.Label
$lblAlignment.Text = 'محل زیرنویس'
$lblAlignment.Location = New-Object System.Drawing.Point(704, 178)
$lblAlignment.Size = New-Object System.Drawing.Size(105, 28)
$tabBurn.Controls.Add($lblAlignment)

$cmbAlignment = New-Object System.Windows.Forms.ComboBox
$cmbAlignment.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$cmbAlignment.Location = New-Object System.Drawing.Point(530, 174)
$cmbAlignment.Size = New-Object System.Drawing.Size(166, 28)
$cmbAlignment.Items.AddRange([object[]]@('پایین چپ', 'پایین وسط', 'پایین راست', 'میانه چپ', 'مرکز', 'میانه راست', 'بالا چپ', 'بالا وسط', 'بالا راست'))
$cmbAlignment.SelectedIndex = 1
$tabBurn.Controls.Add($cmbAlignment)

$lblMargin = New-Object System.Windows.Forms.Label
$lblMargin.Text = 'فاصلهٔ عمودی'
$lblMargin.Location = New-Object System.Drawing.Point(405, 178)
$lblMargin.Size = New-Object System.Drawing.Size(112, 28)
$tabBurn.Controls.Add($lblMargin)

$numMargin = New-Object System.Windows.Forms.NumericUpDown
$numMargin.Minimum = 0
$numMargin.Maximum = 300
$numMargin.Value = 30
$numMargin.Location = New-Object System.Drawing.Point(323, 174)
$numMargin.Size = New-Object System.Drawing.Size(77, 28)
$tabBurn.Controls.Add($numMargin)

$lblMarginH = New-Object System.Windows.Forms.Label
$lblMarginH.Text = 'فاصلهٔ افقی'
$lblMarginH.Location = New-Object System.Drawing.Point(205, 178)
$lblMarginH.Size = New-Object System.Drawing.Size(110, 28)
$tabBurn.Controls.Add($lblMarginH)

$numMarginH = New-Object System.Windows.Forms.NumericUpDown
$numMarginH.Minimum = 0
$numMarginH.Maximum = 300
$numMarginH.Value = 20
$numMarginH.Location = New-Object System.Drawing.Point(123, 174)
$numMarginH.Size = New-Object System.Drawing.Size(77, 28)
$tabBurn.Controls.Add($numMarginH)

$lblSubtitleColor = New-Object System.Windows.Forms.Label
$lblSubtitleColor.Text = 'رنگ زیرنویس'
$lblSubtitleColor.Location = New-Object System.Drawing.Point(704, 220)
$lblSubtitleColor.Size = New-Object System.Drawing.Size(105, 28)
$tabBurn.Controls.Add($lblSubtitleColor)

$btnSubtitleColor = New-Object System.Windows.Forms.Button
$btnSubtitleColor.Location = New-Object System.Drawing.Point(586, 216)
$btnSubtitleColor.Size = New-Object System.Drawing.Size(110, 31)
$tabBurn.Controls.Add($btnSubtitleColor)
Set-ColorButtonAppearance -Button $btnSubtitleColor -Color $script:SubtitleColor

$lblCrf = New-Object System.Windows.Forms.Label
$lblCrf.Text = 'کیفیت ویدیو'
$lblCrf.Location = New-Object System.Drawing.Point(477, 220)
$lblCrf.Size = New-Object System.Drawing.Size(100, 28)
$tabBurn.Controls.Add($lblCrf)

$numCrf = New-Object System.Windows.Forms.NumericUpDown
$numCrf.Minimum = 14
$numCrf.Maximum = 30
$numCrf.Value = 20
$numCrf.Location = New-Object System.Drawing.Point(397, 216)
$numCrf.Size = New-Object System.Drawing.Size(72, 28)
$tabBurn.Controls.Add($numCrf)

$chkBackground = New-Object System.Windows.Forms.CheckBox
$chkBackground.Text = 'فعال‌سازی پس‌زمینه'
$chkBackground.Checked = $true
$chkBackground.Location = New-Object System.Drawing.Point(175, 215)
$chkBackground.Size = New-Object System.Drawing.Size(200, 31)
$tabBurn.Controls.Add($chkBackground)

$lblBackgroundColor = New-Object System.Windows.Forms.Label
$lblBackgroundColor.Text = 'رنگ پس‌زمینه'
$lblBackgroundColor.Location = New-Object System.Drawing.Point(704, 261)
$lblBackgroundColor.Size = New-Object System.Drawing.Size(105, 28)
$tabBurn.Controls.Add($lblBackgroundColor)

$btnBackgroundColor = New-Object System.Windows.Forms.Button
$btnBackgroundColor.Location = New-Object System.Drawing.Point(586, 257)
$btnBackgroundColor.Size = New-Object System.Drawing.Size(110, 31)
$tabBurn.Controls.Add($btnBackgroundColor)
Set-ColorButtonAppearance -Button $btnBackgroundColor -Color $script:BackgroundColor

$lblOpacity = New-Object System.Windows.Forms.Label
$lblOpacity.Text = 'شفافیت'
$lblOpacity.Location = New-Object System.Drawing.Point(497, 261)
$lblOpacity.Size = New-Object System.Drawing.Size(80, 28)
$tabBurn.Controls.Add($lblOpacity)

$numOpacity = New-Object System.Windows.Forms.NumericUpDown
$numOpacity.Minimum = 0
$numOpacity.Maximum = 100
$numOpacity.Value = 70
$numOpacity.Location = New-Object System.Drawing.Point(416, 257)
$numOpacity.Size = New-Object System.Drawing.Size(75, 28)
$tabBurn.Controls.Add($numOpacity)

$lblOpacityPercent = New-Object System.Windows.Forms.Label
$lblOpacityPercent.Text = 'درصد'
$lblOpacityPercent.Location = New-Object System.Drawing.Point(360, 261)
$lblOpacityPercent.Size = New-Object System.Drawing.Size(50, 28)
$tabBurn.Controls.Add($lblOpacityPercent)

$lblBackgroundSize = New-Object System.Windows.Forms.Label
$lblBackgroundSize.Text = 'اندازهٔ حاشیه'
$lblBackgroundSize.Location = New-Object System.Drawing.Point(239, 261)
$lblBackgroundSize.Size = New-Object System.Drawing.Size(115, 28)
$tabBurn.Controls.Add($lblBackgroundSize)

$numBackgroundSize = New-Object System.Windows.Forms.NumericUpDown
$numBackgroundSize.Minimum = 0
$numBackgroundSize.Maximum = 20
$numBackgroundSize.DecimalPlaces = 1
$numBackgroundSize.Increment = 0.5
$numBackgroundSize.Value = 1
$numBackgroundSize.Location = New-Object System.Drawing.Point(151, 257)
$numBackgroundSize.Size = New-Object System.Drawing.Size(80, 28)
$tabBurn.Controls.Add($numBackgroundSize)

$chkBurnCompress = New-Object System.Windows.Forms.CheckBox
$chkBurnCompress.Text = 'فشرده‌سازی هم‌زمان تا حجم هدف'
$chkBurnCompress.Checked = $false
$chkBurnCompress.Location = New-Object System.Drawing.Point(275, 307)
$chkBurnCompress.Size = New-Object System.Drawing.Size(245, 30)
$tabBurn.Controls.Add($chkBurnCompress)

$numBurnTarget = New-Object System.Windows.Forms.NumericUpDown
$numBurnTarget.Minimum = 10
$numBurnTarget.Maximum = 4096
$numBurnTarget.Value = 120
$numBurnTarget.Enabled = $false
$numBurnTarget.Location = New-Object System.Drawing.Point(181, 306)
$numBurnTarget.Size = New-Object System.Drawing.Size(86, 28)
$tabBurn.Controls.Add($numBurnTarget)

$lblBurnTargetMb = New-Object System.Windows.Forms.Label
$lblBurnTargetMb.Text = 'مگابایت'
$lblBurnTargetMb.Enabled = $false
$lblBurnTargetMb.Location = New-Object System.Drawing.Point(103, 311)
$lblBurnTargetMb.Size = New-Object System.Drawing.Size(72, 28)
$tabBurn.Controls.Add($lblBurnTargetMb)

$chkBurnAutoScale = New-Object System.Windows.Forms.CheckBox
$chkBurnAutoScale.Text = 'کاهش هوشمند رزولوشن در صورت نیاز'
$chkBurnAutoScale.Checked = $true
$chkBurnAutoScale.Enabled = $false
$chkBurnAutoScale.Location = New-Object System.Drawing.Point(196, 340)
$chkBurnAutoScale.Size = New-Object System.Drawing.Size(324, 28)
$tabBurn.Controls.Add($chkBurnAutoScale)

$btnBurn = New-Object System.Windows.Forms.Button
$btnBurn.Text = 'ساخت ویدیوی زیرنویس‌دار'
$btnBurn.Location = New-Object System.Drawing.Point(536, 306)
$btnBurn.Size = New-Object System.Drawing.Size(260, 48)
$btnBurn.BackColor = [System.Drawing.Color]::FromArgb(42, 101, 220)
$btnBurn.ForeColor = [System.Drawing.Color]::White
$btnBurn.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
$tabBurn.Controls.Add($btnBurn)

$txtCompressVideo = Add-PathRow -Parent $tabCompress -Y 35 -LabelText 'فایل ویدیو' -Filter $videoFilter

$lblTarget = New-Object System.Windows.Forms.Label
$lblTarget.Text = 'سقف حجم'
$lblTarget.Location = New-Object System.Drawing.Point(704, 94)
$lblTarget.Size = New-Object System.Drawing.Size(105, 28)
$tabCompress.Controls.Add($lblTarget)

$numTarget = New-Object System.Windows.Forms.NumericUpDown
$numTarget.Minimum = 10
$numTarget.Maximum = 4096
$numTarget.Value = 120
$numTarget.Location = New-Object System.Drawing.Point(591, 90)
$numTarget.Size = New-Object System.Drawing.Size(105, 28)
$tabCompress.Controls.Add($numTarget)

$lblMb = New-Object System.Windows.Forms.Label
$lblMb.Text = 'مگابایت'
$lblMb.Location = New-Object System.Drawing.Point(515, 94)
$lblMb.Size = New-Object System.Drawing.Size(70, 28)
$tabCompress.Controls.Add($lblMb)

$chkAutoScale = New-Object System.Windows.Forms.CheckBox
$chkAutoScale.Text = 'در صورت نیاز، رزولوشن برای جلوگیری از افت شدید کیفیت کاهش یابد'
$chkAutoScale.Checked = $true
$chkAutoScale.Location = New-Object System.Drawing.Point(274, 143)
$chkAutoScale.Size = New-Object System.Drawing.Size(522, 30)
$tabCompress.Controls.Add($chkAutoScale)

$lblCompressHint = New-Object System.Windows.Forms.Label
$lblCompressHint.Text = "برنامه با رمزگذاری دوگذره، بیت‌ریت را از مدت ویدیو محاسبه می‌کند. اگر رسیدن به حجم هدف بدون افت محسوس ممکن نباشد، قبل از شروع هشدار می‌دهد."
$lblCompressHint.Location = New-Object System.Drawing.Point(74, 190)
$lblCompressHint.Size = New-Object System.Drawing.Size(720, 58)
$lblCompressHint.ForeColor = [System.Drawing.Color]::DimGray
$tabCompress.Controls.Add($lblCompressHint)

$btnCompress = New-Object System.Windows.Forms.Button
$btnCompress.Text = 'تحلیل و فشرده‌سازی'
$btnCompress.Location = New-Object System.Drawing.Point(536, 280)
$btnCompress.Size = New-Object System.Drawing.Size(260, 48)
$btnCompress.BackColor = [System.Drawing.Color]::FromArgb(20, 145, 105)
$btnCompress.ForeColor = [System.Drawing.Color]::White
$btnCompress.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
$tabCompress.Controls.Add($btnCompress)

$lblYoutubeUrl = New-Object System.Windows.Forms.Label
$lblYoutubeUrl.Text = 'لینک یوتیوب'
$lblYoutubeUrl.Location = New-Object System.Drawing.Point(704, 39)
$lblYoutubeUrl.Size = New-Object System.Drawing.Size(105, 28)
$tabYoutube.Controls.Add($lblYoutubeUrl)

$txtYoutubeUrl = New-Object System.Windows.Forms.TextBox
$txtYoutubeUrl.Location = New-Object System.Drawing.Point(26, 35)
$txtYoutubeUrl.Size = New-Object System.Drawing.Size(670, 28)
$txtYoutubeUrl.RightToLeft = [System.Windows.Forms.RightToLeft]::No
$tabYoutube.Controls.Add($txtYoutubeUrl)

$lblYoutubeFolder = New-Object System.Windows.Forms.Label
$lblYoutubeFolder.Text = 'پوشهٔ خروجی'
$lblYoutubeFolder.Location = New-Object System.Drawing.Point(704, 86)
$lblYoutubeFolder.Size = New-Object System.Drawing.Size(105, 28)
$tabYoutube.Controls.Add($lblYoutubeFolder)

$txtYoutubeFolder = New-Object System.Windows.Forms.TextBox
$defaultDownloadFolder = Join-Path ([Environment]::GetFolderPath('UserProfile')) 'Downloads'
$txtYoutubeFolder.Text = $defaultDownloadFolder
$txtYoutubeFolder.Location = New-Object System.Drawing.Point(118, 82)
$txtYoutubeFolder.Size = New-Object System.Drawing.Size(578, 28)
$txtYoutubeFolder.RightToLeft = [System.Windows.Forms.RightToLeft]::No
$tabYoutube.Controls.Add($txtYoutubeFolder)

$btnYoutubeFolder = New-Object System.Windows.Forms.Button
$btnYoutubeFolder.Text = 'انتخاب'
$btnYoutubeFolder.Location = New-Object System.Drawing.Point(26, 81)
$btnYoutubeFolder.Size = New-Object System.Drawing.Size(82, 31)
$tabYoutube.Controls.Add($btnYoutubeFolder)

$lblYoutubeQuality = New-Object System.Windows.Forms.Label
$lblYoutubeQuality.Text = 'کیفیت ویدیو'
$lblYoutubeQuality.Location = New-Object System.Drawing.Point(704, 135)
$lblYoutubeQuality.Size = New-Object System.Drawing.Size(105, 28)
$tabYoutube.Controls.Add($lblYoutubeQuality)

$cmbYoutubeQuality = New-Object System.Windows.Forms.ComboBox
$cmbYoutubeQuality.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$cmbYoutubeQuality.Location = New-Object System.Drawing.Point(530, 131)
$cmbYoutubeQuality.Size = New-Object System.Drawing.Size(166, 28)
$cmbYoutubeQuality.Items.AddRange([object[]]@('بهترین کیفیت', 'حداکثر 1080p', 'حداکثر 720p', 'حداکثر 480p'))
$cmbYoutubeQuality.SelectedIndex = 1
$tabYoutube.Controls.Add($cmbYoutubeQuality)

$lblYoutubeLanguages = New-Object System.Windows.Forms.Label
$lblYoutubeLanguages.Text = 'زبان زیرنویس'
$lblYoutubeLanguages.Location = New-Object System.Drawing.Point(405, 135)
$lblYoutubeLanguages.Size = New-Object System.Drawing.Size(112, 28)
$tabYoutube.Controls.Add($lblYoutubeLanguages)

$txtYoutubeLanguages = New-Object System.Windows.Forms.TextBox
$txtYoutubeLanguages.Text = 'fa.*,fa,en.*,en'
$txtYoutubeLanguages.Location = New-Object System.Drawing.Point(184, 131)
$txtYoutubeLanguages.Size = New-Object System.Drawing.Size(215, 28)
$txtYoutubeLanguages.RightToLeft = [System.Windows.Forms.RightToLeft]::No
$tabYoutube.Controls.Add($txtYoutubeLanguages)

$lblYoutubeLanguageHint = New-Object System.Windows.Forms.Label
$lblYoutubeLanguageHint.Text = 'مثال: fa.*,fa,en.*,en'
$lblYoutubeLanguageHint.Location = New-Object System.Drawing.Point(26, 135)
$lblYoutubeLanguageHint.Size = New-Object System.Drawing.Size(150, 28)
$lblYoutubeLanguageHint.ForeColor = [System.Drawing.Color]::DimGray
$tabYoutube.Controls.Add($lblYoutubeLanguageHint)

$lblYoutubeCookies = New-Object System.Windows.Forms.Label
$lblYoutubeCookies.Text = 'کوکی مرورگر'
$lblYoutubeCookies.Location = New-Object System.Drawing.Point(704, 178)
$lblYoutubeCookies.Size = New-Object System.Drawing.Size(105, 28)
$tabYoutube.Controls.Add($lblYoutubeCookies)

$cmbYoutubeCookies = New-Object System.Windows.Forms.ComboBox
$cmbYoutubeCookies.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$cmbYoutubeCookies.Location = New-Object System.Drawing.Point(488, 174)
$cmbYoutubeCookies.Size = New-Object System.Drawing.Size(208, 28)
$cmbYoutubeCookies.Items.AddRange([object[]]@('بدون کوکی', 'Firefox', 'Microsoft Edge', 'Google Chrome', 'Brave', 'فایل cookies.txt (پیشنهادی)'))
$cmbYoutubeCookies.SelectedIndex = 0
$tabYoutube.Controls.Add($cmbYoutubeCookies)

$lblYoutubeCookieHint = New-Object System.Windows.Forms.Label
$lblYoutubeCookieHint.Text = 'برای خطای ورود یا تشخیص ربات، فایل تازهٔ cookies.txt مطمئن‌تر است.'
$lblYoutubeCookieHint.Location = New-Object System.Drawing.Point(26, 178)
$lblYoutubeCookieHint.Size = New-Object System.Drawing.Size(450, 28)
$lblYoutubeCookieHint.ForeColor = [System.Drawing.Color]::DimGray
$tabYoutube.Controls.Add($lblYoutubeCookieHint)

$lblYoutubeCookieFile = New-Object System.Windows.Forms.Label
$lblYoutubeCookieFile.Text = 'فایل کوکی'
$lblYoutubeCookieFile.Location = New-Object System.Drawing.Point(704, 218)
$lblYoutubeCookieFile.Size = New-Object System.Drawing.Size(105, 28)
$lblYoutubeCookieFile.Enabled = $false
$tabYoutube.Controls.Add($lblYoutubeCookieFile)

$txtYoutubeCookieFile = New-Object System.Windows.Forms.TextBox
$txtYoutubeCookieFile.Location = New-Object System.Drawing.Point(118, 214)
$txtYoutubeCookieFile.Size = New-Object System.Drawing.Size(578, 28)
$txtYoutubeCookieFile.RightToLeft = [System.Windows.Forms.RightToLeft]::No
$txtYoutubeCookieFile.Enabled = $false
$tabYoutube.Controls.Add($txtYoutubeCookieFile)

$btnYoutubeCookieFile = New-Object System.Windows.Forms.Button
$btnYoutubeCookieFile.Text = 'انتخاب'
$btnYoutubeCookieFile.Location = New-Object System.Drawing.Point(26, 213)
$btnYoutubeCookieFile.Size = New-Object System.Drawing.Size(82, 31)
$btnYoutubeCookieFile.Enabled = $false
$tabYoutube.Controls.Add($btnYoutubeCookieFile)

$lblYoutubeHint = New-Object System.Windows.Forms.Label
$lblYoutubeHint.Text = 'Deno چالش‌های JavaScript را پردازش می‌کند؛ ویدیو و زیرنویس جدا ذخیره می‌شوند.'
$lblYoutubeHint.Location = New-Object System.Drawing.Point(74, 255)
$lblYoutubeHint.Size = New-Object System.Drawing.Size(720, 34)
$lblYoutubeHint.ForeColor = [System.Drawing.Color]::DimGray
$tabYoutube.Controls.Add($lblYoutubeHint)

$btnYoutubeDownload = New-Object System.Windows.Forms.Button
$btnYoutubeDownload.Text = 'دانلود ویدیو و زیرنویس'
$btnYoutubeDownload.Location = New-Object System.Drawing.Point(536, 300)
$btnYoutubeDownload.Size = New-Object System.Drawing.Size(260, 48)
$btnYoutubeDownload.BackColor = [System.Drawing.Color]::FromArgb(130, 60, 190)
$btnYoutubeDownload.ForeColor = [System.Drawing.Color]::White
$btnYoutubeDownload.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
$tabYoutube.Controls.Add($btnYoutubeDownload)

$lblTools = New-Object System.Windows.Forms.Label
$lblTools.Location = New-Object System.Drawing.Point(326, 490)
$lblTools.Size = New-Object System.Drawing.Size(540, 30)
$form.Controls.Add($lblTools)

$btnLocateTools = New-Object System.Windows.Forms.Button
$btnLocateTools.Text = 'معرفی FFmpeg'
$btnLocateTools.Location = New-Object System.Drawing.Point(18, 486)
$btnLocateTools.Size = New-Object System.Drawing.Size(150, 34)
$form.Controls.Add($btnLocateTools)

$lblState = New-Object System.Windows.Forms.Label
$lblState.Text = 'آماده'
$lblState.Location = New-Object System.Drawing.Point(580, 530)
$lblState.Size = New-Object System.Drawing.Size(286, 28)
$form.Controls.Add($lblState)

$progress = New-Object System.Windows.Forms.ProgressBar
$progress.Location = New-Object System.Drawing.Point(178, 529)
$progress.Size = New-Object System.Drawing.Size(390, 24)
$form.Controls.Add($progress)

$btnCancel = New-Object System.Windows.Forms.Button
$btnCancel.Text = 'لغو'
$btnCancel.Location = New-Object System.Drawing.Point(18, 526)
$btnCancel.Size = New-Object System.Drawing.Size(150, 32)
$btnCancel.Enabled = $false
$form.Controls.Add($btnCancel)

$txtLog = New-Object System.Windows.Forms.TextBox
$txtLog.Location = New-Object System.Drawing.Point(18, 568)
$txtLog.Size = New-Object System.Drawing.Size(848, 100)
$txtLog.Multiline = $true
$txtLog.ScrollBars = 'Vertical'
$txtLog.ReadOnly = $true
$txtLog.RightToLeft = [System.Windows.Forms.RightToLeft]::No
$txtLog.Font = New-Object System.Drawing.Font('Consolas', 9)
$txtLog.Anchor = 'Top,Bottom,Left,Right'
$form.Controls.Add($txtLog)

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 300
$timer.Add_Tick({
    if ($script:CurrentProcess -and $script:CurrentProcess.HasExited) {
        $script:CurrentProcess.WaitForExit()
        $exitCode = $script:CurrentProcess.ExitCode
        Collect-ProcessOutput
        $script:CurrentProcess.Dispose()
        $script:CurrentProcess = $null
        if ($script:Cancelled) {
            Complete-Operation $false 'عملیات لغو شد'
        }
        elseif ($exitCode -ne 0) {
            $toolName = if ($script:CurrentToolName) { $script:CurrentToolName } else { 'ابزار پردازش' }
            $allDetails = $script:LastToolOutput -join "`r`n"
            $details = ($script:LastToolOutput | Select-Object -Last 14) -join "`r`n"
            $isBotCheck = $allDetails -match 'Sign in to confirm you.re not a bot|LOGIN_REQUIRED'
            if ($script:OperationKind -eq 'youtube' -and $isBotCheck -and $script:YoutubeFallbackStage -and -not $script:YoutubeFallbackAttempted) {
                $script:YoutubeFallbackAttempted = $true
                Add-Log 'روش عادی توسط YouTube رد شد؛ تلاش خودکار با کلاینت سازگار جایگزین آغاز می‌شود'
                $script:StageQueue.Enqueue($script:YoutubeFallbackStage)
                Start-NextStage
            }
            else {
                $failureMessage = "$toolName با کد خطای $exitCode متوقف شد."
                if ($isBotCheck) {
                    $failureMessage += "`r`nYouTube این IP را بدون ورود نپذیرفت. «فایل cookies.txt» را انتخاب کنید و فایل تازهٔ حساب واردشده را معرفی کنید."
                }
                elseif ($allDetails -match 'Failed to decrypt with DPAPI') {
                    $failureMessage += "`r`nکوکی‌های Chrome/Edge در ویندوز قابل رمزگشایی نبودند. از فایل cookies.txt استفاده کنید."
                }
                if ($details) { $failureMessage += "`r`n`r`n$details" }
                Complete-Operation $false $failureMessage
            }
        }
        else {
            Start-NextStage
        }
    }
})

$btnLocateTools.Add_Click({
    $dialog = New-Object System.Windows.Forms.OpenFileDialog
    $dialog.Filter = 'ffmpeg.exe|ffmpeg.exe'
    $dialog.Title = 'فایل ffmpeg.exe را انتخاب کنید'
    if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        $folder = Split-Path -Parent $dialog.FileName
        $probe = Join-Path $folder 'ffprobe.exe'
        if (Test-Path -LiteralPath $probe) {
            $script:FfmpegPath = $dialog.FileName
            $script:FfprobePath = $probe
            $lblTools.Text = 'FFmpeg و FFprobe آماده‌اند'
            $lblTools.ForeColor = [System.Drawing.Color]::FromArgb(20, 120, 75)
            $lblTools.Tag = $true
            Add-Log 'ابزارهای ویدیویی شناسایی شدند'
        }
        else {
            [System.Windows.Forms.MessageBox]::Show('ffprobe.exe باید در همان پوشهٔ ffmpeg.exe باشد', 'فایل ناقص') | Out-Null
        }
    }
    $dialog.Dispose()
})

$btnYoutubeFolder.Add_Click({
    $selectedFolder = Select-OutputFolder -InitialPath $txtYoutubeFolder.Text.Trim()
    if ($selectedFolder) { $txtYoutubeFolder.Text = $selectedFolder }
})

$cmbYoutubeCookies.Add_SelectedIndexChanged({
    $useCookieFile = $cmbYoutubeCookies.SelectedIndex -eq 5
    $lblYoutubeCookieFile.Enabled = $useCookieFile
    $txtYoutubeCookieFile.Enabled = $useCookieFile
    $btnYoutubeCookieFile.Enabled = $useCookieFile
    if ($useCookieFile -and [string]::IsNullOrWhiteSpace($txtYoutubeCookieFile.Text)) {
        Select-InputFile -Target $txtYoutubeCookieFile -Filter 'فایل کوکی Netscape|cookies.txt;*.txt|همهٔ فایل‌ها|*.*'
    }
})

$btnYoutubeCookieFile.Add_Click({
    Select-InputFile -Target $txtYoutubeCookieFile -Filter 'فایل کوکی Netscape|cookies.txt;*.txt|همهٔ فایل‌ها|*.*'
})

$btnYoutubeDownload.Add_Click({
    try {
        $script:YtDlpPath = Resolve-Executable 'yt-dlp'
        if (-not $script:YtDlpPath) { throw 'yt-dlp.exe پیدا نشد؛ پوشهٔ tools را بررسی کنید' }
        $script:DenoPath = Resolve-Executable 'deno'
        if (-not $script:DenoPath) { throw 'deno.exe پیدا نشد؛ این ابزار برای چالش‌های جدید YouTube ضروری است' }
        if (-not $script:FfmpegPath -or -not $script:FfprobePath) {
            if (-not (Test-Tools)) { throw 'FFmpeg و FFprobe پیدا نشدند؛ پوشهٔ tools را بررسی کنید' }
        }

        $url = $txtYoutubeUrl.Text.Trim().Trim('"').Trim("'")
        if ($url -match '^\[(https?://[^\]]+)\]\(https?://[^\)]+\)$') { $url = $Matches[1] }
        if ($url -notmatch '^https?://') { throw 'لینک یوتیوب معتبر نیست' }
        $downloadFolder = $txtYoutubeFolder.Text.Trim()
        if ([string]::IsNullOrWhiteSpace($downloadFolder)) { throw 'پوشهٔ خروجی را انتخاب کنید' }
        if (-not (Test-Path -LiteralPath $downloadFolder -PathType Container)) {
            [void][System.IO.Directory]::CreateDirectory($downloadFolder)
        }
        $languages = $txtYoutubeLanguages.Text.Trim()
        if ([string]::IsNullOrWhiteSpace($languages)) { $languages = 'fa.*,fa,en.*,en' }

        $format = switch ($cmbYoutubeQuality.SelectedIndex) {
            0 { 'bv*+ba/b' }
            1 { 'bv*[height<=1080]+ba/b[height<=1080]/b' }
            2 { 'bv*[height<=720]+ba/b[height<=720]/b' }
            3 { 'bv*[height<=480]+ba/b[height<=480]/b' }
            default { 'bv*[height<=1080]+ba/b[height<=1080]/b' }
        }

        $toolsFolder = Split-Path -Parent $script:FfmpegPath
        $arguments = @(
            '--verbose',
            '--newline',
            '--no-playlist',
            '--windows-filenames',
            '--ffmpeg-location', $toolsFolder,
            '--js-runtimes', ('deno:' + $script:DenoPath),
            '--retries', '10',
            '--fragment-retries', '10',
            '--retry-sleep', '2',
            '-f', $format,
            '--merge-output-format', 'mp4',
            '--write-subs',
            '--write-auto-subs',
            '--sub-langs', $languages,
            '--sub-format', 'srt/best',
            '--convert-subs', 'srt',
            '--paths', $downloadFolder,
            '-o', '%(title).180B [%(id)s].%(ext)s'
        )
        $cookieBrowser = switch ($cmbYoutubeCookies.SelectedIndex) {
            1 { 'firefox' }
            2 { 'edge' }
            3 { 'chrome' }
            4 { 'brave' }
            default { $null }
        }
        if ($cookieBrowser) {
            $arguments += @('--cookies-from-browser', $cookieBrowser)
            Add-Log "استفاده از کوکی‌های مرورگر: $cookieBrowser"
        }
        elseif ($cmbYoutubeCookies.SelectedIndex -eq 5) {
            $cookieFile = $txtYoutubeCookieFile.Text.Trim()
            if ([string]::IsNullOrWhiteSpace($cookieFile)) {
                Select-InputFile -Target $txtYoutubeCookieFile -Filter 'فایل کوکی Netscape|cookies.txt;*.txt|همهٔ فایل‌ها|*.*'
                $cookieFile = $txtYoutubeCookieFile.Text.Trim()
            }
            if ([string]::IsNullOrWhiteSpace($cookieFile)) {
                throw 'فایل انتخاب نشد؛ برای عبور از بررسی ربات باید cookies.txt تازه را معرفی کنید'
            }
            if (-not (Test-Path -LiteralPath $cookieFile -PathType Leaf)) { throw 'فایل cookies.txt معتبر نیست' }
            $firstLine = [System.IO.File]::ReadLines($cookieFile) | Select-Object -First 1
            if ($firstLine) { $firstLine = $firstLine.TrimStart([char[]]@([char]0xFEFF)) }
            if ($firstLine -notin @('# HTTP Cookie File', '# Netscape HTTP Cookie File')) {
                throw 'فایل کوکی باید در قالب Netscape باشد و خط اول آن # Netscape HTTP Cookie File باشد'
            }
            $cookieText = [System.IO.File]::ReadAllText($cookieFile)
            if ($cookieText -notmatch '(?im)^(?:#HttpOnly_)?\.?([^\t]*\.)?youtube\.com\t') {
                throw 'این فایل هیچ کوکی مربوط به youtube.com ندارد؛ کوکی‌ها را دوباره از نشست واردشدهٔ YouTube صادر کنید'
            }
            if ($cookieText -notmatch '(?im)\t(?:SID|HSID|SSID|APISID|SAPISID|__Secure-1PSID|__Secure-3PSID|LOGIN_INFO)\t') {
                throw 'این فایل کوکی ورود YouTube را ندارد؛ ابتدا در پنجرهٔ Private وارد حساب شوید و سپس cookies.txt تازه بسازید'
            }
            $arguments += @('--cookies', $cookieFile)
            Add-Log 'استفاده از فایل محلی cookies.txt؛ محتوای کوکی‌ها در برنامه نمایش داده نمی‌شود'
        }
        $script:YoutubeFallbackStage = $null
        $script:YoutubeFallbackAttempted = $false
        if ($cmbYoutubeCookies.SelectedIndex -eq 0) {
            $fallbackArguments = @($arguments) + @(
                '--extractor-args', 'youtube:player_client=tv_simply,web_embedded',
                $url
            )
            $script:YoutubeFallbackStage = [pscustomobject]@{
                Name = 'تلاش جایگزین برای عبور از محدودیت ناشناس YouTube'
                ToolName = 'yt-dlp'
                CaptureOutput = $true
                Executable = $script:YtDlpPath
                Arguments = $fallbackArguments
            }
        }
        $arguments += $url

        $script:DownloadFolder = $downloadFolder
        $script:DownloadStarted = Get-Date
        $stage = [pscustomobject]@{
            Name = 'در حال دانلود ویدیو و زیرنویس از یوتیوب'
            ToolName = 'yt-dlp'
            CaptureOutput = $true
            Executable = $script:YtDlpPath
            Arguments = $arguments
        }
        Add-Log 'دانلود یوتیوب آغاز شد؛ گزارش کامل yt-dlp پس از پایان همین‌جا نمایش داده می‌شود'
        Start-OperationQueue -Stages @($stage) -SuccessMessage 'دانلود ویدیو و زیرنویس کامل شد' -OutputPath $downloadFolder -OperationKind 'youtube'
    }
    catch {
        [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'خطا', [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
    }
})

$btnCancel.Add_Click({
    $script:Cancelled = $true
    $script:StageQueue.Clear()
    try {
        if ($script:CurrentProcess -and -not $script:CurrentProcess.HasExited) {
            $script:CurrentProcess.Kill()
        }
    }
    catch { }
    $lblState.Text = 'در حال لغو…'
})

$btnChooseFont.Add_Click({
    $dialog = New-Object System.Windows.Forms.FontDialog
    $dialog.ShowEffects = $false
    try {
        $dialog.Font = New-Object System.Drawing.Font($txtFont.Text, [single]$numFontSize.Value)
    }
    catch {
        $dialog.Font = New-Object System.Drawing.Font('Segoe UI', [single]$numFontSize.Value)
    }
    if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        $txtFont.Text = $dialog.Font.FontFamily.Name
        $chosenSize = [math]::Round($dialog.Font.Size)
        if ($chosenSize -ge $numFontSize.Minimum -and $chosenSize -le $numFontSize.Maximum) {
            $numFontSize.Value = $chosenSize
        }
    }
    $dialog.Dispose()
})

$btnSubtitleColor.Add_Click({
    $selected = Select-Color -InitialColor $script:SubtitleColor
    if ($null -ne $selected) {
        $script:SubtitleColor = $selected
        Set-ColorButtonAppearance -Button $btnSubtitleColor -Color $selected
    }
})

$btnBackgroundColor.Add_Click({
    $selected = Select-Color -InitialColor $script:BackgroundColor
    if ($null -ne $selected) {
        $script:BackgroundColor = $selected
        Set-ColorButtonAppearance -Button $btnBackgroundColor -Color $selected
    }
})

$chkBackground.Add_CheckedChanged({
    $enabled = $chkBackground.Checked
    $btnBackgroundColor.Enabled = $enabled
    $numOpacity.Enabled = $enabled
    $numBackgroundSize.Enabled = $enabled
})

$chkBurnCompress.Add_CheckedChanged({
    $enabled = $chkBurnCompress.Checked
    $numBurnTarget.Enabled = $enabled
    $lblBurnTargetMb.Enabled = $enabled
    $chkBurnAutoScale.Enabled = $enabled
})

$btnBurn.Add_Click({
    $tempFolder = $null
    try {
        if (-not $script:FfmpegPath -or -not $script:FfprobePath) {
            if (-not (Test-Tools)) { throw 'ابتدا FFmpeg و FFprobe را کنار برنامه بگذارید یا معرفی کنید' }
        }
        $video = $txtBurnVideo.Text.Trim()
        $subtitlePath = $txtBurnSubtitle.Text.Trim()
        if (-not (Test-Path -LiteralPath $video -PathType Leaf)) { throw 'فایل ویدیو معتبر نیست' }
        if (-not (Test-Path -LiteralPath $subtitlePath -PathType Leaf)) { throw 'فایل زیرنویس معتبر نیست' }

        $baseName = [System.IO.Path]::GetFileNameWithoutExtension($video)
        $combinedMode = $chkBurnCompress.Checked
        $burnTargetMb = [double]$numBurnTarget.Value
        $suggestedOutput = if ($combinedMode) { $baseName + "_subtitled_${burnTargetMb}MB.mp4" } else { $baseName + '_subtitled.mp4' }
        $output = Select-OutputFile $suggestedOutput
        if (-not $output) { return }
        if ([System.IO.Path]::GetFullPath($output) -eq [System.IO.Path]::GetFullPath($video)) { throw 'فایل خروجی نباید با ورودی یکسان باشد' }

        $tempFolder = Join-Path ([System.IO.Path]::GetTempPath()) ('SubtitleVideoTool_' + [guid]::NewGuid().ToString('N'))
        [void][System.IO.Directory]::CreateDirectory($tempFolder)
        $subtitleExtension = [System.IO.Path]::GetExtension($subtitlePath).ToLowerInvariant()
        if ($subtitleExtension -notin @('.srt', '.ass', '.ssa', '.vtt')) { $subtitleExtension = '.srt' }
        $safeSubtitle = Join-Path $tempFolder ('subtitle' + $subtitleExtension)
        [System.IO.File]::Copy($subtitlePath, $safeSubtitle, $true)

        $filterPath = Get-SafeSubtitleFilterPath $safeSubtitle
        $fontName = $txtFont.Text.Trim().Replace("'", '')
        if ([string]::IsNullOrWhiteSpace($fontName)) { throw 'نام فونت خالی است' }
        $primaryColour = Convert-ColorToAss -Color $script:SubtitleColor -Alpha 0
        $alignment = $cmbAlignment.SelectedIndex + 1
        $marginV = [int]$numMargin.Value
        $marginH = [int]$numMarginH.Value
        if ($chkBackground.Checked) {
            $alpha = [math]::Round(255 * (1 - ([double]$numOpacity.Value / 100)))
            $backColour = Convert-ColorToAss -Color $script:BackgroundColor -Alpha $alpha
            $borderStyle = 4
            $outline = $numBackgroundSize.Value.ToString([System.Globalization.CultureInfo]::InvariantCulture)
        }
        else {
            $backColour = Convert-ColorToAss -Color $script:BackgroundColor -Alpha 255
            $borderStyle = 1
            $outline = '1'
        }
        $style = "FontName=$fontName,FontSize=$([int]$numFontSize.Value),PrimaryColour=$primaryColour,BackColour=$backColour,BorderStyle=$borderStyle,Outline=$outline,OutlineColour=&H00000000,Alignment=$alignment,MarginV=$marginV,MarginL=$marginH,MarginR=$marginH"
        $filter = "subtitles=filename='$filterPath':force_style='$style'"

        if (-not $combinedMode) {
            $arguments = @(
                '-hide_banner', '-y',
                '-i', $video,
                '-vf', $filter,
                '-c:v', 'libx264',
                '-crf', ([int]$numCrf.Value).ToString(),
                '-preset', 'medium',
                '-c:a', 'copy',
                '-movflags', '+faststart',
                $output
            )
            $stage = [pscustomobject]@{ Name = 'در حال چسباندن زیرنویس به ویدیو'; Executable = $script:FfmpegPath; Arguments = $arguments }
            Add-Log 'فرایند چسباندن زیرنویس آغاز شد'
            Start-OperationQueue -Stages @($stage) -SuccessMessage 'ویدیوی زیرنویس‌دار ساخته شد' -OutputPath $output -CleanupPaths @($tempFolder)
        }
        else {
            $targetBytes = [long]($burnTargetMb * 1MB)
            $probe = Get-ProbeData $video
            $duration = [double]::Parse([string]$probe.format.duration, [System.Globalization.CultureInfo]::InvariantCulture)
            if ($duration -le 0) { throw 'مدت ویدیو قابل تشخیص نیست' }
            $videoStream = $probe.streams | Where-Object { $_.codec_type -eq 'video' } | Select-Object -First 1
            if (-not $videoStream) { throw 'جریان ویدیویی پیدا نشد' }
            $hasAudio = [bool]($probe.streams | Where-Object { $_.codec_type -eq 'audio' } | Select-Object -First 1)
            $width = [int]$videoStream.width
            $height = [int]$videoStream.height
            $fps = Get-FrameRate ([string]$videoStream.r_frame_rate)

            $availableKbps = [math]::Floor(($targetBytes * 0.94 * 8 / $duration) / 1000)
            $audioKbps = if (-not $hasAudio) { 0 } elseif ($availableKbps -lt 1000) { 96 } else { 128 }
            $videoKbps = [math]::Floor($availableKbps - $audioKbps)
            if ($videoKbps -lt 250) {
                throw 'برای این مدت ویدیو، حجم هدف بدون افت شدید کیفیت قابل دستیابی نیست'
            }

            $scaleHeight = $height
            $effectiveWidth = $width
            $aspect = if ($height -gt 0) { $width / [double]$height } else { 16.0 / 9.0 }
            $bpp = ($videoKbps * 1000) / [math]::Max(1.0, ($width * $height * $fps))
            if ($chkBurnAutoScale.Checked -and $bpp -lt 0.045) {
                foreach ($candidate in @(1080, 900, 720, 576, 480, 360)) {
                    if ($candidate -ge $height) { continue }
                    $candidateWidth = [math]::Floor(($candidate * $aspect) / 2) * 2
                    $candidateBpp = ($videoKbps * 1000) / [math]::Max(1.0, ($candidateWidth * $candidate * $fps))
                    if ($candidateBpp -ge 0.045 -or $candidate -eq 360) {
                        $scaleHeight = $candidate
                        $effectiveWidth = $candidateWidth
                        $bpp = $candidateBpp
                        break
                    }
                }
            }
            $combinedFilter = if ($scaleHeight -lt $height) { "$filter,scale=-2:${scaleHeight}:flags=lanczos" } else { $filter }
            $qualityMessage = "بیت‌ریت ویدیو: $videoKbps کیلوبیت بر ثانیه`r`nصدا: $audioKbps کیلوبیت بر ثانیه`r`nرزولوشن خروجی: $effectiveWidth × $scaleHeight`r`nحجم هدف: $burnTargetMb مگابایت"
            Add-Log ($qualityMessage -replace "`r`n", ' — ')
            if ($bpp -lt 0.030) {
                $answer = [System.Windows.Forms.MessageBox]::Show(
                    "چسباندن زیرنویس و رسیدن به این حجم احتمالاً افت کیفیت محسوسی ایجاد می‌کند.`r`n`r`n$qualityMessage`r`n`r`nادامه می‌دهید؟",
                    'هشدار کیفیت',
                    [System.Windows.Forms.MessageBoxButtons]::YesNo,
                    [System.Windows.Forms.MessageBoxIcon]::Warning
                )
                if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) {
                    Remove-Item -LiteralPath $tempFolder -Recurse -Force -ErrorAction SilentlyContinue
                    return
                }
            }

            $passLog = Join-Path $tempFolder 'ffmpeg2pass_burn'
            $commonVideo = @(
                '-map', '0:v:0',
                '-vf', $combinedFilter,
                '-c:v', 'libx264',
                '-b:v', ($videoKbps.ToString() + 'k'),
                '-preset', 'medium',
                '-pix_fmt', 'yuv420p',
                '-passlogfile', $passLog
            )
            $passOne = @('-hide_banner', '-y', '-i', $video) + $commonVideo + @('-pass', '1', '-an', '-f', 'null', 'NUL')
            $passTwo = @('-hide_banner', '-y', '-i', $video) + $commonVideo + @('-pass', '2')
            if ($hasAudio) {
                $passTwo += @('-map', '0:a?', '-c:a', 'aac', '-b:a', ($audioKbps.ToString() + 'k'))
            }
            else {
                $passTwo += @('-an')
            }
            $passTwo += @('-movflags', '+faststart', '-max_muxing_queue_size', '2048', $output)
            $stages = @(
                [pscustomobject]@{ Name = 'زیرنویس و فشرده‌سازی — مرحلهٔ اول از دو'; Executable = $script:FfmpegPath; Arguments = $passOne },
                [pscustomobject]@{ Name = 'زیرنویس و فشرده‌سازی — مرحلهٔ دوم از دو'; Executable = $script:FfmpegPath; Arguments = $passTwo }
            )
            Add-Log 'چسباندن زیرنویس و فشرده‌سازی هم‌زمان آغاز شد'
            Start-OperationQueue -Stages $stages -SuccessMessage 'ویدیوی زیرنویس‌دار با حجم کنترل‌شده ساخته شد' -OutputPath $output -CleanupPaths @($tempFolder) -ExpectedMaximumBytes $targetBytes
        }
    }
    catch {
        if ($tempFolder -and (Test-Path -LiteralPath $tempFolder)) {
            Remove-Item -LiteralPath $tempFolder -Recurse -Force -ErrorAction SilentlyContinue
        }
        [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'خطا', [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
    }
})

$btnCompress.Add_Click({
    try {
        if (-not $script:FfmpegPath -or -not $script:FfprobePath) {
            if (-not (Test-Tools)) { throw 'ابتدا FFmpeg و FFprobe را کنار برنامه بگذارید یا معرفی کنید' }
        }
        $video = $txtCompressVideo.Text.Trim()
        if (-not (Test-Path -LiteralPath $video -PathType Leaf)) { throw 'فایل ویدیو معتبر نیست' }
        $targetMb = [double]$numTarget.Value
        $targetBytes = [long]($targetMb * 1MB)
        $inputSize = (Get-Item -LiteralPath $video).Length

        $baseName = [System.IO.Path]::GetFileNameWithoutExtension($video)
        $output = Select-OutputFile ($baseName + "_${targetMb}MB.mp4")
        if (-not $output) { return }
        if ([System.IO.Path]::GetFullPath($output) -eq [System.IO.Path]::GetFullPath($video)) { throw 'فایل خروجی نباید با ورودی یکسان باشد' }

        if ($inputSize -le $targetBytes) {
            if ([System.IO.Path]::GetExtension($video).Equals('.mp4', [System.StringComparison]::OrdinalIgnoreCase)) {
                [System.IO.File]::Copy($video, $output, $true)
                $script:Cancelled = $false
                $script:FinalOutput = $output
                $script:ExpectedMaximumBytes = $targetBytes
                $script:SuccessMessage = 'فایل از قبل زیر سقف تعیین‌شده بود و بدون بازفشرده‌سازی کپی شد'
                $script:OperationKind = 'media'
                Complete-Operation $true ''
            }
            else {
                $remuxArguments = @('-hide_banner', '-y', '-i', $video, '-map', '0:v:0', '-map', '0:a?', '-c', 'copy', '-movflags', '+faststart', $output)
                $remuxStage = [pscustomobject]@{ Name = 'فایل از سقف کوچک‌تر است؛ در حال تبدیل ظرف به ام‌پی‌فور'; Executable = $script:FfmpegPath; Arguments = $remuxArguments }
                Start-OperationQueue -Stages @($remuxStage) -SuccessMessage 'فایل بدون بازفشرده‌سازی در ظرف ام‌پی‌فور ذخیره شد' -OutputPath $output -ExpectedMaximumBytes $targetBytes
            }
            return
        }

        $probe = Get-ProbeData $video
        $duration = [double]::Parse([string]$probe.format.duration, [System.Globalization.CultureInfo]::InvariantCulture)
        if ($duration -le 0) { throw 'مدت ویدیو قابل تشخیص نیست' }
        $videoStream = $probe.streams | Where-Object { $_.codec_type -eq 'video' } | Select-Object -First 1
        if (-not $videoStream) { throw 'جریان ویدیویی پیدا نشد' }
        $hasAudio = [bool]($probe.streams | Where-Object { $_.codec_type -eq 'audio' } | Select-Object -First 1)
        $width = [int]$videoStream.width
        $height = [int]$videoStream.height
        $fps = Get-FrameRate ([string]$videoStream.r_frame_rate)

        $availableKbps = [math]::Floor(($targetBytes * 0.94 * 8 / $duration) / 1000)
        $audioKbps = if (-not $hasAudio) { 0 } elseif ($availableKbps -lt 1000) { 96 } else { 128 }
        $videoKbps = [math]::Floor($availableKbps - $audioKbps)
        if ($videoKbps -lt 250) {
            throw 'برای این مدت ویدیو، حجم هدف بیت‌ریت ویدیویی کمتر از حد قابل‌استفاده ایجاد می‌کند و رسیدن به آن بدون افت شدید ممکن نیست'
        }

        $scaleHeight = $height
        $filterArguments = @()
        $aspect = if ($height -gt 0) { $width / [double]$height } else { 16.0 / 9.0 }
        $effectiveWidth = $width
        $bpp = ($videoKbps * 1000) / [math]::Max(1.0, ($width * $height * $fps))
        if ($chkAutoScale.Checked -and $bpp -lt 0.045) {
            foreach ($candidate in @(1080, 900, 720, 576, 480, 360)) {
                if ($candidate -ge $height) { continue }
                $candidateWidth = [math]::Floor(($candidate * $aspect) / 2) * 2
                $candidateBpp = ($videoKbps * 1000) / [math]::Max(1.0, ($candidateWidth * $candidate * $fps))
                if ($candidateBpp -ge 0.045 -or $candidate -eq 360) {
                    $scaleHeight = $candidate
                    $effectiveWidth = $candidateWidth
                    $bpp = $candidateBpp
                    break
                }
            }
        }
        if ($scaleHeight -lt $height) {
            $filterArguments = @('-vf', "scale=-2:${scaleHeight}:flags=lanczos")
        }

        $qualityMessage = "بیت‌ریت ویدیو: $videoKbps کیلوبیت بر ثانیه`r`nصدا: $audioKbps کیلوبیت بر ثانیه`r`nرزولوشن خروجی: $effectiveWidth × $scaleHeight`r`nمدت: $([math]::Round($duration / 60, 1)) دقیقه"
        Add-Log ($qualityMessage -replace "`r`n", ' — ')
        if ($bpp -lt 0.030) {
            $answer = [System.Windows.Forms.MessageBox]::Show(
                "رسیدن به $targetMb مگابایت احتمالاً افت کیفیت محسوسی ایجاد می‌کند.`r`n`r`n$qualityMessage`r`n`r`nادامه می‌دهید؟",
                'هشدار کیفیت',
                [System.Windows.Forms.MessageBoxButtons]::YesNo,
                [System.Windows.Forms.MessageBoxIcon]::Warning
            )
            if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) { return }
        }

        $tempFolder = Join-Path ([System.IO.Path]::GetTempPath()) ('SubtitleVideoTool_' + [guid]::NewGuid().ToString('N'))
        [void][System.IO.Directory]::CreateDirectory($tempFolder)
        $passLog = Join-Path $tempFolder 'ffmpeg2pass'

        $commonVideo = @('-map', '0:v:0') + $filterArguments + @(
            '-c:v', 'libx264',
            '-b:v', ($videoKbps.ToString() + 'k'),
            '-preset', 'medium',
            '-pix_fmt', 'yuv420p',
            '-passlogfile', $passLog
        )
        $passOne = @('-hide_banner', '-y', '-i', $video) + $commonVideo + @(
            '-pass', '1', '-an', '-f', 'null', 'NUL'
        )
        $passTwo = @('-hide_banner', '-y', '-i', $video) + $commonVideo + @('-pass', '2')
        if ($hasAudio) {
            $passTwo += @('-map', '0:a?', '-c:a', 'aac', '-b:a', ($audioKbps.ToString() + 'k'))
        }
        else {
            $passTwo += @('-an')
        }
        $passTwo += @('-movflags', '+faststart', '-max_muxing_queue_size', '2048', $output)

        $stages = @(
            [pscustomobject]@{ Name = 'فشرده‌سازی دوگذره — مرحلهٔ اول از دو'; Executable = $script:FfmpegPath; Arguments = $passOne },
            [pscustomobject]@{ Name = 'فشرده‌سازی دوگذره — مرحلهٔ دوم از دو'; Executable = $script:FfmpegPath; Arguments = $passTwo }
        )
        Start-OperationQueue -Stages $stages -SuccessMessage 'فشرده‌سازی ویدیو کامل شد' -OutputPath $output -CleanupPaths @($tempFolder) -ExpectedMaximumBytes $targetBytes
    }
    catch {
        [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'خطا', [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
    }
})

$form.Add_FormClosing({
    if ($script:CurrentProcess -and -not $script:CurrentProcess.HasExited) {
        $answer = [System.Windows.Forms.MessageBox]::Show('عملیات در حال اجراست. برنامه بسته شود؟', 'تأیید خروج', [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Question)
        if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) {
            $_.Cancel = $true
            return
        }
        try { $script:CurrentProcess.Kill() } catch { }
    }
    Remove-TemporaryFiles
})

[void](Test-Tools)
Add-Log 'برنامه آماده است'
[void]$form.ShowDialog()
if ($script:AppIcon) { $script:AppIcon.Dispose() }
$form.Dispose()
