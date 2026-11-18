using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using HAQQ.CAD.Presentation;

namespace HAQQ.CAD.Desktop;

/// <summary>
/// The application shell. Behaviour lives in <see cref="MainViewModel"/> and the editing session;
/// this class only wires WPF input (keyboard routing, timers, window events) to them.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly TimeSpan AutosaveInterval = TimeSpan.FromMinutes(2);

    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _autosaveTimer;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel(new DialogService(this), settings: new UserSettingsStore(UserSettingsStore.DefaultPath));
        // Property edits rebuild the properties panel; run them after the edit box has finished updating.
        _viewModel.Properties.Dispatch = action => Dispatcher.BeginInvoke(action);
        DataContext = _viewModel;
        SetPropertiesCollapsed(_viewModel.PropertiesCollapsed);   // folded away unless the user left it open last time

        DrawingCanvas.Attach(_viewModel);
        _viewModel.CloseRequested += (_, _) => Close();
        _viewModel.LayersRequested += (_, _) => ShowLayers();
        // Shown after the command line has finished handling the key that asked for it.
        _viewModel.LinetypesRequested += (_, _) => Dispatcher.BeginInvoke(new Action(ShowLinetypes));
        _viewModel.ShortcutsRequested += (_, _) => Dispatcher.BeginInvoke(new Action(ShowShortcuts));
        _viewModel.DimensionStylesRequested += (_, _) => Dispatcher.BeginInvoke(new Action(ShowDimensionStyles));
        _viewModel.GridLinesRequested += (_, _) => Dispatcher.BeginInvoke(new Action(ShowGridWizard));
        _viewModel.PlotRequested += (_, _) => Dispatcher.BeginInvoke(new Action(ShowPlot));
        // Scroll after the list itself has taken in the new line. Scrolling from inside the change
        // notification forces a layout while the list is still one line behind, which WPF rejects.
        _viewModel.History.CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(new Action(ScrollHistoryToEnd), DispatcherPriority.Background);

        _autosaveTimer = new DispatcherTimer { Interval = AutosaveInterval };
        _autosaveTimer.Tick += OnAutosaveTick;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // "HaqqCad.exe drawing.hcad" (also what Windows passes for an associated file type).
        string[] args = Environment.GetCommandLineArgs();
        bool opened = args.Length > 1 && File.Exists(args[1]) && _viewModel.OpenFile(args[1]);
        if (!opened) _viewModel.OfferRecovery();

        _autosaveTimer.Start();
        ScrollHistoryToEnd();
        DrawingCanvas.Focus();
        _ = CheckForUpdateAsync();
    }

    /// <summary>
    /// Test releases look for a newer build each time they start (see tools/installer). The download runs in
    /// the background; when it is in place the user is asked whether to restart. Any failure leaves the
    /// installed program as it is and is only noted in the command history.
    /// </summary>
    private async Task CheckForUpdateAsync()
    {
        try
        {
            var channel = UpdateChannel.FromAssembly(typeof(App).Assembly);
            string? program = Environment.ProcessPath;
            if (channel is null || program is null) return;

            _viewModel.AddHistory($"HAQQ CAD test build {channel.Build}.");
            string downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HAQQ CAD", "Updates");
            Updater.CleanUp(program, downloads);

            var updater = new Updater(channel);
            _viewModel.UpdateStatus = "Checking for updates...";
            var manifest = await updater.CheckAsync();
            if (manifest is null)
            {
                if (updater.LastError is { } why)
                {
                    _viewModel.AddHistory("Update check: " + why);
                    _viewModel.UpdateStatus = "Update check failed";
                }
                else
                {
                    _viewModel.AddHistory("HAQQ CAD is up to date.");
                    _viewModel.UpdateStatus = $"Up to date (build {channel.Build})";
                }
                return;
            }

            _viewModel.AddHistory($"A newer build ({manifest.Build}) is being downloaded in the background. You can keep working.");
            _viewModel.UpdateStatus = $"Downloading update {manifest.Build}...";
            var progress = new Progress<double>(part =>
                _viewModel.UpdateStatus = $"Downloading update {manifest.Build}... {part * 100:0}%");
            string? file = await updater.DownloadAsync(manifest, downloads, default, progress);
            if (file is null)
            {
                _viewModel.AddHistory("Update: " + (updater.LastError ?? "the download failed."));
                _viewModel.UpdateStatus = "Update failed";
                return;
            }
            _viewModel.UpdateStatus = "Installing update...";
            if (!Updater.Apply(file, program, out string? error))
            {
                _viewModel.AddHistory("Update: " + error);
                _viewModel.UpdateStatus = "Update failed";
                return;
            }
            _viewModel.UpdateStatus = $"Update {manifest.Build} installed - restart to use it";

            _viewModel.AddHistory($"Build {manifest.Build} is installed and will be used the next time HAQQ CAD starts.");
            if (MessageBox.Show(this,
                    $"A newer version of HAQQ CAD (build {manifest.Build}) has been installed.\n\nRestart now to use it?",
                    "HAQQ CAD update", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;

            _restartAfterClose = program;
            Close();                                     // the usual unsaved-changes question still applies
        }
        catch (Exception ex)
        {
            _viewModel.AddHistory("Update check failed: " + ex.Message);
            _viewModel.UpdateStatus = "Update check failed";
        }
    }

    private string? _restartAfterClose;

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_restartAfterClose is not { } program) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(program) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private async void OnAutosaveTick(object? sender, EventArgs e)
    {
        try
        {
            await _viewModel.AutosaveAsync();
        }
        catch (Exception ex)
        {
            // An autosave problem must never take the editor down.
            _viewModel.AddHistory("Autosave failed: " + ex.Message);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_viewModel.RequestClose())
        {
            e.Cancel = true;
            return;
        }
        _autosaveTimer.Stop();
    }

    private void ScrollHistoryToEnd()
    {
        if (HistoryList.Items.Count > 0)
            HistoryList.ScrollIntoView(HistoryList.Items[HistoryList.Items.Count - 1]);
    }

    /// <summary>A ribbon button with a drop-down list of methods (Circle, Arc): show the list under the button.</summary>
    private void RibbonMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private GridLength _propertiesWidth = new(270);

    /// <summary>Click on the strip at the right edge: folds the Properties panel away, or brings it back.</summary>
    private void PropertiesToggle_Click(object sender, MouseButtonEventArgs e)
    {
        bool hide = PropertiesPanel.Visibility == Visibility.Visible;
        SetPropertiesCollapsed(hide);
        _viewModel.PropertiesCollapsed = hide;          // remembered for the next start
        DrawingCanvas.Focus();
        e.Handled = true;
    }

    private void SetPropertiesCollapsed(bool hide)
    {
        if (hide)
        {
            if (PropertiesColumn.Width.Value >= 170) _propertiesWidth = PropertiesColumn.Width;
            PropertiesColumn.MinWidth = 0;
            PropertiesColumn.Width = new GridLength(0);
        }
        else
        {
            PropertiesColumn.MinWidth = 170;
            PropertiesColumn.Width = _propertiesWidth.Value >= 170 ? _propertiesWidth : new GridLength(270);
        }
        PropertiesPanel.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
        PropertiesSplitter.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
        PropertiesToggleArrow.Text = hide ? "\u00AB" : "\u00BB";
        PropertiesToggleLabel.Visibility = hide ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Right-click on OSNAP: check boxes for the snaps that act automatically.</summary>
    private void OsnapButton_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not UIElement target) return;
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Top };
        foreach (var entry in _viewModel.GetSnapSettingsMenu())
        {
            var action = entry.Invoke;
            var item = new MenuItem { Header = entry.Label, IsCheckable = true, IsChecked = entry.IsChecked == true, StaysOpenOnClick = true };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>An entry of a ribbon drop-down: its Tag is the command plus option, for example "CIRCLE 2P".</summary>
    private void RibbonMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string script }) _viewModel.RunCommand(script);
        DrawingCanvas.Focus();
    }

    private LayersWindow? _layersWindow;

    /// <summary>Shows the layer table (one window, brought to the front when it is already open).</summary>
    private void ShowLayers()
    {
        if (_layersWindow is { IsLoaded: true })
        {
            _layersWindow.Activate();
            return;
        }
        _layersWindow = new LayersWindow(_viewModel) { Owner = this };
        _layersWindow.Closed += (_, _) => _layersWindow = null;
        _layersWindow.Show();
    }

    private bool _plotOpen;

    /// <summary>Shows the Plot window (preview, then the printer) as a dialog over the drawing.</summary>
    private void ShowPlot()
    {
        if (_plotOpen) return;
        _plotOpen = true;
        try
        {
            var document = _viewModel.Session.Document;
            string job = document.ActiveLayout is { } layout ? $"{_viewModel.DocumentName} - {layout.Name}" : _viewModel.DocumentName;
            var window = new PlotWindow(document, _viewModel.PlotSubject, job) { Owner = this };
            window.ShowDialog();
            if (window.Result is { } result) _viewModel.Session.Post(result);
        }
        finally
        {
            _plotOpen = false;
            DrawingCanvas.Focus();
        }
    }

    /// <summary>
    /// Page Setup and Scale on the LAYOUT tab: a list made for the drawing's units (sheets, or scales written
    /// as 1/4" = 1'-0" or 1:50), shown under the button.
    /// </summary>
    private void LayoutListButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var settings = _viewModel.Session.Document.Settings;
        var choices = button.CommandParameter as string == "Scales" ? LayoutChoices.Scales(settings) : LayoutChoices.Papers(settings);
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (var choice in choices)
        {
            var item = new MenuItem { Header = choice.Label.Replace("_", "__"), Tag = choice.Script };
            item.Click += RibbonMenuItem_Click;
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>Right-click on a tab under the drawing: opens that tab and offers what can be done with a layout.</summary>
    private void SpaceTab_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button { DataContext: SpaceTabViewModel tab } button) return;
        e.Handled = true;
        _viewModel.Session.SwitchSpace(tab.Id);

        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Top };
        void Add(string header, string script)
        {
            var item = new MenuItem { Header = header, Tag = script };
            item.Click += RibbonMenuItem_Click;
            menu.Items.Add(item);
        }
        Add("New layout", "LAYOUT");
        if (!tab.IsModel)
        {
            Add("Rename...", "LAYOUTRENAME");
            Add("Page setup...", "PAGESETUP");
            Add("Delete...", "LAYOUTDELETE");
        }
        menu.Items.Add(new Separator());
        Add("Plot...", "PLOT");
        menu.IsOpen = true;
    }

    private bool _gridWizardOpen;

    /// <summary>Shows the grid line wizard as a dialog over the drawing.</summary>
    private void ShowGridWizard()
    {
        if (_gridWizardOpen) return;
        _gridWizardOpen = true;
        try
        {
            new GridWizardWindow(new GridWizardViewModel(_viewModel.Session)) { Owner = this }.ShowDialog();
        }
        finally
        {
            _gridWizardOpen = false;
            DrawingCanvas.Focus();
        }
    }

    private bool _dimensionStylesOpen;

    /// <summary>Shows the Dimension Settings as a dialog over the drawing.</summary>
    private void ShowDimensionStyles()
    {
        if (_dimensionStylesOpen) return;
        _dimensionStylesOpen = true;
        var styles = new DimensionStylesViewModel(_viewModel.Session);
        try
        {
            new DimensionStylesWindow(styles) { Owner = this }.ShowDialog();
        }
        finally
        {
            styles.Dispose();
            _dimensionStylesOpen = false;
            DrawingCanvas.Focus();
        }
    }

    private bool _shortcutsOpen;

    /// <summary>Shows the command shortcut settings as a dialog over the drawing.</summary>
    private void ShowShortcuts()
    {
        if (_shortcutsOpen) return;
        _shortcutsOpen = true;
        try
        {
            new ShortcutsWindow(new ShortcutsViewModel(_viewModel)) { Owner = this }.ShowDialog();
        }
        finally
        {
            _shortcutsOpen = false;
            DrawingCanvas.Focus();
        }
    }

    private bool _linetypesOpen;

    /// <summary>Shows the Linetype Manager as a dialog over the drawing.</summary>
    private void ShowLinetypes()
    {
        if (_linetypesOpen) return;
        _linetypesOpen = true;
        var manager = new LinetypeManagerViewModel(_viewModel.Session);
        try
        {
            new LinetypesWindow(manager) { Owner = this }.ShowDialog();
        }
        finally
        {
            manager.Dispose();
            _linetypesOpen = false;
            DrawingCanvas.Focus();
        }
    }

    /// <summary>After a choice in a Properties drop-down the keyboard goes back to the drawing, so a command in progress carries on.</summary>
    private void PropertyCombo_DropDownClosed(object? sender, EventArgs e) => DrawingCanvas.Focus();

    /// <summary>Save in the Properties panel: what is shown becomes the properties of every new object.</summary>
    private void PropertiesSave_Click(object sender, RoutedEventArgs e)
    {
        // The button does not take the keyboard, so a value still being typed is taken in first.
        if (Keyboard.FocusedElement is TextBox box) box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        // Typed values are applied through the dispatcher; save after them.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _viewModel.Properties.Save();
            DrawingCanvas.Focus();
        }), DispatcherPriority.Background);
    }

    /// <summary>Enter commits an edit box and returns the keyboard to the drawing.</summary>
    private void EditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        DrawingCanvas.Focus();
        e.Handled = true;
    }

    /// <summary>
    /// Central keyboard routing: shortcuts work wherever the focus is, and ordinary typing always
    /// lands on the command line, as in traditional CAD programs.
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift)
        {
            DrawingCanvas.RefreshModifiers(); // Shift = temporary ORTHO while picking points
            return;
        }

        bool control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        TextBox? editBox = Keyboard.FocusedElement is TextBox focused && !ReferenceEquals(focused, CommandBox) ? focused : null;

        if (e.Key == Key.Escape)
        {
            // Leave an edit box without applying what was typed, then cancel the command.
            editBox?.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            DrawingCanvas.CancelInteraction();
            _viewModel.Cancel();
            DrawingCanvas.Focus();
            e.Handled = true;
            return;
        }

        // F10 arrives as a system key in WPF (it normally opens the window menu).
        if (e.Key == Key.F10 || (e.Key == Key.System && e.SystemKey == Key.F10))
        {
            _viewModel.PolarOn = !_viewModel.PolarOn;
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.F11: _viewModel.TrackOn = !_viewModel.TrackOn; e.Handled = true; return;
            case Key.F3: _viewModel.OsnapOn = !_viewModel.OsnapOn; e.Handled = true; return;
            case Key.F7: _viewModel.GridOn = !_viewModel.GridOn; e.Handled = true; return;
            case Key.F8: _viewModel.OrthoOn = !_viewModel.OrthoOn; e.Handled = true; return;
        }

        if (control && !alt)
        {
            switch (e.Key)
            {
                case Key.S: _viewModel.Save(); e.Handled = true; return;
                case Key.O: _viewModel.Open(); e.Handled = true; return;
                case Key.N: _viewModel.NewDocument(); e.Handled = true; return;
                case Key.Q: Close(); e.Handled = true; return;
                case Key.Z when editBox is null && !CommandBox.IsKeyboardFocused:
                    _viewModel.Session.Undo(); e.Handled = true; return;
                case Key.Y when editBox is null && !CommandBox.IsKeyboardFocused:
                    _viewModel.Session.Redo(); e.Handled = true; return;
            }
            return; // other Ctrl combinations (copy/paste in text boxes...) keep their normal meaning
        }

        if (editBox is not null || alt) return; // typing into a panel field

        if (e.Key == Key.Tab && _viewModel.Session.WantsPoint)
        {
            // Tab moves between the distance and angle boxes (or X and Y) at the crosshair.
            _viewModel.SwitchInputField();
            CommandBox.Focus();
            CommandBox.CaretIndex = CommandBox.Text.Length;
            e.Handled = true;
            return;
        }

        bool commandLineEmpty = CommandBox.Text.Length == 0;
        switch (e.Key)
        {
            case Key.Enter:
                _viewModel.SubmitCommandLine();
                e.Handled = true;
                return;

            case Key.Space when commandLineEmpty || SpaceSubmits(CommandBox.Text):
                // Space acts as Enter (repeat the last command, accept, or run a typed command name such as L)
                // unless a value such as 5'-6 1/2" is being typed.
                _viewModel.SubmitCommandLine();
                e.Handled = true;
                return;

            case Key.Delete when commandLineEmpty:
                _viewModel.DeleteSelection();
                e.Handled = true;
                return;
        }

    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift) DrawingCanvas.RefreshModifiers();
    }

    /// <summary>True when Space should send the typed text: a command name while idle, or an option keyword (letters only).</summary>
    private bool SpaceSubmits(string text) =>
        _viewModel.Session.ActiveCommand is null || text.All(char.IsLetter);

    /// <summary>
    /// Text typed while the focus is not in a text box (for example on the canvas) is appended to
    /// the command line, which then takes the keyboard focus.
    /// </summary>
    private void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;

        _viewModel.CommandText += e.Text;
        CommandBox.Focus();
        CommandBox.CaretIndex = CommandBox.Text.Length;
        e.Handled = true;
    }
}
