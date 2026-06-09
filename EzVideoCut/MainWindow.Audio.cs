using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace EzVideoCut;

public partial class MainWindow
{

    private async Task StartAdditionalAudioTracksAsync()
    {
        await RefreshAudioTrackOptionsFromCurrentMediaAsync(preserveChoices: false);
    }

    private async Task RefreshAudioTrackOptionsFromCurrentMediaAsync(bool preserveChoices, Func<bool>? shouldContinue = null)
    {
        if (_inputPath is null && CurrentCutMode != CutMode.Concat)
        {
            return;
        }

        var previousOptions = preserveChoices
            ? _audioTrackOptions.ToDictionary(audio => audio.DisplayIndex)
            : new Dictionary<int, AudioTrackOption>();
        var previousSelectedIndex = preserveChoices ? _selectedAudioTrackDisplayIndex : null;

        var tracks = Array.Empty<LibVLCSharp.Shared.Structures.TrackDescription>();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (shouldContinue?.Invoke() == false)
            {
                return;
            }

            tracks = _mediaPlayer.AudioTrackDescription?
                .Where(track => track.Id >= 0)
                .ToArray() ?? Array.Empty<LibVLCSharp.Shared.Structures.TrackDescription>();

            if (tracks.Length > 0)
            {
                break;
            }

            await Task.Delay(250);
        }

        if (shouldContinue?.Invoke() == false)
        {
            return;
        }

        var audioCodecNames = Array.Empty<string>();
        var audioCodecSourcePath = CurrentCutMode == CutMode.Concat
            && _currentConcatClipIndex >= 0
            && _currentConcatClipIndex < _concatClips.Count
            ? _concatClips[_currentConcatClipIndex].Path
            : _inputPath;
        if (audioCodecSourcePath is not null)
        {
            try
            {
                audioCodecNames = await ProbeAudioCodecNamesAsync(audioCodecSourcePath);
            }
            catch
            {
                audioCodecNames = Array.Empty<string>();
            }
        }

        if (shouldContinue?.Invoke() == false)
        {
            return;
        }

        _audioTrackOptions.Clear();
        if (tracks.Length == 0)
        {
            _selectedAudioTrackDisplayIndex = null;
            ApplyPrimaryAudioVolumeOnly();
            ShowAudioTrackPlaceholder("오디오 트랙이 없습니다.");
            return;
        }

        for (var index = 0; index < tracks.Length; index++)
        {
            var track = tracks[index];
            var displayName = string.IsNullOrWhiteSpace(track.Name) ? $"Track {index + 1}" : track.Name;
            var codecName = index < audioCodecNames.Length ? audioCodecNames[index] : null;
            var option = new AudioTrackOption(track.Id, index + 1, displayName, codecName);
            if (previousOptions.TryGetValue(option.DisplayIndex, out var previous))
            {
                option.ExcludeFromOutput = previous.ExcludeFromOutput;
            }

            _audioTrackOptions.Add(option);
        }

        _selectedAudioTrackDisplayIndex = previousSelectedIndex.HasValue
            && _audioTrackOptions.Any(audio => audio.DisplayIndex == previousSelectedIndex.Value)
            ? previousSelectedIndex
            : _audioTrackOptions.FirstOrDefault()?.DisplayIndex;
        ApplySelectedAudioTrack();
        PopulateAudioTrackControls();
        if (CurrentCutMode == CutMode.AudioExtract)
        {
            RefreshDefaultOutputPathForCurrentMode();
        }
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
        AudioTrackHost.Visibility = Visibility.Visible;
        if (_audioTrackOptions.Count == 0)
        {
            ShowAudioTrackPlaceholder("비디오를 선택하면 표시됩니다.");
            return;
        }

        AudioTrackHelpText.Text = CurrentCutMode == CutMode.AudioExtract
            ? "선택한 트랙을 원본 그대로 추출"
            : "미리듣기/출력에 적용";

        foreach (var audio in _audioTrackOptions)
        {
            AudioTracksPanel.Children.Add(CreateAudioTrackOptionControl(audio));
        }

        UpdateAudioMixOptionState();
        UpdateAudioTrackButtonStates();
    }

    private void ShowAudioTrackPlaceholder(string message)
    {
        AudioTrackHost.Visibility = Visibility.Visible;
        AudioTrackHelpText.Text = string.Empty;
        AudioTracksPanel.Children.Clear();
        UpdateAudioMixOptionState();
        AudioTracksPanel.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
            VerticalAlignment = VerticalAlignment.Center
        });
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
            Text = string.IsNullOrWhiteSpace(audio.CodecName) ? audio.Name : $"{audio.Name} ({audio.CodecName})",
            Margin = new Thickness(8, 0, 0, 0),
            MaxWidth = 180,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = string.IsNullOrWhiteSpace(audio.CodecName) ? audio.Name : $"{audio.Name} ({audio.CodecName})"
        });
        panel.Children.Add(titlePanel);

        if (CurrentCutMode == CutMode.AudioExtract)
        {
            return box;
        }

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
            UpdateAudioMixOptionState();
            ValidateTrimRange();
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
        if (CurrentCutMode == CutMode.AudioExtract)
        {
            RefreshDefaultOutputPathForCurrentMode();
        }

        ReportSelectedAudioTrack();
        ValidateTrimRange();
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
            if (isSelected)
            {
                button.Style = (Style)FindResource("SelectedAudioTrackButtonStyle");
            }
            else
            {
                button.ClearValue(StyleProperty);
                button.FontWeight = FontWeights.Normal;
                button.ClearValue(Control.BackgroundProperty);
                button.ClearValue(Control.ForegroundProperty);
                button.ClearValue(Control.BorderBrushProperty);
            }
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
        StatusText.Text = CurrentCutMode == CutMode.AudioExtract
            ? $"오디오 트랙 {_selectedAudioTrackDisplayIndex} 추출 대상으로 선택"
            : $"오디오 트랙 {_selectedAudioTrackDisplayIndex} 재생";
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
        ShowAudioTrackPlaceholder("비디오를 선택하면 표시됩니다.");
    }

    private void ClearAudioTrackOptions()
    {
        _audioTrackOptions.Clear();
        _selectedAudioTrackDisplayIndex = null;
        ShowAudioTrackPlaceholder("비디오를 선택하면 표시됩니다.");
    }

}
