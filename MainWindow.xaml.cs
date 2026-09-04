using System.Windows;
using System.Windows.Controls;
using CellularDesktop.ViewModels;

namespace CellularDesktop;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += async (_, _) => await ViewModel.ConnectAsync();
    }

    private void ThreadList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: string number })
        {
            ViewModel.SelectThread(number);
        }
    }
}
