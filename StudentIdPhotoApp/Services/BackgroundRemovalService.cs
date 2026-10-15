using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace StudentIdPhotoApp.Services;

public class BackgroundRemovalService : IBackgroundRemovalService, IDisposable
{
    private readonly IOnnxModelBootstrapper _bootstrapper;
    private readonly ILogger<BackgroundRemovalService> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private InferenceSession? _session;
    private int _modelInputWidth = 1024;
    private int _modelInputHeight = 1024;
    private string? _inputName;
    private string? _outputName;

    public BackgroundRemovalService(IOnnxModelBootstrapper bootstrapper, ILogger<BackgroundRemovalService> logger)
    {
        _bootstrapper = bootstrapper;
        _logger = logger;
    }

    private async Task<InferenceSession?> GetSessionAsync(CancellationToken cancellationToken)
    {
        if (_session != null) return _session;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_session != null) return _session;

            var modelPath = _bootstrapper.BgRemovalModelPath;
            if (!File.Exists(modelPath))
            {
                _logger.LogInformation("Background removal model not yet downloaded. Awaiting bootstrapper...");
                await _bootstrapper.EnsureModelsAsync(cancellationToken);
            }

            if (!File.Exists(modelPath))
            {
                _logger.LogWarning("Model file missing at {Path}. Background removal will fallback to pass-through.", modelPath);
                return null;
            }

            _logger.LogInformation("Loading ONNX Background Removal Session from {Path} ...", modelPath);
            var sessionOptions = new Microsoft.ML.OnnxRuntime.SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                InterOpNumThreads = Environment.ProcessorCount,
                IntraOpNumThreads = Environment.ProcessorCount
            };

            _session = new InferenceSession(modelPath, sessionOptions);

            // Discover input & output names and expected shapes
            var inputMeta = _session.InputMetadata;
            _inputName = inputMeta.Keys.First();
            var inputDims = inputMeta[_inputName].Dimensions;
            if (inputDims != null && inputDims.Length >= 4 && inputDims[2] > 0 && inputDims[3] > 0)
            {
                _modelInputHeight = inputDims[2];
                _modelInputWidth = inputDims[3];
            }
            else
            {
                // Default to 1024 for RMBG-1.4 or 320 for U2Net
                var fi = new FileInfo(modelPath);
                if (fi.Length < 10_000_000) // u2netp is ~4.5MB
                {
                    _modelInputWidth = 320;
                    _modelInputHeight = 320;
                }
                else
                {
                    _modelInputWidth = 1024;
                    _modelInputHeight = 1024;
                }
            }

            var outputMeta = _session.OutputMetadata;
            _outputName = outputMeta.Keys.First();

            _logger.LogInformation("ONNX Session loaded successfully. Input: '{Input}' [{H}x{W}], Output: '{Output}'",
                _inputName, _modelInputHeight, _modelInputWidth, _outputName);

            return _session;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize ONNX InferenceSession for background removal.");
            return null;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<Image<L8>> GenerateMaskAsync(Image<Rgba32> croppedImage, CancellationToken cancellationToken = default)
    {
        var session = await GetSessionAsync(cancellationToken);
        if (session == null || _inputName == null || _outputName == null)
        {
            _logger.LogWarning("ONNX session unavailable. Returning fallback solid mask.");
            return new Image<L8>(croppedImage.Width, croppedImage.Height, new L8(255));
        }

        // 1. Pre-processing: Resize copy of cropped image to model input size
        using var resized = croppedImage.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new SixLabors.ImageSharp.Size(_modelInputWidth, _modelInputHeight),
            Sampler = KnownResamplers.Bicubic,
            Mode = ResizeMode.Stretch
        }));

        // 2. Normalize pixel values to float tensor [1, 3, H, W]
        var tensor = new DenseTensor<float>(new[] { 1, 3, _modelInputHeight, _modelInputWidth });

        resized.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < accessor.Width; x++)
                {
                    ref readonly var pixel = ref row[x];
                    // RMBG/U2Net normalization: (pixel / 255.0 - 0.5) / 1.0
                    tensor[0, 0, y, x] = (pixel.R / 255f - 0.5f) / 1.0f;
                    tensor[0, 1, y, x] = (pixel.G / 255f - 0.5f) / 1.0f;
                    tensor[0, 2, y, x] = (pixel.B / 255f - 0.5f) / 1.0f;
                }
            }
        });

        // 3. Run Inference (offload to thread pool for async pipeline)
        var inputContainer = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputName, tensor)
        };

        var (outH, outW, buffer) = await Task.Run(() =>
        {
            using var results = session.Run(inputContainer);
            var output = results.First(r => r.Name == _outputName);
            var outTensor = output.AsTensor<float>();
            var dims = outTensor.Dimensions;
            var h = dims[^2];
            var w = dims[^1];
            var arr = outTensor.ToArray();
            return (h, w, arr);
        }, cancellationToken);

        // 4. Post-processing: Min-Max scaling to [0, 255]
        var minVal = float.MaxValue;
        var maxVal = float.MinValue;

        for (var i = 0; i < buffer.Length; i++)
        {
            var val = buffer[i];
            if (val < minVal) minVal = val;
            if (val > maxVal) maxVal = val;
        }

        var range = maxVal - minVal;
        if (range <= 1e-6f) range = 1.0f;

        var mask = new Image<L8>(outW, outH);
        mask.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < outH; y++)
            {
                var row = accessor.GetRowSpan(y);
                var rowOffset = y * outW;
                for (var x = 0; x < outW; x++)
                {
                    var rawVal = buffer[rowOffset + x];
                    var norm = (rawVal - minVal) / range;
                    norm = Math.Clamp(norm, 0f, 1f);
                    row[x] = new L8((byte)Math.Round(norm * 255f));
                }
            }
        });

        // Resize mask back to exact dimensions of the cropped image using Bicubic interpolation
        if (mask.Width != croppedImage.Width || mask.Height != croppedImage.Height)
        {
            mask.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new SixLabors.ImageSharp.Size(croppedImage.Width, croppedImage.Height),
                Sampler = KnownResamplers.Bicubic,
                Mode = ResizeMode.Stretch
            }));
        }

        return mask;
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
        _initLock.Dispose();
    }
}
