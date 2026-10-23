using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using VmpLauncher.Core.ModInfo;
using Xunit;

namespace VmpLauncher.Tests.Core;

public sealed class SpaceDockClientTests : IDisposable
{
    private const int EightMb = 8 * 1024 * 1024;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vmpl-spacedock-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string DataFile(int id) => Path.Combine(_dir, "spacedock", id + ".json");

    private string ImageDir => Path.Combine(_dir, "img");

    private SpaceDockClient NewClient(HttpMessageHandler handler, TimeSpan? maxAge = null) => new(_dir, handler, maxAge);

    // A response shaped like the real /api/mod/{id}: relative paths, an odd mix of shared_authors shapes,
    // null fields, and junk entries that must be skipped rather than thrown on.
    private const string Sample = """
        {
          "name": "Example Parts Pack",
          "id": 1234,
          "short_description": "Adds parts.",
          "description": "# Example\n\nBig **bold** text ![shot](https://i.imgur.com/a.png)",
          "description_html": "<h1>Example</h1>",
          "downloads": 51234,
          "followers": 77,
          "author": "SomeAuthor",
          "shared_authors": [
            { "user": "Helper1" },
            "Helper2",
            { "username": "Helper3" },
            { "user": { "username": "Helper4" } },
            { "user": "Pending", "accepted": false },
            "helper2",
            42
          ],
          "background": "/content/users/1/Example-bg.png",
          "bg_offset_y": 12,
          "license": "MIT",
          "website": "example.com",
          "donations": null,
          "source_code": "https://github.com/example/parts",
          "url": "/mod/1234/Example%20Parts%20Pack",
          "versions": [
            {
              "friendly_version": "2.0.1",
              "game_version": "1.12.5",
              "id": 9,
              "created": "2023-04-12T10:11:12.123456",
              "download_path": "/mod/1234/Example%20Parts%20Pack/download/2.0.1",
              "changelog": "Fixed things",
              "downloads": 100
            },
            { "friendly_version": "1.0", "id": 8, "created": null, "download_path": null, "changelog": null },
            "not an object"
          ]
        }
        """;

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Image(byte[] bytes, string? contentType)
    {
        var content = new ByteArrayContent(bytes);
        if (contentType is not null)
        {
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>Content whose length is not known up front, like a chunked response, so only the streaming cap can stop it.</summary>
    private sealed class UnsizedContent : HttpContent
    {
        private readonly byte[] _bytes;

        public UnsizedContent(byte[] bytes, string contentType)
        {
            _bytes = bytes;
            Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(_bytes, 0, _bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly List<Uri> _requests = new();

        public string? LastUserAgent { get; private set; }

        public IReadOnlyList<Uri> Requests
        {
            get
            {
                lock (_requests)
                {
                    return _requests.ToList();
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_requests)
            {
                _requests.Add(request.RequestUri!);
            }
            LastUserAgent = request.Headers.UserAgent.ToString();
            try
            {
                return Task.FromResult(respond(request));
            }
            catch (Exception ex)
            {
                return Task.FromException<HttpResponseMessage>(ex);
            }
        }
    }

    private static FakeHandler Offline() => new(_ => throw new HttpRequestException("offline"));

    // --- parsing ---

    [Fact]
    public async Task Parses_a_realistic_response_and_asks_the_right_url_as_ourselves()
    {
        var handler = new FakeHandler(_ => Json(Sample));
        var before = DateTimeOffset.UtcNow;

        var info = await NewClient(handler).GetAsync(1234, CancellationToken.None);

        Assert.Equal(new Uri("https://spacedock.info/api/mod/1234"), Assert.Single(handler.Requests));
        Assert.Equal("VmpLauncher (+https://github.com/Rjwolfe44/VMPLauncher)", handler.LastUserAgent);
        Assert.NotNull(info);
        Assert.Equal(1234, info.SpaceDockId);
        Assert.Equal("Example Parts Pack", info.Name);
        Assert.Equal("Adds parts.", info.ShortDescription);
        Assert.StartsWith("# Example", info.Description, StringComparison.Ordinal);
        Assert.Equal(51234, info.Downloads);
        Assert.Equal(77, info.Followers);
        Assert.Equal("SomeAuthor", info.Author);
        Assert.Equal(new[] { "Helper1", "Helper2", "Helper3", "Helper4" }, info.SharedAuthors);
        Assert.Equal(new Uri("https://spacedock.info/content/users/1/Example-bg.png"), info.HeaderImage);
        Assert.Equal("MIT", info.License);
        Assert.Equal(new Uri("https://example.com/"), info.Website);
        Assert.Null(info.Donations);
        Assert.Equal(new Uri("https://github.com/example/parts"), info.SourceCode);
        Assert.Equal(new Uri("https://spacedock.info/mod/1234/Example%20Parts%20Pack"), info.PageUrl);
        Assert.InRange(info.FetchedUtc, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));

        Assert.Equal(2, info.Versions.Count);
        var newest = info.Versions[0];
        Assert.Equal("2.0.1", newest.FriendlyVersion);
        Assert.Equal("1.12.5", newest.GameVersion);
        Assert.Equal(new DateTimeOffset(2023, 4, 12, 10, 11, 12, 123, TimeSpan.Zero).AddTicks(4_560), newest.Created);
        Assert.Equal("Fixed things", newest.Changelog);
        Assert.Equal(100, newest.Downloads);
        Assert.Equal(new Uri("https://spacedock.info/mod/1234/Example%20Parts%20Pack/download/2.0.1"), newest.DownloadUrl);

        var oldest = info.Versions[1];
        Assert.Equal("1.0", oldest.FriendlyVersion);
        Assert.Null(oldest.GameVersion);
        Assert.Null(oldest.Created);
        Assert.Null(oldest.Changelog);
        Assert.Equal(0, oldest.Downloads);
        Assert.Null(oldest.DownloadUrl);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"name":null,"id":null,"short_description":null,"description":null,"downloads":null,"followers":null,"author":null,"shared_authors":null,"background":null,"license":null,"website":null,"donations":null,"source_code":null,"url":null,"versions":null}""")]
    [InlineData("""{"name":42,"description":["x"],"shared_authors":"nobody","versions":{"a":1},"downloads":{"n":1},"background":{"x":1}}""")]
    public async Task Missing_null_or_oddly_typed_fields_never_throw(string json)
    {
        var info = await NewClient(new FakeHandler(_ => Json(json))).GetAsync(5, CancellationToken.None);

        Assert.NotNull(info);
        Assert.Equal(5, info.SpaceDockId);
        Assert.False(string.IsNullOrWhiteSpace(info.Name));
        Assert.Null(info.ShortDescription);
        Assert.Null(info.Author);
        Assert.Equal(0, info.Downloads);
        Assert.Empty(info.SharedAuthors);
        Assert.Null(info.HeaderImage);
        Assert.Null(info.Website);
        Assert.Equal(new Uri("https://spacedock.info/mod/5"), info.PageUrl);
        Assert.Empty(info.Versions);
    }

    [Fact]
    public async Task Author_supplied_links_are_sanitized_and_loose_numbers_accepted()
    {
        const string json = """
            {
              "name": "X",
              "background": "http://insecure.example/x.png",
              "website": "javascript:alert(1)",
              "donations": "ftp://donate.test/",
              "source_code": "https://user:secret@github.com/a/b",
              "url": "https://evil.test/mod/1",
              "downloads": "1500",
              "followers": 2.0,
              "versions": [ { "friendly_version": 1.5, "download_path": "https://other.test/file.zip", "downloads": "12" } ]
            }
            """;

        var info = await NewClient(new FakeHandler(_ => Json(json))).GetAsync(8, CancellationToken.None);

        Assert.NotNull(info);
        Assert.Null(info.HeaderImage);
        Assert.Null(info.Website);
        Assert.Null(info.Donations);
        Assert.Null(info.SourceCode);
        Assert.Equal(new Uri("https://spacedock.info/mod/8"), info.PageUrl);
        Assert.Equal(1500, info.Downloads);
        Assert.Equal(2, info.Followers);
        var version = Assert.Single(info.Versions);
        Assert.Equal("1.5", version.FriendlyVersion);
        Assert.Equal(12, version.Downloads);
        Assert.Equal(new Uri("https://other.test/file.zip"), version.DownloadUrl);
    }

    [Theory]
    [InlineData("/content/a b/bg.png", "https://spacedock.info/content/a%20b/bg.png")]
    [InlineData("https://cdn.example.test/bg.jpg", "https://cdn.example.test/bg.jpg")]
    [InlineData("//cdn.example.test/bg.jpg", "https://cdn.example.test/bg.jpg")]
    public async Task Header_image_may_be_relative_absolute_or_protocol_relative(string background, string expected)
    {
        var json = $$"""{"name":"X","background":"{{background}}"}""";

        var info = await NewClient(new FakeHandler(_ => Json(json))).GetAsync(3, CancellationToken.None);

        Assert.Equal(new Uri(expected), info?.HeaderImage);
    }

    // --- cache ---

    [Fact]
    public async Task A_fresh_cache_entry_makes_no_request()
    {
        var first = new FakeHandler(_ => Json(Sample));
        var original = await NewClient(first).GetAsync(1234, CancellationToken.None);
        Assert.Single(first.Requests);
        Assert.True(File.Exists(DataFile(1234)));

        var second = new FakeHandler(_ => throw new InvalidOperationException("must not be called"));
        var again = await NewClient(second).GetAsync(1234, CancellationToken.None);

        Assert.Empty(second.Requests);
        Assert.NotNull(again);
        Assert.Equal(original!.Name, again.Name);
        Assert.Equal(original.FetchedUtc, again.FetchedUtc);
        Assert.Equal(original.Versions.Count, again.Versions.Count);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(DataFile(1234))!, "*.tmp"));
    }

    [Fact]
    public async Task A_stale_entry_is_refreshed_from_the_network()
    {
        await NewClient(new FakeHandler(_ => Json(Sample))).GetAsync(1234, CancellationToken.None);

        var updated = new FakeHandler(_ => Json("""{"name":"Renamed","id":1234}"""));
        var info = await NewClient(updated, TimeSpan.Zero).GetAsync(1234, CancellationToken.None);

        Assert.Single(updated.Requests);
        Assert.Equal("Renamed", info?.Name);
        Assert.Equal("Renamed", (await NewClient(Offline()).GetAsync(1234, CancellationToken.None))?.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_stale_copy_is_returned_when_the_network_fails(bool serverError)
    {
        var original = await NewClient(new FakeHandler(_ => Json(Sample))).GetAsync(1234, CancellationToken.None);
        var failing = serverError ? new FakeHandler(_ => Json("busy", HttpStatusCode.ServiceUnavailable)) : Offline();

        var stale = await NewClient(failing, TimeSpan.Zero).GetAsync(1234, CancellationToken.None);

        Assert.Single(failing.Requests);
        Assert.NotNull(stale);
        Assert.Equal("Example Parts Pack", stale.Name);
        Assert.Equal(original!.FetchedUtc, stale.FetchedUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Without_a_cached_copy_a_network_failure_gives_null_and_caches_nothing(bool serverError)
    {
        var failing = serverError ? new FakeHandler(_ => Json("busy", HttpStatusCode.InternalServerError)) : Offline();

        Assert.Null(await NewClient(failing).GetAsync(1234, CancellationToken.None));
        Assert.False(File.Exists(DataFile(1234)));
    }

    [Fact]
    public async Task A_404_returns_null_and_is_remembered_until_the_entry_goes_stale()
    {
        var gone = new FakeHandler(_ => Json("""{"error":true,"reason":"Mod not found."}""", HttpStatusCode.NotFound));
        Assert.Null(await NewClient(gone).GetAsync(99, CancellationToken.None));
        Assert.Single(gone.Requests);
        Assert.True(File.Exists(DataFile(99)), "a small missing marker is written");
        Assert.True(new FileInfo(DataFile(99)).Length < 512);

        // Remembered: no new request, even if SpaceDock would now answer.
        var later = new FakeHandler(_ => Json(Sample));
        Assert.Null(await NewClient(later).GetAsync(99, CancellationToken.None));
        Assert.Empty(later.Requests);

        // Once stale it is re-checked, and a mod that has appeared since is returned.
        var recheck = new FakeHandler(_ => Json("""{"name":"Back","id":99}"""));
        var info = await NewClient(recheck, TimeSpan.Zero).GetAsync(99, CancellationToken.None);
        Assert.Single(recheck.Requests);
        Assert.Equal("Back", info?.Name);
    }

    [Fact]
    public async Task A_stale_marker_with_the_network_down_stays_null()
    {
        await NewClient(new FakeHandler(_ => Json("{}", HttpStatusCode.NotFound))).GetAsync(99, CancellationToken.None);

        Assert.Null(await NewClient(Offline(), TimeSpan.Zero).GetAsync(99, CancellationToken.None));
    }

    [Fact]
    public async Task A_404_replaces_an_older_good_copy()
    {
        await NewClient(new FakeHandler(_ => Json(Sample))).GetAsync(1234, CancellationToken.None);

        Assert.Null(await NewClient(new FakeHandler(_ => Json("{}", HttpStatusCode.NotFound)), TimeSpan.Zero).GetAsync(1234, CancellationToken.None));
        Assert.Null(await NewClient(Offline(), TimeSpan.Zero).GetAsync(1234, CancellationToken.None));
    }

    [Theory]
    [InlineData("<html>Bad gateway</html>")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"error":true,"reason":"nope"}""")]
    public async Task Answers_that_are_not_a_mod_are_not_cached(string body)
    {
        var handler = new FakeHandler(_ => Json(body));
        var client = NewClient(handler);

        Assert.Null(await client.GetAsync(7, CancellationToken.None));
        Assert.Null(await client.GetAsync(7, CancellationToken.None));

        Assert.Equal(2, handler.Requests.Count);
        Assert.False(File.Exists(DataFile(7)));
    }

    [Fact]
    public async Task A_corrupt_cache_file_is_ignored_and_replaced()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DataFile(1234))!);
        await File.WriteAllTextAsync(DataFile(1234), "{ this is not json");

        var info = await NewClient(new FakeHandler(_ => Json(Sample))).GetAsync(1234, CancellationToken.None);

        Assert.Equal("Example Parts Pack", info?.Name);
        Assert.Equal("Example Parts Pack", (await NewClient(Offline()).GetAsync(1234, CancellationToken.None))?.Name);
    }

    [Fact]
    public async Task Json_over_4_MB_is_refused_even_when_the_server_does_not_say_how_big_it_is()
    {
        var huge = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnsizedContent(Enumerable.Repeat((byte)' ', (4 * 1024 * 1024) + 1).ToArray(), "application/json") });

        Assert.Null(await NewClient(huge).GetAsync(1, CancellationToken.None));
        Assert.False(File.Exists(DataFile(1)));
    }

    [Fact]
    public async Task Nonsense_ids_never_touch_the_network()
    {
        var handler = new FakeHandler(_ => Json(Sample));
        var client = NewClient(handler);

        Assert.Null(await client.GetAsync(0, CancellationToken.None));
        Assert.Null(await client.GetAsync(-3, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Cancellation_propagates_instead_of_being_swallowed()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewClient(new FakeHandler(_ => Json(Sample))).GetAsync(1, cts.Token));
    }

    [Fact]
    public async Task No_more_than_four_requests_are_in_flight_at_once()
    {
        var current = 0;
        var peak = 0;
        var handler = new SlowHandler(
            enter: () =>
            {
                var now = Interlocked.Increment(ref current);
                int seen;
                do { seen = Volatile.Read(ref peak); } while (now > seen && Interlocked.CompareExchange(ref peak, now, seen) != seen);
            },
            leave: () => Interlocked.Decrement(ref current));
        var client = NewClient(handler);

        var all = await Task.WhenAll(Enumerable.Range(1, 16).Select(id => client.GetAsync(id, CancellationToken.None)));

        Assert.All(all, info => Assert.NotNull(info));
        Assert.InRange(peak, 2, 4);
    }

    private sealed class SlowHandler(Action enter, Action leave) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            enter();
            try
            {
                await Task.Delay(40, cancellationToken);
                return Json("""{"name":"Slow"}""");
            }
            finally
            {
                leave();
            }
        }
    }

    // --- images ---

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    [Fact]
    public async Task An_image_is_downloaded_once_and_named_by_a_hash_of_its_url()
    {
        var handler = new FakeHandler(_ => Image(Png, "image/png"));
        var client = NewClient(handler);
        var url = new Uri("https://cdn.example.test/a/shot.PNG?v=2");

        var path = await client.GetImageFileAsync(url, CancellationToken.None);

        Assert.NotNull(path);
        Assert.Equal(ImageDir, Path.GetDirectoryName(path));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url.AbsoluteUri)))[..16].ToLowerInvariant();
        Assert.Equal(hash + ".png", Path.GetFileName(path));
        Assert.Equal(Png, await File.ReadAllBytesAsync(path));

        var again = await client.GetImageFileAsync(url, CancellationToken.None);
        Assert.Equal(path, again);
        Assert.Single(handler.Requests);
        Assert.Empty(Directory.EnumerateFiles(ImageDir, "*.tmp"));

        // A brand-new client (a restart) finds the file too.
        var restarted = new FakeHandler(_ => throw new InvalidOperationException("must not be called"));
        Assert.Equal(path, await NewClient(restarted).GetImageFileAsync(url, CancellationToken.None));
        Assert.Empty(restarted.Requests);
    }

    [Theory]
    [InlineData("https://cdn.example.test/x.jpeg", "image/png", ".jpeg")]
    [InlineData("https://cdn.example.test/x.webp", "image/webp", ".webp")]
    [InlineData("https://cdn.example.test/x.bin", "image/png", ".png")]
    [InlineData("https://cdn.example.test/img/12345", "image/jpeg", ".jpg")]
    [InlineData("https://cdn.example.test/img/12345", "image/gif", ".gif")]
    [InlineData("https://cdn.example.test/img/12345", "image/webp", ".webp")]
    [InlineData("https://cdn.example.test/img/12345", "image/bmp", ".img")]
    public async Task The_extension_comes_from_the_url_then_the_content_type(string url, string contentType, string extension)
    {
        var path = await NewClient(new FakeHandler(_ => Image(Png, contentType))).GetImageFileAsync(new Uri(url), CancellationToken.None);

        Assert.NotNull(path);
        Assert.Matches("^[0-9a-f]{16}" + System.Text.RegularExpressions.Regex.Escape(extension) + "$", Path.GetFileName(path));
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/octet-stream")]
    [InlineData("image/svg+xml")]
    [InlineData(null)]
    public async Task Anything_but_a_raster_image_content_type_is_refused(string? contentType)
    {
        var handler = new FakeHandler(_ => Image(Png, contentType));

        Assert.Null(await NewClient(handler).GetImageFileAsync(new Uri("https://cdn.example.test/x.png"), CancellationToken.None));
        Assert.Single(handler.Requests);
        Assert.Empty(ImageFiles());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_image_over_8_MB_is_refused_and_leaves_nothing_behind(bool lengthUnknown)
    {
        var big = new byte[EightMb + 1];
        var handler = new FakeHandler(_ => lengthUnknown
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnsizedContent(big, "image/png") }
            : Image(big, "image/png"));

        Assert.Null(await NewClient(handler).GetImageFileAsync(new Uri("https://cdn.example.test/big.png"), CancellationToken.None));
        Assert.Empty(ImageFiles());
    }

    [Fact]
    public async Task An_image_of_exactly_8_MB_is_accepted()
    {
        var path = await NewClient(new FakeHandler(_ => Image(new byte[EightMb], "image/png"))).GetImageFileAsync(new Uri("https://cdn.example.test/edge.png"), CancellationToken.None);

        Assert.NotNull(path);
        Assert.Equal(EightMb, new FileInfo(path).Length);
    }

    [Theory]
    [InlineData("http://cdn.example.test/x.png")]
    [InlineData("ftp://cdn.example.test/x.png")]
    [InlineData("https://localhost/x.png")]
    [InlineData("https://127.0.0.1/x.png")]
    [InlineData("https://192.168.1.5/x.png")]
    [InlineData("https://10.0.0.8/x.png")]
    [InlineData("https://172.20.1.1/x.png")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://[::1]/x.png")]
    [InlineData("https://[fe80::1]/x.png")]
    public async Task Insecure_or_local_image_urls_are_refused_without_a_request(string url)
    {
        var handler = new FakeHandler(_ => Image(Png, "image/png"));

        Assert.Null(await NewClient(handler).GetImageFileAsync(new Uri(url), CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_relative_uri_is_refused()
    {
        var handler = new FakeHandler(_ => Image(Png, "image/png"));

        Assert.Null(await NewClient(handler).GetImageFileAsync(new Uri("/x.png", UriKind.Relative), CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Image_network_errors_and_empty_bodies_give_null()
    {
        var url = new Uri("https://cdn.example.test/x.png");

        Assert.Null(await NewClient(Offline()).GetImageFileAsync(url, CancellationToken.None));
        Assert.Null(await NewClient(new FakeHandler(_ => Json("gone", HttpStatusCode.NotFound))).GetImageFileAsync(url, CancellationToken.None));
        Assert.Null(await NewClient(new FakeHandler(_ => Image([], "image/png"))).GetImageFileAsync(url, CancellationToken.None));
        Assert.Empty(ImageFiles());
    }

    private IEnumerable<string> ImageFiles() => Directory.Exists(ImageDir) ? Directory.EnumerateFiles(ImageDir) : Enumerable.Empty<string>();

    // --- ids ---

    [Theory]
    [InlineData("https://spacedock.info/mod/1234/Some%20Name", 1234)]
    [InlineData("https://spacedock.info/mod/1234", 1234)]
    [InlineData("https://spacedock.info/mod/1234/", 1234)]
    [InlineData("http://spacedock.info/mod/77/Name?x=1#top", 77)]
    [InlineData("https://www.spacedock.info/mod/9/x", 9)]
    [InlineData("https://SpaceDock.info/mod/5/Name", 5)]
    [InlineData("https://spacedock.info/mod/3/Name/download/1.2", 3)]
    [InlineData(null, null)]
    [InlineData("https://spacedock.info/", null)]
    [InlineData("https://spacedock.info/mod", null)]
    [InlineData("https://spacedock.info/mod/abc", null)]
    [InlineData("https://spacedock.info/mod/12abc/x", null)]
    [InlineData("https://spacedock.info/mod/0", null)]
    [InlineData("https://spacedock.info/mod/-5", null)]
    [InlineData("https://spacedock.info/mod/99999999999", null)]
    [InlineData("https://spacedock.info/user/1234", null)]
    [InlineData("https://spacedock.info/api/mod/1234", null)]
    [InlineData("https://example.com/mod/1234/x", null)]
    [InlineData("https://spacedock.info.evil.test/mod/1/x", null)]
    [InlineData("https://notspacedock.info/mod/1/x", null)]
    [InlineData("ftp://spacedock.info/mod/1/x", null)]
    [InlineData("/mod/1234", null)]
    public void IdFromUrl_reads_only_spacedock_mod_pages(string? url, int? expected)
    {
        var uri = url is null ? null : new Uri(url, UriKind.RelativeOrAbsolute);

        Assert.Equal(expected, SpaceDockClient.IdFromUrl(uri));
    }
    // --- availability ---

    [Fact]
    public async Task A_server_error_marks_SpaceDock_down_and_the_next_answer_brings_it_back()
    {
        var down = true;
        var handler = new FakeHandler(_ => down ? Json("oops", HttpStatusCode.InternalServerError) : Json(Sample));
        var client = NewClient(handler);
        var changes = 0;
        client.AvailabilityChanged += () => Interlocked.Increment(ref changes);

        Assert.Null(client.UnavailableSinceUtc);
        Assert.Null(await client.GetAsync(1, CancellationToken.None));
        Assert.NotNull(client.UnavailableSinceUtc);
        Assert.Null(await client.GetAsync(2, CancellationToken.None));
        Assert.Equal(1, changes); // still down: no second event

        down = false;
        Assert.True(await client.ProbeAsync(CancellationToken.None));
        Assert.Null(client.UnavailableSinceUtc);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task A_404_is_an_answer_not_an_outage()
    {
        var client = NewClient(new FakeHandler(_ => Json("{}", HttpStatusCode.NotFound)));
        Assert.Null(await client.GetAsync(7, CancellationToken.None));
        Assert.Null(client.UnavailableSinceUtc);
    }

    [Fact]
    public async Task No_network_marks_SpaceDock_down()
    {
        var client = NewClient(Offline());
        Assert.Null(await client.GetAsync(3, CancellationToken.None));
        Assert.NotNull(client.UnavailableSinceUtc);
        Assert.False(await client.ProbeAsync(CancellationToken.None));
    }
}
