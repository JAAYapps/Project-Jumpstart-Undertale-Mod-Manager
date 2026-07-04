using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Project_Jumpstart_Undertale_Mod_Manager.Services.GameLocator;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Launcher;
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
        
        Services = collection.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;
            
            // Create the ViewModel, mixing DI services with the window's StorageProvider
            mainWindow.DataContext = ActivatorUtilities.CreateInstance<MainWindowViewModel>(
                Services, 
                mainWindow.StorageProvider);
        }

        base.OnFrameworkInitializationCompleted();
    }
}