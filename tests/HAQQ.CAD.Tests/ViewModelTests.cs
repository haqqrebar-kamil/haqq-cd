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
        vm.Properties.Rows.Single(r => r.Label == "Color").Value = "#00FF00";
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
        Type(vm2, "LINE", "0,200", "@50,0", "");
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
        Assert.Equal(3, vm.Layers.Count);                                 // 0, Layer 1 (default), and the new one
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
        Assert.Equal(3, vm.Layers.Count);

        vm.UndoCommand.Execute(null);                                     // undo "set current"
        Assert.False(row.IsCurrent);
        vm.DeleteLayerCommand.Execute(null);
        Assert.Equal(2, vm.Layers.Count);
        vm.UndoCommand.Execute(null);
        Assert.Equal(3, vm.Layers.Count);
        Assert.Equal("DIMS", vm.Layers[2].Name);
    }

    [Fact]
    public void Properties_panel_shows_and_edits_the_selection()
    {
        var vm = NewViewModel();
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
        Assert.Equal("ByLayer", Row("Color").Value);
        Row("Color").Value = "#FF8000";
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
        Type(vm, "ZOOM");
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
        Assert.Equal(new[] { "0", "Layer 1" }, doc.Layers.Select(l => l.Name).ToArray());
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

        vm.Properties.SelectedLayerName = "0";                            // nothing selected: changes the current layer
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
        string? file = await updater.DownloadAsync(manifest, downloads);
        Assert.NotNull(file);
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
