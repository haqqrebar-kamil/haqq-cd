using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HAQQ.CAD.Presentation;

namespace HAQQ.CAD.Desktop;

/// <summary>
/// The Linetype Manager: every linetype of the drawing drawn to the same scale, and an editor that builds a
/// linetype from up to four dashes and four spaces, each with a slider and a number box.
/// </summary>
public partial class LinetypesWindow : Window
{
    private readonly LinetypeManagerViewModel _viewModel;

    public LinetypesWindow(LinetypeManagerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    /// <summary>Double-click a custom linetype to edit it.</summary>
    private void LinetypeList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.EditCommand.CanExecute(null)) _viewModel.BeginEdit();
    }

    /// <summary>Enter commits an edit box.</summary>
    private void EditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        box.SelectAll();
        e.Handled = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // The buttons do not take the keyboard, so a number still being typed has to be taken in first.
        if (Keyboard.FocusedElement is TextBox box) box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        _viewModel.Save();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
