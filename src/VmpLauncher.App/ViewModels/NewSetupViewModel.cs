using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmpLauncher.App.Services;

namespace VmpLauncher.App.ViewModels;

public sealed record InstallChoice(string Path, string Label);

/// <summary>The "New setup" dialog: a fresh launcher copy of KSP, or a KSP folder the player already has.</summary>
public sealed partial class NewSetupViewModel : ObservableObject
{
    public NewSetupViewModel(IReadOnlyList<FoundInstall> found, string? defaultSource)
    {
        foreach (var f in found)
        {
            var source = f.Source switch { "steam" => "Steam", "ckan" => "CKAN", "remembered" => "used before", _ => f.Source };
            Installs.Add(new InstallChoice(f.Path, $"{f.Path}  ({source}{(f.Version is { } v ? ", KSP " + v : string.Empty)})"));
        }
        if (defaultSource is not null && File.Exists(System.IO.Path.Combine(defaultSource, "KSP_x64.exe")) && Installs.All(i => !string.Equals(i.Path, defaultSource, StringComparison.OrdinalIgnoreCase)))
        {
            Installs.Insert(0, new InstallChoice(defaultSource, $"{defaultSource}  (your KSP)"));
        }
        SelectedInstall = Installs.FirstOrDefault();
    }

    public ObservableCollection<InstallChoice> Installs { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    private string _name = "My mods";

    /// <summary>True: a new launcher copy; false: use the chosen folder itself.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Explanation), nameof(SourceLabel))]
    private bool _makeCopy = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    private InstallChoice? _selectedInstall;

    public bool CanCreate => Name.Trim().Length is > 0 and <= 60 && SelectedInstall is not null;

    public string SourceLabel => MakeCopy ? "Copy the game from" : "KSP folder";

    public string Explanation => MakeCopy
        ? "Makes a clean copy of the stock game (no mods, saves or settings). On the same drive it uses hard links, so it takes almost no space. Your KSP stays untouched."
        : "Manages mods right in that folder, like CKAN does. If CKAN already manages it, the launcher reads and updates the same mod list. The folder is never moved or deleted.";

    [RelayCommand]
    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose your Kerbal Space Program folder" };
        if (dialog.ShowDialog() == true)
        {
            var path = dialog.FolderName;
            var existing = Installs.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new InstallChoice(path, File.Exists(System.IO.Path.Combine(path, "KSP_x64.exe")) ? path : $"{path}  (no KSP_x64.exe here)");
                Installs.Add(existing);
            }
            SelectedInstall = existing;
        }
    }
}
