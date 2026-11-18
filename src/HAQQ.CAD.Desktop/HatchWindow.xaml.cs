using System.Windows;
using System.Windows.Input;
using HAQQ.CAD.Presentation;

namespace HAQQ.CAD.Desktop;

/// <summary>
/// The Hatch window: choose a pattern from pictures, its scale and angle, and how openings are treated. For a
/// new hatch the window then closes and the areas are clicked (or their boundary objects selected) in the
/// drawing; for an existing hatch, Apply changes it.
/// </summary>
public partial class HatchWindow : Window
{
    private readonly HatchViewModel _viewModel;

    public HatchWindow(HatchViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };
    }

    private void Pick_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.PickPoints()) Close();              // otherwise the message says what to put right
    }

    private void Select_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectObjects()) Close();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Apply()) Close();
    }

    /// <summary>A double-click on a pattern goes straight on with it.</summary>
    private void Items_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.Selected is null) return;
        if (_viewModel.IsEditing ? _viewModel.Apply() : _viewModel.PickPoints()) Close();
    }

    private void ScaleUp_Click(object sender, RoutedEventArgs e) => _viewModel.StepScale(larger: true);
    private void ScaleDown_Click(object sender, RoutedEventArgs e) => _viewModel.StepScale(larger: false);
    private void Favorite_Click(object sender, RoutedEventArgs e) => _viewModel.ToggleFavorite();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
