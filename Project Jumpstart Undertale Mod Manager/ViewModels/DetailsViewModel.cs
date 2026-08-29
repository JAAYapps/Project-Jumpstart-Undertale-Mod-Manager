using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Project_Jumpstart_Undertale_Mod_Manager.ViewModels;

public abstract partial class DetailsViewModel(string name, string author, string version, string category, string modDirectory, string imageFileName) : ViewModelBase
{
    [ObservableProperty]
    public partial string Name { get; set; } = name;

    [ObservableProperty]
    public partial string Author { get; set; } = author;

    [ObservableProperty]
    public partial string Version { get; set; } = version;

    [ObservableProperty]
    public partial string Category { get; set; } = category;
    
    public string FullImagePath => string.IsNullOrEmpty(imageFileName) ? string.Empty : Path.Combine(modDirectory, imageFileName);
}