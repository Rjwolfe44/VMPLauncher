using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using VmpLauncher.App.Services;
using VmpLauncher.Core.Catalog;

namespace VmpLauncher.App.ViewModels;

public sealed record ChangeRow(string Name, string Version, string Reason, string Size, bool ByPlayer);

public sealed partial class SuggestionRow : ObservableObject
{
    private readonly Action _changed;

    public SuggestionRow(ModSuggestion s, Action changed)
    {
        Suggestion = s;
        _changed = changed;
        _isChecked = s.PreChecked;
    }

    public ModSuggestion Suggestion { get; }
    public string Name => Suggestion.Name;
    public string Detail => $"{Suggestion.Kind switch { "recommended" => "Recommended by", "suggested" => "Suggested by", _ => "Works with" }} {string.Join(", ", Suggestion.By)}" + (Suggestion.Abstract.Length > 0 ? $" · {Suggestion.Abstract}" : string.Empty);

    [ObservableProperty] private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => _changed();
}

public sealed partial class ChoiceRow : ObservableObject
{
    public ChoiceRow(ProvidesOption option, bool selected)
    {
        Option = option;
        _isSelected = selected;
    }

    public ProvidesOption Option { get; }
    public string Name => Option.Name;
    public string Detail => string.Join(" · ", new[] { Option.Downloads > 0 ? $"{ModRowViewModel.FormatCount(Option.Downloads)} downloads" : null, Option.Cached ? "already downloaded" : null, Option.Abstract.Length > 0 ? Option.Abstract : null }.Where(s => s is not null));

    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// The Review dialog: CKAN's full plan for the queued changes. Picking a provider or ticking a suggestion previews
/// again, so what Apply runs is always what the player last saw.
/// </summary>
public sealed partial class ReviewViewModel : ObservableObject
{
    private readonly ModManagerService _mods;
    private readonly SetupInfo _setup;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private int _version;

    public ReviewViewModel(ModManagerService mods, SetupInfo setup, ChangeRequest request)
    {
        _mods = mods;
        _setup = setup;
        Request = request;
    }

    public ChangeRequest Request { get; private set; }
    public string SetupName => _setup.Name;

    public ObservableCollection<ChangeRow> Installs { get; } = new();
    public ObservableCollection<ChangeRow> Upgrades { get; } = new();
    public ObservableCollection<ChangeRow> Removals { get; } = new();
    public ObservableCollection<string> Problems { get; } = new();
    public ObservableCollection<string> Notes { get; } = new();
    public ObservableCollection<SuggestionRow> Suggestions { get; } = new();
    public ObservableCollection<ChoiceRow> Choices { get; } = new();

    [ObservableProperty] private bool _isBusy = true;
    [ObservableProperty] private bool _canApply;
    [ObservableProperty] private string _summary = "Working out what changes…";
    [ObservableProperty] private string? _choiceTitle;
    [ObservableProperty] private ChoiceRow? _selectedChoice;

    public bool HasInstalls => Installs.Count > 0;
    public bool HasUpgrades => Upgrades.Count > 0;
    public bool HasRemovals => Removals.Count > 0;
    public bool HasProblems => Problems.Count > 0;
    public bool HasNotes => Notes.Count > 0;
    public bool HasSuggestions => Suggestions.Count > 0;
    public bool HasChoice => Choices.Count > 0;

    partial void OnSelectedChoiceChanged(ChoiceRow? value)
    {
        if (value is null || ChoiceTitle is null || _pendingChoice is null)
        {
            return;
        }
        var choices = new Dictionary<string, string>(Request.ProvidesChoices) { [_pendingChoice] = value.Option.Identifier };
        Request = Request with { ProvidesChoices = choices };
        _ = PreviewAsync();
    }

    private string? _pendingChoice;

    public Task LoadAsync() => PreviewAsync();

    private void SuggestionsChanged()
    {
        var accepted = Suggestions.Where(s => s.IsChecked).Select(s => s.Suggestion.Identifier).ToList();
        Request = Request with { AcceptedSuggestions = Request.AcceptedSuggestions.Where(a => !Suggestions.Any(s => s.Suggestion.Identifier == a)).Concat(accepted).Distinct().ToList() };
        _ = PreviewAsync();
    }

    private async Task PreviewAsync()
    {
        var version = ++_version;
        IsBusy = true;
        CanApply = false;
        ChangePlan plan;
        try
        {
            plan = await _mods.PreviewAsync(_setup, Request with { SeenSuggestions = _seen.ToList() }, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (version != _version)
            {
                return;
            }
            Problems.Clear();
            Problems.Add(ErrorCatalog.From(ex).Message);
            Summary = "Could not work out these changes.";
            IsBusy = false;
            Notify();
            return;
        }
        if (version != _version)
        {
            return; // a newer preview is on its way
        }

        Installs.Clear();
        Upgrades.Clear();
        Removals.Clear();
        foreach (var c in plan.Changes)
        {
            var row = new ChangeRow(c.Name, CatalogFormat.DisplayVersion(c.Version), c.Reason, c.Cached || c.DownloadSize <= 0 ? string.Empty : Format.Bytes(c.DownloadSize), c.ByPlayer);
            (c.Action switch { ChangeAction.Install => Installs, ChangeAction.Upgrade => Upgrades, _ => Removals }).Add(row);
        }
        Problems.Clear();
        foreach (var p in plan.Conflicts.Select(c => "Conflict: " + c).Concat(plan.Errors))
        {
            Problems.Add(p);
        }
        Notes.Clear();
        foreach (var n in plan.Notes)
        {
            Notes.Add(n);
        }

        // Ticked suggestions stay listed (ticked) so they can be unticked again.
        var keep = Suggestions.Where(s => s.IsChecked).ToList();
        Suggestions.Clear();
        foreach (var s in keep)
        {
            Suggestions.Add(s);
        }
        foreach (var s in plan.Suggestions.Where(s => keep.All(k => k.Suggestion.Identifier != s.Identifier)))
        {
            _seen.Add(s.Identifier);
            var row = new SuggestionRow(s, SuggestionsChanged);
            Suggestions.Add(row);
        }
        if (plan.Suggestions.Any(s => s.PreChecked && keep.All(k => k.Suggestion.Identifier != s.Identifier)))
        {
            // CKAN pre-ticks recommendations: include them now so the plan shows their own dependencies too.
            SuggestionsChanged();
            return;
        }

        Choices.Clear();
        _pendingChoice = null;
        ChoiceTitle = null;
        if (plan.Choice is { } choice)
        {
            _pendingChoice = choice.Requested;
            ChoiceTitle = $"{choice.RequestedBy} needs {choice.Requested}. Several mods provide it; pick one:";
            foreach (var o in choice.Options.Take(40))
            {
                Choices.Add(new ChoiceRow(o, false));
            }
        }

        var parts = new List<string>();
        if (Installs.Count > 0) { parts.Add($"{Installs.Count} to install"); }
        if (Upgrades.Count > 0) { parts.Add($"{Upgrades.Count} to update"); }
        if (Removals.Count > 0) { parts.Add($"{Removals.Count} to remove"); }
        Summary = (parts.Count == 0 ? "Nothing to change" : string.Join(", ", parts))
                  + (plan.DownloadCount > 0 ? $" · downloads {Format.Bytes(plan.DownloadBytes)} ({plan.DownloadCount} file{(plan.DownloadCount == 1 ? string.Empty : "s")})" : plan.Changes.Count > 0 ? " · nothing to download" : string.Empty)
                  + $" · {_setup.Name}";
        CanApply = plan.CanApply;
        IsBusy = false;
        Notify();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(HasInstalls));
        OnPropertyChanged(nameof(HasUpgrades));
        OnPropertyChanged(nameof(HasRemovals));
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(HasSuggestions));
        OnPropertyChanged(nameof(HasChoice));
    }
}
