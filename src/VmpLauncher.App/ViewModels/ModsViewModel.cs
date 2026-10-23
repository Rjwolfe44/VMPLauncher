using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using VmpLauncher.App.Services;
using VmpLauncher.Core.Catalog;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;

namespace VmpLauncher.App.ViewModels;

public sealed record FilterOption(string Label, CatalogFilter Value);

public sealed record SortOption(string Label, CatalogSort Value);

/// <summary>
/// The mod manager: every CKAN mod for the chosen setup, searchable and filterable, with a detail panel and a queue
/// of changes the player reviews and applies in one go. The pack's own setup is browse-only.
/// </summary>
public sealed partial class ModsViewModel : ObservableObject
{
    private const string AllTags = "All tags";

    private readonly SetupService _setups;
    private readonly ModManagerService _mods;
    private readonly ModInfoService _info;
    private readonly LauncherService _launcher;
    private readonly OperationRunner _runner;
    private readonly IContentDialogService _dialogs;
    private readonly AppSettingsStore _settings;
    private readonly ILogger<ModsViewModel> _log;
    private readonly DispatcherTimer _debounce;
    private readonly Dictionary<string, ModRowViewModel> _rowCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChangeAction> _pending = new(StringComparer.Ordinal);
    private CatalogSnapshot? _snapshot;
    private CatalogIndex? _index;
    private CancellationTokenSource? _loadCts;
    private Task _loading = Task.CompletedTask;
    private bool _suppressSetup;
    private bool _loadedOnce;

    public ModsViewModel(SetupService setups, ModManagerService mods, ModInfoService info, LauncherService launcher, OperationRunner runner,
        IContentDialogService dialogs, AppSettingsStore settings, ILogger<ModsViewModel> log)
    {
        _setups = setups;
        _mods = mods;
        _info = info;
        _launcher = launcher;
        _runner = runner;
        _dialogs = dialogs;
        _settings = settings;
        _log = log;
        _filter = Filters[0];
        _sort = Sorts[0];
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Requery();
        };
        setups.Changed += () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(RefreshSetups);
        // SpaceDock (pictures, long descriptions) going down shows a notice; coming back fills the pictures in.
        info.SpaceDockAvailabilityChanged += () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var wasDown = SpaceDockDown;
            SpaceDockDown = _info.IsSpaceDockDown;
            if (wasDown && !SpaceDockDown)
            {
                foreach (var row in _rowCache.Values)
                {
                    row.RetryThumbnail();
                }
                if (Details is { } open)
                {
                    OpenDetails(open.Row);
                }
            }
        });
        // An installed or updated pack changes the pack setup's pins.
        launcher.StateChanged += () =>
        {
            if (_loadedOnce && SelectedSetup?.IsPack == true && !IsLoading)
            {
                _ = ReloadSnapshotAsync();
            }
        };
    }

    public IReadOnlyList<FilterOption> Filters { get; } = new[]
    {
        new FilterOption("Compatible", CatalogFilter.Compatible),
        new FilterOption("Not installed", CatalogFilter.NotInstalled),
        new FilterOption("New", CatalogFilter.New),
        new FilterOption("Already downloaded", CatalogFilter.Cached),
        new FilterOption("Not for this KSP", CatalogFilter.Incompatible),
        new FilterOption("Everything", CatalogFilter.All),
    };

    public IReadOnlyList<SortOption> Sorts { get; } = new[]
    {
        new SortOption("Most popular", CatalogSort.Popular),
        new SortOption("Recently updated", CatalogSort.RecentlyUpdated),
        new SortOption("Name", CatalogSort.Name),
        new SortOption("Biggest", CatalogSort.Size),
    };

    public ObservableCollection<SetupInfo> Setups { get; } = new();
    public ObservableCollection<string> Tags { get; } = new() { AllTags };

    [ObservableProperty] private SetupInfo? _selectedSetup;
    [ObservableProperty] private IReadOnlyList<ModRowViewModel> _visibleRows = Array.Empty<ModRowViewModel>();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrowseTab))]
    private string _tab = "browse"; // browse | installed | updates

    public bool IsBrowseTab => Tab == "browse";
    [ObservableProperty] private string _search = string.Empty;
    [ObservableProperty] private FilterOption _filter;
    [ObservableProperty] private SortOption _sort;
    [ObservableProperty] private string _tag = AllTags;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _loadingText = string.Empty;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private string _catalogLine = string.Empty;
    [ObservableProperty] private string _countLine = string.Empty;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _isReadOnly;
    [ObservableProperty] private string _readOnlyReason = string.Empty;
    [ObservableProperty] private int _installedCount;
    [ObservableProperty] private int _updateCount;
    [ObservableProperty] private ModDetailsViewModel? _details;
    [ObservableProperty] private ModRowViewModel? _selectedRow;
    [ObservableProperty] private string _pendingLine = string.Empty;
    [ObservableProperty] private bool _hasPending;
    [ObservableProperty] private UserFacingError? _error;
    [ObservableProperty] private bool _spaceDockDown;

    public bool CanPlaySetup => SelectedSetup is { IsPack: false, IsReady: true };
    public bool CanManageSetup => SelectedSetup is { IsPack: false };
    public bool IsPackSetup => SelectedSetup?.IsPack == true;
    public string SetupKind => SelectedSetup is { } s ? $"{s.KindLabel} · {s.GameDir}" : string.Empty;

    partial void OnSearchChanged(string value)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    partial void OnSelectedRowChanged(ModRowViewModel? value)
    {
        if (value is not null && Details?.Row != value)
        {
            OpenDetails(value);
        }
    }

    partial void OnDetailsChanged(ModDetailsViewModel? value)
    {
        if (value is null)
        {
            SelectedRow = null;
        }
    }

    partial void OnFilterChanged(FilterOption value) => Requery();
    partial void OnSortChanged(SortOption value) => Requery();
    partial void OnTagChanged(string value) => Requery();
    partial void OnTabChanged(string value) => Requery();

    partial void OnSelectedSetupChanged(SetupInfo? value)
    {
        OnPropertyChanged(nameof(CanPlaySetup));
        OnPropertyChanged(nameof(CanManageSetup));
        OnPropertyChanged(nameof(IsPackSetup));
        OnPropertyChanged(nameof(SetupKind));
        if (_suppressSetup || value is null)
        {
            return;
        }
        _setups.Select(value.Id);
        _pending.Clear();
        UpdatePendingLine();
        Details = null;
        _loading = LoadAsync();
    }

    [RelayCommand]
    private void SetTab(string tab) => Tab = tab;

    private void RefreshSetups()
    {
        var list = _setups.List();
        var active = _setups.Active;
        _suppressSetup = true;
        try
        {
            Setups.Clear();
            foreach (var s in list)
            {
                Setups.Add(s);
            }
            SelectedSetup = Setups.FirstOrDefault(s => s.Id == active.Id) ?? Setups.FirstOrDefault();
        }
        finally
        {
            _suppressSetup = false;
        }
    }

    /// <summary>Called when the page is shown: loads the catalog the first time.</summary>
    public async Task OnShownAsync()
    {
        if (!_settings.Current.ModsOpened)
        {
            _settings.Update(s => s with { ModsOpened = true });
        }
        var before = SelectedSetup?.Id;
        RefreshSetups();
        if (!_loadedOnce || SelectedSetup?.Id != before)
        {
            await LoadAsync().ConfigureAwait(true);
        }
    }

    private async Task LoadAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var setup = SelectedSetup ?? _setups.Active;
        IsLoading = true;
        Error = null;
        try
        {
            LoadingText = "Loading the CKAN mod list…";
            var progress = new Progress<Ckan.Users.CkanProgress>(p =>
            {
                if (!string.IsNullOrWhiteSpace(p.Message))
                {
                    LoadingText = p.Message;
                }
            });
            var (_, stale) = await _mods.EnsureCatalogAsync(progress, cts.Token).ConfigureAwait(true);
            LoadingText = $"Checking mods for {setup.Name}…";
            var snapshot = await _mods.SnapshotAsync(setup, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
            {
                return;
            }
            ApplySnapshot(snapshot);
            _loadedOnce = true;
            if (stale)
            {
                _ = RefreshCatalogAsync(force: false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Loading the mod catalog failed");
            Error = ErrorCatalog.From(ex);
        }
        finally
        {
            if (_loadCts == cts)
            {
                IsLoading = false;
            }
        }
    }

    private async Task ReloadSnapshotAsync()
    {
        var setup = SelectedSetup;
        if (setup is null)
        {
            return;
        }
        try
        {
            ApplySnapshot(await _mods.SnapshotAsync(setup, CancellationToken.None).ConfigureAwait(true));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Reloading the mod list failed");
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => RefreshCatalogAsync(force: true);

    private async Task RefreshCatalogAsync(bool force)
    {
        if (IsRefreshing)
        {
            return;
        }
        IsRefreshing = true;
        try
        {
            var outcome = await _mods.UpdateCatalogAsync(force, null, CancellationToken.None).ConfigureAwait(true);
            if (outcome.Changed || force)
            {
                await ReloadSnapshotAsync().ConfigureAwait(true);
            }
            UpdateCatalogLine();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Catalog refresh failed");
            CatalogLine = "Could not refresh the mod list (offline?). Showing the copy you have.";
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private void UpdateCatalogLine()
    {
        var u = _mods.CatalogUpdatedUtc;
        var count = _snapshot?.Entries.Count ?? 0;
        CatalogLine = $"{count:N0} mods for KSP {_snapshot?.GameVersion}" + (u is { } t ? $" · list updated {Format.Ago(t)}" : string.Empty);
    }

    private void ApplySnapshot(CatalogSnapshot snapshot)
    {
        _snapshot = snapshot;
        _index = new CatalogIndex(snapshot.Entries);
        IsReadOnly = snapshot.ReadOnly;
        ReadOnlyReason = snapshot.ReadOnlyReason ?? string.Empty;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in snapshot.Entries)
        {
            seen.Add(e.Identifier);
            if (_rowCache.TryGetValue(e.Identifier, out var row))
            {
                row.Update(e);
            }
            else
            {
                _rowCache[e.Identifier] = new ModRowViewModel(e, _info);
            }
            _rowCache[e.Identifier].ReadOnly = snapshot.ReadOnly;
        }
        foreach (var gone in _rowCache.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _rowCache.Remove(gone);
        }
        // Queued changes that no longer make sense (already applied, mod gone) drop out.
        foreach (var id in _pending.Keys.ToList())
        {
            if (!_rowCache.TryGetValue(id, out var r) || !Valid(r.Entry, _pending[id]))
            {
                _pending.Remove(id);
            }
        }
        foreach (var r in _rowCache.Values)
        {
            r.Pending = _pending.TryGetValue(r.Identifier, out var a) ? a : null;
        }
        InstalledCount = snapshot.Entries.Count(e => e.IsInstalled || e.IsAutodetected);
        UpdateCount = snapshot.Entries.Count(e => e.HasUpdate);
        var tag = Tag;
        Tags.Clear();
        Tags.Add(AllTags);
        foreach (var t in snapshot.Tags)
        {
            Tags.Add(t);
        }
        Tag = Tags.Contains(tag) ? tag : AllTags;
        UpdateCatalogLine();
        UpdatePendingLine();
        Requery();
        if (Details is { } d && _rowCache.TryGetValue(d.Entry.Identifier, out var current))
        {
            OpenDetails(current);
        }
    }

    private static bool Valid(CatalogEntry e, ChangeAction a) => a switch
    {
        ChangeAction.Install => !e.IsInstalled,
        ChangeAction.Upgrade => e.HasUpdate,
        _ => e.IsInstalled,
    };

    private void Requery()
    {
        if (_index is null)
        {
            return;
        }
        var filter = Tab switch
        {
            "installed" => CatalogFilter.Installed,
            "updates" => CatalogFilter.Updates,
            _ => Filter.Value,
        };
        var list = _index.Query(Search, filter, Tag == AllTags ? null : Tag, Sort.Value);
        VisibleRows = list.Select(e => _rowCache[e.Identifier]).ToList();
        IsEmpty = VisibleRows.Count == 0;
        CountLine = VisibleRows.Count == 1 ? "1 mod" : $"{VisibleRows.Count:N0} mods";
    }

    [RelayCommand]
    private void Act(ModRowViewModel row)
    {
        if (row.Pending is not null)
        {
            _pending.Remove(row.Identifier);
            row.Pending = null;
        }
        else if (!row.CanAct)
        {
            return;
        }
        else
        {
            var action = row.Entry.HasUpdate ? ChangeAction.Upgrade : row.Entry.IsInstalled ? ChangeAction.Remove : ChangeAction.Install;
            _pending[row.Identifier] = action;
            row.Pending = action;
        }
        UpdatePendingLine();
    }

    [RelayCommand]
    private void RemoveInstalled(ModRowViewModel row)
    {
        if (row.ReadOnly || !row.Entry.IsInstalled)
        {
            return;
        }
        _pending[row.Identifier] = ChangeAction.Remove;
        row.Pending = ChangeAction.Remove;
        UpdatePendingLine();
    }

    [RelayCommand]
    private void UpdateAll()
    {
        foreach (var r in _rowCache.Values.Where(r => r.Entry.HasUpdate && !r.ReadOnly))
        {
            _pending[r.Identifier] = ChangeAction.Upgrade;
            r.Pending = ChangeAction.Upgrade;
        }
        UpdatePendingLine();
    }

    [RelayCommand]
    private void ClearPending()
    {
        _pending.Clear();
        foreach (var r in _rowCache.Values)
        {
            r.Pending = null;
        }
        UpdatePendingLine();
    }

    private void UpdatePendingLine()
    {
        var i = _pending.Values.Count(a => a == ChangeAction.Install);
        var u = _pending.Values.Count(a => a == ChangeAction.Upgrade);
        var r = _pending.Values.Count(a => a == ChangeAction.Remove);
        HasPending = _pending.Count > 0;
        PendingLine = string.Join(" · ", new[] { i > 0 ? $"{i} to install" : null, u > 0 ? $"{u} to update" : null, r > 0 ? $"{r} to remove" : null }.Where(s => s is not null));
    }

    [RelayCommand]
    private void OpenDetails(ModRowViewModel row)
    {
        var setup = SelectedSetup;
        if (setup is null)
        {
            return;
        }
        var details = new ModDetailsViewModel(row, _info, id => _rowCache.ContainsKey(id), OpenById);
        Details = details;
        _ = details.LoadAsync(_mods.DetailsAsync(setup, row.Identifier, CancellationToken.None));
    }

    private void OpenById(string identifier)
    {
        if (_rowCache.TryGetValue(identifier, out var row))
        {
            OpenDetails(row);
        }
    }

    [RelayCommand]
    private void CloseDetails() => Details = null;

    [RelayCommand]
    private async Task ReviewAsync()
    {
        var setup = SelectedSetup;
        if (setup is null || _pending.Count == 0 || setup.IsPack)
        {
            return;
        }
        var request = new ChangeRequest
        {
            Install = _pending.Where(p => p.Value == ChangeAction.Install).Select(p => p.Key).ToList(),
            Upgrade = _pending.Where(p => p.Value == ChangeAction.Upgrade).Select(p => p.Key).ToList(),
            Remove = _pending.Where(p => p.Value == ChangeAction.Remove).Select(p => p.Key).ToList(),
        };
        await ReviewAndApplyAsync(setup, request, "Review changes").ConfigureAwait(true);
    }

    private async Task ReviewAndApplyAsync(SetupInfo setup, ChangeRequest request, string title)
    {
        var review = new ReviewViewModel(_mods, setup, request);
        var view = new Views.Dialogs.ReviewView { DataContext = review };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = view,
            PrimaryButtonText = "Apply changes",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            DialogMaxWidth = 860,
        };
        dialog.SetBinding(ContentDialog.IsPrimaryButtonEnabledProperty, new Binding(nameof(ReviewViewModel.CanApply)) { Source = review });
        _ = review.LoadAsync();
        var result = await _dialogs.ShowAsync(dialog, CancellationToken.None).ConfigureAwait(true);
        if (result != ContentDialogResult.Primary || !review.CanApply)
        {
            return;
        }
        Error = null;
        var n = review.Installs.Count + review.Upgrades.Count + review.Removals.Count;
        try
        {
            await _mods.ApplyAsync(setup, review.Request, $"Changing {n} mod{(n == 1 ? string.Empty : "s")} in {setup.Name}").ConfigureAwait(true);
            ClearPending();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Applying mod changes failed");
            Error = ErrorCatalog.From(ex);
        }
        await ReloadSnapshotAsync().ConfigureAwait(true);
    }

    /// <summary>Share the selected setup's CKAN mods as a code (small setups) or a .vmpack file.</summary>
    [RelayCommand]
    private async Task ShareAsync()
    {
        if (_snapshot is null || SelectedSetup is not { } setup)
        {
            return;
        }
        var mods = _snapshot.Entries.Where(e => e.IsInstalled && e.InstalledVersion is not null)
            .OrderBy(e => e.IsAutoInstalled).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new SharedMod(e.Identifier, e.InstalledVersion!, e.IsAutoInstalled))
            .ToList();
        if (mods.Count == 0)
        {
            Error = new UserFacingError("Nothing to share yet", "This setup has no mods from CKAN installed.", null, string.Empty, Core.Diagnostics.RecoveryHint.None);
            return;
        }
        var notShared = _snapshot.Entries.Where(e => e.IsAutodetected).Select(e => e.Name).ToList();
        if (setup.IsPack)
        {
            notShared.Add("the pack's private and custom mods");
        }
        var player = _settings.Current.PlayerName;
        var share = new SharedSetup
        {
            Name = setup.Name.Length > 60 ? setup.Name[..60] : setup.Name,
            By = string.IsNullOrWhiteSpace(player) ? null : player,
            GameVersion = _snapshot.GameVersion,
            Mods = mods,
        };
        var vm = new ShareViewModel(share, notShared);
        await _dialogs.ShowAsync(new ContentDialog
        {
            Title = $"Share \"{setup.Name}\"",
            Content = new Views.Dialogs.ShareView { DataContext = vm },
            CloseButtonText = "Done",
            DialogMaxWidth = 760,
        }, CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task ImportAsync() => BeginImportAsync(null);

    /// <summary>
    /// Import from a share code or .vmpack text (also used when one is opened from outside the launcher). Shows what it
    /// contains first, makes the target setup, then hands the exact changes to the normal Review dialog.
    /// </summary>
    public async Task BeginImportAsync(string? text)
    {
        RefreshSetups();
        var current = SelectedSetup ?? _setups.Active;
        var vm = new ImportViewModel(_setups.FindInstalls(), _settings.Current.SourceKspPath, current, text);
        var dialog = new ContentDialog
        {
            Title = "Import a shared setup",
            Content = new Views.Dialogs.ImportView { DataContext = vm },
            PrimaryButtonText = "Next: review",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            DialogMaxWidth = 760,
        };
        dialog.SetBinding(ContentDialog.IsPrimaryButtonEnabledProperty, new Binding(nameof(ImportViewModel.CanImport)) { Source = vm });
        if (await _dialogs.ShowAsync(dialog, CancellationToken.None).ConfigureAwait(true) != ContentDialogResult.Primary || vm.Setup is not { } shared)
        {
            return;
        }
        Error = null;
        try
        {
            SetupInfo target;
            if (vm.NewCopy)
            {
                var source = vm.Target.SelectedInstall ?? throw new Core.Diagnostics.LauncherException(Core.Diagnostics.ErrorCode.GameNotFound, "Choose the KSP to copy.");
                target = await _setups.CreateCopyAsync(vm.Target.Name, source.Path).ConfigureAwait(true);
            }
            else
            {
                target = current;
            }
            RefreshSetups();
            if (SelectedSetup?.Id != target.Id)
            {
                SelectedSetup = Setups.FirstOrDefault(s => s.Id == target.Id);
            }
            await _loading.ConfigureAwait(true);
            await ReviewAndApplyAsync(target, vm.ToRequest(), $"Import \"{shared.Name}\"").ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Importing a shared setup failed");
            Error = ErrorCatalog.From(ex);
        }
    }

    [RelayCommand]
    private void PlaySetup()
    {
        if (SelectedSetup is not { IsPack: false } setup)
        {
            return;
        }
        Error = null;
        try
        {
            using var process = _setups.Launch(setup);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Launching setup {Id} failed", setup.Id);
            Error = ErrorCatalog.From(ex);
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (SelectedSetup is { } s && System.IO.Directory.Exists(s.GameDir))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{s.GameDir}\"") { UseShellExecute = true });
        }
    }

    [RelayCommand]
    private async Task NewSetupAsync() => await NewSetupCoreAsync(makeCopy: true).ConfigureAwait(true);

    /// <summary>From the pack setup: a custom copy to experiment in, leaving the pack untouched.</summary>
    [RelayCommand]
    private async Task MakeCustomCopyAsync() => await NewSetupCoreAsync(makeCopy: true).ConfigureAwait(true);

    private async Task NewSetupCoreAsync(bool makeCopy)
    {
        var vm = new NewSetupViewModel(_setups.FindInstalls(), _settings.Current.SourceKspPath) { MakeCopy = makeCopy };
        var dialog = new ContentDialog
        {
            Title = "New setup",
            Content = new Views.Dialogs.NewSetupView { DataContext = vm },
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            DialogMaxWidth = 720,
        };
        dialog.SetBinding(ContentDialog.IsPrimaryButtonEnabledProperty, new Binding(nameof(NewSetupViewModel.CanCreate)) { Source = vm });
        if (await _dialogs.ShowAsync(dialog, CancellationToken.None).ConfigureAwait(true) != ContentDialogResult.Primary || vm.SelectedInstall is not { } source)
        {
            return;
        }
        Error = null;
        try
        {
            if (vm.MakeCopy)
            {
                await _setups.CreateCopyAsync(vm.Name, source.Path).ConfigureAwait(true);
            }
            else
            {
                _setups.AddExisting(vm.Name, source.Path);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Creating a setup failed");
            Error = ErrorCatalog.From(ex);
        }
    }

    [RelayCommand]
    private async Task RemoveSetupAsync()
    {
        if (SelectedSetup is not { IsPack: false } setup)
        {
            return;
        }
        var existing = setup.Kind == Core.Profiles.SetupKinds.Existing;
        var answer = await _dialogs.ShowSimpleDialogAsync(new SimpleContentDialogCreateOptions
        {
            Title = $"Remove \"{setup.Name}\"?",
            Content = existing
                ? $"The launcher forgets this setup. The folder {setup.GameDir} and its mods stay exactly as they are."
                : $"This deletes the launcher's copy in {setup.Root}, including its mods, saves and settings. Your own KSP is not touched.",
            PrimaryButtonText = existing ? "Forget it" : "Delete the copy",
            CloseButtonText = "Cancel",
        }).ConfigureAwait(true);
        if (answer != ContentDialogResult.Primary)
        {
            return;
        }
        Error = null;
        try
        {
            await _setups.RemoveAsync(setup.Id).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Removing setup {Id} failed", setup.Id);
            Error = ErrorCatalog.From(ex);
        }
    }

    [RelayCommand]
    private void DismissError() => Error = null;
}
