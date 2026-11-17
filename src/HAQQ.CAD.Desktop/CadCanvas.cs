using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using HAQQ.CAD.Geometry;
using HAQQ.CAD.Presentation;
using HAQQ.CAD.Rendering;
using CadRenderOptions = HAQQ.CAD.Rendering.RenderOptions;

namespace HAQQ.CAD.Desktop;

/// <summary>
/// The drawing surface. It owns the view transform, turns mouse input into model-space input for
/// the editing session and paints the scene through <see cref="SceneRenderer"/>. All geometry
/// decisions (snapping, selection, commands) are made in model space by the session.
/// </summary>
public sealed class CadCanvas : FrameworkElement
{
    private const double PickBoxPixels = 5;
    private const double GripPickPixels = 6;
    private const double GripDragPixels = 4;
    private const double SnapAperturePixels = 10;
    private const double WheelZoomFactor = 1.2;

    private static readonly Brush BackgroundBrush = CreateBackground();
    private static readonly Brush TipPromptBrush = Frozen(Color.FromRgb(0x3A, 0x3F, 0x47));
    private static readonly Brush TipFieldBrush = Frozen(Color.FromRgb(0x18, 0x1B, 0x1F));
    private static readonly Brush TipTypingBrush = Frozen(Color.FromRgb(0x1F, 0x4E, 0x8C));
    private static readonly Brush TipTextBrush = Frozen(Color.FromRgb(0xE4, 0xE6, 0xEA));
    private static readonly Pen TipBorderPen = FrozenPen(Color.FromRgb(0x6A, 0x72, 0x7E));
    private static readonly Typeface TipTypeface = new("Segoe UI");
    private static readonly Pen GuidePen = CreateGuidePen();

    private static Pen CreateGuidePen()
    {
        var pen = new Pen(Frozen(Color.FromRgb(0xA0, 0xA6, 0xB0)), 1) { DashStyle = new DashStyle(new double[] { 2, 3 }, 0) };
        pen.Freeze();
        return pen;
    }

    private readonly ViewTransform _view = new();
    private readonly SceneRenderer _renderer = new();
    private readonly WpfRenderSurface _surface = new();

    private MainViewModel? _viewModel;
    private Point? _mouse;            // last mouse position over the canvas, in pixels
    private Point? _panLast;          // last position while panning with the middle button
    private Point? _gripDown;         // where the left button went down on a grip, while it is still held
    private Point? _windowStart;      // first corner of a selection window, in pixels
    private bool _zoomExtentsPending = true;

    public CadCanvas()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        Cursor = Cursors.None; // the crosshair is drawn by the renderer
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color)
    {
        var pen = new Pen(Frozen(color), 1);
        pen.Freeze();
        return pen;
    }

    /// <summary>
    /// Shift held while a command asks for a point temporarily switches ORTHO. Call when the Shift
    /// key goes down or up so the preview follows without waiting for a mouse move.
    /// </summary>
    public void RefreshModifiers()
    {
        if (_viewModel is null) return;
        bool before = _viewModel.Session.OrthoOverride;
        ApplyShiftOverride();
        if (before == _viewModel.Session.OrthoOverride) return;
        if (_mouse is { } mouse) TrackMouse(mouse);
        InvalidateVisual();
    }

    private void ApplyShiftOverride()
    {
        if (_viewModel is null) return;
        var session = _viewModel.Session;
        bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        session.OrthoOverride = shift && session.WantsPoint;
    }

    /// <summary>
    /// Draws dynamic input. For a first point: prompt, X and Y beside the crosshair. For a following
    /// point: the length on a dimension along the rubber-band line and the angle on an arc measured
    /// from the horizontal, with the prompt (and anything being typed) beside the crosshair.
    /// </summary>
    private void DrawDynamicInput(DrawingContext dc, Point mouse)
    {
        var info = _viewModel?.GetDynamicInput();
        if (info is null) return;

        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Text(string text) => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            TipTypeface, 12, TipTextBrush, pixelsPerDip);

        bool onLine = false;
        if (info.From is { } fromModel && !info.IsTyping)
        {
            var f = _view.WorldToScreen(fromModel);
            var c = _view.WorldToScreen(info.To);
            double dx = c.X - f.X, dy = c.Y - f.Y;
            double r = Math.Sqrt(dx * dx + dy * dy);
            if (r >= 45 && r < 20000)
            {
                onLine = true;
                var from = new Point(f.X, f.Y);
                var to = new Point(c.X, c.Y);
                double ux = dx / r, uy = dy / r;

                // Angle: dotted horizontal reference, dotted arc up to the line, value on the arc.
                var reference = new Point(from.X + r, from.Y);
                dc.DrawLine(GuidePen, from, reference);
                double degrees = info.AngleDegrees;
                if (degrees > 0.05 && degrees < 359.95)
                {
                    var arc = new StreamGeometry();
                    using (StreamGeometryContext context = arc.Open())
                    {
                        context.BeginFigure(reference, false, false);
                        // Counter-clockwise in the model is counter-clockwise on screen (Y is flipped once).
                        context.ArcTo(to, new Size(r, r), 0, degrees > 180, SweepDirection.Counterclockwise, true, false);
                    }
                    arc.Freeze();
                    dc.DrawGeometry(null, GuidePen, arc);
                }
                double half = degrees * Math.PI / 360.0;
                DrawCenteredBox(dc, Text(info.Field2.TrimStart('<', ' ')), new Point(from.X + r * Math.Cos(half), from.Y - r * Math.Sin(half)), TipFieldBrush);

                // Length: a dotted dimension parallel to the line, on the side away from the angle arc.
                double nx = -uy, ny = ux;
                if (nx > 0) { nx = -nx; ny = -ny; }
                const double offset = 30, overshoot = 6;
                var a1 = new Point(from.X + nx * offset, from.Y + ny * offset);
                var a2 = new Point(to.X + nx * offset, to.Y + ny * offset);
                dc.DrawLine(GuidePen, from, new Point(from.X + nx * (offset + overshoot), from.Y + ny * (offset + overshoot)));
                dc.DrawLine(GuidePen, to, new Point(to.X + nx * (offset + overshoot), to.Y + ny * (offset + overshoot)));
                dc.DrawLine(GuidePen, a1, a2);
                DrawCenteredBox(dc, Text(info.Field1), new Point((a1.X + a2.X) / 2, (a1.Y + a2.Y) / 2), TipTypingBrush);
            }
        }

        // Boxes beside the crosshair.
        const double padX = 6, height = 22, gap = 4;
        var parts = new List<(FormattedText Text, Brush Fill)>();
        void Add(string text, Brush fill)
        {
            if (text.Length > 0) parts.Add((Text(text), fill));
        }
        Add(info.PolarText.Length > 0 ? info.PolarText : info.Prompt, TipPromptBrush);
        if (!onLine)
        {
            Add(info.Field1, info.IsTyping && info.ActiveField == 0 ? TipTypingBrush : TipFieldBrush);
            Add(info.Field2, info.IsTyping && info.ActiveField == 1 ? TipTypingBrush : TipFieldBrush);
        }

        double x = mouse.X + 20, y = mouse.Y + 20, total = 0;
        foreach (var part in parts) total += part.Text.Width + 2 * padX + gap;
        if (x + total > ActualWidth) x = Math.Max(0, mouse.X - 20 - total);   // keep it on screen
        if (y + height > ActualHeight) y = Math.Max(0, mouse.Y - 20 - height);

        foreach (var part in parts)
        {
            double width = part.Text.Width + 2 * padX;
            dc.DrawRectangle(part.Fill, TipBorderPen, new Rect(Math.Round(x) + 0.5, Math.Round(y) + 0.5, Math.Round(width), height));
            dc.DrawText(part.Text, new Point(Math.Round(x) + padX, Math.Round(y) + (height - part.Text.Height) / 2));
            x += width + gap;
        }
    }

    private static void DrawCenteredBox(DrawingContext dc, FormattedText text, Point center, Brush fill)
    {
        const double padX = 6, height = 20;
        double width = Math.Round(text.Width + 2 * padX);
        double x = Math.Round(center.X - width / 2), y = Math.Round(center.Y - height / 2);
        dc.DrawRectangle(fill, TipBorderPen, new Rect(x + 0.5, y + 0.5, width, height));
        dc.DrawText(text, new Point(x + padX, y + (height - text.Height) / 2));
    }

    private static Brush CreateBackground()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x21, 0x25, 0x2B));
        brush.Freeze();
        return brush;
    }

    /// <summary>Connects the canvas to the view model. Call once after construction.</summary>
    public void Attach(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        viewModel.RedrawRequested += (_, _) => InvalidateVisual();
        viewModel.ZoomExtentsRequested += (_, _) => ZoomExtents();
    }

    /// <summary>Fits the whole drawing in the view.</summary>
    public void ZoomExtents()
    {
        if (_viewModel is null || ActualWidth < 1 || ActualHeight < 1)
        {
            _zoomExtentsPending = true; // not laid out yet: do it on the first paint
            return;
        }
        _zoomExtentsPending = false;
        _view.SetViewport(ActualWidth, ActualHeight);
        _view.ZoomExtents(_viewModel.Session.Document.GetExtents());
        InvalidateVisual();
    }

    /// <summary>Esc: abandons a selection window that has only its first corner.</summary>
    public void CancelInteraction()
    {
        _windowStart = null;
        InvalidateVisual();
    }

    // ------------------------------------------------------------- painting

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _view.SetViewport(ActualWidth, ActualHeight);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        // The filled background also makes the whole element respond to the mouse.
        drawingContext.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_viewModel is null || ActualWidth < 1 || ActualHeight < 1) return;

        _view.SetViewport(ActualWidth, ActualHeight);
        if (_zoomExtentsPending)
        {
            _zoomExtentsPending = false;
            _view.ZoomExtents(_viewModel.Session.Document.GetExtents());
        }

        var session = _viewModel.Session;
        var options = new CadRenderOptions
        {
            ShowGrid = _viewModel.GridOn,
            IsSelected = session.IsHighlighted,
            Preview = session.GetPreview(),
            Snap = session.CurrentSnap,
            PolarTrack = session.CurrentPolarTrack is { } track ? (track.From, track.AngleDegrees) : null,
            TrackLines = session.CurrentObjectTrack?.Lines ?? Array.Empty<(HAQQ.CAD.Geometry.Point2D, double)>(),
            TrackPoints = session.TrackingPoints,
        };

        if (_mouse is { } mouse)
        {
            options.Crosshair = new ScreenPoint(mouse.X, mouse.Y);
            // Idle: short cross with a pick box in the middle. Picking a point: cross only.
            // Selecting objects inside a command: pick box only.
            options.CrosshairArmLength = _viewModel.GetCrosshairArmLength(ActualWidth, ActualHeight);
            options.PickBoxHalfSize = session.WantsPoint && !session.PickingObject ? 0 : PickBoxPixels;
            options.ShowCrosshairArms = session.ActiveCommand is null || session.WantsPoint;
            if (_windowStart is { } start)
            {
                // Dragging to the left makes a crossing window, to the right a containing window.
                options.SelectionRectangle =
                    (new ScreenPoint(start.X, start.Y), new ScreenPoint(mouse.X, mouse.Y), mouse.X < start.X);
            }
        }

        _surface.Target = drawingContext;
        try
        {
            _renderer.Render(_surface, session.Document, _view, options);
        }
        finally
        {
            _surface.Target = null;
        }

        DrawSnapLabel(drawingContext);
        if (_mouse is { } tipAnchor) DrawDynamicInput(drawingContext, tipAnchor);
    }

    /// <summary>The name of the acquired object snap ("Tangent", "Endpoint"...) beside its marker.</summary>
    private void DrawSnapLabel(DrawingContext dc)
    {
        if (_viewModel?.Session.CurrentSnap is not { } snap) return;
        var at = _view.WorldToScreen(snap.Point);
        var text = new FormattedText(HAQQ.CAD.Snapping.SnapEngine.NameOf(snap.Mode), CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, TipTypeface, 12, TipTextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        // Below and to the right of the marker, clear of the crosshair's input boxes above it.
        DrawCenteredBox(dc, text, new Point(at.X + 14 + text.Width / 2, at.Y + 24), TipFieldBrush);
    }

    // ---------------------------------------------------------------- mouse

    private Point2D ToModel(Point pixel) => _view.ScreenToWorld(new ScreenPoint(pixel.X, pixel.Y));

    private void TrackMouse(Point pixel)
    {
        _mouse = pixel;
        if (_viewModel is null) return;
        var session = _viewModel.Session;
        ApplyShiftOverride();
        session.UpdateCursor(ToModel(pixel), _view.PixelsToWorld(SnapAperturePixels));
        _viewModel.UpdateCoordinates(session.EffectiveCursor);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point position = e.GetPosition(this);

        if (_panLast is { } last && e.MiddleButton == MouseButtonState.Pressed)
        {
            _view.Pan(position.X - last.X, position.Y - last.Y);
            _panLast = position;
        }

        TrackMouse(position);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_panLast is null) _mouse = null;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        Point position = e.GetPosition(this);
        _view.SetViewport(ActualWidth, ActualHeight);
        _view.ZoomAt(new ScreenPoint(position.X, position.Y), e.Delta > 0 ? WheelZoomFactor : 1.0 / WheelZoomFactor);
        TrackMouse(position);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (_viewModel is null) return;
        Focus();
        Point position = e.GetPosition(this);

        switch (e.ChangedButton)
        {
            case MouseButton.Middle:
                if (e.ClickCount == 2)
                {
                    ZoomExtents();
                }
                else
                {
                    _panLast = position;
                    CaptureMouse();
                }
                break;

            case MouseButton.Left:
                OnLeftClick(position);
                break;

            case MouseButton.Right:
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) ShowMenu(_viewModel.GetObjectSnapMenu());   // Shift + right-click: object snap menu
                else if (_windowStart is not null) _windowStart = null;                  // abandon the selection window
                else if (_viewModel.Session.ActiveCommand is { WantsSelection: true })
                    _viewModel.Session.SubmitEnter();                                    // accept the selection
                else ShowContextMenu();                                                  // Enter / Cancel / options / Repeat
                break;
        }

        TrackMouse(position);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.Left && _gripDown is { } down)
        {
            _gripDown = null;
            Point up = e.GetPosition(this);
            bool dragged = Math.Abs(up.X - down.X) > GripDragPixels || Math.Abs(up.Y - down.Y) > GripDragPixels;
            if (dragged && _viewModel is not null && _viewModel.Session.ActiveCommand is HAQQ.CAD.Commands.GripStretchCommand)
            {
                // Released after dragging: this is where the grip goes (snap, ORTHO and POLAR apply).
                ApplyShiftOverride();
                _viewModel.Session.UpdateCursor(ToModel(up), _view.PixelsToWorld(SnapAperturePixels));
                _viewModel.ClickPoint();
                TrackMouse(up);
                InvalidateVisual();
            }
        }
        if (e.ChangedButton == MouseButton.Middle && _panLast is not null)
        {
            _panLast = null;
            ReleaseMouseCapture();
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _panLast = null;
    }

    private void ShowContextMenu()
    {
        if (_viewModel is null) return;
        ShowMenu(_viewModel.GetContextMenu());
    }

    private void ShowMenu(IReadOnlyList<ContextMenuEntry> entries)
    {
        if (entries.Count == 0) return;

        var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.MousePoint };
        foreach (var entry in entries) menu.Items.Add(BuildMenuItem(entry));
        menu.IsOpen = true;
    }

    private MenuItem BuildMenuItem(ContextMenuEntry entry)
    {
        var item = new MenuItem { Header = entry.Label };
        if (entry.Children is { } children)
        {
            foreach (var child in children) item.Items.Add(BuildMenuItem(child));
            return item;
        }
        if (entry.IsChecked is { } isChecked)
        {
            // A check-box entry: the menu stays open so several can be changed in one go.
            item.IsCheckable = true;
            item.IsChecked = isChecked;
            item.StaysOpenOnClick = true;
        }
        var action = entry.Invoke;
        item.Click += (_, _) =>
        {
            action();
            InvalidateVisual();
        };
        return item;
    }

    private void OnLeftClick(Point position)
    {
        if (_viewModel is null) return;
        var session = _viewModel.Session;
        Point2D model = ToModel(position);

        if (session.WantsPoint)
        {
            // Resolve snap / ortho for exactly this position, then hand the point to the command.
            ApplyShiftOverride();
            session.UpdateCursor(model, _view.PixelsToWorld(SnapAperturePixels));
            _viewModel.ClickPoint();
            return;
        }

        if (_windowStart is { } start)
        {
            var box = BoundingBox.FromCorners(ToModel(start), model);
            session.SelectBox(box, crossing: position.X < start.X);
            _windowStart = null;
            return;
        }

        // A click on a grip of a selected object starts stretching it; the next click places the grip.
        // Either drag (press, move, release) or click the grip and then click the new position.
        if (session.TryStartGripEdit(model, _view.PixelsToWorld(GripPickPixels)))
        {
            _gripDown = position;
            return;
        }

        bool toggle = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool hit = session.SelectAt(model, _view.PixelsToWorld(PickBoxPixels), toggle);
        if (!hit) _windowStart = position; // empty space: first corner of a selection window
    }
}
