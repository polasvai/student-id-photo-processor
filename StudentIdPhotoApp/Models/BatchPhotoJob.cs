namespace StudentIdPhotoApp.Models;

public class BatchPhotoJob
{
    public string JobId { get; set; } = Guid.NewGuid().ToString("N");
    public string BatchId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public byte[] InputBytes { get; set; } = Array.Empty<byte>();
    public ProcessingOptions Options { get; set; } = new();
}
