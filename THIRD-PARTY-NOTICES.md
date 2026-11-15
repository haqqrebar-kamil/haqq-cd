# Third-party components

HAQQ-owned source code lives in `src/`, `tests/` and `tools/`. No third-party source code is copied
into this repository. Third-party components are referenced only as NuGet packages.

## Shipped with the application

| Component | Licence | Notes |
|---|---|---|
| .NET runtime and Windows Desktop (WPF) runtime | MIT | Redistributable; installed separately or bundled by a self-contained publish |

## Development and test only (not shipped)

| Component | Version | Licence |
|---|---|---|
| xunit | 2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio | 2.8.2 | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 17.12.0 | MIT |

## Review rule for new dependencies

Before a package is added, record here: name, version, licence, whether it is shipped, and any
redistribution or attribution obligation. Candidates for later phases that still need this review:
a 2D renderer (SkiaSharp), SQLite, a DXF library, a PDF writer, an Excel writer, and any DWG conversion
component. DWG converters in particular must not be assumed to be redistributable.

Interface icons are original HAQQ CAD vector glyphs defined in `src/HAQQ.CAD.Desktop/Themes/Dark.xaml`.
