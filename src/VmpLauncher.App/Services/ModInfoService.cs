using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VmpLauncher.Core;
using VmpLauncher.Core.Catalog;
using VmpLauncher.Core.ModInfo;

namespace VmpLauncher.App.Services;

/// <summary>
/// SpaceDock pages, pictures and descriptions for catalog mods, cached on disk for a week (under the data folder, not
/// the download cache, so cache cleanup never touches them). Images are decoded off the UI thread at display size.
/// </summary>
public sealed class ModInfoService
{
    private readonly SpaceDockClient _client;
    private readonly ConcurrentDictionary<int, Task<ModInfo?>> _infos = new();
    private readonly ConcurrentDictionary<string, Task<ImageSource?>> _images = new(StringComparer.Ordinal);

    public ModInfoService(LauncherPaths paths)
    {
        _client = new SpaceDockClient(Path.Combine(paths.DataRoot, "modinfo"));
    }

    public Task<ModInfo?> GetAsync(CatalogEntry entry)
        => SpaceDockClient.IdFromUrl(entry.SpaceDock) is int id
            ? _infos.GetOrAdd(id, i => _client.GetAsync(i, CancellationToken.None))
            : Task.FromResult<ModInfo?>(null);

    /// <summary>The mod's SpaceDock header picture at <paramref name="width"/> pixels, or null.</summary>
    public async Task<ImageSource?> HeaderImageAsync(CatalogEntry entry, int width)
    {
        var info = await GetAsync(entry).ConfigureAwait(false);
        return info?.HeaderImage is { } url ? await ImageAsync(url, width).ConfigureAwait(false) : null;
    }

    public Task<ImageSource?> ImageAsync(Uri url, int width)
        => _images.GetOrAdd($"{width}|{url}", _ => LoadAsync(url, width));

    private async Task<ImageSource?> LoadAsync(Uri url, int width)
    {
        var file = await _client.GetImageFileAsync(url, CancellationToken.None).ConfigureAwait(false);
        if (file is null)
        {
            return null;
        }
        return await Task.Run(() =>
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.DecodePixelWidth = width;
                bmp.UriSource = new Uri(file);
                bmp.EndInit();
                bmp.Freeze();
                return (ImageSource?)bmp;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
            {
                return null;
            }
        }).ConfigureAwait(false);
    }
}
