# Bundled tools

This folder contains the bundled LGPL FFmpeg command-line tools used by
EZ Video Cut:

- `ffmpeg.exe`
- `ffprobe.exe`

The app looks here first. If these files are missing during development,
it can still fall back to `ffmpeg.exe` and `ffprobe.exe` available on `PATH`.

See `THIRD_PARTY_NOTICES.md` and `FFmpeg-LICENSE-LGPL-3.0.txt` for license
and source information.
