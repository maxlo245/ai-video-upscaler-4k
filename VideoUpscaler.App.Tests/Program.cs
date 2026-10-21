using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using VideoUpscaler.App;

var workDirectory = Path.Combine(Path.GetTempPath(), $"UpscaleStudio4K-test-{Guid.NewGuid():N}");
Directory.CreateDirectory(workDirectory);

try
{
    for (var coordinate = 0; coordinate < 288; coordinate++)
    {
        var outgoingWeight = OnnxUpscaler.CalculateAxisBlendWeight(240 + coordinate, 528, 0, 288);
        var incomingWeight = OnnxUpscaler.CalculateAxisBlendWeight(coordinate, 528, 288, 0);
        if (MathF.Abs(outgoingWeight + incomingWeight - 1f) > 0.02f)
            throw new InvalidDataException("Tile blending has a visible discontinuity across the overlap.");
    }
    Console.WriteLine("PASS: neighboring inference tiles blend smoothly across their full overlap");

    var inputPath = Path.Combine(workDirectory, "input.png");
    using (var input = new Bitmap(400, 24, PixelFormat.Format24bppRgb))
    {
        for (var y = 0; y < input.Height; y++)
        for (var x = 0; x < input.Width; x++)
            input.SetPixel(x, y, Color.FromArgb(x % 256, y * 10, (x + y) % 256));

        input.Save(inputPath, ImageFormat.Png);
    }

    var appAssembly = typeof(VideoProcessor).Assembly;
    foreach (var model in new[]
             {
                 (Resource: "UpscaleStudio4K.Models.General.onnx", Name: "Real-ESRGAN x4plus"),
                 (Resource: "UpscaleStudio4K.Models.Anime.onnx", Name: "Real-ESRGAN x4plus Anime 6B")
             })
    {
        var modelPath = Path.Combine(workDirectory, $"{model.Name}.onnx");
        using (var resource = appAssembly.GetManifestResourceStream(model.Resource)
                              ?? throw new InvalidDataException($"Embedded model missing: {model.Resource}"))
        await using (var destination = File.Create(modelPath))
        {
            await resource.CopyToAsync(destination);
        }

        var outputPath = Path.Combine(workDirectory, $"{model.Name}.png");
        using (var upscaler = new OnnxUpscaler(modelPath, Console.WriteLine))
            upscaler.ProcessFrame(inputPath, outputPath, 800, 48, CancellationToken.None);

        using var output = new Bitmap(outputPath);
        if (output.Width != 800 || output.Height != 48)
            throw new InvalidDataException($"{model.Name} returned unexpected output dimensions.");
        if (output.GetPixel(64, 24).ToArgb() == output.GetPixel(480, 24).ToArgb())
            throw new InvalidDataException($"{model.Name} returned a constant output image.");

        Console.WriteLine($"PASS: {model.Name} tiled inference at {output.Width}×{output.Height}");
    }

    var tools = new ExternalTools();
    if (tools.CurrentFfmpegPath is not null && tools.CurrentFfprobePath is not null)
    {
        await tools.EnsureInstalledAsync(Console.WriteLine, CancellationToken.None);
        var videoPath = Path.Combine(workDirectory, "input.mp4");
        var outputVideoPath = Path.Combine(workDirectory, "upscaled.mp4");
        await ProcessRunner.RunAsync(
            tools.CurrentFfmpegPath,
            [
                "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "testsrc=size=64x48:rate=2",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
                "-t", "1", "-pix_fmt", "yuv420p", "-c:a", "aac", "-y", videoPath
            ],
            workDirectory,
            null,
            CancellationToken.None);
        var progressUpdates = new List<VideoProcessor.VideoProgress>();
        await new VideoProcessor(tools).RunAsync(
            videoPath, outputVideoPath, 2, false, Console.WriteLine, CancellationToken.None,
            progressUpdates.Add);
        if (!progressUpdates.Any(progress =>
                progress.Stage == "Upscaling des images" &&
                progress.CompletedFrames > 0 &&
                progress.TotalFrames > 0 &&
                progress.Remaining.HasValue))
            throw new InvalidDataException("The video processor did not report image timing estimates.");
        if (!progressUpdates.Any(progress => progress.Stage == "Assemblage de la vidéo et de l'audio"))
            throw new InvalidDataException("The video processor did not report the assembly stage.");
        if (!progressUpdates.Any(progress =>
                progress.Stage == "Assemblage de la vidéo et de l'audio" &&
                progress.Remaining.HasValue))
            throw new InvalidDataException("The video processor did not report an assembly time estimate.");

        var outputInfo = await ProcessRunner.RunAsync(
            tools.CurrentFfprobePath,
            [
                "-v", "error", "-show_entries", "stream=codec_type,width,height",
                "-of", "json", outputVideoPath
            ],
            workDirectory,
            null,
            CancellationToken.None);
        using var videoDocument = System.Text.Json.JsonDocument.Parse(outputInfo);
        var streams = videoDocument.RootElement.GetProperty("streams");
        if (!streams.EnumerateArray().Any(stream =>
                stream.GetProperty("codec_type").GetString() == "video" &&
                stream.GetProperty("width").GetInt32() == 128 &&
                stream.GetProperty("height").GetInt32() == 96))
            throw new InvalidDataException("The video processor returned unexpected dimensions.");
        if (!streams.EnumerateArray().Any(stream => stream.GetProperty("codec_type").GetString() == "audio"))
            throw new InvalidDataException("The video processor did not preserve its audio track.");

        Console.WriteLine("PASS: end-to-end video inference, output dimensions, and audio preservation");
    }
    else
    {
        Console.WriteLine("SKIP: FFmpeg is unavailable; end-to-end video test could not run.");
    }
}
finally
{
    Directory.Delete(workDirectory, recursive: true);
}
