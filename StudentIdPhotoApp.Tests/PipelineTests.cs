using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using StudentIdPhotoApp.Models;
using StudentIdPhotoApp.Services;

namespace StudentIdPhotoApp.Tests;

public class PipelineTests
{
    [Fact]
    public void Test_ProcessingOptions_Defaults_MatchSpec()
    {
        var options = new ProcessingOptions();

        Assert.Equal(300, options.TargetWidth);
        Assert.Equal(300, options.TargetHeight);
        Assert.Equal("#F4F4F4", options.BgColorHex);
        Assert.Equal(0.45, options.HeadroomRatio);
        Assert.Equal(2.35, options.FaceHeightMultiplier);
        Assert.Equal(300, options.TargetDpi);
        Assert.Equal(1.0, options.AspectRatio);
    }

    [Fact]
    public void Test_FaceCrop_FramingMath_CalculatesExpectedCoordinates()
    {
        var mockBootstrapper = new Mock<IOnnxModelBootstrapper>();
        var cropService = new FaceCropService(mockBootstrapper.Object, NullLogger<FaceCropService>.Instance);

        var face = new FaceBoundingBox(X: 100, Y: 100, Width: 100, Height: 100, Score: 0.95f);
        var options = new ProcessingOptions { TargetWidth = 300, TargetHeight = 300 };

        // Math:
        // CenterX = 100 + 50 = 150
        // CenterY = 100 + 50 = 150
        // HCrop = 100 * 2.35 = 235
        // WCrop = 235 * 1.0 = 235
        // Y1 = 100 - (100 * 0.45) = 55
        // X1 = 150 - (235 / 2) = 150 - 117.5 = 32.5 => 33
        var cropBox = cropService.CalculateCropBox(800, 800, face, options);

        Assert.Equal(235, cropBox.Height);
        Assert.Equal(235, cropBox.Width);
        Assert.Equal(55, cropBox.Y);
        Assert.True(cropBox.X >= 32 && cropBox.X <= 33);
    }

    [Fact]
    public void Test_FaceCrop_Fallback_WhenNoFaceDetected()
    {
        var mockBootstrapper = new Mock<IOnnxModelBootstrapper>();
        var cropService = new FaceCropService(mockBootstrapper.Object, NullLogger<FaceCropService>.Instance);

        var options = new ProcessingOptions { TargetWidth = 300, TargetHeight = 300 };
        var cropBox = cropService.CalculateCropBox(600, 800, null, options);

        // Portrait 600x800 -> target ratio 1.0 -> crop width 600, crop height 600
        Assert.Equal(600, cropBox.Width);
        Assert.Equal(600, cropBox.Height);
        Assert.Equal(0, cropBox.X);
        Assert.True(cropBox.Y >= 0);
    }

    [Fact]
    public async Task Test_PhotoPipeline_EndToEnd_GeneratesValidTargetImage()
    {
        // Path to the downloaded models in StudentIdPhotoApp
        var baseDir = AppContext.BaseDirectory;
        var solutionDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        var appDir = Path.Combine(solutionDir, "StudentIdPhotoApp");
        var modelsDir = Path.Combine(appDir, "wwwroot", "models");

        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.Setup(e => e.WebRootPath).Returns(Path.Combine(appDir, "wwwroot"));
        mockEnv.Setup(e => e.ContentRootPath).Returns(appDir);

        var bootstrapper = new OnnxModelBootstrapper(mockEnv.Object, NullLogger<OnnxModelBootstrapper>.Instance);
        var faceCropService = new FaceCropService(bootstrapper, NullLogger<FaceCropService>.Instance);
        var bgRemovalService = new BackgroundRemovalService(bootstrapper, NullLogger<BackgroundRemovalService>.Instance);
        var pipeline = new PhotoPipelineService(faceCropService, bgRemovalService, NullLogger<PhotoPipelineService>.Instance);

        // Create a synthetic test image with a circle representing a face & shoulders
        using var testImg = new Image<Rgba32>(600, 800, new Rgba32(50, 150, 220, 255)); // Blue background
        testImg.Mutate(ctx =>
        {
            // Draw head
            ctx.Fill(Color.SandyBrown, new SixLabors.ImageSharp.Drawing.EllipsePolygon(300, 300, 100, 130));
            // Draw body / shoulders
            ctx.Fill(Color.DarkBlue, new SixLabors.ImageSharp.Drawing.EllipsePolygon(300, 600, 220, 180));
        });

        using var ms = new MemoryStream();
        await testImg.SaveAsJpegAsync(ms);
        var inputBytes = ms.ToArray();

        var options = new ProcessingOptions
        {
            TargetWidth = 300,
            TargetHeight = 300,
            BgColorHex = "#F4F4F4",
            OutputJpeg = true,
            JpegQuality = 95
        };

        var outputBytes = await pipeline.ProcessPhotoAsync(inputBytes, options);

        Assert.NotNull(outputBytes);
        Assert.True(outputBytes.Length > 0);

        // Verify output image dimensions & DPI
        using var resultImg = Image.Load(outputBytes);
        Assert.Equal(300, resultImg.Width);
        Assert.Equal(300, resultImg.Height);
        Assert.Equal(300, (int)Math.Round(resultImg.Metadata.HorizontalResolution));
        Assert.Equal(300, (int)Math.Round(resultImg.Metadata.VerticalResolution));
    }

    [Fact]
    public void Test_Filename_Preserves_Exact_StudentID()
    {
        var mockBatchService = new Mock<IBatchProcessingService>();
        var mockBootstrapper = new Mock<IOnnxModelBootstrapper>();

        var studentIdFilename = "2023-CS-0492.jpg";
        var jobId = "test-job-123";
        var dummyBytes = new byte[] { 1, 2, 3, 4 };

        var jobStatus = new PhotoJobStatus
        {
            JobId = jobId,
            BatchId = "batch-1",
            FileName = studentIdFilename,
            Status = PhotoJobState.Completed,
            OutputBytes = dummyBytes
        };

        mockBatchService.Setup(b => b.GetJobStatus(jobId)).Returns(jobStatus);
        mockBatchService.Setup(b => b.GetJobResult(jobId)).Returns(dummyBytes);

        var controller = new Controllers.PhotoBatchController(
            mockBatchService.Object,
            mockBootstrapper.Object,
            NullLogger<Controllers.PhotoBatchController>.Instance);

        var singleResult = controller.DownloadSingle(jobId) as Microsoft.AspNetCore.Mvc.FileContentResult;
        Assert.NotNull(singleResult);
        // FileDownloadName MUST be the exact student ID filename
        Assert.Equal(studentIdFilename, singleResult.FileDownloadName);

        // Also verify ZIP entry preserves the student ID filename
        IReadOnlyList<PhotoJobStatus> completedList = new List<PhotoJobStatus> { jobStatus };
        mockBatchService.Setup(b => b.TryGetBatchResults("batch-1", out completedList)).Returns(true);

        var zipResult = controller.DownloadBatchZip("batch-1") as Microsoft.AspNetCore.Mvc.FileStreamResult;
        Assert.NotNull(zipResult);
        using var zipArchive = new System.IO.Compression.ZipArchive(zipResult.FileStream);
        Assert.Single(zipArchive.Entries);
        Assert.Equal(studentIdFilename, zipArchive.Entries[0].FullName);
    }
}
