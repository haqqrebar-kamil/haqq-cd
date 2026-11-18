using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using HAQQ.CAD.Presentation;

namespace HAQQ.CAD.Desktop;

/// <summary>
/// The Table window, for a new table and for one opened by a double-click: its size, title and look, and a box
/// for every cell. Rows and columns are put in and taken out at the cell the cursor is in.
/// </summary>
public partial class TableWindow : Window
{
    private const double CellWidth = 130;

    private readonly TableEditorViewModel _viewModel;
    private int _row, _column;                      // the cell that last had the cursor

    public TableWindow(TableEditorViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.StructureChanged += (_, _) => BuildCells();
        BuildCells();
        Loaded += (_, _) => FocusCell(0, 0);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Margin = new Thickness(0, 0, 8, 0),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Right,
        Foreground = (Brush)Application.Current.FindResource("DimTextBrush"),
    };

    /// <summary>Lays the boxes out again: a line of column widths, then one line of cells per row.</summary>
    private void BuildCells()
    {
        CellGrid.Children.Clear();
        CellGrid.RowDefinitions.Clear();
        CellGrid.ColumnDefinitions.Clear();

        CellGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int c = 0; c < _viewModel.Columns.Count; c++) CellGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CellWidth) });
        for (int r = 0; r <= _viewModel.Rows.Count; r++) CellGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void Place(UIElement element, int row, int column)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            CellGrid.Children.Add(element);
        }

        Place(Label("Column width"), 0, 0);
        for (int c = 0; c < _viewModel.Columns.Count; c++)
        {
            var width = new TextBox { Margin = new Thickness(1, 1, 1, 7), ToolTip = "Width of this column, in drawing units", IsTabStop = false };
            width.SetBinding(TextBox.TextProperty, new Binding(nameof(TableColumnViewModel.WidthText))
            {
                Source = _viewModel.Columns[c],
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            });
            Place(width, 0, c + 1);
        }

        for (int r = 0; r < _viewModel.Rows.Count; r++)
        {
            Place(Label((r + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)), r + 1, 0);
            for (int c = 0; c < _viewModel.Columns.Count; c++)
            {
                var cell = new TextBox { Margin = new Thickness(1), Tag = (r, c) };
                cell.SetBinding(TextBox.TextProperty, new Binding(nameof(TableCellViewModel.Text))
                {
                    Source = _viewModel.Rows[r].Cells[c],
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                });
                cell.GotKeyboardFocus += Cell_GotFocus;
                Place(cell, r + 1, c + 1);
            }
        }
        _row = Math.Clamp(_row, 0, _viewModel.Rows.Count - 1);
        _column = Math.Clamp(_column, 0, _viewModel.Columns.Count - 1);
    }

    private void Cell_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { Tag: ValueTuple<int, int> at }) (_row, _column) = at;
    }

    private void FocusCell(int row, int column)
    {
        foreach (var child in CellGrid.Children)
        {
            if (child is TextBox { Tag: ValueTuple<int, int> at } box && at.Item1 == row && at.Item2 == column)
            {
                box.Focus();
                box.SelectAll();
                return;
            }
        }
    }

    private void ApplySize_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.ApplySize()) FocusCell(_row, _column);
    }

    private void SizeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _viewModel.ApplySize();
    }

    /// <summary>A row or a column in or out, beside the cell the cursor was last in.</summary>
    private void Structure_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string what }) return;
        bool done = what switch
        {
            "RowAbove" => _viewModel.InsertRow(_row),
            "RowBelow" => _viewModel.InsertRow(_row + 1),
            "RowDelete" => _viewModel.DeleteRow(_row),
            "ColumnLeft" => _viewModel.InsertColumn(_column),
            "ColumnRight" => _viewModel.InsertColumn(_column + 1),
            "ColumnDelete" => _viewModel.DeleteColumn(_column),
            _ => false,
        };
        if (!done) return;
        if (what == "RowBelow") _row++;
        if (what == "ColumnRight") _column++;
        FocusCell(Math.Clamp(_row, 0, _viewModel.Rows.Count - 1), Math.Clamp(_column, 0, _viewModel.Columns.Count - 1));
    }

    private void Commit_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Commit()) Close();            // otherwise the message says what to put right
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
