using CommunityToolkit.Mvvm.ComponentModel;
using VmpLauncher.Net.Directory;

namespace VmpLauncher.App.ViewModels;

/// <summary>The Add server / Edit server dialog on the Play page.</summary>
public sealed partial class AddServerViewModel : ObservableObject
{
    private readonly IReadOnlyList<KnownRoute> _known;

    /// <param name="known">The routes already listed (without the one being edited).</param>
    /// <param name="editing">The server being edited, or null for a new one.</param>
    public AddServerViewModel(IReadOnlyList<KnownRoute> known, MyServer? editing)
    {
        _known = known;
        if (editing is not null)
        {
            _name = editing.Name;
            _address = editing.Port == MyServers.DefaultPort ? editing.Host : $"{editing.Host}:{editing.Port}";
        }
        Refresh();
    }

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _address = string.Empty;

    /// <summary>What is wrong with what was typed, or null (also null while a field is still empty).</summary>
    [ObservableProperty]
    private string? _error;

    /// <summary>The server to save, once both fields are good.</summary>
    public MyServer? Result { get; private set; }

    public bool CanSave => Result is not null;

    partial void OnNameChanged(string value) => Refresh();

    partial void OnAddressChanged(string value) => Refresh();

    private void Refresh()
    {
        var check = MyServers.Check(Name, Address, _known);
        Result = check.Server;
        Error = check.Error;
        OnPropertyChanged(nameof(CanSave));
    }
}
