# HAQQ CAD — development status

Last updated: 2026-11-15 · Version 0.2.0-dev · Phase 1 complete, Phase 2 started

## How this build was verified

| Check | Result |
|---|---|
| First release (Phase 1) built on Windows 10/11 with the .NET 10 SDK (10.0.404), including the WPF project | **Passed**, 0 warnings, 0 errors |
| First release: `dotnet test` with real xUnit on Windows | **160 passed, 0 failed** |
| Current source: engine and view-model libraries compiled on Linux (.NET 8 SDK) | **Passed**, 0 warnings |
| Current source: 275 automated tests through `tools/OfflineTestRunner` | **275 passed, 0 failed** |
| Build of 14 Nov evening on Windows (.NET 10): solution build and `dotnet test` | **Passed**, 181 tests at that point; the app was seen running (screenshots from the owner) |
| Current source: WPF project compiled on Windows | **Compiled** by the installer build of 15 Nov 10:38 (all features up to the updater); the self-installer added afterwards is **not compiled yet**. Earlier note: not confirmed for the latest changes (per-section polyline width, rectangle options, the 11 arc methods and the longer Arc drop-down, grip editing, object snaps and their menus, folding Properties panel); no build log read since the 181-test build |
| Running the application and using it by hand | Owner has run it and sent screenshots of the window, crosshair and input box; no systematic manual test |

"Tested" below means logic exercised by automated tests on real code. Mouse handling, painting and
keyboard routing in the WPF window are not covered by automated tests.

## Phase 1 scope

Legend: **Tested** = implemented and covered by executed automated tests ·
**Untested UI** = implemented in the WPF shell, not compiled/run · **Partial** · **Not started**

| Requirement | State | Notes |
|---|---|---|
| Main application window | Untested UI | `MainWindow.xaml` |
| Professional dark UI | Untested UI | Custom dark theme; scroll bars still use the Windows default look |
| Vector drawing canvas | Untested UI | Scene logic (culling, clipping, colours, level of detail) is tested through a recording surface |
| Pan and zoom | Tested (math) / Untested UI (mouse) | Wheel zoom about the cursor, middle-button pan, double-click wheel = extents |
| LINE | Tested | Ends at the second point by default (owner's request, 15 Nov); Enter or Space starts the next line. `LINEMODE` switches to Continuous (chain with Undo/Close, as before); the mode is not saved between runs. Direct distance entry |
| PLINE | Tested | Straight segments, Close/Undo. Arc segments and widths: see below |
| RECTANG | Tested | Two corners or `@width,height` |
| CIRCLE | Tested | Radius, diameter, or picked point |
| Entity selection | Tested | Pick, window (left-to-right), crossing (right-to-left), Shift toggles, `ALL` |
| MOVE | Tested | Base point + second point, relative/polar input, distance in cursor direction |
| COPY | Tested | Multiple copies, new persistent ids |
| Delete (ERASE) | Tested | Command, `E`, Delete key |
| Endpoint and midpoint snapping | Tested | Plus circle centre. Works on real geometry through the spatial index |
| Basic layers | Tested | Name, colour, lineweight, on/off, lock, freeze, current layer; all undoable |
| Properties | Tested | Layer, colour, line endpoints, circle centre/radius/diameter; computed length/area |
| Command line | Tested | Names, aliases, prompts, options, Enter/Space repeat, Esc cancel |
| Coordinate input | Tested | `x,y`, `@dx,dy`, `@dist<angle`, feet-inches-fractions (`5'-6 1/2"`) |
| ORTHO, GRID, OSNAP toggles | Tested (logic) | F8, F7, F3 and status-bar buttons |
| Undo/Redo | Tested | Every edit, including layer and property changes |
| Native HCAD Save/Open | Tested | Versioned schema, bit-exact coordinates, see `HCAD-FORMAT.md` |
| Save As, atomic save, `.bak` backup | Tested | |
| Autosave and crash recovery | Tested | Snapshot every 2 minutes while there are unsaved changes |
| Unsaved-changes protection | Tested | New / Open / Close |
| **Acceptance test** (create → modify → save → close → reopen → continue editing) | Tested at view-model level | `MainViewModelTests.Phase1_acceptance_...` |

## Added after Phase 1 (from the AutoCAD 2D Fundamentals chapter the owner supplied)

| Feature | State | Notes |
|---|---|---|
| ARC | Tested | All 11 methods: 3-Point; Start-Center-End/Angle/Length; Start-End-Angle/Direction/Radius; Center-Start-End/Angle/Length; Continue (tangent to the last line or arc drawn in this session; polylines do not count yet). Negative angle = clockwise, negative chord length or radius = major arc. A click at an angle prompt is measured from the X axis. `arc` entity: snaps, selection, properties, HCAD |
| CIRCLE 2P and 3P, Diameter, default radius in `<>` | Tested | TTR/TTT: see below |
| UNITS command | Tested | Decimal / Architectural / Fractional; inches, feet, mm, cm, meters; precision. Does not rescale geometry |
| SNAP mode (F9) and SNAP / GRID spacing commands | Tested (logic) | |
| `@` coordinates relative to the last point of the previous command | Tested | |
| Shift = temporary ORTHO | Tested (logic) | |
| Input boxes beside the crosshair (prompt, X/Y, distance/angle, typed text) | Tested (content) / Untested UI (drawing) | No Tab between fields |
| Right-click menu: Enter, Cancel, command options, Repeat, Undo/Redo | Tested (content) / Untested UI | Right-click still accepts a selection directly |
| Coordinate read-out modes (absolute, distance<angle, off) | Tested | Click the read-out |
| QUIT / EXIT, Ctrl+Q | Tested (command) | |
| CIRCLE Tan-Tan-Radius and Tan-Tan-Tan | Tested | Tangent to lines, polyline segments, arcs and circles; nearest solution to the picks |
| Circle and Arc drop-down lists on the ribbon | Untested UI | Standard light menu style, no preview icons |
| Polar tracking (POLAR, F10) | Tested (logic and ray) | 90-degree increments only; green dashed ray and "Polar:" tooltip |
| Length and angle shown on the rubber-band line | Tested (values) / Untested UI | Angle is 0-360 counter-clockwise |
| Tab between distance and angle boxes (X and Y for a first point) | Tested (logic) | Preview is not yet constrained to a locked value |
| Crosshair: short cross with centre square; CURSORSIZE setting saved per user | Tested | |
| ROTATE, SCALE, MIRROR | Tested | SCALE shows the scaled result while the cursor moves and a click accepts it (same size when the cursor is as far from the base point as the farthest corner of the selection); a typed factor still works. No Reference or Copy options |
| PLINE options: Arc, Line, Close, Halfwidth, Length, Undo, Width | Tested | Arcs are tangent to the previous segment. Width and Halfwidth apply to the segments drawn after them and can taper (starting and ending width). HCAD schema 3 |
| RECTANG options: Area, Dimensions, Rotation; Chamfer, Fillet, Width | Tested | Elevation and Thickness (3D) are not offered |
| RECTANG: Shift makes a square; ORTHO/POLAR no longer flatten rectangles | Tested | |
| Grip editing: drag a grip of a selected object (or click it, then click the new place) | Tested (logic) / Untested UI (mouse drag) | Line ends stretch, line midpoint moves; polyline vertices; arc ends and midpoint re-fit the arc, center moves; circle center moves, quadrants change radius. Selected objects sharing the grip point move together. Snap, ORTHO, POLAR and typed coordinates apply. One undo step. No grip hover highlight, no Shift multi-grip, no polyline edge-midpoint grips |
| Object snaps: Endpoint, Midpoint, Center, Intersection, Quadrant, Perpendicular, Tangent, Nearest | Tested (logic and markers) | Perpendicular and Tangent need a previous point (second point of a line, not the first). Intersection is of the visible objects only, not their extensions. A perpendicular whose foot falls past the end of a line is not offered |
| Right-click > Snap Overrides (one snap for the next point only, also with OSNAP off) | Tested (logic) / Untested UI (sub-menu) | A click that finds no such snap is refused and the override is cancelled |
| Automatic (running) snap check boxes: right-click OSNAP, or right-click > Object Snap Settings; saved per user | Tested (logic) / Untested UI | Default: Endpoint, Midpoint, Center, Intersection |
| Properties panel folds away to the right edge (strip with arrow) | Untested UI | Not remembered between runs |
| New drawings start on "Layer 1" (layer 0 is kept as well); the Properties layer box shows and sets the current layer when nothing is selected; new layers are named "Layer 2", "Layer 3"… | Tested | Drawings saved earlier open with the layers they were saved with |
| Test release for other PCs: `make-installer.cmd` builds a self-contained single-file `HaqqCad.exe`, a portable zip and a setup exe | Build and portable zip **ran on the owner's PC** (15 Nov); setup exe **not run yet** | The setup exe is the program itself under the name `HAQQ-CAD-Setup-*.exe`; under that name it installs itself per user and adds shortcuts (the first attempt with IExpress produced no file and was dropped). Unsigned, so Windows SmartScreen will warn. No uninstaller |
| Automatic update of test releases (check at every start, https + SHA-256, swap on restart) | Tested (check, download, verify, swap) / **Not run end to end** | Needs an https web folder named in `update-url.txt`; the WPF wiring, the restart and the installer script changes have not been executed |
| Update status shown in the status bar (checking, downloading with percent, installed, up to date, failed) | Tested (progress values) / Untested UI | Auto-update itself: owner published build-2 and build-3 on 15 Nov; **whether an installed copy updated has not been confirmed to me** |
| Space bar = Enter, also after a typed command name or option keyword | Untested UI | |
| OFFSET (O) | Tested | Lines, arcs, circles and polylines (corners re-joined, arc corners change radius, widths kept); distance remembered; repeats until Enter. No Through or Erase options. A polyline whose corner arc would vanish is refused |
| TRIM (TR), quick mode | Tested | Click the part to remove; every other displayed object is a cutting edge. Targets: lines, arcs, circles (a circle needs two crossings). **Polyline targets not yet** (EXPLODE first). No "select cutting edges" mode, no fence |
| EXTEND (EX), quick mode | Tested | Click near an end of a line or arc; it grows to the first object met. Polyline targets not yet |
| FILLET (F) and CHAMFER (CHA) | Tested | Two lines only (not arcs, circles or polyline corners yet). Radius / distances remembered for the session; radius 0 makes a sharp corner. No Polyline, Trim-mode or Multiple options |
| JOIN (J) | Tested | Selected lines, arcs and open polylines whose ends touch (within 0.000001 units) become polylines; a loop becomes a closed polyline; curves and widths are kept. Objects with a gap are not joined (no gap tolerance setting) |
| Snap name shown beside the snap marker; snap overrides can be typed (END, MID, INT, CEN, QUA, PER, TAN, NEA, NON) | Tested (typed codes) / Untested UI (label) | The owner reported on 16 Nov that Tangent on a second point "does not work" in the application; the same sequence passes at engine and view-model level, so the cause in the running program is **not yet found** |
| Tangent snap on the first point of a LINE, and a line tangent to two circles | Tested (logic) / Untested UI | The line start slides round the circle until the second point is given. Only LINE supports this; Perpendicular still needs a previous point |
| EXPLODE (X) | Tested | Polylines into lines and arcs; width is dropped |
| While a command asks for an object click, object snap is off and the pick box is shown | Tested (logic) / Untested UI | |
| Not taken from the chapter yet | Not started | Drawing LIMITS, dotted grid style, Pan command, UCS icon, templates |

## Known limitations

- Lineweights and linetypes are stored and round-tripped but everything is drawn as a continuous 1-pixel line.
- Very large arcs viewed at extreme zoom are drawn with at most 20,000 chords.
- Snaps: no extended/apparent intersection, no deferred perpendicular or tangent for a first point, no object-snap tracking.
- One drawing per window; model space only.
- The whole visible scene is redrawn on each change. There is no cached bitmap/tile layer yet.
- Document edits happen on the UI thread; only autosave file writing is on a background thread.
- HCAD is indented JSON and therefore large; the compressed container is planned.
- The ribbon shows only tabs whose tools exist (FILE, HOME, DRAW, MODIFY, LAYERS).

## Measurements (engine only, no WPF drawing; 2-core Linux VM, .NET 8, Release)

200,000 random lines and circles:

| Operation | Time |
|---|---|
| Build the drawing in memory | about 1.9 s |
| Cull + scene traversal, zoomed in (74 entities on screen) | about 0.2 ms per frame |
| Cull + scene traversal, whole drawing visible | 60–100 ms per frame |
| Object-snap query | about 4 µs |
| Pick query | about 3 µs |
| Save to HCAD | about 1.5 s, 76 MB |
| Open from HCAD | about 1.5 s |

Zoomed-in work stays fast because cost depends on what is on screen. Viewing a very large drawing all at
once is not yet smooth; that needs the planned render cache.

## Later phases — not started

| Area | State |
|---|---|
| ELLIPSE, SPLINE, POLYGON, POINT, XLINE, RAY, HATCH | Not started |
| TEXT, MTEXT, LEADER, MLEADER, dimensions, styles | Not started |
| STRETCH (window), BREAK, ARRAY, LENGTHEN; PEDIT | Not started |
| Remaining object snaps, polar and object-snap tracking, dynamic input | Not started |
| Blocks, block editor, attributes, Match Properties, measure tools | Not started |
| Paper space, layouts, viewports, title blocks, printing, PDF/SVG export | Not started |
| DXF import/export (`HAQQ.CAD.DXF`) | Not started |
| DWG converter interface and placeholder adapter (`HAQQ.CAD.DWGConverter`) | Not started |
| Rebar shape engine (`HAQQ.CAD.Rebar`) | Not started |
| Rebar generators and bar bending schedule (`HAQQ.CAD.RebarSchedules`) | Not started |
| Licensing, installer, updates (`HAQQ.CAD.Licensing`) | Not started |

Projects for later phases are intentionally not in the solution yet, so nothing appears to exist that does not work.

## Planned: AI plan reading (decisions of 2026-11-14, not started)

- Purpose: the user loads a screenshot or PDF page of a structural or architectural plan (no site photos) and
  HAQQ CAD draws an editable vector copy on proper layers.
- Reading is done by a cloud vision model; this feature needs internet. The rest of HAQQ CAD stays offline.
- Prototype: the owner's own API access. Product: customers buy tokens from HAQQ and spend them per page,
  which means requests go through a HAQQ service that meters usage (ties into the licensing phase).
- "Teaching": an element library maintained by the owner (concrete and building elements, descriptions,
  target layers, approved examples and corrections) that is sent with each request. The model is not retrained.
- Order: after the core drawing tools. First step, needing no AI: load an image as a scaled background to trace.
