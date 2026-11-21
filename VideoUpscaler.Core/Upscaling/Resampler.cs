namespace VideoUpscaler.Core.Upscaling;

/// <summary>
/// Poids de rééchantillonnage séparables (Mitchell-Netravali, B = C = 1/3) calculés une
/// seule fois par image. Le noyau est élargi quand on réduit, ce qui évite le crénelage.
/// </summary>
public sealed class ResamplePlan
{
    private ResamplePlan(int inputLength, int outputLength, int taps, int[] first, float[] weights)
    {
        InputLength = inputLength;
        OutputLength = outputLength;
        Taps = taps;
        First = first;
        Weights = weights;
    }

    public int InputLength { get; }
    public int OutputLength { get; }
    public int Taps { get; }
    /// <summary>Premier index (dans l'image modèle) utilisé pour chaque pixel de sortie.</summary>
    public int[] First { get; }
    /// <summary>Poids aplatis : <c>Weights[x * Taps + t]</c>.</summary>
    public float[] Weights { get; }

    public static ResamplePlan Create(int inputLength, int outputLength, int modelScale)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(inputLength, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(outputLength, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(modelScale, 1);

        var ratio = (double)modelScale * inputLength / outputLength;
        var radius = 2.0 * ratio;
        var taps = (int)Math.Ceiling(2 * radius) + 2;
        var first = new int[outputLength];
        var weights = new float[checked(outputLength * taps)];

        for (var x = 0; x < outputLength; x++)
        {
            var center = ((x + 0.5) * ratio) - 0.5;
            var start = (int)Math.Floor(center - radius) + 1;
            first[x] = start;

            var sum = 0.0;
            for (var t = 0; t < taps; t++)
            {
                var weight = Mitchell((start + t - center) / ratio);
                weights[x * taps + t] = (float)weight;
                sum += weight;
            }

            if (sum <= 1e-6)
            {
                // Filet de sécurité : noyau dégénéré, on retombe sur le plus proche voisin.
                Array.Clear(weights, x * taps, taps);
                var nearest = Math.Clamp((int)Math.Round(center), start, start + taps - 1);
                weights[x * taps + nearest - start] = 1f;
                continue;
            }

            var inverse = (float)(1.0 / sum);
            for (var t = 0; t < taps; t++)
                weights[x * taps + t] *= inverse;
        }

        return new ResamplePlan(inputLength, outputLength, taps, first, weights);
    }

    private static double Mitchell(double x)
    {
        const double b = 1.0 / 3.0;
        const double c = 1.0 / 3.0;
        x = Math.Abs(x);
        if (x < 1.0)
        {
            return (((12 - 9 * b - 6 * c) * x * x * x) +
                    ((-18 + 12 * b + 6 * c) * x * x) +
                    (6 - 2 * b)) / 6.0;
        }

        if (x < 2.0)
        {
            return (((-b - 6 * c) * x * x * x) +
                    ((6 * b + 30 * c) * x * x) +
                    ((-12 * b - 48 * c) * x) +
                    (8 * b + 24 * c)) / 6.0;
        }

        return 0;
    }
}
