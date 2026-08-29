using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Project_Jumpstart_Undertale_Mod_Manager.ViewModels;

public partial class ViewDetailsViewModel(string name, string author, string version, string category, string modDirectory, string imageFileName, ViewDetailsViewModel.EditDetails editDetails) : DetailsViewModel(name, author, version, category, modDirectory, imageFileName)
{
    public delegate void EditDetails();

    [RelayCommand]
    private void Edit()
    {
        editDetails();
    }
}