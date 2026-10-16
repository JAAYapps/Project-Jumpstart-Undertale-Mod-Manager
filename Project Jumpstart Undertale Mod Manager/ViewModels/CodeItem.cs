using CommunityToolkit.Mvvm.ComponentModel;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Codes;

namespace Project_Jumpstart_Undertale_Mod_Manager.ViewModels;

public partial class CodeItem(CheatCode code) : ObservableObject
{
    public CheatCode Code { get; } = code;

    public string Name => Code.Name;

    public string Description => Code.Description;

    public string Target => Code.Target;

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }
}
