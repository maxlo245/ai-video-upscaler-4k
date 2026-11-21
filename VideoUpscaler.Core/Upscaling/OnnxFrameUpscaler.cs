using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VideoUpscaler.Core.Upscaling;

/// <summary>
/// Exécute un modèle Real-ESRGAN (ONNX) sur une tuile. DirectML est utilisé quand il est
/// disponible, avec repli automatique sur le processeur. Les formes d'entrée étant
/// constantes, ONNX Runtime peut réutiliser ses tampons et ses noyaux compilés.
/// </summary>
public sealed class OnnxFrameUpscaler : IFrameUpscaler
{
    private readonly InferenceSession session;
    private readonly string inputName;
    private bool disposed;

    public OnnxFrameUpscaler(string modelPath, bool preferGpu = true, Action<string>? report = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("Le modèle ONNX est introuvable.", modelPath);

        session = CreateSession(modelPath, preferGpu, report);
        inputName = session.InputMetadata.Keys.Single();
        Scale = DetectScale();

        report?.Invoke($"Modèle chargé : agrandissement ×{Scale}.");
    }

    public int Scale { get; }

    public void Upscale(float[] input, int width, int height, float[] output)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        var inputLength = checked(3 * width * height);
        if (input.Length < inputLength)
            throw new ArgumentException("Le tampon d'entrée est trop petit.", nameof(input));

        var tensor = new DenseTensor<float>(
            input.AsMemory(0, inputLength),
            [1, 3, height, width]);
        var value = NamedOnnxValue.CreateFromTensor(inputName, tensor);
        using var results = session.Run([value]);

        if (results.Count != 1 || results[0].AsTensor<float>() is not DenseTensor<float> result)
            throw new InvalidDataException("Le modèle ONNX doit produire une seule sortie tenseur.");

        var expectedOutput = checked(3 * width * height * Scale * Scale);
        if (result.Dimensions.Length != 4 ||
            result.Dimensions[0] != 1 ||
            result.Dimensions[1] != 3 ||
            result.Dimensions[2] != height * Scale ||
            result.Dimensions[3] != width * Scale)
        {
            throw new InvalidDataException(
                $"Le modèle ONNX a renvoyé une résolution inattendue (attendu {width * Scale} × {height * Scale}).");
        }

        if (output.Length < expectedOutput)
            throw new ArgumentException("Le tampon de sortie est trop petit.", nameof(output));

        if (result.Buffer.Length < expectedOutput)
            throw new InvalidDataException("La sortie du modèle ONNX est incomplète.");

        result.Buffer.Span[..expectedOutput].CopyTo(output);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        session.Dispose();
    }

    private int DetectScale()
    {
        const int probeSize = 32;
        var probe = new float[3 * probeSize * probeSize];
        Array.Fill(probe, 0.5f);

        var tensor = new DenseTensor<float>(probe, [1, 3, probeSize, probeSize]);
        var value = NamedOnnxValue.CreateFromTensor(inputName, tensor);
        using var results = session.Run([value]);

        if (results.Count != 1 || results[0].AsTensor<float>() is not DenseTensor<float> result ||
            result.Dimensions.Length != 4 || result.Dimensions[1] != 3)
        {
            throw new InvalidDataException("Le modèle ONNX ne renvoie pas une image RVB.");
        }

        var width = result.Dimensions[3];
        if (width % probeSize != 0 || width / probeSize is not (2 or 4))
            throw new InvalidDataException(
                $"Facteur d'agrandissement non pris en charge : {width} pixels pour une entrée de {probeSize}.");

        var scale = width / probeSize;
        if (result.Dimensions[2] != probeSize * scale)
            throw new InvalidDataException("Le modèle ONNX n'agrandit pas l'image de façon homogène.");

        return scale;
    }

    private static InferenceSession CreateSession(string modelPath, bool preferGpu, Action<string>? report)
    {
        if (preferGpu)
        {
            try
            {
                using var gpuOptions = CreateOptions();
                gpuOptions.AppendExecutionProvider_DML(0);
                var gpuSession = new InferenceSession(modelPath, gpuOptions);
                report?.Invoke("Inférence ONNX Runtime accélérée par DirectML.");
                return gpuSession;
            }
            catch (Exception exception) when (
                exception is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException
                    or TypeInitializationException)
            {
                report?.Invoke($"DirectML indisponible ({exception.Message}). Inférence sur le processeur.");
            }
        }

        var cpuSession = new InferenceSession(modelPath, CreateOptions());
        report?.Invoke("Inférence ONNX Runtime sur le processeur.");
        return cpuSession;
    }

    private static SessionOptions CreateOptions() => new()
    {
        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        // Les tuiles ont toujours la même forme : mémoire et noyaux sont réutilisés.
        EnableMemoryPattern = true
    };
}
