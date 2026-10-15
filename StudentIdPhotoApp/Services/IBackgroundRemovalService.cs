using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace StudentIdPhotoApp.Services;

public interface IBackgroundRemovalService
{
    Task<Image<L8>> GenerateMaskAsync(Image<Rgba32> croppedImage, CancellationToken cancellationToken = default);
}
