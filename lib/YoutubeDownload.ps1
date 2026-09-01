<#
    YoutubeDownload.ps1

    GUI-free helpers for the YouTube download tab, the bundled-tool lookup and
    the yt-dlp command line. Nothing in this file touches System.Windows.Forms,
    so every function can be exercised by tests\Run-Tests.ps1.

    Both SubtitleVideoTool.ps1 and the test runner dot-source this file.
#>

# ---------------------------------------------------------------- command line

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

# ------------------------------------------------------------- bundled tooling

function Resolve-Executable {
    param(
        [Parameter(Mandatory)][string]$Name,
        [string]$AppRoot
    )

    if ([string]::IsNullOrWhiteSpace($AppRoot)) { $AppRoot = $script:AppRoot }
    if ([string]::IsNullOrWhiteSpace($AppRoot)) { $AppRoot = (Get-Location).Path }

    $candidates = @(
        (Join-Path (Join-Path $AppRoot 'tools') ($Name + '.exe')),
        (Join-Path $AppRoot ($Name + '.exe'))
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Get-Item -LiteralPath $candidate).FullName
        }
    }

    $command = Get-Command ($Name + '.exe') -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $command) {
        $command = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    }
    if ($command) { return $command.Source }
    return $null
}

function Get-ToolVersionArgument {
    param([Parameter(Mandatory)][string]$Name)

    switch ($Name.ToLowerInvariant()) {
        'ffmpeg'  { return @('-version') }
        'ffprobe' { return @('-version') }
        default   { return @('--version') }
    }
}

function Test-ExecutableRunnable {
    <#
        A bundled .exe that merely exists is not enough: a truncated or partly
        downloaded binary still passes Test-Path, but Windows refuses to start
        it. Launching it once with its version flag is the only reliable check.
    #>
    param(
        [Parameter(Mandatory)][AllowEmptyString()][AllowNull()][string]$Path,
        [string[]]$Arguments = @(),
        [int]$TimeoutSeconds = 20
    )

    $result = [pscustomobject]@{
        Path       = $Path
        IsRunnable = $false
        Version    = ''
        Reason     = ''
    }

    if ([string]::IsNullOrWhiteSpace($Path)) {
        $result.Reason = 'مسیر ابزار خالی است'
        return $result
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $result.Reason = 'فایل ابزار پیدا نشد'
        return $result
    }

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $Path
    if ($Arguments -and $Arguments.Count -gt 0) { $startInfo.Arguments = Join-ProcessArguments $Arguments }
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $startInfo.StandardErrorEncoding = [System.Text.Encoding]::UTF8

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        [void]$process.Start()
    }
    catch {
        $result.Reason = 'فایل ابزار اجرا نشد؛ احتمالا ناقص یا خراب دانلود شده است'
        $process.Dispose()
        return $result
    }

    try {
        $outputTask = $process.StandardOutput.ReadToEndAsync()
        $errorTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill() } catch { }
            $result.Reason = 'ابزار در زمان تعیین‌شده پاسخ نداد'
            return $result
        }
        $text = ''
        try { $text = [string]$outputTask.Result } catch { }
        if ([string]::IsNullOrWhiteSpace($text)) {
            try { $text = [string]$errorTask.Result } catch { }
        }
        $firstLine = ($text -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1)
        if ($firstLine) { $result.Version = $firstLine.Trim() }
        $result.IsRunnable = $true
    }
    finally {
        $process.Dispose()
    }
    return $result
}

function Get-ToolStatus {
    <#
        One status row per requested tool. -SkipRunCheck keeps the call cheap
        when only the lookup itself is under test.
    #>
    param(
        [Parameter(Mandatory)][string[]]$Names,
        [string]$AppRoot,
        [switch]$SkipRunCheck
    )

    foreach ($name in $Names) {
        $path = Resolve-Executable -Name $name -AppRoot $AppRoot
        $row = [pscustomobject]@{
            Name       = $name
            Path       = $path
            IsPresent  = [bool]$path
            IsRunnable = $false
            Version    = ''
            Reason     = ''
        }
        if (-not $path) {
            $row.Reason = "$name پیدا نشد"
        }
        elseif ($SkipRunCheck) {
            $row.IsRunnable = $true
        }
        else {
            $probe = Test-ExecutableRunnable -Path $path -Arguments (Get-ToolVersionArgument $name)
            $row.IsRunnable = $probe.IsRunnable
            $row.Version = $probe.Version
            if (-not $probe.IsRunnable) { $row.Reason = "$name اجرا نشد: $($probe.Reason)" }
        }
        $row
    }
}

# ------------------------------------------------------------------ user input

function Remove-InvisibleMark {
    param([AllowEmptyString()][AllowNull()][string]$Text)
    if ($null -eq $Text) { return '' }
    return ($Text -replace '[\u200B-\u200F\u202A-\u202E\u2066-\u2069\uFEFF]', '')
}

function ConvertTo-CleanYoutubeUrl {
    <#
        Accepts what people actually paste: quoted links, markdown links, links
        without a scheme, youtu.be / shorts / live / embed forms, and links that
        carry playlist or tracking parameters.
    #>
    param([AllowEmptyString()][AllowNull()][string]$Text)

    $cleaned = (Remove-InvisibleMark $Text).Trim()
    $cleaned = $cleaned.Trim([char[]]@('"', "'", '<', '>', '`')).Trim()
    if ([string]::IsNullOrWhiteSpace($cleaned)) { throw 'لینک یوتیوب را وارد کنید' }

    $markdown = [regex]::Match($cleaned, '^\[[^\]]*\]\(\s*(?<url>[^)\s]+)\s*\)$')
    if ($markdown.Success) { $cleaned = $markdown.Groups['url'].Value }

    if ($cleaned -notmatch '(?i)^[a-z][a-z0-9+.\-]*://') {
        if ($cleaned -match '(?i)^(www\.|m\.|music\.)?(youtube\.com|youtu\.be|youtube-nocookie\.com)(/|$)') {
            $cleaned = 'https://' + $cleaned
        }
    }
    if ($cleaned -notmatch '(?i)^https?://') { throw 'لینک یوتیوب معتبر نیست' }

    $uri = $null
    if (-not [System.Uri]::TryCreate($cleaned, [System.UriKind]::Absolute, [ref]$uri)) { throw 'لینک یوتیوب معتبر نیست' }

    $hostName = $uri.Host.ToLowerInvariant() -replace '^www\.', ''
    $youtubeHosts = @('youtube.com', 'm.youtube.com', 'music.youtube.com', 'youtu.be', 'youtube-nocookie.com')
    if ($youtubeHosts -notcontains $hostName) {
        # yt-dlp supports far more than YouTube; hand anything else over untouched.
        return $uri.AbsoluteUri
    }

    $query = [ordered]@{}
    if ($uri.Query.Length -gt 1) {
        foreach ($pair in $uri.Query.TrimStart('?').Split('&')) {
            if ([string]::IsNullOrWhiteSpace($pair)) { continue }
            $parts = $pair.Split([char[]]'=', 2)
            $key = [System.Uri]::UnescapeDataString($parts[0])
            $value = ''
            if ($parts.Count -eq 2) { $value = [System.Uri]::UnescapeDataString($parts[1]) }
            if (-not $query.Contains($key)) { $query[$key] = $value }
        }
    }

    $path = $uri.AbsolutePath.Trim('/')
    $videoId = $null
    if ($hostName -eq 'youtu.be') {
        $videoId = $path.Split('/')[0]
    }
    elseif ($path -match '^(?i)(shorts|live|embed|v)/([^/]+)') {
        $videoId = $Matches[2]
    }
    elseif ($query.Contains('v')) {
        $videoId = [string]$query['v']
    }

    if ($videoId -and $videoId -match '^[A-Za-z0-9_-]{11}$') {
        $result = 'https://www.youtube.com/watch?v=' + $videoId
        foreach ($key in @('t', 'start')) {
            if ($query.Contains($key) -and -not [string]::IsNullOrWhiteSpace([string]$query[$key])) {
                $result += '&t=' + [System.Uri]::EscapeDataString([string]$query[$key])
                break
            }
        }
        return $result
    }

    # Playlist, channel or search links stay as they are; only tracking noise goes.
    $dropped = @('si', 'pp', 'feature', 'ab_channel', 'gclid', 'fbclid')
    $kept = @()
    foreach ($key in $query.Keys) {
        $lowerKey = ([string]$key).ToLowerInvariant()
        if ($dropped -contains $lowerKey) { continue }
        if ($lowerKey.StartsWith('utm_')) { continue }
        $kept += ([System.Uri]::EscapeDataString([string]$key) + '=' + [System.Uri]::EscapeDataString([string]$query[$key]))
    }
    $rebuilt = $uri.GetLeftPart([System.UriPartial]::Path)
    if ($kept.Count -gt 0) { $rebuilt += '?' + ($kept -join '&') }
    return $rebuilt
}

function Get-YoutubeFormatSelector {
    <#
        Index order matches the quality combo box.
        For the capped modes H.264 + AAC is tried first so that
        --merge-output-format mp4 really produces a playable MP4 instead of
        silently falling back to Matroska; the generic selectors stay as
        fallbacks so a video without an AVC rendition still downloads.
    #>
    param([int]$QualityIndex = 1)

    $height = switch ($QualityIndex) {
        0 { 0 }
        1 { 1080 }
        2 { 720 }
        3 { 480 }
        default { 1080 }
    }

    if ($height -le 0) { return 'bv*+ba/b' }
    return "bv*[height<=$height][vcodec^=avc1]+ba[acodec^=mp4a]/bv*[height<=$height]+ba/b[height<=$height]/b"
}

function ConvertTo-SubtitleLanguageList {
    param([AllowEmptyString()][AllowNull()][string]$Text)

    $default = 'fa.*,fa,en.*,en'
    $normalized = (Remove-InvisibleMark $Text)
    if ([string]::IsNullOrWhiteSpace($normalized)) { return $default }

    $normalized = $normalized -replace '[،؛;\s]+', ','
    $tokens = New-Object 'System.Collections.Generic.List[string]'
    foreach ($token in $normalized.Split(',')) {
        $value = $token.Trim()
        if ([string]::IsNullOrEmpty($value)) { continue }
        if ($value -notmatch '^-?[A-Za-z0-9_.*-]+$') { throw "کد زبان زیرنویس معتبر نیست: $value" }
        if (-not ($tokens -contains $value)) { [void]$tokens.Add($value) }
    }
    if ($tokens.Count -eq 0) { return $default }
    return ($tokens -join ',')
}

function Resolve-DownloadFolder {
    param(
        [AllowEmptyString()][AllowNull()][string]$Path,
        [switch]$CreateIfMissing
    )

    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'پوشهٔ خروجی را انتخاب کنید' }

    $candidate = (Remove-InvisibleMark $Path).Trim().Trim([char[]]@('"', "'")).Trim()
    if ([string]::IsNullOrWhiteSpace($candidate)) { throw 'پوشهٔ خروجی را انتخاب کنید' }

    $invalidCharacters = [System.IO.Path]::GetInvalidPathChars()
    foreach ($character in $candidate.ToCharArray()) {
        if ($invalidCharacters -contains $character) { throw 'مسیر پوشهٔ خروجی نویسهٔ غیرمجاز دارد' }
    }
    if (-not [System.IO.Path]::IsPathRooted($candidate)) { throw 'مسیر پوشهٔ خروجی باید کامل باشد؛ مثل D:\Videos' }

    $full = $null
    try { $full = [System.IO.Path]::GetFullPath($candidate) }
    catch { throw 'مسیر پوشهٔ خروجی معتبر نیست' }

    if (Test-Path -LiteralPath $full -PathType Leaf) { throw 'مسیر واردشده یک فایل است، نه پوشه' }
    if (-not (Test-Path -LiteralPath $full -PathType Container)) {
        if (-not $CreateIfMissing) { throw 'پوشهٔ خروجی وجود ندارد' }
        try { [void][System.IO.Directory]::CreateDirectory($full) }
        catch { throw ('پوشهٔ خروجی ساخته نشد: ' + $_.Exception.Message) }
    }

    if ($full.Length -gt 3) { $full = $full.TrimEnd('\') }
    return $full
}

# --------------------------------------------------------------------- cookies

function Get-CookieSourceOption {
    <#
        Single source of truth for the cookie combo box. The GUI binds these
        objects directly, so the download handler never depends on item order.
    #>
    return @(
        [pscustomobject]@{ Key = 'none';         Label = 'بدون کوکی' },
        [pscustomobject]@{ Key = 'browserlogin'; Label = 'ورود با مرورگر (نشست اختصاصی برنامه)' },
        [pscustomobject]@{ Key = 'file';         Label = 'فایل cookies.txt' },
        [pscustomobject]@{ Key = 'firefox';      Label = 'کوکی Firefox نصب‌شده' },
        [pscustomobject]@{ Key = 'edge';         Label = 'کوکی Microsoft Edge' },
        [pscustomobject]@{ Key = 'chrome';       Label = 'کوکی Google Chrome' },
        [pscustomobject]@{ Key = 'brave';        Label = 'کوکی Brave' }
    )
}

function Test-CookieFile {
    <#
        Validates shape only. Cookie values are never returned, logged or copied.
    #>
    param([AllowEmptyString()][AllowNull()][string]$Path)

    $result = [pscustomobject]@{ IsValid = $false; Message = '' }

    if ([string]::IsNullOrWhiteSpace($Path)) {
        $result.Message = 'فایل انتخاب نشد؛ برای عبور از بررسی ربات باید cookies.txt تازه را معرفی کنید'
        return $result
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $result.Message = 'فایل cookies.txt پیدا نشد'
        return $result
    }

    $item = Get-Item -LiteralPath $Path
    if ($item.Length -eq 0) {
        $result.Message = 'فایل cookies.txt خالی است'
        return $result
    }
    if ($item.Length -gt 8MB) {
        $result.Message = 'این فایل برای cookies.txt غیرعادی بزرگ است؛ فایل درست را انتخاب کنید'
        return $result
    }

    $text = ''
    try { $text = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8) }
    catch {
        $result.Message = 'فایل cookies.txt خوانده نشد'
        return $result
    }
    $text = $text.TrimStart([char]0xFEFF)

    # The magic line python's MozillaCookieJar looks for, but tolerant about
    # exporters that append their own text to it or put a comment line first.
    $hasHeader = $false
    $index = 0
    foreach ($line in ($text -split "`r?`n")) {
        if ($index -ge 5) { break }
        $index++
        if ($line -match '(?i)#\s*(Netscape\s+)?HTTP\s+Cookie\s+File') { $hasHeader = $true; break }
    }
    if (-not $hasHeader) {
        $result.Message = 'فایل کوکی باید در قالب Netscape باشد و سطر «# Netscape HTTP Cookie File» را داشته باشد'
        return $result
    }

    if ($text -notmatch '(?im)^(?:#HttpOnly_)?\.?([^\t]*\.)?youtube\.com\t') {
        $result.Message = 'این فایل هیچ کوکی مربوط به youtube.com ندارد؛ کوکی‌ها را دوباره از نشست واردشدهٔ YouTube صادر کنید'
        return $result
    }
    if ($text -notmatch '(?im)\t(?:SID|HSID|SSID|APISID|SAPISID|__Secure-1PSID|__Secure-3PSID|LOGIN_INFO)\t') {
        $result.Message = 'این فایل کوکی ورود YouTube را ندارد؛ ابتدا در پنجرهٔ Private وارد حساب شوید و سپس cookies.txt تازه بسازید'
        return $result
    }

    $result.IsValid = $true
    $result.Message = 'قالب فایل cookies.txt تأیید شد'
    return $result
}

# ----------------------------------------------- browser sign-in (own profile)

function Get-FirefoxCandidatePath {
    $roots = @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:LOCALAPPDATA)
    $paths = @()
    foreach ($root in $roots) {
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        $paths += (Join-Path $root 'Mozilla Firefox\firefox.exe')
    }
    return $paths
}

function Find-SignInBrowser {
    <#
        Firefox is the only browser whose cookie store yt-dlp can read from an
        arbitrary profile folder on Windows; Chromium profiles are sealed with
        DPAPI plus app-bound encryption.
    #>
    param([string[]]$SearchPaths)

    if (-not $SearchPaths -or $SearchPaths.Count -eq 0) { $SearchPaths = Get-FirefoxCandidatePath }
    foreach ($path in $SearchPaths) {
        if ([string]::IsNullOrWhiteSpace($path)) { continue }
        if (Test-Path -LiteralPath $path -PathType Leaf) { return (Get-Item -LiteralPath $path).FullName }
    }
    return $null
}

function Get-SignInProfileFolder {
    param([string]$Root)

    if ([string]::IsNullOrWhiteSpace($Root)) { $Root = [Environment]::GetFolderPath('LocalApplicationData') }
    return (Join-Path (Join-Path $Root 'SubtitleVideoTool') 'youtube-signin-profile')
}

function New-BrowserSignInArgument {
    param(
        [Parameter(Mandatory)][string]$ProfileFolder,
        [string]$Url = 'https://www.youtube.com/'
    )
    return @('-no-remote', '-profile', $ProfileFolder, $Url)
}

function Test-SignInProfile {
    param([AllowEmptyString()][AllowNull()][string]$ProfileFolder)

    $result = [pscustomobject]@{ IsReady = $false; Message = '' }
    if ([string]::IsNullOrWhiteSpace($ProfileFolder) -or -not (Test-Path -LiteralPath $ProfileFolder -PathType Container)) {
        $result.Message = 'هنوز با دکمهٔ «ورود به یوتیوب» وارد حساب نشده‌اید'
        return $result
    }
    $database = Join-Path $ProfileFolder 'cookies.sqlite'
    if (-not (Test-Path -LiteralPath $database -PathType Leaf)) {
        $result.Message = 'در نشست اختصاصی مرورگر هنوز کوکی ذخیره نشده است؛ دوباره وارد شوید'
        return $result
    }
    if ((Get-Item -LiteralPath $database).Length -eq 0) {
        $result.Message = 'کوکی نشست اختصاصی مرورگر خالی است؛ دوباره وارد شوید'
        return $result
    }
    $result.IsReady = $true
    $result.Message = 'نشست اختصاصی مرورگر آماده است'
    return $result
}

function Get-CookiesFromBrowserValue {
    param(
        [Parameter(Mandatory)][string]$Browser,
        [string]$ProfileFolder
    )
    if ([string]::IsNullOrWhiteSpace($ProfileFolder)) { return $Browser }
    return ($Browser + ':' + $ProfileFolder)
}

# ------------------------------------------------------------ yt-dlp arguments

function Get-YoutubeFallbackPlayerClient {
    <#
        Tried in order when the default clients are answered with a bot check.
        Kept as data so a future YouTube change is a one-line edit.
    #>
    return @(
        'tv_simply,web_embedded',
        'android_vr,web_safari'
    )
}

function New-YtDlpArgument {
    param(
        [Parameter(Mandatory)][string]$Url,
        [Parameter(Mandatory)][string]$DownloadFolder,
        [Parameter(Mandatory)][string]$FormatSelector,
        [Parameter(Mandatory)][string]$SubtitleLanguages,
        [string]$FfmpegFolder,
        [string]$DenoPath,
        [string]$CookieFile,
        [string]$CookiesFromBrowser,
        [string]$PlayerClients,
        [string]$OutputTemplate = '%(title).180B [%(id)s].%(ext)s'
    )

    $arguments = New-Object 'System.Collections.Generic.List[string]'
    # --ignore-config keeps a stray %APPDATA%\yt-dlp\config from changing what
    # the window says it is going to do.
    foreach ($value in @('--ignore-config', '--verbose', '--newline', '--no-playlist', '--windows-filenames')) {
        [void]$arguments.Add($value)
    }
    if (-not [string]::IsNullOrWhiteSpace($FfmpegFolder)) { $arguments.AddRange([string[]]@('--ffmpeg-location', $FfmpegFolder)) }
    if (-not [string]::IsNullOrWhiteSpace($DenoPath)) { $arguments.AddRange([string[]]@('--js-runtimes', ('deno:' + $DenoPath))) }
    $arguments.AddRange([string[]]@(
        '--retries', '10',
        '--fragment-retries', '10',
        '--retry-sleep', '2',
        '-f', $FormatSelector,
        '--merge-output-format', 'mp4',
        '--write-subs',
        '--write-auto-subs',
        '--sub-langs', $SubtitleLanguages,
        '--sub-format', 'srt/best',
        '--convert-subs', 'srt',
        '--paths', $DownloadFolder,
        '-o', $OutputTemplate
    ))
    if (-not [string]::IsNullOrWhiteSpace($CookieFile)) { $arguments.AddRange([string[]]@('--cookies', $CookieFile)) }
    if (-not [string]::IsNullOrWhiteSpace($CookiesFromBrowser)) { $arguments.AddRange([string[]]@('--cookies-from-browser', $CookiesFromBrowser)) }
    if (-not [string]::IsNullOrWhiteSpace($PlayerClients)) { $arguments.AddRange([string[]]@('--extractor-args', ('youtube:player_client=' + $PlayerClients))) }
    [void]$arguments.Add($Url)
    return $arguments.ToArray()
}

# ------------------------------------------------------------ failure analysis

function Get-YtDlpFailureKind {
    param([AllowEmptyString()][AllowNull()][string]$Output)

    if ([string]::IsNullOrWhiteSpace($Output)) { return 'Unknown' }

    if ($Output -match '(?i)Failed to decrypt with DPAPI|Could not decrypt.{0,30}cookie|app-bound encryption') { return 'Dpapi' }
    if ($Output -match '(?i)could not (?:find|copy|open).{0,40}cookie|unsupported browser|cookie database') { return 'CookieRead' }
    if ($Output -match '(?i)Sign in to confirm you.{0,3}re not a bot') { return 'BotCheck' }
    if ($Output -match '(?i)Sign in to confirm your age|age-restricted|inappropriate for some users') { return 'AgeRestricted' }
    if ($Output -match '(?i)LOGIN_REQUIRED|Please sign in|account cookies are no longer valid|only available to') { return 'LoginRequired' }
    if ($Output -match '(?i)ffmpeg is not installed|ffprobe and ffmpeg not found|ffmpeg not found|requested merging of multiple formats but') { return 'FfmpegMissing' }
    if ($Output -match '(?i)Private video|members-only|Join this channel') { return 'PrivateVideo' }
    if ($Output -match '(?i)available in your country|blocked it in your country|geo.?restrict|not available from your location') { return 'GeoBlocked' }
    if ($Output -match '(?i)Video unavailable|This video is (?:no longer|not) available|removed by the uploader') { return 'Unavailable' }
    if ($Output -match '(?i)Requested format is not available') { return 'FormatUnavailable' }
    if ($Output -match '(?i)ProxyError|Unable to connect to proxy|Tunnel connection failed|SOCKS') { return 'Proxy' }
    if ($Output -match '(?i)timed out|Connection refused|Connection reset|getaddrinfo|Name or service not known|Network is unreachable|Remote end closed|Temporary failure in name resolution|SSLError|CERTIFICATE_VERIFY_FAILED') { return 'Network' }
    if ($Output -match '(?i)No space left|not enough space|Errno 28') { return 'DiskFull' }
    if ($Output -match '(?i)Permission denied|Errno 13|Access is denied') { return 'Permission' }
    return 'Unknown'
}

function Get-YtDlpFailureAdvice {
    param([Parameter(Mandatory)][string]$Kind)

    switch ($Kind) {
        'BotCheck'          { return 'YouTube این IP را بدون ورود نپذیرفت. این محدودیت سمت YouTube است، نه ایراد برنامه. گزینهٔ «ورود با مرورگر» را انتخاب و یک بار وارد حساب شوید، یا فایل تازهٔ cookies.txt را معرفی کنید.' }
        'LoginRequired'     { return 'YouTube برای این ویدیو ورود به حساب می‌خواهد. گزینهٔ «ورود با مرورگر» را انتخاب و یک بار وارد حساب شوید، یا فایل تازهٔ cookies.txt را معرفی کنید.' }
        'AgeRestricted'     { return 'این ویدیو محدودیت سنی دارد و بدون حساب واردشده دانلود نمی‌شود.' }
        'Dpapi'             { return 'کوکی‌های Chrome/Edge در ویندوز قابل رمزگشایی نبودند. از گزینهٔ «ورود با مرورگر» یا فایل cookies.txt استفاده کنید.' }
        'CookieRead'        { return 'کوکی مرورگر خوانده نشد؛ معمولاً چون همان مرورگر باز است. مرورگر را کامل ببندید — در Chrome و Edge حتی پردازش‌های پس‌زمینه — و دوباره تلاش کنید، یا از «ورود با مرورگر» یا فایل cookies.txt استفاده کنید.' }
        'FfmpegMissing'     { return 'ادغام صدا و تصویر و تبدیل زیرنویس بدون FFmpeg ممکن نیست. فایل tools\ffmpeg.exe ناقص یا خراب است و باید دوباره تهیه شود.' }
        'PrivateVideo'      { return 'این ویدیو خصوصی یا ویژهٔ اعضاست و با حساب فعلی قابل دانلود نیست.' }
        'GeoBlocked'        { return 'این ویدیو در موقعیت جغرافیایی فعلی در دسترس نیست.' }
        'Unavailable'       { return 'این ویدیو دیگر روی YouTube در دسترس نیست.' }
        'FormatUnavailable' { return 'کیفیت انتخاب‌شده برای این ویدیو موجود نیست؛ کیفیت دیگری را انتخاب کنید.' }
        'Proxy'             { return 'اتصال به پروکسی برقرار نشد. تنظیمات پروکسی یا VPN را بررسی کنید.' }
        'Network'           { return 'ارتباط شبکه با YouTube برقرار نشد. اتصال اینترنت، VPN یا پروکسی را بررسی کنید.' }
        'DiskFull'          { return 'فضای دیسک برای ذخیرهٔ خروجی کافی نیست.' }
        'Permission'        { return 'دسترسی نوشتن در پوشهٔ خروجی وجود ندارد؛ پوشهٔ دیگری انتخاب کنید.' }
        default             { return '' }
    }
}

function Get-DownloadFailureMessage {
    param(
        [Parameter(Mandatory)][string]$ToolName,
        [int]$ExitCode,
        [AllowEmptyString()][AllowNull()][string]$Output,
        [int]$DetailLineCount = 14
    )

    $kind = Get-YtDlpFailureKind -Output $Output
    $message = "$ToolName با کد خطای $ExitCode متوقف شد."
    $advice = Get-YtDlpFailureAdvice -Kind $kind
    if ($advice) { $message += "`r`n" + $advice }

    if (-not [string]::IsNullOrWhiteSpace($Output)) {
        $lines = @($Output -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        if ($lines.Count -gt 0) {
            $tail = @($lines | Select-Object -Last $DetailLineCount)
            $message += "`r`n`r`n" + ($tail -join "`r`n")
        }
    }
    return $message
}
