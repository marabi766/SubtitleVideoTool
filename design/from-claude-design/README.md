# Claude Design output

Exported from the Claude Design project on 2 September 2026 and unpacked here
unchanged. `Subtitle Video Compressor.dc.html` is the design itself — open it in
a browser to view all nineteen artboards. `support.js` is the canvas runtime,
not design content. `uploads/` is what was sent into the design session (the
brief and the screenshots of the old window).

Design decisions worth carrying into the implementation, beyond the brief:

- **A five-step rail** — Prepare, Connect, Download, Process, Done — sits above
  every download state and never changes shape, so each state reads as a
  position in one journey rather than as a new screen.
- **Cookie source is not a dropdown.** It is three radio cards: try without
  signing in, sign in to YouTube, use a browser I'm signed into.
- **Every failure fills the same four slots**: what happened, why, the one
  action that fixes it, and the raw tool output folded away. All fourteen
  classified errors use that shape.
- **The progress bar is two-part**, so the restart between the video and audio
  streams does not read as a regression.
- **The subtitle hand-off is a suggestion, not a selection**: the field stays
  empty and the downloaded file sits beneath it carrying its provenance.
- **The log is collapsed by default**, with its line count on the disclosure.
- Section 6 of the design carries the full token set — type scale, 4pt spacing
  scale, control sizes, and light/dark colour tokens keyed to WinUI names.
