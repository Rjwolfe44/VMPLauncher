using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmpLauncher.App.Services;
using VmpLauncher.Core.Catalog;

namespace VmpLauncher.App.ViewModels;

/// <summary>The Share dialog: a code for chat (when it fits) and a .vmpack file for anything bigger.</summary>
public sealed partial class ShareViewModel : ObservableObject
{
    private readonly SharedSetup _setup;

    public ShareViewModel(SharedSetup setup, IReadOnlyList<string> notShared)
    {
        _setup = setup;
        NotShared = notShared;
        var code = SetupShare.ToCode(setup);
        CodeFits = code.Length <= SetupShare.ChatLimit;
        Code = code;
        Summary = $"{setup.Mods.Count(m => !m.Auto)} mods you picked and {setup.Mods.Count(m => m.Auto)} dependencies, with exact versions, for KSP {setup.GameVersion}.";
        CodeNote = CodeFits
            ? "Paste this in a chat. Your friend pastes it into Import on their Mods page."
            : $"This setup is too big for a chat message ({code.Length:N0} characters). Save the file and send that instead; double-clicking it opens the launcher.";
    }

    public string Summary { get; }
    public string Code { get; }
    public bool CodeFits { get; }
    public string CodeNote { get; }
    public IReadOnlyList<string> NotShared { get; }
    public bool HasNotShared => NotShared.Count > 0;
    public string NotSharedLine => $"Not included (not in CKAN, or installed by hand): {string.Join(", ", NotShared.Take(12))}{(NotShared.Count > 12 ? $" and {NotShared.Count - 12} more" : string.Empty)}.";

    [ObservableProperty] private string? _status;

    [RelayCommand]
    private void CopyCode()
    {
        Clipboard.SetText(Code);
        Status = "Code copied.";
    }

    [RelayCommand]
    private void SaveFile()
    {
        var safe = string.Concat(_setup.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = safe + ".vmpack", Filter = "Shared setup (*.vmpack)|*.vmpack", DefaultExt = ".vmpack" };
        if (dialog.ShowDialog() == true)
        {
            File.WriteAllText(dialog.FileName, SetupShare.ToJson(_setup));
            Status = $"Saved {Path.GetFileName(dialog.FileName)}.";
        }
    }
}

/// <summary>The Import dialog: paste a code or open a file, see what it contains, choose where it goes.</summary>
public sealed partial class ImportViewModel : ObservableObject
{
    public ImportViewModel(IReadOnlyList<FoundInstall> found, string? defaultSource, SetupInfo current, string? text)
    {
        Current = current;
        Target = new NewSetupViewModel(found, defaultSource);
        Target.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CanImport));
        CanUseCurrent = !current.IsPack && current.IsReady;
        _text = text ?? string.Empty;
        Parse();
    }

    public SetupInfo Current { get; }
    public NewSetupViewModel Target { get; }
    public bool CanUseCurrent { get; }
    public ObservableCollection<string> Preview { get; } = new();

    [ObservableProperty] private string _text;
    [ObservableProperty] private SharedSetup? _setup;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _summary = string.Empty;

    /// <summary>True: a new launcher copy (recommended); false: add the mods to the current setup.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    private bool _newCopy = true;

    public bool CanImport => Setup is not null && (!NewCopy || Target.CanCreate) && (NewCopy || CanUseCurrent);

    partial void OnTextChanged(string value) => Parse();

    private void Parse()
    {
        Preview.Clear();
        if (string.IsNullOrWhiteSpace(Text))
        {
            Setup = null;
            Error = null;
            Summary = string.Empty;
            OnPropertyChanged(nameof(CanImport));
            return;
        }
        if (SetupShare.TryParse(Text, out var s, out var error))
        {
            Setup = s;
            Error = null;
            Summary = $"\"{s!.Name}\"{(s.By is { } by ? $" from {by}" : string.Empty)}: {s.Mods.Count(m => !m.Auto)} mods plus {s.Mods.Count(m => m.Auto)} dependencies{(s.GameVersion is { } v ? $", made for KSP {v}" : string.Empty)}.";
            foreach (var m in s.Mods.Where(m => !m.Auto).Take(14))
            {
                Preview.Add($"{m.Id} {CatalogFormat.DisplayVersion(m.Version)}");
            }
            if (s.Mods.Count(m => !m.Auto) > 14)
            {
                Preview.Add($"and {s.Mods.Count(m => !m.Auto) - 14} more");
            }
            Target.Name = s.Name;
        }
        else
        {
            Setup = null;
            Error = error;
            Summary = string.Empty;
        }
        OnPropertyChanged(nameof(CanImport));
    }

    [RelayCommand]
    private void OpenFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Shared setup (*.vmpack)|*.vmpack|All files (*.*)|*.*" };
        if (dialog.ShowDialog() == true)
        {
            var info = new FileInfo(dialog.FileName);
            Text = info.Length > 1024 * 1024 ? string.Empty : File.ReadAllText(info.FullName);
            if (info.Length > 1024 * 1024)
            {
                Error = "That file is too large to be a shared setup.";
            }
        }
    }

    /// <summary>The changes that reproduce the shared setup (exact versions; dependencies marked as such).</summary>
    public ChangeRequest ToRequest() => new()
    {
        Install = Setup!.Mods.Where(m => !m.Auto).Select(m => m.Id).ToList(),
        Dependencies = Setup.Mods.Where(m => m.Auto).Select(m => m.Id).ToList(),
        Versions = Setup.Mods.ToDictionary(m => m.Id, m => m.Version, StringComparer.Ordinal),
    };
}
