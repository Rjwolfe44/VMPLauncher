using System.IO;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using VmpLauncher.Core;
using VmpLauncher.Core.Catalog;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Net.Library;

namespace VmpLauncher.App.Services;

/// <summary>What the "Vlad's mods" tab shows about the library itself.</summary>
public sealed record LibraryStatus(string Name, string Tagline, long Sequence, DateTimeOffset IssuedAt, string? KeyId, bool FromCache, string? Warning, int Mods);

/// <summary>
/// Vlad's mod library: downloads the signed index, checks it against the publisher key built into the launcher, and keeps
/// the last good copy for offline use. A problem here never breaks the Mods page: the tab just says why it is empty.
/// </summary>
public sealed class LibraryService
{
    /// <summary>Where the host publishes the library (the public packs repository).</summary>
    public const string DefaultUrl = "https://raw.githubusercontent.com/Rjwolfe44/VMP-Packs/main/library/library.json";

    private static readonly TimeSpan RecheckAfter = TimeSpan.FromMinutes(10);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly LauncherPaths _paths;
    private readonly AppSettingsStore _settings;
    private readonly ILogger<LibraryService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LibraryResult? _current;
    private DateTimeOffset _checkedAt;

    public LibraryService(LauncherPaths paths, AppSettingsStore settings, ILogger<LibraryService> log)
    {
        _paths = paths;
        _settings = settings;
        _log = log;
    }

    /// <summary>The newest verified library this session has, or null.</summary>
    public LibraryResult? Current => _current;

    /// <summary>Why there is no library right now (nothing published yet, offline with nothing saved, a refused signature), or null.</summary>
    public string? Problem { get; private set; }

    /// <summary>Checks for a newer library (at most every 10 minutes unless forced) and returns the best verified copy, or null.</summary>
    public async Task<LibraryResult?> GetAsync(bool force, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!force && _current is not null && DateTimeOffset.UtcNow - _checkedAt < RecheckAfter)
            {
                return _current;
            }
            var client = new LibraryClient(_paths, Http, new CoreSignedDocumentVerifier());
            try
            {
                var local = _settings.Current.LocalLibraryDir;
                var result = !string.IsNullOrWhiteSpace(local) && Directory.Exists(local)
                    ? client.LoadFromFolder(local)
                    : await client.FetchAsync(new Uri(DefaultUrl), ct).ConfigureAwait(false);
                _current = result;
                Problem = null;
                if (result.Warning is not null)
                {
                    _log.LogInformation("Vlad's mods: {Warning}", result.Warning);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is LauncherException or IOException or HttpRequestException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Vlad's mods could not be refreshed");
                _current ??= client.LoadCached();
                Problem = _current is null ? Describe(ex) : null;
            }
            _checkedAt = DateTimeOffset.UtcNow;
            return _current;
        }
        finally
        {
            _gate.Release();
        }
    }

    public LibraryStatus? Status() => _current is { } r
        ? new LibraryStatus(r.Index.Name, r.Index.Tagline, r.Index.Sequence, r.Index.IssuedAt, r.KeyId, r.IsFromCache, r.Warning, r.Index.Identifiers.Count)
        : null;

    /// <summary>The verified index in the form the catalog takes.</summary>
    public static VladLibrary ToCatalogLibrary(LibraryResult result)
    {
        var index = result.Index;
        return new VladLibrary(
            index.Name, index.Tagline, index.Sequence, index.IssuedAt,
            index.Modules.Select(m => new VladLibraryModule(m.Identifier, m.Version, m.Json.GetRawText())).ToList(),
            index.Showcase.ToDictionary(s => s.Key, s => new VladShowcase(s.Value.Tagline, s.Value.Highlights, s.Value.Accent, s.Value.Order, s.Value.Banner), StringComparer.Ordinal));
    }

    private static string Describe(Exception ex) => ex switch
    {
        LauncherException le when le.Code == ErrorCode.Tampered => "The mod library didn't pass its signature check, so it was not used.",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } => "Vlad hasn't published the library yet.",
        LauncherException { InnerException: HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } } => "Vlad hasn't published the library yet.",
        _ => "The library couldn't be downloaded right now. It will try again.",
    };
}
