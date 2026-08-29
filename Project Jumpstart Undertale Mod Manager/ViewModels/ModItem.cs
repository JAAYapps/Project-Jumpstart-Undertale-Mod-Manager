using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;

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
    
    [JsonIgnore]
    public string FullImagePath => string.IsNullOrEmpty(ImageFileName) ? string.Empty : Path.Combine(ModDirectory, ImageFileName);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullImagePath))]
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
        
        Name  = ModDetails[0].Name = ModDetails[1].Name;
        Author = ModDetails[0].Author = ModDetails[1].Author;
        Version = ModDetails[0].Version = ModDetails[1].Version;
        Category = ModDetails[0].Category = ModDetails[1].Category;
        
        // Write back to mod.json
        string jsonPath = Path.Combine(ModDirectory, "mod.json");
        var options = new JsonSerializerOptions { WriteIndented = true };
        string jsonString = JsonSerializer.Serialize(this, options);
        File.WriteAllText(jsonPath, jsonString);
        
        SelectModDetails = ModDetails[0];
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
}