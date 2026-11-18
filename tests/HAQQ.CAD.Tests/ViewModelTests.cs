using HAQQ.CAD.Commands;
using HAQQ.CAD.Core;
using HAQQ.CAD.Documents;
using HAQQ.CAD.Geometry;
using HAQQ.CAD.Presentation;
using Xunit;
using static HAQQ.CAD.Tests.T;

namespace HAQQ.CAD.Tests;

public class MainViewModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUi _ui = new();

    public void Dispose() => _temp.Dispose();

    private MainViewModel NewViewModel() => new(_ui, new RecoveryStore(_temp.File("recovery")));

    private static void Type(MainViewModel vm, params string[] lines)
    {
        foreach (string line in lines)
        {
            vm.CommandText = line;
            vm.SubmitCommandLine();
        }
    }

    /// <summary>
    /// Phase 1 acceptance test: create a drawing, modify it, save as HCAD, close the application,
    /// reopen the file and continue editing without loss of supported information.
    /// </summary>
    [Fact]
    public void Phase1_acceptance_create_modify_save_close_reopen_continue()
    {
        string path = _temp.File("acceptance.hcad");

        // --- Session 1: create and modify -------------------------------------------------
        var vm = NewViewModel();
        vm.Session.ContinuousLines = true;                 // these steps draw a chain of lines (LINEMODE Continuous)
        Type(vm, "LINE", "0,0", "@10',0", "@0,5'-6 1/2\"", "");          // two lines, feet-inch input
        Type(vm, "PLINE", "200,0", "@40,0", "@0,30", "C");                // closed polyline
        Type(vm, "RECTANG", "300,0", "@24,36");                           // rectangle
        Type(vm, "CIRCLE", "400,0", "12");                                // circle r = 12

        vm.AddLayerCommand.Execute(null);                                 // new layer "Layer 2"
        var layerVm = vm.Layers.Single(l => l.Name == "Layer 2");
        layerVm.Name = "REBAR";
        layerVm.ColorHex = "#FF0000";
        vm.SelectedLayer = layerVm;
        vm.SetCurrentLayerCommand.Execute(null);
        Type(vm, "CIRCLE", "500,0", "6");                                 // drawn on REBAR

        var s = vm.Session;
        Assert.Equal(6, s.Document.EntityCount);

        // Modify: move the rectangle, copy the first circle, erase the second line, change a property.
        s.SelectBox(new BoundingBox(299, -1, 325, 37), crossing: false);
        Assert.Equal(1, s.Selection.Count);
        Type(vm, "MOVE", "0,0", "@0,100");
        s.ClearSelection();

        s.SelectAt(P(412, 0), 0.5);
        Type(vm, "COPY", "400,0", "400,50", "");
        s.ClearSelection();

        s.SelectAt(P(120, 30), 0.5);
        vm.DeleteSelection();
        Assert.Equal(6, s.Document.EntityCount);

        s.SelectAt(P(60, 0), 0.5);
        vm.Properties.SelectedColor = "Green";
        s.ClearSelection();

        Type(vm, "U");                                                    // undo the colour change...
        Type(vm, "REDO");                                                 // ...and redo it

        Assert.True(s.Document.IsDirty);
        Assert.Contains("*", vm.Title);
        _ui.SavePath = path;
        Assert.True(vm.Save());                                           // first save asks for a name
        Assert.False(s.Document.IsDirty);
        Assert.DoesNotContain("*", vm.Title);
        Assert.Contains("acceptance.hcad", vm.Title);

        var saved = s.Document;
        Assert.True(vm.RequestClose());                                   // "close the application"

        // --- Session 2: reopen and verify nothing was lost ---------------------------------
        var vm2 = NewViewModel();
        Assert.True(vm2.OpenFile(path));
        Assert.Empty(_ui.Errors);
        Assert.Empty(_ui.Infos);                                          // no warnings on open
        var doc = vm2.Session.Document;
        Assert.NotSame(saved, doc);
        Assert.False(doc.IsDirty);

        Assert.Equal(saved.Id, doc.Id);
        Assert.Equal(saved.EntityCount, doc.EntityCount);
        Assert.Equal(saved.Layers.Select(l => (l.Id, l.Name, l.Color)).ToArray(), doc.Layers.Select(l => (l.Id, l.Name, l.Color)).ToArray());
        Assert.Equal(saved.CurrentLayerId, doc.CurrentLayerId);
        Assert.Equal("REBAR", doc.CurrentLayer.Name);
        for (int i = 0; i < saved.EntityCount; i++)
        {
            Assert.Equal(saved.Entities[i].Id, doc.Entities[i].Id);
            Assert.Equal(saved.Entities[i].TypeName, doc.Entities[i].TypeName);
            Assert.Equal(saved.Entities[i].LayerId, doc.Entities[i].LayerId);
            Assert.Equal(saved.Entities[i].Color, doc.Entities[i].Color);
            Assert.Equal(saved.Entities[i].Bounds, doc.Entities[i].Bounds);
        }

        // Spot-check the actual geometry that the edits should have produced.
        var firstLine = doc.Entities.OfType<LineEntity>().Single();
        Assert.Equal(P(0, 0), firstLine.Start);
        Assert.Equal(P(120, 0), firstLine.End);
        Assert.Equal(CadColor.FromRgb(0, 255, 0), firstLine.Color);
        var rect = doc.Entities.OfType<PolylineEntity>().Single(p => p.Vertices.Count == 4);
        Assert.Equal(new BoundingBox(300, 100, 324, 136), rect.Bounds);
        var circles = doc.Entities.OfType<CircleEntity>().OrderBy(c => c.Center.X).ThenBy(c => c.Center.Y).ToList();
        Assert.Equal(3, circles.Count);
        Assert.Equal(P(400, 50), circles[1].Center);
        Assert.Equal(12.0, circles[1].Radius);
        Assert.Equal(doc.FindLayer("REBAR")!.Id, circles[2].LayerId);
        Assert.Equal(CadColor.FromRgb(255, 0, 0), doc.FindLayer("REBAR")!.Color);

        // --- Continue editing the reopened drawing -----------------------------------------
        Type(vm2, "LINE", "0,200", "@50,0");                              // LINE ends at the second point
        vm2.Session.SelectAt(P(400, 12), 0.5);
        Type(vm2, "MOVE", "0,0", "@1000,0");
        Assert.Equal(7, doc.EntityCount);
        Assert.True(doc.IsDirty);
        Assert.True(vm2.Save());                                          // saves to the same file, no dialog

        var vm3 = NewViewModel();
        Assert.True(vm3.OpenFile(path));
        Assert.Equal(7, vm3.Session.Document.EntityCount);
        Assert.Contains(vm3.Session.Document.Entities.OfType<CircleEntity>(), c => c.Center == P(1400, 0));
        Assert.True(File.Exists(path + ".bak"));
    }

    [Fact]
    public void Command_line_echoes_input_and_reports_prompts()
    {
        var vm = NewViewModel();
        Assert.Equal("Command:", vm.Prompt);
        Type(vm, "line");
        Assert.Contains("first point", vm.Prompt);
        Assert.Contains("Command: line", vm.History);
        Assert.Contains("Command: LINE", vm.History);
        Assert.Equal("", vm.CommandText);

        Type(vm, "nonsense!");
        Assert.Contains(vm.History, h => h.Contains("Invalid input"));
        vm.Cancel();
        Assert.Equal("Command:", vm.Prompt);
        Assert.Contains("*Cancel*", vm.History);
    }

    [Fact]
    public void Unsaved_changes_are_protected_on_new_open_and_close()
    {
        var vm = NewViewModel();
        Type(vm, "CIRCLE", "0,0", "1");

        _ui.SaveChangesAnswer = SaveChangesChoice.Cancel;
        Assert.False(vm.NewDocument());
        Assert.False(vm.RequestClose());
        Assert.Equal(1, vm.Session.Document.EntityCount);

        _ui.SaveChangesAnswer = SaveChangesChoice.Save;
        _ui.SavePath = null;                                              // user cancels the Save As dialog
        Assert.False(vm.NewDocument());
        Assert.Equal(1, vm.Session.Document.EntityCount);

        _ui.SavePath = _temp.File("kept");                                // extension is added automatically
        Assert.True(vm.NewDocument());
        Assert.True(File.Exists(_temp.File("kept.hcad")));
        Assert.Equal(0, vm.Session.Document.EntityCount);
        Assert.Null(vm.FilePath);
        Assert.Equal("Untitled - HAQQ CAD", vm.Title);

        int prompts = _ui.SaveChangesPrompts;
        Assert.True(vm.NewDocument());                                    // clean document: no prompt
        Assert.Equal(prompts, _ui.SaveChangesPrompts);

        Type(vm, "CIRCLE", "0,0", "1");
        _ui.SaveChangesAnswer = SaveChangesChoice.Discard;
        Assert.True(vm.RequestClose());
    }

    [Fact]
    public void Opening_a_bad_file_shows_an_error_and_keeps_the_current_drawing()
    {
        var vm = NewViewModel();
        Type(vm, "CIRCLE", "0,0", "1");
        string bad = _temp.File("bad.hcad");
        File.WriteAllText(bad, "not a drawing");

        Assert.False(vm.OpenFile(bad));
        Assert.Single(_ui.Errors);
        Assert.Equal(1, vm.Session.Document.EntityCount);
        Assert.False(vm.OpenFile(_temp.File("missing.hcad")));
        Assert.Equal(2, _ui.Errors.Count);
    }

    [Fact]
    public void Layer_panel_edits_reach_the_document_and_can_be_undone()
    {
        var vm = NewViewModel();
        vm.AddLayerCommand.Execute(null);
        Assert.Equal(5, vm.Layers.Count);                                 // 0, Layer 1 (default), Sheet and Viewport of Layout 1, and the new one
        var row = vm.SelectedLayer!;
        Assert.Equal("Layer 2", row.Name);

        row.Name = "DIMS";
        row.IsVisible = false;
        row.IsLocked = true;
        row.LineweightText = "0.5";
        vm.SetLayerColorCommand.Execute("#00FFFF");

        var layer = vm.Session.Document.FindLayer("DIMS")!;
        Assert.False(layer.IsVisible);
        Assert.True(layer.IsLocked);
        Assert.Equal(0.5, layer.LineweightMm);
        Assert.Equal(CadColor.FromRgb(0, 255, 255), layer.Color);
        Assert.Equal("#00FFFF", row.ColorHex);

        row.Name = "0";                                                   // duplicate: rejected, row keeps its name
        Assert.Equal("DIMS", row.Name);
        row.LineweightText = "thick";                                     // not a number: rejected
        Assert.Equal("0.5", row.LineweightText);

        vm.SetCurrentLayerCommand.Execute(null);
        Assert.True(row.IsCurrent);
        Assert.Equal("DIMS", vm.CurrentLayerName);
        vm.DeleteLayerCommand.Execute(null);                              // current layer cannot be deleted
        Assert.Equal(5, vm.Layers.Count);

        vm.UndoCommand.Execute(null);                                     // undo "set current"
        Assert.False(row.IsCurrent);
        vm.DeleteLayerCommand.Execute(null);
        Assert.Equal(4, vm.Layers.Count);
        vm.UndoCommand.Execute(null);
        Assert.Equal(5, vm.Layers.Count);
        Assert.Equal("DIMS", vm.Layers[2].Name);
    }

    [Fact]
    public void Properties_panel_shows_and_edits_the_selection()
    {
        var vm = NewViewModel();
        vm.Session.ContinuousLines = true;                 // these steps draw a chain of lines (LINEMODE Continuous)
        var props = vm.Properties;
        Assert.Equal("No selection", props.Header);
        Assert.False(props.HasSelection);

        Type(vm, "LINE", "0,0", "@3',0", "");
        Type(vm, "CIRCLE", "100,100", "6");
        var s = vm.Session;
        s.SelectAt(P(18, 0), 0.5);

        Assert.Equal("Line", props.Header);
        Assert.True(props.HasSelection);
        Assert.Equal("Layer 1", props.SelectedLayerName);            // new drawings start on Layer 1
        PropertyRow Row(string label) => props.Rows.Single(r => r.Label == label);
        Assert.Equal("3'-0\"", Row("Length").Value);
        Assert.True(Row("Length").IsReadOnly);
        Assert.Equal("3'-0\"", Row("End X").Value);
        Assert.Equal("0°", Row("Angle").Value);

        Row("End X").Value = "4'-6\"";
        var line = s.Document.Entities.OfType<LineEntity>().Single();
        Assert.Equal(P(54, 0), line.End);
        Assert.Equal("4'-6\"", Row("Length").Value);

        Row("End X").Value = "banana";                                    // rejected with a message
        Assert.Equal(P(54, 0), s.Document.Entities.OfType<LineEntity>().Single().End);
        Assert.Contains(vm.History, h => h.Contains("not a valid length"));
        Row("End X").Value = "0";                                         // would be zero length: rejected
        Assert.Equal(P(54, 0), s.Document.Entities.OfType<LineEntity>().Single().End);

        // Move the selection to another layer through the layer drop-down.
        vm.AddLayerCommand.Execute(null);
        Assert.Contains("Layer 2", props.LayerNames);
        props.SelectedLayerName = "Layer 2";
        Assert.Equal(s.Document.FindLayer("Layer 2")!.Id, s.Document.Entities.OfType<LineEntity>().Single().LayerId);
        Assert.Equal("Layer 2", props.SelectedLayerName);

        // Circle: editing the diameter changes the radius.
        s.ClearSelection();
        s.SelectAt(P(106, 100), 0.5);
        Assert.Equal("Circle", props.Header);
        Row("Diameter").Value = "20";
        Assert.Equal(10.0, s.Document.Entities.OfType<CircleEntity>().Single().Radius);

        // Multiple selection.
        s.SelectAll();
        Assert.Equal("2 objects", props.Header);
        Assert.Null(props.SelectedLayerName);                             // layers differ
        Assert.Equal("ByLayer", props.SelectedColor);
        props.SelectedColor = "#FF8000";
        Assert.Equal("#FF8000", props.SelectedColor);                     // not a standard colour: shown by its code
        Assert.Contains("#FF8000", props.ColorNames);
        Assert.True(s.Document.Entities.All(e => e.Color == CadColor.FromRgb(255, 128, 0)));
        vm.UndoCommand.Execute(null);
        Assert.True(s.Document.Entities.All(e => e.Color == CadColor.ByLayer));
    }

    [Fact]
    public void Status_toggles_and_coordinates()
    {
        var vm = NewViewModel();
        int redraws = 0;
        vm.RedrawRequested += (_, _) => redraws++;

        Assert.True(vm.OsnapOn);
        vm.OsnapOn = false;
        Assert.False(vm.Session.Snap.Enabled);
        vm.OrthoOn = true;
        Assert.True(vm.Session.OrthoEnabled);
        vm.GridOn = false;
        Assert.Equal(3, redraws);

        vm.UpdateCoordinates(P(66.5, -18));
        Assert.Equal("5'-6 1/2\", -1'-6\"", vm.CoordinateText);

        Type(vm, "CIRCLE", "0,0", "1");
        Assert.Contains("1 objects", vm.StatusText);
        Assert.Contains("   Circles: 1", vm.ExplorerItems);
    }

    [Fact]
    public void Dynamic_input_shows_coordinates_then_distance_and_angle_then_typed_text()
    {
        var vm = NewViewModel();
        vm.Session.ContinuousLines = true;                 // these steps draw a chain of lines (LINEMODE Continuous)
        vm.Session.Snap.Enabled = false;
        Assert.Null(vm.GetDynamicInput());              // idle: nothing beside the crosshair

        Type(vm, "LINE");
        vm.Session.UpdateCursor(P(66.5, -18), 0.1);
        var first = vm.GetDynamicInput()!;
        Assert.Equal("Specify first point:", first.Prompt);
        Assert.Equal("5'-6 1/2\"", first.Field1);
        Assert.Equal("-1'-6\"", first.Field2);
        Assert.False(first.IsTyping);

        Type(vm, "0,0");                                // first point typed as coordinates
        vm.Session.UpdateCursor(P(0, 36), 0.1);
        var next = vm.GetDynamicInput()!;
        Assert.Contains("next point", next.Prompt);
        Assert.Equal("3'-0\"", next.Field1);
        Assert.Equal("< 90\u00B0", next.Field2);
        Assert.Equal(P(0, 0), next.From);                // lets the canvas draw the length and angle on the line
        Assert.Equal(P(0, 36), next.To);
        Assert.Equal(90.0, next.AngleDegrees, 9);
        vm.Session.UpdateCursor(P(-10, -10), 0.1);
        Assert.Equal(225.0, vm.GetDynamicInput()!.AngleDegrees, 9);   // counter-clockwise from +X, same as typed @d<225
        Assert.Null(first.From);

        int redraws = 0;
        vm.RedrawRequested += (_, _) => redraws++;
        vm.CommandText = "@24,0";
        Assert.Equal(1, redraws);
        var typing = vm.GetDynamicInput()!;
        Assert.True(typing.IsTyping);
        Assert.Equal("@24,0", typing.Field1);
        vm.SubmitCommandLine();                         // following point typed as coordinates
        Assert.Equal(P(24, 0), ((LineEntity)vm.Session.Document.Entities[0]).End);

        Type(vm, "");
        Type(vm, "MOVE");                               // selecting objects: no point requested
        Assert.Null(vm.GetDynamicInput());
    }

    [Fact]
    public void Zoom_extents_is_requested_for_new_documents_and_the_zoom_command()
    {
        var vm = NewViewModel();
        int requests = 0;
        vm.ZoomExtentsRequested += (_, _) => requests++;
        Type(vm, "ZOOM", "");                         // Enter takes the default: All
        Assert.Equal(1, requests);
        vm.NewDocument();
        Assert.Equal(2, requests);
        vm.ZoomExtentsCommand.Execute(null);
        Assert.Equal(3, requests);
    }

    [Fact]
    public async Task Autosave_then_crash_then_recover()
    {
        string path = _temp.File("job.hcad");
        var vm = NewViewModel();
        Type(vm, "CIRCLE", "0,0", "1");
        _ui.SavePath = path;
        Assert.True(vm.Save());

        await vm.AutosaveAsync();                                         // nothing unsaved: no snapshot
        Assert.Empty(vm.Recovery.FindSnapshots());

        Type(vm, "LINE", "0,0", "10,10", "");                            // unsaved work
        await vm.AutosaveAsync();
        var snapshot = Assert.Single(vm.Recovery.FindSnapshots());
        Assert.Equal(Path.GetFullPath(path), snapshot.OriginalPath);
        Assert.True(vm.Session.Document.IsDirty);                         // autosave is not a save

        // The application "crashes" here: vm is abandoned without RequestClose().
        var vm2 = NewViewModel();
        _ui.ConfirmAnswers.Enqueue(true);                                 // "Recover?" -> yes
        Assert.True(vm2.OfferRecovery());
        Assert.Equal(2, vm2.Session.Document.EntityCount);
        Assert.True(vm2.Session.Document.IsDirty);
        Assert.Equal(Path.GetFullPath(path), vm2.FilePath);
        Assert.Equal(1, HcadFileStore.Load(path).Document.EntityCount);   // the real file is untouched so far

        Assert.True(vm2.Save());
        Assert.Equal(2, HcadFileStore.Load(path).Document.EntityCount);
        Assert.Empty(vm2.Recovery.FindSnapshots());                       // snapshot removed after a real save
    }

    [Fact]
    public async Task Declined_recovery_can_delete_the_snapshot_and_clean_close_removes_it()
    {
        var vm = NewViewModel();
        Type(vm, "CIRCLE", "0,0", "1");
        await vm.AutosaveAsync();
        Assert.Single(vm.Recovery.FindSnapshots());

        var vm2 = NewViewModel();
        _ui.ConfirmAnswers.Enqueue(false);                                // "Recover?" -> no
        _ui.ConfirmAnswers.Enqueue(false);                                // "Delete it?" -> no: keep for next time
        Assert.False(vm2.OfferRecovery());
        Assert.Single(vm2.Recovery.FindSnapshots());

        _ui.ConfirmAnswers.Enqueue(false);
        _ui.ConfirmAnswers.Enqueue(true);                                 // "Delete it?" -> yes
        Assert.False(vm2.OfferRecovery());
        Assert.Empty(vm2.Recovery.FindSnapshots());
        Assert.False(vm2.OfferRecovery());                                // nothing left to offer

        // A clean close removes the snapshot of the drawing being closed.
        await vm.AutosaveAsync();                                         // same change stamp: nothing rewritten
        Type(vm, "CIRCLE", "5,5", "1");
        await vm.AutosaveAsync();
        Assert.Single(vm.Recovery.FindSnapshots());
        _ui.SaveChangesAnswer = SaveChangesChoice.Discard;
        Assert.True(vm.RequestClose());
        Assert.Empty(vm.Recovery.FindSnapshots());
    }
}

public class DefaultLayerTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    public void Dispose() => _temp.Dispose();

    [Fact]
    public void New_drawings_start_on_Layer_1_and_the_layer_box_shows_and_sets_the_current_layer()
    {
        var vm = new MainViewModel(new FakeUi(), new RecoveryStore(_temp.File("recovery")));
        var doc = vm.Session.Document;
        Assert.Equal(new[] { "0", "Layer 1", "Sheet", "Viewport" }, doc.Layers.Select(l => l.Name).ToArray());   // the last two belong to Layout 1
        Assert.Equal("Layer 1", doc.GetLayer(doc.CurrentLayerId)!.Name);
        Assert.False(doc.IsDirty);                                        // a fresh drawing has nothing to save
        Assert.False(vm.Session.UndoStack.CanUndo);

        Assert.False(vm.Properties.HasSelection);
        Assert.Equal("Layer 1", vm.Properties.SelectedLayerName);         // not blank when nothing is selected

        vm.RunCommand("LINE");
        vm.Session.SubmitText("0,0");
        vm.Session.SubmitText("10,0");
        vm.Session.SubmitEnter();
        Assert.Equal(doc.FindLayer("Layer 1")!.Id, doc.Entities[0].LayerId);

        vm.Session.Cancel();                                              // Enter started the next LINE; end it
        vm.Properties.SelectedLayerName = "0";                            // nothing selected, no command: changes the current layer
        Assert.Equal("0", doc.GetLayer(doc.CurrentLayerId)!.Name);
        Assert.Equal("0", vm.Properties.SelectedLayerName);
        Assert.Equal(doc.FindLayer("Layer 1")!.Id, doc.Entities[0].LayerId);   // existing objects stay where they are
        vm.Session.Undo();
        Assert.Equal("Layer 1", vm.Properties.SelectedLayerName);

        vm.AddLayerCommand.Execute(null);
        Assert.Equal("Layer 2", vm.SelectedLayer!.Name);
    }
}

public class UpdaterTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    public void Dispose() => _temp.Dispose();

    private sealed class FakeServer : System.Net.Http.HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = new();
        public List<string> Requests { get; } = new();

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancel)
        {
            string url = request.RequestUri!.ToString();
            Requests.Add(url);
            var response = Files.TryGetValue(url, out var bytes)
                ? new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.ByteArrayContent(bytes) }
                : new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            return Task.FromResult(response);
        }
    }

    private sealed class SynchronousProgress : IProgress<double>
    {
        private readonly Action<double> _report;
        public SynchronousProgress(Action<double> report) => _report = report;
        public void Report(double value) => _report(value);
    }

    private const string Base = "https://example.com/haqqcad/";
    private static readonly UpdateChannel Channel = new(202611150900, new Uri(Base + "haqq-cad-update.json"));

    private static string Sha(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

    private static byte[] Manifest(long build, string file, string sha) =>
        System.Text.Encoding.UTF8.GetBytes($"{{\"build\": {build}, \"file\": \"{file}\", \"sha256\": \"{sha}\"}}");

    [Fact]
    public void Channel_and_manifest_parsing_reject_anything_unsafe_or_malformed()
    {
        var channel = UpdateChannel.Parse("202611150900\r\nhttps://example.com/haqqcad/haqq-cad-update.json\r\n");
        Assert.Equal(Channel, channel);
        Assert.Null(UpdateChannel.Parse(null));
        Assert.Null(UpdateChannel.Parse("202611150900"));
        Assert.Null(UpdateChannel.Parse("abc\nhttps://example.com/x.json"));
        Assert.Null(UpdateChannel.Parse("202611150900\nhttp://example.com/x.json"));          // not https

        string sha = new string('a', 64);
        var m = UpdateManifest.Parse($"{{\"build\": 5, \"file\": \"HaqqCad.exe\", \"sha256\": \"{sha}\"}}", Channel.ManifestUrl)!;
        Assert.Equal(new Uri(Base + "HaqqCad.exe"), m.Download);                             // a name beside the manifest
        Assert.Equal(5, m.Build);
        Assert.NotNull(UpdateManifest.Parse($"{{\"build\": 5, \"file\": \"https://cdn.example.com/a.exe\", \"sha256\": \"{sha}\"}}", Channel.ManifestUrl));
        Assert.Null(UpdateManifest.Parse($"{{\"build\": 5, \"file\": \"http://cdn.example.com/a.exe\", \"sha256\": \"{sha}\"}}", Channel.ManifestUrl));
        Assert.Null(UpdateManifest.Parse($"{{\"build\": 5, \"file\": \"HaqqCad.exe\", \"sha256\": \"short\"}}", Channel.ManifestUrl));
        Assert.Null(UpdateManifest.Parse("{\"build\": 5}", Channel.ManifestUrl));
        Assert.Null(UpdateManifest.Parse("<html>not found</html>", Channel.ManifestUrl));
    }

    [Fact]
    public async Task Newer_build_is_downloaded_verified_and_swapped_in()
    {
        byte[] newProgram = System.Text.Encoding.UTF8.GetBytes("new program bytes");
        var server = new FakeServer();
        server.Files[Base + "haqq-cad-update.json"] = Manifest(202611151200, "HaqqCad.exe", Sha(newProgram));
        server.Files[Base + "HaqqCad.exe"] = newProgram;
        var updater = new Updater(Channel, server);

        var manifest = await updater.CheckAsync();
        Assert.NotNull(manifest);
        Assert.Equal(202611151200, manifest!.Build);

        string downloads = _temp.File("updates");
        var reported = new List<double>();
        string? file = await updater.DownloadAsync(manifest, downloads, default, new SynchronousProgress(reported.Add));
        Assert.NotNull(file);
        Assert.NotEmpty(reported);
        Assert.Equal(1.0, reported[^1], 9);                        // progress reaches 100%
        Assert.Equal(newProgram, File.ReadAllBytes(file!));
        Assert.Empty(Directory.GetFiles(downloads, "*.part"));

        string program = _temp.File("HaqqCad.exe");
        File.WriteAllText(program, "old program");
        Assert.True(Updater.Apply(file!, program, out string? error));
        Assert.Null(error);
        Assert.Equal(newProgram, File.ReadAllBytes(program));
        Assert.Equal("old program", File.ReadAllText(program + ".old"));
        Assert.False(File.Exists(file));

        Updater.CleanUp(program, downloads);                       // what the next start does
        Assert.False(File.Exists(program + ".old"));
        Assert.Equal(newProgram, File.ReadAllBytes(program));
    }

    [Fact]
    public async Task Same_or_older_build_unreachable_server_and_bad_manifest_mean_no_update()
    {
        var server = new FakeServer();
        var updater = new Updater(Channel, server);
        Assert.Null(await updater.CheckAsync());                   // 404
        Assert.Contains("could not be reached", updater.LastError);

        server.Files[Base + "haqq-cad-update.json"] = Manifest(Channel.Build, "HaqqCad.exe", new string('b', 64));
        Assert.Null(await updater.CheckAsync());                   // same build
        Assert.Null(updater.LastError);

        server.Files[Base + "haqq-cad-update.json"] = Manifest(202601010000, "HaqqCad.exe", new string('b', 64));
        Assert.Null(await updater.CheckAsync());                   // older build
        Assert.Null(updater.LastError);

        server.Files[Base + "haqq-cad-update.json"] = System.Text.Encoding.UTF8.GetBytes("oops");
        Assert.Null(await updater.CheckAsync());
        Assert.Contains("could not be read", updater.LastError);
    }

    [Fact]
    public async Task A_download_with_the_wrong_checksum_is_discarded_and_nothing_is_installed()
    {
        var server = new FakeServer();
        server.Files[Base + "haqq-cad-update.json"] = Manifest(202611151200, "HaqqCad.exe", new string('c', 64));
        server.Files[Base + "HaqqCad.exe"] = new byte[] { 1, 2, 3 };
        var updater = new Updater(Channel, server);
        var manifest = await updater.CheckAsync();
        string downloads = _temp.File("updates");
        Assert.Null(await updater.DownloadAsync(manifest!, downloads));
        Assert.Contains("checksum", updater.LastError);
        Assert.Empty(Directory.GetFiles(downloads));

        server.Files.Remove(Base + "HaqqCad.exe");                 // file missing on the server
        Assert.Null(await updater.DownloadAsync(manifest!, downloads));
        Assert.Contains("could not be downloaded", updater.LastError);

        string program = _temp.File("HaqqCad.exe");
        File.WriteAllText(program, "old program");
        Assert.False(Updater.Apply(_temp.File("missing.exe"), program, out string? error));
        Assert.NotNull(error);
        Assert.Equal("old program", File.ReadAllText(program));    // put back
    }
}

public class LayerTableTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    public void Dispose() => _temp.Dispose();

    private MainViewModel NewViewModel() => new(new FakeUi(), new RecoveryStore(_temp.File("recovery")));

    [Fact]
    public void Layer_command_asks_the_shell_to_show_the_layer_table()
    {
        var vm = NewViewModel();
        int shown = 0;
        vm.LayersRequested += (_, _) => shown++;
        vm.RunCommand("LAYER");
        vm.RunCommand("la");
        Assert.Equal(2, shown);
        Assert.Null(vm.Session.ActiveCommand);              // it opens a window; no command stays active
        Assert.Contains("Command: LAYER", vm.History);
    }

    [Fact]
    public void Rows_show_colour_names_and_linetype_and_can_make_themselves_current()
    {
        var vm = NewViewModel();
        vm.AddLayerCommand.Execute(null);
        var row = vm.Layers.Single(l => l.Name == "Layer 2");
        Assert.Equal("White", row.ColorName);
        Assert.Equal("Continuous", row.Linetype);
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        row.ColorHex = "#FF0000";
        Assert.Equal("Red", row.ColorName);
        Assert.Contains(nameof(LayerViewModel.ColorName), changed);
        row.ColorHex = "#123456";
        Assert.Equal("#123456", row.ColorName);             // not a standard colour: shown as its code

        Assert.False(row.IsCurrent);
        row.MakeCurrent();
        Assert.True(row.IsCurrent);
        Assert.Equal("Layer 2", vm.CurrentLayerName);
        row.IsFrozen = true;                                 // freeze toggle reaches the document
        Assert.True(vm.Session.Document.FindLayer("Layer 2")!.IsFrozen);
    }

    [Fact]
    public void Properties_panel_starts_folded_and_the_choice_is_remembered()
    {
        string path = _temp.File("settings.json");
        var vm = new MainViewModel(new FakeUi(), new RecoveryStore(_temp.File("r1")), new UserSettingsStore(path));
        Assert.True(vm.PropertiesCollapsed);                // default
        vm.PropertiesCollapsed = false;
        vm.SetRunningSnap(HAQQ.CAD.Snapping.SnapModes.Nearest, true);     // other settings share the file

        var again = new MainViewModel(new FakeUi(), new RecoveryStore(_temp.File("r2")), new UserSettingsStore(path));
        Assert.False(again.PropertiesCollapsed);
        Assert.True(again.IsRunningSnap(HAQQ.CAD.Snapping.SnapModes.Nearest));
        Assert.Equal(UserSettings.DefaultCrosshairPercent, again.Session.CrosshairSizePercent);
    }

    [Fact]
    public void Remove_unused_deletes_empty_layers_in_one_undo_step_and_keeps_used_current_and_layer_0()
    {
        var vm = NewViewModel();
        var s = vm.Session;
        vm.AddLayerCommand.Execute(null);                   // Layer 2: gets an object
        vm.AddLayerCommand.Execute(null);                   // Layer 3: empty
        vm.AddLayerCommand.Execute(null);                   // Layer 4: empty
        var used = s.Document.FindLayer("Layer 2")!;
        s.Document.AddEntity(new LineEntity(P(0, 0), P(1, 1)) { LayerId = used.Id });

        vm.PurgeLayersCommand.Execute(null);
        Assert.Equal(new[] { "0", "Layer 1", "Sheet", "Viewport", "Layer 2" }, s.Document.Layers.Select(l => l.Name).ToArray());
        Assert.Contains(vm.History, h => h.Contains("2 unused layer(s) removed"));

        s.Undo();
        Assert.Equal(7, s.Document.Layers.Count);
        Assert.Equal(new[] { "0", "Layer 1", "Sheet", "Viewport", "Layer 2", "Layer 3", "Layer 4" }, s.Document.Layers.Select(l => l.Name).ToArray());

        vm.PurgeLayersCommand.Execute(null);
        vm.PurgeLayersCommand.Execute(null);                // nothing left to remove
        Assert.Contains(vm.History, h => h.Contains("No unused layers"));
        Assert.Equal(5, s.Document.Layers.Count);
    }
}

public class LinetypeManagerTests
{
    private static (EditorSession Session, LinetypeManagerViewModel Vm) Make()
    {
        var s = new EditorSession();
        return (s, new LinetypeManagerViewModel(s));
    }

    [Fact]
    public void Lists_every_linetype_with_how_it_is_made()
    {
        var (s, vm) = Make();
        Assert.Equal(s.Document.LinetypeTable.Select(l => l.Name).ToArray(), vm.Rows.Select(r => r.Name).ToArray());
        Assert.Equal("Solid line", vm.Rows.First(r => r.Name == "Continuous").Recipe);
        Assert.Equal("dash 0.5, space 0.25", vm.Rows.First(r => r.Name == "Dashed").Recipe);
        Assert.Equal("dash 0.5, space 0.25, dot, space 0.25", vm.Rows.First(r => r.Name == "Dash Dot").Recipe);
        Assert.True(vm.Rows.First(r => r.Name == "Long Short Short").IsBuiltIn);
        Assert.True(vm.PreviewUnits >= 2.5 * 2.5);                        // long enough for the longest pattern, same for all rows
    }

    [Fact]
    public void New_linetype_is_built_from_dashes_and_spaces_and_can_be_undone()
    {
        var (s, vm) = Make();
        vm.BeginNew();
        Assert.True(vm.IsEditing);
        Assert.False(vm.NameLocked);
        Assert.Equal(new[] { "Dash 1", "Space 1" }, vm.Segments.Select(x => x.Label).ToArray());

        vm.EditName = "Long";
        vm.Segments[0].Text = "2";
        vm.Segments[1].SliderValue = 0.52;                                // the slider snaps to its step
        Assert.Equal(0.5, vm.Segments[1].Value, 9);
        vm.AddPair();
        vm.Segments[2].SliderValue = 0;                                   // a dash of 0 is a dot
        Assert.Equal("dash 2, space 0.5, dot, space 0.5", vm.EditRecipe);

        Assert.True(vm.Save());
        Assert.False(vm.IsEditing);
        Assert.Equal(new[] { 2, 0.5, 0, 0.5 }, s.Document.FindLinetype("long")!.Pattern.ToArray());
        Assert.Equal("Long", vm.Selected!.Name);
        Assert.False(vm.Selected.IsBuiltIn);

        s.Undo();
        Assert.Null(s.Document.FindLinetype("Long"));
        Assert.DoesNotContain(vm.Rows, r => r.Name == "Long");            // the list follows the drawing
    }

    [Fact]
    public void Up_to_four_dashes_and_four_spaces()
    {
        var (_, vm) = Make();
        vm.BeginNew();
        Assert.False(vm.RemovePairCommand.CanExecute(null));
        vm.AddPair(); vm.AddPair(); vm.AddPair();
        Assert.Equal(8, vm.Segments.Count);
        Assert.False(vm.AddPairCommand.CanExecute(null));
        vm.AddPair();
        Assert.Equal(8, vm.Segments.Count);
        Assert.Equal("Space 4", vm.Segments[7].Label);
        vm.RemovePair();
        Assert.Equal(6, vm.Segments.Count);
    }

    [Fact]
    public void A_space_cannot_be_zero_and_bad_text_is_refused()
    {
        var (_, vm) = Make();
        vm.BeginNew();
        vm.Segments[1].SliderValue = 0;
        Assert.True(vm.Segments[1].Value > 0);
        vm.Segments[0].Text = "longDash";
        Assert.Equal(0.5, vm.Segments[0].Value, 9);
        Assert.NotEqual("", vm.Message);
        vm.Segments[0].Text = "3/8";
        Assert.Equal(0.375, vm.Segments[0].Value, 9);
        Assert.Equal("", vm.Message);
    }

    [Fact]
    public void Typing_a_long_value_stretches_the_sliders()
    {
        var (_, vm) = Make();
        vm.BeginNew();
        double before = vm.SliderMaximum;
        vm.Segments[0].Text = "12";
        Assert.True(vm.SliderMaximum >= 24 && vm.SliderMaximum > before);
        Assert.Equal(12.0, vm.Segments[0].SliderValue, 9);
        Assert.True(vm.PreviewUnits >= 12.25 * 2.5);
    }

    [Fact]
    public void Name_must_be_new_and_not_reserved()
    {
        var (s, vm) = Make();
        vm.BeginNew();
        vm.EditName = "dashed";
        Assert.False(vm.Save());
        Assert.True(vm.IsEditing);
        Assert.Contains("already", vm.Message);
        vm.EditName = "ByLayer";
        Assert.False(vm.Save());
        vm.EditName = "  ";
        Assert.False(vm.Save());
        Assert.Equal(Linetypes.BuiltIn.Count, s.Document.LinetypeTable.Count);
    }

    [Fact]
    public void Duplicate_starts_from_the_selected_linetype()
    {
        var (s, vm) = Make();
        vm.Selected = vm.Rows.First(r => r.Name == "Center");
        Assert.True(vm.DuplicateCommand.CanExecute(null));
        vm.DuplicateCommand.Execute(null);
        Assert.Equal("Center 1", vm.EditName);
        Assert.Equal(new[] { 1.25, 0.25, 0.25, 0.25 }, vm.EditPattern);
        Assert.True(vm.Save());
        Assert.NotNull(s.Document.FindLinetype("Center 1"));

        vm.Selected = vm.Rows.First(r => r.Name == "Continuous");
        Assert.False(vm.DuplicateCommand.CanExecute(null));
    }

    [Fact]
    public void Custom_linetype_can_be_edited_built_in_cannot()
    {
        var (s, vm) = Make();
        s.AddLinetype("Mine", "", new[] { 1.0, 0.5 });
        var layer = s.Document.Layers[0];
        layer.Linetype = "Mine";

        vm.Selected = vm.Rows.First(r => r.Name == "Dashed");
        Assert.False(vm.EditCommand.CanExecute(null));
        Assert.False(vm.DeleteCommand.CanExecute(null));

        vm.Selected = vm.Rows.First(r => r.Name == "Mine");
        vm.BeginEdit();
        Assert.True(vm.NameLocked);
        vm.Segments[0].Text = "3";
        Assert.True(vm.Save());
        Assert.Equal(new[] { 3.0, 0.5 }, s.Document.FindLinetype("Mine")!.Pattern.ToArray());
        Assert.Equal("Mine", layer.Linetype);                              // layers keep pointing at it

        s.Undo();
        Assert.Equal(new[] { 1.0, 0.5 }, s.Document.FindLinetype("Mine")!.Pattern.ToArray());
        Assert.Equal("dash 1, space 0.5", vm.Rows.First(r => r.Name == "Mine").Recipe);

        vm.Selected = vm.Rows.First(r => r.Name == "Mine");
        vm.DeleteSelected();                                               // in use by a layer
        Assert.NotNull(s.Document.FindLinetype("Mine"));
        Assert.Contains("used by layer", vm.Message);

        layer.Linetype = "Continuous";
        vm.DeleteSelected();
        Assert.Null(s.Document.FindLinetype("Mine"));
        Assert.False(s.ModifyLinetype("Dashed", "", new[] { 1.0, 1.0 }));  // built-in
    }

    [Fact]
    public void Linetype_scale_box_sets_the_drawing_scale()
    {
        var (s, vm) = Make();
        vm.LinetypeScaleText = "12";
        Assert.Equal(12.0, s.Document.Settings.LinetypeScale, 9);
        vm.LinetypeScaleText = "0";
        Assert.Equal(12.0, s.Document.Settings.LinetypeScale, 9);
        Assert.NotEqual("", vm.Message);
        Assert.Equal("12", vm.LinetypeScaleText);
    }

    [Fact]
    public void LINETYPE_opens_the_window_and_the_typed_version_still_works()
    {
        using var temp = new TempDirectory();
        var vm = new MainViewModel(new FakeUi(), new RecoveryStore(temp.File("r")));
        int opened = 0;
        vm.LinetypesRequested += (_, _) => opened++;
        vm.RunCommand("LT");
        vm.RunCommand("LINETYPE");
        Assert.Equal(2, opened);
        Assert.Null(vm.Session.ActiveCommand);

        vm.Session.StartCommand("-LT");
        Assert.NotNull(vm.Session.ActiveCommand);
    }
}

public class NewObjectPropertiesTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    public void Dispose() => _temp.Dispose();

    private MainViewModel NewViewModel()
    {
        var vm = new MainViewModel(new FakeUi(), new RecoveryStore(_temp.File("recovery")));
        vm.Session.Document.Settings.Format = LengthFormat.Decimal;
        return vm;
    }

    private static void Line(MainViewModel vm, string from, string to)
    {
        vm.Session.SubmitText(from);
        vm.Session.SubmitText(to);
    }

    [Fact]
    public void While_drawing_the_panel_shows_the_new_object_and_a_change_is_for_that_one_only()
    {
        var vm = NewViewModel();
        var s = vm.Session;
        var props = vm.Properties;
        s.AddLayer("Rebar");
        Assert.Equal("No selection", props.Header);
        Assert.Equal("ByLayer", props.SelectedColor);
        Assert.Equal("ByLayer", props.SelectedLinetype);
        Assert.Equal("ByLayer", props.SelectedLineweight);
        Assert.False(props.CanSave);

        vm.RunCommand("LINE");
        Assert.Equal("LINE (new object)", props.Header);
        Assert.Equal("Layer 1", props.SelectedLayerName);
        props.SelectedColor = "Red";
        props.SelectedLinetype = "Dashed";
        props.SelectedLineweight = "0.50 mm";
        props.SelectedLayerName = "Rebar";
        props.Rows.Single(r => r.Label == "Linetype scale").Value = "2";
        Assert.True(props.CanSave);
        Assert.Equal("Red", props.SelectedColor);
        Assert.Equal("Rebar", props.SelectedLayerName);
        Assert.Equal("Layer 1", s.Document.GetLayer(s.Document.CurrentLayerId)!.Name);    // the current layer is untouched
        Line(vm, "0,0", "10,0");                                                           // LINE ends at the second point

        var first = s.Document.Entities.Single();
        Assert.Equal(CadColor.FromRgb(255, 0, 0), first.Color);
        Assert.Equal("Dashed", first.Linetype);
        Assert.Equal(0.5, first.LineweightMm);
        Assert.Equal(2.0, first.LinetypeScale);
        Assert.Equal("Rebar", s.Document.GetLayer(first.LayerId)!.Name);

        // Not saved: the next line is back to the usual properties.
        Assert.Equal("No selection", props.Header);
        Assert.Equal("ByLayer", props.SelectedColor);
        Assert.Equal("Layer 1", props.SelectedLayerName);
        vm.RunCommand("LINE");
        Line(vm, "0,5", "10,5");
        var second = s.Document.Entities.Single(e => e.Id != first.Id);
        Assert.Equal(CadColor.ByLayer, second.Color);
        Assert.Equal("ByLayer", second.Linetype);
        Assert.Null(second.LineweightMm);
        Assert.Equal("Layer 1", s.Document.GetLayer(second.LayerId)!.Name);

        s.Undo();                                                                          // one step each
        Assert.Equal(1, s.Document.EntityCount);
    }

    [Fact]
    public void Save_while_drawing_makes_the_change_stick_for_every_new_object()
    {
        var vm = NewViewModel();
        var s = vm.Session;
        var props = vm.Properties;
        s.AddLayer("Rebar");

        vm.RunCommand("CIRCLE");
        props.SelectedColor = "Cyan";
        props.SelectedLayerName = "Rebar";
        Assert.True(props.Save());
        Assert.False(props.CanSave);                                                       // nothing left to save
        s.SubmitText("0,0");
        s.SubmitText("5");
        Assert.Equal("Rebar", s.Document.GetLayer(s.Document.CurrentLayerId)!.Name);       // switched when the command ended
        Assert.Equal("Cyan", props.SelectedColor);

        vm.RunCommand("LINE");
        Line(vm, "0,0", "10,0");
        Assert.Equal(2, s.Document.EntityCount);
        Assert.True(s.Document.Entities.All(e => e.Color == CadColor.FromRgb(0, 255, 255)));
        Assert.True(s.Document.Entities.All(e => s.Document.GetLayer(e.LayerId)!.Name == "Rebar"));

        // With no command running a change is for all new objects straight away.
        props.SelectedColor = "ByLayer";
        vm.RunCommand("LINE");
        Line(vm, "0,9", "10,9");
        Assert.Equal(1, s.Document.Entities.Count(e => e.Color == CadColor.ByLayer));
    }

    [Fact]
    public void Selected_object_changes_alone_and_Save_copies_its_properties_to_new_objects()
    {
        var vm = NewViewModel();
        var s = vm.Session;
        var props = vm.Properties;
        var rebar = s.AddLayer("Rebar")!;
        vm.RunCommand("LINE"); Line(vm, "0,0", "10,0");
        vm.RunCommand("LINE"); Line(vm, "0,5", "10,5");
        var a = s.Document.Entities[0];

        s.SelectAt(P(5, 0), 0.5);
        Assert.Equal("Line", props.Header);
        props.SelectedColor = "Yellow";
        props.SelectedLineweight = "0.70 mm";
        props.SelectedLayerName = "Rebar";
        a = s.Document.GetEntity(a.Id)!;
        Assert.Equal(CadColor.FromRgb(255, 255, 0), a.Color);
        Assert.Equal(0.7, a.LineweightMm);
        Assert.Equal(rebar.Id, a.LayerId);
        Assert.Equal(1, s.Document.Entities.Count(e => e.Color != CadColor.ByLayer));     // the other line is untouched
        Assert.Equal("0.70 mm", props.SelectedLineweight);

        // Without Save a new line is still plain.
        s.ClearSelection();
        vm.RunCommand("LINE"); Line(vm, "0,7", "10,7");
        Assert.Equal(1, s.Document.Entities.Count(e => e.Color != CadColor.ByLayer));

        s.SelectAt(P(5, 0), 0.5);
        Assert.True(props.CanSave);
        Assert.True(props.Save());
        s.ClearSelection();
        Assert.Equal("Yellow", props.SelectedColor);
        Assert.Equal("Rebar", props.SelectedLayerName);
        vm.RunCommand("LINE"); Line(vm, "0,9", "10,9");
        Assert.Equal(2, s.Document.Entities.Count(e => e.Color == CadColor.FromRgb(255, 255, 0) && e.LayerId == rebar.Id && e.LineweightMm == 0.7));
    }

    [Fact]
    public void Layer_box_keeps_its_value_when_the_layer_list_changes()
    {
        var vm = NewViewModel();
        var raised = new List<string?>();
        vm.Properties.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.Session.AddLayer("Rebar");                                                      // the list changes, the current layer does not
        Assert.Equal("Layer 1", vm.Properties.SelectedLayerName);
        Assert.Contains(nameof(PropertiesViewModel.SelectedLayerName), raised);            // the drop-down is told again
    }
}


public class LayerPlotTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Layer_plot_switch_is_on_by_default_undoable_and_saved()
    {
        var vm = new MainViewModel(new FakeUi(), new RecoveryStore(_temp.File("recovery")));
        var s = vm.Session;
        Assert.True(vm.CurrentLayerPlots);
        Assert.True(vm.Layers.All(l => l.IsPlotted || l.Name == "Viewport"));          // viewport frames are not printed

        vm.CurrentLayerPlots = false;                                   // the HOME tab switch acts on the current layer
        Assert.False(s.Document.CurrentLayer.IsPlotted);
        Assert.False(vm.Layers.Single(l => l.IsCurrent).IsPlotted);
        Assert.True(s.Document.CurrentLayer.IsDisplayed);               // still shown on screen

        var loaded = HcadSerializer.LoadFromBytes(HcadSerializer.SaveToBytes(s.Document)).Document;
        Assert.False(loaded.CurrentLayer.IsPlotted);
        Assert.True(loaded.Layers.Where(l => l.Id != loaded.CurrentLayerId && l.Name != "Viewport").All(l => l.IsPlotted));

        s.Undo();
        Assert.True(vm.CurrentLayerPlots);
        vm.Layers[0].IsPlotted = false;                                 // the layer table switch
        Assert.False(s.Document.Layers[0].IsPlotted);
    }
}

public class ShortcutTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    public void Dispose() => _temp.Dispose();

    private MainViewModel NewViewModel(string settingsFile) =>
        new(new FakeUi(), new RecoveryStore(_temp.File("recovery")), new UserSettingsStore(settingsFile));

    [Fact]
    public void Every_command_is_listed_with_its_standard_shortcuts()
    {
        var vm = NewViewModel(_temp.File("settings.json"));
        var shortcuts = new ShortcutsViewModel(vm);
        Assert.Equal(vm.Session.CommandCatalog.Count, shortcuts.Rows.Count);
        var line = shortcuts.Rows.Single(r => r.Command == "LINE");
        Assert.Equal("L", line.Text);
        Assert.Equal("Draw a line", line.Description);
        Assert.False(line.IsChanged);
        Assert.Equal("LA, LAYERS", shortcuts.Rows.Single(r => r.Command == "LAYER").Text);
        Assert.True(shortcuts.Rows.All(r => r.Description.Length > 0));

        shortcuts.Filter = "dimension";
        Assert.True(shortcuts.Rows.Count is > 3 and < 12);
        Assert.Contains(shortcuts.Rows, r => r.Command == "DIMRAD");
        shortcuts.Filter = "";
        Assert.Equal(vm.Session.CommandCatalog.Count, shortcuts.Rows.Count);
    }

    [Fact]
    public void A_changed_shortcut_starts_the_command_and_is_kept_for_next_time()
    {
        string file = _temp.File("settings.json");
        var vm = NewViewModel(file);
        var shortcuts = new ShortcutsViewModel(vm);
        shortcuts.Rows.Single(r => r.Command == "LINE").Text = "li, ln";        // L is given up
        shortcuts.Rows.Single(r => r.Command == "LAYER").Text = "L";            // and goes to the layer table
        Assert.True(shortcuts.Rows.Single(r => r.Command == "LINE").IsChanged);
        Assert.True(shortcuts.Save());

        int layers = 0;
        vm.LayersRequested += (_, _) => layers++;
        vm.RunCommand("L");
        Assert.Equal(1, layers);
        Assert.Null(vm.Session.ActiveCommand);
        vm.RunCommand("LN");
        Assert.Equal("LINE", vm.Session.ActiveCommand!.Name);
        vm.Session.Cancel();
        vm.RunCommand("LINE");                                                   // the full name always works
        Assert.Equal("LINE", vm.Session.ActiveCommand!.Name);
        vm.Session.Cancel();
        Assert.False(vm.Session.StartCommand("LA"));                             // no longer a shortcut

        var again = NewViewModel(file);                                          // next run
        Assert.Equal(new[] { "LI", "LN" }, again.Session.ShortcutsOf("LINE").ToArray());
        again.RunCommand("LI");
        Assert.Equal("LINE", again.Session.ActiveCommand!.Name);
        Assert.Equal("C", string.Join(",", again.Session.ShortcutsOf("CIRCLE")));  // untouched commands keep theirs

        var reset = new ShortcutsViewModel(again);
        reset.ResetAll();
        Assert.True(reset.Save());
        Assert.Empty(again.Session.CustomShortcuts);
        Assert.Empty(NewViewModel(file).Session.CustomShortcuts);
    }

    [Fact]
    public void A_shortcut_used_twice_or_not_usable_is_refused_and_nothing_changes()
    {
        var vm = NewViewModel(_temp.File("settings.json"));
        var shortcuts = new ShortcutsViewModel(vm);
        shortcuts.Rows.Single(r => r.Command == "MOVE").Text = "C";              // CIRCLE already has C
        Assert.False(shortcuts.Save());
        Assert.Contains("CIRCLE", shortcuts.Message);
        Assert.Contains("MOVE", shortcuts.Message);
        Assert.Equal("M", string.Join(",", vm.Session.ShortcutsOf("MOVE")));

        shortcuts.Rows.Single(r => r.Command == "MOVE").Text = "TRIM";           // the name of another command
        Assert.False(shortcuts.Save());
        shortcuts.Rows.Single(r => r.Command == "MOVE").Text = "M!";
        Assert.False(shortcuts.Save());

        shortcuts.Rows.Single(r => r.Command == "MOVE").Text = "";               // no shortcut at all is allowed
        Assert.True(shortcuts.Save());
        Assert.False(vm.Session.StartCommand("M"));
        Assert.True(vm.Session.StartCommand("MOVE"));
    }

    [Fact]
    public void SHORTCUTS_command_asks_for_the_window()
    {
        var vm = NewViewModel(_temp.File("settings.json"));
        int opened = 0;
        vm.ShortcutsRequested += (_, _) => opened++;
        vm.RunCommand("SHORTCUTS");
        vm.RunCommand("KEYS");
        Assert.Equal(2, opened);
    }
}

public class ZoomPanTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Zoom_offers_all_window_center_in_and_out()
    {
        var vm = new MainViewModel(new FakeUi(), new RecoveryStore(_temp.File("recovery")));
        var s = vm.Session;
        s.Document.Settings.Format = LengthFormat.Decimal;
        var changes = new List<ViewChange>();
        int all = 0;
        vm.ViewChangeRequested += changes.Add;
        vm.ZoomExtentsRequested += (_, _) => all++;

        s.StartCommand("ZOOM");
        Assert.Equal(new[] { "All", "Window", "Center", "In", "Out" }, s.ActiveCommand!.Options.ToArray());
        s.SubmitKeyword("A");
        Assert.Equal(1, all);
        Assert.Null(s.ActiveCommand);
        vm.RunCommand("ZA");                                   // straight to All
        Assert.Equal(2, all);

        vm.RunCommand("ZOOM W");                               // as the ribbon menu does
        s.SubmitPoint(P(10, 20));
        Assert.Equal(4, s.GetPreview().Count);                 // the window is shown while it is picked
        s.SubmitPoint(P(110, 60));
        Assert.Equal(ViewChangeKind.Window, changes[^1].Kind);
        Assert.Equal(new BoundingBox(10, 20, 110, 60), changes[^1].Box);

        s.StartCommand("Z");                                   // a click at the first prompt starts a window too
        s.SubmitPoint(P(0, 0));
        s.SubmitPoint(P(0, 0));                                // the same point: refused
        Assert.NotNull(s.ActiveCommand);
        s.SubmitPoint(P(5, 5));
        Assert.Equal(new BoundingBox(0, 0, 5, 5), changes[^1].Box);

        vm.RunCommand("ZOOM C");
        s.SubmitPoint(P(50, 50));
        s.SubmitText("4");
        Assert.Equal(new ViewChange(ViewChangeKind.Center, Center: P(50, 50), Factor: 4), changes[^1]);
        vm.RunCommand("ZOOM C");
        s.SubmitPoint(P(7, 8));
        s.SubmitEnter();                                       // keep the size
        Assert.Equal(1.0, changes[^1].Factor);

        vm.RunCommand("ZOOM I");
        Assert.Equal(new ViewChange(ViewChangeKind.Factor, Factor: 2), changes[^1]);
        vm.RunCommand("ZO");
        Assert.Equal(0.5, changes[^1].Factor);
        vm.RunCommand("ZW");
        Assert.Contains("first corner", s.Prompt);
        s.Cancel();
        Assert.Equal(0, s.Document.EntityCount);               // zooming draws nothing
        Assert.False(s.UndoStack.CanUndo);
    }

    [Fact]
    public void Pan_runs_until_enter_and_takes_no_points()
    {
        var s = new EditorSession();
        Assert.False(s.IsPanning);
        s.StartCommand("PAN");
        Assert.True(s.IsPanning);
        s.SubmitPoint(P(5, 5));                                // a click is not a point for PAN
        Assert.True(s.IsPanning);
        Assert.Equal(0, s.Document.EntityCount);
        s.SubmitEnter();
        Assert.False(s.IsPanning);
        s.StartCommand("P");
        Assert.True(s.IsPanning);
        s.Cancel();
        Assert.False(s.IsPanning);
    }
}

