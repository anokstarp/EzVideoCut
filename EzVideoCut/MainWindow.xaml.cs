using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using VlcMedia = LibVLCSharp.Shared.Media;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace EzVideoCut;

public partial class MainWindow : Window
{
    private enum CutMode
    {
        Range,
        Split
    }

    private readonly DispatcherTimer _positionTimer;
    private readonly LibVLC _libVlc;
    private readonly VlcMediaPlayer _mediaPlayer;
    private readonly List<AdditionalAudioPlayer> _additionalAudioPlayers = new();
    private readonly List<AudioTrackOption> _audioTrackOptions = new();
    private readonly Stopwatch _playbackClock = Stopwatch.StartNew();

    private string? _inputPath;
    private string? _outputPath;
    private TimeSpan _duration = TimeSpan.Zero;
    private TimeSpan _displayTimeBase = TimeSpan.Zero;
    private TimeSpan _displayClockBase = TimeSpan.Zero;
    private TimeSpan _lastVlcSyncClock = TimeSpan.Zero;
    private bool _controlsEnabled;
    private bool _isCutting;
    private bool _isDraggingTimeline;
    private bool _isUpdatingTimeline;
    private bool _isPlaybackPaused = true;
    private bool _isPlaybackEnded;
    private int? _selectedAudioTrackDisplayIndex;
    private int _audioPlaybackStateVersion;
    private int _masterVolume = 100;

    public MainWindow()
    {
        InitializeComponent();
        UpdateCutModeVisibility();
        AudioTrackHelpText.Text = string.Empty;

        Core.Initialize();
        _libVlc = new LibVLC("--no-video-title-show");
        _masterVolume = (int)VolumeSlider.Value;
        _mediaPlayer = new VlcMediaPlayer(_libVlc)
        {
            Volume = 0,
            Mute = true
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

    private CutMode CurrentCutMode => CutModeTabs.SelectedIndex == 0 ? CutMode.Split : CutMode.Range;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_inputPath is null || e.Key is not Key.Left and not Key.Right and not Key.Space)
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

        if (handled || !IsActive || _inputPath is null || msg.message != wmKeyDown)
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
        const double aspectRatio = 16d / 9d;
        var availableWidth = Math.Max(0, e.NewSize.Width - VideoHost.BorderThickness.Left - VideoHost.BorderThickness.Right);
        var availableHeight = Math.Max(0, e.NewSize.Height - VideoHost.BorderThickness.Top - VideoHost.BorderThickness.Bottom);
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

        _inputPath = dialog.FileName;

        ClearAudioTrackOptions();
        FileNameText.Text = Path.GetFileName(_inputPath);
        SetOutputPath(BuildDefaultOutputPath(_inputPath));
        ResetCutProgress();
        StatusText.Text = "영상 정보를 읽는 중...";

        SetControlsEnabled(false);

        try
        {
            _duration = await ProbeDurationAsync(_inputPath);
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
            await StartAdditionalAudioTracksAsync();
            _isPlaybackPaused = false;
            _mediaPlayer.SetPause(false);
            ResetDisplayClock(TimeSpan.FromMilliseconds(Math.Max(0, _mediaPlayer.Time)));
            ApplySelectedAudioTrack();
            PlayPauseButton.Content = "?쇱떆?뺤?";
            StatusText.Text = "영상이 열렸습니다.";
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
        if (_inputPath is null)
        {
            return;
        }

        if (!_isPlaybackPaused)
        {
            _isPlaybackPaused = true;
            _isPlaybackEnded = false;
            _mediaPlayer.SetPause(true);
            SyncDisplayClockFromVlc(force: true);
            PlayPauseButton.Content = "재생";
        }
        else
        {
            if (IsAtPlaybackEnd())
            {
                RestartPlaybackFrom(TimeSpan.Zero);
                return;
            }

            _mediaPlayer.Play();
            _mediaPlayer.SetPause(false);
            _isPlaybackPaused = false;
            _isPlaybackEnded = false;
            ResetDisplayClock(GetCurrentTime());
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

        UpdateCutModeVisibility();
    }

    private void PickOutputButton_Click(object sender, RoutedEventArgs e)
    {
        if (_inputPath is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "저장 위치 선택",
            FileName = Path.GetFileName(_outputPath),
            InitialDirectory = Path.GetDirectoryName(_outputPath),
            Filter = "MP4 file|*.mp4|MKV file|*.mkv|MOV file|*.mov|All files|*.*",
            OverwritePrompt = false
        };

        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        SetOutputPath(GetAvailableOutputPath(dialog.FileName));
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
        if (_isCutting || _inputPath is null || _outputPath is null)
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
        var hasMutedAudioTracks = _audioTrackOptions
            .Any(track => track.MuteInOutput && !track.ExcludeFromOutput);
        if (excludedAudioTracks.Length > 0)
        {
            var trackList = string.Join(", ", excludedAudioTracks.Select(track => $"{track.DisplayIndex}번"));
            var deleteAnswer = MessageBox.Show(
                this,
                $"오디오 제거는 음소거가 아니라 출력 파일에서 트랙이 제거됩니다.\n\n제거할 오디오 트랙: {trackList}\n\n계속 진행하시겠습니까?",
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
        StatusText.Text = hasMutedAudioTracks
            ? "ffmpeg 컷과 오디오 음소거를 실행 중입니다."
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
                var args = BuildFfmpegCutArguments(_inputPath, job.Start, job.Duration, job.OutputPath, _audioTrackOptions);
                var result = await RunFfmpegCutAsync(ResolveToolPath("ffmpeg.exe"), args, job.Duration, stopwatch, progressTitle);
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
                SetOutputPath(GetAvailableOutputPath(_outputPath));
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
            SetControlsEnabled(_inputPath is not null);
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
        if (_inputPath is null)
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
        if (_inputPath is null || !_isDraggingTimeline || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        SeekToTimelineClick(e.GetPosition(TimelineSlider));
        e.Handled = true;
    }

    private void TimelineSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_inputPath is null)
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
        if (_isUpdatingTimeline || !_isDraggingTimeline || _inputPath is null)
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

    private void PositionTimer_Tick(object? sender, EventArgs e)
    {
        if (_inputPath is null || _isDraggingTimeline)
        {
            return;
        }

        SyncDisplayClockFromVlc(force: false);
        var current = GetDisplayTime();
        CurrentTimeText.Text = FormatTime(current);

        _isUpdatingTimeline = true;
        TimelineSlider.Value = Math.Clamp(current.TotalMilliseconds, TimelineSlider.Minimum, TimelineSlider.Maximum);
        _isUpdatingTimeline = false;
        if (!_isPlaybackPaused && IsAtPlaybackEnd())
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
        if (RangeStartGrid is null || RangeEndGrid is null || SplitPointGrid is null)
        {
            return;
        }

        var splitMode = CurrentCutMode == CutMode.Split;
        RangeStartGrid.Visibility = splitMode ? Visibility.Collapsed : Visibility.Visible;
        RangeEndGrid.Visibility = splitMode ? Visibility.Collapsed : Visibility.Visible;
        SplitPointGrid.Visibility = splitMode ? Visibility.Visible : Visibility.Collapsed;
        ValidateTrimRange();
    }

    private static void SetTimeText(TextBox textBox, TimeSpan value)
    {
        textBox.Text = FormatTime(value);
        textBox.CaretIndex = textBox.Text.Length;
    }

    private static string BuildCutNotice()
    {
        return
            "시작지점과 종료지점이 컨테이너 경계로 인한 오차가 있을 수 있습니다.\n" +
            "계속 진행하시겠습니까?";
    }

    private async Task<TimeSpan> ProbeDurationAsync(string inputPath)
    {
        var result = await RunProcessAsync(ResolveToolPath("ffprobe.exe"), new[]
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

    private void ValidateTrimRange()
    {
        var canCut = _controlsEnabled
            && !_isCutting
            && _inputPath is not null
            && _outputPath is not null;

        if (canCut)
        {
            canCut = CurrentCutMode == CutMode.Split
                ? TryReadSplitPoint(out _)
                : TryReadTrimRange(out var start, out var end)
                    && end > start
                    && start >= TimeSpan.Zero
                    && (_duration == TimeSpan.Zero || end <= _duration);
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
        PlayPauseButton.IsEnabled = enabled || (_isCutting && _inputPath is not null);
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
        ValidateTrimRange();
    }

    private async Task StartAdditionalAudioTracksAsync()
    {
        ClearAudioTrackOptions();
        if (_inputPath is null)
        {
            return;
        }

        var tracks = Array.Empty<LibVLCSharp.Shared.Structures.TrackDescription>();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            tracks = _mediaPlayer.AudioTrackDescription?
                .Where(track => track.Id >= 0)
                .ToArray() ?? Array.Empty<LibVLCSharp.Shared.Structures.TrackDescription>();

            if (tracks.Length > 0)
            {
                break;
            }

            await Task.Delay(250);
        }

        if (tracks.Length == 0)
        {
            ApplyPrimaryAudioVolumeOnly();
            AudioTrackHost.Visibility = Visibility.Collapsed;
            return;
        }

        for (var index = 0; index < tracks.Length; index++)
        {
            var track = tracks[index];
            var displayName = string.IsNullOrWhiteSpace(track.Name) ? $"Track {index + 1}" : track.Name;
            _audioTrackOptions.Add(new AudioTrackOption(track.Id, index + 1, displayName));
        }

        _selectedAudioTrackDisplayIndex = _audioTrackOptions.FirstOrDefault()?.DisplayIndex;
        ApplySelectedAudioTrack();
        PopulateAudioTrackControls();
    }

    private static async Task InitializeAdditionalAudioPlayerAsync(AdditionalAudioPlayer audio, TimeSpan currentTime)
    {
        var tracks = Array.Empty<LibVLCSharp.Shared.Structures.TrackDescription>();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            tracks = audio.Player.AudioTrackDescription?
                .Where(track => track.Id >= 0)
                .ToArray() ?? Array.Empty<LibVLCSharp.Shared.Structures.TrackDescription>();

            if (tracks.Length > 0)
            {
                break;
            }

            await Task.Delay(100);
        }

        audio.SelectedTrackId = ResolveTrackId(audio, tracks);
        audio.TrackSelectionSucceeded = audio.Player.SetAudioTrack(audio.SelectedTrackId);
        audio.Player.Time = (long)Math.Round(currentTime.TotalMilliseconds);
    }

    private static int ResolveTrackId(
        AdditionalAudioPlayer audio,
        LibVLCSharp.Shared.Structures.TrackDescription[] tracks)
    {
        if (tracks.Any(track => track.Id == audio.TrackId))
        {
            return audio.TrackId;
        }

        var index = audio.DisplayIndex - 1;
        if (index >= 0 && index < tracks.Length)
        {
            return tracks[index].Id;
        }

        return audio.TrackId;
    }

    private void PlayAdditionalAudioPlayers()
    {
        _isPlaybackPaused = false;
        ApplyAudioPlaybackStateOrdered(syncTime: true);
    }

    private void PauseAdditionalAudioPlayers()
    {
        _audioPlaybackStateVersion++;
        _isPlaybackPaused = true;
        foreach (var audio in _additionalAudioPlayers)
        {
            audio.Player.SetPause(true);
        }
    }

    private void ApplyAdditionalAudioVolume()
    {
        ApplyAdditionalAudioVolumeOnly();
    }

    private void ApplyPrimaryAudioVolumeOnly()
    {
        _mediaPlayer.Mute = _masterVolume <= 0;
        _mediaPlayer.Volume = _masterVolume;
    }

    private void ApplyAdditionalAudioVolumeOnly()
    {
        foreach (var audio in _additionalAudioPlayers)
        {
            var isSelected = _selectedAudioTrackDisplayIndex == audio.DisplayIndex;
            if (isSelected)
            {
                audio.Player.Mute = _masterVolume <= 0;
                audio.Player.Volume = _masterVolume;
            }
            else
            {
                audio.Player.Volume = 0;
            }
        }
    }

    private void ApplyPrimaryAudioSettings()
    {
        if (_additionalAudioPlayers.Count == 0)
        {
            ApplyPrimaryAudioVolumeOnly();
            return;
        }

        _mediaPlayer.SetAudioTrack(-1);
        _mediaPlayer.Mute = true;
        _mediaPlayer.Volume = 0;
    }

    private void ApplyAudioPlaybackState(bool syncTime)
    {
        if (_additionalAudioPlayers.Count == 0)
        {
            ApplyPrimaryAudioVolumeOnly();
            _mediaPlayer.SetPause(_isPlaybackPaused);
            return;
        }

        _mediaPlayer.Mute = true;
        _mediaPlayer.Volume = 0;

        var currentMilliseconds = (long)Math.Round(GetCurrentTime().TotalMilliseconds);
        foreach (var audio in _additionalAudioPlayers)
        {
            audio.Player.Volume = 0;
            audio.Player.SetPause(true);
            audio.Player.Mute = false;

            if (syncTime)
            {
                audio.Player.Time = currentMilliseconds;
            }
        }

        var selectedAudio = _additionalAudioPlayers
            .FirstOrDefault(audio => audio.DisplayIndex == _selectedAudioTrackDisplayIndex);
        if (selectedAudio is null)
        {
            StatusText.Text = "선택된 오디오 트랙을 찾을 수 없습니다.";
            return;
        }

        selectedAudio.Player.SetAudioTrack(selectedAudio.SelectedTrackId);
        if (syncTime)
        {
            selectedAudio.Player.Time = currentMilliseconds;
        }

        selectedAudio.Player.Mute = _masterVolume <= 0;
        selectedAudio.Player.Volume = _masterVolume;

        if (!_isPlaybackPaused)
        {
            selectedAudio.Player.Play();
            if (syncTime)
            {
                selectedAudio.Player.Time = currentMilliseconds;
            }

            selectedAudio.Player.SetPause(false);
        }
        else
        {
            selectedAudio.Player.SetPause(true);
        }
    }

    private void ApplyAudioPlaybackStateOrdered(bool syncTime)
    {
        var version = ++_audioPlaybackStateVersion;
        _ = ApplyAudioPlaybackStateOrderedAsync(syncTime, version);
    }

    private async Task ApplyAudioPlaybackStateOrderedAsync(bool syncTime, int version)
    {
        if (_additionalAudioPlayers.Count == 0)
        {
            ApplyPrimaryAudioVolumeOnly();
            _mediaPlayer.SetPause(_isPlaybackPaused);
            return;
        }

        _mediaPlayer.Mute = true;
        _mediaPlayer.Volume = 0;

        var currentMilliseconds = (long)Math.Round(GetCurrentTime().TotalMilliseconds);
        var selectedAudio = _additionalAudioPlayers
            .FirstOrDefault(audio => audio.DisplayIndex == _selectedAudioTrackDisplayIndex);
        if (selectedAudio is null)
        {
            StatusText.Text = "선택된 오디오 트랙을 찾을 수 없습니다.";
            return;
        }

        foreach (var audio in _additionalAudioPlayers)
        {
            if (audio == selectedAudio)
            {
                continue;
            }

            audio.Player.Volume = 0;
            audio.Player.SetPause(true);
            audio.Player.Mute = false;

            if (syncTime)
            {
                audio.Player.Time = currentMilliseconds;
            }
        }

        if (version != _audioPlaybackStateVersion)
        {
            return;
        }

        selectedAudio.Player.SetAudioTrack(selectedAudio.SelectedTrackId);
        if (syncTime)
        {
            selectedAudio.Player.Time = currentMilliseconds;
        }

        selectedAudio.Player.Mute = _masterVolume <= 0;
        selectedAudio.Player.Volume = _masterVolume;

        if (_isPlaybackPaused)
        {
            selectedAudio.Player.SetPause(true);
            return;
        }

        selectedAudio.Player.Play();
        if (syncTime)
        {
            selectedAudio.Player.Time = currentMilliseconds;
        }

        await Dispatcher.Yield(DispatcherPriority.Background);
        if (version != _audioPlaybackStateVersion)
        {
            return;
        }

        var latestMilliseconds = (long)Math.Round(GetCurrentTime().TotalMilliseconds);
        selectedAudio.Player.SetAudioTrack(selectedAudio.SelectedTrackId);
        if (syncTime)
        {
            selectedAudio.Player.Time = latestMilliseconds;
        }

        selectedAudio.Player.Mute = _masterVolume <= 0;
        selectedAudio.Player.Volume = _masterVolume;
        selectedAudio.Player.SetPause(false);
    }

    private void PopulateAudioTrackControls()
    {
        AudioTracksPanel.Children.Clear();
        AudioTrackHost.Visibility = _audioTrackOptions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var audio in _audioTrackOptions)
        {
            AudioTracksPanel.Children.Add(CreateAudioTrackOptionControl(audio));
        }

        UpdateAudioTrackButtonStates();
    }

    private UIElement CreateAudioTrackOptionControl(AudioTrackOption audio)
    {
        var box = new Border
        {
            Margin = new Thickness(0, 0, 10, 0),
            Padding = new Thickness(8),
            BorderBrush = System.Windows.Media.Brushes.LightGray,
            BorderThickness = new Thickness(1),
            Background = System.Windows.Media.Brushes.White
        };

        var panel = new StackPanel
        {
            Orientation = Orientation.Vertical
        };
        box.Child = panel;

        var titlePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal
        };
        titlePanel.Children.Add(CreateAudioTrackButton(audio.DisplayIndex.ToString(CultureInfo.InvariantCulture), audio, audio.Name));
        titlePanel.Children.Add(new TextBlock
        {
            Text = audio.Name,
            Margin = new Thickness(8, 0, 0, 0),
            MaxWidth = 180,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = audio.Name
        });
        panel.Children.Add(titlePanel);

        var optionsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0)
        };

        var deleteCheckBox = new CheckBox
        {
            Content = "제거",
            Tag = audio,
            IsChecked = audio.ExcludeFromOutput,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "자르기 결과에서 이 오디오 트랙 제거"
        };
        deleteCheckBox.Checked += OutputAudioTrackCheckBox_Changed;
        deleteCheckBox.Unchecked += OutputAudioTrackCheckBox_Changed;
        optionsPanel.Children.Add(deleteCheckBox);

        var muteCheckBox = new CheckBox
        {
            Content = "음소거",
            Tag = audio,
            IsChecked = audio.MuteInOutput,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "트랙은 유지하고 출력 오디오만 0으로 만들기"
        };
        muteCheckBox.Checked += OutputAudioTrackMuteCheckBox_Changed;
        muteCheckBox.Unchecked += OutputAudioTrackMuteCheckBox_Changed;
        optionsPanel.Children.Add(muteCheckBox);

        panel.Children.Add(optionsPanel);

        return box;
    }

    private Button CreateAudioTrackButton(string text, object tag, string? toolTip)
    {
        var button = new Button
        {
            Content = text,
            Tag = tag,
            ToolTip = toolTip,
            MinWidth = 48,
            Height = 32,
            Padding = new Thickness(12, 0, 12, 0)
        };
        button.Click += AudioTrackButton_Click;
        return button;
    }

    private void OutputAudioTrackCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox && checkBox.Tag is AudioTrackOption audio)
        {
            audio.ExcludeFromOutput = checkBox.IsChecked == true;
            if (audio.ExcludeFromOutput && audio.MuteInOutput)
            {
                audio.MuteInOutput = false;
                PopulateAudioTrackControls();
            }
        }
    }

    private void OutputAudioTrackMuteCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox && checkBox.Tag is AudioTrackOption audio)
        {
            audio.MuteInOutput = checkBox.IsChecked == true;
            if (audio.MuteInOutput && audio.ExcludeFromOutput)
            {
                audio.ExcludeFromOutput = false;
                PopulateAudioTrackControls();
            }
        }
    }

    private void AudioTrackButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        if (button.Tag is not AudioTrackOption audio)
        {
            return;
        }

        _selectedAudioTrackDisplayIndex = audio.DisplayIndex;
        ApplySelectedAudioTrack();
        ReportSelectedAudioTrack();
    }

    private void ApplySelectedAudioTrack()
    {
        var selectedAudio = _audioTrackOptions
            .FirstOrDefault(audio => audio.DisplayIndex == _selectedAudioTrackDisplayIndex);
        if (selectedAudio is null)
        {
            StatusText.Text = "선택된 오디오 트랙을 찾을 수 없습니다.";
            return;
        }

        _mediaPlayer.SetAudioTrack(selectedAudio.TrackId);
        ApplyPrimaryAudioVolumeOnly();
        UpdateAudioTrackButtonStates();
    }

    private void UpdateAudioTrackButtonStates()
    {
        foreach (var button in AudioTracksPanel.Children
            .OfType<Border>()
            .SelectMany(GetAudioTrackButtons))
        {
            var isSelected = button.Tag is AudioTrackOption audio
                && _selectedAudioTrackDisplayIndex == audio.DisplayIndex;
            button.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal;
            button.Background = isSelected ? SystemColors.HighlightBrush : SystemColors.ControlBrush;
            button.Foreground = isSelected ? SystemColors.HighlightTextBrush : SystemColors.ControlTextBrush;
        }
    }

    private static IEnumerable<Button> GetAudioTrackButtons(UIElement element)
    {
        if (element is Button button)
        {
            yield return button;
        }

        if (element is Border { Child: UIElement borderChild })
        {
            foreach (var childButton in GetAudioTrackButtons(borderChild))
            {
                yield return childButton;
            }
        }

        if (element is Panel panel)
        {
            foreach (UIElement panelChild in panel.Children)
            {
                foreach (var childButton in GetAudioTrackButtons(panelChild))
                {
                    yield return childButton;
                }
            }
        }
    }

    private void ReportSelectedAudioTrack()
    {
        StatusText.Text = $"오디오 트랙 {_selectedAudioTrackDisplayIndex} 재생";
    }

    private UIElement CreateAudioTrackControl(AdditionalAudioPlayer audio)
    {
        var card = new Border
        {
            Width = 250,
            Margin = new Thickness(0, 0, 10, 0),
            Padding = new Thickness(10),
            BorderBrush = System.Windows.Media.Brushes.LightGray,
            BorderThickness = new Thickness(1),
            Background = System.Windows.Media.Brushes.White
        };

        var panel = new StackPanel();
        card.Child = panel;

        panel.Children.Add(new TextBlock
        {
            Text = $"트랙 {audio.DisplayIndex}: {audio.Name}",
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = audio.Name
        });

        var enabledCheckBox = new CheckBox
        {
            Content = "음소거",
            IsChecked = !audio.IsEnabled,
            Tag = audio,
            Margin = new Thickness(0, 8, 0, 0)
        };
        enabledCheckBox.Checked += TrackEnabledCheckBox_Changed;
        enabledCheckBox.Unchecked += TrackEnabledCheckBox_Changed;
        panel.Children.Add(enabledCheckBox);

        return card;
    }

    private void TrackEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox && checkBox.Tag is AdditionalAudioPlayer audio)
        {
            audio.IsEnabled = checkBox.IsChecked != true;
            audio.Player.SetAudioTrack(audio.SelectedTrackId);
            audio.Player.Time = (long)Math.Round(GetCurrentTime().TotalMilliseconds);
            ApplyAudioPlaybackStateOrdered(syncTime: true);
            audio.Player.SetPause(_isPlaybackPaused);
            ReportAudioTrackState(audio);
        }
    }

    private void ReportAudioTrackState(AdditionalAudioPlayer audio)
    {
        StatusText.Text =
            $"트랙 {audio.DisplayIndex} {(audio.IsEnabled ? "재생" : "음소거")} | " +
            $"선택 트랙 ID {audio.Player.AudioTrack} / 요청 {audio.TrackId}";
    }

    private void SynchronizeAdditionalAudioPlayers(TimeSpan target, bool force)
    {
        if (_isPlaybackPaused && !force)
        {
            return;
        }

        var targetMilliseconds = (long)Math.Round(target.TotalMilliseconds);
        foreach (var audio in _additionalAudioPlayers)
        {
            var drift = Math.Abs(audio.Player.Time - targetMilliseconds);
            if (force || drift > 350)
            {
                audio.Player.Time = targetMilliseconds;
            }
        }
    }

    private void DisposeAdditionalAudioPlayers()
    {
        _audioPlaybackStateVersion++;
        foreach (var audio in _additionalAudioPlayers)
        {
            audio.Player.Stop();
            audio.Player.Dispose();
            audio.Media.Dispose();
            audio.LibVlc.Dispose();
        }

        _additionalAudioPlayers.Clear();
        _selectedAudioTrackDisplayIndex = null;
        AudioTracksPanel.Children.Clear();
        AudioTrackHost.Visibility = Visibility.Collapsed;
    }

    private void ClearAudioTrackOptions()
    {
        _audioTrackOptions.Clear();
        _selectedAudioTrackDisplayIndex = null;
        AudioTracksPanel.Children.Clear();
        AudioTrackHost.Visibility = Visibility.Collapsed;
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
            && _duration - time <= TimeSpan.FromMilliseconds(300);
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
                new CutJob(TimeSpan.Zero, splitPoint, GetAvailableOutputPath(BuildPartOutputPath(outputPath, 1))),
                new CutJob(splitPoint, _duration - splitPoint, GetAvailableOutputPath(BuildPartOutputPath(outputPath, 2)))
            };
        }

        if (!TryReadTrimRange(out var start, out var end) || end <= start)
        {
            return Array.Empty<CutJob>();
        }

        return new[]
        {
            new CutJob(start, end - start, GetAvailableOutputPath(outputPath))
        };
    }

    private static List<string> BuildFfmpegCutArguments(
        string inputPath,
        TimeSpan start,
        TimeSpan trimDuration,
        string outputPath,
        IEnumerable<AudioTrackOption> audioTracks)
    {
        var audioTrackOptions = audioTracks.ToArray();
        var excludedAudioTracks = audioTrackOptions
            .Where(track => track.ExcludeFromOutput)
            .ToArray();
        var mutedAudioTracks = audioTrackOptions
            .Where(track => track.MuteInOutput && !track.ExcludeFromOutput)
            .ToArray();
        var args = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-progress",
            "pipe:1",
            "-nostats",
            "-y",
            "-ss",
            ToFfmpegTime(start),
            "-i",
            inputPath,
            "-t",
            ToFfmpegTime(trimDuration)
        };

        if (mutedAudioTracks.Length > 0)
        {
            args.Add("-filter_complex");
            args.Add(string.Join(";", mutedAudioTracks.Select(track =>
                $"[0:a:{track.DisplayIndex - 1}]volume=0[{GetMutedAudioLabel(track)}]")));
        }

        args.AddRange(new[]
        {
            "-map",
            "0"
        });

        foreach (var audioTrack in excludedAudioTracks)
        {
            args.Add("-map");
            args.Add($"-0:a:{audioTrack.DisplayIndex - 1}");
        }

        foreach (var audioTrack in mutedAudioTracks)
        {
            args.Add("-map");
            args.Add($"-0:a:{audioTrack.DisplayIndex - 1}");
        }

        foreach (var audioTrack in mutedAudioTracks)
        {
            args.Add("-map");
            args.Add($"[{GetMutedAudioLabel(audioTrack)}]");
        }

        args.AddRange(new[]
        {
            "-c",
            "copy"
        });

        var copiedAudioTrackCount = audioTrackOptions
            .Count(track => !track.ExcludeFromOutput && !track.MuteInOutput);
        for (var index = 0; index < mutedAudioTracks.Length; index++)
        {
            args.Add($"-c:a:{copiedAudioTrackCount + index}");
            args.Add("aac");
        }

        args.AddRange(new[]
        {
            "-avoid_negative_ts",
            "make_zero",
            outputPath
        });

        return args;
    }

    private static string GetMutedAudioLabel(AudioTrackOption audioTrack)
    {
        return $"muted_audio_{audioTrack.DisplayIndex}";
    }

    private async Task<ProcessResult> RunFfmpegCutAsync(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan totalDuration,
        Stopwatch stopwatch,
        string? progressTitle)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"{fileName} 실행에 실패했습니다.");

        var errorTask = process.StandardError.ReadToEndAsync();
        var progressTask = ReadProgressAsync(process, totalDuration, stopwatch, progressTitle);

        await process.WaitForExitAsync();
        await progressTask;
        var error = await errorTask;

        return new ProcessResult(process.ExitCode, "", error.Trim());
    }

    private async Task ReadProgressAsync(Process process, TimeSpan totalDuration, Stopwatch stopwatch, string? progressTitle)
    {
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            if (TryParseProgressSeconds(line, out var seconds))
            {
                ReportCutProgress(seconds, totalDuration, stopwatch, progressTitle);
            }
            else if (line.Equals("progress=end", StringComparison.OrdinalIgnoreCase))
            {
                ReportCutProgress(totalDuration.TotalSeconds, totalDuration, stopwatch, progressTitle);
            }
        }
    }

    private static bool TryParseProgressSeconds(string line, out double seconds)
    {
        seconds = 0;
        var equalsIndex = line.IndexOf('=');
        if (equalsIndex <= 0 || equalsIndex == line.Length - 1)
        {
            return false;
        }

        var key = line[..equalsIndex];
        var value = line[(equalsIndex + 1)..];

        if (key is "out_time_ms" or "out_time_us"
            && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
        {
            seconds = microseconds / 1_000_000d;
            return true;
        }

        if (key == "out_time"
            && TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var time))
        {
            seconds = time.TotalSeconds;
            return true;
        }

        return false;
    }

    private void SetOutputPath(string path)
    {
        _outputPath = path;
        OutputPathTextBlock.Text = path;
        OutputPathTextBlock.ToolTip = path;
    }

    private static string BuildDefaultOutputPath(string inputPath)
    {
        var directory = Path.GetDirectoryName(inputPath) ?? "";
        var name = Path.GetFileNameWithoutExtension(inputPath);
        var extension = Path.GetExtension(inputPath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".mp4";
        }

        return GetAvailableOutputPath(Path.Combine(directory, $"{name}_cut{extension}"));
    }

    private static string BuildPartOutputPath(string outputPath, int partNumber)
    {
        var directory = Path.GetDirectoryName(outputPath) ?? "";
        var name = Path.GetFileNameWithoutExtension(outputPath);
        var extension = Path.GetExtension(outputPath);

        return Path.Combine(directory, $"{name}_part{partNumber}{extension}");
    }

    private static string GetAvailableOutputPath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? "";
        var extension = Path.GetExtension(path);
        var name = Path.GetFileNameWithoutExtension(path);
        var (baseName, startIndex) = SplitNumberSuffix(name);

        for (var index = Math.Max(1, startIndex + 1); ; index++)
        {
            var candidate = Path.Combine(directory, $"{baseName} ({index}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static (string BaseName, int Index) SplitNumberSuffix(string name)
    {
        if (!name.EndsWith(')'))
        {
            return (name, 0);
        }

        var openIndex = name.LastIndexOf(" (", StringComparison.Ordinal);
        if (openIndex < 0)
        {
            return (name, 0);
        }

        var numberText = name[(openIndex + 2)..^1];
        return int.TryParse(numberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            ? (name[..openIndex], index)
            : (name, 0);
    }

    private static string ResolveToolPath(string exeName)
    {
        var localPath = Path.Combine(AppContext.BaseDirectory, "tools", exeName);
        return File.Exists(localPath) ? localPath : exeName;
    }

    private static async Task<ProcessResult> RunProcessAsync(string fileName, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"{fileName} 실행에 실패했습니다.");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;

        return new ProcessResult(process.ExitCode, output.Trim(), error.Trim());
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

    private static string ToFfmpegTime(TimeSpan time)
    {
        return time.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string Shorten(string text)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= 180 ? text : text[..180] + "...";
    }

    private sealed class AdditionalAudioPlayer
    {
        public AdditionalAudioPlayer(int trackId, int displayIndex, string name, LibVLC libVlc, VlcMediaPlayer player, VlcMedia media)
        {
            TrackId = trackId;
            SelectedTrackId = trackId;
            DisplayIndex = displayIndex;
            Name = name;
            LibVlc = libVlc;
            Player = player;
            Media = media;
        }

        public int TrackId { get; }

        public int SelectedTrackId { get; set; }

        public bool TrackSelectionSucceeded { get; set; }

        public int DisplayIndex { get; }

        public string Name { get; }

        public LibVLC LibVlc { get; }

        public VlcMediaPlayer Player { get; }

        public VlcMedia Media { get; }

        public bool IsEnabled { get; set; } = true;
    }

    private sealed class AudioTrackOption
    {
        public AudioTrackOption(int trackId, int displayIndex, string name)
        {
            TrackId = trackId;
            DisplayIndex = displayIndex;
            Name = name;
        }

        public int TrackId { get; }

        public int DisplayIndex { get; }

        public string Name { get; }

        public bool ExcludeFromOutput { get; set; }

        public bool MuteInOutput { get; set; }
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);

    private sealed record CutJob(TimeSpan Start, TimeSpan Duration, string OutputPath);
}
