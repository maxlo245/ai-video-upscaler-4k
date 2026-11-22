namespace VideoUpscaler.Core;

/// <summary>Chemins des binaires FFmpeg utilisés par le traitement vidéo.</summary>
public interface IToolPaths
{
    string? CurrentFfmpegPath { get; }
    string? CurrentFfprobePath { get; }
}

/// <summary>Exécution d'un processus externe avec collecte de la sortie et annulation.</summary>
public static class ProcessRunner
{
    public static async Task<string> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string? workingDirectory,
        Action<string>? output,
        CancellationToken cancellationToken,
        Action<TimeSpan>? mediaProgress = null)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (workingDirectory is not null)
            startInfo.WorkingDirectory = workingDirectory;
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is null)
                return;
            lock (stdout)
                stdout.AppendLine(eventArgs.Data);
            if (mediaProgress is not null &&
                eventArgs.Data.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                long.TryParse(
                    eventArgs.Data.AsSpan("out_time_us=".Length),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var microseconds))
            {
                mediaProgress(TimeSpan.FromMilliseconds(microseconds / 1000d));
            }

            output?.Invoke(eventArgs.Data);
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is null)
                return;
            lock (stderr)
                stderr.AppendLine(eventArgs.Data);
            output?.Invoke(eventArgs.Data);
        };

        if (!process.Start())
            throw new InvalidOperationException($"Impossible de démarrer {Path.GetFileName(executable)}.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        });

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }

        lock (stdout)
        lock (stderr)
        {
            if (process.ExitCode != 0)
            {
                var detail = stderr.Length > 0 ? stderr.ToString() : stdout.ToString();
                throw new InvalidOperationException(
                    $"{Path.GetFileName(executable)} a échoué (code {process.ExitCode}).\n{detail.Trim()}");
            }

            return stdout.ToString();
        }
    }
}
