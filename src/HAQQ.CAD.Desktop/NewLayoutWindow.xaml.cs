using System.Windows;
using System.Windows.Input;
using HAQQ.CAD.Presentation;

namespace HAQQ.CAD.Desktop;

/// <summary>The New Layout window: name, paper, orientation, what the sheet starts with.</summary>
public partial class NewLayoutWindow : Window
{
    private readonly NewLayoutViewModel _viewModel;

    public NewLayoutWindow(NewLayoutViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
            else if (e.Key == Key.Enter) { e.Handled = true; Create(); }
        };
    }

    private void Create()
    {
        if (_viewModel.Create()) Close();            // otherwise the message says what to put right
    }

    private void Create_Click(object sender, RoutedEventArgs e) => Create();
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
