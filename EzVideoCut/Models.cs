using LibVLCSharp.Shared;

namespace EzVideoCut;

internal sealed class AdditionalAudioPlayer
{
    public AdditionalAudioPlayer(int trackId, int displayIndex, string name, LibVLC libVlc, MediaPlayer player, Media media)
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

    public MediaPlayer Player { get; }

    public Media Media { get; }

    public bool IsEnabled { get; set; } = true;
}

internal sealed class AudioTrackOption
{
    public AudioTrackOption(int trackId, int displayIndex, string name, string? codecName)
    {
        TrackId = trackId;
        DisplayIndex = displayIndex;
        Name = name;
        CodecName = codecName;
    }

    public int TrackId { get; }

    public int DisplayIndex { get; }

    public string Name { get; }

    public string? CodecName { get; }

    public bool ExcludeFromOutput { get; set; }
}

internal sealed record ProcessResult(int ExitCode, string Output, string Error);

internal sealed record CutJob(TimeSpan Start, TimeSpan Duration, string OutputPath);

internal sealed record ConcatClip(string Path, TimeSpan Duration);
