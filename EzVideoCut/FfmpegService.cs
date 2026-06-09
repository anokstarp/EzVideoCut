using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace EzVideoCut;

internal static class FfmpegService
{
    public static List<string> BuildConcatArguments(
        string concatListPath,
        string outputPath,
        IEnumerable<AudioTrackOption> audioTracks,
        bool mixAudioTracks,
        bool disableAudioLimiter)
    {
        var audioTrackOptions = audioTracks.ToArray();
        var args = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-progress",
            "pipe:1",
            "-nostats",
            "-y",
            "-f",
            "concat",
            "-safe",
            "0",
            "-i",
            concatListPath
        };

        AddAudioMixFilterArguments(args, audioTrackOptions, mixAudioTracks, disableAudioLimiter);
        AddOutputStreamMapArguments(args, audioTrackOptions, mixAudioTracks);

        args.AddRange(new[]
        {
            "-c",
            "copy"
        });

        AddAudioCodecArguments(args, audioTrackOptions, mixAudioTracks);

        args.AddRange(new[]
        {
            "-avoid_negative_ts",
            "make_zero",
            outputPath
        });

        return args;
    }

    public static List<string> BuildCutArguments(
        string inputPath,
        TimeSpan start,
        TimeSpan trimDuration,
        string outputPath,
        IEnumerable<AudioTrackOption> audioTracks,
        bool mixAudioTracks,
        bool disableAudioLimiter)
    {
        var audioTrackOptions = audioTracks.ToArray();
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

        AddAudioMixFilterArguments(args, audioTrackOptions, mixAudioTracks, disableAudioLimiter);
        AddOutputStreamMapArguments(args, audioTrackOptions, mixAudioTracks);

        args.AddRange(new[]
        {
            "-c",
            "copy"
        });

        AddAudioCodecArguments(args, audioTrackOptions, mixAudioTracks);

        args.AddRange(new[]
        {
            "-avoid_negative_ts",
            "make_zero",
            outputPath
        });

        return args;
    }

    public static List<string> BuildAudioExtractArguments(
        string inputPath,
        int audioDisplayIndex,
        string outputPath)
    {
        return new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-progress",
            "pipe:1",
            "-nostats",
            "-y",
            "-i",
            inputPath,
            "-map",
            $"0:a:{audioDisplayIndex - 1}",
            "-vn",
            "-sn",
            "-dn",
            "-c:a",
            "copy",
            "-avoid_negative_ts",
            "make_zero",
            outputPath
        };
    }

    public static IEnumerable<string> BuildConcatListLines(IEnumerable<ConcatClip> clips)
    {
        yield return "ffconcat version 1.0";

        foreach (var clip in clips)
        {
            yield return $"file '{EscapeConcatFilePath(clip.Path)}'";
            if (clip.Duration > TimeSpan.Zero)
            {
                yield return $"duration {ToFfmpegTime(clip.Duration)}";
            }
        }
    }

    public static async Task<ProcessResult> RunFfmpegAsync(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan totalDuration,
        Stopwatch stopwatch,
        string? progressTitle,
        Action<double, TimeSpan, Stopwatch, string?> reportProgress)
    {
        var startInfo = BuildStartInfo(fileName, arguments);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"{fileName} 실행에 실패했습니다.");

        var errorTask = process.StandardError.ReadToEndAsync();
        var progressTask = ReadProgressAsync(process, totalDuration, stopwatch, progressTitle, reportProgress);

        await process.WaitForExitAsync();
        await progressTask;
        var error = await errorTask;

        return new ProcessResult(process.ExitCode, "", error.Trim());
    }

    public static async Task<ProcessResult> RunProcessAsync(string fileName, IEnumerable<string> arguments)
    {
        var startInfo = BuildStartInfo(fileName, arguments);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"{fileName} 실행에 실패했습니다.");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;

        return new ProcessResult(process.ExitCode, output.Trim(), error.Trim());
    }

    public static string ResolveToolPath(string exeName)
    {
        var localPath = Path.Combine(AppContext.BaseDirectory, "tools", exeName);
        return File.Exists(localPath) ? localPath : exeName;
    }

    private static ProcessStartInfo BuildStartInfo(string fileName, IEnumerable<string> arguments)
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

        return startInfo;
    }

    private static async Task ReadProgressAsync(
        Process process,
        TimeSpan totalDuration,
        Stopwatch stopwatch,
        string? progressTitle,
        Action<double, TimeSpan, Stopwatch, string?> reportProgress)
    {
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            if (TryParseProgressSeconds(line, out var seconds))
            {
                reportProgress(seconds, totalDuration, stopwatch, progressTitle);
            }
            else if (line.Equals("progress=end", StringComparison.OrdinalIgnoreCase))
            {
                reportProgress(totalDuration.TotalSeconds, totalDuration, stopwatch, progressTitle);
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

    private static void AddAudioMixFilterArguments(List<string> args, AudioTrackOption[] audioTrackOptions, bool mixAudioTracks, bool disableAudioLimiter)
    {
        var includedAudioTracks = GetIncludedAudioTracks(audioTrackOptions);
        if (!mixAudioTracks || includedAudioTracks.Length <= 1)
        {
            return;
        }

        var mixFilter = $"amix=inputs={includedAudioTracks.Length}:duration=longest:normalize=0";
        if (!disableAudioLimiter)
        {
            mixFilter += ",alimiter=limit=0.98";
        }

        args.Add("-filter_complex");
        args.Add(
            $"{string.Concat(includedAudioTracks.Select(track => $"[0:a:{track.DisplayIndex - 1}]"))}" +
            $"{mixFilter}[{GetMixedAudioLabel()}]");
    }

    private static void AddOutputStreamMapArguments(List<string> args, AudioTrackOption[] audioTrackOptions, bool mixAudioTracks)
    {
        var includedAudioTracks = GetIncludedAudioTracks(audioTrackOptions);
        var shouldMixAudio = mixAudioTracks && includedAudioTracks.Length > 1;
        args.Add("-map");
        args.Add("0:v?");
        if (shouldMixAudio)
        {
            args.Add("-map");
            args.Add($"[{GetMixedAudioLabel()}]");
            return;
        }

        args.Add("-map");
        args.Add("0:a?");
        foreach (var audioTrack in audioTrackOptions.Where(track => track.ExcludeFromOutput))
        {
            args.Add("-map");
            args.Add($"-0:a:{audioTrack.DisplayIndex - 1}");
        }
    }

    private static void AddAudioCodecArguments(List<string> args, AudioTrackOption[] audioTrackOptions, bool mixAudioTracks)
    {
        if (!mixAudioTracks || GetIncludedAudioTracks(audioTrackOptions).Length <= 1)
        {
            return;
        }

        args.Add("-c:a:0");
        args.Add("aac");
        args.Add("-b:a:0");
        args.Add("384k");
        args.Add("-ar:a:0");
        args.Add("48000");
        args.Add("-ac:a:0");
        args.Add("2");
    }

    private static AudioTrackOption[] GetIncludedAudioTracks(IEnumerable<AudioTrackOption> audioTrackOptions)
    {
        return audioTrackOptions
            .Where(track => !track.ExcludeFromOutput)
            .ToArray();
    }

    private static string EscapeConcatFilePath(string path)
    {
        return path.Replace("\\", "/").Replace("'", "'\\''");
    }

    private static string GetMixedAudioLabel()
    {
        return "mixed_audio";
    }

    private static string ToFfmpegTime(TimeSpan time)
    {
        return time.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
