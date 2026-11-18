using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HAQQ.CAD.Core;
using HAQQ.CAD.Rendering;
using CadRenderOptions = HAQQ.CAD.Rendering.RenderOptions;

namespace HAQQ.CAD.Desktop;

/// <summary>
/// The Plot window: a preview of the layout (or the model) as it will be printed, and the way to a printer.
/// A layout is printed at its true size; the model is fitted to the page.
/// </summary>
public partial class PlotWindow : Window
{
    private readonly DrawingDocument _document;
    private readonly string _jobName;

    /// <summary>What happened, for the command history ("Plotted ... at true size"); null when nothing was printed.</summary>
    public string? Result { get; private set; }

    public PlotWindow(DrawingDocument document, string subject, string jobName)
    {
        InitializeComponent();
        _document = document;
        _jobName = jobName;
        SubjectText.Text = subject;
        SizeText.Text = document.ActiveLayout is not null
            ? "Printed at its true size when the paper in the printer is at least as large as the sheet. On smaller paper it is made smaller to fit, and the scales of its viewports no longer hold."
            : "The model has no size on paper, so everything drawn is fitted to the page. To print to a scale, make a layout and give its viewport a scale.";
        Preview.Document = document;
    }

    private void BlackOnly_Changed(object sender, RoutedEventArgs e)
    {
        // Also raised while the window is still being built, before the preview exists.
        if (Preview is not null && sender is CheckBox box) Preview.Monochrome = box.IsChecked == true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Plot_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new PrintDialog();
            try
            {
                dialog.PrintTicket.PageOrientation = PlotPlanner.PrefersLandscape(_document)
                    ? System.Printing.PageOrientation.Landscape
                    : System.Printing.PageOrientation.Portrait;
            }
            catch (Exception)
            {
                // No default printer to take a ticket from: the dialog still lets one be chosen.
            }
            if (dialog.ShowDialog() != true) return;

            double pageWidth = dialog.PrintableAreaWidth, pageHeight = dialog.PrintableAreaHeight;
            if (!(pageWidth > 1) || !(pageHeight > 1))
            {
                MessageText.Text = "The printer did not report a page size. Nothing was plotted.";
                return;
            }

            var plan = PlotPlanner.Plan(_document, pageWidth, pageHeight);
            var options = new CadRenderOptions { Plot = true, PlotMonochrome = BlackOnly.IsChecked == true, ShowGrid = false, PixelsPerMm = plan.PixelsPerMm, MinTextSize = 0.75 };

            var visual = new DrawingVisual();
            var surface = new WpfRenderSurface();
            using (var dc = visual.RenderOpen())
            {
                surface.Target = dc;
                try
                {
                    new SceneRenderer().Render(surface, _document, plan.View, options);
                }
                finally
                {
                    surface.Target = null;
                }
            }
            dialog.PrintVisual(visual, _jobName);

            Result = _document.ActiveLayout is { } layout
                ? plan.ActualSize
                    ? $"Plotted layout \"{layout.Name}\" at true size."
                    : $"Plotted layout \"{layout.Name}\" at {(plan.Shrink * 100).ToString("0.#", CultureInfo.InvariantCulture)}% of true size, because the paper is smaller than the sheet."
                : "Plotted the model, fitted to the page.";
            Close();
        }
        catch (Exception ex)
        {
            // A printer that is offline, a cancelled PDF file name, a driver fault: say so and stay open.
            MessageText.Text = "The plot could not be sent: " + ex.Message;
        }
    }
}
