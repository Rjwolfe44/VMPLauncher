using System.IO;
using System.Net;
using System.Net.Http;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Core.Locking;
using VmpLauncher.Core.Transactions;
using Xunit;

namespace VmpLauncher.Tests.Core;

public sealed class ArtifactFetcherTests : IDisposable
{
    private static readonly byte[] Payload = "override bundle"u8.ToArray();

    private readonly string _cache = Path.Combine(Path.GetTempPath(), "vmpl-fetchtests", Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_cache, recursive: true); } catch (IOException) { }
    }

    /// <summary>Serves redirects from a map; anything else gets the payload. Records every URL requested.</summary>
    private sealed class FakeHandler(Dictionary<string, string> redirects) : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            if (redirects.TryGetValue(request.RequestUri!.ToString(), out var location))
            {
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri(location);
                return Task.FromResult(r);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });
        }
    }

    private static ArtifactRef Artifact(string url) => new() { Sha256 = Sha256Util.HashBytes(Payload), Size = Payload.Length, Urls = new[] { url } };

    [Fact]
    public void Redirect_to_a_host_outside_the_allow_list_is_refused()
    {
        var handler = new FakeHandler(new() { ["https://github.com/o/r/releases/download/v1/b.zip"] = "https://evil.example.com/b.zip?token=secret" });
        using var http = ArtifactFetcher.CreateClient(handler);
        var fetcher = new ArtifactFetcher(_cache, null, http);
        var ex = Assert.Throws<LauncherException>(() => fetcher.Fetch(Artifact("https://github.com/o/r/releases/download/v1/b.zip")));
        Assert.Equal(ErrorCode.ArtifactUnavailable, ex.Code);
        Assert.Contains(ex.Details, d => d.Contains("evil.example.com", StringComparison.Ordinal) && d.Contains("not allowed", StringComparison.Ordinal));
        Assert.DoesNotContain(ex.Details, d => d.Contains("token=secret", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requested, u => u.Host == "evil.example.com");
    }

    [Fact]
    public void Redirect_within_the_allow_list_is_followed_and_hash_checked()
    {
        var handler = new FakeHandler(new() { ["https://github.com/o/r/releases/download/v1/b.zip"] = "https://objects.githubusercontent.com/b.zip" });
        using var http = ArtifactFetcher.CreateClient(handler);
        var fetcher = new ArtifactFetcher(_cache, null, http);
        var path = fetcher.Fetch(Artifact("https://github.com/o/r/releases/download/v1/b.zip"));
        Assert.Equal(Payload, File.ReadAllBytes(path));
        Assert.Equal(2, handler.Requested.Count);
    }

    [Fact]
    public void Redirect_to_plain_http_is_refused()
    {
        var handler = new FakeHandler(new() { ["https://github.com/b.zip"] = "http://github.com/b.zip" });
        using var http = ArtifactFetcher.CreateClient(handler);
        var ex = Assert.Throws<LauncherException>(() => new ArtifactFetcher(_cache, null, http).Fetch(Artifact("https://github.com/b.zip")));
        Assert.Contains(ex.Details, d => d.Contains("not https", StringComparison.Ordinal));
        Assert.Single(handler.Requested);
    }
}
