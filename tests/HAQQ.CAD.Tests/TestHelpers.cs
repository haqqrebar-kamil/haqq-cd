using HAQQ.CAD.Commands;
using HAQQ.CAD.Core;
using HAQQ.CAD.Geometry;
using HAQQ.CAD.Presentation;
using HAQQ.CAD.Rendering;

namespace HAQQ.CAD.Tests;

internal static class T
{
    public static Point2D P(double x, double y) => new(x, y);

    /// <summary>Adds a line on the current layer directly (no undo record).</summary>
    public static LineEntity AddLine(DrawingDocument doc, double x1, double y1, double x2, double y2)
    {
        var line = new LineEntity(P(x1, y1), P(x2, y2)) { LayerId = doc.CurrentLayerId };
        doc.AddEntity(line);
        return line;
    }

    public static CircleEntity AddCircle(DrawingDocument doc, double cx, double cy, double r)
    {
        var circle = new CircleEntity(P(cx, cy), r) { LayerId = doc.CurrentLayerId };
        doc.AddEntity(circle);
        return circle;
    }

    public static List<string> CaptureMessages(EditorSession session)
    {
        var messages = new List<string>();
        session.MessagePosted += messages.Add;
        return messages;
    }
}

/// <summary>A scratch folder that is deleted when the test finishes.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "haqqcad-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

/// <summary>Scripted replacement for the dialogs of the desktop shell.</summary>
internal sealed class FakeUi : IUserInteraction
{
    public string? OpenPath { get; set; }
    public string? SavePath { get; set; }
    public SaveChangesChoice SaveChangesAnswer { get; set; } = SaveChangesChoice.Discard;
    public Queue<bool> ConfirmAnswers { get; } = new();
    public List<string> Errors { get; } = new();
    public List<string> Infos { get; } = new();
    public int SaveChangesPrompts { get; private set; }

    public string? PickOpenFile() => OpenPath;
    public string? PickSaveFile(string suggestedName) => SavePath;

    public SaveChangesChoice ConfirmSaveChanges(string documentName)
    {
        SaveChangesPrompts++;
        return SaveChangesAnswer;
    }

    public bool Confirm(string message, string title) => ConfirmAnswers.Count == 0 || ConfirmAnswers.Dequeue();
    public void ShowError(string message, string title) => Errors.Add(message);
    public void ShowInfo(string message, string title) => Infos.Add(message);
}

/// <summary>Render surface that records every primitive it is asked to draw.</summary>
internal sealed class RecordingSurface : IRenderSurface
{
    public List<(double X1, double Y1, double X2, double Y2, RenderStyle Style)> Lines { get; } = new();
    public List<(ScreenPoint[] Points, bool Closed, RenderStyle Style)> Polylines { get; } = new();
    public List<(double Cx, double Cy, double R, RenderStyle Style)> Circles { get; } = new();
    public List<(double X, double Y, double W, double H, RenderStyle Style, byte Fill)> Rectangles { get; } = new();

    public List<(ScreenPoint[] Points, RenderStyle Style)> Polygons { get; } = new();
    public void FillPolygon(IReadOnlyList<ScreenPoint> points, in RenderStyle style) => Polygons.Add((points.ToArray(), style));

    public int Count => Lines.Count + Polylines.Count + Circles.Count + Rectangles.Count;

    public List<(double X, double Y, double W, double H)> Clips { get; } = new();
    public int ClipDepth { get; private set; }
    public void PushClip(double x, double y, double width, double height) { Clips.Add((x, y, width, height)); ClipDepth++; }
    public void PopClip() => ClipDepth--;

    public List<(string Text, double X, double Y, double Height, double Angle, RenderStyle Style)> Texts { get; } = new();
    public void DrawText(string text, double cx, double cy, double height, double angle, in RenderStyle style) => Texts.Add((text, cx, cy, height, angle, style));

    public void DrawLine(double x1, double y1, double x2, double y2, in RenderStyle style) => Lines.Add((x1, y1, x2, y2, style));
    public void DrawPolyline(IReadOnlyList<ScreenPoint> points, bool closed, in RenderStyle style) => Polylines.Add((points.ToArray(), closed, style));
    public void DrawCircle(double cx, double cy, double radius, in RenderStyle style) => Circles.Add((cx, cy, radius, style));
    public void DrawRectangle(double x, double y, double width, double height, in RenderStyle style, byte fillAlpha) => Rectangles.Add((x, y, width, height, style, fillAlpha));
}
