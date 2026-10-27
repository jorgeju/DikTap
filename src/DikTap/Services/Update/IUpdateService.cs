namespace DikTap.Services.Update;

internal sealed record UpdateCheckResult(
    bool HasUpdate,
    string CurrentVersion,
    string? LatestVersion,
    string? ReleaseNotes,
    string? ReleaseUrl,
    string? ZipDownloadUrl,
    long ZipSize,
    string? Sha256DownloadUrl
);

internal sealed record StagedUpdate(string StagedExe, string StagingDir);

internal interface IUpdateService
{
    Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default);
    Task<StagedUpdate> DownloadAndPrepareAsync(UpdateCheckResult update, IProgress<double>? progress = null, CancellationToken ct = default);
    bool LaunchUpdater(string targetExe, string stagedExe, string stagingDir);
}
