using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmpLauncher.App.Services;
using VmpLauncher.Core;
using VmpLauncher.Game;
using Wpf.Ui;

namespace VmpLauncher.App.ViewModels;

public partial class KspCandidate : ObservableObject
{
    public required KspInstallInfo Info { get; init; }
    public string Title => Info.Path;
    public string Summary => $"{(Info.HasExecutable ? "KSP_x64.exe" : "no executable")} · build {Info.BuildId ?? "?"} · {(Info.HasMakingHistory ? "Making History" : "no Making History")} · {(Info.HasBreakingGround ? "Breaking Ground" : "no Breaking Ground")}";
    public bool Complete => Info.IsComplete("03190");
    [ObservableProperty] private bool _isSelected;
}

/// <summary>First-run wizard: find KSP → profile location → player setup.</summary>
public partial class WelcomeViewModel : ObservableObject
{
    private readonly AppSettingsStore _settings;
    private readonly ISecretStore _secrets;
    private readonly LauncherPaths _paths;
    private readonly INavigationService _navigation;

    public WelcomeViewModel(AppSettingsStore settings, ISecretStore secrets, LauncherPaths paths, INavigationService navigation)
    {
        _settings = settings;
        _secrets = secrets;
        _paths = paths;
        _navigation = navigation;
        var s = settings.Current;
        PlayerName = s.PlayerName ?? string.Empty;
        ProfileRoot = s.ProfileRoot ?? Path.Combine(paths.ProfilesDir, s.ActiveProfileId);
        DisclaimerAccepted = s.DisclaimerAccepted;
    }

    public ObservableCollection<KspCandidate> Candidates { get; } = new();

    [ObservableProperty] private int _step;
    [ObservableProperty] private string _playerName = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private bool _disclaimerAccepted;
    [ObservableProperty] private string _profileRoot = string.Empty;
    [ObservableProperty] private string _profileHint = string.Empty;
    [ObservableProperty] private string _validation = string.Empty;

    public bool PlayerNameValid => VmpSettingsFile.PlayerNamePattern.IsMatch(PlayerName);

    public string Disclaimer =>
        "VMP (Vlad Multiplayer) is a community mod based on Luna Multiplayer. It synchronises your game with other players on the host's server. " +
        "Use a dedicated profile (this launcher creates one), expect occasional desyncs, and never share the server password publicly. " +
        "By accepting you agree to let the mod exchange vessel, time and chat data with the server.";

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
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose your Kerbal Space Program folder" };
        if (dlg.ShowDialog() == true)
        {
            var info = KspLocator.Inspect(dlg.FolderName);
            foreach (var c in Candidates)
            {
                c.IsSelected = false;
            }
            Candidates.Insert(0, new KspCandidate { Info = info, IsSelected = true });
        }
    }

    [RelayCommand]
    private void BrowseProfile()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose where the modded KSP profile lives (needs ~15 GB)" };
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
                    Validation = "Choose a KSP folder that contains KSP_x64.exe.";
                    return;
                }
                if (!sel.Complete)
                {
                    Validation = "This install is missing the required version or DLC. You can continue, but installing the pack will fail until it is fixed.";
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
                catch (Core.Diagnostics.LauncherException ex)
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
                    Validation = "Player name: 1-15 characters, letters, digits, _ or -. It must differ from the host's name.";
                    return;
                }
                _settings.Update(s => s with { PlayerName = PlayerName, DisclaimerAccepted = DisclaimerAccepted, WelcomeCompleted = true });
                if (!string.IsNullOrEmpty(Password))
                {
                    _secrets.Set($"server-password:{_settings.Current.ActiveProfileId}", Password);
                    Password = string.Empty;
                }
                _navigation.Navigate(typeof(Views.Pages.HomePage));
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
                ? $"Same drive as your KSP: stock files are hard-linked (saves ~5.6 GB). {Format.Bytes(drive.AvailableFreeSpace)} free; the pack needs ~13 GB."
                : $"Different drive from your KSP: stock files are copied (~5.7 GB extra). {Format.Bytes(drive.AvailableFreeSpace)} free; the pack needs ~19 GB.";
        }
        catch (Exception)
        {
            ProfileHint = string.Empty;
        }
    }
}
