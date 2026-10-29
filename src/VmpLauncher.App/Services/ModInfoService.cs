using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VmpLauncher.Core;
using VmpLauncher.Core.Catalog;
using VmpLauncher.Core.ModInfo;

namespace VmpLauncher.App.Services;

/// <summary>
/// SpaceDock pages, pictures and descriptions for catalog mods. SpaceDock only serves full-size pictures (about 1 MB,
/// up to several MB), so each one is downloaded once, shrunk to the size it is shown at, and kept as a small JPEG under
/// <c>modinfo\thumbs</c>; the original is deleted. After that a picture loads from disk in a few milliseconds.
/// Downloads run newest-first, so the rows on screen load before rows the player already scrolled past.
/// </summary>
public sealed class ModInfoService
{
    /// <summary>Row tiles are drawn at 58 DIP; 160 px stays sharp up to about 275 % display scaling.</summary>
    public const int RowWidth = 160;

    /// <summary>Header and description pictures on the mod page (460 DIP wide pane).</summary>
    public const int PageWidth = 900;

    private static readonly TimeSpan ThumbMaxAge = TimeSpan.FromDays(7);

    private readonly SpaceDockClient _client;
    private readonly string _thumbDir;
    private readonly LifoScheduler _scheduler = new(workers: 8, maxPending: 240);
    private readonly ConcurrentDictionary<int, Task<ModInfo?>> _infos = new();
    private readonly ConcurrentDictionary<string, Task<ImageSource?>> _images = new(StringComparer.Ordinal);

    private int _probing;

    public ModInfoService(LauncherPaths paths)
    {
        var root = Path.Combine(paths.DataRoot, "modinfo");
        _client = new SpaceDockClient(root, maxParallelRequests: 8);
        _thumbDir = Path.Combine(root, "thumbs");
        _client.AvailabilityChanged += () =>
        {
            if (IsSpaceDockDown)
            {
                _ = ProbeUntilBackAsync();
            }
            SpaceDockAvailabilityChanged?.Invoke();
        };
    }

    /// <summary>SpaceDock is failing right now (it is the only source of mod pictures and long descriptions).</summary>
    public bool IsSpaceDockDown => _client.UnavailableSinceUtc is not null;

    /// <summary>Raised on a background thread when SpaceDock goes down or comes back.</summary>
    public event Action? SpaceDockAvailabilityChanged;

    /// <summary>While SpaceDock is down: one check a minute instead of a request per row.</summary>
    private async Task ProbeUntilBackAsync()
    {
        if (Interlocked.Exchange(ref _probing, 1) == 1)
        {
            return;
        }
        try
        {
            while (IsSpaceDockDown)
            {
                await Task.Delay(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                await _client.ProbeAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // A failed probe just means another one in a minute (the loop condition is re-read).
        }
        finally
        {
            Interlocked.Exchange(ref _probing, 0);
        }
    }

    public Task<ModInfo?> GetAsync(CatalogEntry entry)
    {
        if (SpaceDockClient.IdFromUrl(entry.SpaceDock) is not int id)
        {
            return Task.FromResult<ModInfo?>(null);
        }
        var task = _infos.GetOrAdd(id, i => _client.GetAsync(i, CancellationToken.None));
        ForgetIfOutage(task, () => _infos.TryRemove(new KeyValuePair<int, Task<ModInfo?>>(id, task)));
        return task;
    }

    /// <summary>A result that came back empty because SpaceDock was down is not kept, so it is asked again later.</summary>
    private void ForgetIfOutage<T>(Task<T?> task, Action forget) where T : class
        => _ = task.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && t.Result is null && IsSpaceDockDown)
            {
                forget();
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>
    /// The mod's SpaceDock picture at row size, or null (not on SpaceDock, no picture). A cached tile loads straight
    /// from disk; otherwise the work is queued newest-first. A request dropped from a long queue throws
    /// <see cref="OperationCanceledException"/> so the row can ask again when it is shown again.
    /// </summary>
    public Task<ImageSource?> RowImageAsync(CatalogEntry entry)
    {
        if (SpaceDockClient.IdFromUrl(entry.SpaceDock) is not int id)
        {
            return Task.FromResult<ImageSource?>(null);
        }
        var key = $"row|{id}";
        if (_images.TryGetValue(key, out var existing) && !existing.IsCanceled && !existing.IsFaulted)
        {
            return existing;
        }
        var file = Path.Combine(_thumbDir, $"sd{id}-{RowWidth}.jpg");
        if (!IsFresh(file) && IsSpaceDockDown)
        {
            return Task.FromResult<ImageSource?>(null); // nothing cached and nobody to ask: placeholder until it is back
        }
        var task = IsFresh(file)
            ? Task.Run<ImageSource?>(() => Decode(file, 0))
            : _scheduler.Run(async () =>
            {
                var info = await GetAsync(entry).ConfigureAwait(false);
                return info?.HeaderImage is { } url ? await DeriveAsync(url, RowWidth, file).ConfigureAwait(false) : null;
            });
        _images[key] = task;
        ForgetIfOutage(task, () => _images.TryRemove(new KeyValuePair<string, Task<ImageSource?>>(key, task)));
        return task;
    }

    /// <summary>The mod page's header picture.</summary>
    public async Task<ImageSource?> HeaderImageAsync(CatalogEntry entry)
    {
        var info = await GetAsync(entry).ConfigureAwait(false);
        return info?.HeaderImage is { } url ? await ImageAsync(url, PageWidth).ConfigureAwait(false) : null;
    }

    /// <summary>Any https picture (headers, screenshots in descriptions) at <paramref name="width"/> pixels.</summary>
    public Task<ImageSource?> ImageAsync(Uri url, int width)
    {
        var key = $"{width}|{url.AbsoluteUri}";
        if (_images.TryGetValue(key, out var existing) && !existing.IsCanceled && !existing.IsFaulted)
        {
            return existing;
        }
        var file = Path.Combine(_thumbDir, $"{Hash(url)}-{width}.jpg");
        var task = IsFresh(file)
            ? Task.Run<ImageSource?>(() => Decode(file, 0))
            : _scheduler.Run(() => DeriveAsync(url, width, file));
        _images[key] = task;
        ForgetIfOutage(task, () => _images.TryRemove(new KeyValuePair<string, Task<ImageSource?>>(key, task)));
        return task;
    }

    /// <summary>Downloads the original once, shrinks it, saves the small copy and deletes the original.</summary>
    private async Task<ImageSource?> DeriveAsync(Uri url, int width, string target)
    {
        var original = await _client.GetImageFileAsync(url, CancellationToken.None).ConfigureAwait(false);
        if (original is null)
        {
            return null;
        }
        var image = Decode(original, width);
        if (image is { } bitmap)
        {
            try
            {
                Directory.CreateDirectory(_thumbDir);
                var tmp = target + ".tmp";
                using (var fs = File.Create(tmp))
                {
                    var encoder = new JpegBitmapEncoder { QualityLevel = 88 };
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    encoder.Save(fs);
                }
                File.Move(tmp, target, overwrite: true);
                // The small copy is all the launcher shows; originals average about 1 MB each.
                File.Delete(original);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
            {
                // Keep the original; the picture still shows and the next run tries again.
            }
        }
        return image;
    }

    private static bool IsFresh(string file)
    {
        try
        {
            return File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < ThumbMaxAge;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Hash(Uri url) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url.AbsoluteUri)))[..16].ToLowerInvariant();

    /// <param name="width">Decode width in pixels, or 0 for the file's own size (already shrunk).</param>
    private static BitmapImage? Decode(string file, int width)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (width > 0)
            {
                bmp.DecodePixelWidth = width;
            }
            bmp.UriSource = new Uri(file);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Runs at most <c>workers</c> jobs at once, always starting the most recently queued one next. When more than
    /// <c>maxPending</c> are waiting, the oldest are cancelled (their rows ask again if they come back on screen).
    /// </summary>
    private sealed class LifoScheduler(int workers, int maxPending)
    {
        private readonly object _gate = new();
        private readonly LinkedList<(Func<Task> Start, Action Cancel)> _pending = new();
        private int _running;

        public Task<T> Run<T>(Func<Task<T>> work)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Start()
            {
                try
                {
                    tcs.TrySetResult(await work().ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }
            lock (_gate)
            {
                _pending.AddFirst((Start, () => tcs.TrySetCanceled()));
                while (_pending.Count > maxPending)
                {
                    var oldest = _pending.Last!.Value;
                    _pending.RemoveLast();
                    oldest.Cancel();
                }
            }
            Pump();
            return tcs.Task;
        }

        private void Pump()
        {
            while (true)
            {
                Func<Task> start;
                lock (_gate)
                {
                    if (_running >= workers || _pending.Count == 0)
                    {
                        return;
                    }
                    start = _pending.First!.Value.Start;
                    _pending.RemoveFirst();
                    _running++;
                }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await start().ConfigureAwait(false);
                    }
                    finally
                    {
                        lock (_gate)
                        {
                            _running--;
                        }
                        Pump();
                    }
                });
            }
        }
    }
}
