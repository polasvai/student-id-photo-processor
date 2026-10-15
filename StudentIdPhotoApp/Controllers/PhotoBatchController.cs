using System.IO.Compression;
using Microsoft.AspNetCore.Mvc;
using StudentIdPhotoApp.Models;
using StudentIdPhotoApp.Services;

namespace StudentIdPhotoApp.Controllers;

[ApiController]
[Route("api/batch")]
public class PhotoBatchController : ControllerBase
{
    private readonly IBatchProcessingService _batchService;
    private readonly IOnnxModelBootstrapper _bootstrapper;
    private readonly ILogger<PhotoBatchController> _logger;

    public PhotoBatchController(
        IBatchProcessingService batchService,
        IOnnxModelBootstrapper bootstrapper,
        ILogger<PhotoBatchController> logger)
    {
        _batchService = batchService;
        _bootstrapper = bootstrapper;
        _logger = logger;
    }

    [HttpGet("models/status")]
    public IActionResult GetModelStatus()
    {
        return Ok(new
        {
            ready = _bootstrapper.AreModelsReady,
            faceModelExists = System.IO.File.Exists(_bootstrapper.FaceModelPath),
            bgModelExists = System.IO.File.Exists(_bootstrapper.BgRemovalModelPath)
        });
    }

    [HttpPost("upload")]
    [RequestSizeLimit(500 * 1024 * 1024)] // 500 MB max batch upload
    public async Task<IActionResult> UploadBatch(
        [FromForm] List<IFormFile> files,
        [FromForm] int? targetWidth,
        [FromForm] int? targetHeight,
        [FromForm] string? bgColorHex,
        [FromForm] bool? outputJpeg,
        [FromForm] int? jpegQuality,
        [FromForm] int? targetDpi,
        [FromForm] double? headroomRatio,
        [FromForm] double? faceHeightMultiplier)
    {
        if (files == null || files.Count == 0)
        {
            return BadRequest(new { error = "No files uploaded." });
        }

        var batchId = Guid.NewGuid().ToString("N")[..12];
        var options = new ProcessingOptions
        {
            TargetWidth = targetWidth is > 0 ? targetWidth.Value : 300,
            TargetHeight = targetHeight is > 0 ? targetHeight.Value : 300,
            BgColorHex = string.IsNullOrWhiteSpace(bgColorHex) ? "#F4F4F4" : bgColorHex.Trim(),
            OutputJpeg = outputJpeg ?? true,
            JpegQuality = jpegQuality is > 0 ? jpegQuality.Value : 95,
            TargetDpi = targetDpi is > 0 ? targetDpi.Value : 300,
            HeadroomRatio = headroomRatio is > 0 ? headroomRatio.Value : 0.45,
            FaceHeightMultiplier = faceHeightMultiplier is > 0 ? faceHeightMultiplier.Value : 2.35
        };

        var enqueuedJobs = new List<object>();

        foreach (var file in files)
        {
            if (file.Length == 0) continue;

            using var memoryStream = new MemoryStream();
            await file.CopyToAsync(memoryStream);

            var job = new BatchPhotoJob
            {
                JobId = Guid.NewGuid().ToString("N")[..16],
                BatchId = batchId,
                FileName = Path.GetFileName(file.FileName),
                InputBytes = memoryStream.ToArray(),
                Options = options
            };

            await _batchService.EnqueueJobAsync(job);

            enqueuedJobs.Add(new
            {
                jobId = job.JobId,
                fileName = job.FileName,
                sizeBytes = file.Length
            });
        }

        _logger.LogInformation("Batch {BatchId} created with {Count} files.", batchId, enqueuedJobs.Count);

        return Ok(new
        {
            batchId,
            totalFiles = enqueuedJobs.Count,
            options,
            jobs = enqueuedJobs
        });
    }

    [HttpGet("status/{batchId}")]
    public IActionResult GetStatus(string batchId)
    {
        var jobs = _batchService.GetBatchStatus(batchId);
        if (jobs.Count == 0)
        {
            return NotFound(new { error = $"Batch '{batchId}' not found." });
        }

        var total = jobs.Count;
        var completed = jobs.Count(j => j.Status == PhotoJobState.Completed);
        var failed = jobs.Count(j => j.Status == PhotoJobState.Failed);
        var processing = jobs.Count(j => j.Status == PhotoJobState.Processing);
        var queued = jobs.Count(j => j.Status == PhotoJobState.Queued);
        var isFinished = (completed + failed) == total;

        return Ok(new
        {
            batchId,
            total,
            completed,
            failed,
            processing,
            queued,
            isFinished,
            progressPercent = total > 0 ? Math.Round((double)(completed + failed) / total * 100.0, 1) : 0,
            jobs = jobs.Select(j => new
            {
                j.JobId,
                j.FileName,
                status = j.Status.ToString(),
                j.StatusMessage,
                j.ErrorMessage,
                j.OriginalSizeBytes,
                j.OutputSizeBytes,
                j.CreatedAt,
                j.CompletedAt,
                previewUrl = j.PreviewUrl,
                downloadUrl = $"/api/batch/download-single/{j.JobId}"
            })
        });
    }

    [HttpGet("preview/{jobId}")]
    public IActionResult GetPreview(string jobId)
    {
        var bytes = _batchService.GetJobResult(jobId);
        if (bytes == null || bytes.Length == 0)
        {
            return NotFound();
        }

        return File(bytes, "image/jpeg");
    }

    [HttpGet("download-single/{jobId}")]
    public IActionResult DownloadSingle(string jobId)
    {
        var status = _batchService.GetJobStatus(jobId);
        var bytes = _batchService.GetJobResult(jobId);
        if (status == null || bytes == null || bytes.Length == 0)
        {
            return NotFound(new { error = "Photo not ready or job not found." });
        }

        // Preserve exact original filename (which represents the Student ID)
        var originalFileName = Path.GetFileName(status.FileName);
        if (string.IsNullOrWhiteSpace(originalFileName))
        {
            originalFileName = $"{status.JobId}.jpg";
        }

        return File(bytes, "application/octet-stream", originalFileName);
    }

    [HttpGet("download/{batchId}")]
    public IActionResult DownloadBatchZip(string batchId)
    {
        if (!_batchService.TryGetBatchResults(batchId, out var completedJobs) || completedJobs.Count == 0)
        {
            return NotFound(new { error = "No completed photos found for this batch." });
        }

        var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var job in completedJobs)
            {
                if (job.OutputBytes == null) continue;

                // Keep exact original filename to preserve student ID
                var entryName = Path.GetFileName(job.FileName);
                if (string.IsNullOrWhiteSpace(entryName))
                {
                    entryName = $"{job.JobId}.jpg";
                }

                // If identical duplicate filenames were uploaded, disambiguate safely
                var uniqueName = entryName;
                if (usedNames.Contains(uniqueName))
                {
                    var baseName = Path.GetFileNameWithoutExtension(entryName);
                    var ext = Path.GetExtension(entryName);
                    var counter = 1;
                    while (usedNames.Contains(uniqueName))
                    {
                        uniqueName = $"{baseName}_{counter++}{ext}";
                    }
                }
                usedNames.Add(uniqueName);

                var entry = archive.CreateEntry(uniqueName, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                entryStream.Write(job.OutputBytes, 0, job.OutputBytes.Length);
            }
        }

        zipStream.Position = 0;
        var zipFileName = $"StudentID_Photos_{batchId}.zip";
        return File(zipStream, "application/zip", zipFileName);
    }
}
