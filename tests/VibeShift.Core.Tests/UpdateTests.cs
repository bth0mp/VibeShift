using System.Net;
using System.Security.Cryptography;
using VibeShift.Core.Updates;

namespace VibeShift.Core.Tests;

public sealed class UpdateTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "vibeshift-test-" + Guid.NewGuid().ToString("N"));

    public UpdateTests() => Directory.CreateDirectory(dir);

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private static string ReleaseJson(string tag, string? digest, string asset = "VibeShift.exe") => $$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "https://github.com/bth0mp/VibeShift/releases/tag/{{tag}}",
          "assets": [
            { "name": "notes.txt", "browser_download_url": "https://example.invalid/notes.txt", "digest": null },
            { "name": "{{asset}}", "browser_download_url": "https://github.com/bth0mp/VibeShift/releases/download/{{tag}}/{{asset}}",
              "digest": {{(digest is null ? "null" : "\"" + digest + "\"")}} }
          ]
        }
        """;

    [Fact]
    public void Parses_the_version_exe_and_its_sha256()
    {
        var r = Updater.Parse(ReleaseJson("v1.5.0", "sha256:70be55ad"))!;
        Assert.Equal(new Version(1, 5, 0), r.Version);
        Assert.Equal("https://github.com/bth0mp/VibeShift/releases/download/v1.5.0/VibeShift.exe", r.ExeUrl);
        Assert.Equal("70be55ad", r.Sha256);
        Assert.EndsWith("/tag/v1.5.0", r.PageUrl);
    }

    [Fact] // nothing to check the download against, or nothing to download: don't offer it
    public void Release_without_exe_or_digest_is_ignored()
    {
        Assert.Null(Updater.Parse(ReleaseJson("v1.5.0", null)));
        Assert.Null(Updater.Parse(ReleaseJson("v1.5.0", "sha256:ab", asset: "Other.exe")));
        Assert.Null(Updater.Parse(ReleaseJson("nightly", "sha256:ab")));
    }

    [Fact] // only this repo's own release downloads are ever fetched
    public void Release_hosted_anywhere_else_is_ignored() =>
        Assert.Null(Updater.Parse(ReleaseJson("v1.5.0", "sha256:ab").Replace("https://github.com/bth0mp/VibeShift/releases/download/", "https://evil.example/")));

    [Theory]
    [InlineData("1.5.0", "1.4.0.0", true)]
    [InlineData("1.4.0", "1.4.0.0", false)] // the assembly's 4-part version is the same release
    [InlineData("1.4.1", "1.4.0.0", true)]
    [InlineData("1.3.9", "1.4.0.0", false)]
    public void Only_newer_versions_are_offered(string latest, string current, bool newer) =>
        Assert.Equal(newer, Updater.IsNewer(new Release(Version.Parse(latest), "", "", ""), Version.Parse(current)));

    [Fact]
    public async Task Install_swaps_in_the_verified_exe_and_keeps_the_old_one()
    {
        string exe = Path.Combine(dir, "VibeShift.exe");
        File.WriteAllText(exe, "old");
        byte[] fresh = "new build"u8.ToArray();
        var r = new Release(new Version(1, 5, 0), "https://example.invalid/VibeShift.exe", Convert.ToHexStringLower(SHA256.HashData(fresh)), "");

        await Updater.InstallAsync(new HttpClient(new Serve(fresh)), r, exe);

        Assert.Equal("new build", File.ReadAllText(exe));
        Assert.Equal("old", File.ReadAllText(exe + ".old"));
        Assert.False(File.Exists(exe + ".new"));
        Updater.CleanUp(exe);
        Assert.False(File.Exists(exe + ".old"));
    }

    [Fact]
    public async Task Install_refuses_a_download_that_does_not_match_its_sha256()
    {
        string exe = Path.Combine(dir, "VibeShift.exe");
        File.WriteAllText(exe, "old");
        var r = new Release(new Version(1, 5, 0), "https://example.invalid/VibeShift.exe", new string('0', 64), "");

        await Assert.ThrowsAsync<InvalidDataException>(() => Updater.InstallAsync(new HttpClient(new Serve("tampered"u8.ToArray())), r, exe));

        Assert.Equal("old", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + ".new"));
        Assert.False(File.Exists(exe + ".old"));
    }

    [Fact]
    public void A_failed_swap_puts_the_old_exe_back()
    {
        string exe = Path.Combine(dir, "VibeShift.exe");
        File.WriteAllText(exe, "old");
        Assert.ThrowsAny<IOException>(() => Updater.Swap(exe, Path.Combine(dir, "missing.new"), exe + ".old"));
        Assert.Equal("old", File.ReadAllText(exe));
    }

    private sealed class Serve(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }
}
