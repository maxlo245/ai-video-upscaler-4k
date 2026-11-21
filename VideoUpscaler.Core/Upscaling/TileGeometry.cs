namespace VideoUpscaler.Core.Upscaling;

/// <summary>
/// Découpe l'image en tuiles dont la zone « utile » (core) ne se chevauche jamais.
/// Chaque tuile est inférée avec une marge de contexte réfléchie, puis la marge est
/// ignorée : aucun mélange entre tuiles, donc aucune bande floue ni double contour.
/// </summary>
public readonly record struct TileSpec(
    int CoreX,
    int CoreY,
    int CoreWidth,
    int CoreHeight,
    int OutputX,
    int OutputY,
    int OutputWidth,
    int OutputHeight);

public static class TileGeometry
{
    public static IReadOnlyList<TileSpec> Enumerate(
        int width,
        int height,
        int outputWidth,
        int outputHeight,
        int tileSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(outputWidth, width);
        ArgumentOutOfRangeException.ThrowIfLessThan(outputHeight, height);
        ArgumentOutOfRangeException.ThrowIfLessThan(tileSize, 1);

        var tiles = new List<TileSpec>(
            checked(((width + tileSize - 1) / tileSize) * ((height + tileSize - 1) / tileSize)));

        for (var y = 0; y < height; y += tileSize)
        {
            var coreY = y;
            var coreHeight = Math.Min(tileSize, height - y);
            var outputY = MapStart(coreY, height, outputHeight);
            var outputBottom = MapStart(coreY + coreHeight, height, outputHeight);

            for (var x = 0; x < width; x += tileSize)
            {
                var coreX = x;
                var coreWidth = Math.Min(tileSize, width - x);
                var outputX = MapStart(coreX, width, outputWidth);
                var outputRight = MapStart(coreX + coreWidth, width, outputWidth);

                tiles.Add(new TileSpec(
                    coreX,
                    coreY,
                    coreWidth,
                    coreHeight,
                    outputX,
                    outputY,
                    Math.Max(1, outputRight - outputX),
                    Math.Max(1, outputBottom - outputY)));
            }
        }

        return tiles;
    }

    /// <summary>Début de la plage de sortie correspondant à une coordonnée source.</summary>
    public static int MapStart(int coordinate, int inputLength, int outputLength) =>
        (int)Math.Round((double)coordinate * outputLength / inputLength, MidpointRounding.AwayFromZero);

    /// <summary>Miroir sans répétition du bord (BORDER_REFLECT_101).</summary>
    public static int Reflect(int index, int length)
    {
        if (length <= 1)
            return 0;

        var period = 2 * (length - 1);
        index %= period;
        if (index < 0)
            index += period;
        return index < length ? index : period - index;
    }
}
