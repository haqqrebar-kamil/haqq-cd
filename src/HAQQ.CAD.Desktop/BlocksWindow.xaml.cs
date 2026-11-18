using System.Windows;
using System.Windows.Input;
using HAQQ.CAD.Presentation;

namespace HAQQ.CAD.Desktop;

/// <summary>
/// The Block Library window: the blocks of the drawing, the user's own library and the HAQQ symbols, each
/// with a picture. The chosen one is placed by clicks after the window closes.
/// </summary>
public partial class BlocksWindow : Window
{
    private readonly BlockLibraryViewModel _viewModel;

    public BlocksWindow(BlockLibraryViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };
    }

    private void Insert()
    {
        if (_viewModel.Insert()) Close();            // otherwise the message says what to put right
    }

    private void Insert_Click(object sender, RoutedEventArgs e) => Insert();

    /// <summary>A double-click on a block places it straight away.</summary>
    private void Items_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.Selected is not null) Insert();
    }

    private void SaveToLibrary_Click(object sender, RoutedEventArgs e) => _viewModel.SaveToLibrary();
    private void Delete_Click(object sender, RoutedEventArgs e) => _viewModel.Delete();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
