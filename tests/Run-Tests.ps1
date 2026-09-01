<#
    Run-Tests.ps1

    Repeatable tests for lib\YoutubeDownload.ps1. No module has to be installed:
    run it with the Windows PowerShell that ships with the operating system.

        powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\Run-Tests.ps1

    Exit code 0 means every test passed, 1 means at least one failed.
    Cookie fixtures below contain invented values only; no real session data is
    read, written or printed by this file.
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$script:AppRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path (Join-Path $script:AppRoot 'lib') 'YoutubeDownload.ps1')

# --------------------------------------------------------------- tiny harness

$script:Passed = 0
$script:Failures = New-Object 'System.Collections.Generic.List[string]'
$script:Group = ''

function Start-TestGroup {
    param([Parameter(Mandatory)][string]$Name)
    $script:Group = $Name
    Write-Host ''
    Write-Host ("== " + $Name) -ForegroundColor Cyan
}

function Add-TestResult {
    param([Parameter(Mandatory)][string]$Name, [bool]$Ok, [string]$Detail)
    if ($Ok) {
        $script:Passed++
        Write-Host ("  [ok]   " + $Name) -ForegroundColor DarkGray
    }
    else {
        $script:Failures.Add(("{0} > {1}: {2}" -f $script:Group, $Name, $Detail))
        Write-Host ("  [FAIL] " + $Name + " -- " + $Detail) -ForegroundColor Red
    }
}

function Assert-Equal {
    param([Parameter(Mandatory)][string]$Name, $Expected, $Actual)
    $ok = ([string]$Expected -ceq [string]$Actual)
    Add-TestResult -Name $Name -Ok $ok -Detail ("expected <{0}> got <{1}>" -f $Expected, $Actual)
}

function Assert-True {
    param([Parameter(Mandatory)][string]$Name, $Value, [string]$Detail = 'expected true')
    Add-TestResult -Name $Name -Ok ([bool]$Value) -Detail $Detail
}

function Assert-False {
    param([Parameter(Mandatory)][string]$Name, $Value, [string]$Detail = 'expected false')
    Add-TestResult -Name $Name -Ok (-not [bool]$Value) -Detail $Detail
}

function Assert-Contains {
    param([Parameter(Mandatory)][string]$Name, [string[]]$Collection, [string]$Value)
    Add-TestResult -Name $Name -Ok ([bool]($Collection -ccontains $Value)) -Detail ("<{0}> not found" -f $Value)
}

function Assert-Sequence {
    <# The value that follows a flag, checked positionally. #>
    param([Parameter(Mandatory)][string]$Name, [string[]]$Collection, [string]$Flag, [string]$Value)
    $index = [array]::IndexOf($Collection, $Flag)
    $ok = ($index -ge 0) -and ($index + 1 -lt $Collection.Count) -and ($Collection[$index + 1] -ceq $Value)
    $actual = ''
    if ($index -ge 0 -and $index + 1 -lt $Collection.Count) { $actual = $Collection[$index + 1] }
    Add-TestResult -Name $Name -Ok $ok -Detail ("{0} -> expected <{1}> got <{2}>" -f $Flag, $Value, $actual)
}

function Assert-Throws {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][scriptblock]$Script, [string]$MessageLike)
    try {
        & $Script | Out-Null
        Add-TestResult -Name $Name -Ok $false -Detail 'no error was raised'
    }
    catch {
        $message = $_.Exception.Message
        if ($MessageLike -and ($message -notlike $MessageLike)) {
            Add-TestResult -Name $Name -Ok $false -Detail ("message <{0}> does not match <{1}>" -f $message, $MessageLike)
        }
        else {
            Add-TestResult -Name $Name -Ok $true -Detail ''
        }
    }
}

# ------------------------------------------------------------------- fixtures

$script:FixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('svt-tests-' + [guid]::NewGuid().ToString('N'))
$fixtureTools = Join-Path $script:FixtureRoot 'tools'
[void][System.IO.Directory]::CreateDirectory($fixtureTools)

# A real, tiny Windows executable stands in for a healthy bundled tool.
$goodTool = Join-Path $fixtureTools 'goodtool.exe'
Copy-Item -LiteralPath (Join-Path $env:SystemRoot 'System32\where.exe') -Destination $goodTool -Force

# A text file with an .exe name stands in for a truncated or half-downloaded
# binary: it passes Test-Path but Windows refuses to start it.
$brokenTool = Join-Path $fixtureTools 'brokentool.exe'
Set-Content -LiteralPath $brokenTool -Value 'this is not a portable executable' -Encoding Ascii

# A tool sitting next to the application instead of inside tools\.
$besideTool = Join-Path $script:FixtureRoot 'besidetool.exe'
Copy-Item -LiteralPath $goodTool -Destination $besideTool -Force

$TAB = [char]9
function New-CookieFixture {
    param([Parameter(Mandatory)][string]$FileName, [Parameter(Mandatory)][AllowEmptyString()][string[]]$Lines)
    $path = Join-Path $script:FixtureRoot $FileName
    Set-Content -LiteralPath $path -Value ($Lines -join "`r`n") -Encoding UTF8
    return $path
}

function New-CookieLine {
    param([string]$Domain = '.youtube.com', [string]$Name = 'SID', [string]$Value = 'FIXTURE-NOT-A-REAL-VALUE')
    return ($Domain, 'TRUE', '/', 'TRUE', '1900000000', $Name, $Value) -join $TAB
}

# --------------------------------------------------------- native round-trip

Add-Type -Namespace SubtitleVideoToolTests -Name NativeCommandLine -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("shell32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
public static extern System.IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

[System.Runtime.InteropServices.DllImport("kernel32.dll")]
public static extern System.IntPtr LocalFree(System.IntPtr hMem);
'@

function ConvertFrom-CommandLine {
    <#
        Parses a command line exactly the way a launched program does, so the
        quoting helper is checked against Windows itself and not against a
        second copy of the same assumptions.
    #>
    param([Parameter(Mandatory)][string]$CommandLine)

    $count = 0
    $pointer = [SubtitleVideoToolTests.NativeCommandLine]::CommandLineToArgvW(('prog.exe ' + $CommandLine), [ref]$count)
    if ($pointer -eq [IntPtr]::Zero) { throw 'CommandLineToArgvW failed' }
    try {
        $parsed = @()
        for ($i = 1; $i -lt $count; $i++) {
            $itemPointer = [System.Runtime.InteropServices.Marshal]::ReadIntPtr($pointer, $i * [IntPtr]::Size)
            $parsed += [System.Runtime.InteropServices.Marshal]::PtrToStringUni($itemPointer)
        }
        return $parsed
    }
    finally { [void][SubtitleVideoToolTests.NativeCommandLine]::LocalFree($pointer) }
}

try {

# ============================================================ finding the tools

Start-TestGroup 'پیدا کردن ابزارهای همراه برنامه'

Assert-Equal -Name 'tools\ ابزار داخل پوشهٔ' -Expected $goodTool -Actual (Resolve-Executable -Name 'goodtool' -AppRoot $script:FixtureRoot)
Assert-Equal -Name 'ابزار کنار فایل برنامه' -Expected $besideTool -Actual (Resolve-Executable -Name 'besidetool' -AppRoot $script:FixtureRoot)
Assert-True  -Name 'ابزار ناموجود پیدا نمی‌شود' -Value ($null -eq (Resolve-Executable -Name 'svt-no-such-tool-xyz' -AppRoot $script:FixtureRoot))
Assert-Equal -Name 'آرگومان نسخهٔ ffmpeg' -Expected '-version' -Actual ((Get-ToolVersionArgument 'ffmpeg') -join ' ')
Assert-Equal -Name 'آرگومان نسخهٔ yt-dlp' -Expected '--version' -Actual ((Get-ToolVersionArgument 'yt-dlp') -join ' ')

$goodProbe = Test-ExecutableRunnable -Path $goodTool -Arguments @('/?')
Assert-True -Name 'ابزار سالم اجرا می‌شود' -Value $goodProbe.IsRunnable

$brokenProbe = Test-ExecutableRunnable -Path $brokenTool -Arguments @('--version')
Assert-False -Name 'ابزار خراب اجرا نمی‌شود' -Value $brokenProbe.IsRunnable
Assert-True  -Name 'ابزار خراب علت دارد' -Value (-not [string]::IsNullOrWhiteSpace($brokenProbe.Reason))

$missingProbe = Test-ExecutableRunnable -Path (Join-Path $script:FixtureRoot 'nothing.exe')
Assert-False -Name 'ابزار ناموجود اجرا نمی‌شود' -Value $missingProbe.IsRunnable

$status = @(Get-ToolStatus -Names @('goodtool', 'brokentool', 'svt-no-such-tool-xyz') -AppRoot $script:FixtureRoot)
Assert-Equal -Name 'سه ردیف وضعیت' -Expected 3 -Actual $status.Count
Assert-True  -Name 'ابزار سالم قابل اجرا' -Value $status[0].IsRunnable
Assert-True  -Name 'ابزار خراب موجود است' -Value $status[1].IsPresent
Assert-False -Name 'ابزار خراب قابل اجرا نیست' -Value $status[1].IsRunnable
Assert-False -Name 'ابزار ناموجود موجود نیست' -Value $status[2].IsPresent

# ============================================================ cleaning the URL

Start-TestGroup 'پاک‌سازی لینک‌های یوتیوب'

$expectedWatch = 'https://www.youtube.com/watch?v=jNQXAC9IVRw'
Assert-Equal -Name 'لینک ساده' -Expected $expectedWatch -Actual (ConvertTo-CleanYoutubeUrl 'https://www.youtube.com/watch?v=jNQXAC9IVRw')
Assert-Equal -Name 'حذف پارامتر پلی‌لیست' -Expected $expectedWatch -Actual (ConvertTo-CleanYoutubeUrl 'https://www.youtube.com/watch?v=jNQXAC9IVRw&list=PLabcdef&index=4')
Assert-Equal -Name 'حذف پارامتر ردیابی si' -Expected $expectedWatch -Actual (ConvertTo-CleanYoutubeUrl 'https://youtu.be/jNQXAC9IVRw?si=Ab12Cd34')
Assert-Equal -Name 'لینک کوتاه youtu.be' -Expected $expectedWatch -Actual (ConvertTo-CleanYoutubeUrl 'https://youtu.be/jNQXAC9IVRw')
Assert-Equal -Name 'لینک shorts' -Expected $expectedWatch -Actual (ConvertTo-CleanYoutubeUrl 'https://www.youtube.com/shorts/jNQXAC9IVRw')
Assert-Equal -Name 'لینک embed' -Expected $expectedWatch -Actual (ConvertTo-CleanYoutubeUrl 'https://www.youtube.com/embed/jNQXAC9IVRw')
Assert-Equal -Name 'لینک live' -Expected $expectedWatch -Actual (ConvertTo-CleanYoutubeUrl 'https://www.youtube.com/live/jNQXAC9IVRw')
Assert-Equal -Name 'لینک موبایل' -Expected $expectedWatch -Actual (ConvertTo-CleanYoutubeUrl 'https://m.youtube.com/watch?v=jNQXAC9IVRw')
Assert-Equal -Name 'لینک بدون https' -Expected $expectedWatch -Actual (ConvertTo-CleanYoutubeUrl 'www.youtube.com/watch?v=jNQXAC9IVRw')
Assert-Equal -Name 'لینک داخل گیومه' -Expected $expectedWatch -Actual (ConvertTo-CleanYoutubeUrl '  "https://www.youtube.com/watch?v=jNQXAC9IVRw"  ')
Assert-Equal -Name 'لینک مارک‌داون' -Expected $expectedWatch -Actual (ConvertTo-CleanYoutubeUrl '[ویدیو](https://www.youtube.com/watch?v=jNQXAC9IVRw)')
Assert-Equal -Name 'لینک داخل کروشهٔ زاویه‌دار' -Expected $expectedWatch -Actual (ConvertTo-CleanYoutubeUrl '<https://www.youtube.com/watch?v=jNQXAC9IVRw>')
Assert-Equal -Name 'حذف نویسه‌های نامرئی فارسی' -Expected $expectedWatch -Actual (ConvertTo-CleanYoutubeUrl ([char]0x200F + 'https://www.youtube.com/watch?v=jNQXAC9IVRw' + [char]0x200E))
Assert-Equal -Name 'حفظ زمان شروع' -Expected ($expectedWatch + '&t=42') -Actual (ConvertTo-CleanYoutubeUrl 'https://www.youtube.com/watch?v=jNQXAC9IVRw&t=42')
Assert-Equal -Name 'پلی‌لیست دست‌نخورده می‌ماند' -Expected 'https://www.youtube.com/playlist?list=PLabcdef' -Actual (ConvertTo-CleanYoutubeUrl 'https://www.youtube.com/playlist?list=PLabcdef&si=track123')
Assert-Equal -Name 'سایت غیر یوتیوب تغییر نمی‌کند' -Expected 'https://vimeo.com/123456' -Actual (ConvertTo-CleanYoutubeUrl 'https://vimeo.com/123456')
Assert-Throws -Name 'لینک خالی رد می‌شود' -Script { ConvertTo-CleanYoutubeUrl '' }
Assert-Throws -Name 'فقط فاصله رد می‌شود' -Script { ConvertTo-CleanYoutubeUrl '     ' }
Assert-Throws -Name 'متن بی‌ربط رد می‌شود' -Script { ConvertTo-CleanYoutubeUrl 'یک متن معمولی' }
Assert-Throws -Name 'مسیر فایل رد می‌شود' -Script { ConvertTo-CleanYoutubeUrl 'C:\videos\clip.mp4' }

# ================================================== quality and subtitle langs

Start-TestGroup 'انتخاب کیفیت و زبان زیرنویس'

Assert-Equal -Name 'بهترین کیفیت' -Expected 'bv*+ba/b' -Actual (Get-YoutubeFormatSelector 0)
Assert-Equal -Name 'حداکثر ۱۰۸۰' -Expected 'bv*[height<=1080][vcodec^=avc1]+ba[acodec^=mp4a]/bv*[height<=1080]+ba/b[height<=1080]/b' -Actual (Get-YoutubeFormatSelector 1)
Assert-Equal -Name 'حداکثر ۷۲۰' -Expected 'bv*[height<=720][vcodec^=avc1]+ba[acodec^=mp4a]/bv*[height<=720]+ba/b[height<=720]/b' -Actual (Get-YoutubeFormatSelector 2)
Assert-Equal -Name 'حداکثر ۴۸۰' -Expected 'bv*[height<=480][vcodec^=avc1]+ba[acodec^=mp4a]/bv*[height<=480]+ba/b[height<=480]/b' -Actual (Get-YoutubeFormatSelector 3)
Assert-Equal -Name 'اندیس نامعتبر به ۱۰۸۰ برمی‌گردد' -Expected (Get-YoutubeFormatSelector 1) -Actual (Get-YoutubeFormatSelector 99)
Assert-True  -Name 'هر انتخاب کیفیت صدا دارد' -Value ((Get-YoutubeFormatSelector 2) -like '*+ba*')

Assert-Equal -Name 'زبان خالی به پیش‌فرض' -Expected 'fa.*,fa,en.*,en' -Actual (ConvertTo-SubtitleLanguageList '')
Assert-Equal -Name 'زبان فقط فاصله به پیش‌فرض' -Expected 'fa.*,fa,en.*,en' -Actual (ConvertTo-SubtitleLanguageList '    ')
Assert-Equal -Name 'حذف فاصله‌های اضافه' -Expected 'fa,en' -Actual (ConvertTo-SubtitleLanguageList ' fa ,  en ')
Assert-Equal -Name 'ویرگول فارسی' -Expected 'fa,en' -Actual (ConvertTo-SubtitleLanguageList 'fa، en')
Assert-Equal -Name 'حذف تکراری' -Expected 'fa,en' -Actual (ConvertTo-SubtitleLanguageList 'fa,en,fa')
Assert-Equal -Name 'الگوی ستاره‌دار' -Expected 'fa.*,en-US' -Actual (ConvertTo-SubtitleLanguageList 'fa.*,en-US')
Assert-Equal -Name 'حذف نویسهٔ نامرئی از زبان' -Expected 'fa' -Actual (ConvertTo-SubtitleLanguageList ([char]0x200F + 'fa'))
Assert-Throws -Name 'کد زبان نامعتبر رد می‌شود' -Script { ConvertTo-SubtitleLanguageList 'fa,!!!' }

# ============================================================== output folder

Start-TestGroup 'اعتبارسنجی مسیر پوشهٔ خروجی'

Assert-Throws -Name 'مسیر خالی' -Script { Resolve-DownloadFolder -Path '' } -MessageLike '*پوشهٔ خروجی*'
Assert-Throws -Name 'مسیر فقط فاصله' -Script { Resolve-DownloadFolder -Path '    ' } -MessageLike '*پوشهٔ خروجی*'
Assert-Throws -Name 'مسیر تهی' -Script { Resolve-DownloadFolder -Path $null } -MessageLike '*پوشهٔ خروجی*'
Assert-Throws -Name 'مسیر نسبی' -Script { Resolve-DownloadFolder -Path 'videos\output' }
Assert-Throws -Name 'نویسهٔ غیرمجاز' -Script { Resolve-DownloadFolder -Path 'C:\videos|bad' }
Assert-Throws -Name 'پوشهٔ ناموجود بدون ساخت' -Script { Resolve-DownloadFolder -Path (Join-Path $script:FixtureRoot 'never-created') }

$asFile = Join-Path $script:FixtureRoot 'not-a-folder.txt'
Set-Content -LiteralPath $asFile -Value 'x' -Encoding Ascii
Assert-Throws -Name 'مسیر به فایل اشاره دارد' -Script { Resolve-DownloadFolder -Path $asFile } -MessageLike '*فایل است*'

$createdFolder = Join-Path $script:FixtureRoot 'created output'
Assert-Equal -Name 'ساخت پوشهٔ ناموجود' -Expected $createdFolder -Actual (Resolve-DownloadFolder -Path $createdFolder -CreateIfMissing)
Assert-True  -Name 'پوشه واقعاً ساخته شد' -Value (Test-Path -LiteralPath $createdFolder -PathType Container)

$persianFolder = Join-Path $script:FixtureRoot 'ویدیو های من'
Assert-Equal -Name 'مسیر فارسی با فاصله' -Expected $persianFolder -Actual (Resolve-DownloadFolder -Path $persianFolder -CreateIfMissing)
Assert-Equal -Name 'حذف بک‌اسلش پایانی' -Expected $persianFolder -Actual (Resolve-DownloadFolder -Path ($persianFolder + '\') -CreateIfMissing)
Assert-Equal -Name 'حذف گیومهٔ دور مسیر' -Expected $persianFolder -Actual (Resolve-DownloadFolder -Path ('"' + $persianFolder + '"') -CreateIfMissing)

# ================================================================ cookies.txt

Start-TestGroup 'اعتبارسنجی فایل cookies.txt'

$validCookie = New-CookieFixture -FileName 'valid-cookies.txt' -Lines @(
    '# Netscape HTTP Cookie File',
    '# This file is generated by a fixture. Do not edit.',
    '',
    (New-CookieLine)
)
Assert-True -Name 'فایل معتبر' -Value (Test-CookieFile -Path $validCookie).IsValid

$headerWithSuffix = New-CookieFixture -FileName 'suffix-cookies.txt' -Lines @(
    '# Netscape HTTP Cookie File (exported by a browser add-on)',
    (New-CookieLine)
)
Assert-True -Name 'سرصفحه با متن اضافه' -Value (Test-CookieFile -Path $headerWithSuffix).IsValid

$headerLater = New-CookieFixture -FileName 'late-header-cookies.txt' -Lines @(
    '# exported at 2026-09-02',
    '# Netscape HTTP Cookie File',
    (New-CookieLine)
)
Assert-True -Name 'سرصفحه در سطر دوم' -Value (Test-CookieFile -Path $headerLater).IsValid

$shortHeader = New-CookieFixture -FileName 'short-header-cookies.txt' -Lines @(
    '# HTTP Cookie File',
    (New-CookieLine)
)
Assert-True -Name 'سرصفحهٔ کوتاه' -Value (Test-CookieFile -Path $shortHeader).IsValid

$httpOnly = New-CookieFixture -FileName 'httponly-cookies.txt' -Lines @(
    '# Netscape HTTP Cookie File',
    (New-CookieLine -Domain '#HttpOnly_.youtube.com' -Name '__Secure-3PSID')
)
Assert-True -Name 'کوکی HttpOnly پذیرفته می‌شود' -Value (Test-CookieFile -Path $httpOnly).IsValid

$noHeader = New-CookieFixture -FileName 'no-header-cookies.txt' -Lines @((New-CookieLine))
Assert-False -Name 'بدون سرصفحهٔ Netscape' -Value (Test-CookieFile -Path $noHeader).IsValid

$noYoutube = New-CookieFixture -FileName 'no-youtube-cookies.txt' -Lines @(
    '# Netscape HTTP Cookie File',
    (New-CookieLine -Domain '.example.com')
)
Assert-False -Name 'بدون دامنهٔ youtube.com' -Value (Test-CookieFile -Path $noYoutube).IsValid

$noLogin = New-CookieFixture -FileName 'no-login-cookies.txt' -Lines @(
    '# Netscape HTTP Cookie File',
    (New-CookieLine -Name 'VISITOR_INFO1_LIVE')
)
Assert-False -Name 'بدون کوکی ورود' -Value (Test-CookieFile -Path $noLogin).IsValid

$emptyCookie = Join-Path $script:FixtureRoot 'empty-cookies.txt'
Set-Content -LiteralPath $emptyCookie -Value '' -NoNewline -Encoding Ascii
Assert-False -Name 'فایل خالی' -Value (Test-CookieFile -Path $emptyCookie).IsValid
Assert-False -Name 'مسیر خالی' -Value (Test-CookieFile -Path '').IsValid
Assert-False -Name 'فایل ناموجود' -Value (Test-CookieFile -Path (Join-Path $script:FixtureRoot 'missing.txt')).IsValid

$noLoginResult = Test-CookieFile -Path $noLogin
Assert-True -Name 'پیام خطا محتوای کوکی را لو نمی‌دهد' -Value ($noLoginResult.Message -notmatch 'FIXTURE-NOT-A-REAL-VALUE')

# ================================================== yt-dlp command generation

Start-TestGroup 'تولید دستور yt-dlp'

$commandFolder = Join-Path $script:FixtureRoot 'ویدیو های من'
$commandArguments = @(New-YtDlpArgument `
    -Url 'https://www.youtube.com/watch?v=jNQXAC9IVRw' `
    -DownloadFolder $commandFolder `
    -FormatSelector (Get-YoutubeFormatSelector 1) `
    -SubtitleLanguages 'fa.*,fa,en.*,en' `
    -FfmpegFolder $fixtureTools `
    -DenoPath $goodTool)

Assert-Contains -Name 'نادیده‌گرفتن فایل پیکربندی' -Collection $commandArguments -Value '--ignore-config'
Assert-Contains -Name 'بدون پلی‌لیست' -Collection $commandArguments -Value '--no-playlist'
Assert-Contains -Name 'نام فایل سازگار با ویندوز' -Collection $commandArguments -Value '--windows-filenames'
Assert-Contains -Name 'گزارش کامل' -Collection $commandArguments -Value '--verbose'
Assert-Sequence -Name 'مسیر FFmpeg' -Collection $commandArguments -Flag '--ffmpeg-location' -Value $fixtureTools
Assert-Sequence -Name 'معرفی Deno' -Collection $commandArguments -Flag '--js-runtimes' -Value ('deno:' + $goodTool)
Assert-Sequence -Name 'انتخاب فرمت' -Collection $commandArguments -Flag '-f' -Value (Get-YoutubeFormatSelector 1)
Assert-Sequence -Name 'ادغام در mp4' -Collection $commandArguments -Flag '--merge-output-format' -Value 'mp4'
Assert-Sequence -Name 'زبان زیرنویس' -Collection $commandArguments -Flag '--sub-langs' -Value 'fa.*,fa,en.*,en'
Assert-Sequence -Name 'تبدیل زیرنویس به srt' -Collection $commandArguments -Flag '--convert-subs' -Value 'srt'
Assert-Sequence -Name 'پوشهٔ خروجی' -Collection $commandArguments -Flag '--paths' -Value $commandFolder
Assert-Equal    -Name 'لینک در انتهای دستور' -Expected 'https://www.youtube.com/watch?v=jNQXAC9IVRw' -Actual $commandArguments[$commandArguments.Count - 1]
Assert-False    -Name 'بدون کوکی وقتی لازم نیست' -Value ($commandArguments -ccontains '--cookies')
Assert-False    -Name 'بدون cookies-from-browser وقتی لازم نیست' -Value ($commandArguments -ccontains '--cookies-from-browser')
Assert-False    -Name 'بدون extractor-args وقتی لازم نیست' -Value ($commandArguments -ccontains '--extractor-args')

$cookieArguments = @(New-YtDlpArgument -Url 'https://youtu.be/x' -DownloadFolder $commandFolder `
    -FormatSelector 'b' -SubtitleLanguages 'fa' -CookieFile $validCookie)
Assert-Sequence -Name 'معرفی فایل کوکی' -Collection $cookieArguments -Flag '--cookies' -Value $validCookie

$browserArguments = @(New-YtDlpArgument -Url 'https://youtu.be/x' -DownloadFolder $commandFolder `
    -FormatSelector 'b' -SubtitleLanguages 'fa' `
    -CookiesFromBrowser (Get-CookiesFromBrowserValue -Browser 'firefox' -ProfileFolder 'C:\a b\profile'))
Assert-Sequence -Name 'کوکی از مرورگر با مسیر پروفایل' -Collection $browserArguments -Flag '--cookies-from-browser' -Value 'firefox:C:\a b\profile'

$fallbackArguments = @(New-YtDlpArgument -Url 'https://youtu.be/x' -DownloadFolder $commandFolder `
    -FormatSelector 'b' -SubtitleLanguages 'fa' -PlayerClients 'tv_simply,web_embedded')
Assert-Sequence -Name 'کلاینت جایگزین' -Collection $fallbackArguments -Flag '--extractor-args' -Value 'youtube:player_client=tv_simply,web_embedded'
Assert-True -Name 'فهرست کلاینت جایگزین خالی نیست' -Value ((Get-YoutubeFallbackPlayerClient).Count -ge 1)

Start-TestGroup 'نقل‌قول مسیرهای دارای فاصله و حروف فارسی'

$roundTrip = ConvertFrom-CommandLine (Join-ProcessArguments $commandArguments)
Assert-Equal -Name 'تعداد آرگومان‌ها پس از تجزیه' -Expected $commandArguments.Count -Actual $roundTrip.Count
$identical = $true
for ($i = 0; $i -lt $commandArguments.Count; $i++) {
    if ($commandArguments[$i] -cne $roundTrip[$i]) { $identical = $false; break }
}
Assert-True -Name 'هر آرگومان بدون تغییر به ابزار می‌رسد' -Value $identical

$trickyValues = @(
    'C:\ویدیو های من\خروجی',
    'C:\path with spaces\',
    'plain',
    'a"quote"inside',
    'C:\ends\with\backslash\',
    '%(title).180B [%(id)s].%(ext)s',
    'bv*[height<=1080][vcodec^=avc1]+ba[acodec^=mp4a]/b'
)
$trickyParsed = ConvertFrom-CommandLine (Join-ProcessArguments $trickyValues)
$trickyOk = ($trickyParsed.Count -eq $trickyValues.Count)
if ($trickyOk) {
    for ($i = 0; $i -lt $trickyValues.Count; $i++) {
        if ($trickyValues[$i] -cne $trickyParsed[$i]) { $trickyOk = $false; break }
    }
}
Assert-True -Name 'مسیرهای دشوار سالم می‌مانند' -Value $trickyOk

# ============================================================ browser sign-in

Start-TestGroup 'ورود با مرورگر'

Assert-Equal -Name 'مسیر پروفایل اختصاصی' -Expected (Join-Path 'C:\Temp' 'SubtitleVideoTool\youtube-signin-profile') -Actual (Get-SignInProfileFolder -Root 'C:\Temp')
Assert-Equal -Name 'آرگومان اجرای مرورگر' -Expected '-no-remote -profile C:\p https://www.youtube.com/' -Actual ((New-BrowserSignInArgument -ProfileFolder 'C:\p') -join ' ')
Assert-Equal -Name 'مقدار cookies-from-browser بدون پروفایل' -Expected 'firefox' -Actual (Get-CookiesFromBrowserValue -Browser 'firefox')
Assert-True  -Name 'مرورگر ناموجود پیدا نمی‌شود' -Value ($null -eq (Find-SignInBrowser -SearchPaths @((Join-Path $script:FixtureRoot 'no-browser.exe'))))
Assert-Equal -Name 'مرورگر موجود پیدا می‌شود' -Expected $goodTool -Actual (Find-SignInBrowser -SearchPaths @($goodTool))

Assert-False -Name 'پروفایل ناموجود آماده نیست' -Value (Test-SignInProfile -ProfileFolder (Join-Path $script:FixtureRoot 'no-profile')).IsReady
Assert-False -Name 'مسیر خالی آماده نیست' -Value (Test-SignInProfile -ProfileFolder '').IsReady

$emptyProfile = Join-Path $script:FixtureRoot 'profile-empty'
[void][System.IO.Directory]::CreateDirectory($emptyProfile)
Assert-False -Name 'پروفایل بدون cookies.sqlite آماده نیست' -Value (Test-SignInProfile -ProfileFolder $emptyProfile).IsReady

$readyProfile = Join-Path $script:FixtureRoot 'profile-ready'
[void][System.IO.Directory]::CreateDirectory($readyProfile)
Set-Content -LiteralPath (Join-Path $readyProfile 'cookies.sqlite') -Value 'SQLite format 3 fixture' -Encoding Ascii
Assert-True -Name 'پروفایل کامل آماده است' -Value (Test-SignInProfile -ProfileFolder $readyProfile).IsReady

$cookieKeys = (Get-CookieSourceOption | ForEach-Object { $_.Key }) -join ','
Assert-Equal -Name 'گزینه‌های منبع کوکی' -Expected 'none,browserlogin,file,firefox,edge,chrome,brave' -Actual $cookieKeys
Assert-Equal -Name 'گزینهٔ پیش‌فرض بدون کوکی است' -Expected 'none' -Actual (Get-CookieSourceOption)[0].Key

# ====================================================== yt-dlp error detection

Start-TestGroup 'تشخیص انواع خطای yt-dlp'

Assert-Equal -Name 'تشخیص ربات' -Expected 'BotCheck' -Actual (Get-YtDlpFailureKind -Output ([char]0x2018 + 'ERROR: [youtube] jNQXAC9IVRw: Sign in to confirm you' + [char]0x2019 + 're not a bot. Use --cookies-from-browser'))
Assert-Equal -Name 'تشخیص ربات با آپاستروف ساده' -Expected 'BotCheck' -Actual (Get-YtDlpFailureKind -Output "ERROR: Sign in to confirm you're not a bot")
Assert-Equal -Name 'نیاز به ورود' -Expected 'LoginRequired' -Actual (Get-YtDlpFailureKind -Output '[debug] [youtube] web player response playability status: LOGIN_REQUIRED')
Assert-Equal -Name 'محدودیت سنی' -Expected 'AgeRestricted' -Actual (Get-YtDlpFailureKind -Output 'ERROR: Sign in to confirm your age. This video may be inappropriate for some users.')
Assert-Equal -Name 'خطای DPAPI' -Expected 'Dpapi' -Actual (Get-YtDlpFailureKind -Output 'WARNING: Failed to decrypt with DPAPI. See https://github.com/yt-dlp/yt-dlp/issues/1")')
Assert-Equal -Name 'قفل بودن پایگاه کوکی مرورگر' -Expected 'CookieRead' -Actual (Get-YtDlpFailureKind -Output 'ERROR: Could not copy Chrome cookie database. See https://github.com/yt-dlp/yt-dlp/issues/7271 for more info')
Assert-Equal -Name 'نبود پایگاه کوکی مرورگر' -Expected 'CookieRead' -Actual (Get-YtDlpFailureKind -Output 'ERROR: could not find firefox cookies database in "C:\nope"')
Assert-True -Name 'راهنمای کوکی به بستن مرورگر اشاره دارد' -Value ((Get-YtDlpFailureAdvice -Kind 'CookieRead') -like '*ببندید*')
Assert-Equal -Name 'نبود FFmpeg' -Expected 'FfmpegMissing' -Actual (Get-YtDlpFailureKind -Output 'ERROR: You have requested merging of multiple formats but ffmpeg is not installed. Aborting due to --abort-on-error')
Assert-Equal -Name 'ویدیوی خصوصی' -Expected 'PrivateVideo' -Actual (Get-YtDlpFailureKind -Output 'ERROR: [youtube] abc: Private video. Sign in if you have been granted access to this video')
Assert-Equal -Name 'محدودیت جغرافیایی' -Expected 'GeoBlocked' -Actual (Get-YtDlpFailureKind -Output 'ERROR: The uploader has not made this video available in your country')
Assert-Equal -Name 'ویدیوی حذف‌شده' -Expected 'Unavailable' -Actual (Get-YtDlpFailureKind -Output 'ERROR: [youtube] abc: Video unavailable')
Assert-Equal -Name 'فرمت ناموجود' -Expected 'FormatUnavailable' -Actual (Get-YtDlpFailureKind -Output 'ERROR: [youtube] abc: Requested format is not available. Use --list-formats')
Assert-Equal -Name 'خطای پروکسی' -Expected 'Proxy' -Actual (Get-YtDlpFailureKind -Output 'ERROR: Unable to download webpage: Unable to connect to proxy')
Assert-Equal -Name 'خطای شبکه' -Expected 'Network' -Actual (Get-YtDlpFailureKind -Output "ERROR: Unable to download API page: Connection to www.youtube.com timed out. (connect timeout=20.0)")
Assert-Equal -Name 'کمبود فضای دیسک' -Expected 'DiskFull' -Actual (Get-YtDlpFailureKind -Output 'ERROR: unable to write data: [Errno 28] No space left on device')
Assert-Equal -Name 'نبود دسترسی' -Expected 'Permission' -Actual (Get-YtDlpFailureKind -Output 'ERROR: unable to open for writing: [Errno 13] Permission denied')
Assert-Equal -Name 'خطای ناشناخته' -Expected 'Unknown' -Actual (Get-YtDlpFailureKind -Output 'ERROR: something completely different happened')
Assert-Equal -Name 'خروجی خالی' -Expected 'Unknown' -Actual (Get-YtDlpFailureKind -Output '')

Assert-True -Name 'راهنمای هر خطای شناخته‌شده وجود دارد' -Value (
    -not [string]::IsNullOrWhiteSpace((Get-YtDlpFailureAdvice -Kind 'BotCheck')) -and
    -not [string]::IsNullOrWhiteSpace((Get-YtDlpFailureAdvice -Kind 'FfmpegMissing')) -and
    -not [string]::IsNullOrWhiteSpace((Get-YtDlpFailureAdvice -Kind 'Network')) -and
    [string]::IsNullOrWhiteSpace((Get-YtDlpFailureAdvice -Kind 'Unknown'))
)

$sampleOutput = @(
    '[debug] Command-line config: ...',
    "ERROR: [youtube] abc: Sign in to confirm you're not a bot."
) -join "`r`n"
$failureMessage = Get-DownloadFailureMessage -ToolName 'yt-dlp' -ExitCode 1 -Output $sampleOutput
Assert-True -Name 'پیام خطا کد خروج را دارد' -Value ($failureMessage -like '*1*')
Assert-True -Name 'پیام خطا راهنمای فارسی دارد' -Value ($failureMessage -like '*ورود با مرورگر*')
Assert-True -Name 'پیام خطا محدودیت یوتیوب را جدا می‌کند' -Value ($failureMessage -like '*سمت YouTube*')
Assert-True -Name 'پیام خطا گزارش ابزار را دارد' -Value ($failureMessage.Contains('ERROR: [youtube] abc'))

# ------------------------------------------------- live progress from yt-dlp

Start-TestGroup 'خواندن زندهٔ پیشرفت دانلود'

$destinationLine = ConvertFrom-YtDlpOutputLine -Line '[download] Destination: D:\Videos\Sample Title [dQw4w9WgXcQ].f137.mp4'
Assert-Equal -Name 'تشخیص سطر مقصد' -Expected 'Destination' -Actual $destinationLine.Kind
Assert-Equal -Name 'مسیر مقصد استخراج می‌شود' -Expected 'D:\Videos\Sample Title [dQw4w9WgXcQ].f137.mp4' -Actual $destinationLine.Destination

$alreadyLine = ConvertFrom-YtDlpOutputLine -Line '[download] D:\Videos\Sample.mp4 has already been downloaded'
Assert-Equal -Name 'تشخیص فایل از پیش دانلودشده' -Expected 'AlreadyDownloaded' -Actual $alreadyLine.Kind

$startingLine = ConvertFrom-YtDlpOutputLine -Line '[download]   0.0% of  123.45MiB at  Unknown B/s ETA Unknown'
Assert-Equal -Name 'تشخیص سطر پیشرفت' -Expected 'Progress' -Actual $startingLine.Kind
Assert-Equal -Name 'درصد صفر خوانده می‌شود' -Expected '0' -Actual $startingLine.Percent
Assert-Equal -Name 'حجم کل خوانده می‌شود' -Expected '123.45MiB' -Actual $startingLine.TotalText
Assert-Equal -Name 'سرعت نامعلوم خوانده می‌شود' -Expected 'UnknownB/s' -Actual $startingLine.SpeedText
Assert-Equal -Name 'زمان باقی‌مانده نامعلوم خوانده می‌شود' -Expected 'Unknown' -Actual $startingLine.EtaText

$midLine = ConvertFrom-YtDlpOutputLine -Line '[download]  42.3% of ~ 500.00MiB at    1.23MiB/s ETA 05:09 (frag 12/100)'
Assert-Equal -Name 'درصد اعشاری خوانده می‌شود' -Expected '42.3' -Actual $midLine.Percent
Assert-Equal -Name 'حجم تخمینی خوانده می‌شود' -Expected '500.00MiB' -Actual $midLine.TotalText
Assert-Equal -Name 'سرعت خوانده می‌شود' -Expected '1.23MiB/s' -Actual $midLine.SpeedText
Assert-Equal -Name 'زمان باقی‌مانده خوانده می‌شود' -Expected '05:09' -Actual $midLine.EtaText
Assert-Equal -Name 'شمارهٔ قطعه خوانده می‌شود' -Expected '12/100' -Actual $midLine.Fragment

$finishedLine = ConvertFrom-YtDlpOutputLine -Line '[download] 100% of  123.45MiB in 00:12'
Assert-Equal -Name 'درصد صد خوانده می‌شود' -Expected '100' -Actual $finishedLine.Percent

$mergeLine = ConvertFrom-YtDlpOutputLine -Line '[Merger] Merging formats into "D:\Videos\Sample.mp4"'
Assert-Equal -Name 'تشخیص ادغام' -Expected 'Merging' -Actual $mergeLine.Kind

$subtitleLine = ConvertFrom-YtDlpOutputLine -Line '[SubtitlesConvertor] Converting subtitles'
Assert-Equal -Name 'تشخیص زیرنویس' -Expected 'Subtitle' -Actual $subtitleLine.Kind

$errorLine = ConvertFrom-YtDlpOutputLine -Line 'ERROR: [youtube] abc: Video unavailable'
Assert-Equal -Name 'تشخیص سطر خطا' -Expected 'Diagnostic' -Actual $errorLine.Kind

Assert-Equal -Name 'سطر خالی' -Expected 'Empty' -Actual (ConvertFrom-YtDlpOutputLine -Line '   ').Kind
Assert-Equal -Name 'سطر ناشناخته' -Expected 'Other' -Actual (ConvertFrom-YtDlpOutputLine -Line 'random text').Kind

# A verbose debug line must never be mistaken for progress, or the bar would
# jump around on text that has nothing to do with the download.
Assert-Equal -Name 'سطر اشکال‌زدایی پیشرفت نیست' -Expected 'Other' -Actual (ConvertFrom-YtDlpOutputLine -Line '[debug] 100% something').Kind

Assert-True -Name 'مقصد شروع دانلود شمرده می‌شود' -Value (Test-DownloadStartKind -Kind 'Destination')
Assert-True -Name 'پیشرفت شروع دانلود شمرده می‌شود' -Value (Test-DownloadStartKind -Kind 'Progress')
Assert-False -Name 'سطر اطلاعات شروع دانلود نیست' -Value (Test-DownloadStartKind -Kind 'Info')

Assert-Equal -Name 'تبدیل رقم انگلیسی به فارسی' -Expected '۴۲۵' -Actual (ConvertTo-PersianDigit '425')
Assert-Equal -Name 'حروف غیرعددی دست‌نخورده می‌مانند' -Expected 'MiB/s' -Actual (ConvertTo-PersianDigit 'MiB/s')
Assert-Equal -Name 'ممیز فارسی فقط با سوییچ اعمال می‌شود' -Expected '۴۲.۳' -Actual (ConvertTo-PersianDigit '42.3')
Assert-Equal -Name 'ممیز فارسی بین دو رقم' -Expected '۴۲٫۳' -Actual (ConvertTo-PersianDigit '42.3' -PersianDecimal)
Assert-Equal -Name 'نقطهٔ غیرعددی ممیز نمی‌شود' -Expected 'sample.mp۴' -Actual (ConvertTo-PersianDigit 'sample.mp4' -PersianDecimal)

$progressText = Format-DownloadProgressText -Progress $midLine -PartNumber 1
Assert-True -Name 'متن پیشرفت درصد دارد' -Value ($progressText -like '*۴۲٫۳٪*')
Assert-True -Name 'متن پیشرفت سرعت دارد' -Value ($progressText -like '*سرعت*')
Assert-True -Name 'متن پیشرفت زمان باقی‌مانده دارد' -Value ($progressText -like '*باقی‌مانده*')

$unknownText = Format-DownloadProgressText -Progress $startingLine -PartNumber 1
Assert-False -Name 'سرعت نامعلوم نمایش داده نمی‌شود' -Value ($unknownText -like '*سرعت*')
Assert-False -Name 'زمان نامعلوم نمایش داده نمی‌شود' -Value ($unknownText -like '*باقی‌مانده*')

Assert-Equal -Name 'برچسب بخش نخست' -Expected 'در حال دانلود' -Actual (Get-DownloadPartLabel -PartNumber 1)
Assert-Equal -Name 'برچسب بخش صدا' -Expected 'در حال دانلود صدا' -Actual (Get-DownloadPartLabel -PartNumber 2)
Assert-True -Name 'برچسب بخش سوم شماره دارد' -Value ((Get-DownloadPartLabel -PartNumber 3) -like '*۳*')

Assert-True -Name 'نخستین درصد در گزارش ثبت می‌شود' -Value (Test-ProgressLogDue -Percent 0 -LastLoggedPercent -1)
Assert-False -Name 'درصد نزدیک ثبت نمی‌شود' -Value (Test-ProgressLogDue -Percent 12 -LastLoggedPercent 10)
Assert-True -Name 'گام ده‌درصدی ثبت می‌شود' -Value (Test-ProgressLogDue -Percent 20 -LastLoggedPercent 10)
Assert-True -Name 'پایان کار همیشه ثبت می‌شود' -Value (Test-ProgressLogDue -Percent 100 -LastLoggedPercent 95)
Assert-False -Name 'پایان کار دوبار ثبت نمی‌شود' -Value (Test-ProgressLogDue -Percent 100 -LastLoggedPercent 100)
Assert-False -Name 'سطر بدون درصد ثبت نمی‌شود' -Value (Test-ProgressLogDue -Percent $null -LastLoggedPercent 10)

# ------------------------------------------------ pumping a real child process

Start-TestGroup 'خواندن خروجی زندهٔ یک پردازش واقعی'

Assert-True -Name 'مجموعهٔ خالی تخلیه‌شده است' -Value (Test-PumpSetDrained -Pumps @())
Assert-Equal -Name 'مجموعهٔ خالی سطری نمی‌دهد' -Expected 0 -Actual (Read-PendingPumpLine -Pumps $null).Count

# A real child process is used on purpose: the whole point of the pump is that
# ReadLineAsync can be polled without blocking, which a fake reader cannot show.
$childScript = Join-Path $script:FixtureRoot 'emit.ps1'
Set-Content -LiteralPath $childScript -Encoding UTF8 -Value @'
[Console]::Out.WriteLine('[download] Destination: C:\out\Sample.f137.mp4')
[Console]::Out.WriteLine('[download]  10.0% of  100.00MiB at 1.00MiB/s ETA 01:30')
[Console]::Error.WriteLine('WARNING: an example warning')
[Console]::Out.WriteLine('[download] 100% of  100.00MiB in 00:20')
'@

$childInfo = New-Object System.Diagnostics.ProcessStartInfo
$childInfo.FileName = (Get-Process -Id $PID).Path
$childInfo.Arguments = Join-ProcessArguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $childScript)
$childInfo.UseShellExecute = $false
$childInfo.CreateNoWindow = $true
$childInfo.RedirectStandardOutput = $true
$childInfo.RedirectStandardError = $true
$childInfo.StandardOutputEncoding = [System.Text.Encoding]::UTF8
$childInfo.StandardErrorEncoding = [System.Text.Encoding]::UTF8

$child = New-Object System.Diagnostics.Process
$child.StartInfo = $childInfo
[void]$child.Start()

$pumps = @(
    (New-OutputPump -Reader $child.StandardOutput),
    (New-OutputPump -Reader $child.StandardError)
)
Assert-False -Name 'مجموعهٔ فعال هنوز تخلیه نشده' -Value (Test-PumpSetDrained -Pumps $pumps)

$collected = New-Object 'System.Collections.Generic.List[string]'
$deadline = (Get-Date).AddSeconds(30)
while (-not (Test-PumpSetDrained -Pumps $pumps) -and (Get-Date) -lt $deadline) {
    foreach ($line in (Read-PendingPumpLine -Pumps $pumps)) { [void]$collected.Add($line) }
    if (Test-PumpSetDrained -Pumps $pumps) { break }
    Start-Sleep -Milliseconds 20
}
[void]$child.WaitForExit(10000)
$child.Dispose()

Assert-True -Name 'هر دو جریان تا انتها خوانده شدند' -Value (Test-PumpSetDrained -Pumps $pumps)
Assert-Equal -Name 'همهٔ چهار سطر دریافت شد' -Expected 4 -Actual $collected.Count
Assert-True -Name 'سطر خطای استاندارد هم دریافت شد' -Value ($collected -contains 'WARNING: an example warning')

$parsedLines = @($collected | ForEach-Object { ConvertFrom-YtDlpOutputLine -Line $_ })
$firstStart = @($parsedLines | Where-Object { Test-DownloadStartKind -Kind $_.Kind }) | Select-Object -First 1
Assert-True -Name 'آغاز دانلود از جریان زنده تشخیص داده شد' -Value ($null -ne $firstStart)
$percentages = @($parsedLines | Where-Object { $_.Kind -eq 'Progress' } | ForEach-Object { $_.Percent })
Assert-Equal -Name 'دو سطر پیشرفت خوانده شد' -Expected 2 -Actual $percentages.Count
Assert-Equal -Name 'آخرین درصد صد است' -Expected '100' -Actual $percentages[-1]

}
finally {
    if (Test-Path -LiteralPath $script:FixtureRoot) {
        Remove-Item -LiteralPath $script:FixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ''
Write-Host ('نتیجه: {0} تست موفق، {1} تست ناموفق' -f $script:Passed, $script:Failures.Count)
if ($script:Failures.Count -gt 0) {
    Write-Host ''
    foreach ($failure in $script:Failures) { Write-Host ('  - ' + $failure) -ForegroundColor Red }
    exit 1
}
exit 0
