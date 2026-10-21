using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VideoUpscaler.App;

internal sealed class OnnxUpscaler : IDisposable
{
    private const int ModelScale = 4;
    private const int TileSize = 384;
    private const int TileOverlap = 144;
    private readonly InferenceSession session;
    private readonly string inputName;
    private readonly string outputName;

    public OnnxUpscaler(string modelPath, Action<string> report)
    {
        session = CreateSession(modelPath, report);
        inputName = session.InputMetadata.Keys.Single();
        outputName = session.OutputMetadata.Keys.Single();
    }

    public void ProcessFrame(
        string inputPath,
        string outputPath,
        int outputWidth,
        int outputHeight,
        CancellationToken cancellationToken)
    {
        using var sourceBitmap = new Bitmap(inputPath);
        var sourceWidth = sourceBitmap.Width;
        var sourceHeight = sourceBitmap.Height;
        var sourcePixels = ReadBgrPixels(sourceBitmap);
        var outputPixels = new byte[checked(outputWidth * outputHeight * 3)];
        var accumulatedWeights = new float[checked(outputWidth * outputHeight)];

        for (var coreY = 0; coreY < sourceHeight; coreY += TileSize)
        {
            var coreHeight = Math.Min(TileSize, sourceHeight - coreY);
            var tileY = Math.Max(0, coreY - TileOverlap);
            var tileBottom = Math.Min(sourceHeight, coreY + coreHeight + TileOverlap);
            var tileHeight = tileBottom - tileY;

            for (var coreX = 0; coreX < sourceWidth; coreX += TileSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var coreWidth = Math.Min(TileSize, sourceWidth - coreX);
                var tileX = Math.Max(0, coreX - TileOverlap);
                var tileRight = Math.Min(sourceWidth, coreX + coreWidth + TileOverlap);
                var tileWidth = tileRight - tileX;
                var input = CreateInputTensor(sourcePixels, sourceWidth, tileX, tileY, tileWidth, tileHeight);
                using var results = session.Run([NamedOnnxValue.CreateFromTensor(inputName, input)]);
                using var upscaledBitmap = CreateUpscaledBitmap(results.Single().AsTensor<float>(), tileWidth, tileHeight);

                var scaledTileX = ScaleCoordinate(tileX, outputWidth, sourceWidth);
                var scaledTileY = ScaleCoordinate(tileY, outputHeight, sourceHeight);
                var scaledTileRight = ScaleCoordinate(tileRight, outputWidth, sourceWidth);
                var scaledTileBottom = ScaleCoordinate(tileBottom, outputHeight, sourceHeight);
                var resizedWidth = scaledTileRight - scaledTileX;
                var resizedHeight = scaledTileBottom - scaledTileY;
                using var resizedTile =
                    resizedWidth == upscaledBitmap.Width && resizedHeight == upscaledBitmap.Height
                        ? null
                        : ResizeBitmap(upscaledBitmap, resizedWidth, resizedHeight);
                var tilePixels = ReadBgrPixels(resizedTile ?? upscaledBitmap);

                var fadeInWidth = coreX > 0
                    ? ScaleCoordinate(coreX + TileOverlap, outputWidth, sourceWidth) - scaledTileX
                    : 0;
                var fadeInHeight = coreY > 0
                    ? ScaleCoordinate(coreY + TileOverlap, outputHeight, sourceHeight) - scaledTileY
                    : 0;
                var fadeOutWidth = coreX + coreWidth < sourceWidth
                    ? scaledTileRight - ScaleCoordinate(coreX + coreWidth - TileOverlap, outputWidth, sourceWidth)
                    : 0;
                var fadeOutHeight = coreY + coreHeight < sourceHeight
                    ? scaledTileBottom - ScaleCoordinate(coreY + coreHeight - TileOverlap, outputHeight, sourceHeight)
                    : 0;
                var horizontalWeights = new float[resizedWidth];
                for (var x = 0; x < resizedWidth; x++)
                    horizontalWeights[x] = CalculateAxisBlendWeight(x, resizedWidth, fadeInWidth, fadeOutWidth);
                var verticalWeights = new float[resizedHeight];
                for (var y = 0; y < resizedHeight; y++)
                    verticalWeights[y] = CalculateAxisBlendWeight(y, resizedHeight, fadeInHeight, fadeOutHeight);

                for (var y = 0; y < resizedHeight; y++)
                {
                    var weightY = verticalWeights[y];
                    var targetRow = (scaledTileY + y) * outputWidth + scaledTileX;
                    var sourceRow = y * resizedWidth;
                    for (var x = 0; x < resizedWidth; x++)
                    {
                        var blendWeight = weightY * horizontalWeights[x];
                        if (blendWeight <= 0)
                            continue;

                        var targetPixel = targetRow + x;
                        var previousWeight = accumulatedWeights[targetPixel];
                        var totalWeight = previousWeight + blendWeight;
                        var targetOffset = targetPixel * 3;
                        var sourceOffset = (sourceRow + x) * 3;
                        for (var channel = 0; channel < 3; channel++)
                        {
                            outputPixels[targetOffset + channel] = (byte)Math.Clamp(
                                (int)MathF.Round(
                                    (outputPixels[targetOffset + channel] * previousWeight +
                                     tilePixels[sourceOffset + channel] * blendWeight) / totalWeight),
                                0,
                                255);
                        }

                        accumulatedWeights[targetPixel] = totalWeight;
                    }
                }
            }
        }

        SaveBgrPixels(outputPixels, outputWidth, outputHeight, outputPath);
    }

    public void Dispose() => session.Dispose();

    private static InferenceSession CreateSession(string modelPath, Action<string> report)
    {
        using var gpuOptions = CreateSessionOptions();
        try
        {
            gpuOptions.AppendExecutionProvider_DML(0);
            var gpuSession = new InferenceSession(modelPath, gpuOptions);
            report("Moteur IA ONNX Runtime accéléré par DirectML.");
            return gpuSession;
        }
        catch (Exception exception) when (
            exception is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException)
        {
            report($"DirectML indisponible ({exception.Message}). Inférence de secours sur le processeur.");
        }

        using var cpuOptions = CreateSessionOptions();
        var cpuSession = new InferenceSession(modelPath, cpuOptions);
        report("Moteur IA ONNX Runtime sur le processeur.");
        return cpuSession;
    }

    private static SessionOptions CreateSessionOptions() => new()
    {
        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        EnableMemoryPattern = false
    };

    private static DenseTensor<float> CreateInputTensor(
        byte[] sourcePixels,
        int sourceWidth,
        int tileX,
        int tileY,
        int tileWidth,
        int tileHeight)
    {
        var planeSize = checked(tileWidth * tileHeight);
        var values = new float[checked(planeSize * 3)];
        for (var y = 0; y < tileHeight; y++)
        {
            for (var x = 0; x < tileWidth; x++)
            {
                var sourceIndex = ((tileY + y) * sourceWidth + tileX + x) * 3;
                var targetIndex = y * tileWidth + x;
                values[targetIndex] = sourcePixels[sourceIndex + 2] / 255f;
                values[planeSize + targetIndex] = sourcePixels[sourceIndex + 1] / 255f;
                values[(planeSize * 2) + targetIndex] = sourcePixels[sourceIndex] / 255f;
            }
        }

        return new DenseTensor<float>(values, [1, 3, tileHeight, tileWidth]);
    }

    private static Bitmap CreateUpscaledBitmap(Tensor<float> values, int inputWidth, int inputHeight)
    {
        var expectedWidth = checked(inputWidth * ModelScale);
        var expectedHeight = checked(inputHeight * ModelScale);
        if (values.Dimensions.Length != 4 ||
            values.Dimensions[0] != 1 ||
            values.Dimensions[1] != 3 ||
            values.Dimensions[2] != expectedHeight ||
            values.Dimensions[3] != expectedWidth)
        {
            throw new InvalidDataException("Le modèle ONNX a renvoyé une résolution inattendue.");
        }

        var bitmap = new Bitmap(expectedWidth, expectedHeight, PixelFormat.Format24bppRgb);
        var rectangle = new Rectangle(0, 0, expectedWidth, expectedHeight);
        var bitmapData = bitmap.LockBits(rectangle, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[expectedWidth * 3];
            for (var y = 0; y < expectedHeight; y++)
            {
                for (var x = 0; x < expectedWidth; x++)
                {
                    row[x * 3] = ToByte(values[0, 2, y, x]);
                    row[(x * 3) + 1] = ToByte(values[0, 1, y, x]);
                    row[(x * 3) + 2] = ToByte(values[0, 0, y, x]);
                }

                Marshal.Copy(row, 0, IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride), row.Length);
            }
        }
        catch
        {
            bitmap.UnlockBits(bitmapData);
            bitmap.Dispose();
            throw;
        }

        bitmap.UnlockBits(bitmapData);
        return bitmap;
    }

    private static Bitmap ResizeBitmap(Bitmap source, int width, int height)
    {
        if (width < 1 || height < 1)
            throw new InvalidDataException("Le modèle IA a produit une tuile vide.");

        var resized = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(resized);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        return resized;
    }

    private static byte[] ReadBgrPixels(Bitmap source)
    {
        using var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.DrawImage(source, new Rectangle(0, 0, bitmap.Width, bitmap.Height));

        var pixels = new byte[checked(bitmap.Width * bitmap.Height * 3)];
        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var bitmapData = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var rowSize = bitmap.Width * 3;
            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(
                    IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride),
                    pixels,
                    y * rowSize,
                    rowSize);
            }
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }

        return pixels;
    }

    private static void SaveBgrPixels(byte[] pixels, int width, int height, string outputPath)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        var rectangle = new Rectangle(0, 0, width, height);
        var bitmapData = bitmap.LockBits(rectangle, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var rowSize = width * 3;
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(
                    pixels,
                    y * rowSize,
                    IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride),
                    rowSize);
            }
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }

        bitmap.Save(outputPath, ImageFormat.Png);
    }

    private static int ScaleCoordinate(int coordinate, int outputLength, int inputLength) =>
        (int)Math.Round((double)coordinate * outputLength / inputLength, MidpointRounding.AwayFromZero);

    internal static float CalculateAxisBlendWeight(
        int coordinate, int length, int fadeInLength, int fadeOutLength)
    {
        var weight = 1f;
        if (fadeInLength > 0)
            weight *= SmoothStep((float)coordinate / fadeInLength);
        if (fadeOutLength > 0)
            weight *= SmoothStep((float)(length - 1 - coordinate) / fadeOutLength);
        return weight;
    }

    private static float SmoothStep(float value)
    {
        var clamped = Math.Clamp(value, 0f, 1f);
        return clamped * clamped * (3f - 2f * clamped);
    }

    private static byte ToByte(float value) =>
        (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
}
