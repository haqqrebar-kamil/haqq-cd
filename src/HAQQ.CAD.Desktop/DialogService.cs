using System.Windows;
using HAQQ.CAD.Presentation;
using Microsoft.Win32;

namespace HAQQ.CAD.Desktop;

/// <summary>Windows dialogs behind the view model's <see cref="IUserInteraction"/> requests.</summary>
internal sealed class DialogService : IUserInteraction
{
    private const string Filter = "HAQQ CAD drawings (*.hcad)|*.hcad|All files (*.*)|*.*";
    private const string Caption = "HAQQ CAD";

    private readonly Window _owner;

    public DialogService(Window owner) => _owner = owner;

    public string? PickOpenFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open drawing",
            Filter = Filter,
            CheckFileExists = true,
        };
        return dialog.ShowDialog(_owner) == true ? dialog.FileName : null;
    }

    public string? PickSaveFile(string suggestedName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save drawing",
            Filter = Filter,
            DefaultExt = ".hcad",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = suggestedName,
        };
        return dialog.ShowDialog(_owner) == true ? dialog.FileName : null;
    }

    public string? PickPdfFile(string suggestedName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save PDF",
            Filter = "PDF (*.pdf)|*.pdf",
            DefaultExt = ".pdf",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = suggestedName,
        };
        return dialog.ShowDialog(_owner) == true ? dialog.FileName : null;
    }

    public SaveChangesChoice ConfirmSaveChanges(string documentName)
    {
        var result = MessageBox.Show(_owner, $"Save changes to {documentName}?", Caption,
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
        return result switch
        {
            MessageBoxResult.Yes => SaveChangesChoice.Save,
            MessageBoxResult.No => SaveChangesChoice.Discard,
            _ => SaveChangesChoice.Cancel,
        };
    }

    public bool Confirm(string message, string title) =>
        MessageBox.Show(_owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void ShowError(string message, string title) =>
        MessageBox.Show(_owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public void ShowInfo(string message, string title) =>
        MessageBox.Show(_owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
}
