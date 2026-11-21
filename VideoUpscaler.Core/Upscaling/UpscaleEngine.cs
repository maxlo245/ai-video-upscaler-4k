using Microsoft.ML.OnnxRuntime;

namespace VideoUpscaler.Core.Upscaling;

/// <summary>
/// Moteur d'agrandissement d'une image : tuilage avec marge réfléchie, accumulation en
/// virgule flottante, fondu étroit entre tuiles et rééchantillonnage de haute qualité.
/// Le fondu ne porte que sur quelques pixels : aucune bande floue, aucune couture visible.
/// </summary>
public sealed class UpscaleEngine : IDisposable
{
    private readonly IFrameUpscaler model;
    private readonly UpscaleOptions options;
    private readonly Action<string>? report;
    private float[]? inputBuffer;
    private float[]? modelBuffer;
    private float[]? accumulation;
    private float[]? weights;
    private float[]? tempBuffer;
    private byte[]? outputBuffer;
    private byte[]? previousFrame;
    private int previousWidth;
    private int previousHeight;
    private bool disposed;

    public UpscaleEngine(IFrameUpscaler model, UpscaleOptions options, Action<string>? report = null)
    {
        this.model = model ?? throw new ArgumentNullException(nameof(model));
        this.options = options.Validate();
        this.report = report;
    }

    /// <summary>
    /// Traite une image et renvoie le résultat encodé en PNG. Conserve l'état temporel
    /// entre deux appels successifs.
    /// </summary>
    public RgbImage ProcessFrame(
        string inputPath,
        string outputPath,
        int outputWidth,
        int outputHeight,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var image = PngCodec.Decode(inputPath);
        var result = ProcessImage(image, outputWidth, outputHeight, cancellationToken);
        PngCodec.Encode(outputPath, result);
        return result;
    }

    /// <summary>
    /// Traite une image en mémoire. Le tampon renvoyé est réutilisé au prochain appel :
    /// l'appelant doit le consommer (écriture, encodage) avant de rappeler cette méthode.
    /// </summary>
    public RgbImage ProcessImage(
        RgbImage source,
        int outputWidth,
        int outputHeight,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(source);

        if (outputWidth < source.Width || outputHeight < source.Height)
            throw new InvalidDataException("La sortie ne peut pas être plus petite que la source.");

        var pixels = RenderWithFallback(source, outputWidth, outputHeight, cancellationToken);
        if (options.TemporalStability > 0)
            Stabilize(pixels, outputWidth, outputHeight);
        if (options.Fidelity < 0.999)
            ApplyFidelity(pixels, source, outputWidth, outputHeight, cancellationToken);

        return new RgbImage(outputWidth, outputHeight, pixels);
    }

    public void Dispose() => disposed = true;

    private byte[] RenderWithFallback(RgbImage image, int outputWidth, int outputHeight, CancellationToken cancellationToken)
    {
        var tileSize = options.TileSize;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return Render(image, outputWidth, outputHeight, tileSize, cancellationToken);
            }
            catch (OnnxRuntimeException exception) when (
                attempt < 2 && tileSize > 64 && IsOutOfMemory(exception))
            {
                tileSize = Math.Max(8, tileSize / 2);
                report?.Invoke($"Mémoire GPU insuffisante : nouvelle tentative avec des tuiles de {tileSize} px.");
            }
        }
    }

    private static bool IsOutOfMemory(OnnxRuntimeException exception) =>
        exception.Message.Contains("memory", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("alloc", StringComparison.OrdinalIgnoreCase);

    private byte[] Render(
        RgbImage image,
        int outputWidth,
        int outputHeight,
        int tileSize,
        CancellationToken cancellationToken)
    {
        var scale = model.Scale;
        var padding = options.TilePadding;
        var feather = options.TileFeather;
        var maximumInput = checked(tileSize + (padding * 2));
        var maximumPlane = checked(maximumInput * maximumInput);

        if (inputBuffer is null || inputBuffer.Length < maximumPlane * 3 ||
            modelBuffer is null || modelBuffer.Length < maximumPlane * scale * scale * 3)
        {
            inputBuffer = new float[checked(maximumPlane * 3)];
            modelBuffer = new float[checked(maximumPlane * scale * scale * 3)];
        }

        var outputLength = checked(outputWidth * outputHeight);
        if (accumulation is null || accumulation.Length < outputLength * 3 ||
            weights is null || weights.Length < outputLength)
        {
            accumulation = new float[checked(outputLength * 3)];
            weights = new float[outputLength];
        }

        Array.Clear(accumulation, 0, outputLength * 3);
        Array.Clear(weights, 0, outputLength);

        var tiles = TileGeometry.Enumerate(image.Width, image.Height, outputWidth, outputHeight, tileSize);
        var copyPath = outputWidth == image.Width * scale && outputHeight == image.Height * scale;
        var halfPath = !copyPath &&
                       outputWidth * 2 == image.Width * scale &&
                       outputHeight * 2 == image.Height * scale;

        ResamplePlan? horizontal = null;
        ResamplePlan? vertical = null;
        if (!copyPath && !halfPath)
        {
            horizontal = ResamplePlan.Create(image.Width, outputWidth, scale);
            vertical = ResamplePlan.Create(image.Height, outputHeight, scale);
        }

        foreach (var tile in tiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var inputWidth = tile.CoreWidth + (padding * 2);
            var inputHeight = tile.CoreHeight + (padding * 2);
            FillInput(image, tile, padding, inputWidth, inputHeight);
            model.Upscale(inputBuffer!, inputWidth, inputHeight, modelBuffer!);

            var modelWidth = checked(inputWidth * scale);
            var modelHeight = checked(inputHeight * scale);
            var modelPlane = checked(modelWidth * modelHeight);
            var originX = (tile.CoreX - padding) * scale;
            var originY = (tile.CoreY - padding) * scale;

            var coreEndX = tile.CoreX + tile.CoreWidth;
            var coreEndY = tile.CoreY + tile.CoreHeight;
            var renderX0 = tile.CoreX > 0 ? Math.Max(0, tile.CoreX - feather) : 0;
            var renderY0 = tile.CoreY > 0 ? Math.Max(0, tile.CoreY - feather) : 0;
            var outputX0 = TileGeometry.MapStart(renderX0, image.Width, outputWidth);
            var outputY0 = TileGeometry.MapStart(renderY0, image.Height, outputHeight);
            var coreX0 = TileGeometry.MapStart(tile.CoreX, image.Width, outputWidth);
            var coreY0 = TileGeometry.MapStart(tile.CoreY, image.Height, outputHeight);
            var coreX1 = TileGeometry.MapStart(coreEndX, image.Width, outputWidth);
            var coreY1 = TileGeometry.MapStart(coreEndY, image.Height, outputHeight);

            var leftBand = tile.CoreX > 0 && feather > 0 ? coreX0 - outputX0 : 0;
            var topBand = tile.CoreY > 0 && feather > 0 ? coreY0 - outputY0 : 0;
            var rightBand = coreEndX < image.Width && feather > 0
                ? coreX1 - TileGeometry.MapStart(Math.Max(0, coreEndX - feather), image.Width, outputWidth)
                : 0;
            var bottomBand = coreEndY < image.Height && feather > 0
                ? coreY1 - TileGeometry.MapStart(Math.Max(0, coreEndY - feather), image.Height, outputHeight)
                : 0;

            var wx = BuildAxisWeights(outputX0, coreX0, coreX1, leftBand, rightBand);
            var wy = BuildAxisWeights(outputY0, coreY0, coreY1, topBand, bottomBand);

            if (copyPath)
            {
                CopyTile(modelBuffer!, modelWidth, modelPlane, originX, originY,
                    outputWidth, outputX0, outputY0, coreX1, coreY1, wx, wy);
            }
            else if (halfPath)
            {
                HalfTile(modelBuffer!, modelWidth, modelPlane, originX, originY,
                    outputWidth, outputX0, outputY0, coreX1, coreY1, wx, wy);
            }
            else
            {
                ResampleTile(modelBuffer!, modelWidth, modelHeight, modelPlane,
                    originX, originY, outputWidth, outputX0, outputY0, coreX1, coreY1, wx, wy,
                    horizontal!, vertical!, cancellationToken);
            }
        }

        if (outputBuffer is null || outputBuffer.Length != outputLength * 3)
            outputBuffer = new byte[checked(outputLength * 3)];
        var output = outputBuffer;
        for (var index = 0; index < outputLength; index++)
        {
            var weight = weights[index];
            if (weight <= 0f)
                continue;

            var target = index * 3;
            var inverse = 1f / weight;
            var x = index % outputWidth;
            var y = index / outputWidth;
            var dither = Dither(x, y);
            output[target] = ToByte(accumulation[target] * inverse, dither);
            output[target + 1] = ToByte(accumulation[target + 1] * inverse, dither);
            output[target + 2] = ToByte(accumulation[target + 2] * inverse, dither);
        }

        return output;
    }

    internal static float[] BuildAxisWeights(int start, int coreStart, int coreEnd, int leftBand, int rightBand)
    {
        var result = new float[coreEnd - start];
        for (var index = 0; index < result.Length; index++)
        {
            var position = start + index;
            if (leftBand > 0 && position < coreStart)
                result[index] = (position - start + 0.5f) / leftBand;
            else if (rightBand > 0 && position >= coreEnd - rightBand)
                result[index] = (coreEnd - position - 0.5f) / rightBand;
            else
                result[index] = 1f;
        }

        return result;
    }

    private void FillInput(RgbImage image, TileSpec tile, int padding, int inputWidth, int inputHeight)
    {
        var input = inputBuffer!;
        var sourceX = tile.CoreX - padding;
        var sourceY = tile.CoreY - padding;
        var plane = inputWidth * inputHeight;
        var pixels = image.Pixels;

        for (var y = 0; y < inputHeight; y++)
        {
            var sy = TileGeometry.Reflect(sourceY + y, image.Height);
            var sourceRow = sy * image.Width;
            var target = y * inputWidth;
            for (var x = 0; x < inputWidth; x++)
            {
                var sx = TileGeometry.Reflect(sourceX + x, image.Width);
                var offset = (sourceRow + sx) * 3;
                var index = target + x;
                input[index] = pixels[offset] / 255f;
                input[plane + index] = pixels[offset + 1] / 255f;
                input[(plane * 2) + index] = pixels[offset + 2] / 255f;
            }
        }
    }

    private void CopyTile(
        float[] model,
        int modelWidth,
        int plane,
        int originX,
        int originY,
        int outputWidth,
        int startX,
        int startY,
        int endX,
        int endY,
        float[] wx,
        float[] wy)
    {
        var accum = accumulation!;
        var totalWeights = weights!;
        for (var y = startY; y < endY; y++)
        {
            var modelRow = (y - originY) * modelWidth;
            var weightY = wy[y - startY];
            var targetRow = y * outputWidth;
            for (var x = startX; x < endX; x++)
            {
                var modelIndex = modelRow + x - originX;
                var weight = weightY * wx[x - startX];
                var target = (targetRow + x) * 3;
                accum[target] += model[modelIndex] * weight;
                accum[target + 1] += model[plane + modelIndex] * weight;
                accum[target + 2] += model[(plane * 2) + modelIndex] * weight;
                totalWeights[targetRow + x] += weight;
            }
        }

    }

    private void HalfTile(
        float[] model,
        int modelWidth,
        int plane,
        int originX,
        int originY,
        int outputWidth,
        int startX,
        int startY,
        int endX,
        int endY,
        float[] wx,
        float[] wy)
    {
        var accum = accumulation!;
        var totalWeights = weights!;
        for (var y = startY; y < endY; y++)
        {
            var modelY = (y * 2) - originY;
            var row0 = modelY * modelWidth;
            var row1 = (modelY + 1) * modelWidth;
            var weightY = wy[y - startY];
            var targetRow = y * outputWidth;
            for (var x = startX; x < endX; x++)
            {
                var modelX = (x * 2) - originX;
                var weight = weightY * wx[x - startX];
                var target = (targetRow + x) * 3;
                for (var channel = 0; channel < 3; channel++)
                {
                    var p = channel * plane;
                    var sum = model[p + row0 + modelX] +
                              model[p + row0 + modelX + 1] +
                              model[p + row1 + modelX] +
                              model[p + row1 + modelX + 1];
                    accum[target + channel] += sum * 0.25f * weight;
                }

                totalWeights[targetRow + x] += weight;
            }
        }

    }

    private void ResampleTile(
        float[] model,
        int modelWidth,
        int modelHeight,
        int plane,
        int originX,
        int originY,
        int outputWidth,
        int startX,
        int startY,
        int endX,
        int endY,
        float[] wx,
        float[] wy,
        ResamplePlan horizontal,
        ResamplePlan vertical,
        CancellationToken cancellationToken)
    {
        var countX = endX - startX;
        var countY = endY - startY;
        var firstRow = vertical.First[startY];
        var lastRow = vertical.First[endY - 1] + vertical.Taps - 1;
        var rowCount = lastRow - firstRow + 1;
        var planeTemp = rowCount * countX;
        var tempLength = checked(planeTemp * 3);

        if (tempBuffer is null || tempBuffer.Length < tempLength)
            tempBuffer = new float[tempLength];
        var temp = tempBuffer;

        for (var modelY = firstRow; modelY <= lastRow; modelY++)
        {
            if ((modelY & 15) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            var localRow = Math.Clamp(modelY - originY, 0, modelHeight - 1) * modelWidth;
            var tempRow = (modelY - firstRow) * countX;

            for (var x = 0; x < countX; x++)
            {
                var outputX = startX + x;
                var weightBase = outputX * horizontal.Taps;
                var start = horizontal.First[outputX] - originX;
                for (var channel = 0; channel < 3; channel++)
                {
                    var source = (channel * plane) + localRow;
                    var sum = 0f;
                    for (var t = 0; t < horizontal.Taps; t++)
                    {
                        var index = Math.Clamp(start + t, 0, modelWidth - 1);
                        sum += model[source + index] * horizontal.Weights[weightBase + t];
                    }

                    temp[(channel * planeTemp) + tempRow + x] = sum;
                }
            }
        }

        var accum = accumulation!;
        var totalWeights = weights!;
        for (var y = 0; y < countY; y++)
        {
            var outputY = startY + y;
            var weightY = wy[y];
            var weightBase = outputY * vertical.Taps;
            var startRow = vertical.First[outputY];
            var targetRow = outputY * outputWidth;

            for (var x = 0; x < countX; x++)
            {
                var weight = weightY * wx[x];
                var target = (targetRow + startX + x) * 3;
                for (var channel = 0; channel < 3; channel++)
                {
                    var sum = 0f;
                    for (var t = 0; t < vertical.Taps; t++)
                    {
                        var row = (startRow + t - firstRow) * countX;
                        sum += temp[(channel * planeTemp) + row + x] * vertical.Weights[weightBase + t];
                    }

                    accum[target + channel] += sum * weight;
                }

                totalWeights[targetRow + startX + x] += weight;
            }
        }

    }

    private void Stabilize(byte[] pixels, int width, int height)
    {
        var length = checked(width * height * 3);
        if (previousFrame is null || previousFrame.Length != length ||
            previousWidth != width || previousHeight != height)
        {
            previousFrame = new byte[length];
            Buffer.BlockCopy(pixels, 0, previousFrame, 0, length);
            previousWidth = width;
            previousHeight = height;
            return;
        }

        var strength = (float)(options.TemporalStability * 0.5);
        for (var i = 0; i < length; i += 3)
        {
            var difference = Math.Max(
                Math.Abs(pixels[i] - previousFrame[i]),
                Math.Max(Math.Abs(pixels[i + 1] - previousFrame[i + 1]), Math.Abs(pixels[i + 2] - previousFrame[i + 2])));

            if (difference >= 20)
                continue;

            var weight = strength * (1f - SmoothStep(6f, 20f, difference));
            if (weight <= 0.002f)
                continue;

            pixels[i] = Blend(pixels[i], previousFrame[i], weight);
            pixels[i + 1] = Blend(pixels[i + 1], previousFrame[i + 1], weight);
            pixels[i + 2] = Blend(pixels[i + 2], previousFrame[i + 2], weight);
        }

        Buffer.BlockCopy(pixels, 0, previousFrame, 0, length);
    }

    private void ApplyFidelity(byte[] pixels, RgbImage source, int width, int height, CancellationToken cancellationToken)
    {
        var fidelity = (float)options.Fidelity;
        var inverse = 1f - fidelity;
        var ratioX = (double)source.Width / width;
        var ratioY = (double)source.Height / height;
        var src = source.Pixels;
        var srcWidth = source.Width;
        var srcHeight = source.Height;

        for (var y = 0; y < height; y++)
        {
            if ((y & 31) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            var sourceY = ((y + 0.5) * ratioY) - 0.5;
            var y0 = (int)Math.Floor(sourceY);
            var fy = (float)(sourceY - y0);
            var row0 = ((uint)y0 < (uint)srcHeight ? y0 : TileGeometry.Reflect(y0, srcHeight)) * srcWidth;
            var row1 = ((uint)(y0 + 1) < (uint)srcHeight ? y0 + 1 : TileGeometry.Reflect(y0 + 1, srcHeight)) * srcWidth;

            for (var x = 0; x < width; x++)
            {
                var sourceX = ((x + 0.5) * ratioX) - 0.5;
                var x0 = (int)Math.Floor(sourceX);
                var fx = (float)(sourceX - x0);
                var column0 = ((uint)x0 < (uint)srcWidth ? x0 : TileGeometry.Reflect(x0, srcWidth)) * 3;
                var column1 = ((uint)(x0 + 1) < (uint)srcWidth ? x0 + 1 : TileGeometry.Reflect(x0 + 1, srcWidth)) * 3;
                var target = ((y * width) + x) * 3;

                for (var channel = 0; channel < 3; channel++)
                {
                    var top = (src[(row0 * 3) + column0 + channel] * (1f - fx)) + (src[(row0 * 3) + column1 + channel] * fx);
                    var bottom = (src[(row1 * 3) + column0 + channel] * (1f - fx)) + (src[(row1 * 3) + column1 + channel] * fx);
                    var baseline = ((top * (1f - fy)) + (bottom * fy)) / 255f;
                    var blended = ((pixels[target + channel] / 255f) * fidelity) + (baseline * inverse);
                    pixels[target + channel] = ToByte(blended, Dither(x, y));
                }
            }
        }
    }

    private static byte Blend(byte current, byte previous, float weight) =>
        (byte)Math.Clamp((int)MathF.Round(current + ((previous - current) * weight)), 0, 255);

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        var t = Math.Clamp((value - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - (2f * t));
    }

    private static byte ToByte(float value, float dither)
    {
        var rounded = MathF.Round((value * 255f) + dither, MidpointRounding.AwayFromZero);
        return (byte)Math.Clamp((int)rounded, 0, 255);
    }

    private static float Dither(int x, int y)
    {
        var hash = unchecked((uint)((x * 374761393) + (y * 668265263)));
        hash = unchecked((hash ^ (hash >> 13)) * 1274126177u);
        hash ^= hash >> 16;
        return (((hash & 1023) / 1023f) - 0.5f) / 255f;
    }
}
