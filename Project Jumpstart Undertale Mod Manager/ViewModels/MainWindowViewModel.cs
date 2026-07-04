using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Project_Jumpstart_Undertale_Mod_Manager.Services.GameLocator;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Launcher;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge;

namespace Project_Jumpstart_Undertale_Mod_Manager.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IStorageProvider? _storageProvider = null;
    private readonly IGameLocatorService? _gameLocator = null;

    [ObservableProperty]
    private bool _showCredits = false;
    
    public ObservableCollection<GameViewModel> Games { get; } = new ObservableCollection<GameViewModel>();
    
    [ObservableProperty]
    private GameViewModel? _selectedGame;
    
    public MainWindowViewModel(IStorageProvider storageProvider, IGameLocatorService gameLocator, ILauncherService launcherService, IModMergeService mergeService)
    {
        _storageProvider = storageProvider;
        _gameLocator = gameLocator;
        List<string> games = gameLocator.FindGameInstallations();
        Console.WriteLine($"Found {games.Count} game installations");
        foreach (string game in games)
            Games.Add(new GameViewModel(storageProvider, launcherService, mergeService, game));
        if (Games.Count > 0)
            SelectedGame = Games[0];
    }

    public MainWindowViewModel() {}
    
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
            Console.WriteLine($"Added game {filePath}");
        }
    }
    
    [RelayCommand]
    private void SaveAndPlay()
    {
        SelectedGame?.SaveAndPlayAsync(); 
    }
    
    [RelayCommand]
    public void ToggleCredits()
    {
        ShowCredits = !ShowCredits;
    }
}