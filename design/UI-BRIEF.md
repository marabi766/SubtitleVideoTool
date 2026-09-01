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
   the video to job 1.

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
| Video quality | dropdown | populated from the video itself — see below |
| English subtitles | checkbox + availability line | English only — see below |
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

### Detecting what the video actually offers

When a link is pasted, the app asks yt-dlp what that specific video has, and
fills the quality list from the answer instead of offering a fixed list. Quality
options the video does not have must not be offered. The same answer says
whether an English subtitle exists and what kind it is.

**This is not instant.** The probe is a network round trip that measured
**6–8 seconds** on a real video. It can also fail, for the same reasons a
download fails — bot check, login required, private video, no connection — so
it needs its own loading and failure treatment, and a way to run it again after
the user changes the cookie source.

Measured on a real video (Rick Astley, "Never Gonna Give You Up"), so the design
is sized against real numbers rather than a guess:

**Subtitles — English only. There is no language picker.**

The app fetches English subtitles and nothing else, so the interface does not
ask which language. What it does need to say is *what kind of English subtitle
this video has*, because the quality difference is large and the user should
know before downloading. There are four outcomes, and the probe distinguishes
them reliably (verified against real output, see the note below):

1. **Human-written English** — the best case. On the video measured, English was
   one of five human-written tracks.
2. **Auto-generated English** — speech recognition on English audio. Usable,
   with mistakes.
3. **Auto-translated English** — machine translation of a non-English video.
   Roughest, but often the only option on foreign-language content.
4. **None** — no English subtitle exists at all. The download should still be
   allowed to proceed with video only, not blocked.

So the control is a single checkbox — "Download English subtitles" — plus one
line of text stating which of those four applies to the pasted video. The design
should give those four outcomes a clear visual treatment, particularly the
difference between case 1 and cases 2–3, and case 4 must not read as an error.

*Implementation note, not a design constraint:* in the probe output, case 1 is
an `en` key under `subtitles`; cases 2 and 3 are an `en` key under
`automatic_captions`, told apart by whether the track URL carries a `tlang=`
parameter — present means it was machine-translated from another language.
`design/sample-probe.json` contains a real example of each.

**Quality — 8 real options on that video**, each of which the app should label
plainly: 2160p (4K), 1440p, 1080p, 720p, 480p, 360p, 240p, 144p, plus a
"Best available" entry. Frame rate belongs in the label when it is above 30
(720p60). An approximate file size is available for roughly two thirds of the
formats and absent for the rest, so if the design shows sizes they have to
degrade gracefully to nothing.

One caveat worth surfacing: 1440p and 2160p exist only as VP9/AV1, with no
H.264 version. Those download fine but are less compatible with older players,
which is worth a quiet note next to the 4K option rather than a blocking dialog.

## States the design must cover

These are the states that actually occur, and the current UI expresses them
poorly. Each deserves a considered treatment.

1. **Idle, tools ready** — nothing running.
2. **Idle, tools broken or missing** — a bundled `.exe` exists but will not
   start. The user must be told which one and what to do.
3. **Inspecting the link** — a link has been pasted and the app is asking
   YouTube what the video offers. Takes 6–8 seconds. The quality list is empty,
   the English-subtitle line is unknown, and Download cannot be pressed.
4. **Link inspected** — the video's title, duration and thumbnail are known, the
   quality list holds what this video actually has, and the English-subtitle
   line says which of the four cases applies.
5. **Link inspection failed** — same causes as a failed download. The user must
   be able to fix the cause (usually by choosing a cookie source) and re-run the
   inspection without retyping the link.
6. **Preparing** — the tool has been launched but has not reported anything yet.
   Indeterminate progress.
7. **Download started** — the moment yt-dlp reports a destination file or a
   first percentage. This is the app's proof it got past YouTube's checks, and
   it should read as a distinct, positive state, not just another log line.
8. **Downloading** — percentage, total size, transfer speed, time remaining,
   and which part is being fetched (video and audio download separately, so the
   percentage restarts once; the design must keep that from looking like a
   regression).
9. **Post-processing** — merging video and audio, converting subtitles. No
   percentage is available here.
10. **Retrying** — YouTube rejected the first attempt, the app is automatically
   trying an alternative player client. The user should see that this is a
   deliberate retry, not a failure.
11. **Failed** — a classified error with a plain-English explanation and a
   concrete next step. Fourteen kinds exist, including: bot check, login
   required, age restricted, browser cookies locked, ffmpeg missing, private
   video, geo-blocked, video unavailable, format unavailable, proxy error,
   network error, disk full, permission denied.
13. **Cancelled** — the user pressed Cancel.
14. **Succeeded** — with the output path, and for a download, the video and
    subtitle files that were produced.

    On success the app moves to the burn-subtitles tab and fills in the
    **video** path. It deliberately does **not** fill in the subtitle path: the
    downloaded subtitle is often a machine translation the user wants to edit,
    replace or pick between, so choosing the subtitle stays a deliberate act.
    The design should still make the downloaded subtitle easy to find and
    select — showing its filename, or offering it as a one-click suggestion —
    rather than leaving the user to hunt through a folder.

The log is currently the only place most of this is visible. The redesign
should decide what belongs in a status region and what stays in the log.

## What to produce

Artboards for:

- The shell in its idle state, light and dark.
- Each of the three tabs.
- The download tab in states 3, 4, 5, 7, 8, 9, 10 and 14 above — this is where
  the design earns its keep.
- The quality list, open, with the 8 real options.
- The English-subtitle line in all four of its outcomes: human-written,
  auto-generated, auto-translated, and none available.
- The failure presentation, using the bot-check error as the worked example.
- The tools-missing state.

Please also state the type scale, spacing scale, colour tokens and control
sizes, since the result is implemented in XAML rather than exported.

## Implementation target

The design will be built as a .NET WPF application, so it should stay within
what native Windows controls and standard XAML styling can do. Fluent / WinUI
styling is available. Anything that would require a web view is out of scope.
