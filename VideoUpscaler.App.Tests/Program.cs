using VideoUpscaler.Core.Upscaling;

// Test de fumée Windows : vérifie que DirectML (ou le repli processeur) fonctionne
// réellement avec le modèle embarqué.
var repository = FindRepositoryRoot();
if (repository is null)
{
    Console.WriteLine("SKIP: bundled model not found.");
    return;
}

var workDirectory = Path.Combine(Path.GetTempPath(), $"UpscaleStudio4K-app-tests-{Guid.NewGuid():N}");
Directory.CreateDirectory(workDirectory);

try
{
    var modelPath = Path.Combine(repository, "Models", "RealESRGAN", "General", "model.onnx");
    var input = new RgbImage(64, 48, new byte[64 * 48 * 3]);
    for (var y = 0; y < input.Height; y++)
    for (var x = 0; x < input.Width; x++)
    {
        var index = ((y * input.Width) + x) * 3;
        input.Pixels[index] = (byte)(x * 4);
        input.Pixels[index + 1] = (byte)(y * 5);
        input.Pixels[index + 2] = (byte)(((x / 8) + (y / 8)) % 2 == 0 ? 230 : 20);
    }

    var inputPath = Path.Combine(workDirectory, "input.png");
    var outputPath = Path.Combine(workDirectory, "output.png");
    PngCodec.Encode(inputPath, input);

    using var upscaler = new OnnxFrameUpscaler(modelPath, preferGpu: true, Console.WriteLine);
    using var engine = new UpscaleEngine(
        upscaler,
        new UpscaleOptions { TileSize = 64, TilePadding = 16, Fidelity = 0.9, TemporalStability = 0.3 });

    engine.ProcessFrame(inputPath, outputPath, 256, 192, CancellationToken.None);

    var output = PngCodec.Decode(outputPath);
    if (output.Width != 256 || output.Height != 192)
        throw new InvalidDataException($"Unexpected output size: {output.Width}x{output.Height}.");

    var distinct = new HashSet<byte>();
    for (var index = 0; index < output.Pixels.Length; index += 31)
        distinct.Add(output.Pixels[index]);
    if (distinct.Count < 16)
        throw new InvalidDataException("The model returned a flat image.");

    Console.WriteLine("PASS: DirectML/CPU inference produced a 256x192 frame.");
}
finally
{
    Directory.Delete(workDirectory, recursive: true);
}

static string? FindRepositoryRoot()
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
