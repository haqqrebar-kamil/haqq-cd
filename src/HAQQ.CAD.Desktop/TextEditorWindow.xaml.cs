using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using HAQQ.CAD.Presentation;

namespace HAQQ.CAD.Desktop;

/// <summary>
/// The Text window: the words with their font, height, bold, italic, underline, side and angle. A new text is
/// placed by a click after the window closes; a text opened by a double-click is saved back.
/// </summary>
public partial class TextEditorWindow : Window
{
    private readonly TextEditorViewModel _viewModel;

    public TextEditorWindow(TextEditorViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelChanged;
        ShowLook();
        Loaded += (_, _) =>
        {
            WordsBox.Focus();
            WordsBox.CaretIndex = WordsBox.Text.Length;
        };
        // Ctrl+Enter finishes without reaching for the mouse (Enter alone is a new line).
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { e.Handled = true; Commit(); }
            else if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TextEditorViewModel.Font) or nameof(TextEditorViewModel.Bold) or nameof(TextEditorViewModel.Italic)
            or nameof(TextEditorViewModel.Underline) or nameof(TextEditorViewModel.Justify)) ShowLook();
    }

    /// <summary>Shows the words in the chosen font and style, so the choice can be judged before placing.</summary>
    private void ShowLook()
    {
        try
        {
            WordsBox.FontFamily = new FontFamily(_viewModel.Font);
        }
        catch (ArgumentException)
        {
            // A name that is not a font: the box keeps the font it had.
        }
        WordsBox.FontWeight = _viewModel.Bold ? FontWeights.Bold : FontWeights.Normal;
        WordsBox.FontStyle = _viewModel.Italic ? FontStyles.Italic : FontStyles.Normal;
        WordsBox.TextDecorations = _viewModel.Underline ? TextDecorations.Underline : null;
        WordsBox.TextAlignment = _viewModel.Justify switch { "Center" => TextAlignment.Center, "Right" => TextAlignment.Right, _ => TextAlignment.Left };
    }

    private void Commit()
    {
        if (_viewModel.Commit()) Close();            // otherwise the message says what to put right
    }

    private void Commit_Click(object sender, RoutedEventArgs e) => Commit();
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
