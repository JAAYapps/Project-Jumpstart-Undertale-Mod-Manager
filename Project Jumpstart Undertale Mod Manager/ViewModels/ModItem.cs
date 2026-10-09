using System.Collections.ObjectModel;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using Project_Jumpstart_Undertale_Mod_Manager.Models;

namespace Project_Jumpstart_Undertale_Mod_Manager.ViewModels;

public partial class ModItem : ObservableObject
{
    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Author { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Version { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Category { get; set; } = string.Empty;
    
    [JsonIgnore]
    public string ModDirectory { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ImageFileName { get; set; } = string.Empty;
    
    [JsonIgnore]
    public ObservableCollection<DetailsViewModel> ModDetails { get; } = [];

    [JsonIgnore]
    [ObservableProperty]
    public partial DetailsViewModel? SelectModDetails { get; set; }

    [JsonIgnore]
    public IStorageProvider? StorageProvider;
    
    public void Init()
    {
        ModDetails.Add(new ViewDetailsViewModel(Name, Author, Version, Category, ModDirectory, ImageFileName, EditDetails));
        ModDetails.Add(new EditDetailsViewModel(Name, Author, Version, Category, ModDirectory, ImageFileName, StorageProvider, UpdateDetails, CancelChange));
        SelectModDetails = ModDetails[0];
    }
    
    public void UpdateDetails(bool updateArt, string artFileName, string sourceArtPath)
    {
        // Update the model with just the file name
        if (updateArt)
        {
            string destPath = Path.Combine(ModDirectory, artFileName);
            
            // Copy the file into the mod folder if it isn't already there
            if (sourceArtPath != destPath)
            {
                File.Copy(sourceArtPath, destPath, true);
            }
            
            ImageFileName = artFileName;
        }
        
        Name = ModDetails[0].Name = ModDetails[1].Name;
        Author = ModDetails[0].Author = ModDetails[1].Author;
        Version = ModDetails[0].Version = ModDetails[1].Version;
        Category = ModDetails[0].Category = ModDetails[1].Category;
        ModDetails[0].FullImagePath = ModDetails[1].FullImagePath = ComputeImagePath();
        
        SaveManifest();
        
        SelectModDetails = ModDetails[0];
    }

    // Writes the details this screen owns into mod.json and leaves every other
    // key (newAssets, requires, integrityHash, anything a mod author added)
    // exactly as it was. Serializing this ModItem instead would drop them.
    public void SaveManifest()
    {
        string jsonPath = Path.Combine(ModDirectory, "mod.json");
        // Case-insensitive so an existing "Name" or "name" is updated in place
        // rather than getting a second key beside it.
        var nodeOptions = new JsonNodeOptions { PropertyNameCaseInsensitive = true };

        JsonObject manifest;
        try
        {
            manifest = File.Exists(jsonPath)
                ? JsonNode.Parse(File.ReadAllText(jsonPath), nodeOptions) as JsonObject ?? new JsonObject(nodeOptions)
                : new JsonObject(nodeOptions);
        }
        catch (JsonException ex)
        {
            MainWindowViewModel.SendLog($"Did not save {jsonPath}: it is not valid JSON ({ex.Message})", LogLevel.Error);
            return;
        }

        manifest["isEnabled"] = IsEnabled;
        manifest["name"] = Name;
        manifest["author"] = Author;
        manifest["version"] = Version;
        manifest["category"] = Category;
        manifest["imageFileName"] = ImageFileName;

        // Relaxed escaping keeps ">=1.0.0" readable instead of "\u003E=1.0.0".
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(jsonPath, manifest.ToJsonString(options));
    }

    private void EditDetails()
    {
        SelectModDetails = ModDetails[1];
    }

    private void CancelChange()
    {
        SelectModDetails = ModDetails[0];
        ModDetails[1].Name = Name;
        ModDetails[1].Author = Author;
        ModDetails[1].Version = Version;
        ModDetails[1].Category = Category;
    }

    public string ComputeImagePath()
    {
        return string.IsNullOrEmpty(ImageFileName) ? string.Empty : Path.Combine(ModDirectory, ImageFileName);
    }
}