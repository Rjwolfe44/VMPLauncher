using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmpLauncher.App.Services;
using VmpLauncher.Core;
using VmpLauncher.Core.Diagnostics;
using VmpLauncher.Game;

namespace VmpLauncher.App.ViewModels;

public partial class KspCandidate : ObservableObject
{
    public required KspInstallInfo Info { get; init; }
    public string Title => Info.Path;
    public string Summary => $"{(Info.HasExecutable ? "KSP_x64.exe found" : "no KSP_x64.exe")} · build {Info.BuildId ?? "?"} · {(Info.HasMakingHistory ? "Making History" : "no Making History")} · {(Info.HasBreakingGround ? "Breaking Ground" : "no Breaking Ground")}";
    public bool Complete => Info.IsComplete("03190");
    [ObservableProperty] private bool _isSelected;
}

public partial class WizardStep : ObservableObject
{
    public required int Index { get; init; }
    public required string Label { get; init; }
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isDone;
    public string Number => (Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>First-run wizard: welcome → find KSP → profile location → player (+ optional invite code).</summary>
public partial class WelcomeViewModel : ObservableObject
{
    private readonly AppSettingsStore _settings;
    private readonly ISecretStore _secrets;
    private readonly LauncherPaths _paths;
    private readonly LauncherService _launcher;
    private readonly IAppNavigator _navigation;
    private bool _acceptedIncomplete;

    public WelcomeViewModel(AppSettingsStore settings, ISecretStore secrets, LauncherPaths paths, LauncherService launcher, IAppNavigator navigation)
    {
        _settings = settings;
        _secrets = secrets;
        _paths = paths;
        _launcher = launcher;
        _navigation = navigation;
        var s = settings.Current;
        _playerName = s.PlayerName ?? string.Empty;
        _profileRoot = s.ProfileRoot ?? Path.Combine(paths.ProfilesDir, s.ActiveProfileId);
        _disclaimerAccepted = s.DisclaimerAccepted;
        Steps = new ObservableCollection<WizardStep>
        {
            new() { Index = 0, Label = "Welcome", IsActive = true },
            new() { Index = 1, Label = "Your KSP" },
            new() { Index = 2, Label = "Install folder" },
            new() { Index = 3, Label = "You" },
        };
    }

    public ObservableCollection<KspCandidate> Candidates { get; } = new();
    public ObservableCollection<WizardStep> Steps { get; }

    [ObservableProperty] private int _step;
    [ObservableProperty] private string _playerName;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _inviteText = string.Empty;
    [ObservableProperty] private bool _disclaimerAccepted;
    [ObservableProperty] private string _profileRoot;
    [ObservableProperty] private string _profileHint = string.Empty;
    [ObservableProperty] private string _validation = string.Empty;
    [ObservableProperty] private string _nextLabel = "Get started";
    [ObservableProperty] private bool _canGoBack;

    public bool PlayerNameValid => VmpSettingsFile.PlayerNamePattern.IsMatch(PlayerName);

    public string Disclaimer =>
        "VMP (Vlad Multiplayer) is a community mod based on Luna Multiplayer. It synchronises your game with the host's server. " +
        "The launcher keeps it in its own modded copy, so your normal KSP and saves are untouched. Expect the odd desync, and never share the server password publicly. " +
        "By accepting you let the mod exchange vessel, time and chat data with the server.";

    partial void OnStepChanged(int value)
    {
        foreach (var s in Steps)
        {
            s.IsActive = s.Index == value;
            s.IsDone = s.Index < value;
        }
        NextLabel = value switch { 0 => "Get started", 3 => "Finish", _ => "Continue" };
        CanGoBack = value > 0;
    }

    /// <summary>Opening the wizard again from Settings starts at the beginning.</summary>
    public void OnShown()
    {
        if (_settings.Current.WelcomeCompleted && Step == 3)
        {
            Step = 0;
        }
    }

    [RelayCommand]
    public void Detect()
    {
        Candidates.Clear();
        foreach (var info in KspLocator.Discover(_settings.Current.RememberedKspPaths))
        {
            Candidates.Add(new KspCandidate { Info = info, IsSelected = string.Equals(info.Path, _settings.Current.SourceKspPath, StringComparison.OrdinalIgnoreCase) });
        }
        if (Candidates.Count > 0 && !Candidates.Any(c => c.IsSelected))
        {
            (Candidates.FirstOrDefault(c => c.Complete) ?? Candidates[0]).IsSelected = true;
        }
    }

    [RelayCommand]
    private void Browse()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose your Kerbal Space Program folder (the one with KSP_x64.exe)" };
        if (dlg.ShowDialog() == true)
        {
            var info = KspLocator.Inspect(dlg.FolderName);
            foreach (var c in Candidates)
            {
                c.IsSelected = false;
            }
            Candidates.Insert(0, new KspCandidate { Info = info, IsSelected = true });
            _acceptedIncomplete = false;
        }
    }

    [RelayCommand]
    private void BrowseProfile()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose where the modded copy goes (needs about 15 GB)" };
        if (dlg.ShowDialog() == true)
        {
            ProfileRoot = Path.Combine(dlg.FolderName, _settings.Current.ActiveProfileId);
        }
    }

    [RelayCommand]
    private void Select(KspCandidate c)
    {
        foreach (var x in Candidates)
        {
            x.IsSelected = ReferenceEquals(x, c);
        }
        _acceptedIncomplete = false;
        Validation = string.Empty;
    }

    [RelayCommand]
    private void Next()
    {
        Validation = string.Empty;
        switch (Step)
        {
            case 0:
                Step = 1;
                Detect();
                break;
            case 1:
            {
                var sel = Candidates.FirstOrDefault(c => c.IsSelected);
                if (sel is null || !sel.Info.HasExecutable)
                {
                    Validation = "Choose a KSP folder that contains KSP_x64.exe. Use Browse if it is not listed.";
                    return;
                }
                if (!sel.Complete && !_acceptedIncomplete)
                {
                    _acceptedIncomplete = true;
                    Validation = "This install is not KSP 1.12.5 with both DLCs, so installing the pack will fail until it is. Press Continue again to use it anyway.";
                    return;
                }
                _settings.Update(s => s with { SourceKspPath = sel.Info.Path, RememberedKspPaths = s.RememberedKspPaths.Append(sel.Info.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList() });
                UpdateProfileHint();
                Step = 2;
                break;
            }
            case 2:
            {
                try
                {
                    Core.Profiles.ProfilePathValidator.Validate(ProfileRoot, 180, _settings.Current.SourceKspPath, _paths.CacheDir);
                }
                catch (LauncherException ex)
                {
                    Validation = ex.Message;
                    return;
                }
                var defaultRoot = Path.Combine(_paths.ProfilesDir, _settings.Current.ActiveProfileId);
                _settings.Update(s => s with { ProfileRoot = string.Equals(ProfileRoot, defaultRoot, StringComparison.OrdinalIgnoreCase) ? null : ProfileRoot });
                Step = 3;
                break;
            }
            case 3:
            {
                if (!PlayerNameValid)
                {
                    Validation = "Player name: 1 to 15 letters, digits, _ or -. It must differ from the host's name.";
                    return;
                }
                if (!string.IsNullOrWhiteSpace(InviteText))
                {
                    try
                    {
                        _launcher.PrivateMods.ApplyInvite(InviteText, _launcher.PasswordKey);
                        InviteText = string.Empty;
                    }
                    catch (LauncherException ex)
                    {
                        Validation = ex.Message;
                        return;
                    }
                }
                _settings.Update(s => s with { PlayerName = PlayerName, DisclaimerAccepted = DisclaimerAccepted, WelcomeCompleted = true });
                if (!string.IsNullOrEmpty(Password))
                {
                    _secrets.Set(_launcher.PasswordKey, Password);
                    Password = string.Empty;
                }
                _navigation.Navigate(AppPage.Home);
                break;
            }
        }
    }

    [RelayCommand]
    private void Back()
    {
        if (Step > 0)
        {
            Step--;
            Validation = string.Empty;
        }
    }

    partial void OnProfileRootChanged(string value) => UpdateProfileHint();

    private void UpdateProfileHint()
    {
        try
        {
            var source = _settings.Current.SourceKspPath;
            var same = source is not null && Core.Profiles.ProfilePathValidator.SameVolume(source, ProfileRoot);
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(ProfileRoot))!);
            ProfileHint = same
                ? $"Same drive as your KSP, so stock files are linked instead of copied (saves about 5.6 GB). {Format.Bytes(drive.AvailableFreeSpace)} free; the pack needs about 13 GB."
                : $"Different drive from your KSP, so stock files are copied (about 5.7 GB extra). {Format.Bytes(drive.AvailableFreeSpace)} free; the pack needs about 19 GB.";
        }
        catch (Exception)
        {
            ProfileHint = string.Empty;
        }
    }
}
