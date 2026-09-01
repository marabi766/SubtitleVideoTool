# UI design brief — Subtitle & Video Compressor

Hand this file to Claude Design together with the three screenshots in
`design/current-ui/`. It describes what the application does and every piece of
state the interface has to express. It does not prescribe a visual style — that
is what the design work is for.

## What the application is

A Windows desktop utility that wraps three command line tools (`yt-dlp`,
`ffmpeg`, `ffprobe`, plus `deno` as a JavaScript runtime for YouTube) behind a
single window. It does three jobs:

1. **Burn subtitles** — permanently render an `.srt` into a video.
2. **Compress to a size target** — shrink a video to fit a maximum file size
   (default 120 MB), choosing the bitrate automatically.
3. **Download from YouTube** — fetch a video plus its subtitle track, then hand
   both to job 1.

The three jobs share one queue: only one runs at a time, and the window shows a
single progress bar, a status line and a log.

**Language: English only.** The current build is Persian; the redesign is
English throughout, left-to-right.

## Audience and constraints

- Non-technical users on Windows 10 and 11.
- Single window, no MDI, no browser chrome. Currently 900×756 fixed; the
  redesign should say what it wants and whether it resizes.
- Must work at 100%, 125% and 150% Windows display scaling.
- Light and dark themes, following the Windows system theme.
- Every long operation is cancellable, and cancelling must always be reachable.

## Screens

### Shell

- Application title and one-line description.
- Three tabs (or another navigation pattern, if a better one fits):
  Burn Subtitles · Compress · Download from YouTube.
- A persistent bottom region shared by all tabs:
  - Tool status line: which of ffmpeg / ffprobe / yt-dlp / deno are present and
    runnable. Three states: all ready, partly ready, missing or broken.
  - A "Locate FFmpeg" button for when the bundled tools are not found.
  - Progress bar: indeterminate before a percentage is known, determinate after.
  - Status line: current stage, or live download figures.
  - Cancel button, enabled only while something is running.
  - Log: monospaced, scrolling, timestamped, read-only.

### Tab 1 — Burn Subtitles

| Control | Type | Notes |
| --- | --- | --- |
| Video file | path + Browse | required |
| Subtitle file | path + Browse | `.srt`, required |
| Output file | path + Browse | required |
| Font family | text + font picker | |
| Font size | number | |
| Subtitle colour | colour swatch + picker | |
| Background colour | colour swatch + picker | |
| Also compress to target | checkbox | reveals the size target field |
| Size target (MB) | number | only when the checkbox is on |
| Start | primary button | |

### Tab 2 — Compress

| Control | Type | Notes |
| --- | --- | --- |
| Video file | path + Browse | required |
| Output file | path + Browse | required |
| Size target (MB) | number | default 120 |
| Start | primary button | |

The result reports the achieved output size, and warns when it overshot the
target.

### Tab 3 — Download from YouTube

| Control | Type | Notes |
| --- | --- | --- |
| YouTube link | text | accepts pasted links in many shapes |
| Output folder | path + Browse | defaults to the user's Downloads folder |
| Video quality | dropdown | Best available · 1080p · 720p · 480p |
| Subtitle languages | text + example hint | comma separated language codes |
| Cookie source | dropdown | see below |
| Sign in to YouTube | button | enabled only for "Sign in with browser" |
| Cookie file | path + Browse | enabled only for "cookies.txt file" |
| Download | primary button | |

Cookie source options: No cookies · Sign in with browser (app's own session) ·
cookies.txt file · Firefox · Microsoft Edge · Google Chrome · Brave.

This dropdown is the part users get wrong most often. It exists because YouTube
answers anonymous downloads with a bot check. The design should make the
"No cookies → blocked → sign in" path obvious rather than burying it in a
dropdown, and should make clear that the app never displays or copies cookie
contents.

## States the design must cover

These are the states that actually occur, and the current UI expresses them
poorly. Each deserves a considered treatment.

1. **Idle, tools ready** — nothing running.
2. **Idle, tools broken or missing** — a bundled `.exe` exists but will not
   start. The user must be told which one and what to do.
3. **Preparing** — the tool has been launched but has not reported anything yet.
   Indeterminate progress.
4. **Download started** — the moment yt-dlp reports a destination file or a
   first percentage. This is the app's proof it got past YouTube's checks, and
   it should read as a distinct, positive state, not just another log line.
5. **Downloading** — percentage, total size, transfer speed, time remaining,
   and which part is being fetched (video and audio download separately, so the
   percentage restarts once; the design must keep that from looking like a
   regression).
6. **Post-processing** — merging video and audio, converting subtitles. No
   percentage is available here.
7. **Retrying** — YouTube rejected the first attempt, the app is automatically
   trying an alternative player client. The user should see that this is a
   deliberate retry, not a failure.
8. **Failed** — a classified error with a plain-English explanation and a
   concrete next step. Fourteen kinds exist, including: bot check, login
   required, age restricted, browser cookies locked, ffmpeg missing, private
   video, geo-blocked, video unavailable, format unavailable, proxy error,
   network error, disk full, permission denied.
9. **Cancelled** — the user pressed Cancel.
10. **Succeeded** — with the output path, and for downloads the video and
    subtitle files found, offered as a jump to the burn-subtitles tab.

The log is currently the only place most of this is visible. The redesign
should decide what belongs in a status region and what stays in the log.

## What to produce

Artboards for:

- The shell in its idle state, light and dark.
- Each of the three tabs.
- The download tab in states 3, 4, 5, 6, 7, 8 and 10 above — this is where the
  design earns its keep.
- The failure presentation, using the bot-check error as the worked example.
- The tools-missing state.

Please also state the type scale, spacing scale, colour tokens and control
sizes, since the result is implemented in XAML rather than exported.

## Implementation target

The design will be built as a .NET WPF application, so it should stay within
what native Windows controls and standard XAML styling can do. Fluent / WinUI
styling is available. Anything that would require a web view is out of scope.
