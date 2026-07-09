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
                // null (not false) when --tail/--bin weren't passed, so a persisted "tailing
                // enabled" setting or the file's own auto-detected mode isn't overridden by an
                // unrelated CLI open.
                bool? enableTailing = cliArgs.Tail ? true : null;
                bool? forceBinary = cliArgs.Bin ? true : null;
                window.Opened += (_, _) => window.OpenFile(cliArgs.Path, cliArgs.Encoding, enableTailing, forceBinary);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}