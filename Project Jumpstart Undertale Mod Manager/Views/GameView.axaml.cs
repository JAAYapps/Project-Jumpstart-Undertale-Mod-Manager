using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Project_Jumpstart_Undertale_Mod_Manager.ViewModels;

namespace Project_Jumpstart_Undertale_Mod_Manager.Views;

public partial class GameView : UserControl
{
    public GameView()
    {
        InitializeComponent();
        Unloaded += (sender, args) =>
        {
            GameViewModel? gameViewModel = DataContext as GameViewModel;
            gameViewModel?.OnUnload();
        };
    }
}