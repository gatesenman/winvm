using Avalonia;
using Avalonia.Headless;
using WinVM.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace WinVM.Tests;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<WinVM.App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
