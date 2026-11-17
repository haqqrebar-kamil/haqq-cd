# HCAD file format — schema version 4

`.hcad` is the native drawing format of HAQQ CAD: a single UTF-8 JSON document.

Schema history: **1** — lines, circles, polylines. **2** — adds the `arc` entity and `units.snapSpacing`
(additive, so `minReaderVersion` stays 1; a schema-1 program keeps arcs it cannot show and writes them back).
**3** — polylines may have `bulges` (arc segments) and a `width`. A file that uses either is written with
`minReaderVersion` 3, because an older program would flatten the arcs and lose them on save; files that use
neither keep `minReaderVersion` 1.
A compressed, multi-part container is planned for production (see "Planned evolution").

The reader and writer live in `src/HAQQ.CAD.Documents/HcadSerializer.cs`.

## Top level

```json
{
  "format": "HCAD",
  "schemaVersion": 7,
  "minReaderVersion": 1,
  "generator": "HAQQ CAD 0.1.0",
  "recovery": { "originalPath": "C:\\Jobs\\footing.hcad", "savedUtc": "2026-11-14T06:30:00.000Z" },
  "document": { ... }
}
```

| Field | Meaning |
|---|---|
| `format` | Always `"HCAD"`. Anything else is rejected. |
| `schemaVersion` | Version of the schema the writer used. |
| `minReaderVersion` | Oldest reader that can open the file without losing data. |
| `generator` | Informational: the program that wrote the file. |
| `recovery` | Present only in autosave snapshots (`*.hcad-recovery`). |

## Versioning and compatibility rules

1. A reader opens a file when `minReaderVersion` is not greater than the reader's own schema version.
   Otherwise it refuses with a clear "saved by a newer version" message. It never opens a file partially.
2. A writer that only **adds** optional fields or new entity types increases `schemaVersion` and leaves
   `minReaderVersion` alone, so older programs can still open the file.
3. A writer that changes the meaning of existing data increases **both** numbers.
4. Readers ignore unknown object properties.
5. Readers never drop unknown entities. An entity with an unknown `type`, or one that cannot be read,
   is kept as raw JSON, reported as a warning on open, and written back unchanged on save.
6. Every repair made while opening (missing layer, duplicate id, missing layer 0) is reported as a warning.

## `document`

```json
{
  "id": "b1c6...",
  "units": { "linear": "Inch", "format": "Architectural", "decimalPrecision": 4,
             "fractionDenominator": 16, "gridSpacing": 12, "snapSpacing": 1 },
  "metadata": { "projectName": "", "description": "", "author": "", "revision": "",
                "createdUtc": "2026-11-14T06:00:00.000Z", "modifiedUtc": "2026-11-14T06:30:00.000Z" },
  "customProperties": { "client": "ACME" },
  "currentLayerId": "9d2e...",
  "layers": [ ... ],
  "entities": [ ... ]
}
```

- `id` — persistent identifier of the drawing (GUID).
- `units.linear` — `Inch`, `Foot`, `Millimeter`, `Centimeter` or `Meter`: the real length of one model unit.
- `units.format` — `Decimal`, `Architectural` (feet and fractional inches) or `Fractional`.
- `customProperties` — free string key/value pairs, sorted by key when written.
- Dates are UTC, ISO 8601, millisecond precision.

## Layers

```json
{ "id": "9d2e...", "name": "REBAR", "color": "#FF0000", "linetype": "Continuous",
  "lineweightMm": 0.5, "visible": true, "locked": false, "frozen": false }
```

Layer `0` always exists; it is recreated (with a warning) if a file lacks it. Layer colours are always explicit.

## Entities

Entities are stored in draw order. Common fields:

```json
{ "type": "line", "id": "4f0a...", "layerId": "9d2e...", "color": "ByLayer",
  "linetype": "ByLayer", "lineweightMm": null, "properties": { "barMark": "A1" } }
```

- `id` — persistent entity identifier (GUID). It never changes when the entity is edited, moved, saved or reopened.
- `color` — `"ByLayer"`, `"ByBlock"` or `"#RRGGBB"`.
- `lineweightMm` — number, or `null` for ByLayer.
- `properties` — optional extensible string key/value pairs.

Type-specific fields:

| `type` | Fields |
|---|---|
| `line` | `start: [x, y]`, `end: [x, y]` |
| `circle` | `center: [x, y]`, `radius` |
| `arc` (schema 2) | `center: [x, y]`, `radius`, `startAngle`, `endAngle` — radians, counter-clockwise from +X; the arc runs counter-clockwise from start to end |
| `polyline` | `closed: bool`, `vertices: [[x, y], ...]`; optional `bulges: [b0, b1, ...]` (schema 3, one per vertex: curvature of the segment starting there, `tan(included angle / 4)`, positive = counter-clockwise, 0 = straight) and width (schema 3): either `width` (one constant width for every segment) or `widths: [[start, end], ...]` (one pair per vertex: the width at the start and end of the segment starting there; different values taper). RECTANG writes a closed polyline |

### Numeric precision

Coordinates are model-space doubles. They are written in the shortest form that parses back to the
identical 64-bit value, so saving and reopening is bit-for-bit lossless. Screen pixels are never stored.

## Saving, backup and recovery

- **Atomic save.** Data is written and flushed to `~<name>.<random>.tmp` in the target folder, then swapped
  over the target in one step. An interrupted save leaves the previous file intact.
- **Backup.** The previous version is kept as `<name>.hcad.bak`.
- **Autosave.** While a drawing has unsaved changes, a snapshot is written every two minutes to
  `%LOCALAPPDATA%\HAQQ CAD\Recovery\<document id>.hcad-recovery`. A snapshot is an ordinary HCAD document
  plus the `recovery` block. It is deleted after a successful save or a clean close; a snapshot left by a
  crash is offered for recovery at the next start.

## Linetypes (schema 4)

Additive, so `minReaderVersion` is unchanged: an older program opens the file, draws everything continuous and,
if it saves, drops the custom definitions (layers and objects keep their linetype names).

- `document.linetypes`: the linetypes defined in the drawing, each `{ "name", "description", "pattern": [dash, gap, dash, gap, ...] }`.
  Lengths are in drawing units; a dash of 0 is a dot; every gap is greater than 0. The built-in linetypes
  are never stored: `Continuous`, `Dashed` (0.5, 0.25), `Short Dash` (0.25, 0.125), `Center` (1.25, 0.25, 0.25, 0.25),
  `Long Short Short` (1.25, 0.25, 0.25, 0.25, 0.25, 0.25), `Dash Dot` (0.5, 0.25, 0, 0.25), `Dotted` (0, 0.25) and
  `Dash Dash Dot` (0.5, 0.25, 0.5, 0.25, 0, 0.25). A stored linetype whose name is a built-in one is skipped with a
  warning when the file is read (the built-in is used). A reader that does not know a linetype name draws it
  continuous and keeps the name.
- Earlier names: development builds of 17 Nov 2026 (schema 4, never released) wrote `Solid`, `Dash`, `Long Short`,
  `Hidden`, `Phantom`, `DashDot`, `Dot` and `Border` for a while. A reader maps these to `Continuous`, `Dashed`,
  `Center`, `Short Dash`, `Long Short Short`, `Dash Dot`, `Dotted` and `Dash Dash Dot` on layers and entities,
  unless the file itself defines a linetype with that name. Writers use the current names only.
- `document.units.linetypeScale`: the drawing's linetype scale (default 1). Every pattern length is multiplied by it.
- Layer `linetype`: a linetype name.
- Entity `linetype`: `ByLayer` (use the layer's), `ByBlock` (use the containing block's; drawn continuous until
  blocks exist) or a linetype name. A name that is not in the table is kept and drawn continuous.
- Entity `linetypeScale`: optional, written only when it is not 1; multiplies the drawing's scale for that object.
- A linetype entry that cannot be read is skipped with a warning.

## Planned evolution (not implemented yet)

Reserved for later phases; each will follow the versioning rules above:
ellipses, splines, text, dimensions, hatches, blocks and block references, layouts and viewports,
rebar objects, bar marks and schedules, revision records, and a compressed container
(ZIP with `document.json`, thumbnails and embedded resources).

## Dimensions (schema 5)

- Entity type `dimension`:
  `{ "type": "dimension", "kind", "p1": [x, y], "p2": [x, y], "center": [x, y], "linePoint": [x, y], "angle", "textHeight", "text" }`
  plus the common entity fields (id, layerId, color, linetype, lineweightMm).
- `kind`: `Linear` (distance between `p1` and `p2` measured along the direction `angle`, in radians; 0 = horizontal),
  `Aligned` (true distance between `p1` and `p2`), `Radius` and `Diameter` (`center`, and `p1` on the circle where
  the arrow touches), `Angle` (angle at `center` between the rays to `p1` and `p2`, on the side of `linePoint`),
  `ArcLength` (length along the arc about `center` from `p1` counter-clockwise to `p2`).
- `linePoint`: a point the dimension line, or dimension arc, passes through.
- `textHeight`: height of the text in drawing units; arrowheads and gaps are sized from it.
- `text`: empty to show the measured value; otherwise the text to show, in which `<>` stands for the measured value.
- The measured value is **not stored**: a reader works it out from the points. Fields a kind does not use are
  still written (as written by the program, normally `[0, 0]` or `0`).
- `document.units.dimensionTextHeight`: text height given to new dimensions (default 4).
- `minReaderVersion` stays 1: a reader that does not know `dimension` keeps the entity as an unsupported object
  and writes it back unchanged.
- Layer `plot` (boolean, optional, default `true`; added with schema 5): `false` means the layer is shown on screen
  but left out of prints and exports.

## Dimension styles (schema 6)

- `document.dimensionStyles`: the named styles of the drawing, each
  `{ "name", "textHeight", "textGap", "arrow", "arrowSize", "extensionOffset", "extensionBeyond", "format",
  "precision", "fractionDenominator", "roundOff", "scaleFactor", "prefix", "suffix", "suppressZeroFeet",
  "suppressZeroInches", "anglePrecision" }`. Sizes are in drawing units. `arrow`: `Arrow`, `Tick`, `Dot`, `None`.
  `format`: `Drawing` (as the drawing's units), `Decimal`, `Architectural`, `Fractional`. A style named `Standard`
  always exists; when the file has one, its values replace the built-in ones.
- `document.currentDimensionStyle`: name of the style new dimensions are drawn with.
- Dimension entity `style`: an object with the same fields: the values that dimension is drawn with (a copy,
  so a dimension can be read without the table). It replaces the entity field `textHeight` of schema 5.
- Reading schema 5: a dimension with `textHeight` and no `style` gets the Standard values scaled so that the text
  has that height; `document.units.dimensionTextHeight` scales the Standard style the same way. Schema 6 no longer
  writes either field.
- A missing or unusable field of a style keeps its standard value; a style that is still not usable is skipped
  with a warning.

## Groups, grid lines and text (schema 7)

- Every entity may have `groupId` (a GUID string). Entities with the same `groupId` belong together (the lines
  of one grid system). Omitted when the entity is in no group.
- Entity type `gridline`: a grid line with its label. Straight: `"start": [x, y], "end": [x, y]`. Along an arc:
  `"center": [x, y], "radius", "startAngle", "endAngle"` (radians, counter-clockwise from start to end).
  Both: `"label"` (may be empty), `"bubbleDiameter"` (diameter of the label circle, drawing units),
  `"labelAtStart"`, `"labelAtEnd"` (booleans). The label circle sits just past the end it belongs to, in line
  with the grid line; it is not stored. The linetype of a grid line applies to the line only.
- Entity type `text`: one line of text. `"position": [x, y]` (the centre of the text), `"height"` (of a capital
  letter, drawing units), `"angle"` (radians, counter-clockwise), `"text"`.
- `minReaderVersion` stays 1: older readers keep these entities as unsupported objects and write them back.

