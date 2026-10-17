using Avalonia;
using System;
using System.Linq;
using Project_Jumpstart_Undertale_Mod_Manager.Utilities;

namespace Project_Jumpstart_Undertale_Mod_Manager;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        string? link = args.FirstOrDefault(a => a.StartsWith("pjum:", StringComparison.OrdinalIgnoreCase));
        if (link is not null && OneClickPipe.TrySend(link))
            return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}