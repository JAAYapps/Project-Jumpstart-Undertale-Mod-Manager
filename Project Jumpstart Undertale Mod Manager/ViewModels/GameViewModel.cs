using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Launcher;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge;

namespace Project_Jumpstart_Undertale_Mod_Manager.ViewModels;

public partial class GameViewModel : ViewModelBase
{
    private readonly IStorageProvider _storageProvider;
    
    private ILauncherService _launcherService;
    
    private IModMergeService _mergeService;
    
    public string GameName => new DirectoryInfo(GameDirectory).Name;
    
    public ObservableCollection<string> Executables { get; } = new ObservableCollection<string>();
    public ObservableCollection<string> DataFiles { get; } = new ObservableCollection<string>();
    
    public ObservableCollection<ModItem> Mods { get; } = new ObservableCollection<ModItem>();

    [ObservableProperty]
    public partial string SelectedExecutable { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SelectedDataFile { get; set; } = string.Empty;
    
    [ObservableProperty]
    public partial string GameDirectory { get; set; }

    [ObservableProperty]
    private ModItem? _selectedMod;
    
    public GameViewModel(IStorageProvider storageProvider, ILauncherService launcherService, IModMergeService mergeService, string gamePath)
    {
        GameDirectory = gamePath;
        _storageProvider = storageProvider;
        _launcherService = launcherService;
        _mergeService =  mergeService;
        
        // Scan for executables
        var exes = Directory.GetFiles(gamePath, "*.exe");
        foreach (var exe in exes) 
            Executables.Add(Path.GetFileName(exe));

        // Scan for GameMaker data files (.win for Windows/Proton, .unx for Linux native)
        var wins = Directory.GetFiles(gamePath, "*.win").ToList();
        wins.AddRange(Directory.GetFiles(gamePath, "*.unx"));
        
        foreach (var win in wins) 
            DataFiles.Add(Path.GetFileName(win));

        // Set defaults if files exist
        if (Executables.Count > 0) SelectedExecutable = Executables[0];
        if (DataFiles.Count > 0) SelectedDataFile = DataFiles[0];
        
        RefreshMods();
    }
    
    [RelayCommand]
    private async Task AddModAsync()
    {
        var files = await _storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Game Data File",
            AllowMultiple = false
        });

        if (files.Count > 0)
        {
            string filePath = files[0].Path.LocalPath;
            
            // TODO: import this mod into the game's /mods folder, then RefreshMods()
        }
    }

    [RelayCommand]
    private void RefreshMods()
    {
        Mods.Clear();
    
        // Looks for a "mods" folder inside the Undertale or Deltarune install path
        string modsDir = Path.Combine(GameDirectory, "mods");

        if (!Directory.Exists(modsDir))
        {
            Directory.CreateDirectory(modsDir);
            return;
        }

        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        foreach (string folder in Directory.GetDirectories(modsDir))
        {
            string jsonPath = Path.Combine(folder, "mod.json");
        
            if (File.Exists(jsonPath))
            {
                string jsonString = File.ReadAllText(jsonPath);
                ModItem? mod = JsonSerializer.Deserialize<ModItem>(jsonString, jsonOptions);
            
                if (mod != null)
                {
                    mod.ModDirectory = folder;
                    Mods.Add(mod);
                }
            }
        }
    }
    
    [RelayCommand]
    private async Task UploadArtAsync()
    {
        if (SelectedMod == null) return;

        var files = await _storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Mod Artwork",
            AllowMultiple = false,
            FileTypeFilter = new[] { FilePickerFileTypes.ImageAll }
        });

        if (files.Count > 0)
        {
            string sourcePath = files[0].Path.LocalPath;
            string fileName = Path.GetFileName(sourcePath);
            string destPath = Path.Combine(SelectedMod.ModDirectory, fileName);

            // Copy the file into the mod folder if it isn't already there
            if (sourcePath != destPath)
            {
                File.Copy(sourcePath, destPath, true);
            }

            // Update the model with just the file name
            SelectedMod.ImageFileName = fileName;
        
            // Write back to mod.json
            string jsonPath = Path.Combine(SelectedMod.ModDirectory, "mod.json");
            var options = new JsonSerializerOptions { WriteIndented = true };
            string jsonString = JsonSerializer.Serialize(SelectedMod, options);
            File.WriteAllText(jsonPath, jsonString);
        }
    }
    
    [RelayCommand]
    private void CreateMod()
    {
        string modsDir = Path.Combine(GameDirectory, "mods");
    
        if (!Directory.Exists(modsDir))
        {
            Directory.CreateDirectory(modsDir);
        }

        // Generate a unique folder name so multiple creations don't overwrite
        string newModDir = Path.Combine(modsDir, $"NewMod_{DateTime.Now.Ticks}");
        Directory.CreateDirectory(newModDir);

        // Create the default blank mod data
        var newMod = new ModItem
        {
            Name = "New Custom Mod",
            Author = "Author Name",
            Version = "1.0.0",
            Category = "Misc",
            ModDirectory = newModDir
        };

        // Serialize and write the mod.json file to disk
        var options = new JsonSerializerOptions { WriteIndented = true };
        string jsonString = JsonSerializer.Serialize(newMod, options);
        File.WriteAllText(Path.Combine(newModDir, "mod.json"), jsonString);

        // Add it to the live UI list
        Mods.Add(newMod);
    
        // Automatically select the new mod so the user can immediately click "Upload Art"
        SelectedMod = newMod;
    }
    
    // REPLACE the whole SaveAndPlayAsync method in GameViewModel.cs with this.
// It compiles against the new dir-model ApplyAsync(gameDir, mods) and lands the
// commit. The full temp-copy + launch + cleanup lifecycle is the LAUNCHER piece
// (next session) — marked TODO here, not silently missing.
 
    [RelayCommand]
    public async Task SaveAndPlayAsync()
    {
        if (string.IsNullOrEmpty(SelectedExecutable) || string.IsNullOrEmpty(SelectedDataFile))
            return;
 
        // ---- LAUNCHER TODO (next session): --------------------------------
        // 1. Create <managerRoot>/tempgame/<guid>/ .
        // 2. Copy the whole GameDirectory (or the Deltarune chapter dir) into it
        //    so data.win + audiogroupN.dat + loose .ogg are all present.
        // 3. Merge into that copy (below).
        // 4. Launch the runner with --game <tempdir>/<SelectedExecutable>.
        // 5. Delete the temp dir when the game process exits.
        // For now we merge into a temp copy but do not yet copy the full game
        // dir or launch — this keeps the app compiling and the merge callable
        // while the launcher lifecycle is built as its own focused piece.
        // -------------------------------------------------------------------
 
        string managerRoot = AppContext.BaseDirectory;
        string tempGameDir = Path.Combine(managerRoot, "tempgame", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempGameDir);
 
        // Minimal prep so the merge has a data.win to work on. The real launcher
        // will copy the ENTIRE game directory here (step 2 above).
        File.Copy(
            Path.Combine(GameDirectory, SelectedDataFile),
            Path.Combine(tempGameDir, "data.win"),
            overwrite: true);
 
        List<ModSource> modList = new List<ModSource>();
        foreach (var modItem in Mods)
            modList.Add(new ModSource(modItem.Name, modItem.ModDirectory));
 
        var result = await _mergeService.ApplyAsync(tempGameDir, modList);
 
        if (!result.Success)
        {
            // TODO (launcher/UI): show the "retry without {result.FailedMod}?"
            // prompt via the dialog service, then re-run with that mod removed.
            // VM stays UI-only; Core already returns FailedMod/Reason.
            return;
        }
 
        // TODO (launcher): copy full game dir + launch --game against
        //   Path.Combine(tempGameDir, SelectedExecutable) + delete on exit.
        // The old direct LaunchGame call assumed the merged file sat in
        // GameDirectory; under the dir-model it lives in tempGameDir instead.
        // _launcherService.LaunchGame(tempGameDir, SelectedExecutable, "data.win");
    }
}