# HAQQ CAD

A native Windows 2D CAD application for HAQQ Construction and HAQQ Rebar.
This repository contains **Phase 1: working vector CAD** — draw, select, modify, layers, properties,
command line, undo/redo and the native `.hcad` format.

Read [`docs/STATUS.md`](docs/STATUS.md) first: it states exactly what was verified and what was not.
In short, the first release built cleanly on Windows and passed its tests; later additions are tested
at engine level and listed there.

## Build and run (Windows)

Requirements: Windows 10 or 11 and the **.NET 10 SDK** (x64).

```bat
build.cmd        rem restore, build, run the tests
run.cmd          rem start HAQQ CAD
```

or by hand:

```bat
dotnet build HAQQ.CAD.sln -c Release
dotnet test  HAQQ.CAD.sln -c Release
dotnet run --project src\HAQQ.CAD.Desktop -c Release
```

The solution also opens in Visual Studio 2022 or later (set `HAQQ.CAD.Desktop` as the startup project).
To build against a different installed SDK, add for example `-p:HaqqTfm=net8.0` to the `dotnet build` and `dotnet test` commands (the 160-test run recorded in `docs/STATUS.md` used that setting).

Tests can also be run without NuGet access:

```bat
dotnet run --project tools\OfflineTestRunner -c Release
```

## Using the application

| Action | How |
|---|---|
| Start a command | Type its name or alias and press Enter, or use the ribbon |
| Commands | `LINE` (L), `PLINE` (PL), `RECTANG` (REC), `CIRCLE` (C), `ARC` (A), `MOVE` (M), `COPY` (CO), `ERASE` (E), `UNITS` (UN), `SNAP`, `GRID`, `UNDO` (U), `REDO`, `ZOOM` (Z), `QUIT` |
| Give a point | Click, or type `x,y` · `@dx,dy` · `@distance<angle` |
| Give a length | `66.5` · `5'-6 1/2"` · `5'6` · `2 1/2` · `6"` |
| Direct distance | Point the cursor in a direction and type a distance |
| Finish / repeat | Enter, Space (when the command line is empty) or right-click |
| Cancel | Esc |
| Select | Click an object; click empty space and then a second corner for a window (to the right = inside, to the left = crossing); Shift-click toggles |
| Zoom / pan | Mouse wheel / hold the wheel button and drag; double-click the wheel to fit the drawing |
| Toggles | F3 object snap · F7 grid · F8 ortho · F9 snap to increment · hold Shift for temporary ortho |
| Options | Type the capital letters shown in the prompt (`C` for Close, `D` for Diameter, `3P`), or right-click and pick from the menu |
| Files | Ctrl+N, Ctrl+O, Ctrl+S |
| Undo / redo | Ctrl+Z, Ctrl+Y |

New drawings use inches with architectural (feet-inch) display. Use `UNITS` to change the unit or the display.

## Architecture

The geometry engine, document model and commands know nothing about WPF. The window is a thin shell.

```
HAQQ.CAD.Geometry       Points, vectors, boxes, transforms, segment and circle algorithms (doubles only)
   └─ HAQQ.CAD.Core     Entities, layers, units and feet-inch parsing, document, spatial index, undo/redo
        ├─ HAQQ.CAD.Documents   HCAD serializer, atomic save, backup, recovery snapshots
        ├─ HAQQ.CAD.Snapping    Object snap engine
        │    ├─ HAQQ.CAD.Commands    Command framework, coordinate input, selection, EditorSession
        │    └─ HAQQ.CAD.Rendering   View transform, scene renderer, IRenderSurface abstraction
        └─ HAQQ.CAD.Presentation     View models (MVVM), no WPF types
             └─ HAQQ.CAD.Desktop     WPF window, canvas, dark theme, dialogs   (Windows only)

tests/HAQQ.CAD.Tests         xUnit tests for everything except HAQQ.CAD.Desktop
tools/OfflineTestRunner      Runs the same tests without NuGet (xUnit-compatible shim)
```

Key design points:

- **Model coordinates are doubles.** Pixels exist only inside `ViewTransform` and are never stored.
- **Every edit is an undoable action** applied to the document (`IUndoableAction`, `UndoStack`).
- **Persistent ids.** Each entity has a GUID that survives edits, undo/redo, save and reopen.
- **Commands are UI-free state machines** fed with points, numbers, keywords and Enter (`ICadCommand`),
  so the same code runs from the mouse, the command line and the tests.
- **Spatial index.** A uniform-grid hash accelerates viewport culling, picking and snapping.
- **Renderer behind an interface.** `SceneRenderer` draws through `IRenderSurface`; the WPF backend is
  one small class (`WpfRenderSurface`), so a SkiaSharp or Direct2D backend can replace it later.
- **Nothing is silently lost.** Unknown or unreadable objects in a file are kept and reported.

## Dependencies

| Component | Used by | Licence |
|---|---|---|
| .NET 10 runtime and WPF | Application | MIT (Microsoft) |
| xunit 2.9.3, xunit.runner.visualstudio 2.8.2, Microsoft.NET.Test.Sdk 17.12.0 | Tests only, not shipped | Apache-2.0 / MIT |

The application itself has **no third-party runtime dependencies** in Phase 1: JSON comes from
`System.Text.Json` in .NET, and rendering uses WPF's own drawing API. SkiaSharp, SQLite and a DXF library
from the original technology list are deferred until the phases that need them; each will be reviewed
for commercial redistribution before it is added (see `THIRD-PARTY-NOTICES.md`).

## Repository layout

```
HAQQ.CAD.sln            Solution
Directory.Build.props   Shared build settings (target framework, language version, product info)
build.cmd, run.cmd      Windows convenience scripts
docs/STATUS.md          What works, what is partial, what is not started, and how it was verified
docs/HCAD-FORMAT.md     Native file format specification and compatibility rules
src/                    Application projects
tests/                  Automated tests
tools/                  Offline test runner
```
