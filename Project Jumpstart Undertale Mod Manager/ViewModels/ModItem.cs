using System.IO;
using System.Text.Json.Serialization;
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
    private string _imageFileName = string.Empty;
}