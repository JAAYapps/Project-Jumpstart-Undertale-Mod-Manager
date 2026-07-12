using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MsBox.Avalonia.Enums;
using Project_Jumpstart_Undertale_Mod_Manager.Models;
using Project_Jumpstart_Undertale_Mod_Manager.Reporting;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Launcher;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge;
using Project_Jumpstart_Undertale_Mod_Manager.Utilities;

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

    public ObservableCollection<MergeTarget> MergeTargets { get; } = new();

    [ObservableProperty]
    private MergeTarget? _selectedTarget;

    // True only when there's a real choice to make — drives picker visibility.
    public bool HasMultipleTargets => MergeTargets.Count > 1;
    
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
        
        DiscoverMergeTargets();
        if (MergeTargets.Count > 0) SelectedTarget = MergeTargets[0];
        
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
    
    [RelayCommand]
    public async Task SaveAndPlayAsync()
    {
        if (SelectedTarget is null || string.IsNullOrEmpty(SelectedExecutable))
            return;

        string managerRoot = AppContext.BaseDirectory;
        
        SweepTempGames(managerRoot); // Just in case try to delete doesn't work.

        string tempGameDir = Path.Combine(managerRoot, "tempgame", Guid.NewGuid().ToString("N"));

        try
        {
            CopyGameDir(GameDirectory, tempGameDir);

            string relTargetDir = Path.GetRelativePath(GameDirectory, SelectedTarget.DataDirectory);
            string mergeDir = Path.Combine(tempGameDir, relTargetDir);

            string dataInTarget = Path.Combine(mergeDir, "data.win");
            if (!File.Exists(dataInTarget))
                File.Copy(Path.Combine(mergeDir, SelectedTarget.DataFileName), dataInTarget, overwrite: true);

            var modList = new List<ModSource>();
            foreach (var m in Mods)
                if (m.IsEnabled)
                    modList.Add(new ModSource(m.Name, m.ModDirectory));

            var result = await _mergeService.ApplyAsync(mergeDir, modList);

            string report = MergeReport.Format(result);
            MainWindowViewModel.SendLog(report);
            string logPath = Path.Combine(managerRoot, "last_merge.log");
            try { File.WriteAllText(logPath, report); } catch { }

            if (!result.Success)
            {
                await (Application.Current?.ShowError(
                    $"{result.FailedMod}: {result.Reason}", "Merge failed") ?? Task.FromResult(ButtonResult.Ok));
                return;
            }

            if (result.Warnings.Count > 0)
            {
                await (Application.Current?.ShowWarning(
                    string.Join("\n", result.Warnings) + $"\n\nFull log: {logPath}",
                    "Merge completed with warnings") ?? Task.FromResult(ButtonResult.Ok));
            }

            // Launch and wait for the process.
            await _launcherService.LaunchAndWaitAsync(tempGameDir, SelectedExecutable, "data.win", MainWindowViewModel.SendLog);
        }
        catch (Exception ex)
        {
            await (Application.Current?.ShowError(ex.Message, "Launch failed") ?? Task.FromResult(ButtonResult.Ok));
            MainWindowViewModel.SendLog(ex.Message);
        }
        finally
        {
            TryDeleteDir(tempGameDir);
        }
    }

    private static void CopyGameDir(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceDir, dir);
            if (rel.Split(Path.DirectorySeparatorChar)[0].Equals("mods", StringComparison.OrdinalIgnoreCase))
                continue;   // don't copy the manager's own mods folder into the runtime
            Directory.CreateDirectory(Path.Combine(destDir, rel));
        }
        foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceDir, file);
            if (rel.Split(Path.DirectorySeparatorChar)[0].Equals("mods", StringComparison.OrdinalIgnoreCase))
                continue;
            string target = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch
        {
            // ignored
        }
    }
    
    private void DiscoverMergeTargets()
    {
        MergeTargets.Clear();

        // Deltarune: each chapter keeps its own data.win in chapterN_windows/.
        // Detect by looking for those folders first.
        var chapterDirs = Directory.GetDirectories(GameDirectory, "chapter*_windows")
            .OrderBy(d => d)
            .ToList();

        if (chapterDirs.Count > 0)
        {
            foreach (string dir in chapterDirs)
            {
                if (!File.Exists(Path.Combine(dir, "data.win"))) continue;
                string folder = new DirectoryInfo(dir).Name;         // "chapter1_windows"
                string label  = PrettyChapterName(folder);           // "Chapter 1"
                MergeTargets.Add(new MergeTarget { DisplayName = label, DataDirectory = dir });
            }
            return;   // a Deltarune-style install: chapters ARE the targets
        }

        // Undertale (and anything single-data): the game root is the one target.
        if (File.Exists(Path.Combine(GameDirectory, "data.win")))
            MergeTargets.Add(new MergeTarget { DisplayName = GameName, DataDirectory = GameDirectory });
        else if (!string.IsNullOrEmpty(SelectedDataFile))
            // Fallback: a .unx or oddly-named data file at the root.
            MergeTargets.Add(new MergeTarget
            {
                DisplayName = GameName,
                DataDirectory = GameDirectory,
                DataFileName = SelectedDataFile
            });
    }

    private static string PrettyChapterName(string folder)
    {
        // "chapter1_windows" -> "Chapter 1"
        var m = System.Text.RegularExpressions.Regex.Match(folder, @"chapter(\d+)");
        return m.Success ? $"Chapter {m.Groups[1].Value}" : folder;
    }
    
    private static void SweepTempGames(string managerRoot, string? keepDir = null)
    {
        string tempRoot = Path.Combine(managerRoot, "tempgame");
        if (!Directory.Exists(tempRoot)) return;

        string? keepFull = keepDir is null ? null : Path.GetFullPath(keepDir);

        foreach (string dir in Directory.GetDirectories(tempRoot))
        {
            if (keepFull is not null &&
                Path.GetFullPath(dir).Equals(keepFull, StringComparison.Ordinal))
                continue;
            try { Directory.Delete(dir, true); }
            catch
            {
                MainWindowViewModel.SendLog($"Couldn't delete {dir}");
                // ignored
            }
        }
    }

    public void OnUnload()
    {
        string tempRoot = AppContext.BaseDirectory;
        if (!Directory.Exists(tempRoot)) return;
        SweepTempGames(tempRoot);
    }
}