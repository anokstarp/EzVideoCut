# EZ Video Cut

English | [한국어](README.md)

A Windows video cutting app for previewing a video and quickly cutting the sections you need.

## Key Features

- Open and preview video files.
- Navigate to the desired position on the timeline.
- Cut a video by selecting a start and end range.
- Split a video into two sections at one point.
- Set the start, end, or split point from the current playback position.
- Move backward/forward with the arrow keys and toggle play/pause with Space.
- Choose which audio track to play when multiple tracks are available.
- Remove selected audio tracks from the exported file.
- Mute selected audio tracks in the exported file.
- Choose the output location and open the folder after completion.
- View cut progress and estimated remaining time.

## Requirements

- Windows
- .NET 8 SDK for development
- `ffmpeg.exe` and `ffprobe.exe`

The app first looks for FFmpeg executables in the output directory's `tools` folder, then falls back to the executables available on `PATH`.

## Build

```powershell
dotnet restore EzVideoCut\EzVideoCut.csproj --configfile NuGet.Config
dotnet build EzVideoCut\EzVideoCut.csproj
```

## Run

```powershell
dotnet run --project EzVideoCut\EzVideoCut.csproj
```

## FFmpeg Notes

This app cuts quickly without re-encoding video. Depending on the source file structure, the output may start from a nearby keyframe rather than the exact selected frame.

Muted audio tracks are rewritten as silent audio in the exported file.

## License And Notice Location

Bundled FFmpeg license and notice files are available in `EzVideoCut/tools`:

- `EzVideoCut/tools/THIRD_PARTY_NOTICES.md`
- `EzVideoCut/tools/FFmpeg-LICENSE-GPL-3.0.txt`
