using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using VmpLauncher.App.Services;
using VmpLauncher.Core.Catalog;

namespace VmpLauncher.App.ViewModels;

/// <summary>
/// One mod in the Browse list. The picture is fetched the first time WPF reads <see cref="Thumbnail"/>, which only
/// happens for rows the virtualized list actually shows.
/// </summary>
public sealed partial class ModRowViewModel : ObservableObject
{
    private static readonly Color[][] Palette =
    {
        new[] { Color.FromRgb(0x5B, 0x3F, 0xD9), Color.FromRgb(0xB6, 0x9C, 0xFF) },
        new[] { Color.FromRgb(0x1E, 0x5B, 0xB8), Color.FromRgb(0x5C, 0xC8, 0xFF) },
        new[] { Color.FromRgb(0x0F, 0x76, 0x6E), Color.FromRgb(0x4A, 0xDE, 0x9F) },
        new[] { Color.FromRgb(0xA1, 0x3D, 0x63), Color.FromRgb(0xFF, 0x8F, 0xB1) },
        new[] { Color.FromRgb(0xB4, 0x5A, 0x12), Color.FromRgb(0xFF, 0xC0, 0x6B) },
        new[] { Color.FromRgb(0x34, 0x3B, 0x8F), Color.FromRgb(0x8E, 0x9B, 0xFF) },
    };

    private static readonly Dictionary<int, Brush> Brushes = new();

    private readonly ModInfoService _info;
    private bool _thumbRequested;
    private ImageSource? _thumbnail;

    public ModRowViewModel(CatalogEntry entry, ModInfoService info)
    {
        _info = info;
        Entry = entry;
    }

    public CatalogEntry Entry { get; private set; }

    public string Identifier => Entry.Identifier;
    public string Name => Entry.Name;
    public string Summary => Entry.Abstract;
    public string Author => Entry.Authors.Length == 0 ? string.Empty : $"by {Entry.Authors}";
    public string Version => CatalogFormat.DisplayVersion(Entry.HasUpdate ? Entry.LatestVersion : Entry.InstalledVersion ?? Entry.LatestVersion);
    public string Downloads => Entry.Downloads <= 0 ? string.Empty : FormatCount(Entry.Downloads);
    public string Initials => string.Concat(Entry.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetterOrDigit(w[0])).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    /// <summary>A mod from Vlad's library (gets the "Vlad" badge and its own accent colour).</summary>
    public bool IsVlad => Entry.IsVlad;

    private static readonly Dictionary<string, Brush> VladBrushes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The showcase accent as a gradient tile: deep at the corner, bright at the far side.</summary>
    private static Brush VladBrush(string accent)
    {
        lock (VladBrushes)
        {
            if (!VladBrushes.TryGetValue(accent, out var b))
            {
                var c = (Color)ColorConverter.ConvertFromString(accent);
                static byte Mix(byte a, byte to, double t) => (byte)Math.Round(a + ((to - a) * t));
                var deep = Color.FromRgb(Mix(c.R, 0, 0.55), Mix(c.G, 0, 0.55), Mix(c.B, 0, 0.55));
                var bright = Color.FromRgb(Mix(c.R, 255, 0.35), Mix(c.G, 255, 0.35), Mix(c.B, 255, 0.35));
                b = new LinearGradientBrush(deep, bright, 45);
                b.Freeze();
                VladBrushes[accent] = b;
            }
            return b;
        }
    }

    public Brush PlaceholderBrush
    {
        get
        {
            if (Entry.Vlad is { } vlad)
            {
                return VladBrush(vlad.Accent);
            }
            // FNV-1a: string.GetHashCode changes every run, and a mod's colour should not.
            var h = 2166136261u;
            foreach (var c in Entry.Identifier)
            {
                h = (h ^ c) * 16777619u;
            }
            var i = (int)(h % (uint)Palette.Length);
            lock (Brushes)
            {
                if (!Brushes.TryGetValue(i, out var b))
                {
                    b = new LinearGradientBrush(Palette[i][0], Palette[i][1], 45);
                    b.Freeze();
                    Brushes[i] = b;
                }
                return b;
            }
        }
    }

    /// <summary>Status badge text: Update, Installed, In pack, New, Incompatible (empty for none).</summary>
    public string Badge => Entry.HasUpdate ? "Update" : Entry.IsInstalled ? (Entry.IsInPack ? "In pack" : "Installed") : Entry.IsAutodetected ? "Installed by hand" : Entry.IsNew ? "New" : !Entry.IsCompatible ? "Not for this KSP" : string.Empty;

    public string BadgeKind => Entry.HasUpdate ? "info" : Entry.IsInstalled || Entry.IsAutodetected ? "ok" : Entry.IsNew ? "info" : !Entry.IsCompatible ? "warn" : "neutral";

    /// <summary>What the player queued for this mod, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionLabel), nameof(ActionIsPrimary), nameof(PendingLabel), nameof(CanActOrUndo))]
    private ChangeAction? _pending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionLabel), nameof(CanAct), nameof(CanActOrUndo))]
    private bool _readOnly;

    public bool CanAct => !ReadOnly && (Entry.IsInstalled || Entry.IsCompatible) && !Entry.IsAutodetected;

    public bool CanActOrUndo => Pending is not null || CanAct;

    public string ActionLabel => Pending is not null ? "Undo"
        : ReadOnly ? string.Empty
        : Entry.HasUpdate ? "Update"
        : Entry.IsInstalled ? "Remove"
        : "Install";

    /// <summary>Only updates get the accent colour, so a long list of Install buttons stays calm.</summary>
    public bool ActionIsPrimary => Pending is null && Entry.HasUpdate;

    public string PendingLabel => Pending switch
    {
        ChangeAction.Install => "Will install",
        ChangeAction.Upgrade => "Will update",
        ChangeAction.Remove => "Will remove",
        _ => string.Empty,
    };

    public ImageSource? Thumbnail
    {
        get
        {
            if (!_thumbRequested)
            {
                _thumbRequested = true;
                _ = LoadThumbnailAsync();
            }
            return _thumbnail;
        }
    }

    private async Task LoadThumbnailAsync()
    {
        try
        {
            var image = await _info.RowImageAsync(Entry).ConfigureAwait(true);
            if (image is not null)
            {
                _thumbnail = image;
                OnPropertyChanged(nameof(Thumbnail));
            }
        }
        catch (OperationCanceledException)
        {
            // Dropped from a long queue after the player scrolled on: ask again when this row is shown again.
            _thumbRequested = false;
        }
        catch (Exception)
        {
            // Pictures are decoration; the placeholder stays.
        }
    }

    /// <summary>Asks for the picture again (SpaceDock came back after an outage) if this row has none yet.</summary>
    public void RetryThumbnail()
    {
        if (_thumbnail is null)
        {
            _thumbRequested = false;
            OnPropertyChanged(nameof(Thumbnail));
        }
    }

    /// <summary>Takes fresh catalog data (after a refresh or an apply) without losing the loaded picture.</summary>
    public void Update(CatalogEntry entry)
    {
        Entry = entry;
        OnPropertyChanged(string.Empty);
    }

    public static string FormatCount(int n) => n switch
    {
        >= 1_000_000 => (n / 1_000_000d).ToString("0.#", CultureInfo.CurrentCulture) + "M",
        >= 1_000 => (n / 1_000d).ToString("0.#", CultureInfo.CurrentCulture) + "k",
        _ => n.ToString(CultureInfo.CurrentCulture),
    };
}
