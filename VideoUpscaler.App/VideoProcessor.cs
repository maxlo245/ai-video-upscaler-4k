using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
using VideoUpscaler.Core;

namespace VideoUpscaler.App;

internal sealed class VideoProcessor(ExternalTools tools)
{
    public async Task RunAsync(
        string inputPath,
        string outputPath,
        int scale,
        bool anime,
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
        var plan = VideoPlan.Create(source.Width, source.Height, scale, VideoPlan.ParseFrameRate(source.FrameRate));
        var lastOutputTick = Stopwatch.GetTimestamp();
        Action<string> reportToolOutput = line =>
        {
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) ||
                line.StartsWith("progress=", StringComparison.Ordinal))
                return;

            var now = Stopwatch.GetTimestamp();
            var important = line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("vulkan", StringComparison.OrdinalIgnoreCase);
            if (important || now - lastOutputTick >= Stopwatch.Frequency * 2)
            {
                Interlocked.Exchange(ref lastOutputTick, now);
                report(line);
            }
        };

        report($"Vidéo : {source.Width} × {source.Height}, {plan.FramesPerSecond:0.##} FPS");
        report($"Sortie prévue : {plan.OutputWidth} × {plan.OutputHeight} ({scale}× IA, plafonné à l'UHD 4K)");
        report("L'extraction et l'inférence IA sont locales. Les vidéos ne sont envoyées à aucun service.");

        var jobDirectory = Path.Combine(Path.GetTempPath(), "UpscaleStudio4K", Guid.NewGuid().ToString("N"));
        var framesDirectory = Path.Combine(jobDirectory, "frames");
        var upscaledDirectory = Path.Combine(jobDirectory, "upscaled");
        Directory.CreateDirectory(framesDirectory);
        Directory.CreateDirectory(upscaledDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        try
        {
            progressChanged?.Invoke(new VideoProgress("Extraction des images", 0, 0, jobStopwatch.Elapsed, null));
            report("1/3 — Extraction des images…");
            await ProcessRunner.RunAsync(ffmpeg,
                [
                    "-hide_banner", "-loglevel", "error", "-i", inputPath,
                    "-map", "0:v:0", "-fps_mode", "passthrough",
                    Path.Combine(framesDirectory, "frame_%08d.png")
                ], jobDirectory, reportToolOutput, cancellationToken);

            if (!Directory.EnumerateFiles(framesDirectory, "frame_*.png").Any())
                throw new InvalidDataException("FFmpeg n'a extrait aucune image de cette vidéo.");

            report(anime
                ? "2/3 — Upscaling vidéo par le modèle Real-ESRGAN x4plus Anime 6B…"
                : "2/3 — Upscaling vidéo par le modèle Real-ESRGAN x4plus…");
            var frameFiles = Directory.GetFiles(framesDirectory, "frame_*.png")
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (frameFiles.Length == 0)
                throw new InvalidDataException("FFmpeg n'a extrait aucune image de cette vidéo.");

            await Task.Run(() =>
            {
                using var upscaler = new OnnxUpscaler(
                    anime ? tools.AnimeModelPath : tools.GeneralModelPath, report);
                var inferenceStopwatch = Stopwatch.StartNew();
                for (var index = 0; index < frameFiles.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var outputFrame = Path.Combine(upscaledDirectory, Path.GetFileName(frameFiles[index]));
                    upscaler.ProcessFrame(
                        frameFiles[index],
                        outputFrame,
                        plan.OutputWidth,
                        plan.OutputHeight,
                        cancellationToken);

                    var completedFrames = index + 1;
                    var remainingFrames = frameFiles.Length - completedFrames;
                    var remaining = TimeSpan.FromSeconds(
                        inferenceStopwatch.Elapsed.TotalSeconds * remainingFrames / completedFrames);
                    progressChanged?.Invoke(new VideoProgress(
                        "Upscaling des images", completedFrames, frameFiles.Length, jobStopwatch.Elapsed, remaining));
                    if (completedFrames % 5 == 0 || completedFrames == frameFiles.Length)
                    {
                        report(
                            $"Inférence IA : image {completedFrames}/{frameFiles.Length} | " +
                            $"écoulé {FormatDuration(jobStopwatch.Elapsed)} | " +
                            $"reste estimé {FormatDuration(remaining)} (images)");
                    }
                }
            }, cancellationToken);

            if (!Directory.EnumerateFiles(upscaledDirectory, "frame_*.png").Any())
                throw new InvalidDataException("Le modèle IA n'a produit aucune image.");

            progressChanged?.Invoke(new VideoProgress("Assemblage de la vidéo et de l'audio", 0, frameFiles.Length, jobStopwatch.Elapsed, null));
            report("3/3 — Assemblage de la vidéo et de l'audio…");
            var fps = plan.FramesPerSecond.ToString("0.########", CultureInfo.InvariantCulture);
            var mediaDuration = TimeSpan.FromSeconds(frameFiles.Length / plan.FramesPerSecond);
            var assemblyStopwatch = Stopwatch.StartNew();
            await ProcessRunner.RunAsync(ffmpeg,
                [
                    "-hide_banner", "-loglevel", "error", "-y", "-progress", "pipe:1", "-nostats",
                    "-stats_period", "1",
                    "-framerate", fps, "-i", Path.Combine(upscaledDirectory, "frame_%08d.png"),
                    "-i", inputPath, "-map", "0:v:0", "-map", "1:a?",
                    "-r", fps, "-fps_mode", "cfr",
                    "-c:v", "libx264", "-preset", "fast", "-crf", "16",
                    "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k",
                    "-map_metadata", "1", "-movflags", "+faststart",
                    "-shortest", outputPath
                ], jobDirectory, reportToolOutput, cancellationToken, mediaProgress: processedMediaTime =>
                {
                    var processedTicks = Math.Clamp(processedMediaTime.Ticks, 0, mediaDuration.Ticks);
                    var processedSeconds = TimeSpan.FromTicks(processedTicks).TotalSeconds;
                    TimeSpan? remaining = processedTicks > 0
                        ? TimeSpan.FromSeconds(
                            assemblyStopwatch.Elapsed.TotalSeconds *
                            (mediaDuration.Ticks - processedTicks) / processedTicks)
                        : null;
                    var completedFrames = Math.Clamp(
                        (int)Math.Round(processedSeconds * plan.FramesPerSecond),
                        0,
                        frameFiles.Length);
                    progressChanged?.Invoke(new VideoProgress(
                        "Assemblage de la vidéo et de l'audio",
                        completedFrames,
                        frameFiles.Length,
                        jobStopwatch.Elapsed,
                        remaining));
                });

            progressChanged?.Invoke(new VideoProgress("Terminé", frameFiles.Length, frameFiles.Length, jobStopwatch.Elapsed, TimeSpan.Zero));
            report($"Terminé : {outputPath}");
        }
        finally
        {
            try
            {
                Directory.Delete(jobDirectory, recursive: true);
            }
            catch (IOException exception)
            {
                report($"Nettoyage temporaire différé : {exception.Message}");
            }
            catch (UnauthorizedAccessException exception)
            {
                report($"Nettoyage temporaire différé : {exception.Message}");
            }
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        var hours = (int)duration.TotalHours;
        return hours > 0
            ? $"{hours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes:00}:{duration.Seconds:00}";
    }

    internal sealed record VideoProgress(
        string Stage,
        int CompletedFrames,
        int TotalFrames,
        TimeSpan Elapsed,
        TimeSpan? Remaining);

    private static async Task<VideoStreamInfo> ProbeAsync(
        string ffprobe, string inputPath, CancellationToken cancellationToken)
    {
        var json = await ProcessRunner.RunAsync(ffprobe,
            [
                "-v", "error", "-select_streams", "v:0",
                "-show_entries", "stream=width,height,avg_frame_rate",
                "-of", "json", inputPath
            ], null, null, cancellationToken);

        using var document = JsonDocument.Parse(json);
        var streams = document.RootElement.GetProperty("streams");
        if (streams.GetArrayLength() == 0)
            throw new InvalidDataException("Aucun flux vidéo n'a été trouvé dans le fichier.");

        var stream = streams[0];
        return new VideoStreamInfo(
            stream.GetProperty("width").GetInt32(),
            stream.GetProperty("height").GetInt32(),
            stream.TryGetProperty("avg_frame_rate", out var rate) ? rate.GetString() : null);
    }

    private sealed record VideoStreamInfo(int Width, int Height, string? FrameRate);
}
