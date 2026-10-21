using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoUpscaler.App;

internal sealed class ExternalTools
{
    private const string RealEsrganApi = "https://api.github.com/repos/xinntao/Real-ESRGAN-ncnn-vulkan/releases/latest";
    private const string FfmpegApi = "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest";
    private const string ModelBaseUrl =
        "https://raw.githubusercontent.com/upscayl/custom-models/4b6d2cfa59c7442af115dfc6e50fd8d7d40b96ef/models/";
    public const string GeneralModel = "RealESRGAN_General_x4_v3";
    public const string AnimeModel = "realesr-animevideov3-x4";
    private readonly string toolsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UpscaleStudio4K", "tools");

    private static readonly HttpClient Http = CreateHttpClient();

    public string EnginePath => Path.Combine(toolsDirectory, "realesrgan", "realesrgan-ncnn-vulkan.exe");
    public string ModelDirectory => Path.Combine(toolsDirectory, "realesrgan", "models");
    public string FfmpegPath => Path.Combine(toolsDirectory, "ffmpeg", "bin", "ffmpeg.exe");
    public string FfprobePath => Path.Combine(toolsDirectory, "ffmpeg", "bin", "ffprobe.exe");

    public bool IsReady => File.Exists(EnginePath) &&
                           HasModel(GeneralModel) &&
                           HasModel(AnimeModel) &&
                           (File.Exists(FfmpegPath) || FindOnPath("ffmpeg.exe") is not null);

    public string? CurrentFfmpegPath => File.Exists(FfmpegPath) ? FfmpegPath : FindOnPath("ffmpeg.exe");

    public string? CurrentFfprobePath
    {
        get
        {
            var ffmpeg = CurrentFfmpegPath;
            if (ffmpeg is null)
                return null;

            var besideFfmpeg = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
            return File.Exists(besideFfmpeg) ? besideFfmpeg : FindOnPath("ffprobe.exe");
        }
    }

    public async Task EnsureInstalledAsync(Action<string> report, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(toolsDirectory);

        if (!File.Exists(EnginePath))
        {
            report("Téléchargement du moteur IA Real-ESRGAN depuis GitHub…");
            await InstallRealEsrganAsync(report, cancellationToken);
        }

        await InstallModelAsync(GeneralModel, report, cancellationToken);
        await InstallModelAsync(AnimeModel, report, cancellationToken);

        if (CurrentFfmpegPath is null || CurrentFfprobePath is null)
        {
            report("Téléchargement de FFmpeg LGPL depuis les releases BtbN…");
            await InstallFfmpegAsync(report, cancellationToken);
        }
    }

    private async Task InstallRealEsrganAsync(Action<string> report, CancellationToken cancellationToken)
    {
        var release = await GetReleaseAsync(RealEsrganApi, cancellationToken);
        var asset = release.Assets.FirstOrDefault(item =>
            item.Name.Contains("windows", StringComparison.OrdinalIgnoreCase) &&
            item.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        if (asset is null)
            throw new InvalidOperationException("La release Real-ESRGAN ne contient pas d'archive Windows.");

        var download = await DownloadAssetAsync(asset.DownloadUrl, asset.Name, report, cancellationToken);
        var destination = Path.Combine(toolsDirectory, "realesrgan");
        try
        {
            ExtractSelectedEntries(download, destination, static name =>
            {
                var normalized = name.Replace('\\', '/');
                return normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                       normalized.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                       normalized.EndsWith(".param", StringComparison.OrdinalIgnoreCase) ||
                       normalized.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ||
                       normalized.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
            });
        }
        finally
        {
            File.Delete(download);
        }

        if (!File.Exists(EnginePath))
        {
            throw new InvalidDataException("L'archive Real-ESRGAN ne contient pas le moteur attendu.");
        }
    }

    private async Task InstallModelAsync(string modelName, Action<string> report, CancellationToken cancellationToken)
    {
        if (HasModel(modelName))
            return;

        report($"Téléchargement du modèle IA {modelName}…");
        var parameterFile = await DownloadAssetAsync(
            $"{ModelBaseUrl}{modelName}.param", $"{modelName}.param", report, cancellationToken);
        string? weightsFile = null;
        try
        {
            weightsFile = await DownloadAssetAsync(
                $"{ModelBaseUrl}{modelName}.bin", $"{modelName}.bin", report, cancellationToken);
            Directory.CreateDirectory(ModelDirectory);
            File.Move(parameterFile, Path.Combine(ModelDirectory, $"{modelName}.param"), overwrite: true);
            File.Move(weightsFile, Path.Combine(ModelDirectory, $"{modelName}.bin"), overwrite: true);
        }
        finally
        {
            File.Delete(parameterFile);
            if (weightsFile is not null)
                File.Delete(weightsFile);
        }
    }

    private bool HasModel(string modelName) =>
        File.Exists(Path.Combine(ModelDirectory, $"{modelName}.param")) &&
        File.Exists(Path.Combine(ModelDirectory, $"{modelName}.bin"));

    private async Task InstallFfmpegAsync(Action<string> report, CancellationToken cancellationToken)
    {
        var release = await GetReleaseAsync(FfmpegApi, cancellationToken);
        var asset = release.Assets.FirstOrDefault(item =>
            item.Name.Equals("ffmpeg-master-latest-win64-lgpl-shared.zip", StringComparison.OrdinalIgnoreCase));
        if (asset is null)
            throw new InvalidOperationException("La release FFmpeg ne contient pas l'archive Windows LGPL shared attendue.");

        var download = await DownloadAssetAsync(asset.DownloadUrl, asset.Name, report, cancellationToken);
        var destination = Path.Combine(toolsDirectory, "ffmpeg");
        try
        {
            ExtractSelectedEntries(download, destination, static name =>
            {
                var normalized = name.Replace('\\', '/');
                var binIndex = normalized.IndexOf("/bin/", StringComparison.OrdinalIgnoreCase);
                return binIndex >= 0 &&
                       normalized[(binIndex + 5)..].Length > 0 &&
                       !normalized[(binIndex + 5)..].Contains('/') &&
                       normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                       binIndex >= 0 &&
                       normalized[(binIndex + 5)..].Length > 0 &&
                       !normalized[(binIndex + 5)..].Contains('/') &&
                       normalized.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            });
        }
        finally
        {
            File.Delete(download);
        }

        if (!File.Exists(FfmpegPath) || !File.Exists(FfprobePath))
            throw new InvalidDataException("L'archive FFmpeg ne contient pas ffmpeg.exe et ffprobe.exe.");
    }

    private static async Task<GitHubRelease> GetReleaseAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken)
               ?? throw new InvalidDataException("Réponse GitHub invalide.");
    }

    private static async Task<string> DownloadAssetAsync(
        string url, string fileName, Action<string> report, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host is not ("github.com" or "objects.githubusercontent.com" or
                "release-assets.githubusercontent.com" or "raw.githubusercontent.com"))
        {
            throw new InvalidDataException("L'URL de téléchargement GitHub n'est pas autorisée.");
        }

        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var totalBytes = response.Content.Headers.ContentLength;
        var safeName = Path.GetFileName(fileName);
        var destination = Path.Combine(Path.GetTempPath(), $"upscalestudio-{Guid.NewGuid():N}-{safeName}");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[1024 * 128];
        long downloaded = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;
            if (totalBytes is > 0)
            {
                report($"Téléchargement de {safeName}… {downloaded / 1024 / 1024:N0} / {totalBytes.Value / 1024 / 1024:N0} Mo");
            }
        }

        return destination;
    }

    private static void ExtractSelectedEntries(string archivePath, string destination, Func<string, bool> select)
    {
        Directory.CreateDirectory(destination);
        var destinationRoot = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);

        foreach (var entry in archive.Entries.Where(item => select(item.FullName)))
        {
            var normalizedEntry = entry.FullName.Replace('\\', '/');
            var relativePath = normalizedEntry;
            var binIndex = normalizedEntry.IndexOf("/bin/", StringComparison.OrdinalIgnoreCase);

            if (binIndex >= 0 && normalizedEntry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                binIndex >= 0 && normalizedEntry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                relativePath = normalizedEntry[(binIndex + 5)..];
            }
            else if (normalizedEntry.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
            {
                relativePath = normalizedEntry;
            }
            else if (normalizedEntry.Contains('/'))
            {
                relativePath = Path.GetFileName(normalizedEntry);
            }

            var outputPath = Path.GetFullPath(Path.Combine(destination, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!outputPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("L'archive contient un chemin de fichier invalide.");

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using var source = entry.Open();
            using var target = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            source.CopyTo(target);
        }
    }

    private static string? FindOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;

        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), executable))
            .FirstOrDefault(File.Exists);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("UpscaleStudio4K", "0.1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("assets")] List<GitHubAsset> Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string DownloadUrl);
}

internal static class ProcessRunner
{
    public static async Task<string> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string? workingDirectory,
        Action<string>? output,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
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

        using var process = new Process { StartInfo = startInfo };
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is null)
                return;
            lock (stdout)
                stdout.AppendLine(eventArgs.Data);
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
