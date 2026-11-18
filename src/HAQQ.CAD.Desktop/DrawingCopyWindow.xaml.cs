using System.Windows;
using System.Windows.Input;
using HAQQ.CAD.Presentation;

namespace HAQQ.CAD.Desktop;

/// <summary>
/// The Drawing Copy window: a PDF page shown as the HAQQ CAD objects it will become, with its scale, how it
/// is copied and the layers its line work goes on. Place closes the window and the copy follows the cursor.
/// </summary>
public partial class DrawingCopyWindow : Window
{
    private readonly DrawingCopyViewModel _viewModel;

    public DrawingCopyWindow(DrawingCopyViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };
    }

    private void Place_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Place()) Close();             // otherwise the message says what to put right
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
