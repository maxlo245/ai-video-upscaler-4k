using VideoUpscaler.Core;

var passed = 0;

void Check(string name, bool condition)
{
    if (!condition)
        throw new InvalidOperationException($"FAILED: {name}");
    Console.WriteLine($"PASS: {name}");
    passed++;
}

var hd2x = VideoPlan.Create(1920, 1080, 2, 30);
Check("2x HD produces 4K UHD", hd2x.OutputWidth == 3840 && hd2x.OutputHeight == 2160);

var fullHd4x = VideoPlan.Create(1920, 1080, 4, 29.97);
Check("4x HD is capped at 4K UHD", fullHd4x.OutputWidth == 3840 && fullHd4x.OutputHeight == 2160);

var portrait4x = VideoPlan.Create(1080, 1920, 4, 60);
Check("Portrait videos use 4K portrait bounds", portrait4x.OutputWidth == 2160 && portrait4x.OutputHeight == 3840);

var small2x = VideoPlan.Create(640, 360, 2, 24);
Check("2x low-resolution output is retained", small2x.OutputWidth == 1280 && small2x.OutputHeight == 720);

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

Console.WriteLine($"{passed} checks passed.");
