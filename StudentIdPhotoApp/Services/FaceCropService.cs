using OpenCvSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using StudentIdPhotoApp.Models;
using Point = SixLabors.ImageSharp.Point;
using Size = OpenCvSharp.Size;

namespace StudentIdPhotoApp.Services;

public class FaceCropService : IFaceCropService, IDisposable
{
    private readonly IOnnxModelBootstrapper _bootstrapper;
    private readonly ILogger<FaceCropService> _logger;
    private readonly object _lock = new();
    private FaceDetectorYN? _detector;
    private string? _loadedModelPath;
    private int _lastWidth;
    private int _lastHeight;

    public FaceCropService(IOnnxModelBootstrapper bootstrapper, ILogger<FaceCropService> logger)
    {
        _bootstrapper = bootstrapper;
        _logger = logger;
    }

    private FaceDetectorYN? GetDetector(int width, int height)
    {
        lock (_lock)
        {
            var modelPath = _bootstrapper.FaceModelPath;
            if (!File.Exists(modelPath))
            {
                _logger.LogWarning("YuNet model file not found at {Path}. Face detection will use fallback.", modelPath);
                return null;
            }

            try
            {
                if (_detector == null || _loadedModelPath != modelPath || _lastWidth != width || _lastHeight != height)
                {
                    _detector?.Dispose();
                    // Score threshold: 0.6, NMS threshold: 0.3, topK: 5000
                    _detector = FaceDetectorYN.Create(modelPath, "", new Size(width, height), 0.6f, 0.3f, 5000);
                    _loadedModelPath = modelPath;
                    _lastWidth = width;
                    _lastHeight = height;
                }

                return _detector;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize FaceDetectorYN. Will use fallback.");
                return null;
            }
        }
    }

    public FaceBoundingBox? DetectPrimaryFace(Image<Rgba32> image)
    {
        // For performance and detector stability on very high-res mobile photos (e.g. 4000x3000),
        // we scale the detection image if max dimension > 1280
        var maxDim = Math.Max(image.Width, image.Height);
        var scale = 1.0;
        var detectWidth = image.Width;
        var detectHeight = image.Height;

        if (maxDim > 1280)
        {
            scale = 1280.0 / maxDim;
            detectWidth = (int)Math.Round(image.Width * scale);
            detectHeight = (int)Math.Round(image.Height * scale);
        }

        var detector = GetDetector(detectWidth, detectHeight);
        if (detector == null)
        {
            return null;
        }

        try
        {
            using var matBgr = ConvertImageSharpToMatBgr(image, detectWidth, detectHeight);
            using var facesMat = new Mat();

            lock (_lock)
            {
                detector.Detect(matBgr, facesMat);
            }

            if (facesMat.Rows == 0)
            {
                _logger.LogInformation("No faces detected by YuNet. Falling back to top-weighted framing.");
                return null;
            }

            FaceBoundingBox? bestFace = null;
            var maxArea = 0f;

            for (var i = 0; i < facesMat.Rows; i++)
            {
                var x = facesMat.At<float>(i, 0);
                var y = facesMat.At<float>(i, 1);
                var w = facesMat.At<float>(i, 2);
                var h = facesMat.At<float>(i, 3);
                var score = facesMat.At<float>(i, 14);

                if (score < 0.5f || w <= 0 || h <= 0) continue;

                // Scale coordinates back to original image dimensions
                if (scale < 1.0)
                {
                    x = (float)(x / scale);
                    y = (float)(y / scale);
                    w = (float)(w / scale);
                    h = (float)(h / scale);
                }

                var area = w * h;
                // Prioritize the largest/most prominent face in the frame
                if (area > maxArea)
                {
                    maxArea = area;
                    bestFace = new FaceBoundingBox(x, y, w, h, score);
                }
            }

            if (bestFace != null)
            {
                _logger.LogInformation("Detected primary face at ({X:F0},{Y:F0}) size {W:F0}x{H:F0} (score: {Score:F2})", 
                    bestFace.X, bestFace.Y, bestFace.Width, bestFace.Height, bestFace.Score);
            }

            return bestFace;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during face detection inference.");
            return null;
        }
    }

    public Rectangle CalculateCropBox(int imgWidth, int imgHeight, FaceBoundingBox? face, ProcessingOptions options)
    {
        var targetRatio = options.AspectRatio;

        if (face == null)
        {
            // Fallback: top-weighted center crop
            return CalculateTopWeightedFallback(imgWidth, imgHeight, targetRatio);
        }

        // Spec math:
        // Center_X = x + w/2, Center_Y = y + h/2
        var centerX = face.X + face.Width / 2.0f;
        var centerY = face.Y + face.Height / 2.0f;

        // Height_crop = h * 2.35
        var hCrop = (float)(face.Height * options.FaceHeightMultiplier);
        // Width_crop = H_crop * R
        var wCrop = (float)(hCrop * targetRatio);

        // Top position guaranteeing headroom above hair: Y1 = y - (h * 0.45)
        var y1 = (float)(face.Y - (face.Height * options.HeadroomRatio));
        var x1 = centerX - (wCrop / 2.0f);

        return new Rectangle((int)Math.Round(x1), (int)Math.Round(y1), (int)Math.Round(wCrop), (int)Math.Round(hCrop));
    }

    private static Rectangle CalculateTopWeightedFallback(int imgWidth, int imgHeight, double targetRatio)
    {
        var imgRatio = (double)imgWidth / imgHeight;
        int cropWidth, cropHeight, x, y;

        if (imgRatio > targetRatio)
        {
            // Image is wider than target aspect ratio
            cropHeight = imgHeight;
            cropWidth = (int)Math.Round(cropHeight * targetRatio);
            x = (imgWidth - cropWidth) / 2;
            y = 0;
        }
        else
        {
            // Image is taller than target aspect ratio
            cropWidth = imgWidth;
            cropHeight = (int)Math.Round(cropWidth / targetRatio);
            x = 0;
            // Place towards the top (10% down or at 0) to preserve head & shoulders
            var remaining = imgHeight - cropHeight;
            y = (int)Math.Round(remaining * 0.15);
            if (y + cropHeight > imgHeight) y = imgHeight - cropHeight;
            if (y < 0) y = 0;
        }

        return new Rectangle(x, y, cropWidth, cropHeight);
    }

    public Image<Rgba32> CropToStudentIdFrame(Image<Rgba32> sourceImage, ProcessingOptions options)
    {
        var face = DetectPrimaryFace(sourceImage);
        var cropRect = CalculateCropBox(sourceImage.Width, sourceImage.Height, face, options);

        // Safe framing: if crop box is completely within the source image bounds
        if (cropRect.X >= 0 && cropRect.Y >= 0 &&
            cropRect.Right <= sourceImage.Width && cropRect.Bottom <= sourceImage.Height)
        {
            return sourceImage.Clone(ctx => ctx.Crop(cropRect));
        }

        // If the box fits within the dimensions but just needs shifting
        if (cropRect.Width <= sourceImage.Width && cropRect.Height <= sourceImage.Height)
        {
            var shiftedX = Math.Clamp(cropRect.X, 0, sourceImage.Width - cropRect.Width);
            var shiftedY = Math.Clamp(cropRect.Y, 0, sourceImage.Height - cropRect.Height);
            return sourceImage.Clone(ctx => ctx.Crop(new Rectangle(shiftedX, shiftedY, cropRect.Width, cropRect.Height)));
        }

        // If the subject is close to the edge or the target crop box exceeds source bounds:
        // Pad safely with the configured background color
        var bgColor = ParseRgbaColor(options.BgColorHex);
        var paddedCanvas = new Image<Rgba32>(cropRect.Width, cropRect.Height, bgColor);

        // Determine intersection between source image and target crop rectangle
        var srcX = Math.Max(0, cropRect.X);
        var srcY = Math.Max(0, cropRect.Y);
        var srcRight = Math.Min(sourceImage.Width, cropRect.Right);
        var srcBottom = Math.Min(sourceImage.Height, cropRect.Bottom);

        var copyWidth = srcRight - srcX;
        var copyHeight = srcBottom - srcY;

        if (copyWidth > 0 && copyHeight > 0)
        {
            using var subCrop = sourceImage.Clone(ctx => ctx.Crop(new Rectangle(srcX, srcY, copyWidth, copyHeight)));
            var destX = srcX - cropRect.X;
            var destY = srcY - cropRect.Y;
            paddedCanvas.Mutate(ctx => ctx.DrawImage(subCrop, new Point(destX, destY), 1.0f));
        }

        return paddedCanvas;
    }

    private static Mat ConvertImageSharpToMatBgr(Image<Rgba32> image, int targetWidth, int targetHeight)
    {
        using var clone = image.Clone(ctx =>
        {
            if (image.Width != targetWidth || image.Height != targetHeight)
            {
                ctx.Resize(targetWidth, targetHeight);
            }
        });

        var pixelBytes = new byte[targetWidth * targetHeight * 4];
        clone.CopyPixelDataTo(pixelBytes);

        using var matRgba = Mat.FromPixelData(targetHeight, targetWidth, MatType.CV_8UC4, pixelBytes);
        var matBgr = new Mat();
        Cv2.CvtColor(matRgba, matBgr, ColorConversionCodes.RGBA2BGR);
        return matBgr;
    }

    private static Rgba32 ParseRgbaColor(string hex)
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

    public void Dispose()
    {
        lock (_lock)
        {
            _detector?.Dispose();
            _detector = null;
        }
    }
}
