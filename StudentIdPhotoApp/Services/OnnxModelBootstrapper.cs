namespace StudentIdPhotoApp.Services;

public class OnnxModelBootstrapper : IOnnxModelBootstrapper, IHostedService
{
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<OnnxModelBootstrapper> _logger;
    private readonly HttpClient _httpClient;

    private readonly string _modelsDir;
    private readonly string _faceModelPath;
    private readonly string _bgModelPath;

    private readonly (string Name, string Path, string[] Urls, long MinExpectedSize)[] _models;

    public string FaceModelPath => _faceModelPath;
    public string BgRemovalModelPath => _bgModelPath;
    public bool AreModelsReady { get; private set; }

    public OnnxModelBootstrapper(IWebHostEnvironment env, ILogger<OnnxModelBootstrapper> logger, HttpClient? httpClient = null)
    {
        _env = env;
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        var webRoot = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
        _modelsDir = Path.Combine(webRoot, "models");

        _faceModelPath = Path.Combine(_modelsDir, "face_detection_yunet_2023mar.onnx");
        _bgModelPath = Path.Combine(_modelsDir, "rmbg-1.4.onnx");

        _models = new[]
        {
            (
                "Face Detection (YuNet)",
                _faceModelPath,
                new[]
                {
                    "https://github.com/opencv/opencv_zoo/raw/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx",
                    "https://huggingface.co/opencv/face_detection_yunet/resolve/main/face_detection_yunet_2023mar.onnx"
                },
                200_000L // ~232 KB
            ),
            (
                "Background Removal (RMBG-1.4)",
                _bgModelPath,
                new[]
                {
                    "https://huggingface.co/briaai/RMBG-1.4/resolve/main/onnx/model.onnx",
                    "https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2netp.onnx" // Lightweight fallback if RMBG fails
                },
                4_000_000L // at least 4MB (u2netp is 4.5MB, RMBG is 176MB)
            )
        };
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting ONNX model verification in background...");
        // Run in background task so application startup is not blocked if downloading models
        _ = Task.Run(async () =>
        {
            try
            {
                await EnsureModelsAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background ONNX model initialization failed.");
            }
        }, cancellationToken);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task EnsureModelsAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_modelsDir);

        foreach (var (name, targetPath, urls, minSize) in _models)
        {
            if (File.Exists(targetPath))
            {
                var fileInfo = new FileInfo(targetPath);
                if (fileInfo.Length >= minSize)
                {
                    _logger.LogInformation("Model '{Name}' already exists at {Path} ({Size:N0} bytes).", name, targetPath, fileInfo.Length);
                    continue;
                }
                else
                {
                    _logger.LogWarning("Model '{Name}' file is incomplete ({Size} bytes). Re-downloading...", name, fileInfo.Length);
                    try { File.Delete(targetPath); } catch { /* ignore */ }
                }
            }

            var downloaded = false;
            foreach (var url in urls)
            {
                try
                {
                    _logger.LogInformation("Downloading model '{Name}' from {Url} ...", name, url);
                    await DownloadFileAsync(url, targetPath, cancellationToken);
                    downloaded = true;
                    _logger.LogInformation("Successfully downloaded '{Name}' to {Path}.", name, targetPath);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed downloading '{Name}' from {Url}. Trying next mirror if available...", name, url);
                }
            }

            if (!downloaded)
            {
                throw new InvalidOperationException($"Unable to download model '{name}'. Please verify internet connection or place file at '{targetPath}'.");
            }
        }

        AreModelsReady = true;
        _logger.LogInformation("All required ONNX models are ready.");
    }

    private async Task DownloadFileAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        var tempFile = destinationPath + ".tmp";
        if (File.Exists(tempFile))
        {
            try { File.Delete(tempFile); } catch { }
        }

        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;
        var lastLogTime = DateTime.UtcNow;

        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalRead += bytesRead;

            if ((DateTime.UtcNow - lastLogTime).TotalSeconds >= 3)
            {
                lastLogTime = DateTime.UtcNow;
                if (totalBytes > 0)
                {
                    var pct = (double)totalRead / totalBytes * 100.0;
                    _logger.LogInformation("Download progress: {Pct:F1}% ({Read:N0} / {Total:N0} bytes)", pct, totalRead, totalBytes);
                }
                else
                {
                    _logger.LogInformation("Download progress: {Read:N0} bytes", totalRead);
                }
            }
        }

        await fileStream.FlushAsync(cancellationToken);
        fileStream.Close();

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        File.Move(tempFile, destinationPath);
    }
}
