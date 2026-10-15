using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using StudentIdPhotoApp.Models;

namespace StudentIdPhotoApp.Services;

public class PhotoPipelineService : IPhotoPipelineService
{
    private readonly IFaceCropService _faceCropService;
    private readonly IBackgroundRemovalService _bgRemovalService;
    private readonly ILogger<PhotoPipelineService> _logger;

    public PhotoPipelineService(
        IFaceCropService faceCropService,
        IBackgroundRemovalService bgRemovalService,
        ILogger<PhotoPipelineService> logger)
    {
        _faceCropService = faceCropService;
        _bgRemovalService = bgRemovalService;
        _logger = logger;
    }

    public async Task<byte[]> ProcessPhotoAsync(byte[] inputBytes, ProcessingOptions options, CancellationToken cancellationToken = default)
    {
        if (inputBytes == null || inputBytes.Length == 0)
        {
            throw new ArgumentException("Input bytes cannot be null or empty.", nameof(inputBytes));
        }

        // Step 2: Image Loading & EXIF Normalization
        using var inputStream = new MemoryStream(inputBytes);
        using var originalImage = await Image.LoadAsync<Rgba32>(inputStream, cancellationToken);
        
        // Fix orientation from mobile camera EXIF metadata
        originalImage.Mutate(ctx => ctx.AutoOrient());

        _logger.LogInformation("Image loaded successfully. Size: {W}x{H}", originalImage.Width, originalImage.Height);

        // Step 3: Smart Face & Shoulder Cropping
        using var croppedImage = _faceCropService.CropToStudentIdFrame(originalImage, options);

        // Step 4: Background Removal / Mask Generation
        using var mask = await _bgRemovalService.GenerateMaskAsync(croppedImage, cancellationToken);

        // Step 5: Resize both to target dimensions using high quality Lanczos3
        var targetSize = new SixLabors.ImageSharp.Size(options.TargetWidth, options.TargetHeight);
        var resizeOptions = new ResizeOptions
        {
            Size = targetSize,
            Sampler = KnownResamplers.Lanczos3,
            Mode = ResizeMode.Stretch
        };

        if (croppedImage.Width != options.TargetWidth || croppedImage.Height != options.TargetHeight)
        {
            croppedImage.Mutate(ctx => ctx.Resize(resizeOptions));
        }

        if (mask.Width != options.TargetWidth || mask.Height != options.TargetHeight)
        {
            mask.Mutate(ctx => ctx.Resize(resizeOptions));
        }

        // Solid Background Replacement with zero-allocation ProcessPixelRows
        var bgColor = ParseColor(options.BgColorHex);
        var bgR = bgColor.R;
        var bgG = bgColor.G;
        var bgB = bgColor.B;

        using var compositedImage = new Image<Rgba32>(options.TargetWidth, options.TargetHeight);

        compositedImage.ProcessPixelRows(croppedImage, mask, (outAccessor, fgAccessor, maskAccessor) =>
        {
            for (var y = 0; y < outAccessor.Height; y++)
            {
                var outRow = outAccessor.GetRowSpan(y);
                var fgRow = fgAccessor.GetRowSpan(y);
                var maskRow = maskAccessor.GetRowSpan(y);

                for (var x = 0; x < outAccessor.Width; x++)
                {
                    var alpha = maskRow[x].PackedValue / 255.0f;
                    ref readonly var fgPixel = ref fgRow[x];

                    var r = (byte)Math.Clamp(Math.Round(fgPixel.R * alpha + bgR * (1.0f - alpha)), 0, 255);
                    var g = (byte)Math.Clamp(Math.Round(fgPixel.G * alpha + bgG * (1.0f - alpha)), 0, 255);
                    var b = (byte)Math.Clamp(Math.Round(fgPixel.B * alpha + bgB * (1.0f - alpha)), 0, 255);

                    outRow[x] = new Rgba32(r, g, b, 255);
                }
            }
        });

        // Apply DPI resolution metadata (Standard ID print quality e.g., 300 DPI)
        if (options.TargetDpi > 0)
        {
            compositedImage.Metadata.HorizontalResolution = options.TargetDpi;
            compositedImage.Metadata.VerticalResolution = options.TargetDpi;
            compositedImage.Metadata.ResolutionUnits = SixLabors.ImageSharp.Metadata.PixelResolutionUnit.PixelsPerInch;
        }

        // Encode as high-quality JPEG or PNG
        using var outputStream = new MemoryStream();
        if (options.OutputJpeg)
        {
            var quality = Math.Clamp(options.JpegQuality, 50, 100);
            var encoder = new JpegEncoder { Quality = quality };
            await compositedImage.SaveAsJpegAsync(outputStream, encoder, cancellationToken);
        }
        else
        {
            var encoder = new PngEncoder();
            await compositedImage.SaveAsPngAsync(outputStream, encoder, cancellationToken);
        }

        return outputStream.ToArray();
    }

    private static Rgba32 ParseColor(string hex)
    {
        try
        {
            return Color.ParseHex(hex).ToPixel<Rgba32>();
        }
        catch
        {
            return new Rgba32(244, 244, 244, 255);
        }
    }
}
