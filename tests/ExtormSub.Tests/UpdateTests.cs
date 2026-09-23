using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ExtormSub.Infrastructure.Security;
using ExtormSub.Infrastructure.Updates;

namespace ExtormSub.Tests;

public class UpdateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("extormsub-upd").FullName;
    private static readonly Uri Feed = new("https://github.com/o/r/releases/latest/download/latest.json");
    private static readonly byte[] Installer = RandomNumberGenerator.GetBytes(300_000);
    private static readonly string InstallerSha = Convert.ToHexString(SHA256.HashData(Installer));
    private static string SignedFile => Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "coreclr.dll");

    private static string Manifest(string version, string url = "ExtormSub-Setup-0.3.0.exe", string? sha = null) =>
        $$"""{"version":"{{version}}","url":"{{url}}","sha256":"{{sha ?? InstallerSha}}","size":{{Installer.Length}}}""";

    /// <summary>Serves the manifest for *.json and the installer bytes for anything else.</summary>
    private sealed class FeedServer(string manifest) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            HttpContent content = request.RequestUri!.AbsolutePath.EndsWith(".json")
                ? new StringContent(manifest, Encoding.UTF8, "application/json")
                : new ByteArrayContent(Installer);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    [Fact]
    public void Relative_installer_url_resolves_next_to_the_manifest()
    {
        var info = UpdateChecker.ParseManifest(Manifest("0.3.0"), Feed);
        Assert.Equal("https://github.com/o/r/releases/latest/download/ExtormSub-Setup-0.3.0.exe", info.Url.ToString());
        Assert.Equal(new Version(0, 3, 0, 0), info.Version);
        Assert.Equal(Installer.Length, info.Bytes);
    }

    [Theory]
    [InlineData("""{"version":"0.3.0","url":"http://evil/x.exe","sha256":"00"}""")]
    [InlineData("""{"version":"0.3.0","url":"x.exe","sha256":"not-a-hash"}""")]
    [InlineData("""{"version":"latest","url":"x.exe","sha256":"00"}""")]
    [InlineData("""{"url":"x.exe"}""")]
    [InlineData("<html>404</html>")]
    public void Bad_manifests_are_rejected(string json) =>
        Assert.Throws<InvalidDataException>(() => UpdateChecker.ParseManifest(json, Feed));

    [Theory]
    [InlineData("0.2.0.0", true)]
    [InlineData("0.3.0.0", false)] // "0.3.0" in the manifest is the same version, not newer
    [InlineData("0.3.0", false)]
    [InlineData("1.0.0.0", false)]
    public async Task Only_newer_versions_are_offered(string current, bool offered)
    {
        var checker = new UpdateChecker(new HttpClient(new FeedServer(Manifest("0.3.0"))), Feed);
        Assert.Equal(offered, await checker.CheckAsync(Version.Parse(current), CancellationToken.None) is not null);
    }

    [Fact]
    public async Task Unsigned_build_downloads_and_verifies_the_checksum()
    {
        var server = new FeedServer(Manifest("0.3.0"));
        var checker = new UpdateChecker(new HttpClient(server), Feed);
        var info = await checker.CheckAsync(new Version(0, 2, 0), CancellationToken.None);
        var unsignedExe = Path.Combine(_dir, "ExtormSub.exe");
        File.WriteAllBytes(unsignedExe, [1, 2, 3]);

        var path = await checker.DownloadAsync(info!, _dir, unsignedExe, null, CancellationToken.None);

        Assert.Equal(Installer, File.ReadAllBytes(path));
        Assert.Equal("/o/r/releases/latest/download/ExtormSub-Setup-0.3.0.exe", server.Requests[^1].AbsolutePath);
    }

    [Fact]
    public async Task Tampered_installer_is_deleted()
    {
        var checker = new UpdateChecker(new HttpClient(new FeedServer(Manifest("0.3.0", sha: new string('A', 64)))), Feed);
        var info = await checker.CheckAsync(new Version(0, 2, 0), CancellationToken.None);
        await Assert.ThrowsAnyAsync<IOException>(() => checker.DownloadAsync(info!, _dir, SignedFile, null, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Signed_build_refuses_an_installer_from_another_publisher()
    {
        var checker = new UpdateChecker(new HttpClient(new FeedServer(Manifest("0.3.0"))), Feed);
        var info = await checker.CheckAsync(new Version(0, 2, 0), CancellationToken.None);
        // The running exe is "signed" (a Microsoft-signed runtime file); the download is not.
        await Assert.ThrowsAsync<InvalidDataException>(() => checker.DownloadAsync(info!, _dir, SignedFile, null, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Authenticode_reads_valid_signatures_only()
    {
        Assert.Contains("Microsoft Corporation", Authenticode.TrustedSigner(SignedFile));
        var unsigned = Path.Combine(_dir, "plain.exe");
        File.WriteAllBytes(unsigned, [0x4D, 0x5A, 0, 0]);
        Assert.Null(Authenticode.TrustedSigner(unsigned));
    }

    public void Dispose() => Directory.Delete(_dir, true);
}
