using ClipboardManager.Core.Updates;

namespace ClipboardManager.App.Services;

public interface IUpdateReleaseService
{
    Task<UpdateCheckResult> CheckLatestAsync(string currentVersion, string installDirectory, string userDataDirectory, CancellationToken cancellationToken);
    Task DownloadAssetAsync(UpdateAsset asset, string destinationPath, long expectedSize, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken);
}

public interface IUpdatePackageService
{
    string CreateStagingDirectory(string stagingRoot);
    Task DownloadAsync(UpdateCheckResult update, string stagingDirectory, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken);
    Task VerifyAndExtractAsync(UpdateCheckResult update, string stagingDirectory, CancellationToken cancellationToken);
    void Cleanup(string stagingDirectory);
}

