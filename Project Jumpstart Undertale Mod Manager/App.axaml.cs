using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Codes;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Data;
using Project_Jumpstart_Undertale_Mod_Manager.Services.GameLocator;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Launcher;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge;
using Project_Jumpstart_Undertale_Mod_Manager.Services.ModPackage;
using Project_Jumpstart_Undertale_Mod_Manager.Services.OneClick;
using Avalonia.Threading;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Proton;
using Project_Jumpstart_Undertale_Mod_Manager.Services.SteamScanner;
using Project_Jumpstart_Undertale_Mod_Manager.Utilities;
using Project_Jumpstart_Undertale_Mod_Manager.ViewModels;
using Project_Jumpstart_Undertale_Mod_Manager.Views;

namespace Project_Jumpstart_Undertale_Mod_Manager;

public partial class App : Application
{
    public new static App? Current => Application.Current as App;
    public IServiceProvider? Services { get; private set; }
    
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var collection = new ServiceCollection();
        
        // Register your services here
        collection.AddSingleton<IGameLocatorService, GameLocatorService>();
        collection.AddSingleton<ILauncherService, LauncherService>();
        collection.AddSingleton<IDataService, DataService>();
        collection.AddSingleton<IModMergeService, ModMergeService>();
        collection.AddSingleton<ISteamScanner, CrossPlatformSteamScanner>();
        collection.AddSingleton<IProtonLocatorService, ProtonLocatorService>();
        collection.AddSingleton<ICodesService, CodesService>();
        collection.AddSingleton<IModPackageService, ModPackageService>();
        collection.AddSingleton<IOneClickService, OneClickService>();
        
        Services = collection.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;
            Current?.SetMessageMainWindow(mainWindow);
            // Create the ViewModel, mixing DI services with the window's StorageProvider
            var mainViewModel = ActivatorUtilities.CreateInstance<MainWindowViewModel>(
                Services, 
                mainWindow.StorageProvider);
            mainWindow.DataContext = mainViewModel;

            OneClickPipe.Listen(link => Dispatcher.UIThread.Post(() =>
            {
                mainWindow.Activate();
                _ = mainViewModel.HandleOneClickAsync(link);
            }));
            string? startLink = desktop.Args?.FirstOrDefault(a => a.StartsWith("pjum:", StringComparison.OrdinalIgnoreCase));
            if (startLink is not null)
                mainWindow.Opened += (_, _) => _ = mainViewModel.HandleOneClickAsync(startLink);
        }

        base.OnFrameworkInitializationCompleted();
    }
}