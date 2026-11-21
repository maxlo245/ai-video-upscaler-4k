using System.Diagnostics;
using VideoUpscaler.Core;
using VideoUpscaler.Core.Upscaling;

var passed = 0;
var workDirectory = Path.Combine(Path.GetTempPath(), $"UpscaleStudio4K-tests-{Guid.NewGuid():N}");
Directory.CreateDirectory(workDirectory);

void Check(string name, bool condition)
{
    if (!condition)
        throw new InvalidOperationException($"FAILED: {name}");
    Console.WriteLine($"PASS: {name}");
    passed++;
}

string? FindRepositoryRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "Models", "RealESRGAN", "General", "model.onnx")))
            return directory.FullName;
        directory = directory.Parent;
    }

    return null;
}

try
{
    // --- Planification vidéo -------------------------------------------------
    var hd2x = VideoPlan.Create(1920, 1080, 2, 30);
    Check("2x HD produces 4K UHD", hd2x.OutputWidth == 3840 && hd2x.OutputHeight == 2160);

    var fullHd4x = VideoPlan.Create(1920, 1080, 4, 29.97);
    Check("4x HD is capped at 4K UHD", fullHd4x.OutputWidth == 3840 && fullHd4x.OutputHeight == 2160);

    var portrait4x = VideoPlan.Create(1080, 1920, 4, 60);
    Check("Portrait videos use 4K portrait bounds", portrait4x.OutputWidth == 2160 && portrait4x.OutputHeight == 3840);

    var small4x = VideoPlan.Create(640, 360, 4, 24);
    Check("4x low-resolution output is retained", small4x.OutputWidth == 2560 && small4x.OutputHeight == 1440);

    Check("Frame-rate fraction is parsed", Math.Abs(VideoPlan.ParseFrameRate("30000/1001") - 29.97002997) < 0.001);
    Check("Invalid frame rate falls back safely", VideoPlan.ParseFrameRate("0/0") == 30);

    foreach (var invalid in new[] { (0, 720, 2, 30d), (1920, 1080, 3, 30d), (1920, 1080, 2, 0d) })
    {
        try
        {
            VideoPlan.Create(invalid.Item1, invalid.Item2, invalid.Item3, invalid.Item4);
            throw new InvalidOperationException("Expected invalid plan parameters to be rejected.");
        }
        catch (ArgumentOutOfRangeException)
        {
            passed++;
        }
    }

    // --- Codec PNG -----------------------------------------------------------
    var gradient = new RgbImage(37, 23, new byte[37 * 23 * 3]);
    for (var y = 0; y < gradient.Height; y++)
    for (var x = 0; x < gradient.Width; x++)
    {
        var index = ((y * gradient.Width) + x) * 3;
        gradient.Pixels[index] = (byte)(x * 7);
        gradient.Pixels[index + 1] = (byte)(y * 11);
        gradient.Pixels[index + 2] = (byte)((x + y) * 5);
    }

    var pngPath = Path.Combine(workDirectory, "gradient.png");
    PngCodec.Encode(pngPath, gradient);
    var decoded = PngCodec.Decode(pngPath);
    Check("PNG round-trip preserves dimensions", decoded.Width == gradient.Width && decoded.Height == gradient.Height);
    Check("PNG round-trip is lossless", decoded.Pixels.AsSpan().SequenceEqual(gradient.Pixels));

    // --- Géométrie de tuilage ------------------------------------------------
    foreach (var (width, height, outputWidth, outputHeight, tile) in new[]
             {
                 (1920, 1080, 3840, 2160, 384),
                 (97, 61, 388, 244, 64),
                 (1440, 1080, 3840, 2880, 300)
             })
    {
        var tiles = TileGeometry.Enumerate(width, height, outputWidth, outputHeight, tile);
        var coverage = new int[outputWidth * outputHeight];
        foreach (var spec in tiles)
        for (var y = 0; y < spec.OutputHeight; y++)
        for (var x = 0; x < spec.OutputWidth; x++)
            coverage[((spec.OutputY + y) * outputWidth) + spec.OutputX + x]++;

        Check(
            $"Tiles cover the {outputWidth}x{outputHeight} frame exactly once",
            coverage.All(value => value == 1));
        Check("Every tile has a positive core", tiles.All(spec => spec.CoreWidth > 0 && spec.CoreHeight > 0));
    }

    Check("Reflection mirrors without repeating the edge", TileGeometry.Reflect(-1, 10) == 1);
    Check("Reflection handles the right edge", TileGeometry.Reflect(10, 10) == 8);
    Check("Reflection handles long overshoot", TileGeometry.Reflect(-23, 10) == 5);

    // --- Poids de rééchantillonnage -----------------------------------------
    foreach (var (input, output, scale) in new[]
             {
                 (1920, 3840, 4), (1920, 3840, 2), (2560, 3840, 4), (641, 1282, 2), (1080, 2160, 4)
             })
    {
        var plan = ResamplePlan.Create(input, output, scale);
        var worst = 0.0;
        for (var x = 0; x < output; x++)
        {
            var sum = 0.0;
            for (var t = 0; t < plan.Taps; t++)
                sum += plan.Weights[(x * plan.Taps) + t];
            worst = Math.Max(worst, Math.Abs(sum - 1));
        }

        Check($"Resampling weights sum to one ({input}->{output}, x{scale})", worst < 1e-4);
    }

    // --- Moteur avec un modèle factice --------------------------------------
    var nearest = new NearestUpscaler();
    using (var engine = new UpscaleEngine(nearest, new UpscaleOptions { TileSize = 8, TilePadding = 8 }))
    {
        var source = new RgbImage(8, 6, new byte[8 * 6 * 3]);
        for (var i = 0; i < source.Pixels.Length; i++)
            source.Pixels[i] = (byte)((i * 37) % 256);
        var sourcePath = Path.Combine(workDirectory, "tiny.png");
        PngCodec.Encode(sourcePath, source);

        var copyPath = Path.Combine(workDirectory, "tiny-4x.png");
        engine.ProcessFrame(sourcePath, copyPath, 32, 24, CancellationToken.None);
        var copy = PngCodec.Decode(copyPath);
        var accurate = true;
        for (var y = 0; y < 24 && accurate; y++)
        for (var x = 0; x < 32 && accurate; x++)
        {
            var expected = source.Pixels[(((y / 4) * 8) + (x / 4)) * 3];
            accurate = Math.Abs(copy.Pixels[(((y * 32) + x) * 3)] - expected) <= 1;
        }

        Check("4x passthrough keeps the model pixels", accurate);

        var halfPath = Path.Combine(workDirectory, "tiny-2x.png");
        engine.ProcessFrame(sourcePath, halfPath, 16, 12, CancellationToken.None);
        var half = PngCodec.Decode(halfPath);
        var smooth = true;
        for (var y = 0; y < 12 && smooth; y++)
        for (var x = 0; x < 16 && smooth; x++)
        {
            var expected = source.Pixels[(((y / 2) * 8) + (x / 2)) * 3];
            smooth = Math.Abs(half.Pixels[(((y * 16) + x) * 3)] - expected) <= 1;
        }

        Check("2x box downscale keeps the model detail", smooth);
    }

    // --- Modèle réel ---------------------------------------------------------
    var repository = FindRepositoryRoot();
    if (repository is null)
    {
        Console.WriteLine("SKIP: bundled model not found; real inference tests skipped.");
    }
    else
    {
        var modelPath = Path.Combine(repository, "Models", "RealESRGAN", "General", "model.onnx");
        var pattern = new RgbImage(96, 72, new byte[96 * 72 * 3]);
        for (var y = 0; y < pattern.Height; y++)
        for (var x = 0; x < pattern.Width; x++)
        {
            var index = ((y * pattern.Width) + x) * 3;
            var radius = ((x - 48) * (x - 48)) + ((y - 36) * (y - 36));
            pattern.Pixels[index] = (byte)(80 + (x * 120 / pattern.Width));
            pattern.Pixels[index + 1] = (byte)(110 + (int)(50 * Math.Sin(y * 0.12)));
            pattern.Pixels[index + 2] = (byte)(radius < 420 ? 235 : 55 + (y / 2));
        }

        var patternPath = Path.Combine(workDirectory, "pattern.png");
        PngCodec.Encode(patternPath, pattern);

        var stopwatch = Stopwatch.StartNew();
        using var upscaler = new OnnxFrameUpscaler(modelPath, preferGpu: false, Console.WriteLine);
        Console.WriteLine($"INFO: model loaded in {stopwatch.ElapsedMilliseconds} ms, scale x{upscaler.Scale}");

        using var engine = new UpscaleEngine(
            upscaler,
            new UpscaleOptions { TileSize = 64, TilePadding = 16 });

        var singlePath = Path.Combine(workDirectory, "pattern-single.png");
        using (var single = new UpscaleEngine(
                   upscaler,
                   new UpscaleOptions { TileSize = 1024, TilePadding = 16 }))
        {
            single.ProcessFrame(patternPath, singlePath, 384, 288, CancellationToken.None);
        }

        var tiledPath = Path.Combine(workDirectory, "pattern-tiled.png");
        engine.ProcessFrame(patternPath, tiledPath, 384, 288, CancellationToken.None);

        var singleImage = PngCodec.Decode(singlePath);
        var tiledImage = PngCodec.Decode(tiledPath);
        long totalDifference = 0;
        var maximumDifference = 0;
        for (var i = 0; i < singleImage.Pixels.Length; i++)
        {
            var difference = Math.Abs(singleImage.Pixels[i] - tiledImage.Pixels[i]);
            totalDifference += difference;
            maximumDifference = Math.Max(maximumDifference, difference);
        }

        var meanDifference = (double)totalDifference / singleImage.Pixels.Length;
        var sortedDifferences = new List<int>(singleImage.Pixels.Length);
        for (var i = 0; i < singleImage.Pixels.Length; i++)
            sortedDifferences.Add(Math.Abs(singleImage.Pixels[i] - tiledImage.Pixels[i]));
        sortedDifferences.Sort();
        var percentile = sortedDifferences[(int)(sortedDifferences.Count * 0.999)];
        Console.WriteLine($"INFO: tiled vs untiled mean={meanDifference:0.000} max={maximumDifference} p99.9={percentile}");
        Check("Tiled inference without visible seams", meanDifference < 1.5 && percentile < 24);

        // Les deux rampes de tuiles voisines doivent se compléter exactement.
        var incoming = UpscaleEngine.BuildAxisWeights(240, 256, 384, 16, 0);
        var outgoing = UpscaleEngine.BuildAxisWeights(0, 0, 256, 0, 16);
        var seamless = true;
        for (var x = 240; x < 256; x++)
            seamless &= MathF.Abs((incoming[x - 240] + outgoing[x]) - 1f) < 1e-4f;
        Check("Neighbouring tile ramps sum to one", seamless);
        Check("Real model produced a 4x frame", tiledImage.Width == 384 && tiledImage.Height == 288);

        var downscaledPath = Path.Combine(workDirectory, "pattern-2x.png");
        using (var half = new UpscaleEngine(
                   upscaler,
                   new UpscaleOptions { TileSize = 64, TilePadding = 16 }))
        {
            half.ProcessFrame(patternPath, downscaledPath, 192, 144, CancellationToken.None);
        }

        var downscaled = PngCodec.Decode(downscaledPath);
        Check("4x model drives an exact 2x output", downscaled.Width == 192 && downscaled.Height == 144);

        var distinct = new HashSet<byte>();
        for (var i = 0; i < downscaled.Pixels.Length; i += 97)
            distinct.Add(downscaled.Pixels[i]);
        Check("Downscaled output is not a flat image", distinct.Count > 16);
    }

    // --- Pipeline vidéo complet ---------------------------------------------
    var ffmpeg = FindOnPath("ffmpeg");
    var ffprobe = FindOnPath("ffprobe");
    if (ffmpeg is null || ffprobe is null || repository is null)
    {
        Console.WriteLine("SKIP: FFmpeg or model missing; end-to-end video test skipped.");
    }
    else
    {
        var tools = new StaticToolPaths(ffmpeg, ffprobe, Path.Combine(repository, "Models", "RealESRGAN", "General", "model.onnx"));
        var videoPath = Path.Combine(workDirectory, "input.mp4");
        await ProcessRunner.RunAsync(ffmpeg,
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "testsrc=size=64x48:rate=2",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
                "-t", "1", "-pix_fmt", "yuv420p", "-c:a", "aac", videoPath
            ],
            workDirectory, null, CancellationToken.None);

        var outputPath = Path.Combine(workDirectory, "output.mp4");
        var updates = new List<VideoProcessor.VideoProgress>();
        var processor = new VideoProcessor(tools);
        await processor.RunAsync(
            videoPath, outputPath, 2, false, SourceCleanup.None,
            new UpscaleOptions { TileSize = 64, TilePadding = 16 },
            Console.WriteLine, CancellationToken.None, updates.Add);

        Check("Video progress reports inference timing", updates.Any(update =>
            update.Stage == "Upscaling des images" && update.CompletedFrames > 0 && update.Remaining.HasValue));
        Check("Video progress reaches completion", updates.Any(update => update.Stage == "Terminé"));

        var probe = await ProcessRunner.RunAsync(ffprobe,
            ["-v", "error", "-show_entries", "stream=codec_type,width,height:format=duration", "-of", "json", outputPath],
            workDirectory, null, CancellationToken.None);
        using var document = System.Text.Json.JsonDocument.Parse(probe);
        var streams = document.RootElement.GetProperty("streams");
        Check("Video output is 128x96", streams.EnumerateArray().Any(stream =>
            stream.GetProperty("codec_type").GetString() == "video" &&
            stream.GetProperty("width").GetInt32() == 128 &&
            stream.GetProperty("height").GetInt32() == 96));
        Check("Audio track preserved", streams.EnumerateArray().Any(stream =>
            stream.GetProperty("codec_type").GetString() == "audio"));

        var duration = double.Parse(
            document.RootElement.GetProperty("format").GetProperty("duration").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture);
        Check("Video duration preserved", duration is > 0.5 and < 1.6);
    }

    Console.WriteLine($"{passed} checks passed.");
}
finally
{
    try
    {
        Directory.Delete(workDirectory, recursive: true);
    }
    catch (IOException)
    {
    }
}

static string? FindOnPath(string executable)
{
    var path = Environment.GetEnvironmentVariable("PATH");
    if (string.IsNullOrEmpty(path))
        return null;

    return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(directory => Path.Combine(directory, executable))
        .FirstOrDefault(File.Exists);
}

internal sealed class NearestUpscaler : IFrameUpscaler
{
    public int Scale => 4;

    public void Upscale(float[] input, int width, int height, float[] output)
    {
        var side = width * Scale;
        var planeIn = width * height;
        var planeOut = side * side;
        for (var channel = 0; channel < 3; channel++)
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
            output[(channel * planeOut) + (y * side) + x] =
                input[(channel * planeIn) + ((y / Scale) * width) + (x / Scale)];
    }

    public void Dispose()
    {
    }
}

internal sealed class StaticToolPaths(string ffmpeg, string ffprobe, string model) : IToolPaths
{
    public string? CurrentFfmpegPath => ffmpeg;
    public string? CurrentFfprobePath => ffprobe;
    public string GeneralModelPath => model;
    public string AnimeModelPath => model;
}
