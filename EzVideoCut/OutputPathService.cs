using System.Globalization;
using System.IO;

namespace EzVideoCut;

internal static class OutputPathService
{
    public static string BuildDefaultCutOutputPath(string inputPath, string suffix = "_cut")
    {
        var directory = Path.GetDirectoryName(inputPath) ?? "";
        var name = Path.GetFileNameWithoutExtension(inputPath);
        var extension = GetVideoExtension(inputPath);

        return GetAvailableOutputPath(Path.Combine(directory, $"{name}{suffix}{extension}"));
    }

    public static string BuildDefaultSplitOutputPath(string inputPath)
    {
        var directory = Path.GetDirectoryName(inputPath) ?? "";
        var name = Path.GetFileNameWithoutExtension(inputPath);
        var extension = GetVideoExtension(inputPath);

        return GetAvailableSplitBaseOutputPath(Path.Combine(directory, $"{name}_split{extension}"));
    }

    public static string BuildDefaultConcatOutputPath(string firstInputPath)
    {
        var directory = Path.GetDirectoryName(firstInputPath) ?? "";
        var name = Path.GetFileNameWithoutExtension(firstInputPath);
        var extension = GetVideoExtension(firstInputPath);

        return GetAvailableOutputPath(Path.Combine(directory, $"{name}_joined{extension}"));
    }

    public static string BuildDefaultAudioExtractOutputPath(string inputPath, AudioTrackOption? selectedAudio)
    {
        var directory = Path.GetDirectoryName(inputPath) ?? "";
        var name = Path.GetFileNameWithoutExtension(inputPath);
        var displayIndex = selectedAudio?.DisplayIndex ?? 1;
        var extension = GetAudioExtractExtension(selectedAudio?.CodecName);

        return GetAvailableOutputPath(Path.Combine(directory, $"{name}_audio{displayIndex}{extension}"));
    }

    public static string BuildPartOutputPath(string outputPath, int partNumber)
    {
        var directory = Path.GetDirectoryName(outputPath) ?? "";
        var name = Path.GetFileNameWithoutExtension(outputPath);
        var extension = Path.GetExtension(outputPath);

        return Path.Combine(directory, $"{name}_part{partNumber}{extension}");
    }

    public static string GetAvailableOutputPath(string path)
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

    private static string GetAvailableSplitBaseOutputPath(string path)
    {
        if (!SplitPartOutputExists(path))
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
            if (!SplitPartOutputExists(candidate))
            {
                return candidate;
            }
        }
    }

    private static bool SplitPartOutputExists(string outputPath)
    {
        return File.Exists(BuildPartOutputPath(outputPath, 1))
            || File.Exists(BuildPartOutputPath(outputPath, 2));
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

    private static string GetVideoExtension(string inputPath)
    {
        var extension = Path.GetExtension(inputPath);
        return string.IsNullOrWhiteSpace(extension) ? ".mp4" : extension;
    }

    private static string GetAudioExtractExtension(string? codecName)
    {
        return codecName?.ToLowerInvariant() switch
        {
            "aac" or "alac" => ".m4a",
            "mp3" => ".mp3",
            "flac" => ".flac",
            "opus" => ".opus",
            "vorbis" => ".ogg",
            "pcm_s16le" or "pcm_s24le" or "pcm_s32le" or "pcm_f32le" or "pcm_f64le" => ".wav",
            "ac3" => ".ac3",
            "eac3" => ".eac3",
            "dts" => ".dts",
            "truehd" => ".thd",
            _ => ".mka"
        };
    }
}
