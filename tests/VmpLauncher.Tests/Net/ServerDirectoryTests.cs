using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using VmpLauncher.Core;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Core.Hashing;
using VmpLauncher.Net.Directory;
using Xunit;

namespace VmpLauncher.Tests.Net;

/// <summary>Accepts signatures of the form {"sha256":"&lt;hex of document&gt;"}.</summary>
internal sealed class HashVerifier : ISignedDocumentVerifier
{
    public static string Sign(byte[] doc) => $"{{\"sha256\":\"{Sha256Util.HashBytes(doc)}\"}}";

    public bool Verify(ReadOnlySpan<byte> document, string signatureJson, out string? keyId)
    {
        keyId = "test-key";
        return signatureJson == $"{{\"sha256\":\"{Sha256Util.HashBytes(document)}\"}}";
    }
}

internal sealed class FakeHttp : HttpMessageHandler
{
    public Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Routes { get; } = new();
    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Exact URL first, then the same URL without its query string (so cache-busted retries reach the same route; the handler can inspect the query).</summary>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }
        var uri = request.RequestUri!;
        var found = Routes.TryGetValue(uri.AbsoluteUri, out var f) || Routes.TryGetValue(uri.GetLeftPart(UriPartial.Path), out f);
        return Task.FromResult(found ? f!(request) : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

public sealed class ServerDirectoryTests : IDisposable
{
    private const string Url = "https://example.test/vmp/servers.json";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vmpl-net-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeHttp _http = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_root, true); } catch (IOException) { }
    }

    internal static string Json(long sequence, string extraServerField = "", string packId = "vmp-train-career") => $$"""
        {
          "schemaVersion": 1,
          "packId": "{{packId}}",
          "sequence": {{sequence}},
          "issuedAt": "2026-10-22T12:00:00Z",
          "server": { "id": "vmp-main", "displayName": "VMP Train Career", "protocolForkId": "VMP.VladMultiplayer",
                      "requiredBuild": "0.2.0-train.20261021.1", "passwordRequired": true, "mtu": 1200{{extraServerField}} },
          "routes": [
            { "id": "zerotier", "name": "ZeroTier", "vmpEntryName": "VMP (ZeroTier)", "kind": "zerotier", "host": "10.37.239.191", "port": 8800, "priority": 20,
              "requiresSoftware": { "name": "ZeroTier One", "url": "https://www.zerotier.com/download/", "networkId": "633e31d8a237e3f9", "subnet": "10.37.239.0/24" }, "notes": null },
            { "id": "playit", "name": "Playit tunnel", "vmpEntryName": "VMP (Playit)", "kind": "playit", "host": "signaling-karma.tun.ply.gg", "port": 18113, "priority": 10,
              "requiresSoftware": null, "notes": "Default route" }
          ],
          "motd": "Welcome",
          "maintenance": null
        }
        """;

    private ServerDirectoryClient Client() => new(new LauncherPaths(_root), "vmp-train-career", new HttpClient(_http), new HashVerifier(), _time);

    private void Serve(string json, string? etag = null, string? signature = null)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        _http.Routes[Url] = req =>
        {
            if (etag is not null && req.Headers.IfNoneMatch.Any(t => t.Tag == etag))
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            if (etag is not null) resp.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
            return resp;
        };
        _http.Routes[Url + ".sig"] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(signature ?? HashVerifier.Sign(bytes)) };
    }

    [Fact]
    public void Parses_schema_with_lowercase_kinds_and_orders_by_priority()
    {
        var d = ServerDirectory.Parse(Encoding.UTF8.GetBytes(Json(3)));
        Assert.Equal(3, d.Sequence);
        Assert.True(d.Server.PasswordRequired);
        Assert.Equal(["playit", "zerotier"], d.RoutesByPriority.Select(r => r.Id));
        Assert.Equal(RouteKind.Zerotier, d.Routes[0].Kind);
        Assert.Equal("633e31d8a237e3f9", d.Routes[0].RequiresSoftware!.NetworkId);
        Assert.Null(d.Routes[1].RequiresSoftware);
    }

    [Theory]
    [InlineData(""", "password": "hunter2" """)]
    [InlineData(""", "Passphrase": "x" """)]
    [InlineData(""", "serverPASS": 1 """)]
    [InlineData(""", "extra": { "adminPassword": null } """)]
    public void Refuses_any_pass_key(string extra)
    {
        var ex = Assert.Throws<LauncherException>(() => ServerDirectory.Parse(Encoding.UTF8.GetBytes(Json(1, extra))));
        Assert.Equal(ErrorCode.Tampered, ex.Code);
        Assert.Contains("pass", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PasswordRequired_must_be_boolean()
    {
        var json = Json(1).Replace("\"passwordRequired\": true", "\"passwordRequired\": \"hunter2\"", StringComparison.Ordinal);
        Assert.Throws<LauncherException>(() => ServerDirectory.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Rejects_malformed_schema()
    {
        var json = Json(1).Replace("\"port\": 18113", "\"port\": 0", StringComparison.Ordinal);
        var ex = Assert.Throws<LauncherException>(() => ServerDirectory.Parse(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(ErrorCode.InvalidPack, ex.Code);
    }

    [Fact]
    public async Task Fetch_verifies_caches_and_reports_fresh()
    {
        Serve(Json(5), etag: "\"v5\"");
        var c = Client();
        var r = await c.FetchAsync(new Uri(Url));
        Assert.False(r.IsFromCache);
        Assert.Equal(5, r.Directory.Sequence);
        Assert.Equal("test-key", r.KeyId);
        Assert.True(File.Exists(Path.Combine(_root, "cache", "servers", "vmp-train-career.json")));
        Assert.True(File.Exists(Path.Combine(_root, "cache", "servers", "vmp-train-career.json.sig")));
        Assert.Equal(TimeSpan.Zero, r.Age);

        // Second fetch sends If-None-Match and accepts 304 from the verified cache.
        _time.Advance(TimeSpan.FromHours(3));
        var r2 = await c.FetchAsync(new Uri(Url));
        Assert.Equal(5, r2.Directory.Sequence);
        Assert.False(r2.IsFromCache);
        Assert.Contains(_http.Requests.Last(q => q.RequestUri!.AbsoluteUri == Url).Headers.IfNoneMatch, t => t.Tag == "\"v5\"");
    }

    [Fact]
    public async Task Bad_signature_is_refused_without_cache()
    {
        Serve(Json(5), signature: "{\"sha256\":\"00\"}");
        var ex = await Assert.ThrowsAsync<LauncherException>(() => Client().FetchAsync(new Uri(Url)));
        Assert.Equal(ErrorCode.Tampered, ex.Code);
        Assert.False(File.Exists(Client().CacheFile));
    }

    [Fact]
    public async Task Older_or_equal_sequence_is_refused_and_cache_is_kept()
    {
        Serve(Json(7));
        var c = Client();
        await c.FetchAsync(new Uri(Url));

        Serve(Json(6));
        var older = await c.FetchAsync(new Uri(Url));
        Assert.True(older.IsFromCache);
        Assert.Equal(7, older.Directory.Sequence);
        Assert.Contains("not newer", older.Warning);

        Serve(Json(7).Replace("Welcome", "Changed", StringComparison.Ordinal));
        var same = await c.FetchAsync(new Uri(Url));
        Assert.True(same.IsFromCache);
        Assert.Contains("not newer", same.Warning);

        Serve(Json(8));
        Assert.Equal(8, (await c.FetchAsync(new Uri(Url))).Directory.Sequence);
    }

    [Fact]
    public async Task Pack_id_mismatch_is_refused()
    {
        Serve(Json(1, packId: "other-pack"));
        var ex = await Assert.ThrowsAsync<LauncherException>(() => Client().FetchAsync(new Uri(Url)));
        Assert.Equal(ErrorCode.Tampered, ex.Code);
    }

    [Fact]
    public async Task Network_failure_falls_back_to_cache_with_age()
    {
        Serve(Json(2));
        var c = Client();
        await c.FetchAsync(new Uri(Url));
        _http.Routes.Clear();
        _http.Routes[Url] = _ => throw new HttpRequestException("offline");
        _time.Advance(TimeSpan.FromHours(30));

        var r = await c.FetchAsync(new Uri(Url));
        Assert.True(r.IsFromCache);
        Assert.Equal(TimeSpan.FromHours(30), r.Age);
        Assert.NotNull(r.Warning);
        Assert.NotNull(c.LoadCached());
    }

    [Fact]
    public async Task Tampered_cache_is_ignored()
    {
        Serve(Json(2));
        var c = Client();
        await c.FetchAsync(new Uri(Url));
        File.WriteAllText(c.CacheFile, Json(99));
        Assert.Null(c.LoadCached());
    }

    [Theory]
    [InlineData("https://example.test/vmp/servers.json", "https://example.test/vmp/servers.json?vmpl=42")]
    [InlineData("https://example.test/vmp/servers.json?a=b", "https://example.test/vmp/servers.json?a=b&vmpl=42")]
    public void Cache_buster_is_appended_to_the_query(string url, string expected)
        => Assert.Equal(expected, ServerDirectoryClient.WithCacheBuster(new Uri(url), "42").AbsoluteUri);

    [Fact]
    public async Task Signature_mismatch_is_retried_once_with_the_same_cache_buster_on_both_urls()
    {
        // The CDN still serves the previous .sig for the plain URL right after a push; a cache-busted URL gets the new one.
        var bytes = Encoding.UTF8.GetBytes(Json(6));
        _http.Routes[Url] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        _http.Routes[Url + ".sig"] = req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(req.RequestUri!.Query.Contains("vmpl=", StringComparison.Ordinal)
                ? HashVerifier.Sign(bytes)
                : HashVerifier.Sign(Encoding.UTF8.GetBytes(Json(5)))),
        };

        var r = await Client().FetchAsync(new Uri(Url));

        Assert.False(r.IsFromCache);
        Assert.Null(r.Warning);
        Assert.Equal(6, r.Directory.Sequence);
        var uris = _http.Requests.Select(q => q.RequestUri!).ToList();
        Assert.Equal(4, uris.Count); // doc, sig, then doc + sig again
        var busted = uris.Where(u => u.Query.Contains("vmpl=", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, busted.Count);
        Assert.Equal(busted[0].Query, busted[1].Query);
        Assert.Equal([Url, Url + ".sig"], busted.Select(u => u.GetLeftPart(UriPartial.Path)).ToArray());
    }

    [Fact]
    public async Task Persistent_signature_mismatch_with_a_cache_says_mid_update_or_tampered_and_keeps_the_saved_copy()
    {
        Serve(Json(5));
        var c = Client();
        await c.FetchAsync(new Uri(Url));
        _http.Requests.Clear();

        Serve(Json(6), signature: "{\"sha256\":\"00\"}");
        var r = await c.FetchAsync(new Uri(Url));

        Assert.True(r.IsFromCache);
        Assert.Equal(5, r.Directory.Sequence);
        Assert.Contains("mid-update or tampered", r.Warning);
        Assert.Contains("using the saved copy", r.Warning);
        Assert.Equal(4, _http.Requests.Count); // exactly one retry
    }

    [Fact]
    public async Task Persistent_signature_mismatch_without_a_cache_is_a_Tampered_error_that_mentions_mid_update()
    {
        Serve(Json(5), signature: "{\"sha256\":\"00\"}");
        var ex = await Assert.ThrowsAsync<LauncherException>(() => Client().FetchAsync(new Uri(Url)));
        Assert.Equal(ErrorCode.Tampered, ex.Code);
        Assert.Contains("mid-update", ex.Message);
    }

    [Fact]
    public async Task Rollback_floor_survives_a_cache_that_no_longer_verifies()
    {
        Serve(Json(7));
        var c = Client();
        await c.FetchAsync(new Uri(Url));
        Assert.True(File.Exists(c.SequenceFile));

        // Torn / tampered cache: the pair no longer verifies, so the old code lost its anti-rollback memory here.
        File.WriteAllText(c.CacheFile, Json(99));
        Assert.Null(c.LoadCached());

        Serve(Json(6));
        var ex = await Assert.ThrowsAsync<LauncherException>(() => c.FetchAsync(new Uri(Url)));
        Assert.Equal(ErrorCode.Tampered, ex.Code);
        Assert.Contains("not newer", ex.Message);

        // Same sequence but different bytes is also refused; the exact list that set the floor is fine.
        Serve(Json(7).Replace("Welcome", "Changed", StringComparison.Ordinal));
        await Assert.ThrowsAsync<LauncherException>(() => c.FetchAsync(new Uri(Url)));
        Serve(Json(7));
        Assert.Equal(7, (await c.FetchAsync(new Uri(Url))).Directory.Sequence);
        Serve(Json(8));
        Assert.Equal(8, (await c.FetchAsync(new Uri(Url))).Directory.Sequence);
    }

    [Fact]
    public async Task Rollback_floor_survives_deleting_the_cached_copy()
    {
        Serve(Json(7));
        var c = Client();
        await c.FetchAsync(new Uri(Url));
        File.Delete(c.CacheFile);
        File.Delete(c.SignatureFile);
        File.Delete(c.ETagFile);

        Serve(Json(3));
        await Assert.ThrowsAsync<LauncherException>(() => c.FetchAsync(new Uri(Url)));
        Assert.Null(c.LoadCached());
    }

    [Fact]
    public async Task A_cache_older_than_the_recorded_floor_is_not_trusted()
    {
        Serve(Json(5));
        var c = Client();
        await c.FetchAsync(new Uri(Url));
        var oldDoc = File.ReadAllBytes(c.CacheFile);
        var oldSig = File.ReadAllBytes(c.SignatureFile);
        Serve(Json(9));
        await c.FetchAsync(new Uri(Url));

        // An attacker (or a bad restore) puts the older, validly signed pair back.
        File.WriteAllBytes(c.CacheFile, oldDoc);
        File.WriteAllBytes(c.SignatureFile, oldSig);

        Assert.Null(c.LoadCached());
    }

    [Fact]
    public async Task Floor_is_taken_from_a_verified_cache_when_no_sequence_file_exists_yet()
    {
        // Upgrade path: caches written by earlier launcher versions have no .seq file.
        Serve(Json(7));
        var c = Client();
        await c.FetchAsync(new Uri(Url));
        File.Delete(c.SequenceFile);

        Serve(Json(6));
        var older = await c.FetchAsync(new Uri(Url));
        Assert.True(older.IsFromCache);
        Assert.Contains("not newer", older.Warning);
    }

    [Fact]
    public async Task Concurrent_clients_for_one_pack_never_corrupt_the_cache()
    {
        var clients = Enumerable.Range(1, 12).Select(seq =>
        {
            var http = new FakeHttp();
            var bytes = Encoding.UTF8.GetBytes(Json(seq));
            http.Routes[Url] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            http.Routes[Url + ".sig"] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(HashVerifier.Sign(bytes)) };
            return new ServerDirectoryClient(new LauncherPaths(_root), "vmp-train-career", new HttpClient(http), new HashVerifier(), _time);
        }).ToList();

        for (var round = 0; round < 5; round++)
        {
            var results = await Task.WhenAll(clients.Select(c => Task.Run(() => c.FetchAsync(new Uri(Url)))));
            Assert.All(results, r => Assert.NotNull(r.Directory));
        }

        var final = clients[0].LoadCached();
        Assert.NotNull(final);
        Assert.Equal(12, final!.Directory.Sequence);
        Assert.StartsWith("12 ", File.ReadAllText(clients[0].SequenceFile), StringComparison.Ordinal);
        Assert.Empty(System.IO.Directory.GetFiles(clients[0].CacheDirectory, "*.tmp"));
    }
}
