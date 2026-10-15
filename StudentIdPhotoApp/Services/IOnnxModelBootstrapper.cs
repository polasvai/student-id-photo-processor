namespace StudentIdPhotoApp.Services;

public interface IOnnxModelBootstrapper
{
    string FaceModelPath { get; }
    string BgRemovalModelPath { get; }
    bool AreModelsReady { get; }
    Task EnsureModelsAsync(CancellationToken cancellationToken = default);
}
