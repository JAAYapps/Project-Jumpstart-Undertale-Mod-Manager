using Avalonia.Controls;
using Avalonia.Threading;
using Project_Jumpstart_Undertale_Mod_Manager.ViewModels;

namespace Project_Jumpstart_Undertale_Mod_Manager.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += (s, e) =>
        {
            MainWindowViewModel? vm = DataContext as MainWindowViewModel;
            vm?.OnClose();
        };
        MainWindowViewModel.ConsoleLogs.CollectionChanged += (_, _) =>
            Dispatcher.UIThread.Post(() =>
            {
                if (LogList.ItemCount > 0)
                    LogList.ScrollIntoView(LogList.ItemCount - 1);
            }, DispatcherPriority.Background);
    }
}