using System.Windows;
using HAQQ.CAD.Presentation;

namespace HAQQ.CAD.Desktop;

/// <summary>The grid line wizard: one window for a rectangular or a sector grid, with a picture of the result.</summary>
public partial class GridWizardWindow : Window
{
    private readonly GridWizardViewModel _viewModel;

    public GridWizardWindow(GridWizardViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Create()) Close();
    }

    private void PlaceByClick_Click(object sender, RoutedEventArgs e)
    {
        // The window closes first; the grid then follows the cursor in the drawing until its origin is clicked.
        if (_viewModel.PlaceByClick()) Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
