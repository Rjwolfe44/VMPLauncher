using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using VmpLauncher.App.Services;
using VmpLauncher.Core.Diagnostics;

namespace VmpLauncher.App.ViewModels;

public sealed record PlanAction(string Title, string Commands);

public sealed record ConfigLine(string Label, string Value);

/// <summary>Host tools: publish the pack (check first, then for real) and make invite codes for friends.</summary>
public partial class HostViewModel : ObservableObject
{
    private readonly HostToolsService _host;
    private readonly OperationRunner _runner;
    private readonly ILogger<HostViewModel> _log;
    private string? _checkedNotes;

    public HostViewModel(HostToolsService host, OperationRunner runner, ILogger<HostViewModel> log)
    {
        _host = host;
        _runner = runner;
        _log = log;
        var c = host.Config;
        if (c is not null)
        {
            ConfigLines = new[]
            {
                new ConfigLine("Pack", host.Pack is null ? c.PackFile : $"{host.Pack.Name} ({host.Pack.Id})"),
                new ConfigLine("Your KSP", c.HostKspDir),
                new ConfigLine("Packs repository", $"{c.PacksRepoDir} ({c.PacksRepoBranch})"),
                new ConfigLine("Game server", c.ServerHost is null ? "not managed" : $"{c.ServerHost}: {c.ServerRoot}"),
                new ConfigLine("Signing key", c.KeyId),
            };
        }
        foreach (var repo in host.PrivateRepos)
        {
            Repos.Add(repo);
        }
        _selectedRepo = Repos.FirstOrDefault();
        UpdateTokenState();
    }

    public IReadOnlyList<ConfigLine> ConfigLines { get; } = Array.Empty<ConfigLine>();
    public ObservableCollection<string> Log { get; } = new();
    public ObservableCollection<PlanAction> Actions { get; } = new();
    public ObservableCollection<string> Warnings { get; } = new();
    public ObservableCollection<string> Repos { get; } = new();

    public bool HasPrivateRepos => Repos.Count > 0;

    [ObservableProperty] private string _notes = string.Empty;
    [ObservableProperty] private string _generatedPolicy = string.Empty;
    [ObservableProperty] private bool _allowPatchedPolicy;
    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private bool _hasPlan;
    [ObservableProperty] private string _planHeadline = string.Empty;
    [ObservableProperty] private string _planDetail = string.Empty;
    [ObservableProperty] private string _resultKind = "neutral";
    [ObservableProperty] private string _confirmText = string.Empty;
    [ObservableProperty] private bool _canPublish;
    [ObservableProperty] private UserFacingError? _error;

    [ObservableProperty] private string? _selectedRepo;
    [ObservableProperty] private string _tokenInput = string.Empty;
    [ObservableProperty] private string _tokenState = string.Empty;
    [ObservableProperty] private string _tokenKind = "neutral";
    [ObservableProperty] private bool _includePassword = true;
    [ObservableProperty] private string _inviteState = string.Empty;
    [ObservableProperty] private string _inviteKind = "neutral";

    partial void OnConfirmTextChanged(string value) => UpdateCanPublish();
    partial void OnNotesChanged(string value) => UpdateCanPublish();
    partial void OnSelectedRepoChanged(string? value) => UpdateTokenState();

    private void UpdateCanPublish() => CanPublish = HasPlan && ResultKind != "error" && !IsWorking && ConfirmText.Trim() == "PUBLISH" && Notes == _checkedNotes;

    private void UpdateTokenState()
    {
        if (SelectedRepo is null)
        {
            (TokenState, TokenKind) = ("No private repository in the pack definition yet.", "neutral");
            return;
        }
        (TokenState, TokenKind) = _host.HasToken(SelectedRepo)
            ? ($"Read-only token for {SelectedRepo} saved on this PC.", "ok")
            : ($"No token saved for {SelectedRepo} yet.", "warn");
    }

    [RelayCommand]
    private void BrowsePolicy()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose the KSPModControl.xml generated in-game (KSPModControl + SHA)", Filter = "KSPModControl.xml|*.xml" };
        if (dlg.ShowDialog() == true)
        {
            GeneratedPolicy = dlg.FileName;
        }
    }

    [RelayCommand]
    private Task CheckAsync() => RunAsync(execute: false);

    [RelayCommand]
    private Task PublishAsync() => RunAsync(execute: true);

    private async Task RunAsync(bool execute)
    {
        Error = null;
        Log.Clear();
        IsWorking = true;
        if (!execute)
        {
            HasPlan = false;
            Actions.Clear();
            Warnings.Clear();
        }
        UpdateCanPublish();
        var dispatcher = Application.Current.Dispatcher;
        void OnLine(string line)
        {
            _runner.Report(line, null);
            dispatcher.BeginInvoke(() =>
            {
                Log.Add(line);
                if (Log.Count > 2000)
                {
                    Log.RemoveAt(0);
                }
            });
        }
        var notes = Notes;
        try
        {
            var report = await _runner.RunAsync(OperationKind.Publish, execute ? "Publishing the pack" : "Checking what would be published", (_, ct) =>
                _host.PublishAsync(string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(), string.IsNullOrWhiteSpace(GeneratedPolicy) ? null : GeneratedPolicy, AllowPatchedPolicy, execute, OnLine, ct));
            Actions.Clear();
            foreach (var a in report.Actions)
            {
                Actions.Add(new PlanAction(a.Title, string.Join(Environment.NewLine, a.Commands)));
            }
            Warnings.Clear();
            foreach (var w in report.Warnings)
            {
                Warnings.Add(w);
            }
            foreach (var f in report.SideloadFiles)
            {
                Warnings.Add($"Hand this private file out yourself: {f}");
            }
            var changes = report.Staged.Diff?.Summary() ?? "first version";
            if (execute)
            {
                PlanHeadline = $"Published {report.Version}";
                PlanDetail = $"Changes: {changes}. Server policy: {report.Policy.Summary}. Commit {report.PushedCommit?[..Math.Min(8, report.PushedCommit.Length)]}.";
                ResultKind = "ok";
                ConfirmText = string.Empty;
                HasPlan = false;
                OnLine("Checking what friends will see…");
                var remote = await _host.VerifyRemoteAsync(report.Version, CancellationToken.None);
                foreach (var l in remote.Lines)
                {
                    OnLine("  " + l);
                }
                foreach (var p in remote.Problems)
                {
                    OnLine("  PROBLEM: " + p);
                }
                if (remote.Problems.Count > 0)
                {
                    ResultKind = "warn";
                    PlanDetail += $" The remote check found {remote.Problems.Count} problem(s); see the log.";
                }
            }
            else
            {
                PlanHeadline = $"Ready to publish {report.Version}";
                PlanDetail = $"Changes: {changes}. Server policy: {report.Policy.Summary}. Nothing has left this PC yet.";
                ResultKind = Warnings.Count > 0 ? "warn" : "ok";
                HasPlan = true;
                _checkedNotes = notes;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Publish {Mode} failed", execute ? "execute" : "check");
            Error = ErrorCatalog.From(ex);
            PlanHeadline = execute ? "Publishing stopped" : "The check found a problem";
            PlanDetail = ex.Message;
            ResultKind = "error";
            HasPlan = false;
        }
        finally
        {
            IsWorking = false;
            UpdateCanPublish();
        }
    }

    // ---- Vlad's mods (the library behind the Mods page's "Vlad's mods" tab) ----

    public bool HasLibraryDefinition => _host.LibraryDefinitionFile is not null;

    [ObservableProperty] private bool _libraryHasPlan;
    [ObservableProperty] private string _libraryHeadline = string.Empty;
    [ObservableProperty] private string _libraryDetail = string.Empty;
    [ObservableProperty] private string _libraryKind = "neutral";
    [ObservableProperty] private string _libraryConfirm = string.Empty;
    [ObservableProperty] private bool _canPublishLibrary;

    partial void OnLibraryConfirmChanged(string value) => UpdateCanPublishLibrary();

    private void UpdateCanPublishLibrary() => CanPublishLibrary = LibraryHasPlan && LibraryKind != "error" && !IsWorking && LibraryConfirm.Trim() == "PUBLISH";

    [RelayCommand]
    private Task CheckLibraryAsync() => RunLibraryAsync(execute: false);

    [RelayCommand]
    private Task PublishLibraryAsync() => RunLibraryAsync(execute: true);

    private async Task RunLibraryAsync(bool execute)
    {
        Error = null;
        Log.Clear();
        IsWorking = true;
        if (!execute)
        {
            LibraryHasPlan = false;
        }
        UpdateCanPublishLibrary();
        var dispatcher = Application.Current.Dispatcher;
        void OnLine(string line)
        {
            _runner.Report(line, null);
            dispatcher.BeginInvoke(() => Log.Add(line));
        }
        try
        {
            var report = await _runner.RunAsync(OperationKind.Publish, execute ? "Publishing Vlad's mods" : "Checking Vlad's mods", (_, ct) => _host.PublishLibraryAsync(execute, OnLine, ct));
            var mods = string.Join(", ", report.Modules.Select(m => $"{m.Identifier} {CatalogFormatVersion(m.Version)}"));
            if (report.Unchanged)
            {
                LibraryHeadline = "Nothing new to publish";
                LibraryDetail = $"The published library (list {report.Sequence}) already matches: {mods}.";
                LibraryKind = "ok";
                LibraryHasPlan = false;
            }
            else if (execute)
            {
                LibraryHeadline = $"Published Vlad's mods (list {report.Sequence})";
                LibraryDetail = $"{mods}. Commit {report.PushedCommit?[..Math.Min(8, report.PushedCommit.Length)]}.";
                LibraryKind = "ok";
                LibraryConfirm = string.Empty;
                LibraryHasPlan = false;
                if (report.Remote is { } remote)
                {
                    foreach (var l in remote.Lines)
                    {
                        OnLine("  " + l);
                    }
                    foreach (var p in remote.Problems)
                    {
                        OnLine("  PROBLEM: " + p);
                    }
                    if (remote.Problems.Count > 0)
                    {
                        LibraryKind = "warn";
                        LibraryDetail += $" The remote check found {remote.Problems.Count} problem(s); see the log.";
                    }
                }
            }
            else
            {
                LibraryHeadline = $"Ready to publish Vlad's mods (list {report.Sequence})";
                LibraryDetail = $"{mods}. Nothing has left this PC yet.";
                LibraryKind = report.Warnings.Count > 0 ? "warn" : "ok";
                LibraryHasPlan = true;
                foreach (var w in report.Warnings)
                {
                    OnLine("warning: " + w);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Library {Mode} failed", execute ? "publish" : "check");
            Error = ErrorCatalog.From(ex);
            LibraryHeadline = execute ? "Publishing Vlad's mods stopped" : "The check found a problem";
            LibraryDetail = ex.Message;
            LibraryKind = "error";
            LibraryHasPlan = false;
        }
        finally
        {
            IsWorking = false;
            UpdateCanPublishLibrary();
        }
    }

    private static string CatalogFormatVersion(string version) => VmpLauncher.Core.Catalog.CatalogFormat.DisplayVersion(version);

    [RelayCommand]
    private async Task SaveTokenAsync()
    {
        if (SelectedRepo is null || string.IsNullOrWhiteSpace(TokenInput))
        {
            return;
        }
        try
        {
            (TokenState, TokenKind) = ("Checking the token with GitHub…", "checking");
            var msg = await _host.SaveTokenAsync(SelectedRepo, TokenInput, CancellationToken.None);
            TokenInput = string.Empty;
            (TokenState, TokenKind) = ($"Saved. {msg}", "ok");
        }
        catch (LauncherException ex)
        {
            (TokenState, TokenKind) = (ex.Message, "error");
        }
    }

    [RelayCommand]
    private void CopyInvite()
    {
        try
        {
            var code = _host.CreateInvite(SelectedRepo is not null && _host.HasToken(SelectedRepo) ? SelectedRepo : null, IncludePassword);
            Clipboard.SetText(code);
            (InviteState, InviteKind) = ("Invite code copied. Paste it to your friend in a private message; it works like a key.", "ok");
        }
        catch (Exception ex) when (ex is LauncherException or System.Runtime.InteropServices.ExternalException)
        {
            (InviteState, InviteKind) = (ex.Message, "error");
        }
    }

    [RelayCommand]
    private void OpenTokenPage()
    {
        // GitHub pre-fills name, owner, expiry and Contents: read-only from the URL; the repository itself must still be
        // picked by hand ("Only select repositories"), GitHub has no parameter for it.
        var owner = (SelectedRepo ?? "Rjwolfe44/x").Split('/')[0];
        var url = "https://github.com/settings/personal-access-tokens/new"
                  + "?name=" + Uri.EscapeDataString("VMP private mods (friends)")
                  + "&description=" + Uri.EscapeDataString($"Read-only access for VMP Launcher invite codes. Repository: {SelectedRepo ?? "your private mods repo"} only.")
                  + "&target_name=" + Uri.EscapeDataString(owner)
                  + "&expires_in=none&contents=read";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

    [RelayCommand]
    private void CopyLog()
    {
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, Log));
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
        }
    }
}
