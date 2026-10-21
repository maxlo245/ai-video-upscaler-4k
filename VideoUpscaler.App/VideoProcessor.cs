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
        CancellationToken cancellationToken)
    {
        var ffmpeg = tools.CurrentFfmpegPath
                     ?? throw new InvalidOperationException("FFmpeg est introuvable.");
        var ffprobe = tools.CurrentFfprobePath
                      ?? throw new InvalidOperationException("ffprobe est introuvable.");
        var source = await ProbeAsync(ffprobe, inputPath, cancellationToken);
        var plan = VideoPlan.Create(source.Width, source.Height, scale, VideoPlan.ParseFrameRate(source.FrameRate));
        var lastOutputTick = Stopwatch.GetTimestamp();
        Action<string> reportToolOutput = line =>
        {
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
            report("1/3 — Extraction des images…");
            await ProcessRunner.RunAsync(ffmpeg,
                [
                    "-hide_banner", "-loglevel", "error", "-i", inputPath,
                    "-map", "0:v:0", "-fps_mode", "passthrough",
                    Path.Combine(framesDirectory, "frame_%08d.png")
                ], jobDirectory, reportToolOutput, cancellationToken);

            if (!Directory.EnumerateFiles(framesDirectory, "frame_*.png").Any())
                throw new InvalidDataException("FFmpeg n'a extrait aucune image de cette vidéo.");

            report("2/3 — Upscaling par le modèle IA Real-ESRGAN…");
            var model = anime ? ExternalTools.AnimeModel : ExternalTools.GeneralModel;
            await ProcessRunner.RunAsync(tools.EnginePath,
                [
                    "-i", framesDirectory, "-o", upscaledDirectory,
                    "-m", tools.ModelDirectory,
                    "-n", model, "-s", scale.ToString(CultureInfo.InvariantCulture), "-f", "png"
                ], jobDirectory, reportToolOutput, cancellationToken);

            if (!Directory.EnumerateFiles(upscaledDirectory, "frame_*.png").Any())
                throw new InvalidDataException("Le moteur IA n'a produit aucune image.");

            report("3/3 — Assemblage de la vidéo et de l'audio…");
            var fps = plan.FramesPerSecond.ToString("0.########", CultureInfo.InvariantCulture);
            await ProcessRunner.RunAsync(ffmpeg,
                [
                    "-hide_banner", "-loglevel", "error", "-y",
                    "-framerate", fps, "-i", Path.Combine(upscaledDirectory, "frame_%08d.png"),
                    "-i", inputPath, "-map", "0:v:0", "-map", "1:a?",
                    "-vf", $"scale={plan.OutputWidth}:{plan.OutputHeight}:flags=lanczos",
                    "-r", fps, "-fps_mode", "cfr",
                    "-c:v", "libx264", "-preset", "medium", "-crf", "18",
                    "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k",
                    "-map_metadata", "1", "-movflags", "+faststart",
                    "-shortest", outputPath
                ], jobDirectory, reportToolOutput, cancellationToken);

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
