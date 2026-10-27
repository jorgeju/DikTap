using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DikTap.Localization;

namespace DikTap.Services.Update;

internal sealed record GitHubReleaseDto
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("body")]
    public string? Body { get; init; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; init; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; init; }

    [JsonPropertyName("assets")]
    public List<GitHubAssetDto>? Assets { get; init; }
}

internal sealed record GitHubAssetDto
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; init; }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(GitHubReleaseDto))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;

internal sealed class UpdateService : IUpdateService
{
    private const string DefaultApiUrl = "https://api.github.com/repos/jorgeju/DikTap/releases/latest";

    private readonly Func<HttpClient> _clientFactory;
    private readonly string _apiUrl;

    private static readonly Lazy<HttpClient> SharedClient = new(() => new HttpClient
    {
        DefaultRequestHeaders =
        {
            { "User-Agent", "DikTap" },
            { "Accept", "application/vnd.github+json" }
        },
        Timeout = TimeSpan.FromSeconds(30)
    });

    public UpdateService(Func<HttpClient>? clientFactory = null, string? apiUrl = null)
    {
        _clientFactory = clientFactory ?? (() => SharedClient.Value);
        _apiUrl = apiUrl ?? DefaultApiUrl;
    }

    public static bool IsNewerVersion(string latestTag, string currentVersion)
    {
        if (string.IsNullOrWhiteSpace(latestTag)) return false;
        string cleanLatest = latestTag.TrimStart('v', 'V').Split('-')[0];
        string cleanCurrent = currentVersion.TrimStart('v', 'V').Split('-')[0];
        if (Version.TryParse(cleanLatest, out var l) && Version.TryParse(cleanCurrent, out var c))
            return l > c;
        return false;
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        var client = _clientFactory();
        using var request = new HttpRequestMessage(HttpMethod.Get, _apiUrl);
        if (!request.Headers.Contains("User-Agent"))
            request.Headers.Add("User-Agent", "DikTap");
        if (!request.Headers.Contains("Accept"))
            request.Headers.Add("Accept", "application/vnd.github+json");

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new UpdateCheckResult(false, AppInfo.Version, null, null, null, null, 0, null);
        }
        if ((int)response.StatusCode == 403 || (int)response.StatusCode == 429)
        {
            throw new DictationException(Str.UpdateRateLimited, ErrorKind.Network);
        }
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var release = JsonSerializer.Deserialize(stream, UpdateJsonContext.Default.GitHubReleaseDto);
        if (release is null || string.IsNullOrWhiteSpace(release.TagName))
        {
            return new UpdateCheckResult(false, AppInfo.Version, null, null, null, null, 0, null);
        }

        string latestTag = release.TagName.Trim();
        string cleanVersion = latestTag.TrimStart('v', 'V');
        var assets = release.Assets ?? new List<GitHubAssetDto>();

        var zipAsset = assets.FirstOrDefault(a => a.Name?.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase) == true)
                    ?? assets.FirstOrDefault(a => a.Name?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true);
        var shaAsset = assets.FirstOrDefault(a => a.Name?.EndsWith(".zip.sha256", StringComparison.OrdinalIgnoreCase) == true)
                    ?? assets.FirstOrDefault(a => a.Name?.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase) == true);

        bool hasUpdate = IsNewerVersion(latestTag, AppInfo.Version) && zipAsset?.BrowserDownloadUrl is not null;

        return new UpdateCheckResult(
            HasUpdate: hasUpdate,
            CurrentVersion: AppInfo.Version,
            LatestVersion: cleanVersion,
            ReleaseNotes: release.Body,
            ReleaseUrl: release.HtmlUrl,
            ZipDownloadUrl: zipAsset?.BrowserDownloadUrl,
            ZipSize: zipAsset?.Size ?? 0,
            Sha256DownloadUrl: shaAsset?.BrowserDownloadUrl
        );
    }

    public async Task<StagedUpdate> DownloadAndPrepareAsync(UpdateCheckResult update, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(update.ZipDownloadUrl))
            throw new DictationException(Str.UpdateInvalidPackage, ErrorKind.Generic);

        string stagingDir = Path.Combine(Path.GetTempPath(), "DikTap-Update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDir);
        string zipPath = Path.Combine(stagingDir, "update.zip");

        try
        {
            var client = _clientFactory();

            using (var response = await client.GetAsync(update.ZipDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                long totalBytes = response.Content.Headers.ContentLength ?? update.ZipSize;
                await using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                byte[] buffer = new byte[81920];
                long totalRead = 0;
                int read;
                while ((read = await contentStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    totalRead += read;
                    if (totalBytes > 0)
                        progress?.Report((double)totalRead / totalBytes);
                }
            }

            if (!string.IsNullOrWhiteSpace(update.Sha256DownloadUrl))
            {
                string shaText = await client.GetStringAsync(update.Sha256DownloadUrl, ct).ConfigureAwait(false);
                string expectedHash = shaText.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();

                await using var checkStream = File.OpenRead(zipPath);
                byte[] hashBytes = await SHA256.HashDataAsync(checkStream, ct).ConfigureAwait(false);
                string actualHash = Convert.ToHexStringLower(hashBytes);

                if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                    throw new DictationException(Str.UpdateHashMismatch, ErrorKind.Generic);
            }

            string extractDir = Path.Combine(stagingDir, "extracted");
            ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

            string stagedExe = Path.Combine(extractDir, "DikTap.exe");
            if (!File.Exists(stagedExe) || new FileInfo(stagedExe).Length < 1_000_000)
                throw new DictationException(Str.UpdateInvalidPackage, ErrorKind.Generic);

            return new StagedUpdate(stagedExe, stagingDir);
        }
        catch
        {
            try { Directory.Delete(stagingDir, recursive: true); } catch { }
            throw;
        }
    }

    internal static string? BuildUpdateCommand(string targetExe, string stagedExe, string stagingDir)
    {
        if (string.IsNullOrWhiteSpace(targetExe) || !Path.IsPathFullyQualified(targetExe)) return null;
        if (string.IsNullOrWhiteSpace(stagedExe) || !Path.IsPathFullyQualified(stagedExe)) return null;
        if (new[] { targetExe, stagedExe, stagingDir }.Any(x => x.Contains('%') || x.Contains('"'))) return null;

        string target = $"\"{targetExe}\"";
        string source = $"\"{stagedExe}\"";
        string stageDir = Path.GetDirectoryName(stagedExe)!.TrimEnd('\\');
        string targetDir = Path.GetDirectoryName(targetExe)!.TrimEnd('\\');
        string temp = $"\"{stagingDir.TrimEnd('\\')}\"";

        string extractBase = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Path.GetTempPath(), ".net");
        string netExtract = $"\"{Path.Combine(extractBase, "DikTap")}\"";

        return $"for /l %n in (1,1,60) do @if exist {target} (del /f /q {target} >nul 2>&1 & ping -n 2 127.0.0.1 >nul) else (copy /y {source} {target} >nul 2>&1 & (if exist \"{stageDir}\\VERSION.txt\" copy /y \"{stageDir}\\VERSION.txt\" \"{targetDir}\\VERSION.txt\" >nul 2>&1) & rd /s /q {netExtract} >nul 2>&1 & start \"\" {target} --updated & rd /s /q {temp} >nul 2>&1 & exit)";
    }

    internal static bool RequiresElevation(string targetExe)
    {
        try
        {
            string dir = Path.GetDirectoryName(targetExe) ?? string.Empty;
            if (!Directory.Exists(dir)) return true;
            string testFile = Path.Combine(dir, "DikTap-UacTest-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var fs = new FileStream(testFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }
            return false;
        }
        catch
        {
            return true;
        }
    }

    public bool LaunchUpdater(string targetExe, string stagedExe, string stagingDir)
    {
        string? command = BuildUpdateCommand(targetExe, stagedExe, stagingDir);
        if (command is null) return false;

        bool elevate = RequiresElevation(targetExe);
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = $"/d /s /c \"{command}\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        if (elevate) psi.Verb = "runas";

        try
        {
            using var proc = Process.Start(psi);
            return proc is not null;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Logger.Info("Update cancelled by user at UAC prompt");
            throw new DictationException(Str.UpdateCancelled, ErrorKind.Generic);
        }
        catch (Exception ex)
        {
            Logger.Error("Could not launch updater process", ex);
            return false;
        }
    }
}
