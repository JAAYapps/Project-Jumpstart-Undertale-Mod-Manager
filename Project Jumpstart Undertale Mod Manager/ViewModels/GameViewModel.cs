using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
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
using Project_Jumpstart_Undertale_Mod_Manager.Services.Codes;
using Project_Jumpstart_Undertale_Mod_Manager.Services.GameLocator;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Launcher;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Proton;
using Project_Jumpstart_Undertale_Mod_Manager.Utilities;

namespace Project_Jumpstart_Undertale_Mod_Manager.ViewModels;

public partial class GameViewModel : ViewModelBase
{
    private readonly IStorageProvider _storageProvider;
    
    private readonly ILauncherService _launcherService;
    
    private readonly IModMergeService _mergeService;

    private readonly IGameLocatorService _gameLocator;

    private readonly IProtonLocatorService _protonLocator;

    private readonly ICodesService _codesService;

    private const string Automatic = "Automatic";

    private bool _choicesLoaded;
    
    public string GameName => new DirectoryInfo(GameDirectory).Name;
    
    public ObservableCollection<string> Executables { get; } = [];
    public ObservableCollection<string> DataFiles { get; } = [];

    public ObservableCollection<string> Runners { get; } = [Automatic, "Wine", "Proton", "Native"];

    public ObservableCollection<string> ProtonVersions { get; } = [Automatic];

    public bool IsLinux => OperatingSystem.IsLinux();

    public bool IsProtonChosen => IsLinux && SelectedRunner == "Proton";
    
    public ObservableCollection<ModItem> Mods { get; } = [];

    public ObservableCollection<CodeItem> Codes { get; } = [];

    public bool HasNoCodes => Codes.Count == 0;

    public string CodesFile => _codesService.CodesFileFor(GameName);

    public ObservableCollection<MergeTarget> MergeTargets { get; } = [];
    
    [ObservableProperty]
    private MergeTarget? _selectedTarget;

    // True only when there's a real choice to make — drives picker visibility.
    public bool HasMultipleTargets => MergeTargets.Count > 1;
    
    [ObservableProperty]
    public partial string SelectedExecutable { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SelectedDataFile { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProtonChosen))]
    public partial string SelectedRunner { get; set; } = Automatic;

    [ObservableProperty]
    public partial string SelectedProton { get; set; } = Automatic;
    
    [ObservableProperty]
    public partial string GameDirectory { get; set; }

    [ObservableProperty]
    public partial ModItem? SelectedMod { get; set; }

    [ObservableProperty]
    public partial CodeItem? SelectedCode { get; set; }

    public GameViewModel(IStorageProvider storageProvider, ILauncherService launcherService, IModMergeService mergeService, IGameLocatorService gameLocator, IProtonLocatorService protonLocator, ICodesService codesService, string gamePath)
    {
        GameDirectory = gamePath;
        _storageProvider = storageProvider;
        _launcherService = launcherService;
        _mergeService =  mergeService;
        _gameLocator = gameLocator;
        _protonLocator = protonLocator;
        _codesService = codesService;
        
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

        GameEntry? saved = gameLocator.GetEntry(gamePath);
        if (saved?.Executable is string savedExe && Executables.Contains(savedExe)) SelectedExecutable = savedExe;
        if (saved?.DataFile is string savedData && DataFiles.Contains(savedData)) SelectedDataFile = savedData;
        if (IsLinux)
        {
            foreach (ProtonInstall install in protonLocator.FindProtonInstalls())
                ProtonVersions.Add(install.Name);
        }
        if (saved?.Runner is string savedRunner && Runners.Contains(savedRunner)) SelectedRunner = savedRunner;
        if (saved?.Proton is string savedProton && ProtonVersions.Contains(savedProton)) SelectedProton = savedProton;
        _choicesLoaded = true;
        
        DiscoverMergeTargets();
        if (MergeTargets.Count > 0) SelectedTarget = MergeTargets[0];
        
        RefreshMods();
        RefreshCodes(saved);
    }

    private void RefreshCodes(GameEntry? saved)
    {
        Codes.Clear();
        IReadOnlyList<CheatCode> codes;
        try
        {
            codes = _codesService.LoadCodes(GameName);
        }
        catch (InvalidDataException ex)
        {
            MainWindowViewModel.SendLog(ex.Message, LogLevel.Warn);
            return;
        }

        foreach (CheatCode code in codes)
        {
            var item = new CodeItem(code) { IsEnabled = saved?.EnabledCodes.Contains(code.Name) == true };
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CodeItem.IsEnabled))
                    _gameLocator.SaveEnabledCodes(GameDirectory, Codes.Where(c => c.IsEnabled).Select(c => c.Name));
            };
            Codes.Add(item);
        }
    }
    
    partial void OnSelectedExecutableChanged(string value) => SaveChoices();

    partial void OnSelectedDataFileChanged(string value) => SaveChoices();

    partial void OnSelectedRunnerChanged(string value) => SaveChoices();

    partial void OnSelectedProtonChanged(string value) => SaveChoices();

    private void SaveChoices()
    {
        if (_choicesLoaded)
            _gameLocator.SaveChoices(GameDirectory, SelectedExecutable, SelectedDataFile, SelectedRunner, SelectedProton);
    }

    private LaunchOptions BuildLaunchOptions()
    {
        if (SelectedRunner != "Proton")
            return new LaunchOptions(SelectedRunner);

        string? appId = _protonLocator.SteamAppIdFor(GameDirectory);
        var installs = _protonLocator.FindProtonInstalls();
        ProtonInstall? steamChoice = appId is null ? null : _protonLocator.SteamChoiceFor(appId);
        ProtonInstall? proton = SelectedProton == Automatic
            ? steamChoice
              ?? installs.FirstOrDefault(i => i.Name == "Proton - Experimental")
              ?? installs.FirstOrDefault()
            : installs.FirstOrDefault(i => i.Name == SelectedProton);

        bool sameProtonAsSteam = steamChoice is not null && proton is not null && steamChoice.Directory == proton.Directory;
        string compatData = (sameProtonAsSteam ? _protonLocator.CompatDataFor(GameDirectory) : null)
                            ?? Path.Combine(AppContext.BaseDirectory, "prefixes", GameName);
        return new LaunchOptions("Proton", proton?.Directory, compatData, _protonLocator.SteamRoot, appId);
    }

    [RelayCommand]
    private async Task AddModAsync()
    {
        var files = await _storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a mod (.zip)",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Mod archive") { Patterns = ["*.zip"] }]
        });

        if (files.Count == 0)
            return;

        try
        {
            string newModDir = ImportModZip(files[0].Path.LocalPath, ModsDirectory);
            MainWindowViewModel.SendLog($"Added mod {Path.GetFileName(newModDir)}");
            RefreshMods();
            SelectedMod = Mods.FirstOrDefault(m => m.ModDirectory == newModDir) ?? SelectedMod;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            MainWindowViewModel.SendLog(ex.Message, LogLevel.Error);
            await (Application.Current?.ShowError(ex.Message, "Could not add mod") ?? Task.FromResult(ButtonResult.Ok));
        }
    }

    /// <summary>
    /// Unpack a mod .zip into its own folder under <paramref name="modsDir"/> and
    /// return that folder. mod.json may sit at the top of the zip or inside one
    /// top-level folder; either way the new folder has mod.json directly in it.
    /// </summary>
    public static string ImportModZip(string zipPath, string modsDir)
    {
        Directory.CreateDirectory(modsDir);

        // Unpack beside the final location so the move below stays on one drive.
        string staging = Path.Combine(modsDir, ".import_" + Guid.NewGuid().ToString("N"));
        try
        {
            ZipFile.ExtractToDirectory(zipPath, staging);

            string modRoot = staging;
            string modName = Path.GetFileNameWithoutExtension(zipPath);
            if (!File.Exists(Path.Combine(modRoot, "mod.json")))
            {
                string[] folders = Directory.GetDirectories(staging);
                if (folders.Length == 1 && File.Exists(Path.Combine(folders[0], "mod.json")))
                {
                    modRoot = folders[0];
                    modName = Path.GetFileName(modRoot);
                }
                else
                {
                    throw new InvalidOperationException(
                        $"'{Path.GetFileName(zipPath)}' is not a mod: no mod.json at the top of the zip or inside its one folder.");
                }
            }

            string destination = Path.Combine(modsDir, modName);
            if (Directory.Exists(destination))
                throw new InvalidOperationException(
                    $"A mod folder named '{modName}' already exists. Remove or rename it first.");

            Directory.Move(modRoot, destination);
            return destination;
        }
        finally
        {
            TryDeleteDir(staging);
        }
    }

    [RelayCommand]
    private void RefreshMods()
    {
        Mods.Clear();
    
        // Looks for a "mods" folder inside the Undertale or Deltarune install path
        string modsDir = ModsDirectory;

        if (!Directory.Exists(modsDir))
        {
            Directory.CreateDirectory(modsDir);
            return;
        }

        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        foreach (string folder in InLoadOrder(Directory.GetDirectories(modsDir), ReadLoadOrder()))
        {
            string jsonPath = Path.Combine(folder, "mod.json");
        
            if (File.Exists(jsonPath))
            {
                ModItem? mod;
                try
                {
                    mod = JsonSerializer.Deserialize<ModItem>(File.ReadAllText(jsonPath), jsonOptions);
                }
                catch (JsonException ex)
                {
                    MainWindowViewModel.SendLog($"Skipped {jsonPath}: it is not valid JSON ({ex.Message})", LogLevel.Error);
                    continue;
                }
            
                if (mod != null)
                {
                    mod.ModDirectory = folder;
                    mod.StorageProvider = _storageProvider;
                    mod.Init();
                    Mods.Add(mod);
                }
            }
        }
        if (Mods.Count > 0)
            SelectedMod = Mods[0];
    }

    // Later in the list wins a conflict, so the order is part of the setup.
    [RelayCommand]
    private void MoveModUp() => MoveSelectedMod(-1);

    [RelayCommand]
    private void MoveModDown() => MoveSelectedMod(+1);

    private void MoveSelectedMod(int step)
    {
        ModItem? mod = SelectedMod;
        if (mod is null) return;

        int from = Mods.IndexOf(mod);
        int to = from + step;
        if (from < 0 || to < 0 || to >= Mods.Count) return;

        Mods.Move(from, to);
        SelectedMod = mod;
    }

    /// <summary>
    /// Save the mod setup: the load order to mods/load_order.json and each
    /// mod's enabled checkbox to its mod.json.
    /// </summary>
    [RelayCommand]
    public void SaveMods()
    {
        try
        {
            Directory.CreateDirectory(ModsDirectory);
            var order = Mods.Select(m => Path.GetFileName(m.ModDirectory)).ToList();
            File.WriteAllText(LoadOrderPath, JsonSerializer.Serialize(order, new JsonSerializerOptions { WriteIndented = true }));

            foreach (ModItem mod in Mods)
                mod.SaveManifest();

            MainWindowViewModel.SendLog($"Saved {Mods.Count} mods for {GameName}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MainWindowViewModel.SendLog($"Could not save mods: {ex.Message}", LogLevel.Error);
        }
    }

    [RelayCommand]
    private void OpenGameFolder()
    {
        try
        {
            _launcherService.OpenFolder(GameDirectory);
        }
        catch (Exception ex)
        {
            MainWindowViewModel.SendLog($"Could not open {GameDirectory}: {ex.Message}", LogLevel.Error);
        }
    }

    private string ModsDirectory => Path.Combine(GameDirectory, "mods");

    private string LoadOrderPath => Path.Combine(ModsDirectory, "load_order.json");

    // Mod folder names in saved order. Empty when nothing has been saved yet.
    private List<string> ReadLoadOrder()
    {
        try
        {
            if (File.Exists(LoadOrderPath))
                return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(LoadOrderPath)) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            MainWindowViewModel.SendLog($"Ignored {LoadOrderPath}: {ex.Message}", LogLevel.Warn);
        }
        return [];
    }

    // Saved folders first, in their saved order; folders the order file does
    // not know (newly added mods) after them, by name.
    private static IEnumerable<string> InLoadOrder(string[] folders, List<string> order)
    {
        return folders
            .OrderBy(f =>
            {
                int index = order.FindIndex(name => name.Equals(Path.GetFileName(f), StringComparison.OrdinalIgnoreCase));
                return index < 0 ? int.MaxValue : index;
            })
            .ThenBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);
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
            ModDirectory = newModDir,
            StorageProvider = _storageProvider
        };
        newMod.Init();

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

        SaveMods();

        string managerRoot = AppContext.BaseDirectory;
        string tempGameDir = Path.Combine(managerRoot, "tempgame", GameName);

        SweepTempGames(managerRoot, tempGameDir);
        string? codesDir = null;

        try
        {
            GameCopyResult copy = _launcherService.RefreshGameCopy(GameDirectory, tempGameDir);
            MainWindowViewModel.SendLog($"Game copy ready: {copy.FilesCopied} files copied ({copy.BytesCopied / 1_000_000} MB), {copy.FilesRemoved} removed.");

            string relTargetDir = Path.GetRelativePath(GameDirectory, SelectedTarget.DataDirectory);
            string mergeDir = Path.Combine(tempGameDir, relTargetDir);

            string dataInTarget = Path.Combine(mergeDir, "data.win");
            if (!File.Exists(dataInTarget))
                File.Copy(Path.Combine(mergeDir, SelectedTarget.DataFileName), dataInTarget, overwrite: true);

            var modList = new List<ModSource>();
            foreach (var m in Mods)
                if (m.IsEnabled)
                    modList.Add(new ModSource(m.Name, m.ModDirectory));

            List<CheatCode> enabledCodes = Codes.Where(c => c.IsEnabled).Select(c => c.Code).ToList();
            if (enabledCodes.Count > 0)
            {
                codesDir = Path.Combine(Path.GetTempPath(), "pjum_codes_" + Guid.NewGuid().ToString("N"));
                _codesService.WriteCodesMod(enabledCodes, codesDir);
                modList.Add(new ModSource(CodesService.ModName, codesDir));
            }

            var result = await _mergeService.ApplyAsync(mergeDir, modList, tempGameDir);

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

            IReadOnlyList<string> cutFiles = SelectedExecutable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? _launcherService.FindSpaceCutFiles(Path.Combine(tempGameDir, SelectedExecutable))
                : [];
            if (cutFiles.Count > 0)
            {
                string message = $"These files sit where the path to the game has a space:\n\n{string.Join("\n", cutFiles)}\n\n" +
                                 "When the game starts another copy of itself (Deltarune does this for every chapter), " +
                                 "Windows and Wine can try to run one of these files instead, and the chapter fails with error 0xc1. " +
                                 "Move or delete them, or move the mod manager to a folder without spaces.";
                MainWindowViewModel.SendLog(message, LogLevel.Warn);
                await (Application.Current?.ShowWarning(message, "A file may stop chapters from starting") ?? Task.FromResult(ButtonResult.Ok));
            }

            // Launch and wait for the process.
            await _launcherService.LaunchAndWaitAsync(tempGameDir, SelectedExecutable, "data.win", MainWindowViewModel.SendLog, BuildLaunchOptions());
        }
        catch (Exception ex)
        {
            await (Application.Current?.ShowError(ex.Message, "Launch failed") ?? Task.FromResult(ButtonResult.Ok));
            MainWindowViewModel.SendLog(ex.Message);
        }
        finally
        {
            if (codesDir is not null)
                TryDeleteDir(codesDir);
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