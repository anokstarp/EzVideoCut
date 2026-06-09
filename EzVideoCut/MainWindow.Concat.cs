using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LibVLCSharp.Shared;
using Polygon = System.Windows.Shapes.Polygon;
using Rectangle = System.Windows.Shapes.Rectangle;
using VlcMedia = LibVLCSharp.Shared.Media;

namespace EzVideoCut;

public partial class MainWindow
{

    private void RefreshConcatClipList(int selectedIndex = -1)
    {
        ConcatClipsListBox.Items.Clear();
        for (var index = 0; index < _concatClips.Count; index++)
        {
            var clip = _concatClips[index];
            ConcatClipsListBox.Items.Add($"{index + 1}. {Path.GetFileName(clip.Path)}  {FormatTime(clip.Duration)}");
        }

        if (selectedIndex >= 0 && selectedIndex < _concatClips.Count)
        {
            ConcatClipsListBox.SelectedIndex = selectedIndex;
        }

        _duration = CurrentCutMode == CutMode.Concat ? GetConcatTotalDuration() : _duration;
        ConcatTotalDurationText.Text = $"총 {FormatTime(GetConcatTotalDuration())}";
        DurationText.Text = FormatTime(_duration);
        TimelineSlider.Maximum = Math.Max(1, _duration.TotalMilliseconds);
        UpdateConcatClipButtonStates();
        UpdateTimelineMarkers();
    }

    private void UpdateConcatClipButtonStates()
    {
        if (AddConcatVideosButton is null)
        {
            return;
        }

        var selectedIndex = ConcatClipsListBox.SelectedIndex;
        var hasSelection = selectedIndex >= 0 && selectedIndex < _concatClips.Count;
        var concatMode = CurrentCutMode == CutMode.Concat;
        AddConcatVideosButton.IsEnabled = !_isCutting;
        RemoveConcatVideoButton.IsEnabled = !_isCutting && hasSelection;
        MoveConcatVideoUpButton.IsEnabled = !_isCutting && concatMode && hasSelection && selectedIndex > 0;
        MoveConcatVideoDownButton.IsEnabled = !_isCutting && concatMode && hasSelection && selectedIndex < _concatClips.Count - 1;
        ClearConcatVideosButton.IsEnabled = !_isCutting && _concatClips.Count > 0;
        SelectListedVideoButton.IsEnabled = !_isCutting && !concatMode && hasSelection;
    }

    private void MoveConcatClip(int direction)
    {
        if (CurrentCutMode != CutMode.Concat)
        {
            return;
        }

        var index = ConcatClipsListBox.SelectedIndex;
        var newIndex = index + direction;
        if (index < 0 || index >= _concatClips.Count || newIndex < 0 || newIndex >= _concatClips.Count)
        {
            return;
        }

        (_concatClips[index], _concatClips[newIndex]) = (_concatClips[newIndex], _concatClips[index]);
        RefreshConcatClipList(newIndex);
        RefreshDefaultOutputPathForCurrentMode();
        SetConcatTimelinePosition(TimeSpan.Zero);
        LoadConcatClipForTime(TimeSpan.Zero, autoPlay: false);
        ValidateTrimRange();
    }

    private TimeSpan GetConcatTotalDuration()
    {
        return TimeSpan.FromTicks(_concatClips.Sum(clip => clip.Duration.Ticks));
    }

    private void TimelineMarkerCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTimelineMarkers();
    }

    private void UpdateTimelineMarkers()
    {
        if (TimelineMarkerCanvas is null)
        {
            return;
        }

        TimelineMarkerCanvas.Children.Clear();
        if (CurrentCutMode != CutMode.Concat || _concatClips.Count < 2 || _duration <= TimeSpan.Zero)
        {
            return;
        }

        var width = TimelineMarkerCanvas.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var elapsed = TimeSpan.Zero;
        for (var index = 0; index < _concatClips.Count - 1; index++)
        {
            elapsed += _concatClips[index].Duration;
            var left = Math.Clamp(elapsed.TotalMilliseconds / _duration.TotalMilliseconds * width, 0, width);
            var markerTime = elapsed;
            var triangle = new Polygon
            {
                Points = new PointCollection
                {
                    new(0, 0),
                    new(14, 0),
                    new(7, 10)
                },
                Fill = new SolidColorBrush(Color.FromRgb(64, 93, 130)),
                Opacity = 0.95,
                Cursor = Cursors.Hand,
                ToolTip = FormatTime(markerTime),
                Tag = markerTime
            };
            triangle.MouseLeftButtonDown += TimelineMarker_MouseLeftButtonDown;
            Canvas.SetLeft(triangle, left - 7);
            Canvas.SetTop(triangle, 0);
            TimelineMarkerCanvas.Children.Add(triangle);

            var marker = new Rectangle
            {
                Width = 2,
                Height = 17,
                Fill = new SolidColorBrush(Color.FromRgb(64, 93, 130)),
                Opacity = 0.9,
                Cursor = Cursors.Hand,
                ToolTip = FormatTime(markerTime),
                Tag = markerTime
            };
            marker.MouseLeftButtonDown += TimelineMarker_MouseLeftButtonDown;
            Canvas.SetLeft(marker, left - 1);
            Canvas.SetTop(marker, 10);
            TimelineMarkerCanvas.Children.Add(marker);
        }
    }

    private void TimelineMarker_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TimeSpan markerTime })
        {
            SeekTo(markerTime);
            e.Handled = true;
        }
    }

    private async Task ConcatButton_ClickAsync()
    {
        if (_concatClips.Count < 2)
        {
            MessageBox.Show(this, "이어 붙이려면 영상이 2개 이상 필요합니다.", "이어 붙이기", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(_outputPath))
        {
            RefreshDefaultOutputPathForCurrentMode();
        }

        if (_concatClips.Any(clip => Path.GetFullPath(clip.Path).Equals(Path.GetFullPath(_outputPath!), StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "입력 파일과 출력 파일이 같을 수 없습니다.", "저장 위치 확인", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var excludedAudioTracks = _audioTrackOptions
            .Where(track => track.ExcludeFromOutput)
            .ToArray();
        var hasMixedAudioTracks = ShouldMixAudioTracks();
        if (excludedAudioTracks.Length > 0)
        {
            var trackList = string.Join(", ", excludedAudioTracks.Select(track => $"{track.DisplayIndex}번"));
            var deleteAnswer = MessageBox.Show(
                this,
                $"선택한 오디오 트랙은 출력 파일에서 제거됩니다.\n\n제거할 오디오 트랙: {trackList}\n\n계속 진행하시겠습니까?",
                "오디오 트랙 제거 확인",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (deleteAnswer != MessageBoxResult.Yes)
            {
                StatusText.Text = "이어 붙이기를 취소했습니다.";
                ValidateTrimRange();
                return;
            }
        }

        _isCutting = true;
        SetControlsEnabled(false);
        CutButton.Content = "이어 붙이는 중...";
        StatusText.Text = hasMixedAudioTracks
            ? "이어 붙이기 호환성과 오디오 믹싱을 준비하는 중입니다."
            : "이어 붙이기 호환성을 확인하는 중입니다.";
        var stopwatch = Stopwatch.StartNew();
        UpdateCutProgress(0, stopwatch.Elapsed, null, "이어 붙이기 준비 중");

        string? concatListPath = null;
        try
        {
            await EnsureConcatStreamCompatibilityAsync();
            SetOutputPath(OutputPathService.GetAvailableOutputPath(_outputPath!));

            concatListPath = Path.Combine(Path.GetTempPath(), $"ez-video-cut-concat-{Guid.NewGuid():N}.txt");
            await File.WriteAllLinesAsync(
                concatListPath,
                FfmpegService.BuildConcatListLines(_concatClips),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var args = FfmpegService.BuildConcatArguments(concatListPath, _outputPath!, _audioTrackOptions, _mixAudioTracksToSingleTrack, _disableAudioLimiter);
            StatusText.Text = hasMixedAudioTracks
                ? "ffmpeg copy 이어 붙이기와 오디오 믹싱을 실행 중입니다."
                : "ffmpeg copy 이어 붙이기를 실행 중입니다.";
            var result = await FfmpegService.RunFfmpegAsync(FfmpegService.ResolveToolPath("ffmpeg.exe"), args, GetConcatTotalDuration(), stopwatch, "이어 붙이기", ReportCutProgress);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(result.Error.Length > 0 ? result.Error : result.Output);
            }

            UpdateCutProgress(100, stopwatch.Elapsed, TimeSpan.Zero);
            StatusText.Text = $"완료: {Path.GetFileName(_outputPath)}";
            MessageBox.Show(this, "이어 붙이기 완료", "완료", MessageBoxButton.OK, MessageBoxImage.Information);
            SetOutputPath(OutputPathService.GetAvailableOutputPath(_outputPath!));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"이어 붙이기 실패: {Shorten(ex.Message)}";
            MessageBox.Show(this, ex.Message, "이어 붙이기 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(concatListPath) && File.Exists(concatListPath))
            {
                File.Delete(concatListPath);
            }

            _isCutting = false;
            CutButton.Content = "이어 붙이기 실행";
            SetControlsEnabled(HasPlayableInput);
            ValidateTrimRange();
        }
    }


    private void StopConcatPlayback()
    {
        _currentConcatClipIndex = -1;
        _mediaPlayer.Stop();
        VideoView.Visibility = Visibility.Collapsed;
        EmptyVideoText.Visibility = Visibility.Visible;
        _isPlaybackPaused = true;
        _isPlaybackEnded = false;
        PlayPauseButton.Content = "재생";
    }

    private TimeSpan GetConcatClipStart(int clipIndex)
    {
        var ticks = 0L;
        for (var index = 0; index < clipIndex && index < _concatClips.Count; index++)
        {
            ticks += _concatClips[index].Duration.Ticks;
        }

        return TimeSpan.FromTicks(ticks);
    }

    private bool TryResolveConcatPosition(TimeSpan globalTime, out int clipIndex, out TimeSpan clipTime)
    {
        clipIndex = -1;
        clipTime = TimeSpan.Zero;
        if (_concatClips.Count == 0)
        {
            return false;
        }

        globalTime = ClampTime(globalTime);
        var elapsed = TimeSpan.Zero;
        for (var index = 0; index < _concatClips.Count; index++)
        {
            var clip = _concatClips[index];
            var end = elapsed + clip.Duration;
            if (globalTime < end || index == _concatClips.Count - 1)
            {
                clipIndex = index;
                clipTime = globalTime - elapsed;
                if (clipTime < TimeSpan.Zero)
                {
                    clipTime = TimeSpan.Zero;
                }

                if (clipTime > clip.Duration)
                {
                    clipTime = clip.Duration;
                }

                return true;
            }

            elapsed = end;
        }

        return false;
    }

    private void LoadConcatClipForTime(TimeSpan globalTime, bool autoPlay)
    {
        if (!TryResolveConcatPosition(globalTime, out var clipIndex, out var clipTime))
        {
            return;
        }

        var loadVersion = ++_concatPreviewLoadVersion;
        var shouldRefreshAudioTracks = _currentConcatClipIndex != clipIndex || _audioTrackOptions.Count == 0;
        if (_additionalAudioPlayers.Count > 0)
        {
            DisposeAdditionalAudioPlayers();
        }

        if (_currentConcatClipIndex != clipIndex || _mediaPlayer.Media is null || _mediaPlayer.State == VLCState.Ended)
        {
            using var media = new VlcMedia(_libVlc, new Uri(_concatClips[clipIndex].Path));
            _mediaPlayer.Play(media);
            VideoView.Visibility = Visibility.Visible;
            EmptyVideoText.Visibility = Visibility.Collapsed;
            _currentConcatClipIndex = clipIndex;
        }
        else if (autoPlay)
        {
            _mediaPlayer.Play();
        }

        if (shouldRefreshAudioTracks)
        {
            RefreshConcatAudioTracksAfterLoadAsync(loadVersion, clipIndex);
        }
        else if (_audioTrackOptions.Count > 0)
        {
            ApplySelectedAudioTrack();
        }
        else
        {
            ApplyPrimaryAudioVolumeOnly();
        }

        _mediaPlayer.Time = (long)Math.Round(clipTime.TotalMilliseconds);
        if (autoPlay)
        {
            _mediaPlayer.SetPause(false);
            return;
        }

        _isPlaybackPaused = true;
        _isPlaybackEnded = false;
        _mediaPlayer.SetPause(false);
        PauseConcatPreviewAfterRenderAsync(loadVersion, clipIndex, globalTime, clipTime);
    }

    private async void RefreshConcatAudioTracksAfterLoadAsync(int version, int clipIndex)
    {
        try
        {
            await RefreshAudioTrackOptionsFromCurrentMediaAsync(
                preserveChoices: true,
                shouldContinue: () => CurrentCutMode == CutMode.Concat
                    && version == _concatPreviewLoadVersion
                    && _currentConcatClipIndex == clipIndex);
        }
        catch
        {
            ShowAudioTrackPlaceholder("오디오 트랙을 읽지 못했습니다.");
        }
    }

    private async void PauseConcatPreviewAfterRenderAsync(int version, int clipIndex, TimeSpan globalTime, TimeSpan clipTime)
    {
        await Task.Delay(180);
        await Dispatcher.InvokeAsync(() =>
        {
            if (version != _concatPreviewLoadVersion
                || CurrentCutMode != CutMode.Concat
                || _currentConcatClipIndex != clipIndex
                || !_isPlaybackPaused)
            {
                return;
            }

            _mediaPlayer.Time = (long)Math.Round(clipTime.TotalMilliseconds);
            _mediaPlayer.SetPause(true);
            ApplyPrimaryAudioVolumeOnly();
            SetConcatTimelinePosition(globalTime);
            PlayPauseButton.Content = "재생";
        });
    }

    private void SetConcatTimelinePosition(TimeSpan globalTime)
    {
        globalTime = ClampTime(globalTime);
        ResetDisplayClock(globalTime);
        CurrentTimeText.Text = FormatTime(globalTime);
        _isUpdatingTimeline = true;
        TimelineSlider.Value = Math.Clamp(globalTime.TotalMilliseconds, TimelineSlider.Minimum, TimelineSlider.Maximum);
        _isUpdatingTimeline = false;
    }

}
