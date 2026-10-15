using StudentIdPhotoApp.Models;

namespace StudentIdPhotoApp.Services;

public interface IPhotoPipelineService
{
    Task<byte[]> ProcessPhotoAsync(byte[] inputBytes, ProcessingOptions options, CancellationToken cancellationToken = default);
}
