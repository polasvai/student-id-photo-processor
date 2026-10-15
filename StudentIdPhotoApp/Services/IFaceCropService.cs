using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using StudentIdPhotoApp.Models;

namespace StudentIdPhotoApp.Services;

public record FaceBoundingBox(float X, float Y, float Width, float Height, float Score);

public interface IFaceCropService
{
    FaceBoundingBox? DetectPrimaryFace(Image<Rgba32> image);
    Rectangle CalculateCropBox(int imgWidth, int imgHeight, FaceBoundingBox? face, ProcessingOptions options);
    Image<Rgba32> CropToStudentIdFrame(Image<Rgba32> sourceImage, ProcessingOptions options);
}
