namespace VideoUpscaler.Core;

public sealed record VideoPlan(
    int InputWidth,
    int InputHeight,
    int Scale,
    int OutputWidth,
    int OutputHeight,
    double FramesPerSecond)
{
    public static VideoPlan Create(int width, int height, int scale, double framesPerSecond)
    {
        if (width < 1 || height < 1)
            throw new ArgumentOutOfRangeException(nameof(width), "Video dimensions must be positive.");
        if (scale is not (2 or 4))
            throw new ArgumentOutOfRangeException(nameof(scale), "Scale must be 2x or 4x.");
        if (!double.IsFinite(framesPerSecond) || framesPerSecond <= 0 || framesPerSecond > 240)
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond), "Frame rate must be between 0 and 240 FPS.");

        var portrait = height > width;
        var maxWidth = portrait ? 2160 : 3840;
        var maxHeight = portrait ? 3840 : 2160;
        var factor = Math.Min(scale, Math.Min((double)maxWidth / width, (double)maxHeight / height));
        var outputWidth = EvenDimension(width * factor);
        var outputHeight = EvenDimension(height * factor);

        return new VideoPlan(width, height, scale, outputWidth, outputHeight, framesPerSecond);
    }

    public static double ParseFrameRate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 30;

        var parts = value.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var denominator) &&
            denominator > 0 && numerator > 0)
        {
            var rate = numerator / denominator;
            return double.IsFinite(rate) && rate <= 240 ? rate : 30;
        }

        return double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var plain) &&
               double.IsFinite(plain) && plain > 0 && plain <= 240 ? plain : 30;
    }

    private static int EvenDimension(double value) => Math.Max(2, (int)Math.Round(value / 2) * 2);
}
