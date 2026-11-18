using HAQQ.CAD.Commands;
using HAQQ.CAD.Core;
using HAQQ.CAD.Documents;
using HAQQ.CAD.Documents.Pdf;
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
        var layout = s.AddLayout(template: LayoutTemplate.Blank)!;
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
        var a = s.AddLayout(template: LayoutTemplate.Blank)!;
        s.StartCommand("LINE");
        s.SubmitPoint(P(4, 4));
        s.SubmitPoint(P(30, 4));
        var onSheet = s.Document.Entities.OfType<LineEntity>().Single();
        Assert.Equal(a.Id, onSheet.SpaceId);

        var b = s.AddLayout("Details", template: LayoutTemplate.Blank)!;
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
        var layout = s.AddLayout(template: LayoutTemplate.Blank)!;
        Assert.True(s.RenameLayout(layout.Id, "Plan"));
        Assert.Equal("Plan", s.Document.ActiveLayout!.Name);
        Assert.False(s.RenameLayout(layout.Id, "Model"));
        Assert.Null(s.AddLayout("plan", template: LayoutTemplate.Blank));                                             // the name is taken

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
        s.AddLayout(template: LayoutTemplate.Blank);
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
        s.AddLayout(template: LayoutTemplate.Blank);
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
        Assert.Equal(3, surface.Rectangles.Count);                                    // the grey desk, the white sheet and its edge; no grid
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
        var layout = s.AddLayout("Plan", template: LayoutTemplate.Blank)!;
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
        var back = doc.Layouts.Single();
        Assert.Equal(layout, back);
        Assert.Equal(back.Id, doc.ActiveSpaceId);                                     // a drawing opens on the tab it was saved on
        Assert.False(doc.IsDirty);
        doc.ActiveSpaceId = Guid.Empty;
        Assert.Equal(2, doc.EntityCount);
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
        s.AddLayout(template: LayoutTemplate.Blank);                                                                // 36 x 24 inches
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
        s.AddLayout(template: LayoutTemplate.Blank);
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
        var layout = s.AddLayout("Plan", template: LayoutTemplate.Blank)!;
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

public class LayoutTabTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Tabs_follow_the_layouts_and_a_layout_is_renamed_in_its_tab()
    {
        var vm = new MainViewModel(new FakeUi(), new RecoveryStore(_temp.File("recovery")));
        Assert.Equal(new[] { "Model" }, vm.SpaceTabs.Select(t => t.Name).ToArray());
        Assert.True(vm.SpaceTabs[0].IsActive);

        vm.RunCommand("LAYOUT");                                                      // the + beside the tabs
        vm.RunCommand("LAYOUT");
        vm.RunCommand("LAYOUT");
        Assert.Equal(new[] { "Model", "Layout 1", "Layout 2", "Layout 3" }, vm.SpaceTabs.Select(t => t.Name).ToArray());
        Assert.True(vm.SpaceTabs[3].IsActive && vm.IsLayoutActive);                   // a new layout is opened

        vm.SelectSpaceCommand.Execute(vm.SpaceTabs[1].Id);
        Assert.True(vm.SpaceTabs[1].IsActive && !vm.SpaceTabs[3].IsActive);

        Assert.False(vm.BeginRenameSpace(vm.SpaceTabs[0]));                           // Model keeps its name
        var tab = vm.SpaceTabs[2];
        Assert.True(vm.BeginRenameSpace(tab));
        Assert.Equal((true, "Layout 2"), (tab.IsRenaming, tab.EditName));
        tab.EditName = "  S-101 Foundation Plan ";
        Assert.True(vm.EndRenameSpace(tab, keep: true));
        Assert.Equal("S-101 Foundation Plan", vm.SpaceTabs[2].Name);
        Assert.False(vm.SpaceTabs[2].IsRenaming);
        Assert.True(vm.SpaceTabs[1].IsActive);                                        // renaming does not change which tab is open

        vm.BeginRenameSpace(vm.SpaceTabs[3]);                                         // a name that is taken, then Esc
        vm.SpaceTabs[3].EditName = "s-101 foundation plan";
        Assert.False(vm.EndRenameSpace(vm.SpaceTabs[3], keep: true));
        Assert.Equal("Layout 3", vm.SpaceTabs[3].Name);
        vm.BeginRenameSpace(vm.SpaceTabs[3]);
        vm.SpaceTabs[3].EditName = "Something else";
        Assert.False(vm.EndRenameSpace(vm.SpaceTabs[3], keep: false));
        Assert.Equal("Layout 3", vm.SpaceTabs[3].Name);

        vm.Session.Undo();                                                            // the rename is an undo step
        Assert.Equal("Layout 2", vm.SpaceTabs[2].Name);
    }
}

public class SheetTests
{
    private static EditorSession Session()
    {
        var s = new EditorSession();
        s.Document.Settings.Format = LengthFormat.Decimal;
        AddLine(s.Document, 0, 0, 480, 0);
        AddLine(s.Document, 480, 0, 480, 240);
        return s;
    }

    [Fact]
    public void A_new_layout_has_a_border_a_title_block_and_a_titled_viewport()
    {
        var s = Session();
        var layout = s.AddLayout("S-101 Foundation")!;
        var doc = s.Document;
        Assert.Equal(3, doc.EntityCount);

        var border = doc.Entities.OfType<PolylineEntity>().Single();                  // half an inch in on an Arch D sheet
        Assert.Equal(new BoundingBox(0.5, 0.5, 35.5, 23.5), border.Bounds);
        Assert.Equal("Sheet", doc.GetLayer(border.LayerId)!.Name);
        Assert.True(doc.GetLayer(border.LayerId)!.IsPlotted);

        var block = doc.Entities.OfType<TableEntity>().Single();                      // in the lower-right corner of the border
        Assert.Equal((35.5, 0.5), (Math.Round(block.Bounds.MaxX, 9), Math.Round(block.Bounds.MinY, 9)));
        Assert.Equal(("COMPANY NAME", "S-101 FOUNDATION", "PROJECT TITLE", "S-101"), (block.Title, block.GetCell(0, 0), block.GetCell(1, 0), block.GetCell(1, 1)));

        var viewport = doc.Entities.OfType<ViewportEntity>().Single();
        Assert.Equal(("S-101 FOUNDATION", 0.25), (viewport.Title, viewport.TitleHeight));
        Assert.True(viewport.Frame.MinY > block.Bounds.MaxY);                         // above the title block
        Assert.True(border.Bounds.Contains(viewport.Frame));
        Assert.True(viewport.ModelBounds.Contains(new BoundingBox(0, 0, 480, 240)));

        // Border only, and a bare sheet.
        s.AddLayout("B", template: LayoutTemplate.Border);
        Assert.Equal(2, doc.EntityCount);
        Assert.Empty(doc.Entities.OfType<TableEntity>());
        s.AddLayout("C", 17, 11, LayoutTemplate.Blank, withViewport: false);
        Assert.Equal(0, doc.EntityCount);
        Assert.Equal((17.0, 11.0), (doc.ActiveLayout!.PaperWidth, doc.ActiveLayout.PaperHeight));
        s.AddLayout("Plan");                                                          // no sheet number in the name: its place in the row
        Assert.Equal("4", doc.Entities.OfType<TableEntity>().Single().GetCell(1, 1));

        s.Undo(); s.Undo(); s.Undo(); s.Undo();
        Assert.Empty(doc.Layouts);
        Assert.Null(doc.FindLayer("Sheet"));
        Assert.Equal(2, doc.AllEntities.Count);
        _ = layout;
    }

    [Fact]
    public void The_title_under_a_viewport_shows_its_scale_as_it_is_now_and_plots_only_when_its_layer_does()
    {
        var s = Session();
        s.AddLayout("Plan");
        var viewport = s.Document.Entities.OfType<ViewportEntity>().Single();
        var view = new ViewTransform();
        view.SetViewport(1440, 960);
        view.SetView(P(18, 12), 40);

        string[] Texts(bool plot)
        {
            var surface = new RecordingSurface();
            new SceneRenderer().Render(surface, s.Document, view, new RenderOptions { Plot = plot });
            return surface.StyledTexts.Select(t => t.Text).ToArray();
        }
        Assert.Contains("PLAN", Texts(false));
        Assert.Contains("SCALE: 3/4\" = 1'-0\"", Texts(false));
        Assert.Contains("3/4\" = 1'-0\"", Texts(false));                              // the tag inside the frame, on screen
        s.SetViewportScale(new[] { viewport.Id }, 1 / 48.0);
        Assert.Contains("SCALE: 1/4\" = 1'-0\"", Texts(false));                       // follows the viewport
        var printed = Texts(true);
                                                                                      // the title is on the viewport's layer,
        Assert.DoesNotContain("SCALE: 1/4\" = 1'-0\"", printed);                      // which does not plot: neither frame nor title prints
        Assert.DoesNotContain("1/4\" = 1'-0\"", printed);                             // nor the screen tag

        s.Document.GetLayer(viewport.LayerId)!.IsPlotted = true;                      // plot switched on for that layer: both print
        printed = Texts(true);
        Assert.Contains("SCALE: 1/4\" = 1'-0\"", printed);
        Assert.DoesNotContain("1/4\" = 1'-0\"", printed);
        s.Document.GetLayer(viewport.LayerId)!.IsPlotted = false;

        // On the white paper, white lines are drawn dark.
        var screen = new RecordingSurface();
        new SceneRenderer().Render(screen, s.Document, view, new RenderOptions());
        Assert.Equal((255, 255, 255), (screen.Rectangles[1].Style.R, screen.Rectangles[1].Style.G, screen.Rectangles[1].Style.B));
        Assert.True(screen.Lines.All(l => l.Style.R < 200 || l.Style.G < 200 || l.Style.B < 200));
    }

    [Fact]
    public void Layouts_are_duplicated_and_moved_and_viewports_locked()
    {
        var s = Session();
        var a = s.AddLayout("A")!;
        s.StartCommand("LINE");
        s.SubmitPoint(P(2, 2));
        s.SubmitPoint(P(9, 2));
        s.AddLayout("B");
        int modelCount = 2;

        var copy = s.DuplicateLayout(a.Id)!;
        Assert.Equal("A (2)", copy.Name);
        Assert.Equal(new[] { "A", "A (2)", "B" }, s.Document.Layouts.Select(l => l.Name).ToArray());   // beside the one it copies
        Assert.Equal(copy.Id, s.ActiveSpaceId);
        Assert.Equal(4, s.Document.EntityCount);                                      // border, title block, viewport and the line
        Assert.Equal((36.0, 24.0), (copy.PaperWidth, copy.PaperHeight));
        s.SwitchSpace(Guid.Empty);
        Assert.Equal(modelCount, s.Document.EntityCount);                             // the model was not copied

        Assert.True(s.MoveLayout(copy.Id, +1));
        Assert.Equal(new[] { "A", "B", "A (2)" }, s.Document.Layouts.Select(l => l.Name).ToArray());
        Assert.False(s.MoveLayout(copy.Id, +1));                                      // already last
        s.Undo();
        Assert.Equal(new[] { "A", "A (2)", "B" }, s.Document.Layouts.Select(l => l.Name).ToArray());
        s.Undo();                                                                     // the duplicate
        Assert.Equal(new[] { "A", "B" }, s.Document.Layouts.Select(l => l.Name).ToArray());

        // Locking: the view cannot be changed until it is unlocked; the frame can still be resized.
        s.SwitchSpace(a.Id);
        var viewport = s.Document.Entities.OfType<ViewportEntity>().Single();
        s.StartCommand("VPLOCK");                                                     // the only viewport on the sheet
        Assert.True(((ViewportEntity)s.Document.GetEntity(viewport.Id)!).Locked);
        Assert.False(s.SetViewportScale(new[] { viewport.Id }, 1 / 96.0));
        Assert.False(s.FitViewports(new[] { viewport.Id }));
        Assert.True(s.EnterViewportAt(viewport.Center));
        Assert.False(s.ZoomActiveViewport(viewport.Center, 2));
        Assert.False(s.PanActiveViewport(new Vector2D(1, 0)));
        s.ExitViewport();
        Assert.Equal(viewport.Scale, ((ViewportEntity)s.Document.GetEntity(viewport.Id)!).Scale);
        Assert.True(s.ModifyEntities(new[] { viewport.Id }, e => EntityGrips.Stretch(e, 2, P(30, 20)), "Stretch"));

        var loaded = HcadSerializer.LoadFromBytes(HcadSerializer.SaveToBytes(s.Document)).Document;
        var back = (ViewportEntity)loaded.GetEntity(viewport.Id)!;
        Assert.True(back.Locked);
        Assert.Equal(("A", 0.25), (back.Title, back.TitleHeight));
        s.StartCommand("VPLOCK");
        Assert.False(((ViewportEntity)s.Document.GetEntity(viewport.Id)!).Locked);
        Assert.True(s.SetViewportScale(new[] { viewport.Id }, 1 / 96.0));
    }

    [Fact]
    public void The_scale_and_paper_lists_are_the_standard_ones()
    {
        var inch = new DrawingSettings();
        var names = LayoutStandards.Scales(inch).Select(x => LayoutStandards.ScaleName(x, inch)).ToArray();
        Assert.Equal(new[] { "1:1", "3\" = 1'-0\"", "1 1/2\" = 1'-0\"", "1\" = 1'-0\"", "3/4\" = 1'-0\"", "1/2\" = 1'-0\"", "3/8\" = 1'-0\"",
            "1/4\" = 1'-0\"", "3/16\" = 1'-0\"", "1/8\" = 1'-0\"", "3/32\" = 1'-0\"", "1/16\" = 1'-0\"", "1/32\" = 1'-0\"" }, names);
        var mm = new DrawingSettings { Unit = LinearUnit.Millimeter };
        Assert.Equal("1:1, 1:2, 1:5, 1:10, 1:20, 1:25, 1:50, 1:75, 1:100, 1:200, 1:500, 1:1000", string.Join(", ", LayoutStandards.Scales(mm).Select(x => LayoutStandards.ScaleName(x, mm))));
        Assert.True(LayoutStandards.TryParseScale("1\" = 15'-0\"", inch, out double custom) && Math.Abs(custom - 1 / 180.0) < 1e-12);

        Assert.Equal(16, LayoutStandards.Papers.Count);
        Assert.Equal((36.0, 24.0), (LayoutStandards.FindPaper("ARCH D")!.WidthInches, LayoutStandards.FindPaper("arch-d")!.HeightInches));
        Assert.Equal("ANSIA", LayoutStandards.FindPaper("Letter")!.Key);
        Assert.Equal((841.0, 594.0), LayoutStandards.SizeOf(LayoutStandards.FindPaper("A1")!, mm));
        Assert.Null(LayoutStandards.FindPaper("A9"));
    }
}

public class PdfExportTests
{
    [Fact]
    public void Layouts_are_exported_as_vector_pages_at_their_true_size_and_scale()
    {
        var s = new EditorSession();
        s.Document.Settings.Format = LengthFormat.Decimal;
        AddLine(s.Document, 0, 0, 480, 0);                                            // 40 ft
        s.Document.AddEntity(new CircleEntity(P(240, 120), 48) { LayerId = s.Document.CurrentLayerId });
        var hidden = new Layer(Guid.NewGuid(), "Construction") { IsPlotted = false };
        s.Document.AddLayer(hidden);
        s.Document.AddEntity(new LineEntity(P(0, 240), P(480, 240)) { LayerId = hidden.Id });

        var first = s.AddLayout("S-101 Plan")!;                                       // Arch D, viewport at 3/4" = 1'-0"
        var viewport = s.Document.Entities.OfType<ViewportEntity>().Single();
        s.SetViewportScale(new[] { viewport.Id }, 1 / 48.0);
        viewport = (ViewportEntity)s.Document.GetEntity(viewport.Id)!;
        var second = s.AddLayout("Detail", 17, 11)!;
        s.SwitchSpace(Guid.Empty);

        byte[] bytes = PdfExporter.Export(s.Document, new[] { first.Id, second.Id }, monochrome: true);
        Assert.Equal(Guid.Empty, s.Document.ActiveSpaceId);                           // the open tab is put back
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "haqq-export-test.pdf"), bytes);

        // Read back with HAQQ CAD's own PDF reader.
        var pdf = PdfFile.Open(bytes);
        Assert.Equal(2, pdf.PageCount);
        var page = pdf.ReadPage(0);
        Assert.Equal((36 * 72.0, 24 * 72.0), (page.Width, page.Height));              // the sheet itself, in points
        Assert.Equal((17 * 72.0, 11 * 72.0), (pdf.ReadPage(1).Width, pdf.ReadPage(1).Height));
        Assert.True(page.Paths.All(p => p.R == 0 && p.G == 0 && p.B == 0));           // black only
        Assert.DoesNotContain(page.Texts, t => t.Text.StartsWith("SCALE:"));           // the view title follows the viewport's layer, which does not plot
        Assert.Contains(page.Texts, t => t.Text == "COMPANY NAME");
        Assert.Contains(page.Texts, t => t.Text == "S-101");

        // The 40 ft line is 10 inches on paper at 1/4" = 1'-0": 720 points, where the viewport shows it.
        var copy = DrawingCopy.Convert(page, new CopyOptions { UnitsPerPoint = 1.0 / 72.0 });
        var line = copy.Included.OfType<LineEntity>().Single(l => Math.Abs(l.Start.DistanceTo(l.End) - 10.0) < 1e-3);
        var expected = viewport.ModelToPaper(P(0, 0));
        Assert.True(Math.Abs(Math.Min(line.Start.X, line.End.X) - expected.X) < 1e-3 && Math.Abs(line.Start.Y - expected.Y) < 1e-3);
        var circle = copy.Included.OfType<CircleEntity>().Single();                   // still a circle, 1 inch radius on paper
        Assert.Equal(1.0, circle.Radius, 2);
        // The layer that does not plot is not there: no second 10 inch line; nor is the viewport's frame.
        Assert.Single(copy.Included.OfType<LineEntity>().Where(l => Math.Abs(l.Start.DistanceTo(l.End) - 10.0) < 1e-3));
        Assert.DoesNotContain(copy.Included, e => e is PolylineEntity p && Math.Abs(p.Bounds.Width - viewport.Width) < 1e-3);

        // In colour, and the model on a page of its own.
        s.Document.AddEntity(new LineEntity(P(0, 50), P(100, 50)) { LayerId = s.Document.CurrentLayerId, Color = CadColor.FromRgb(255, 0, 0) });
        var colour = PdfFile.Open(PdfExporter.Export(s.Document, new[] { Guid.Empty }, monochrome: false)).ReadPage(0);
        Assert.Equal((792.0, 612.0), (colour.Width, colour.Height));                  // Letter, landscape
        Assert.Contains(colour.Paths, p => p.R == 255 && p.G == 0 && p.B == 0);
        Assert.Throws<InvalidOperationException>(() => PdfExporter.Export(s.Document, Array.Empty<Guid>(), true));
    }
}

public class LayoutWindowTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    public void Dispose() => _temp.Dispose();

    [Fact]
    public void New_layout_window_makes_the_sheet_that_was_chosen()
    {
        var s = new EditorSession();
        s.Document.Settings.Format = LengthFormat.Decimal;
        string? asked = null;
        s.ViewCommandRequested += name => asked = name;
        s.StartCommand("NEWLAYOUT");
        Assert.Equal("NewLayout", asked);

        var vm = new NewLayoutViewModel(s);
        Assert.Equal(("Layout 1", "ARCH D - 36 x 24 in", true), (vm.Name, vm.Paper, vm.Landscape));
        Assert.Equal(("36.0000", "24.0000", false), (vm.WidthText, vm.HeightText, vm.IsCustom));
        vm.Paper = "ANSI B (Tabloid) - 17 x 11 in";
        vm.Portrait = true;
        Assert.Equal(("11.0000", "17.0000"), (vm.WidthText, vm.HeightText));
        vm.Name = "Model";
        Assert.False(vm.Create());
        vm.Name = "S-201 Details";
        vm.Template = vm.Templates[1];                                                // border only
        vm.WithViewport = false;
        Assert.True(vm.Create());
        var layout = s.Document.ActiveLayout!;
        Assert.Equal(("S-201 Details", 11.0, 17.0), (layout.Name, layout.PaperWidth, layout.PaperHeight));
        Assert.Single(s.Document.Entities);                                           // the border and nothing else

        var custom = new NewLayoutViewModel(s) { Paper = NewLayoutViewModel.CustomPaper };
        Assert.Equal("Layout 1", custom.Name);
        custom.WidthText = "20";
        custom.HeightText = "30";                                                     // landscape is ticked: the long side goes across
        Assert.True(custom.Create());
        Assert.Equal((30.0, 20.0), (s.Document.ActiveLayout!.PaperWidth, s.Document.ActiveLayout.PaperHeight));
        custom = new NewLayoutViewModel(s) { Paper = NewLayoutViewModel.CustomPaper, WidthText = "wide", HeightText = "10" };
        Assert.False(custom.Create());
        Assert.Contains("width and height", custom.Message);
    }

    [Fact]
    public void Tab_menu_duplicates_moves_and_deletes_after_asking_and_the_bar_says_where_you_are()
    {
        var ui = new FakeUi();
        var vm = new MainViewModel(ui, new RecoveryStore(_temp.File("recovery")));
        Assert.Equal(("MODEL", ""), (vm.SpaceLabel, vm.SheetStatus));
        vm.RunCommand("LAYOUT");
        vm.RunCommand("LAYOUT");
        Assert.Equal("PAPER", vm.SpaceLabel);
        Assert.True(vm.SheetStatus.StartsWith("3'-0\" x 2'-0\""), vm.SheetStatus);

        Assert.True(vm.DuplicateSpace(vm.SpaceTabs[1]));
        Assert.Equal(new[] { "Model", "Layout 1", "Layout 1 (2)", "Layout 2" }, vm.SpaceTabs.Select(t => t.Name).ToArray());
        Assert.True(vm.SpaceTabs[2].IsActive);
        Assert.True(vm.MoveSpace(vm.SpaceTabs[2], -1));
        Assert.Equal(new[] { "Model", "Layout 1 (2)", "Layout 1", "Layout 2" }, vm.SpaceTabs.Select(t => t.Name).ToArray());
        Assert.False(vm.MoveSpace(vm.SpaceTabs[0], +1));                              // Model stays first
        Assert.False(vm.DuplicateSpace(vm.SpaceTabs[0]));

        ui.ConfirmAnswers.Enqueue(false);                                             // "No" keeps the layout
        Assert.False(vm.DeleteSpace(vm.SpaceTabs[3]));
        Assert.Equal(4, vm.SpaceTabs.Count);
        ui.ConfirmAnswers.Enqueue(true);
        Assert.True(vm.DeleteSpace(vm.SpaceTabs[3]));
        Assert.Equal(new[] { "Model", "Layout 1 (2)", "Layout 1" }, vm.SpaceTabs.Select(t => t.Name).ToArray());

        // Inside a viewport, and with it locked.
        var viewport = vm.Session.Document.Entities.OfType<ViewportEntity>().Single();
        Assert.True(vm.Session.EnterViewportAt(viewport.Center));
        Assert.Equal("PAPER - IN VIEWPORT", vm.SpaceLabel);
        Assert.Contains("Viewport 1/4\" = 1'-0\"", vm.SheetStatus);
        vm.Session.ExitViewport();
        vm.Session.SetViewportLock(new[] { viewport.Id }, true);
        vm.Session.SelectAt(new Point2D(viewport.Frame.MinX, viewport.Center.Y), 0.05);
        Assert.True(vm.SheetStatus.EndsWith(", locked"), vm.SheetStatus);
    }

    [Fact]
    public void Export_window_writes_all_sheets_in_one_file_or_a_file_for_each()
    {
        var s = new EditorSession();
        AddLine(s.Document, 0, 0, 100, 0);
        var none = new PdfExportViewModel(s);                                         // no layouts: the model is offered
        Assert.True(none.Sheets.Single().Include);
        Assert.Equal(1, PdfFile.Open(none.Build()!.Single().Pdf).PageCount);

        s.AddLayout("S-101 Plan");
        s.AddLayout("S-102 Sections", 17, 11);
        var vm = new PdfExportViewModel(s);
        Assert.Equal(new[] { (false, "Model"), (true, "S-101 Plan"), (true, "S-102 Sections") }, vm.Sheets.Select(x => (x.Include, x.Name)).ToArray());
        var one = vm.Build()!;
        Assert.Equal("", one.Single().SheetName);
        Assert.Equal(2, PdfFile.Open(one.Single().Pdf).PageCount);

        vm.Separate = true;
        var each = vm.Build()!;
        Assert.Equal(new[] { "S-101 Plan", "S-102 Sections" }, each.Select(f => f.SheetName).ToArray());
        Assert.Equal((17 * 72.0, 11 * 72.0), (PdfFile.Open(each[1].Pdf).ReadPage(0).Width, PdfFile.Open(each[1].Pdf).ReadPage(0).Height));
        Assert.Equal(@"C:\Jobs\Tower - S-101 Plan.pdf", PdfExportViewModel.FileNameFor(@"C:\Jobs\Tower.pdf", "S-101 Plan"));
        Assert.Equal(@"C:\Jobs\Tower.pdf", PdfExportViewModel.FileNameFor(@"C:\Jobs\Tower.pdf", ""));

        foreach (var sheet in vm.Sheets) sheet.Include = false;
        Assert.Null(vm.Build());
        Assert.Contains("Tick", vm.Message);
    }
}

public class CompanyNameTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Title_blocks_carry_the_users_own_company_which_is_remembered()
    {
        string path = _temp.File("settings.json");
        var vm = new MainViewModel(new FakeUi(), new RecoveryStore(_temp.File("r1")), new UserSettingsStore(path));
        var s = vm.Session;
        Assert.Equal("", s.CompanyName);

        var window = new NewLayoutViewModel(s);
        Assert.Equal("", window.Company);
        window.Company = "  Acme Structural Engineers ";
        Assert.True(window.Create());
        Assert.Equal("Acme Structural Engineers", s.Document.Entities.OfType<TableEntity>().Single().Title);

        s.StartCommand("LAYOUT");                                                     // the quick way uses it too
        Assert.Equal("Acme Structural Engineers", s.Document.Entities.OfType<TableEntity>().Single().Title);

        s.StartCommand("COMPANY");
        Assert.Contains("<Acme Structural Engineers>", s.Prompt);
        s.SubmitText("Northside Builders, Inc.");
        Assert.Equal("Northside Builders, Inc.", s.CompanyName);
        Assert.Equal("Acme Structural Engineers", s.Document.Entities.OfType<TableEntity>().Single().Title);   // sheets already drawn keep theirs

        var again = new MainViewModel(new FakeUi(), new RecoveryStore(_temp.File("r2")), new UserSettingsStore(path));
        Assert.Equal("Northside Builders, Inc.", again.Session.CompanyName);          // remembered for the next start
        Assert.Equal("Northside Builders, Inc.", new NewLayoutViewModel(again.Session).Company);
        Assert.False(again.Session.SetCompanyName(new string('x', 65)));
    }
}

