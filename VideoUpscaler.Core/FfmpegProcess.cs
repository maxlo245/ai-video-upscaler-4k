using System.Diagnostics;
using System.Text;

namespace VideoUpscaler.Core;

/// <summary>
/// Processus FFmpeg piloté par tubes : entrée binaire (images brutes) ou sortie binaire,
/// selon l'usage. Les erreurs sont drainées en continu pour éviter tout interblocage.
/// </summary>
internal sealed class FfmpegProcess : IDisposable
{
    private readonly Process process;
    private readonly StringBuilder errors = new();
    private readonly object errorLock = new();
    private bool disposed;

    private FfmpegProcess(
        string executable,
        IEnumerable<string> arguments,
        bool binaryStdout,
        bool binaryStdin,
        string? workingDirectory,
        Action<string>? onLine)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = binaryStdin
        };
        if (workingDirectory is not null)
            startInfo.WorkingDirectory = workingDirectory;
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        process = new Process { StartInfo = startInfo };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is null)
                return;
            lock (errorLock)
                errors.AppendLine(eventArgs.Data);
            onLine?.Invoke(eventArgs.Data);
        };
        if (!binaryStdout)
        {
            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is not null)
                    onLine?.Invoke(eventArgs.Data);
            };
        }

        if (!process.Start())
            throw new InvalidOperationException($"Impossible de démarrer {Path.GetFileName(executable)}.");
        process.BeginErrorReadLine();
        if (!binaryStdout)
            process.BeginOutputReadLine();
    }

    public static FfmpegProcess StartDecoder(
        string executable,
        IEnumerable<string> arguments,
        string? workingDirectory,
        Action<string>? onLine) =>
        new(executable, arguments, binaryStdout: true, binaryStdin: false, workingDirectory, onLine);

    public static FfmpegProcess StartEncoder(
        string executable,
        IEnumerable<string> arguments,
        string? workingDirectory,
        Action<string>? onLine) =>
        new(executable, arguments, binaryStdout: false, binaryStdin: true, workingDirectory, onLine);

    public Stream StandardOutput => process.StandardOutput.BaseStream;
    public Stream StandardInput => process.StandardInput.BaseStream;

    public bool HasExited
    {
        get
        {
            try
            {
                return process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public async Task EnsureSuccessAsync(CancellationToken cancellationToken)
    {
        await process.WaitForExitAsync(cancellationToken);
        process.WaitForExit();
        if (process.ExitCode == 0)
            return;

        string detail;
        lock (errorLock)
            detail = errors.ToString().Trim();
        throw new InvalidOperationException(
            $"FFmpeg a échoué (code {process.ExitCode}).\n{detail}");
    }

    public void Kill()
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Kill();
        process.Dispose();
    }
}
