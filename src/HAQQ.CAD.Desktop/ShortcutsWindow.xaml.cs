using System.Windows;
using HAQQ.CAD.Presentation;

namespace HAQQ.CAD.Desktop;

/// <summary>The Shortcuts window: every command and the short names that start it, which the user can change.</summary>
public partial class ShortcutsWindow : Window
{
    private readonly ShortcutsViewModel _viewModel;

    public ShortcutsWindow(ShortcutsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += (_, _) => FilterBox.Focus();
    }

    private void ResetAll_Click(object sender, RoutedEventArgs e) => _viewModel.ResetAll();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Save()) Close();          // otherwise the message says which shortcut is the problem
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
