using System.IO;
using System.Windows;
using System.Windows.Input;
using HAQQ.CAD.Presentation;

namespace HAQQ.CAD.Desktop;

/// <summary>The Export PDF window: which sheets, black or colour, one file or one per sheet; then where to save.</summary>
public partial class PdfExportWindow : Window
{
    private readonly PdfExportViewModel _viewModel;
    private readonly string _suggestedName;

    /// <summary>What was written, for the command history; null when nothing was.</summary>
    public string? Result { get; private set; }

    public PdfExportWindow(PdfExportViewModel viewModel, string suggestedName)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _suggestedName = suggestedName;
        DataContext = viewModel;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export PDF",
            Filter = "PDF (*.pdf)|*.pdf",
            DefaultExt = ".pdf",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = _suggestedName,
        };
        if (dialog.ShowDialog(this) != true) return;

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var files = _viewModel.Build();
            if (files is null) return;                   // the message says why
            var written = new List<string>();
            foreach (var (sheet, pdf) in files)
            {
                string path = PdfExportViewModel.FileNameFor(dialog.FileName, sheet);
                File.WriteAllBytes(path, pdf);
                written.Add(Path.GetFileName(path));
            }
            Result = written.Count == 1 ? $"PDF written: {dialog.FileName}" : $"{written.Count} PDF files written beside {dialog.FileName}: {string.Join(", ", written)}";
            Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "The PDF could not be written: " + ex.Message + "\n\nIf the file is open in a PDF viewer, close it there and try again.",
                "Export PDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }
}
