using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using DikTap.Services.Update;

namespace DikTap.Tests;

public class UpdateServiceTests
{
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }

    [Theory]
    [InlineData("v1.1.0", "1.0.0", true)]
    [InlineData("1.1.0", "1.0.0", true)]
    [InlineData("v2.0.0", "1.9.9", true)]
    [InlineData("v1.0.0", "1.0.0", false)]
    [InlineData("v1.0.0", "1.1.0", false)]
    [InlineData("v0.9.0", "1.0.0", false)]
    [InlineData("", "1.0.0", false)]
    [InlineData("invalid", "1.0.0", false)]
    public void IsNewerVersion_detects_newer_semantic_versions_correctly(string latest, string current, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsNewerVersion(latest, current));
    }

    [Fact]
    public async Task CheckForUpdatesAsync_detects_available_update_and_extracts_assets()
    {
        string json = """
        {
          "tag_name": "v9.9.9",
          "name": "DikTap 9.9.9",
          "body": "Release notes for 9.9.9",
          "html_url": "https://github.com/jorgeju/DikTap/releases/tag/v9.9.9",
          "prerelease": false,
          "assets": [
            {
              "name": "DikTap-9.9.9-win-x64.zip",
              "size": 55000000,
              "browser_download_url": "https://download/DikTap-9.9.9-win-x64.zip"
            },
            {
              "name": "DikTap-9.9.9-win-x64.zip.sha256",
              "size": 92,
              "browser_download_url": "https://download/DikTap-9.9.9-win-x64.zip.sha256"
            }
          ]
        }
        """;

        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
        var client = new HttpClient(handler);
        var service = new UpdateService(() => client);

        var result = await service.CheckForUpdatesAsync();

        Assert.True(result.HasUpdate);
        Assert.Equal("9.9.9", result.LatestVersion);
        Assert.Equal("https://download/DikTap-9.9.9-win-x64.zip", result.ZipDownloadUrl);
        Assert.Equal("https://download/DikTap-9.9.9-win-x64.zip.sha256", result.Sha256DownloadUrl);
        Assert.Equal(55000000, result.ZipSize);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_returns_false_when_version_is_current_or_older()
    {
        string json = """
        {
          "tag_name": "v1.0.0",
          "assets": [
            {
              "name": "DikTap-1.0.0-win-x64.zip",
              "browser_download_url": "https://download/DikTap-1.0.0-win-x64.zip"
            }
          ]
        }
        """;

        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
        var client = new HttpClient(handler);
        var service = new UpdateService(() => client);

        var result = await service.CheckForUpdatesAsync();

        Assert.False(result.HasUpdate);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_returns_false_on_404_not_found()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = new HttpClient(handler);
        var service = new UpdateService(() => client);

        var result = await service.CheckForUpdatesAsync();

        Assert.False(result.HasUpdate);
    }

    [Fact]
    public void BuildUpdateCommand_generates_safe_cmd_loop_and_rejects_unsafe_paths()
    {
        string target = @"C:\Tools\DikTap\DikTap.exe";
        string staged = @"C:\Users\test\AppData\Local\Temp\stage\extracted\DikTap.exe";
        string tempDir = @"C:\Users\test\AppData\Local\Temp\stage";

        string? cmd = UpdateService.BuildUpdateCommand(target, staged, tempDir);

        Assert.NotNull(cmd);
        Assert.Contains("del /f /q", cmd);
        Assert.Contains("copy /y", cmd);
        Assert.Contains(@"C:\Tools\DikTap\DikTap.exe", cmd);
        Assert.Contains(@"--updated", cmd);

        Assert.Null(UpdateService.BuildUpdateCommand(@"C:\Unsafe%Path%\DikTap.exe", staged, tempDir));
        Assert.Null(UpdateService.BuildUpdateCommand(target, @"C:\Unsafe""Path\DikTap.exe", tempDir));
        Assert.Null(UpdateService.BuildUpdateCommand("relative.exe", staged, tempDir));
    }

    [Fact]
    public void RequiresElevation_detects_writable_and_non_writable_locations()
    {
        string userPath = Path.Combine(Path.GetTempPath(), "test.exe");
        Assert.False(UpdateService.RequiresElevation(userPath));

        string progFilesPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DikTap", "DikTap.exe");
        Assert.True(UpdateService.RequiresElevation(progFilesPath));
    }

    [Fact]
    public async Task DownloadAndPrepareAsync_verifies_hash_and_extracts_executable()
    {
        // Create in-memory zip with dummy DikTap.exe (> 1MB)
        using var zipMs = new MemoryStream();
        using (var archive = new ZipArchive(zipMs, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("DikTap.exe");
            using var entryStream = entry.Open();
            byte[] dummyData = new byte[1_200_000];
            dummyData[0] = 0x4D; // 'M'
            dummyData[1] = 0x5A; // 'Z'
            entryStream.Write(dummyData, 0, dummyData.Length);
        }
        byte[] zipBytes = zipMs.ToArray();
        string hash = Convert.ToHexStringLower(SHA256.HashData(zipBytes));

        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.RequestUri!.ToString().EndsWith(".sha256"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{hash} *update.zip") };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) };
        });

        var client = new HttpClient(handler);
        var service = new UpdateService(() => client);

        var updateInfo = new UpdateCheckResult(
            HasUpdate: true,
            CurrentVersion: "1.0.0",
            LatestVersion: "1.1.0",
            ReleaseNotes: null,
            ReleaseUrl: null,
            ZipDownloadUrl: "https://test/update.zip",
            ZipSize: zipBytes.Length,
            Sha256DownloadUrl: "https://test/update.zip.sha256"
        );

        var staged = await service.DownloadAndPrepareAsync(updateInfo);

        try
        {
            Assert.True(File.Exists(staged.StagedExe));
            Assert.True(new FileInfo(staged.StagedExe).Length >= 1_000_000);
            Assert.True(Directory.Exists(staged.StagingDir));
        }
        finally
        {
            try { Directory.Delete(staged.StagingDir, true); } catch { }
        }
    }

    [Fact]
    public async Task DownloadAndPrepareAsync_aborts_on_sha256_mismatch()
    {
        using var zipMs = new MemoryStream();
        using (var archive = new ZipArchive(zipMs, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("DikTap.exe");
            using var entryStream = entry.Open();
            entryStream.Write(new byte[1_200_000]);
        }
        byte[] zipBytes = zipMs.ToArray();

        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.RequestUri!.ToString().EndsWith(".sha256"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("badhash000000000000000000000000000000000000000000000000000000000 *update.zip") };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) };
        });

        var client = new HttpClient(handler);
        var service = new UpdateService(() => client);

        var updateInfo = new UpdateCheckResult(
            HasUpdate: true,
            CurrentVersion: "1.0.0",
            LatestVersion: "1.1.0",
            ReleaseNotes: null,
            ReleaseUrl: null,
            ZipDownloadUrl: "https://test/update.zip",
            ZipSize: zipBytes.Length,
            Sha256DownloadUrl: "https://test/update.zip.sha256"
        );

        await Assert.ThrowsAsync<DictationException>(() => service.DownloadAndPrepareAsync(updateInfo));
    }

    [Fact]
    public async Task Live_download_and_verify_against_real_github_release()
    {
        var service = new UpdateService();
        var check = await service.CheckForUpdatesAsync();
        Assert.NotNull(check.LatestVersion);
        Assert.NotNull(check.ZipDownloadUrl);
        Assert.NotNull(check.Sha256DownloadUrl);

        var staged = await service.DownloadAndPrepareAsync(check);
        try
        {
            Assert.True(File.Exists(staged.StagedExe));
            Assert.True(new FileInfo(staged.StagedExe).Length > 100_000_000);
            Assert.True(Directory.Exists(staged.StagingDir));
        }
        finally
        {
            try { Directory.Delete(staged.StagingDir, true); } catch { }
        }
    }
}
