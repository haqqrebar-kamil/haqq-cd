using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HAQQ.CAD.Presentation;

namespace HAQQ.CAD.Desktop;

/// <summary>The Dimension Settings window: the drawing's dimension styles and every value of the selected one, with a sample picture.</summary>
public partial class DimensionStylesWindow : Window
{
    private readonly DimensionStylesViewModel _viewModel;

    public DimensionStylesWindow(DimensionStylesViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    /// <summary>Enter commits an edit box.</summary>
    private void EditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        box.SelectAll();
        e.Handled = true;
    }

    /// <summary>The buttons do not take the keyboard, so a value still being typed is taken in first.</summary>
    private static void CommitFocusedBox()
    {
        if (Keyboard.FocusedElement is TextBox box) box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        CommitFocusedBox();
        _viewModel.AddNew();
    }

    /// <summary>Double-click a style to make it the current one.</summary>
    private void StyleList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.SetCurrentCommand.CanExecute(null)) _viewModel.SetCurrentCommand.Execute(null);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        CommitFocusedBox();
        Close();
    }
}
