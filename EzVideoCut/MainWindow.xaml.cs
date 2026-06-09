using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using Polygon = System.Windows.Shapes.Polygon;
using Rectangle = System.Windows.Shapes.Rectangle;
using VlcMedia = LibVLCSharp.Shared.Media;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace EzVideoCut;

public partial class MainWindow : Window
{
    private enum CutMode
    {
        Range,
        Split,
        AudioExtract,
        Concat
    }

    private readonly DispatcherTimer _positionTimer;
    private readonly LibVLC _libVlc;
    private readonly VlcMediaPlayer _mediaPlayer;
    private readonly List<AdditionalAudioPlayer> _additionalAudioPlayers = new();
    private readonly List<AudioTrackOption> _audioTrackOptions = new();
    private readonly List<ConcatClip> _concatClips = new();
    private readonly Stopwatch _playbackClock = Stopwatch.StartNew();

    private string? _inputPath;
    private string? _outputPath;
    private TimeSpan _duration = TimeSpan.Zero;
    private TimeSpan _singleInputDuration = TimeSpan.Zero;
    private TimeSpan _concatModePosition = TimeSpan.Zero;
    private TimeSpan _displayTimeBase = TimeSpan.Zero;
    private TimeSpan _displayClockBase = TimeSpan.Zero;
    private TimeSpan _lastVlcSyncClock = TimeSpan.Zero;
    private bool _controlsEnabled;
    private bool _isCutting;
    private bool _isDraggingTimeline;
    private bool _isDraggingVolume;
    private bool _isUpdatingTimeline;
    private bool _isPlaybackPaused = true;
    private bool _isPlaybackEnded;
    private CutMode _activeCutMode = CutMode.Split;
    private int _currentConcatClipIndex = -1;
    private int? _selectedAudioTrackDisplayIndex;
    private int _audioPlaybackStateVersion;
    private int _concatPreviewLoadVersion;
    private int _masterVolume = 100;
    private bool _mixAudioTracksToSingleTrack;
    public MainWindow()
    {
        InitializeComponent();
        _activeCutMode = CurrentCutMode;
        UpdateCutModeVisibility();
        SetInputPathDisplay(null);
        ShowAudioTrackPlaceholder("비디오를 선택하면 표시됩니다.");

        Core.Initialize();
        _libVlc = new LibVLC("--no-video-title-show");
        _masterVolume = (int)VolumeSlider.Value;
        _mediaPlayer = new VlcMediaPlayer(_libVlc)
        {
            Volume = _masterVolume,
            Mute = _masterVolume <= 0
        };
        VideoView.MediaPlayer = _mediaPlayer;

        VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;
        DataObject.AddPastingHandler(StartTextBox, TimeTextBox_OnPaste);
        DataObject.AddPastingHandler(EndTextBox, TimeTextBox_OnPaste);

        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _positionTimer.Tick += PositionTimer_Tick;

        ComponentDispatcher.ThreadPreprocessMessage += ComponentDispatcher_ThreadPreprocessMessage;
        Closed += MainWindow_Closed;
    }

    private CutMode CurrentCutMode => CutModeTabs.SelectedIndex switch
    {
        0 => CutMode.Split,
        1 => CutMode.Range,
        2 => CutMode.AudioExtract,
        3 => CutMode.Concat,
        _ => CutMode.Split
    };

    private bool HasPlayableInput => CurrentCutMode == CutMode.Concat
        ? _concatClips.Count > 0
        : _inputPath is not null;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!HasPlayableInput || e.Key is not Key.Left and not Key.Right and not Key.Space)
        {
            return;
        }

        if (Keyboard.FocusedElement is TextBox)
        {
            return;
        }

        if (e.Key == Key.Space)
        {
            TogglePlayback();
        }
        else
        {
            SeekRelative(e.Key == Key.Left ? -GetSeekStep() : GetSeekStep());
        }

        e.Handled = true;
    }

    private void ComponentDispatcher_ThreadPreprocessMessage(ref MSG msg, ref bool handled)
    {
        const int wmKeyDown = 0x0100;

        if (handled || !IsActive || !HasPlayableInput || msg.message != wmKeyDown)
        {
            return;
        }

        var key = KeyInterop.KeyFromVirtualKey((int)msg.wParam);
        if (key is not Key.Left and not Key.Right and not Key.Space)
        {
            return;
        }

        if (Keyboard.FocusedElement is TextBox)
        {
            return;
        }

        if (key == Key.Space)
        {
            TogglePlayback();
        }
        else
        {
            SeekRelative(key == Key.Left ? -GetSeekStep() : GetSeekStep());
        }

        handled = true;
    }

    private void VideoHost_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        Keyboard.Focus(this);
    }

    private void VideoHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyVideoSurfaceSize(e.NewSize);
    }

    private void ApplyVideoSurfaceSize(Size hostSize)
    {
        const double aspectRatio = 16d / 9d;
        var availableWidth = Math.Max(0, hostSize.Width - VideoHost.BorderThickness.Left - VideoHost.BorderThickness.Right);
        var availableHeight = Math.Max(0, hostSize.Height - VideoHost.BorderThickness.Top - VideoHost.BorderThickness.Bottom);
        var width = availableWidth;
        var height = width / aspectRatio;

        if (height > availableHeight)
        {
            height = availableHeight;
            width = height * aspectRatio;
        }

        VideoSurface.Width = width;
        VideoSurface.Height = height;
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "비디오 선택",
            Filter = "Video files|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.m4v;*.ts;*.mts;*.m2ts|All files|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await LoadSingleVideoAsync(dialog.FileName);
    }

    private async Task LoadSingleVideoAsync(string inputPath)
    {
        _inputPath = inputPath;
        _currentConcatClipIndex = -1;
        ClearAudioTrackOptions();
        SetInputPathDisplay(_inputPath);
        RefreshDefaultOutputPathForCurrentMode();
        ResetCutProgress();
        StatusText.Text = "영상 정보를 읽는 중...";

        SetControlsEnabled(false);

        try
        {
            var duration = await ProbeDurationAsync(_inputPath);
            await Dispatcher.InvokeAsync(() =>
            {
            _singleInputDuration = duration;
            _duration = _singleInputDuration;
            DurationText.Text = FormatTime(_duration);
            CurrentTimeText.Text = FormatTime(TimeSpan.Zero);
            TimelineSlider.Maximum = Math.Max(1, _duration.TotalMilliseconds);
            TimelineSlider.Value = 0;
            ResetDisplayClock(TimeSpan.Zero);
            SetStartTime(TimeSpan.Zero, adjustEnd: false);
            SetEndTime(_duration, adjustStart: false);
            SetSplitTime(TimeSpan.Zero);

            ApplyPrimaryAudioVolumeOnly();
            _isPlaybackPaused = true;
            _isPlaybackEnded = false;
            using var media = new VlcMedia(_libVlc, new Uri(_inputPath));
            VideoView.Visibility = Visibility.Visible;
            EmptyVideoText.Visibility = Visibility.Collapsed;
            _mediaPlayer.Play(media);
            _mediaPlayer.SetPause(true);
            ResetDisplayClock(TimeSpan.Zero);
            _positionTimer.Start();

            PlayPauseButton.Content = "재생";
            SetControlsEnabled(true);
            });
            await StartAdditionalAudioTracksAsync();
            await Dispatcher.InvokeAsync(() =>
            {
            _isPlaybackPaused = false;
            _mediaPlayer.SetPause(false);
            ResetDisplayClock(TimeSpan.FromMilliseconds(Math.Max(0, _mediaPlayer.Time)));
            ApplySelectedAudioTrack();
            PlayPauseButton.Content = "?쇱떆?뺤?";
            StatusText.Text = "영상이 열렸습니다.";
            });
        }
        catch (Exception ex)
        {
            VideoView.Visibility = Visibility.Collapsed;
            EmptyVideoText.Visibility = Visibility.Visible;
            StatusText.Text = $"영상 선택 실패: {Shorten(ex.Message)}";
            MessageBox.Show(this, ex.Message, "영상 선택 실패", MessageBoxButton.OK, MessageBoxImage.Error);
            SetControlsEnabled(false);
        }
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        TogglePlayback();
    }

    private void TogglePlayback()
    {
        if (!HasPlayableInput)
        {
            return;
        }

        if (!_isPlaybackPaused)
        {
            SyncDisplayClockFromVlc(force: true);
            var pauseTime = GetCurrentTime();
            _mediaPlayer.SetPause(true);
            _isPlaybackPaused = true;
            _isPlaybackEnded = false;
            ResetDisplayClock(pauseTime);
            PlayPauseButton.Content = "재생";
        }
        else
        {
            var resumeTime = GetCurrentTime();
            if (IsAtPlaybackEnd(resumeTime))
            {
                RestartPlaybackFrom(TimeSpan.Zero);
                return;
            }

            if (CurrentCutMode == CutMode.Concat)
            {
                LoadConcatClipForTime(resumeTime, autoPlay: true);
            }
            else
            {
                _mediaPlayer.Play();
            }

            _mediaPlayer.SetPause(false);
            _isPlaybackPaused = false;
            _isPlaybackEnded = false;
            ResetDisplayClock(resumeTime);
            PlayPauseButton.Content = "일시정지";
        }
    }

    private void SetStartButton_Click(object sender, RoutedEventArgs e)
    {
        SetStartTime(GetCurrentTime(), adjustEnd: true);
        ValidateTrimRange();
    }

    private void SetEndButton_Click(object sender, RoutedEventArgs e)
    {
        SetEndTime(GetCurrentTime(), adjustStart: true);
        ValidateTrimRange();
    }

    private void SetSplitButton_Click(object sender, RoutedEventArgs e)
    {
        SetSplitTime(GetCurrentTime());
        ValidateTrimRange();
    }

    private void CutModeTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender != CutModeTabs)
        {
            return;
        }

        var selectedMode = CurrentCutMode;
        var changedPlaybackGroup = (_activeCutMode == CutMode.Concat) != (selectedMode == CutMode.Concat);
        if (changedPlaybackGroup)
        {
            StoreModePlaybackPosition(_activeCutMode);
            StopPlaybackForModeSwitch();
        }

        _activeCutMode = selectedMode;
        UpdateCutModeVisibility();
        RefreshDefaultOutputPathForCurrentMode();

        if (changedPlaybackGroup)
        {
            ApplyPlaybackSurfaceForCurrentMode();
        }
    }

    private void StoreModePlaybackPosition(CutMode mode)
    {
        if (mode == CutMode.Concat)
        {
            _concatModePosition = ClampToDuration(GetDisplayTime(), GetConcatTotalDuration());
        }
    }

    private void StopPlaybackForModeSwitch()
    {
        _mediaPlayer.SetPause(true);
        _mediaPlayer.Stop();
        DisposeAdditionalAudioPlayers();
        ClearAudioTrackOptions();
        _currentConcatClipIndex = -1;
        _isPlaybackPaused = true;
        _isPlaybackEnded = false;
        PlayPauseButton.Content = "재생";
    }

    private void ApplyPlaybackSurfaceForCurrentMode()
    {
        if (CurrentCutMode == CutMode.Concat)
        {
            ShowConcatPlaybackSurface();
            return;
        }

        ShowSingleCutPlaceholder();
    }

    private void ShowConcatPlaybackSurface()
    {
        _duration = GetConcatTotalDuration();
        DurationText.Text = FormatTime(_duration);
        TimelineSlider.Maximum = Math.Max(1, _duration.TotalMilliseconds);

        if (_concatClips.Count == 0)
        {
            ShowEmptyVideoSurface();
            SetControlsEnabled(false);
            return;
        }

        RefreshDefaultOutputPathForCurrentMode();

        var target = ClampToDuration(_concatModePosition, _duration);
        if (_duration > TimeSpan.Zero && _duration - target <= GetPlaybackEndTolerance())
        {
            target = TimeSpan.Zero;
        }

        SetConcatTimelinePosition(target);
        LoadConcatClipForTime(target, autoPlay: false);
        SetControlsEnabled(true);
        _positionTimer.Start();
    }

    private void ShowSingleCutPlaceholder()
    {
        _inputPath = null;
        _singleInputDuration = TimeSpan.Zero;
        _duration = TimeSpan.Zero;
        SetInputPathDisplay(null);
        ClearAudioTrackOptions();
        ShowEmptyVideoSurface();
        ResetCutProgress();
        SetStartTime(TimeSpan.Zero, adjustEnd: false);
        SetEndTime(TimeSpan.Zero, adjustStart: false);
        SetSplitTime(TimeSpan.Zero);
        SetControlsEnabled(false);
    }

    private void ShowEmptyVideoSurface()
    {
        VideoView.Visibility = Visibility.Collapsed;
        EmptyVideoText.Text = "비디오를 선택하면 이곳에서 재생됩니다.";
        EmptyVideoText.Visibility = Visibility.Visible;
        ResetDisplayClock(TimeSpan.Zero);
        CurrentTimeText.Text = FormatTime(TimeSpan.Zero);
        DurationText.Text = FormatTime(_duration);
        _isUpdatingTimeline = true;
        TimelineSlider.Maximum = Math.Max(1, _duration.TotalMilliseconds);
        TimelineSlider.Value = 0;
        _isUpdatingTimeline = false;
    }

    private async void AddConcatVideosButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "이어 붙일 영상 선택",
            Filter = "Video files|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.m4v;*.ts;*.mts;*.m2ts|All files|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog(this) != true || dialog.FileNames.Length == 0)
        {
            return;
        }

        SetControlsEnabled(false);
        StatusText.Text = "이어 붙일 영상 정보를 읽는 중...";

        try
        {
            var firstAddedIndex = _concatClips.Count;
            foreach (var fileName in dialog.FileNames)
            {
                var duration = await ProbeDurationAsync(fileName);
                _concatClips.Add(new ConcatClip(fileName, duration));
            }

            RefreshDefaultOutputPathForCurrentMode();

            RefreshConcatClipList(firstAddedIndex);
            if (CurrentCutMode == CutMode.Concat && _concatClips.Count > 0)
            {
                SetConcatTimelinePosition(TimeSpan.Zero);
                LoadConcatClipForTime(TimeSpan.Zero, autoPlay: false);
            }

            StatusText.Text = "이어 붙일 영상이 추가되었습니다.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"영상 추가 실패: {Shorten(ex.Message)}";
            MessageBox.Show(this, ex.Message, "영상 추가 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetControlsEnabled(HasPlayableInput);
            ValidateTrimRange();
        }
    }

    private void RemoveConcatVideoButton_Click(object sender, RoutedEventArgs e)
    {
        var index = ConcatClipsListBox.SelectedIndex;
        if (index < 0 || index >= _concatClips.Count)
        {
            return;
        }

        var removedPath = _concatClips[index].Path;
        _concatClips.RemoveAt(index);
        RefreshConcatClipList(Math.Min(index, _concatClips.Count - 1));
        RefreshDefaultOutputPathForCurrentMode();
        if (CurrentCutMode == CutMode.Concat && _concatClips.Count > 0)
        {
            SetConcatTimelinePosition(TimeSpan.Zero);
            LoadConcatClipForTime(TimeSpan.Zero, autoPlay: false);
        }
        else if (CurrentCutMode == CutMode.Concat)
        {
            StopConcatPlayback();
        }
        else if (_inputPath is not null && Path.GetFullPath(_inputPath).Equals(Path.GetFullPath(removedPath), StringComparison.OrdinalIgnoreCase))
        {
            ClearSingleVideoSelection();
        }

        ValidateTrimRange();
    }

    private void MoveConcatVideoUpButton_Click(object sender, RoutedEventArgs e)
    {
        MoveConcatClip(-1);
    }

    private void MoveConcatVideoDownButton_Click(object sender, RoutedEventArgs e)
    {
        MoveConcatClip(1);
    }

    private void ClearConcatVideosButton_Click(object sender, RoutedEventArgs e)
    {
        _concatClips.Clear();
        RefreshConcatClipList();
        if (CurrentCutMode == CutMode.Concat)
        {
            StopConcatPlayback();
        }
        else
        {
            ClearSingleVideoSelection();
        }

        SetConcatTimelinePosition(TimeSpan.Zero);
        RefreshDefaultOutputPathForCurrentMode();
        ValidateTrimRange();
    }

    private void ConcatClipsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateConcatClipButtonStates();
    }

    private async void SelectListedVideoButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentCutMode == CutMode.Concat)
        {
            return;
        }

        var index = ConcatClipsListBox.SelectedIndex;
        if (index < 0 || index >= _concatClips.Count)
        {
            return;
        }

        await LoadSingleVideoAsync(_concatClips[index].Path);
    }

    private void PickOutputButton_Click(object sender, RoutedEventArgs e)
    {
        if (_inputPath is null && CurrentCutMode != CutMode.Concat)
        {
            return;
        }

        var defaultOutputPath = _outputPath;
        if (string.IsNullOrWhiteSpace(defaultOutputPath) && CurrentCutMode == CutMode.Concat && _concatClips.Count > 0)
        {
            defaultOutputPath = OutputPathService.BuildDefaultConcatOutputPath(_concatClips[0].Path);
        }

        var dialog = new SaveFileDialog
        {
            Title = "저장 위치 선택",
            FileName = Path.GetFileName(defaultOutputPath),
            InitialDirectory = Path.GetDirectoryName(defaultOutputPath),
            Filter = CurrentCutMode == CutMode.AudioExtract
                ? "Audio file|*.m4a;*.mp3;*.wav;*.flac;*.ogg;*.opus;*.ac3;*.eac3;*.dts;*.mka|All files|*.*"
                : "MP4 file|*.mp4|MKV file|*.mkv|MOV file|*.mov|All files|*.*",
            OverwritePrompt = false
        };

        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        SetOutputPath(OutputPathService.GetAvailableOutputPath(dialog.FileName));
        ValidateTrimRange();
    }

    private void OpenOutputFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_outputPath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(_outputPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            MessageBox.Show(this, "출력 폴더를 찾을 수 없습니다.", "폴더 열기", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true
        });
    }

    private async void CutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isCutting)
        {
            return;
        }

        if (CurrentCutMode == CutMode.Concat)
        {
            await ConcatButton_ClickAsync();
            return;
        }

        if (CurrentCutMode == CutMode.AudioExtract)
        {
            await ExtractAudioButton_ClickAsync();
            return;
        }

        if (_inputPath is null || _outputPath is null)
        {
            return;
        }

        var cutJobs = BuildCutJobs(_outputPath);
        if (cutJobs.Length == 0)
        {
            return;
        }

        if (cutJobs.Any(job => Path.GetFullPath(_inputPath).Equals(Path.GetFullPath(job.OutputPath), StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "입력 파일과 출력 파일이 같을 수 없습니다.", "저장 위치 확인", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        CutButton.IsEnabled = false;
        StatusText.Text = "자르기 확인 메시지를 준비하는 중...";

        var noticeText = BuildCutNotice();
        var answer = MessageBox.Show(this, noticeText, "자르기 확인", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes)
        {
            StatusText.Text = "자르기를 취소했습니다.";
            ValidateTrimRange();
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
                StatusText.Text = "?먮Ⅴ湲곕? 痍⑥냼?덉뒿?덈떎.";
                ValidateTrimRange();
                return;
            }
        }

        if (CurrentCutMode == CutMode.Range)
        {
            SetOutputPath(cutJobs[0].OutputPath);
        }

        _isCutting = true;
        SetControlsEnabled(false);
        CutButton.Content = "자르는 중...";
        StatusText.Text = hasMixedAudioTracks
            ? "ffmpeg 컷과 오디오 믹싱을 실행 중입니다."
            : "ffmpeg copy 컷을 실행 중입니다.";

        var stopwatch = Stopwatch.StartNew();
        UpdateCutProgress(0, stopwatch.Elapsed, null);

        try
        {
            for (var index = 0; index < cutJobs.Length; index++)
            {
                var job = cutJobs[index];
                var progressTitle = cutJobs.Length > 1
                    ? $"{index + 1}/{cutJobs.Length} part{index + 1} 생성 중"
                    : null;
                UpdateCutProgress(0, stopwatch.Elapsed, null, progressTitle);
                var args = FfmpegService.BuildCutArguments(_inputPath, job.Start, job.Duration, job.OutputPath, _audioTrackOptions, _mixAudioTracksToSingleTrack);
                var result = await FfmpegService.RunFfmpegAsync(FfmpegService.ResolveToolPath("ffmpeg.exe"), args, job.Duration, stopwatch, progressTitle, ReportCutProgress);
                if (result.ExitCode != 0)
                {
                    throw new InvalidOperationException(result.Error.Length > 0 ? result.Error : result.Output);
                }
            }

            UpdateCutProgress(100, stopwatch.Elapsed, TimeSpan.Zero);
            StatusText.Text = $"완료: {string.Join(", ", cutJobs.Select(job => Path.GetFileName(job.OutputPath)))}";
            MessageBox.Show(this, "자르기 완료", "완료", MessageBoxButton.OK, MessageBoxImage.Information);

            if (CurrentCutMode == CutMode.Range)
            {
                SetOutputPath(OutputPathService.GetAvailableOutputPath(_outputPath));
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"자르기 실패: {Shorten(ex.Message)}";
            MessageBox.Show(this, Shorten(ex.Message), "자르기 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            stopwatch.Stop();
            _isCutting = false;
            CutButton.Content = "자르기 실행";
            SetControlsEnabled(HasPlayableInput);
            ValidateTrimRange();
        }
    }

    private void TimeTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            NormalizeTimeTextBox(textBox);
        }
    }

    private void TimeTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox textBox)
        {
            NormalizeTimeTextBox(textBox);
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void TimeTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = e.Text.Any(character => !char.IsDigit(character));
    }

    private void TimeTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.SelectAll();
        }
    }

    private void TimeTextBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBox textBox && !textBox.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            textBox.Focus();
        }
    }

    private void TimeTextBox_OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.Text))
        {
            e.CancelCommand();
            return;
        }

        var text = e.DataObject.GetData(DataFormats.Text) as string ?? "";
        if (text.Any(character => !char.IsDigit(character)))
        {
            e.CancelCommand();
        }
    }

    private void TimelineSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!HasPlayableInput)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source && IsInsideThumb(source))
        {
            _isDraggingTimeline = true;
            return;
        }

        _isDraggingTimeline = true;
        SeekToTimelineClick(e.GetPosition(TimelineSlider));
        TimelineSlider.CaptureMouse();
        e.Handled = true;
    }

    private void TimelineSlider_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!HasPlayableInput || !_isDraggingTimeline || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        SeekToTimelineClick(e.GetPosition(TimelineSlider));
        e.Handled = true;
    }

    private void TimelineSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!HasPlayableInput)
        {
            return;
        }

        if (TimelineSlider.IsMouseCaptured)
        {
            TimelineSlider.ReleaseMouseCapture();
        }

        _isDraggingTimeline = false;
        SeekToSliderValue();
    }

    private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingTimeline || !_isDraggingTimeline || !HasPlayableInput)
        {
            return;
        }

        SeekToSliderValue();
    }

    private void SeekToTimelineClick(Point point)
    {
        var width = TimelineSlider.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var ratio = Math.Clamp(point.X / width, 0, 1);
        var value = TimelineSlider.Minimum + ratio * (TimelineSlider.Maximum - TimelineSlider.Minimum);
        SeekTo(TimeSpan.FromMilliseconds(value));
    }

    private static bool IsInsideThumb(DependencyObject source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Thumb)
            {
                return true;
            }
        }

        return false;
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _masterVolume = (int)Math.Clamp(Math.Round(e.NewValue), 0, 100);
        VolumeValueText.Text = $"{_masterVolume}%";

        ApplyPrimaryAudioVolumeOnly();
    }

    private void VolumeSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!VolumeSlider.IsEnabled)
        {
            return;
        }

        _isDraggingVolume = true;
        SetVolumeFromSliderPoint(e.GetPosition(VolumeSlider));
        VolumeSlider.CaptureMouse();
        e.Handled = true;
    }

    private void VolumeSlider_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!VolumeSlider.IsEnabled || !_isDraggingVolume || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        SetVolumeFromSliderPoint(e.GetPosition(VolumeSlider));
        e.Handled = true;
    }

    private void VolumeSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingVolume)
        {
            return;
        }

        _isDraggingVolume = false;
        if (VolumeSlider.IsMouseCaptured)
        {
            VolumeSlider.ReleaseMouseCapture();
        }

        SetVolumeFromSliderPoint(e.GetPosition(VolumeSlider));
        e.Handled = true;
    }

    private void SetVolumeFromSliderPoint(Point point)
    {
        var width = VolumeSlider.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var ratio = Math.Clamp(point.X / width, 0, 1);
        VolumeSlider.Value = VolumeSlider.Minimum + ratio * (VolumeSlider.Maximum - VolumeSlider.Minimum);
    }

    private void PositionTimer_Tick(object? sender, EventArgs e)
    {
        if (!HasPlayableInput || _isDraggingTimeline)
        {
            return;
        }

        var current = GetDisplayTime();
        var handledEndedConcatClip = false;
        if (CurrentCutMode == CutMode.Concat && !_isPlaybackPaused)
        {
            if (_mediaPlayer.State == VLCState.Ended && _currentConcatClipIndex >= 0)
            {
                if (_currentConcatClipIndex < _concatClips.Count - 1)
                {
                    current = GetConcatClipStart(_currentConcatClipIndex + 1);
                    LoadConcatClipForTime(current, autoPlay: true);
                    _isPlaybackPaused = false;
                    _isPlaybackEnded = false;
                }
                else
                {
                    current = _duration;
                }

                ResetDisplayClock(current);
                handledEndedConcatClip = true;
            }
        }

        if (!handledEndedConcatClip)
        {
            SyncDisplayClockFromVlc(force: false);
            current = GetDisplayTime();
            if (CurrentCutMode == CutMode.Concat
                && !_isPlaybackPaused
                && !IsAtPlaybackEnd(current)
                && TryResolveConcatPosition(current, out var clipIndex, out _)
                && clipIndex != _currentConcatClipIndex)
            {
                LoadConcatClipForTime(current, autoPlay: true);
            }
        }

        CurrentTimeText.Text = FormatTime(current);

        _isUpdatingTimeline = true;
        TimelineSlider.Value = Math.Clamp(current.TotalMilliseconds, TimelineSlider.Minimum, TimelineSlider.Maximum);
        _isUpdatingTimeline = false;
        if (!_isPlaybackPaused && IsAtPlaybackEnd(current))
        {
            SyncDisplayClockFromVlc(force: true);
            _isPlaybackPaused = true;
            _isPlaybackEnded = true;
        }

        PlayPauseButton.Content = _isPlaybackPaused ? "재생" : "일시정지";
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        ComponentDispatcher.ThreadPreprocessMessage -= ComponentDispatcher_ThreadPreprocessMessage;
        _positionTimer.Stop();
        ClearAudioTrackOptions();
        VideoView.MediaPlayer = null;
        _mediaPlayer.Dispose();
        _libVlc.Dispose();
    }

    private void NormalizeTimeTextBox(TextBox textBox)
    {
        if (!TryParseTimeInput(textBox.Text, out var value))
        {
            value = TimeSpan.Zero;
        }

        value = ClampTime(value);

        if (textBox == StartTextBox && TryParseTimeInput(EndTextBox.Text, out var end) && value > ClampTime(end))
        {
            value = ClampTime(end);
        }
        else if (textBox == EndTextBox && TryParseTimeInput(StartTextBox.Text, out var start) && value < ClampTime(start))
        {
            value = ClampTime(start);
        }

        SetTimeText(textBox, value);
        ValidateTrimRange();
    }

    private void SetStartTime(TimeSpan value, bool adjustEnd)
    {
        value = ClampTime(value);
        if (adjustEnd && TryParseTimeInput(EndTextBox.Text, out var end) && value > ClampTime(end))
        {
            SetTimeText(EndTextBox, value);
        }

        SetTimeText(StartTextBox, value);
    }

    private void SetEndTime(TimeSpan value, bool adjustStart)
    {
        value = ClampTime(value);
        if (adjustStart && TryParseTimeInput(StartTextBox.Text, out var start) && value < ClampTime(start))
        {
            SetTimeText(StartTextBox, value);
        }

        SetTimeText(EndTextBox, value);
    }

    private void SetSplitTime(TimeSpan value)
    {
        SetTimeText(SplitTextBox, ClampTime(value));
    }

    private void UpdateCutModeVisibility()
    {
        if (RangeStartGrid is null || RangeEndGrid is null || SplitPointGrid is null || ConcatOptionsGrid is null)
        {
            return;
        }

        var splitMode = CurrentCutMode == CutMode.Split;
        var rangeMode = CurrentCutMode == CutMode.Range;
        var audioExtractMode = CurrentCutMode == CutMode.AudioExtract;
        var concatMode = CurrentCutMode == CutMode.Concat;
        RangeStartGrid.Visibility = rangeMode ? Visibility.Visible : Visibility.Collapsed;
        RangeEndGrid.Visibility = rangeMode ? Visibility.Visible : Visibility.Collapsed;
        SplitPointGrid.Visibility = splitMode ? Visibility.Visible : Visibility.Collapsed;
        ConcatOptionsGrid.Visibility = Visibility.Visible;
        VideoListDescriptionText.Text = CurrentCutMode switch
        {
            CutMode.Range => "시작·종료 지점을 지정해 설정한 구간을 잘라냅니다.",
            CutMode.Split => "선택한 지점을 기준으로 앞뒤로 영상을 2개로 분할합니다.",
            CutMode.AudioExtract => "선택한 영상에서 오디오 트랙 하나를 원본 그대로 추출합니다.",
            _ => "2개 이상의 영상을 선택한 순서대로 이어 붙입니다."
        };
        ConcatMoveButtonsGrid.Visibility = concatMode ? Visibility.Visible : Visibility.Collapsed;
        SelectListedVideoButton.Visibility = concatMode ? Visibility.Collapsed : Visibility.Visible;
        ConcatTotalDurationText.Visibility = concatMode ? Visibility.Visible : Visibility.Collapsed;
        CutButton.Content = CurrentCutMode switch
        {
            CutMode.Concat => "이어 붙이기 실행",
            CutMode.AudioExtract => "오디오 추출",
            _ => "자르기 실행"
        };
        AudioTrackHost.Visibility = Visibility.Visible;
        MixAudioTracksCheckBox.Visibility = audioExtractMode ? Visibility.Collapsed : Visibility.Visible;

        _duration = concatMode ? GetConcatTotalDuration() : _singleInputDuration;
        DurationText.Text = FormatTime(_duration);
        TimelineSlider.Maximum = Math.Max(1, _duration.TotalMilliseconds);
        UpdateTimelineMarkers();
        UpdateConcatClipButtonStates();
        ValidateTrimRange();
    }

    private static void SetTimeText(TextBox textBox, TimeSpan value)
    {
        textBox.Text = FormatTime(value);
        textBox.CaretIndex = textBox.Text.Length;
    }

    private static string BuildCutNotice()
    {
        return "시작지점과 종료지점이 컨테이너 경계로 인한 오차가 있을 수 있습니다. 계속 진행하시겠습니까?";
    }

    private async Task<TimeSpan> ProbeDurationAsync(string inputPath)
    {
        var result = await FfmpegService.RunProcessAsync(FfmpegService.ResolveToolPath("ffprobe.exe"), new[]
        {
            "-v",
            "error",
            "-show_entries",
            "format=duration",
            "-of",
            "default=noprint_wrappers=1:nokey=1",
            inputPath
        });

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.Error.Length > 0 ? result.Error : result.Output);
        }

        var text = result.Output.Trim();
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            throw new InvalidOperationException($"영상 길이를 읽을 수 없습니다: {text}");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private async Task<string[]> ProbeAudioCodecNamesAsync(string inputPath)
    {
        var result = await FfmpegService.RunProcessAsync(FfmpegService.ResolveToolPath("ffprobe.exe"), new[]
        {
            "-v",
            "error",
            "-select_streams",
            "a",
            "-show_entries",
            "stream=codec_name",
            "-of",
            "csv=p=0",
            inputPath
        });

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.Error.Length > 0 ? result.Error : result.Output);
        }

        return result.Output
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
    }

    private void ValidateTrimRange()
    {
        var hasModeInput = CurrentCutMode == CutMode.Concat
            ? _concatClips.Count >= 2
            : _inputPath is not null;
        var canCut = _controlsEnabled
            && !_isCutting
            && hasModeInput
            && !string.IsNullOrWhiteSpace(_outputPath);

        if (canCut)
        {
            canCut = CurrentCutMode switch
            {
                CutMode.Split => TryReadSplitPoint(out _),
                CutMode.Concat => _concatClips.Count >= 2,
                CutMode.AudioExtract => _selectedAudioTrackDisplayIndex.HasValue
                    && _audioTrackOptions.Any(audio => audio.DisplayIndex == _selectedAudioTrackDisplayIndex.Value),
                _ => TryReadTrimRange(out var start, out var end)
                    && end > start
                    && start >= TimeSpan.Zero
                    && (_duration == TimeSpan.Zero || end <= _duration)
            };
        }

        CutButton.IsEnabled = canCut;
    }

    private bool TryReadTrimRange(out TimeSpan start, out TimeSpan end)
    {
        if (!TryParseTimeInput(StartTextBox.Text, out start) || !TryParseTimeInput(EndTextBox.Text, out end))
        {
            start = TimeSpan.Zero;
            end = TimeSpan.Zero;
            return false;
        }

        start = ClampTime(start);
        end = ClampTime(end);
        return start <= end;
    }

    private bool TryReadSplitPoint(out TimeSpan splitPoint)
    {
        if (!TryParseTimeInput(SplitTextBox.Text, out splitPoint))
        {
            splitPoint = TimeSpan.Zero;
            return false;
        }

        splitPoint = ClampTime(splitPoint);
        return _duration > TimeSpan.Zero
            && splitPoint > TimeSpan.Zero
            && splitPoint < _duration;
    }

    private void SetControlsEnabled(bool enabled)
    {
        _controlsEnabled = enabled;

        OpenButton.IsEnabled = !_isCutting;
        CutModeTabs.IsEnabled = !_isCutting;
        PlayPauseButton.IsEnabled = enabled || (_isCutting && HasPlayableInput);
        SetStartButton.IsEnabled = enabled;
        SetEndButton.IsEnabled = enabled;
        SetSplitButton.IsEnabled = enabled;
        PickOutputButton.IsEnabled = enabled;
        OpenOutputFolderButton.IsEnabled = enabled
            && !string.IsNullOrWhiteSpace(_outputPath)
            && Directory.Exists(Path.GetDirectoryName(_outputPath));
        SeekStepComboBox.IsEnabled = enabled;
        VolumeSlider.IsEnabled = enabled;
        TimelineSlider.IsEnabled = enabled;
        UpdateAudioMixOptionState();
        UpdateConcatClipButtonStates();
        ValidateTrimRange();
    }

    private void MixAudioTracksCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _mixAudioTracksToSingleTrack = MixAudioTracksCheckBox.IsChecked == true;
    }

    private void UpdateAudioMixOptionState()
    {
        if (MixAudioTracksCheckBox is null)
        {
            return;
        }

        MixAudioTracksCheckBox.IsEnabled = CurrentCutMode != CutMode.AudioExtract
            && !_isCutting
            && _audioTrackOptions.Count(audio => !audio.ExcludeFromOutput) > 1;
    }

    private bool ShouldMixAudioTracks()
    {
        return _mixAudioTracksToSingleTrack
            && _audioTrackOptions.Count(audio => !audio.ExcludeFromOutput) > 1;
    }

    private AudioTrackOption? GetSelectedAudioTrackOption()
    {
        return _audioTrackOptions
            .FirstOrDefault(audio => audio.DisplayIndex == _selectedAudioTrackDisplayIndex);
    }

    private async Task ExtractAudioButton_ClickAsync()
    {
        if (_inputPath is null)
        {
            MessageBox.Show(this, "오디오를 추출할 영상을 선택하세요.", "오디오 추출", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedAudio = GetSelectedAudioTrackOption();
        if (selectedAudio is null)
        {
            MessageBox.Show(this, "추출할 오디오 트랙을 선택하세요.", "오디오 추출", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(_outputPath))
        {
            RefreshDefaultOutputPathForCurrentMode();
        }

        if (Path.GetFullPath(_inputPath).Equals(Path.GetFullPath(_outputPath!), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "입력 파일과 출력 파일이 같을 수 없습니다.", "저장 위치 확인", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetOutputPath(OutputPathService.GetAvailableOutputPath(_outputPath!));
        var codecText = string.IsNullOrWhiteSpace(selectedAudio.CodecName)
            ? ""
            : $" ({selectedAudio.CodecName})";
        var extractAnswer = MessageBox.Show(
            this,
            $"선택한 영상: {Path.GetFileName(_inputPath)}\n" +
            $"선택한 오디오 트랙: {selectedAudio.DisplayIndex}번 - {selectedAudio.Name}{codecText}\n" +
            "추출하시겠습니까?",
            "오디오 추출 확인",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (extractAnswer != MessageBoxResult.Yes)
        {
            StatusText.Text = "오디오 추출을 취소했습니다.";
            ValidateTrimRange();
            return;
        }

        _isCutting = true;
        SetControlsEnabled(false);
        CutButton.Content = "추출 중...";
        StatusText.Text = $"오디오 트랙 {selectedAudio.DisplayIndex} 추출 중입니다.";
        var stopwatch = Stopwatch.StartNew();
        UpdateCutProgress(0, stopwatch.Elapsed, null, "오디오 추출");

        try
        {
            var args = FfmpegService.BuildAudioExtractArguments(_inputPath, selectedAudio.DisplayIndex, _outputPath!);
            var result = await FfmpegService.RunFfmpegAsync(FfmpegService.ResolveToolPath("ffmpeg.exe"), args, _duration, stopwatch, "오디오 추출", ReportCutProgress);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(result.Error.Length > 0 ? result.Error : result.Output);
            }

            UpdateCutProgress(100, stopwatch.Elapsed, TimeSpan.Zero);
            StatusText.Text = $"완료: {Path.GetFileName(_outputPath)}";
            MessageBox.Show(this, "오디오 추출 완료", "완료", MessageBoxButton.OK, MessageBoxImage.Information);
            SetOutputPath(OutputPathService.GetAvailableOutputPath(_outputPath!));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"오디오 추출 실패: {Shorten(ex.Message)}";
            MessageBox.Show(this, ex.Message, "오디오 추출 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isCutting = false;
            CutButton.Content = "오디오 추출";
            SetControlsEnabled(HasPlayableInput);
            ValidateTrimRange();
        }
    }

    private TimeSpan GetCurrentTime()
    {
        return GetDisplayTime();
    }

    private TimeSpan GetDisplayTime()
    {
        if (_isPlaybackPaused)
        {
            return ClampTime(_displayTimeBase);
        }

        return ClampTime(_displayTimeBase + (_playbackClock.Elapsed - _displayClockBase));
    }

    private void ResetDisplayClock(TimeSpan time)
    {
        _displayTimeBase = ClampTime(time);
        _displayClockBase = _playbackClock.Elapsed;
        _lastVlcSyncClock = _displayClockBase;
    }

    private void SyncDisplayClockFromVlc(bool force)
    {
        if (CurrentCutMode == CutMode.Concat)
        {
            if (_currentConcatClipIndex < 0 || _currentConcatClipIndex >= _concatClips.Count)
            {
                return;
            }

            var concatNow = _playbackClock.Elapsed;
            if (!force && concatNow - _lastVlcSyncClock < TimeSpan.FromMilliseconds(250))
            {
                return;
            }

            var clipStart = GetConcatClipStart(_currentConcatClipIndex);
            var concatVlcTime = TimeSpan.FromMilliseconds(Math.Max(0, _mediaPlayer.Time));
            var concatDisplayTime = ClampTime(clipStart + concatVlcTime);
            var currentDisplayTime = GetDisplayTime();
            if (force || Math.Abs((concatDisplayTime - currentDisplayTime).TotalMilliseconds) > 250)
            {
                _displayTimeBase = concatDisplayTime;
                _displayClockBase = concatNow;
            }

            _lastVlcSyncClock = concatNow;
            return;
        }

        if (_inputPath is null)
        {
            return;
        }

        var now = _playbackClock.Elapsed;
        if (!force && now - _lastVlcSyncClock < TimeSpan.FromMilliseconds(250))
        {
            return;
        }

        var vlcTime = ClampTime(TimeSpan.FromMilliseconds(Math.Max(0, _mediaPlayer.Time)));
        var displayTime = GetDisplayTime();
        if (force || Math.Abs((vlcTime - displayTime).TotalMilliseconds) > 250)
        {
            _displayTimeBase = vlcTime;
            _displayClockBase = now;
        }

        _lastVlcSyncClock = now;
    }

    private bool IsAtPlaybackEnd()
    {
        return IsAtPlaybackEnd(GetCurrentTime());
    }

    private bool IsAtPlaybackEnd(TimeSpan time)
    {
        return _duration > TimeSpan.Zero
            && _duration - time <= GetPlaybackEndTolerance();
    }

    private TimeSpan GetPlaybackEndTolerance()
    {
        return CurrentCutMode == CutMode.Concat
            ? TimeSpan.FromMilliseconds(80)
            : TimeSpan.FromMilliseconds(300);
    }

    private TimeSpan GetSeekStep()
    {
        if (SeekStepComboBox.SelectedItem is ComboBoxItem item
            && item.Tag is string tag
            && int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return TimeSpan.FromSeconds(5);
    }

    private void SeekRelative(TimeSpan delta)
    {
        SeekTo(GetCurrentTime() + delta);
    }

    private void SeekToSliderValue()
    {
        SeekTo(TimeSpan.FromMilliseconds(TimelineSlider.Value));
    }

    private void SeekTo(TimeSpan target)
    {
        target = ClampTime(target);
        if ((_isPlaybackEnded || _mediaPlayer.State == VLCState.Ended) && !IsAtPlaybackEnd(target))
        {
            RestartPlaybackFrom(target);
            return;
        }

        if (CurrentCutMode == CutMode.Concat)
        {
            LoadConcatClipForTime(target, autoPlay: !_isPlaybackPaused);
            SetConcatTimelinePosition(target);
            return;
        }

        var milliseconds = (long)Math.Round(target.TotalMilliseconds);

        _mediaPlayer.Time = milliseconds;
        ResetDisplayClock(target);
        CurrentTimeText.Text = FormatTime(target);

        _isUpdatingTimeline = true;
        TimelineSlider.Value = Math.Clamp(target.TotalMilliseconds, TimelineSlider.Minimum, TimelineSlider.Maximum);
        _isUpdatingTimeline = false;
    }

    private void RestartPlaybackFrom(TimeSpan target)
    {
        if (CurrentCutMode == CutMode.Concat)
        {
            if (_concatClips.Count == 0)
            {
                return;
            }

            target = ClampTime(target);
            LoadConcatClipForTime(target, autoPlay: true);
            ResetDisplayClock(target);
            _isPlaybackEnded = false;
            _isPlaybackPaused = false;
            PlayPauseButton.Content = "일시정지";
            CurrentTimeText.Text = FormatTime(target);

            _isUpdatingTimeline = true;
            TimelineSlider.Value = Math.Clamp(target.TotalMilliseconds, TimelineSlider.Minimum, TimelineSlider.Maximum);
            _isUpdatingTimeline = false;
            return;
        }

        if (_inputPath is null)
        {
            return;
        }

        target = ClampTime(target);
        using var media = new VlcMedia(_libVlc, new Uri(_inputPath));
        _mediaPlayer.Play(media);
        _mediaPlayer.Time = (long)Math.Round(target.TotalMilliseconds);
        ResetDisplayClock(target);
        _isPlaybackEnded = false;
        _isPlaybackPaused = false;
        _mediaPlayer.SetPause(false);
        if (_audioTrackOptions.Count > 0)
        {
            ApplySelectedAudioTrack();
        }

        ApplyAudioPlaybackStateOrdered(syncTime: true);
        PlayPauseButton.Content = "일시정지";
        CurrentTimeText.Text = FormatTime(target);

        _isUpdatingTimeline = true;
        TimelineSlider.Value = Math.Clamp(target.TotalMilliseconds, TimelineSlider.Minimum, TimelineSlider.Maximum);
        _isUpdatingTimeline = false;
    }

    private TimeSpan ClampTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (_duration > TimeSpan.Zero && time > _duration)
        {
            return _duration;
        }

        return time;
    }

    private static TimeSpan ClampToDuration(TimeSpan time, TimeSpan duration)
    {
        if (time < TimeSpan.Zero || duration <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return time > duration ? duration : time;
    }

    private void ResetCutProgress()
    {
        CutProgressText.Text = "";
        CutProgressBar.Value = 0;
    }

    private void UpdateCutProgress(double percent, TimeSpan elapsed, TimeSpan? remaining, string? title = null)
    {
        percent = Math.Clamp(percent, 0, 100);
        var remainingText = remaining.HasValue ? FormatClock(remaining.Value) : "계산 중";
        var progressText = $"진행률 {percent:0.0}% | 소요 {FormatClock(elapsed)} | 남은 {remainingText}";
        CutProgressText.Text = string.IsNullOrWhiteSpace(title)
            ? progressText
            : $"{title}\n{progressText}";
        CutProgressBar.Value = percent;
    }

    private void ReportCutProgress(double encodedSeconds, TimeSpan totalDuration, Stopwatch stopwatch, string? title)
    {
        if (totalDuration <= TimeSpan.Zero)
        {
            return;
        }

        var percent = Math.Clamp(encodedSeconds / totalDuration.TotalSeconds * 100, 0, 100);
        TimeSpan? remaining = null;
        if (percent > 0.1)
        {
            var remainingSeconds = stopwatch.Elapsed.TotalSeconds * (100 - percent) / percent;
            remaining = TimeSpan.FromSeconds(Math.Max(0, remainingSeconds));
        }

        Dispatcher.BeginInvoke(() => UpdateCutProgress(percent, stopwatch.Elapsed, remaining, title));
    }

    private CutJob[] BuildCutJobs(string outputPath)
    {
        if (CurrentCutMode == CutMode.Split)
        {
            if (!TryReadSplitPoint(out var splitPoint))
            {
                return Array.Empty<CutJob>();
            }

            return new[]
            {
                new CutJob(TimeSpan.Zero, splitPoint, OutputPathService.GetAvailableOutputPath(OutputPathService.BuildPartOutputPath(outputPath, 1))),
                new CutJob(splitPoint, _duration - splitPoint, OutputPathService.GetAvailableOutputPath(OutputPathService.BuildPartOutputPath(outputPath, 2)))
            };
        }

        if (!TryReadTrimRange(out var start, out var end) || end <= start)
        {
            return Array.Empty<CutJob>();
        }

        return new[]
        {
            new CutJob(start, end - start, OutputPathService.GetAvailableOutputPath(outputPath))
        };
    }

    private async Task EnsureConcatStreamCompatibilityAsync()
    {
        if (_concatClips.Count < 2)
        {
            return;
        }

        var firstSignature = await ProbeConcatStreamSignatureAsync(_concatClips[0].Path);
        for (var index = 1; index < _concatClips.Count; index++)
        {
            var signature = await ProbeConcatStreamSignatureAsync(_concatClips[index].Path);
            if (!string.Equals(firstSignature, signature, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "선택한 영상들의 스트림 구조가 달라서 무손실 이어붙이기를 할 수 없습니다.\n" +
                    "비디오/오디오 코덱, 해상도, 오디오 트랙 수가 같은 파일끼리만 지원합니다.");
            }
        }
    }

    private async Task<string> ProbeConcatStreamSignatureAsync(string inputPath)
    {
        var result = await FfmpegService.RunProcessAsync(FfmpegService.ResolveToolPath("ffprobe.exe"), new[]
        {
            "-v",
            "error",
            "-show_entries",
            "stream=index,codec_type,codec_name,profile,level,codec_tag_string,width,height,pix_fmt,sample_fmt,sample_rate,channels,channel_layout,time_base,avg_frame_rate,r_frame_rate,color_range,color_space,color_transfer,color_primaries,field_order,bits_per_raw_sample",
            "-of",
            "compact=p=0:nk=0",
            inputPath
        });

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.Error.Length > 0 ? result.Error : result.Output);
        }

        return string.Join(
            "\n",
            result.Output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim()));
    }

    private void SetOutputPath(string path)
    {
        _outputPath = path;
        if (OutputPathTextBlock is null)
        {
            return;
        }

        OutputPathTextBlock.Text = path;
        OutputPathTextBlock.ToolTip = path;
    }

    private void RefreshDefaultOutputPathForCurrentMode()
    {
        var defaultPath = CurrentCutMode switch
        {
            CutMode.Concat when _concatClips.Count > 0 => OutputPathService.BuildDefaultConcatOutputPath(_concatClips[0].Path),
            CutMode.AudioExtract when !string.IsNullOrWhiteSpace(_inputPath) => OutputPathService.BuildDefaultAudioExtractOutputPath(_inputPath, GetSelectedAudioTrackOption()),
            CutMode.Split when !string.IsNullOrWhiteSpace(_inputPath) => OutputPathService.BuildDefaultSplitOutputPath(_inputPath),
            CutMode.Range when !string.IsNullOrWhiteSpace(_inputPath) => OutputPathService.BuildDefaultCutOutputPath(_inputPath, "_cut"),
            _ => string.Empty
        };

        SetOutputPath(defaultPath);
    }

    private void ClearSingleVideoSelection()
    {
        _inputPath = null;
        _singleInputDuration = TimeSpan.Zero;
        if (CurrentCutMode != CutMode.Concat)
        {
            _duration = TimeSpan.Zero;
            _currentConcatClipIndex = -1;
            _mediaPlayer.Stop();
            VideoView.Visibility = Visibility.Collapsed;
            EmptyVideoText.Visibility = Visibility.Visible;
            CurrentTimeText.Text = FormatTime(TimeSpan.Zero);
            DurationText.Text = FormatTime(TimeSpan.Zero);
            TimelineSlider.Maximum = 1;
            TimelineSlider.Value = 0;
            ResetDisplayClock(TimeSpan.Zero);
        }

        SetInputPathDisplay(null);
        SetOutputPath(string.Empty);
        ResetCutProgress();
        SetStartTime(TimeSpan.Zero, adjustEnd: false);
        SetEndTime(TimeSpan.Zero, adjustStart: false);
        SetSplitTime(TimeSpan.Zero);
        ClearAudioTrackOptions();
        _isPlaybackPaused = true;
        _isPlaybackEnded = false;
        PlayPauseButton.Content = "재생";
        SetControlsEnabled(HasPlayableInput);
    }

    private void SetInputPathDisplay(string? path)
    {
        FileNameText.Inlines.Clear();
        FileNameText.FontWeight = FontWeights.Normal;

        if (string.IsNullOrWhiteSpace(path))
        {
            FileNameText.Inlines.Add(new Run("선택한 비디오 없음"));
            FileNameText.ToolTip = null;
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            FileNameText.Inlines.Add(new Run($"{directory}{Path.DirectorySeparatorChar}"));
        }

        FileNameText.Inlines.Add(new Run(Path.GetFileName(path))
        {
            FontWeight = FontWeights.SemiBold
        });
        FileNameText.ToolTip = path;
    }

    private static bool TryParseTimeInput(string text, out TimeSpan time)
    {
        text = text.Trim().Replace(',', '.');

        if ((text.Contains(':') || text.Contains('.')) && TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out time))
        {
            return true;
        }

        var digits = new string(text.Where(char.IsDigit).ToArray());
        if (digits.Length == 0)
        {
            time = TimeSpan.Zero;
            return true;
        }

        digits = digits.Length > 9 ? digits[..9] : digits.PadRight(9, '0');

        var hours = int.Parse(digits[..2], CultureInfo.InvariantCulture);
        var minutes = int.Parse(digits.Substring(2, 2), CultureInfo.InvariantCulture);
        var seconds = int.Parse(digits.Substring(4, 2), CultureInfo.InvariantCulture);
        var milliseconds = int.Parse(digits.Substring(6, 3), CultureInfo.InvariantCulture);
        time = new TimeSpan(0, hours, minutes, seconds, milliseconds);
        return true;
    }

    private static string FormatTime(TimeSpan time)
    {
        var totalHours = (int)Math.Floor(time.TotalHours);
        return $"{totalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
    }

    private static string FormatClock(TimeSpan time)
    {
        var totalHours = (int)Math.Floor(time.TotalHours);
        return $"{totalHours:00}:{time.Minutes:00}:{time.Seconds:00}";
    }

    private static string Shorten(string text)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= 180 ? text : text[..180] + "...";
    }

}

