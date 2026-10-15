using StudentIdPhotoApp.Models;

namespace StudentIdPhotoApp.Services;

public interface IBatchProcessingService
{
    event Action<PhotoJobStatus>? OnJobUpdated;
    ValueTask EnqueueJobAsync(BatchPhotoJob job, CancellationToken cancellationToken = default);
    PhotoJobStatus? GetJobStatus(string jobId);
    IReadOnlyList<PhotoJobStatus> GetBatchStatus(string batchId);
    byte[]? GetJobResult(string jobId);
    bool TryGetBatchResults(string batchId, out IReadOnlyList<PhotoJobStatus> completedJobs);
}
