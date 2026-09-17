# Third-party notices

This package redistributes unmodified executable builds of the following open-source projects. These components retain their respective copyrights and licenses.

## FFmpeg and FFprobe

- Version: 9.0.1 essentials build for Windows x64
- Project: https://ffmpeg.org/
- Binary distributor: https://www.gyan.dev/ffmpeg/builds/
- License for this build: GNU General Public License version 3 (GPLv3)
- Corresponding source information: https://www.gyan.dev/ffmpeg/builds/#sources and https://ffmpeg.org/download.html#get-sources
- Included files: `tools/ffmpeg.exe`, `tools/ffprobe.exe`
- Release archive SHA-256: `fec81ae03971d9dd4be3ebe02e263bd2ec1d789483f931bdba5f5715e65da2e9`

FFmpeg is a trademark of Fabrice Bellard, originator of the FFmpeg project. This application is not affiliated with or endorsed by the FFmpeg project or Gyan Doshi.

## yt-dlp

- Version: 2026.08.19 Windows x64 executable
- Project and source: https://github.com/yt-dlp/yt-dlp
- License: The Unlicense; third-party components may carry their own terms as described by the project
- Included file: `tools/yt-dlp.exe`
- Executable SHA-256: `66674953fe251b89f4d08c5f0e35e0728679bd67ab3d7d05c0562af101dd3e7a`

Users are responsible for complying with the terms of the websites they access and for downloading only media they are authorized to download.

## Deno

- Version: 2.9.6 Windows x64 executable
- Project and source: https://github.com/denoland/deno
- License: MIT License
- Included file: `tools/deno.exe`
- Release archive SHA-256: `15e5300b0ba3c3695a7621d90160a746ec9e710228cee639afa9d580f6e3cd11`

Deno is used as the external JavaScript runtime recommended by yt-dlp for processing YouTube JavaScript challenges. Its license is included in `third_party/deno/LICENSE`.

## bgutil-ytdlp-pot-provider

- Version: 2.0.0
- Project and source: https://github.com/Brainicism/bgutil-ytdlp-pot-provider
- Commit: `37169ee2656e08c5c2e5dc9df4c598c0cb4c88a8`
- License: GNU General Public License version 3 (GPLv3)
- Included files: `tools/yt-dlp-plugins/bgutil-ytdlp-pot-provider/` (the yt-dlp plugin, unmodified) and `tools/pot-provider/server/` (its `generate_once` script and the `src`/`deno.json`/`deno.lock`/`package.json` it needs, unmodified)
- License text: `third_party/bgutil-pot-provider/LICENSE`

This is a yt-dlp plugin that obtains a "proof of origin" token, which YouTube now requires to hand back the URL of most non-lowest-resolution formats — including for a signed-in request. It runs entirely locally, through the Deno runtime already bundled above; nothing is sent to any server this project does not control.

`tools/pot-provider/server/node_modules/` (the script's own npm dependency tree — axios, bgutils-js, canvas, commander, jsdom, proxy-agent and youtubei.js, with their transitive dependencies) is not stored in Git; `Build-Msi.ps1` restores it with `npm install --omit=dev` against the committed `package-lock.json`, whose per-package hashes are what verify it. All 177 of those packages are under permissive licenses (MIT, ISC, BSD, Apache-2.0, or similar) — none are copyleft. The exact list and versions are in `tools/pot-provider/server/package.json` and `package-lock.json`.
