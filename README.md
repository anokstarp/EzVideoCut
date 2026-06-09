# EZ Video Cut

[English](README.en.md) | 한국어

비디오를 미리 보고 필요한 구간을 빠르게 자를 수 있는 Windows용 영상 커팅 프로그램입니다.

## 주요 기능

- 비디오 파일 열기 및 재생 미리보기.
- 타임라인에서 원하는 위치 탐색.
- 시작/종료 범위를 지정해 영상 자르기.
- 한 지점을 기준으로 영상을 두 구간으로 분할.
- 현재 재생 위치를 시작/종료/분할 지점으로 지정.
- 여러 영상을 선택한 순서대로 이어 붙이고, 이어지는 지점을 타임라인에서 확인.
- 선택한 영상에서 특정 오디오 트랙을 원본 그대로 추출.
- 여러 오디오 트랙 중 재생할 트랙 선택.
- 출력 파일에서 선택한 오디오 트랙 제거.
- 여러 오디오 트랙을 하나의 오디오 트랙으로 믹싱.
- 출력 위치 선택 및 완료 후 폴더 열기.
- 자르기, 이어 붙이기, 오디오 추출 진행률과 예상 남은 시간 확인.

## 요구 사항

- Windows
- 개발용 .NET 8 SDK
- `ffmpeg.exe` 및 `ffprobe.exe`

앱은 출력 디렉터리의 `tools` 폴더에서 FFmpeg 실행 파일을 먼저 찾고, 없으면 `PATH`에 있는 실행 파일을 사용합니다.

## 빌드

```powershell
dotnet restore EzVideoCut\EzVideoCut.csproj --configfile NuGet.Config
dotnet build EzVideoCut\EzVideoCut.csproj
```

## 실행

```powershell
dotnet run --project EzVideoCut\EzVideoCut.csproj
```

## FFmpeg 참고 사항

이 프로그램은 비디오를 재인코딩하지 않는 방식으로 빠르게 자릅니다. 원본 파일 구조에 따라 출력 파일이 선택한 정확한 프레임이 아니라 가까운 키프레임부터 시작할 수 있습니다.

오디오 트랙 제거는 선택한 트랙을 출력 파일에서 제외합니다.

오디오 추출은 선택한 오디오 트랙을 재인코딩하지 않고 원본 스트림 그대로 오디오 파일로 저장합니다.

여러 오디오 트랙을 하나로 믹싱하는 경우에는 오디오만 AAC 384kbps, 48kHz, 스테레오로 다시 인코딩합니다.

## 라이선스 및 고지 위치

번들된 FFmpeg 라이선스와 고지 파일은 `EzVideoCut/tools`에서 확인할 수 있습니다.

- `EzVideoCut/tools/THIRD_PARTY_NOTICES.md`
- `EzVideoCut/tools/FFmpeg-LICENSE-GPL-3.0.txt`
