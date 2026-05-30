# EZ Video Cut

Lightweight WPF demo for previewing a video, choosing a start/end range, and cutting it with FFmpeg stream copy.

## Current demo features

- Open common video files.
- Preview playback with LibVLCSharp.
- Seek through a timeline.
- Seek backward/forward with the left/right arrow keys using a selectable step.
- Toggle play/pause with Space when not typing in a text field.
- Preview multiple audio tracks together when available and adjust preview volume without changing the exported file.
- Set start/end from the current playback position.
- Type start/end with digits only; input is normalized to `HH:MM:SS.mmm`.
- Confirm the possible container-boundary offset before cutting.
- Show cut progress, elapsed time, and estimated remaining time.
- Cut with `ffmpeg -c copy` without re-encoding.

## Requirements

- Windows
- .NET 8 SDK for development
- `ffmpeg.exe` and `ffprobe.exe`

The app first checks `EzVideoCut/tools` in the output directory, then falls back to `PATH`.

## Build

```powershell
dotnet restore EzVideoCut\EzVideoCut.csproj --configfile NuGet.Config
dotnet build EzVideoCut\EzVideoCut.csproj
```

## Run

```powershell
dotnet run --project EzVideoCut\EzVideoCut.csproj
```

## Create an exe folder

```powershell
dotnet publish EzVideoCut\EzVideoCut.csproj -c Release -r win-x64 --self-contained true -o dist\EzVideoCut-win-x64
```

Run:

```powershell
dist\EzVideoCut-win-x64\EzVideoCut.exe
```

## FFmpeg note

This demo uses stream copy, so the output can start from the previous keyframe instead of the exact selected frame. For redistribution, use an FFmpeg build whose license fits the product and include the required FFmpeg notices.
