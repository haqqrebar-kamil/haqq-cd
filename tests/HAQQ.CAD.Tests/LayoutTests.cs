using HAQQ.CAD.Commands;
using HAQQ.CAD.Core;
using HAQQ.CAD.Documents;
using HAQQ.CAD.Geometry;
using HAQQ.CAD.Rendering;
using HAQQ.CAD.Presentation;
using Xunit;
using static HAQQ.CAD.Tests.T;

namespace HAQQ.CAD.Tests;

public class LayoutTests
{
    private static EditorSession Session()
    {
        var s = new EditorSession();
        s.Document.Settings.Format = LengthFormat.Decimal;
        AddLine(s.Document, 0, 0, 480, 0);              // a model 40 feet by 20 feet
        AddLine(s.Document, 480, 0, 480, 240);
        return s;
    }

    private static ViewportEntity Viewport(EditorSession s) => s.Document.Entities.OfType<ViewportEntity>().Single();

    [Fact]
    public void New_layout_is_a_sheet_with_one_viewport_showing_the_whole_model()
    {
        var s = Session();
        Assert.Equal(Guid.Empty, s.ActiveSpaceId);
        var layout = s.AddLayout()!;
        Assert.Equal("Layout 1", layout.Name);
        Assert.Equal((36.0, 24.0), (layout.PaperWidth, layout.PaperHeight));          // Arch D for an inch drawing
        Assert.Equal(layout.Id, s.ActiveSpaceId);                                     // and it is opened

        var vp = Viewport(s);
        Assert.Equal(layout.Id, vp.SpaceId);
        Assert.Equal(P(18, 12), vp.Center);
        Assert.True(vp.Width < 36 && vp.Height < 24);
        Assert.Equal(1 / 16.0, vp.Scale);                                             // the largest standard scale that shows it all
        Assert.Equal("3/4\" = 1'-0\"", LayoutStandards.ScaleName(vp.Scale, s.Document.Settings));
        Assert.True(vp.ModelBounds.Contains(new BoundingBox(0, 0, 480, 240)));

        var layer = s.Document.GetLayer(vp.LayerId)!;
        Assert.Equal("Viewport", layer.Name);
        Assert.False(layer.IsPlotted);                                                // the frame is not printed

        // The layout has its own objects; the model's are not in it.
        Assert.Equal(1, s.Document.EntityCount);
        Assert.Equal(new BoundingBox(0, 0, 36, 24), s.Document.GetExtents());
        s.SwitchSpace(Guid.Empty);
        Assert.Equal(2, s.Document.EntityCount);
        Assert.Empty(s.Document.Entities.OfType<ViewportEntity>());

        s.Undo();                                                                     // sheet, layer and viewport in one step
        Assert.Empty(s.Document.Layouts);
        Assert.Null(s.Document.FindLayer("Viewport"));
        Assert.Equal(2, s.Document.AllEntities.Count);
    }

    [Fact]
    public void What_is_drawn_on_a_layout_stays_on_that_layout()
    {
        var s = Session();
        var a = s.AddLayout()!;
        s.StartCommand("LINE");
        s.SubmitPoint(P(4, 4));
        s.SubmitPoint(P(30, 4));
        var onSheet = s.Document.Entities.OfType<LineEntity>().Single();
        Assert.Equal(a.Id, onSheet.SpaceId);

        var b = s.AddLayout("Details")!;
        Assert.Equal(1, s.Document.EntityCount);                                      // only its own viewport
        Assert.Null(SelectionService.PickAt(s.Document, P(18, 4), 0.5));              // the other sheet's line cannot be picked here

        s.SwitchSpace(a.Id);
        Assert.True(s.SelectAt(P(18, 4), 0.5));
        s.SwitchSpace(Guid.Empty);
        Assert.Equal(0, s.Selection.Count);                                           // changing tab clears the selection
        Assert.False(s.SelectAt(P(18, 4), 0.5));
        Assert.Equal(2, s.Document.Entities.OfType<LineEntity>().Count());

        // Undo and redo while another tab is open put the line back on its own sheet.
        s.SwitchSpace(b.Id);
        s.Undo();                                                                     // the Details layout
        s.Undo();                                                                     // the line on Layout 1
        Assert.DoesNotContain(s.Document.AllEntities, e => e.Id == onSheet.Id);
        s.Redo();
        Assert.Equal(a.Id, s.Document.AllEntities.Single(e => e.Id == onSheet.Id).SpaceId);
    }

    [Fact]
    public void Layouts_can_be_renamed_resized_and_deleted()
    {
        var s = Session();
        var layout = s.AddLayout()!;
        Assert.True(s.RenameLayout(layout.Id, "Plan"));
        Assert.Equal("Plan", s.Document.ActiveLayout!.Name);
        Assert.False(s.RenameLayout(layout.Id, "Model"));
        Assert.Null(s.AddLayout("plan"));                                             // the name is taken

        s.StartCommand("PAGESETUP");
        s.SubmitKeyword("ARCHE1");
        Assert.Equal((42.0, 30.0), (s.Document.ActiveLayout!.PaperWidth, s.Document.ActiveLayout.PaperHeight));
        s.StartCommand("PAGESETUP");
        s.SubmitText("17,11");                                                        // a size of my own
        Assert.Equal(17.0, s.Document.ActiveLayout!.PaperWidth);
        s.Undo();
        Assert.Equal(42.0, s.Document.ActiveLayout!.PaperWidth);

        s.StartCommand("LAYOUTRENAME");
        s.SubmitText("First floor plan");
        Assert.Equal("First floor plan", s.Document.ActiveLayout!.Name);

        s.StartCommand("LAYOUTDELETE");
        s.SubmitEnter();                                                              // No is the default
        Assert.Single(s.Document.Layouts);
        s.StartCommand("LAYOUTDELETE");
        s.SubmitKeyword("Y");
        Assert.Empty(s.Document.Layouts);
        Assert.Equal(Guid.Empty, s.ActiveSpaceId);
        Assert.Equal(2, s.Document.AllEntities.Count);                                // the model is untouched
        s.Undo();
        Assert.Single(s.Document.Layouts);
        Assert.Equal(3, s.Document.AllEntities.Count);                                // its viewport came back

        s.SwitchSpace(Guid.Empty);
        s.StartCommand("PAGESETUP");                                                  // the model has no paper
        Assert.Null(s.ActiveCommand);
        s.StartCommand("VIEWPORT");
        Assert.Null(s.ActiveCommand);
    }

    [Fact]
    public void Viewports_are_objects_with_a_scale()
    {
        var s = Session();
        s.AddLayout();
        s.StartCommand("VIEWPORT");
        s.SubmitPoint(P(2, 2));
        s.SubmitPoint(P(12, 8));
        Assert.Equal(2, s.Document.Entities.OfType<ViewportEntity>().Count());
        var small = s.Document.Entities.OfType<ViewportEntity>().Single(v => v.Width == 10);
        Assert.Equal(P(7, 5), small.Center);

        s.SelectAt(P(7, 2), 0.2);                                                     // by its border
        s.StartCommand("VPSCALE");
        s.SubmitText("1/4\"=1'");
        small = (ViewportEntity)s.Document.GetEntity(small.Id)!;
        Assert.Equal(1 / 48.0, small.Scale, 12);
        s.StartCommand("VPSCALE");
        s.SubmitKeyword("1:100");                                                     // as the ribbon menu sends it
        Assert.Equal(0.01, ((ViewportEntity)s.Document.GetEntity(small.Id)!).Scale, 12);
        s.StartCommand("VPFIT");
        Assert.True(((ViewportEntity)s.Document.GetEntity(small.Id)!).ModelBounds.Contains(new BoundingBox(0, 0, 480, 240)));

        // Corners resize the window without moving what is seen on the sheet; the middle moves it.
        var vp = new ViewportEntity(P(10, 10), 8, 6) { ModelCenter = P(100, 100), Scale = 0.1 };
        var before = vp.PaperToModel(P(8, 9));
        Assert.True(EntityGrips.Stretch(vp, 2, P(20, 15)));                           // the top right corner
        Assert.Equal(new BoundingBox(6, 7, 20, 15), vp.Frame);
        Assert.True(before.NearlyEquals(vp.PaperToModel(P(8, 9)), 1e-9));
        Assert.True(EntityGrips.Stretch(vp, 4, P(0, 0)));
        Assert.Equal(P(0, 0), vp.Center);
        Assert.True(vp.DistanceTo(P(0, 0)) > 3);                                      // the inside does not pick it

        Assert.Equal("1:50", LayoutStandards.ScaleName(0.02, new DrawingSettings { Unit = LinearUnit.Millimeter }));
        Assert.Equal("1/8\" = 1'-0\"", LayoutStandards.ScaleName(1 / 96.0, s.Document.Settings));
        Assert.Equal("1 1/2\" = 1'-0\"", LayoutStandards.ScaleName(1 / 8.0, s.Document.Settings));
        Assert.Equal("1:1", LayoutStandards.ScaleName(1, s.Document.Settings));
        Assert.True(LayoutStandards.TryParseScale("1:48", s.Document.Settings, out double a) && Math.Abs(a - 1 / 48.0) < 1e-12);
        Assert.True(LayoutStandards.TryParseScale("3/8\" = 1'-0\"", s.Document.Settings, out double b) && Math.Abs(b - 1 / 32.0) < 1e-12);
        Assert.False(LayoutStandards.TryParseScale("big", s.Document.Settings, out _));
    }

    [Fact]
    public void Inside_an_entered_viewport_the_wheel_and_pan_move_the_model_as_one_undo_step()
    {
        var s = Session();
        s.AddLayout();
        var before = (ViewportEntity)Viewport(s).Clone();
        Assert.False(s.EnterViewportAt(P(-50, -50)));                                 // not in a viewport
        Assert.True(s.EnterViewportAt(P(18, 12)));
        Assert.Equal(before.Id, s.ActiveViewportId);

        var anchor = P(10, 8);
        var under = before.PaperToModel(anchor);
        Assert.True(s.ZoomActiveViewport(anchor, 2));
        var vp = Viewport(s);
        Assert.Equal(before.Scale * 2, vp.Scale, 12);
        Assert.True(under.NearlyEquals(vp.PaperToModel(anchor), 1e-9));               // the model under the cursor stays there
        Assert.True(s.PanActiveViewport(new Vector2D(3, 0)));
        Assert.True(under.NearlyEquals(Viewport(s).PaperToModel(P(13, 8)), 1e-9));    // it moved 3 across the sheet
        Assert.Equal(before.Frame, Viewport(s).Frame);                                // the window itself did not move

        s.ExitViewport();
        Assert.Null(s.ActiveViewportId);
        s.Undo();                                                                     // one step for all of it
        Assert.Equal(before.Scale, Viewport(s).Scale);
        Assert.Equal(before.ModelCenter, Viewport(s).ModelCenter);
        Assert.False(s.ZoomActiveViewport(anchor, 2));                                // nothing entered
    }

    [Fact]
    public void A_viewport_draws_the_model_at_its_scale_cut_off_at_its_window()
    {
        var doc = new DrawingDocument();
        var line = AddLine(doc, 0, 0, 100, 0);                                        // in the model
        AddLine(doc, 5000, 5000, 5100, 5000);                                         // far outside what the viewport shows
        var layout = new Layout(Guid.NewGuid(), "Sheet", 20, 10);
        doc.AddLayout(layout);
        doc.AddEntity(new ViewportEntity(P(10, 5), 16, 8) { ModelCenter = P(50, 0), Scale = 0.1, SpaceId = layout.Id, LayerId = doc.CurrentLayerId });
        doc.ActiveSpaceId = layout.Id;

        var view = new ViewTransform();
        view.SetViewport(800, 400);
        view.SetView(P(10, 5), 40);                                                   // the sheet fills the 800 x 400 screen

        var surface = new RecordingSurface();
        new SceneRenderer().Render(surface, doc, view, new RenderOptions());
        Assert.Equal(2, surface.Rectangles.Count);                                    // the sheet and its edge, no grid
        Assert.Equal((80.0, 40.0, 640.0, 320.0), surface.Clips.Single());             // the window, in pixels
        Assert.Equal(0, surface.ClipDepth);
        // The model line: 100 long at 0.1 is 10 on the sheet, 400 pixels, centred in the window.
        var drawn = surface.Lines.Single(l => Math.Abs(l.Y1 - 200) < 1e-6 && Math.Abs(l.Y2 - 200) < 1e-6 && Math.Abs(Math.Abs(l.X2 - l.X1) - 400) < 1e-6);
        Assert.Equal(200.0, Math.Min(drawn.X1, drawn.X2), 6);
        Assert.Equal(5, surface.Lines.Count);                                         // that line and the four sides of the frame
        Assert.True(line.SpaceId == Guid.Empty);

        // In the model the viewport and the sheet are not there.
        doc.ActiveSpaceId = Guid.Empty;
        var model = new RecordingSurface();
        view.SetView(P(50, 0), 4);
        new SceneRenderer().Render(model, doc, view, new RenderOptions { ShowGrid = false });
        Assert.Empty(model.Clips);
        Assert.Single(model.Lines);
    }

    [Fact]
    public void Plot_leaves_out_layers_with_plot_off_and_draws_lineweights_in_black()
    {
        var doc = new DrawingDocument();
        var hidden = new Layer(Guid.NewGuid(), "Construction") { IsPlotted = false };
        doc.AddLayer(hidden);
        doc.Layers[0].LineweightMm = 0.5;
        AddLine(doc, 0, 0, 100, 0);
        doc.AddEntity(new LineEntity(P(0, 10), P(100, 10)) { LayerId = hidden.Id });
        doc.AddEntity(new LineEntity(P(0, 20), P(100, 20)) { LayerId = doc.CurrentLayerId, LineweightMm = 1.0, Color = CadColor.FromRgb(255, 0, 0) });

        var view = new ViewTransform();
        view.SetViewport(800, 600);
        view.SetView(P(50, 10), 4);
        var surface = new RecordingSurface();
        new SceneRenderer().Render(surface, doc, view, new RenderOptions { Plot = true, PixelsPerMm = 4 });
        Assert.Equal(2, surface.Lines.Count);                                         // no grid, no construction layer
        Assert.True(surface.Lines.All(l => l.Style.R == 0 && l.Style.G == 0 && l.Style.B == 0));
        Assert.Equal(new[] { 2.0, 4.0 }, surface.Lines.Select(l => l.Style.Width).OrderBy(w => w).ToArray());   // 0.5 mm and 1 mm

        var colour = new RecordingSurface();
        new SceneRenderer().Render(colour, doc, view, new RenderOptions { Plot = true, PlotMonochrome = false });
        Assert.Contains(colour.Lines, l => l.Style.R == 255 && l.Style.G == 0);       // red stays red
        Assert.Contains(colour.Lines, l => l.Style.R == 0 && l.Style.G == 0);         // white becomes black on paper
    }

    [Fact]
    public void Layouts_and_what_is_on_them_are_saved()
    {
        var s = Session();
        var layout = s.AddLayout("Plan")!;
        s.StartCommand("LINE");
        s.SubmitPoint(P(1, 1));
        s.SubmitPoint(P(35, 1));
        s.SetViewportScale(new[] { Viewport(s).Id }, 1 / 48.0);

        byte[] bytes = HcadSerializer.SaveToBytes(s.Document);
        string json = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"minReaderVersion\": 8", json);                             // older versions must not open it wrongly
        var loaded = HcadSerializer.LoadFromBytes(bytes);
        Assert.Empty(loaded.Warnings);
        var doc = loaded.Document;
        Assert.Equal(Guid.Empty, doc.ActiveSpaceId);                                  // a drawing opens on the Model tab
        Assert.Equal(2, doc.EntityCount);
        var back = doc.Layouts.Single();
        Assert.Equal(layout, back);
        doc.ActiveSpaceId = back.Id;
        Assert.Equal(2, doc.EntityCount);
        var vp = doc.Entities.OfType<ViewportEntity>().Single();
        Assert.Equal(1 / 48.0, vp.Scale);
        Assert.Equal(Viewport(s).ModelCenter, vp.ModelCenter);
        Assert.Equal(Viewport(s).Frame, vp.Frame);
        Assert.False(doc.FindLayer("Viewport")!.IsPlotted);

        // A drawing without layouts can still be opened by older versions.
        var plain = new DrawingDocument();
        AddLine(plain, 0, 0, 1, 1);
        Assert.Contains("\"minReaderVersion\": 1", System.Text.Encoding.UTF8.GetString(HcadSerializer.SaveToBytes(plain)));
    }

    [Fact]
    public void A_layout_plots_at_true_size_and_the_model_is_fitted_to_the_page()
    {
        var s = Session();
        s.AddLayout();                                                                // 36 x 24 inches
        var plan = PlotPlanner.Plan(s.Document, 36 * 96, 24 * 96);
        Assert.True(plan.ActualSize);
        Assert.Equal(96.0, plan.View.Scale, 9);                                       // one inch of sheet is one inch of page
        Assert.Equal(1.0, plan.Shrink, 9);
        var corner = plan.View.WorldToScreen(P(0, 24));
        Assert.Equal((0.0, 0.0), (Math.Round(corner.X, 6), Math.Round(corner.Y, 6))); // the sheet covers the page exactly

        var small = PlotPlanner.Plan(s.Document, 11 * 96, 8.5 * 96);                  // on Letter it has to shrink
        Assert.False(small.ActualSize);
        Assert.Equal(11.0 / 36.0, small.Shrink, 9);
        Assert.Equal(96 / 25.4 * 11 / 36, small.PixelsPerMm, 9);                      // lineweights shrink with it
        Assert.True(PlotPlanner.PrefersLandscape(s.Document));

        var large = PlotPlanner.Plan(s.Document, 48 * 96, 36 * 96);                   // a bigger page: still true size, centred
        Assert.True(large.ActualSize);
        Assert.Equal(6 * 96.0, large.View.WorldToScreen(P(0, 24)).X, 6);

        s.SwitchSpace(Guid.Empty);
        var model = PlotPlanner.Plan(s.Document, 1056, 816);
        Assert.False(model.ActualSize);
        var a = model.View.WorldToScreen(P(0, 0));
        var b = model.View.WorldToScreen(P(480, 240));
        Assert.True(a.X > 0 && b.X < 1056 && b.Y > 0 && a.Y < 816);                   // all of it is on the page
        Assert.True(b.X - a.X > 0.9 * 1056);                                          // and it fills the width
    }

    [Fact]
    public void A_scale_chosen_first_is_applied_to_the_viewport_clicked_after_and_escape_leaves_a_viewport()
    {
        var s = Session();
        s.AddLayout();
        s.StartCommand("VPSCALE");                                                    // one viewport on the sheet: no click needed
        s.SubmitKeyword("1:96");
        Assert.Null(s.ActiveCommand);
        Assert.Equal(1 / 96.0, Viewport(s).Scale, 12);

        s.StartCommand("VIEWPORT");
        s.SubmitPoint(P(2, 2));
        s.SubmitPoint(P(12, 8));
        s.StartCommand("VPSCALE");                                                    // now there are two: scale, then which one
        s.SubmitKeyword("1:24");
        Assert.NotNull(s.ActiveCommand);
        s.SubmitPoint(P(7, 2));
        Assert.Null(s.ActiveCommand);
        Assert.Equal(1 / 24.0, s.Document.Entities.OfType<ViewportEntity>().Single(v => v.Width == 10).Scale, 12);

        Assert.True(s.EnterViewportAt(P(7, 5)));                                      // the small one, though it lies inside the big one
        Assert.Equal(10.0, ((ViewportEntity)s.Document.GetEntity(s.ActiveViewportId!.Value)!).Width);
        s.Cancel();
        Assert.Null(s.ActiveViewportId);
        Assert.True(s.EnterViewportAt(P(7, 5)));
        s.SwitchSpace(Guid.Empty);                                                    // changing tab leaves it too
        Assert.Null(s.ActiveViewportId);
    }

    [Fact]
    public void An_object_on_a_layout_that_is_not_in_the_file_is_kept_but_not_shown()
    {
        var s = Session();
        var layout = s.AddLayout("Plan")!;
        string json = System.Text.Encoding.UTF8.GetString(HcadSerializer.SaveToBytes(s.Document));
        json = json.Replace(layout.Id.ToString(), Guid.NewGuid().ToString());         // every "space" now points nowhere...
        int at = json.IndexOf("\"layouts\"", StringComparison.Ordinal);
        Assert.True(at > 0);
        var loaded = HcadSerializer.LoadFromBytes(System.Text.Encoding.UTF8.GetBytes(json.Replace("\"name\": \"Plan\"", "\"name\": \"Model\"")));   // ...and the layout itself is unreadable
        Assert.Empty(loaded.Document.Layouts);
        Assert.Equal(2, loaded.Document.AllEntities.Count);                           // the model's two lines
        Assert.Single(loaded.Document.UnknownEntities);                               // the viewport is kept in the file
        Assert.NotEmpty(loaded.Warnings);
    }
}

public class UnitTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Changing_the_unit_can_keep_everything_its_real_size()
    {
        var s = new EditorSession();
        AddLine(s.Document, 0, 0, 120, 0);                                            // a 10 ft wall
        s.Document.AddEntity(new CircleEntity(P(10, 10), 2) { LayerId = s.Document.CurrentLayerId });
        s.SelectAt(P(10, 12), 0.5);
        s.CreateBlock("Bolt", P(10, 10), s.GetSelectedEntities());
        s.AddLayout();                                                                // 36 x 24 in with a viewport
        var viewport = s.Document.Entities.OfType<ViewportEntity>().Single();
        double viewScale = viewport.Scale;
        var shown = viewport.ModelBounds;
        s.SwitchSpace(Guid.Empty);

        s.StartCommand("UNITS");
        s.SubmitEnter();
        s.SubmitText("MM");
        s.SubmitEnter();                                                              // Size is the default
        s.SubmitEnter();
        Assert.Null(s.ActiveCommand);

        var doc = s.Document;
        Assert.Equal((LinearUnit.Millimeter, LengthFormat.Decimal, 1), (doc.Settings.Unit, doc.Settings.Format, doc.Settings.DecimalPrecision));
        var line = doc.Entities.OfType<LineEntity>().Single();
        Assert.Equal(3048.0, line.End.X, 9);                                          // 120 in
        Assert.Equal("3048.0", UnitFormatter.FormatLength(line.End.X, doc.Settings));
        var bolt = doc.Entities.OfType<BlockReferenceEntity>().Single();
        Assert.Equal((254.0, 1.0), (Math.Round(bolt.Position.X, 9), bolt.Scale));     // moved, not scaled twice
        Assert.Equal(4 * 25.4, bolt.Bounds.Width, 9);
        Assert.Equal(25.4, doc.Settings.LinetypeScale, 12);
        Assert.Equal(4 * 25.4, doc.CurrentDimensionStyle.TextHeight, 9);
        var layout = doc.Layouts.Single();
        Assert.Equal((914.4, 609.6), (Math.Round(layout.PaperWidth, 9), Math.Round(layout.PaperHeight, 9)));
        var vp = doc.AllEntities.OfType<ViewportEntity>().Single();
        Assert.Equal(viewScale, vp.Scale, 12);                                        // the sheet still shows the model at the same scale
        Assert.Equal(shown.MinX * 25.4, vp.ModelBounds.MinX, 6);
        Assert.Equal(shown.MaxY * 25.4, vp.ModelBounds.MaxY, 6);

        s.Undo();                                                                     // one step back to inches
        Assert.Equal(LinearUnit.Inch, s.Document.Settings.Unit);
        Assert.Equal(120.0, s.Document.Entities.OfType<LineEntity>().Single().End.X);
        Assert.Equal(4.0, s.Document.Entities.OfType<BlockReferenceEntity>().Single().Bounds.Width, 9);
        Assert.Equal(36.0, s.Document.Layouts.Single().PaperWidth);
        Assert.Equal(4.0, s.Document.CurrentDimensionStyle.TextHeight);
    }

    [Fact]
    public void Lengths_can_be_typed_with_a_metric_unit_in_any_drawing()
    {
        var inch = new DrawingSettings();
        Assert.True(UnitFormatter.TryParseLength("254mm", inch, out double a) && Math.Abs(a - 10) < 1e-9);
        Assert.True(UnitFormatter.TryParseLength("2.54 cm", inch, out double b) && Math.Abs(b - 1) < 1e-9);
        Assert.True(UnitFormatter.TryParseLength("1M", inch, out double c) && Math.Abs(c - 39.37007874015748) < 1e-9);
        var mm = new DrawingSettings { Unit = LinearUnit.Millimeter };
        Assert.True(UnitFormatter.TryParseLength("2.5m", mm, out double d) && d == 2500);
        Assert.True(UnitFormatter.TryParseLength("1'", mm, out double e) && Math.Abs(e - 304.8) < 1e-9);
        Assert.True(UnitFormatter.TryParseLength("300", mm, out double f) && f == 300);
        Assert.False(UnitFormatter.TryParseLength("mm", mm, out _));
        Assert.False(UnitFormatter.TryParseLength("tenmm", mm, out _));
    }

    [Fact]
    public void New_drawings_start_in_inches_until_the_user_chooses_another_unit_and_that_is_remembered()
    {
        string path = _temp.File("settings.json");
        var vm = new MainViewModel(new FakeUi(), new RecoveryStore(_temp.File("r1")), new UserSettingsStore(path));
        Assert.Equal((LinearUnit.Inch, "in"), (vm.Session.Document.Settings.Unit, vm.UnitLabel));

        vm.Session.StartCommand("NEWUNITS");
        Assert.Contains("<Inches>", vm.Session.Prompt);
        vm.Session.SubmitText("MM");
        Assert.Equal(LinearUnit.Inch, vm.Session.Document.Settings.Unit);            // the open drawing is not touched
        Assert.True(vm.NewDocument());
        var doc = vm.Session.Document;
        Assert.Equal((LinearUnit.Millimeter, LengthFormat.Decimal, 100.0), (doc.Settings.Unit, doc.Settings.Format, doc.Settings.GridSpacing));
        Assert.Equal(100.0, doc.CurrentDimensionStyle.TextHeight, 9);                 // a round size, not 101.6
        Assert.Equal(25.0, doc.Settings.LinetypeScale, 9);
        Assert.False(doc.IsDirty);
        Assert.Equal("mm", vm.UnitLabel);

        var again = new MainViewModel(new FakeUi(), new RecoveryStore(_temp.File("r2")), new UserSettingsStore(path));
        Assert.Equal(LinearUnit.Millimeter, again.Session.Document.Settings.Unit);    // remembered for the next start
        Assert.Equal(LinearUnit.Millimeter, again.Session.NewDrawingUnit);

        // UNITS in an empty drawing does not ask about objects, and sets round sizes too.
        var s = new EditorSession();
        s.StartCommand("UNITS");
        s.SubmitEnter();
        s.SubmitText("Meters");
        Assert.Contains("decimal places", s.Prompt);
        s.SubmitEnter();
        Assert.Equal((LinearUnit.Meter, 3), (s.Document.Settings.Unit, s.Document.Settings.DecimalPrecision));
        Assert.Equal(0.1, s.Document.CurrentDimensionStyle.TextHeight, 9);
    }
}
