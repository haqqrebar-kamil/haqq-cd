using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using HAQQ.CAD.Presentation;

namespace HAQQ.CAD.Desktop;

/// <summary>
/// The layer table: one row per layer with on/off, freeze, lock, name, colour, linetype and lineweight.
/// It edits the same layer view models as the rest of the program, so every change is undoable.
/// </summary>
public partial class LayersWindow : Window
{
    private readonly MainViewModel _viewModel;

    public LayersWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    /// <summary>A click anywhere in a row (also on its buttons and boxes) selects that row.</summary>
    private void LayerList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null; element = ParentOf(element))
        {
            if (element is ListBoxItem item)
            {
                item.IsSelected = true;
                return;
            }
        }
    }

    private static DependencyObject? ParentOf(DependencyObject element) =>
        element is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(element)
            : LogicalTreeHelper.GetParent(element);

    /// <summary>Double-click a row (not one of its edit boxes) to make that layer current.</summary>
    private void LayerList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsInside<TextBox>(source)) return;
        if (LayerList.SelectedItem is LayerViewModel layer) layer.MakeCurrent();
    }

    private static bool IsInside<T>(DependencyObject element) where T : DependencyObject
    {
        for (DependencyObject? e = element; e is not null; e = ParentOf(e))
            if (e is T) return true;
        return false;
    }

    /// <summary>Enter commits an edit box.</summary>
    private void EditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        LayerList.Focus();
        e.Handled = true;
    }

    /// <summary>Shows the standard colours under the colour cell that was clicked.</summary>
    private void ColorCell_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: LayerViewModel layer } button) return;

        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        var converter = new BrushConverter();
        foreach (string hex in _viewModel.LayerColorPalette)
        {
            string colour = hex;
            var swatch = new Border
            {
                Width = 14,
                Height = 14,
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                Background = converter.ConvertFromString(colour) as Brush,
            };
            var item = new MenuItem { Header = LayerViewModel.ColorNameOf(colour), Icon = swatch };
            item.Click += (_, _) => layer.ColorHex = colour;
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
}
