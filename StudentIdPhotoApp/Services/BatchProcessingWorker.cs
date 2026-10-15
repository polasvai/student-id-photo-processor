using System.Collections.Concurrent;
using System.Threading.Channels;
using StudentIdPhotoApp.Models;

namespace StudentIdPhotoApp.Services;

public class BatchProcessingWorker : BackgroundService, IBatchProcessingService
{
    private readonly IPhotoPipelineService _pipelineService;
    private readonly ILogger<BatchProcessingWorker> _logger;
    private readonly Channel<BatchPhotoJob> _channel;
    private readonly ConcurrentDictionary<string, PhotoJobStatus> _jobs = new();
    private readonly ConcurrentDictionary<string, ConcurrentBag<string>> _batches = new();

    public event Action<PhotoJobStatus>? OnJobUpdated;

    // Default max degree of parallelism = 2 or 3 to protect GPU/CPU RAM during heavy ONNX model inference
    private readonly int _maxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 3);

    public BatchProcessingWorker(IPhotoPipelineService pipelineService, ILogger<BatchProcessingWorker> logger)
    {
        _pipelineService = pipelineService;
        _logger = logger;

        var channelOptions = new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        };

        _channel = Channel.CreateBounded<BatchPhotoJob>(channelOptions);
    }

    public async ValueTask EnqueueJobAsync(BatchPhotoJob job, CancellationToken cancellationToken = default)
    {
        var status = new PhotoJobStatus
        {
            JobId = job.JobId,
            BatchId = job.BatchId,
            FileName = job.FileName,
            OriginalSizeBytes = job.InputBytes.Length,
            Status = PhotoJobState.Queued,
            StatusMessage = "Waiting in processing queue..."
        };

        _jobs[job.JobId] = status;
        _batches.GetOrAdd(job.BatchId, _ => new ConcurrentBag<string>()).Add(job.JobId);

        await _channel.Writer.WriteAsync(job, cancellationToken);
        _logger.LogInformation("Job {JobId} ({FileName}) enqueued into batch {BatchId}.", job.JobId, job.FileName, job.BatchId);
        OnJobUpdated?.Invoke(status);
    }

    public PhotoJobStatus? GetJobStatus(string jobId)
    {
        _jobs.TryGetValue(jobId, out var status);
        return status;
    }

    public IReadOnlyList<PhotoJobStatus> GetBatchStatus(string batchId)
    {
        if (!_batches.TryGetValue(batchId, out var jobIds))
        {
            return Array.Empty<PhotoJobStatus>();
        }

        var list = new List<PhotoJobStatus>();
        foreach (var id in jobIds.Distinct())
        {
            if (_jobs.TryGetValue(id, out var jobStatus))
            {
                list.Add(jobStatus);
            }
        }

        return list.OrderBy(j => j.CreatedAt).ToList();
    }

    public byte[]? GetJobResult(string jobId)
    {
        if (_jobs.TryGetValue(jobId, out var status) && status.Status == PhotoJobState.Completed)
        {
            return status.OutputBytes;
        }
        return null;
    }

    public bool TryGetBatchResults(string batchId, out IReadOnlyList<PhotoJobStatus> completedJobs)
    {
        var all = GetBatchStatus(batchId);
        completedJobs = all.Where(j => j.Status == PhotoJobState.Completed && j.OutputBytes != null).ToList();
        return completedJobs.Count > 0;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BatchProcessingWorker started with MaxDegreeOfParallelism = {Parallelism}", _maxDegreeOfParallelism);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = _maxDegreeOfParallelism,
            CancellationToken = stoppingToken
        };

        try
        {
            await Parallel.ForEachAsync(
                _channel.Reader.ReadAllAsync(stoppingToken),
                parallelOptions,
                async (job, token) =>
                {
                    await ProcessSingleJobAsync(job, token);
                });
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("BatchProcessingWorker stopping gracefully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in BatchProcessingWorker loop.");
        }
    }

    private async Task ProcessSingleJobAsync(BatchPhotoJob job, CancellationToken cancellationToken)
    {
        if (!_jobs.TryGetValue(job.JobId, out var status))
        {
            return;
        }

        status.Status = PhotoJobState.Processing;
        status.StatusMessage = "Processing face crop and background replacement...";
        _logger.LogInformation("Processing photo job {JobId} ({FileName}) ...", job.JobId, job.FileName);
        OnJobUpdated?.Invoke(status);

        try
        {
            var output = await _pipelineService.ProcessPhotoAsync(job.InputBytes, job.Options, cancellationToken);

            status.OutputBytes = output;
            status.OutputSizeBytes = output.Length;
            status.Status = PhotoJobState.Completed;
            status.StatusMessage = "Completed successfully";
            status.CompletedAt = DateTime.UtcNow;

            _logger.LogInformation("Job {JobId} ({FileName}) completed successfully ({Size:N0} bytes).",
                job.JobId, job.FileName, output.Length);

            OnJobUpdated?.Invoke(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process photo job {JobId} ({FileName}).", job.JobId, job.FileName);
            status.Status = PhotoJobState.Failed;
            status.ErrorMessage = ex.Message;
            status.StatusMessage = $"Failed: {ex.Message}";
            status.CompletedAt = DateTime.UtcNow;

            OnJobUpdated?.Invoke(status);
        }
        finally
        {
            // Immediately free input memory for large batch uploads
            job.InputBytes = Array.Empty<byte>();
        }
    }
}
