using System.Text.Json.Serialization;

namespace StudentIdPhotoApp.Models;

public enum PhotoJobState
{
    Queued,
    Processing,
    Completed,
    Failed
}

public class PhotoJobStatus
{
    public string JobId { get; set; } = Guid.NewGuid().ToString("N");
    public string BatchId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PhotoJobState Status { get; set; } = PhotoJobState.Queued;

    public string? ErrorMessage { get; set; }
    public string? StatusMessage { get; set; }
    public long OriginalSizeBytes { get; set; }
    public long OutputSizeBytes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    // Preview URL or data URI for frontend thumbnail
    public string? PreviewUrl => Status == PhotoJobState.Completed 
        ? $"/api/batch/preview/{JobId}" 
        : null;

    [JsonIgnore]
    public byte[]? OutputBytes { get; set; }
}
