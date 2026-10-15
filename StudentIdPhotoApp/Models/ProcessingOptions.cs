namespace StudentIdPhotoApp.Models;

public class ProcessingOptions
{
    public int TargetWidth { get; set; } = 300;
    public int TargetHeight { get; set; } = 300;
    public string BgColorHex { get; set; } = "#F4F4F4";
    public double HeadroomRatio { get; set; } = 0.45;
    public double FaceHeightMultiplier { get; set; } = 2.35;
    public bool OutputJpeg { get; set; } = true;
    public int JpegQuality { get; set; } = 95;
    public int TargetDpi { get; set; } = 300;

    public double AspectRatio => TargetHeight > 0 ? (double)TargetWidth / TargetHeight : 1.0;
}
