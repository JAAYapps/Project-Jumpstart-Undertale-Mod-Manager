using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;

namespace Project_Jumpstart_Undertale_Mod_Manager.ViewModels;

public partial class ViewDetailsViewModel(string name, string author, string version, string category, string modDirectory, string imageFileName, ViewDetailsViewModel.EditDetails editDetails, ViewDetailsViewModel.PackageDetails packageDetails) : DetailsViewModel(name, author, version, category, modDirectory, imageFileName)
{
    public delegate void EditDetails();

    public delegate Task PackageDetails();

    [RelayCommand]
    private void Edit()
    {
        editDetails();
    }

    [RelayCommand]
    private Task PackageAsync() => packageDetails();
}