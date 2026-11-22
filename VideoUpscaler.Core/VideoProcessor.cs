using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using VideoUpscaler.Core.Upscaling;

namespace VideoUpscaler.Core;

/// <summary>Modèle choisi pour un traitement, avec son libellé et son type de contenu.</summary>
public sealed record UpscaleModel(string Path, string Label, bool Anime);

/// <summary>
/// Pipeline vidéo en flux continu : FFmpeg décode les images brutes, le moteur les agrandit,
/// puis un second FFmpeg encode le MP4 pendant le traitement. Aucun fichier temporaire,
/// aucune génération PNG intermédiaire.
/// </summary>
public sealed class VideoProcessor(IToolPaths tools)
{
    private static readonly HashSet<string> Mp4AudioCodecs =
        new(StringComparer.OrdinalIgnoreCase) { "aac", "mp3", "ac3", "eac3", "alac" };

    public async Task RunAsync(
        string inputPath,
        string outputPath,
        int scale,
        UpscaleModel model,
        SourceCleanup cleanup,
        UpscaleOptions options,
        Action<string> report,
        CancellationToken cancellationToken,
        Action<VideoProgress>? progressChanged = null)
    {
        var jobStopwatch = Stopwatch.StartNew();
        var ffmpeg = tools.CurrentFfmpegPath
                     ?? throw new InvalidOperationException("FFmpeg est introuvable.");
        var ffprobe = tools.CurrentFfprobePath
                      ?? throw new InvalidOperationException("ffprobe est introuvable.");

        var source = await ProbeAsync(ffprobe, inputPath, cancellationToken);
        var plan = VideoPlan.Create(source.Width, source.Height, scale, source.FramesPerSecond);
        var frameBytes = checked(source.Width * source.Height * 3);
        var outputBytes = checked(plan.OutputWidth * plan.OutputHeight * 3);
        var totalFrames = source.FrameCount > 0
            ? source.FrameCount
            : (int)Math.Round(source.DurationSeconds * plan.FramesPerSecond);

        report($"Vidéo : {source.Width} × {source.Height}, {plan.FramesPerSecond:0.##} FPS");
        report($"Sortie prévue : {plan.OutputWidth} × {plan.OutputHeight} ({scale}× IA, plafonné à l'UHD 4K)");
        if (source.IsHdr)
            report("Attention : source HDR détectée, la sortie est convertie en SDR 8 bits.");
        if (source.Rotation % 180 != 0)
            report($"Rotation source détectée ({source.Rotation}°) : les images sont remises d'aplomb.");
        report(
            $"Réglages : tuiles {options.TileSize} px, fondu {options.TileFeather} px, " +
            $"fidélité {options.Fidelity:P0}, stabilité {options.TemporalStability:P0}, nettoyage {cleanup}.");
        report("Traitement 100 % local : aucune vidéo n'est envoyée à un service.");

        var lastOutputTick = Stopwatch.GetTimestamp();
        Action<string> reportToolOutput = line =>
        {
            var now = Stopwatch.GetTimestamp();
            var important = line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("invalid", StringComparison.OrdinalIgnoreCase);
            if (important || now - lastOutputTick >= Stopwatch.Frequency * 2)
            {
                Interlocked.Exchange(ref lastOutputTick, now);
                report(line);
            }
        };

        report($"Agrandissement par {model.Label}…");
        progressChanged?.Invoke(new VideoProgress("Upscaling des images", 0, totalFrames, jobStopwatch.Elapsed, null));

        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        Directory.CreateDirectory(outputDirectory);

        try
        {
            await Task.Run(async () =>
            {
                using var upscaler = new OnnxFrameUpscaler(model.Path, preferGpu: true, report);
                using var engine = new UpscaleEngine(upscaler, options, report);
                using var decoder = FfmpegProcess.StartDecoder(
                    ffmpeg, BuildDecoderArguments(inputPath, cleanup), null, reportToolOutput);
                using var encoder = FfmpegProcess.StartEncoder(
                    ffmpeg, BuildEncoderArguments(inputPath, outputPath, plan, source, model.Anime), null, reportToolOutput);
                using var registration = cancellationToken.Register(() =>
                {
                    decoder.Kill();
                    encoder.Kill();
                });

                var frameBuffer = new byte[frameBytes];
                var inferenceStopwatch = Stopwatch.StartNew();
                var frameIndex = 0;

                try
                {
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var read = await ReadFrameAsync(decoder.StandardOutput, frameBuffer, cancellationToken);
                        if (read == 0)
                            break;
                        if (read != frameBytes)
                            throw new InvalidDataException("FFmpeg a produit une image de taille inattendue.");

                        var result = engine.ProcessImage(
                            new RgbImage(source.Width, source.Height, frameBuffer),
                            plan.OutputWidth,
                            plan.OutputHeight,
                            cancellationToken);
                        await encoder.StandardInput.WriteAsync(
                            result.Pixels.AsMemory(0, outputBytes), cancellationToken);

                        frameIndex++;
                        var remainingFrames = totalFrames > 0 ? Math.Max(0, totalFrames - frameIndex) : 0;
                        TimeSpan? remaining = totalFrames > 0 && frameIndex > 0
                            ? TimeSpan.FromSeconds(
                                inferenceStopwatch.Elapsed.TotalSeconds * remainingFrames / frameIndex)
                            : null;
                        progressChanged?.Invoke(new VideoProgress(
                            "Upscaling des images", frameIndex, totalFrames, jobStopwatch.Elapsed, remaining));

                        if (frameIndex % 5 == 0 || (totalFrames > 0 && frameIndex == totalFrames))
                        {
                            report(
                                $"Inférence IA : image {frameIndex}" +
                                (totalFrames > 0 ? $"/{totalFrames}" : string.Empty) +
                                $" | écoulé {FormatDuration(jobStopwatch.Elapsed)}" +
                                (remaining is { } value ? $" | reste estimé {FormatDuration(value)}" : string.Empty));
                        }
                    }

                    if (frameIndex == 0)
                        throw new InvalidDataException("Aucune image n'a pu être lue dans cette vidéo.");

                    encoder.StandardInput.Close();
                    await decoder.EnsureSuccessAsync(cancellationToken);
                    await encoder.EnsureSuccessAsync(cancellationToken);
                }
                catch (IOException) when (encoder.HasExited)
                {
                    // L'encodeur a fermé le tube : remonter son message d'erreur réel.
                    await encoder.EnsureSuccessAsync(CancellationToken.None);
                    throw;
                }
            }, cancellationToken);

            progressChanged?.Invoke(new VideoProgress(
                "Terminé", totalFrames, totalFrames, jobStopwatch.Elapsed, TimeSpan.Zero));
            report($"Terminé : {outputPath}");
        }
        catch
        {
            TryDelete(outputPath);
            throw;
        }
    }

    private static async Task<int> ReadFrameAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
                break;
            total += read;
        }

        return total;
    }

    private static IEnumerable<string> BuildDecoderArguments(string inputPath, SourceCleanup cleanup)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-i", inputPath,
            "-map", "0:v:0", "-an", "-fps_mode", "passthrough"
        };

        var denoise = cleanup switch
        {
            SourceCleanup.Light => "hqdn3d=1.5:1.5:5:5",
            SourceCleanup.Strong => "hqdn3d=3.0:3.0:9:9",
            _ => null
        };
        if (denoise is not null)
        {
            arguments.Add("-vf");
            arguments.Add(denoise);
        }

        arguments.AddRange(["-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"]);
        return arguments;
    }

    private static IEnumerable<string> BuildEncoderArguments(
        string inputPath,
        string outputPath,
        VideoPlan plan,
        VideoStreamInfo source,
        bool anime)
    {
        var fps = plan.FramesPerSecond.ToString("0.########", CultureInfo.InvariantCulture);
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", "rgb24",
            "-s", $"{plan.OutputWidth}x{plan.OutputHeight}", "-framerate", fps,
            "-i", "pipe:0",
            "-i", inputPath,
            "-map", "0:v:0", "-map", "1:a?", "-map_chapters", "1",
            "-r", fps, "-fps_mode", "cfr",
            "-c:v", "libx264", "-preset", "slow", "-crf", "16",
            "-tune", anime ? "animation" : "film",
            "-pix_fmt", "yuv420p",
            "-sws_flags", "lanczos+accurate_rnd+full_chroma_int",
            "-x264-params", "aq-mode=3",
            "-max_muxing_queue_size", "4096"
        };

        AddColorMetadata(arguments, source);

        if (source.AudioCodecs.Count > 0 && source.AudioCodecs.All(codec => Mp4AudioCodecs.Contains(codec)))
            arguments.AddRange(["-c:a", "copy"]);
        else
            arguments.AddRange(["-c:a", "aac", "-b:a", "256k"]);

        arguments.AddRange([
            "-map_metadata", "1",
            "-metadata:s:v:0", "rotate=0",
            "-movflags", "+faststart",
            "-shortest",
            outputPath
        ]);
        return arguments;
    }

    private static void AddColorMetadata(List<string> arguments, VideoStreamInfo source)
    {
        if (IsKnown(source.ColorPrimaries))
            arguments.AddRange(["-color_primaries", source.ColorPrimaries!]);
        if (IsKnown(source.ColorTransfer))
            arguments.AddRange(["-color_trc", source.ColorTransfer!]);
        if (IsKnown(source.ColorSpace))
            arguments.AddRange(["-colorspace", source.ColorSpace!]);

        var range = source.ColorRange switch
        {
            "limited" or "tv" => "tv",
            "full" or "pc" => "pc",
            _ => null
        };
        if (range is not null)
            arguments.AddRange(["-color_range", range]);
    }

    private static bool IsKnown(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value is not ("unknown" or "reserved" or "unspecified");

    private static string FormatDuration(TimeSpan duration)
    {
        var hours = (int)duration.TotalHours;
        return hours > 0
            ? $"{hours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public sealed record VideoProgress(
        string Stage,
        int CompletedFrames,
        int TotalFrames,
        TimeSpan Elapsed,
        TimeSpan? Remaining);

    private static async Task<VideoStreamInfo> ProbeAsync(
        string ffprobe, string inputPath, CancellationToken cancellationToken)
    {
        var json = await ProcessRunner.RunAsync(ffprobe,
            ["-v", "error", "-show_streams", "-show_format", "-of", "json", inputPath],
            null, null, cancellationToken);

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("streams", out var streams) ||
            streams.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Aucun flux vidéo n'a été trouvé dans le fichier.");
        }

        JsonElement? video = null;
        var audioCodecs = new List<string>();
        foreach (var stream in streams.EnumerateArray())
        {
            var type = GetString(stream, "codec_type");
            if (type == "video" && video is null && !IsAttachedPicture(stream))
                video = stream;
            else if (type == "audio" && GetString(stream, "codec_name") is { Length: > 0 } codec)
                audioCodecs.Add(codec);
        }

        if (video is not { } videoStream)
            throw new InvalidDataException("Aucun flux vidéo n'a été trouvé dans le fichier.");

        var width = videoStream.GetProperty("width").GetInt32();
        var height = videoStream.GetProperty("height").GetInt32();
        var rotation = ReadRotation(videoStream);
        if (Math.Abs(rotation) % 180 == 90)
            (width, height) = (height, width);

        var framesPerSecond = VideoPlan.ParseFrameRate(
            GetString(videoStream, "avg_frame_rate") ?? GetString(videoStream, "r_frame_rate"));
        var frameCount = ParseInt(GetString(videoStream, "nb_frames"));
        var duration = ParseDouble(
            document.RootElement.TryGetProperty("format", out var format)
                ? GetString(format, "duration")
                : null);

        var transfer = GetString(videoStream, "color_transfer");
        var isHdr = transfer is "smpte2084" or "arib-std-b67";

        return new VideoStreamInfo(
            width,
            height,
            framesPerSecond,
            frameCount,
            duration,
            GetString(videoStream, "color_range"),
            GetString(videoStream, "color_space"),
            transfer,
            GetString(videoStream, "color_primaries"),
            rotation,
            audioCodecs,
            isHdr);
    }

    private static int ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : 0;

    private static double ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : 0;

    private static bool IsAttachedPicture(JsonElement stream) =>
        stream.TryGetProperty("disposition", out var disposition) &&
        disposition.TryGetProperty("attached_pic", out var attached) &&
        attached.GetInt32() == 1;

    private static int ReadRotation(JsonElement stream)
    {
        if (stream.TryGetProperty("side_data_list", out var sideData) &&
            sideData.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in sideData.EnumerateArray())
            {
                if (item.TryGetProperty("rotation", out var rotation) &&
                    rotation.ValueKind == JsonValueKind.Number)
                    return (int)Math.Round(rotation.GetDouble());
            }
        }

        if (stream.TryGetProperty("tags", out var tags) &&
            tags.TryGetProperty("rotate", out var tag) &&
            tag.ValueKind == JsonValueKind.String &&
            int.TryParse(tag.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return 0;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record VideoStreamInfo(
        int Width,
        int Height,
        double FramesPerSecond,
        int FrameCount,
        double DurationSeconds,
        string? ColorRange,
        string? ColorSpace,
        string? ColorTransfer,
        string? ColorPrimaries,
        int Rotation,
        IReadOnlyList<string> AudioCodecs,
        bool IsHdr);
}
