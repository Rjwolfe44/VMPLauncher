using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmpLauncher.App.Services;
using VmpLauncher.Core.Catalog;
using VmpLauncher.Core.ModInfo;

namespace VmpLauncher.App.ViewModels;

/// <summary>A piece of a mod's description: text or a picture.</summary>
public abstract class DescriptionBlock;

public sealed class DescriptionText(string text, double size, bool bold, bool bullet) : DescriptionBlock
{
    public string Text { get; } = text;
    public double Size { get; } = size;
    public bool Bold { get; } = bold;
    public bool Bullet { get; } = bullet;
}

public sealed partial class DescriptionImage : DescriptionBlock, System.ComponentModel.INotifyPropertyChanged
{
    private readonly ModInfoService _info;
    private bool _requested;
    private ImageSource? _image;

    public DescriptionImage(Uri url, ModInfoService info)
    {
        Url = url;
        _info = info;
    }

    public Uri Url { get; }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public ImageSource? Image
    {
        get
        {
            if (!_requested)
            {
                _requested = true;
                _ = LoadAsync();
            }
            return _image;
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            _image = await _info.ImageAsync(Url, ModInfoService.PageWidth).ConfigureAwait(true);
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Image)));
        }
        catch (Exception)
        {
            // Pictures are decoration: a failed or dropped download just leaves the text.
        }
    }
}

public sealed record ModLink(string Label, Uri Url);

public sealed record RelationChip(string Text, string? Identifier, bool Satisfied, bool Known);

public sealed record RelationGroup(string Title, IReadOnlyList<RelationChip> Items);

public sealed record VersionRow(string Version, string GameVersions, string Released, string Size, bool IsCompatible, bool IsInstalled, string Status, string? Changelog);

/// <summary>The mod page shown beside the list: header picture, description, links, relations and versions.</summary>
public sealed partial class ModDetailsViewModel : ObservableObject
{
    private readonly ModInfoService _info;
    private readonly Func<string, bool> _isKnown;
    private readonly Action<string> _open;

    public ModDetailsViewModel(ModRowViewModel row, ModInfoService info, Func<string, bool> isKnown, Action<string> open)
    {
        Row = row;
        _info = info;
        _isKnown = isKnown;
        _open = open;
    }

    public ModRowViewModel Row { get; }
    public CatalogEntry Entry => Row.Entry;

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private ImageSource? _header;
    [ObservableProperty] private string _stats = string.Empty;
    [ObservableProperty] private string? _followers;
    [ObservableProperty] private string? _note;
    [ObservableProperty] private bool _hasVersions;

    public ObservableCollection<DescriptionBlock> Description { get; } = new();
    public ObservableCollection<ModLink> Links { get; } = new();
    public ObservableCollection<RelationGroup> Relations { get; } = new();
    public ObservableCollection<VersionRow> Versions { get; } = new();

    public async Task LoadAsync(Task<ModDetails> details)
    {
        var e = Entry;
        Stats = string.Join("  ·  ", new[]
        {
            e.Downloads > 0 ? $"{ModRowViewModel.FormatCount(e.Downloads)} downloads" : null,
            e.DownloadSize > 0 ? Format.Bytes(e.DownloadSize) : null,
            e.GameVersions.Length > 0 ? e.GameVersions : null,
            e.License is { Length: > 0 } l ? l : null,
            e.ReleaseDate is { } d ? $"updated {d.ToString("d MMM yyyy", CultureInfo.CurrentCulture)}" : null,
        }.Where(s => s is not null));
        AddLink("SpaceDock", e.SpaceDock);
        AddLink("Homepage", e.Homepage);
        AddLink("Source code", e.Repository);
        AddLink("Forum", e.Forum);
        AddLink("Report a bug", e.BugTracker);

        var infoTask = _info.GetAsync(e);
        ModDetails? ckan = null;
        try
        {
            ckan = await details.ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Note = "Could not load this mod's details: " + ErrorCatalog.From(ex).Message;
        }
        ModInfo? sd = null;
        try
        {
            sd = await infoTask.ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // SpaceDock is optional.
        }

        if (sd is not null)
        {
            Followers = sd.Followers > 0 ? $"{ModRowViewModel.FormatCount(sd.Followers)} followers on SpaceDock" : null;
            AddLink("Website", sd.Website);
            AddLink("Donate", sd.Donations);
            if (sd.HeaderImage is { } h)
            {
                _ = LoadHeaderAsync(h);
            }
        }
        var markdown = sd?.Description is { Length: > 0 } md ? md : ckan?.Description is { Length: > 0 } cd ? cd : e.Abstract;
        var pictures = 0;
        foreach (var block in MarkdownLite.Parse(markdown))
        {
            switch (block)
            {
                case MdHeading hd:
                    Description.Add(new DescriptionText(hd.Text, hd.Level <= 1 ? 20 : hd.Level == 2 ? 17 : 15, true, false));
                    break;
                case MdParagraph p:
                    Description.Add(new DescriptionText(p.Text, 14, false, false));
                    break;
                case MdListItem li:
                    Description.Add(new DescriptionText(li.Text, 14, false, true));
                    break;
                case MdImage img when pictures < 12:
                    pictures++;
                    Description.Add(new DescriptionImage(img.Url, _info));
                    break;
            }
        }

        if (ckan is not null)
        {
            string Title(string kind) => kind switch
            {
                "depends" => "Needs",
                "recommends" => "Recommends",
                "suggests" => "Suggests",
                "supports" => "Works with",
                "conflicts" => "Conflicts with",
                _ => kind,
            };
            foreach (var g in ckan.Relations.GroupBy(r => r.Kind))
            {
                Relations.Add(new RelationGroup(Title(g.Key), g.Select(r => new RelationChip(r.Text, r.Identifier, r.Satisfied, r.Identifier is { } id && _isKnown(id))).ToList()));
            }
            var changelogs = sd?.Versions.GroupBy(v => Normalize(v.FriendlyVersion)).ToDictionary(g => g.Key, g => g.First().Changelog) ?? new Dictionary<string, string?>();
            foreach (var v in ckan.Versions.Take(25))
            {
                changelogs.TryGetValue(Normalize(CatalogFormat.DisplayVersion(v.Version)), out var log);
                Versions.Add(new VersionRow(
                    CatalogFormat.DisplayVersion(v.Version),
                    v.GameVersions,
                    v.ReleaseDate?.ToString("d MMM yyyy", CultureInfo.CurrentCulture) ?? string.Empty,
                    v.DownloadSize > 0 ? Format.Bytes(v.DownloadSize) : string.Empty,
                    v.IsCompatible,
                    v.IsInstalled,
                    v.ReleaseStatus == "stable" ? string.Empty : v.ReleaseStatus,
                    string.IsNullOrWhiteSpace(log) ? null : MarkdownLite.ToPlainText(log, 1200)));
            }
        }
        HasVersions = Versions.Count > 0;
        IsLoading = false;
    }

    private static string Normalize(string v) => v.Trim().TrimStart('v', 'V');

    private async Task LoadHeaderAsync(Uri url)
    {
        try
        {
            Header = await _info.ImageAsync(url, ModInfoService.PageWidth).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // The coloured placeholder header stays.
        }
    }

    private void AddLink(string label, Uri? url)
    {
        if (url is not null && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp) && Links.All(l => l.Url != url))
        {
            Links.Add(new ModLink(label, url));
        }
    }

    [RelayCommand]
    private void OpenLink(ModLink link) => Process.Start(new ProcessStartInfo(link.Url.AbsoluteUri) { UseShellExecute = true });

    [RelayCommand]
    private void OpenRelation(RelationChip chip)
    {
        if (chip.Identifier is { } id && chip.Known)
        {
            _open(id);
        }
    }
}
