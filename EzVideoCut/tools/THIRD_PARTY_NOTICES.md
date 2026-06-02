# Third-party notices

This folder includes FFmpeg command-line tools used by EZ Video Cut.

## FFmpeg

- Included files: `ffmpeg.exe`, `ffprobe.exe`
- Build package: gyan.dev FFmpeg essentials build, `ffmpeg-2026-06-01-git-bf608f16fd-essentials_build`
- Build version: `2026-06-01-git-bf608f16fd-essentials_build-www.gyan.dev`
- Build configuration includes: `--enable-gpl --enable-version3 --enable-static`
- License: GNU General Public License version 3
- License text: `FFmpeg-LICENSE-GPL-3.0.txt`
- Upstream project: https://ffmpeg.org/
- FFmpeg source code: https://github.com/FFmpeg/FFmpeg/commit/bf608f16fd
- Binary build source: https://www.gyan.dev/ffmpeg/builds/

EZ Video Cut invokes FFmpeg as a separate executable process for probing
and stream-copy cutting. EZ Video Cut does not modify the included FFmpeg
binaries.

