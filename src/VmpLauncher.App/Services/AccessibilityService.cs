using System.Windows;
using Microsoft.Win32;
using Wpf.Ui.Appearance;

namespace VmpLauncher.App.Services;

/// <summary>
/// Follows Windows accessibility settings: high-contrast switches the WPF-UI theme and disables Mica,
/// and the system text scale (which WPF does not honour by itself) drives a root LayoutTransform.
/// </summary>
public sealed class AccessibilityService : IDisposable
{
    public event Action? Changed;

    public bool HighContrast => SystemParameters.HighContrast;
    public bool ReducedMotion => !SystemParameters.ClientAreaAnimation;

    /// <summary>Scale factor 1.0 .. 2.25 from Settings → Accessibility → Text size, combined with the user's own choice.</summary>
    public double SystemTextScale
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
                if (key?.GetValue("TextScaleFactor") is int pct && pct >= 100 && pct <= 225)
                {
                    return pct / 100.0;
                }
            }
            catch (Exception)
            {
            }
            return 1.0;
        }
    }

    public void Start()
    {
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        ApplyTheme();
    }

    public void ApplyTheme()
    {
        if (HighContrast)
        {
            ApplicationThemeManager.Apply(ApplicationTheme.HighContrast, Wpf.Ui.Controls.WindowBackdropType.None, false);
        }
    }

    private void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            ApplyTheme();
            Changed?.Invoke();
        });
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
}
