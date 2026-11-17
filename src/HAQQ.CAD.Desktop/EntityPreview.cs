using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using HAQQ.CAD.Core;
using HAQQ.CAD.Rendering;
using CadRenderOptions = HAQQ.CAD.Rendering.RenderOptions;

namespace HAQQ.CAD.Desktop;

/// <summary>
/// A picture of some objects that are not in the drawing yet (the grid being set up in the wizard), drawn by
/// the same code as the real drawing and zoomed to fit.
/// </summary>
public sealed class EntityPreview : FrameworkElement
{
    public static readonly DependencyProperty EntitiesProperty = DependencyProperty.Register(
        nameof(Entities), typeof(IReadOnlyList<Entity>), typeof(EntityPreview),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush Backdrop = CreateBackdrop();

    private readonly WpfRenderSurface _surface = new();
    private readonly SceneRenderer _renderer = new();
    private readonly ViewTransform _view = new();

    public IReadOnlyList<Entity>? Entities
    {
        get => (IReadOnlyList<Entity>?)GetValue(EntitiesProperty);
        set => SetValue(EntitiesProperty, value);
    }

    private static Brush CreateBackdrop()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x1B, 0x1E, 0x23));
        brush.Freeze();
        return brush;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Backdrop, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (ActualWidth < 20 || ActualHeight < 20 || Entities is not { Count: > 0 } entities) return;

        // A drawing of its own, so the real one is not touched.
        var document = new DrawingDocument();
        foreach (var entity in entities)
        {
            var copy = entity.CopyWithNewId();
            copy.LayerId = document.CurrentLayerId;
            if (copy.IsValid) document.AddEntity(copy);
        }
        if (document.EntityCount == 0) return;

        _view.SetViewport(ActualWidth, ActualHeight);
        _view.ZoomExtents(document.GetExtents(), 0.06);

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        _surface.Target = dc;
        try
        {
            _renderer.Render(_surface, document, _view, new CadRenderOptions { ShowGrid = false });
        }
        finally
        {
            _surface.Target = null;
            dc.Pop();
        }
    }
}
