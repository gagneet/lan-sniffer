using System.Windows;
using LanInspector.UI.ViewModels;

namespace LanInspector.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
