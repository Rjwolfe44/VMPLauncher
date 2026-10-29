using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Locking;
using VmpLauncher.Core.Private;
using Xunit;

namespace VmpLauncher.Tests.Core;

public sealed class InviteCodeTests
{
    [Fact]
    public void Round_trips_and_is_found_inside_a_chat_message()
    {
        var code = InviteCode.Encode(new Invite { From = "Rjwolfe44", PackId = "p", Repo = "Rjwolfe44/VMP-Private", Token = "github_pat_abc", ServerPassword = "hunter2!" });
        Assert.StartsWith(InviteCode.Prefix, code, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", code, StringComparison.Ordinal);
        var decoded = InviteCode.Decode($"hey, here you go:\n{code}\nhave fun");
        Assert.Equal("Rjwolfe44/VMP-Private", decoded.Repo);
        Assert.Equal("github_pat_abc", decoded.Token);
        Assert.Equal("hunter2!", decoded.ServerPassword);
    }

    [Fact]
    public void Truncated_or_foreign_text_gives_a_clear_error()
    {
        var code = InviteCode.Encode(new Invite { Repo = "a/b", Token = "github_pat_x" });
        var ex = Assert.Throws<LauncherException>(() => InviteCode.Decode(code[..^6]));
        Assert.Contains("damaged or cut off", ex.Message, StringComparison.Ordinal);
        Assert.Throws<LauncherException>(() => InviteCode.Decode("not an invite"));
    }

    [Fact]
    public void Repository_names_are_validated()
    {
        Assert.True(InviteCode.IsValidRepo("Rjwolfe44/VMP-Private"));
        Assert.False(InviteCode.IsValidRepo("../etc"));
        Assert.False(InviteCode.IsValidRepo("a/../b"));
        Assert.False(InviteCode.IsValidRepo("a/b/c"));
        Assert.False(InviteCode.IsValidRepo("https://github.com/a/b"));
        Assert.Throws<ArgumentException>(() => InviteCode.Encode(new Invite { Repo = "bad repo", Token = "t" }));
    }

    [Fact]
    public void Host_token_check_rejects_login_and_classic_tokens()
    {
        Assert.NotNull(HostAccessStore.WhyUnsafe("gho_abcdef"));
        Assert.NotNull(HostAccessStore.WhyUnsafe("ghp_abcdef"));
        Assert.NotNull(HostAccessStore.WhyUnsafe("something"));
        Assert.Null(HostAccessStore.WhyUnsafe("github_pat_11ABCDEF"));
    }
}

public sealed class GitHubPrivateReleasesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vmpl-private-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("pretend this is ApexGuidance.zip");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static ArtifactRef Artifact(byte[]? bytes = null) => new()
    {
        Sha256 = Sha256Util.HashBytes(bytes ?? Payload),
        Size = (bytes ?? Payload).Length,
        FileName = "ApexGuidance-1.zip",
        Distribution = "private-sideload",
    };

    private static readonly PrivateSourceRef Source = new() { Repo = "owner/private-mods", Tag = "apex-1", Asset = "ApexGuidance-1.zip" };

    /// <summary>Fake GitHub: release lookup, asset redirect to a storage host, and the storage download.</summary>
    private sealed class FakeGitHub(byte[] storageBytes, HttpStatusCode releaseStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<(string Url, string? Auth)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add((url, request.Headers.Authorization?.ToString()));
            HttpResponseMessage resp;
            if (url.EndsWith("/repos/owner/private-mods/releases/tags/apex-1", StringComparison.Ordinal))
            {
                resp = new HttpResponseMessage(releaseStatus) { Content = new StringContent("""{"assets":[{"id":42,"name":"ApexGuidance-1.zip"},{"id":7,"name":"other.zip"}]}""") };
            }
            else if (url.EndsWith("/repos/owner/private-mods/releases/assets/42", StringComparison.Ordinal))
            {
                resp = new HttpResponseMessage(HttpStatusCode.Found);
                resp.Headers.Location = new Uri("https://objects.example.test/signed/blob?sig=abc");
            }
            else if (url.StartsWith("https://objects.example.test/", StringComparison.Ordinal))
            {
                resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(storageBytes) };
            }
            else if (url.EndsWith("/repos/owner/private-mods", StringComparison.Ordinal))
            {
                resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"private":true}""") };
            }
            else
            {
                resp = new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            return Task.FromResult(resp);
        }
    }

    [Fact]
    public async Task Downloads_verifies_and_never_sends_the_token_to_storage()
    {
        var fake = new FakeGitHub(Payload);
        using var gh = new GitHubPrivateReleases(fake, new Uri("https://api.example.test/"));
        var dest = Path.Combine(_dir, "apex.zip");
        await gh.DownloadAsync(Source, Artifact(), "github_pat_secret", dest, null, CancellationToken.None);

        Assert.Equal(Payload, await File.ReadAllBytesAsync(dest));
        Assert.All(fake.Requests.Where(r => r.Url.StartsWith("https://api.example.test/", StringComparison.Ordinal)), r => Assert.Equal("Bearer github_pat_secret", r.Auth));
        var storage = Assert.Single(fake.Requests, r => r.Url.StartsWith("https://objects.example.test/", StringComparison.Ordinal));
        Assert.Null(storage.Auth);
    }

    [Fact]
    public async Task Wrong_bytes_are_rejected_and_nothing_is_left_behind()
    {
        using var gh = new GitHubPrivateReleases(new FakeGitHub(Encoding.UTF8.GetBytes("tampered bytes, same length!!!!!")), new Uri("https://api.example.test/"));
        var dest = Path.Combine(_dir, "apex.zip");
        var ex = await Assert.ThrowsAsync<LauncherException>(() => gh.DownloadAsync(Source, Artifact(), "t", dest, null, CancellationToken.None));
        Assert.Equal(ErrorCode.ArtifactCorrupt, ex.Code);
        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest + ".part"));
    }

    [Fact]
    public async Task Oversized_downloads_stop_at_the_promised_size()
    {
        using var gh = new GitHubPrivateReleases(new FakeGitHub(new byte[Payload.Length * 4]), new Uri("https://api.example.test/"));
        var ex = await Assert.ThrowsAsync<LauncherException>(() => gh.DownloadAsync(Source, Artifact(), "t", Path.Combine(_dir, "x.zip"), null, CancellationToken.None));
        Assert.Equal(ErrorCode.ArtifactCorrupt, ex.Code);
    }

    [Fact]
    public async Task Revoked_token_says_ask_for_a_new_invite()
    {
        using var gh = new GitHubPrivateReleases(new FakeGitHub(Payload, HttpStatusCode.Unauthorized), new Uri("https://api.example.test/"));
        var ex = await Assert.ThrowsAsync<LauncherException>(() => gh.DownloadAsync(Source, Artifact(), "t", Path.Combine(_dir, "x.zip"), null, CancellationToken.None));
        Assert.Equal(ErrorCode.PrivateArtifactNeeded, ex.Code);
        Assert.Contains("new invite", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Access_check_reports_private_repositories_as_ok()
    {
        using var gh = new GitHubPrivateReleases(new FakeGitHub(Payload), new Uri("https://api.example.test/"));
        var ok = await gh.CheckAccessAsync("owner/private-mods", "t", CancellationToken.None);
        Assert.Equal(GitHubPrivateReleases.AccessState.Ok, ok.State);
        var missing = await gh.CheckAccessAsync("owner/other", "t", CancellationToken.None);
        Assert.Equal(GitHubPrivateReleases.AccessState.NoAccess, missing.State);
    }
}
