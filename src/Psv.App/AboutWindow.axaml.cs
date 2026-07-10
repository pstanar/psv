using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Psv.App;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        // Process.MainModule.FileVersionInfo reads the version resource baked into the running
        // executable itself, unlike Assembly.Location-based lookups which return an empty path
        // for single-file publishes (our normal distribution form - see scripts/publish.*).
        // ProductVersion (AssemblyInformationalVersion) already includes the file version's
        // 4 parts plus the build's git SHA, so it's the only string worth showing here.
        string? productVersion = Process.GetCurrentProcess().MainModule?.FileVersionInfo.ProductVersion;

        VersionText.Text = $"Version {productVersion ?? GetFallbackVersion()}";
    }

    private static string GetFallbackVersion() =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "unknown";

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
