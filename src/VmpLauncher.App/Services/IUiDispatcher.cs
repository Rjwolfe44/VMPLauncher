using System.Windows;

namespace VmpLauncher.App.Services;

/// <summary>Abstraction over the WPF dispatcher so ViewModels can be unit-tested without one.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
    bool CheckAccess();
}

public sealed class WpfDispatcher : IUiDispatcher
{
    public void Post(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess())
        {
            action();
        }
        else
        {
            d.BeginInvoke(action);
        }
    }

    public bool CheckAccess() => Application.Current?.Dispatcher.CheckAccess() ?? true;
}

public sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
    public bool CheckAccess() => true;
}
