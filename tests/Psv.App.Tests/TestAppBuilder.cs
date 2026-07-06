using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(Psv.App.Tests.TestAppBuilder))]

// SettingsStore.OverridePathForTests is process-wide mutable state shared by every test that
// constructs a MainWindow; running those tests concurrently would let one test's window save
// settings to another's temp path mid-test.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Psv.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
