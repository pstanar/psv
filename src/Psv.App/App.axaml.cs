using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Psv.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;

            if (CliArgsParser.TryParse(desktop.Args ?? [], out var cliArgs, out _) && cliArgs.Path is not null)
            {
                // null (not false) when --tail wasn't passed, so a persisted "tailing enabled"
                // setting from a previous session isn't clobbered by an unrelated CLI open.
                bool? enableTailing = cliArgs.Tail ? true : null;
                window.Opened += (_, _) => window.OpenFile(cliArgs.Path, cliArgs.Encoding, enableTailing);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}