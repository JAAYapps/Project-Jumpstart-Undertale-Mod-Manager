using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Project_Jumpstart_Undertale_Mod_Manager.ViewModels;

public partial class EditDetailsViewModel : DetailsViewModel
{
    private readonly IStorageProvider? _storageProvider;
    private bool updateArt;
    private string sourceArtPath;
    private string imageArtFileName;

    [ObservableProperty]
    private string displayImagePath;
    
    public delegate void UpdateDetails(bool updateArt, string artFileName, string sourceArtPath);

    private readonly UpdateDetails _updateDetails;
    
    public delegate void CancelChange();

    private readonly CancelChange _cancelChange;
    
    public EditDetailsViewModel(string name, string author, string version, string category, string modDirectory, string imageFileName, IStorageProvider? storageProvider, UpdateDetails updateDetails, CancelChange cancelChange) : base(name, author, version, category, modDirectory, imageFileName)
    {
        _storageProvider = storageProvider;
        _updateDetails = updateDetails;
        _cancelChange = cancelChange;
        updateArt = false;
        imageArtFileName = imageFileName;
        sourceArtPath = FullImagePath;
        Name = name;
        Author = author;
        Version = version;
        Category = category;
        displayImagePath = FullImagePath;
    }
    
    [RelayCommand]
    private async Task UploadArtAsync()
    {
        if (_storageProvider != null)
        {
            IReadOnlyList<IStorageFile> files = await _storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Mod Artwork",
                AllowMultiple = false,
                FileTypeFilter = [FilePickerFileTypes.ImageAll]
            });

            if (files.Count > 0)
            {
                sourceArtPath = files[0].Path.LocalPath;
                imageArtFileName = Path.GetFileName(sourceArtPath);
            
                DisplayImagePath = sourceArtPath;
                updateArt  = true;
            }
        }
    }
    
    [RelayCommand]
    private void SaveDetails()
    {
        _updateDetails(updateArt, imageArtFileName, sourceArtPath);
        sourceArtPath = FullImagePath;
    }

    [RelayCommand]
    private void Cancel()
    {
        _cancelChange();
        sourceArtPath = FullImagePath;
        DisplayImagePath = FullImagePath;
    }
}