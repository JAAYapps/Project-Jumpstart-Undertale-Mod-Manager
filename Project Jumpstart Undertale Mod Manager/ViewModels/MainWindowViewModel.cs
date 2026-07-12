using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Project_Jumpstart_Undertale_Mod_Manager.Models;
using Project_Jumpstart_Undertale_Mod_Manager.Services.GameLocator;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Launcher;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge;

namespace Project_Jumpstart_Undertale_Mod_Manager.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IStorageProvider? _storageProvider = null;
    private readonly IGameLocatorService? _gameLocator = null;

    [ObservableProperty]
    public partial bool ShowCredits { get; set; } = false;
    
    [ObservableProperty]
    public partial bool ShowLogs { get; set; } = true;
    
    [ObservableProperty]
    public partial RowDefinition LogHeight { get; set; }

    public ObservableCollection<GameViewModel> Games { get; } = [];
    
    public static RingLog ConsoleLogs { get; } = new(max: 500);
    
    [ObservableProperty]
    private GameViewModel? _selectedGame;
    
    public MainWindowViewModel(IStorageProvider storageProvider, IGameLocatorService gameLocator, ILauncherService launcherService, IModMergeService mergeService)
    {
        _storageProvider = storageProvider;
        _gameLocator = gameLocator;
        var games = gameLocator.FindGameInstallations();
        SendLog($"Found {games.Count} game installations");
        foreach (var game in games)
            Games.Add(new GameViewModel(storageProvider, launcherService, mergeService, game));
        if (Games.Count > 0)
            SelectedGame = Games[0];
        SendLog("Ready.");
    }

    public MainWindowViewModel()
    {
        SendLog("Example Log.");
    }
    
    [RelayCommand]
    private async Task AddGameAsync()
    {
        var files = await _storageProvider?.OpenFolderPickerAsync(new FolderPickerOpenOptions()
        {
            AllowMultiple =  false,
            Title = "Select game installation"
        })!;

        if (files.Count > 0)
        {
            string filePath = files[0].Path.LocalPath;
            _gameLocator?.AddCustomGame(filePath);
            SendLog($"Added game {filePath}");
        }
    }
    
    [RelayCommand]
    private async Task SaveAndPlayAsync()
    {
        if (SelectedGame is not null)
            await SelectedGame.SaveAndPlayAsync();
    }
    
    [RelayCommand]
    public void ToggleCredits()
    {
        ShowCredits = !ShowCredits;
    }

    public void OnClose()
    {
        SelectedGame?.OnUnload();
    }

    public static void SendLog(string message) => SendLog(message, LogLevel.Info);

    public static void SendLog(string message, LogLevel level)
    {
        var entry = new LogEntry(level, DateTime.Now, message);
        if (Dispatcher.UIThread.CheckAccess())
            ConsoleLogs.Append(entry);
        else
            Dispatcher.UIThread.Post(() => ConsoleLogs.Append(entry));
    }
}