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
  "schemaVersion": 9,
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

## Layouts and viewports (schema 8)

`document.layouts` is an array, in tab order. It is always written and is empty for a drawing with only the model.

| Field | Meaning |
|---|---|
| `id` | GUID of the layout |
| `name` | 1 to 64 characters, unique without regard to case, never "Model" |
| `paperWidth`, `paperHeight` | Sheet size in drawing units. The sheet's lower-left corner is at 0,0 |

Every entity may carry `space`: the `id` of the layout it is drawn on. An entity without `space` is in the model.
An entity whose `space` names no layout in the file is kept unmodified with the unsupported objects and a warning is given.

Entity type `viewport` (only meaningful with a `space`):

| Field | Meaning |
|---|---|
| `center` | Middle of the window, on the sheet |
| `width`, `height` | Size of the window on the sheet |
| `modelCenter` | The model point shown at the middle of the window |
| `scale` | Sheet units per model unit: 1/4" = 1'-0" is 0.0208333 (1/48) |

Which tab was open is not saved: a drawing always opens on the model.

Compatibility: a file that has at least one layout is written with `minReaderVersion` 8, because an older reader
would show the objects of every sheet piled on the model. A file without layouts keeps the lower
`minReaderVersion` and still opens in older versions. Files of schema 7 and earlier open unchanged.

## Text with fonts, tables and blocks (schema 9)

### Text

Entity type `text` gained optional fields. A file without them (schema 7 and 8) reads as before: Arial, plain, centred.

| Field | Meaning |
|---|---|
| `text` | The words. Lines are separated by a line feed (`\n`). At most 4000 characters and 200 lines |
| `position` | The insertion point: the middle of the first line's height, at the left end, middle or right end of the lines |
| `height` | Height of a capital letter |
| `angle` | Radians, counter-clockwise |
| `font` | Font family name ("Arial"). A reader without that font uses its default |
| `bold`, `italic`, `underline` | true / false |
| `justify` | `Left`, `Center` or `Right`: which point of the lines `position` is |

The distance from one line to the next is 1.6 times `height`.

### Table

Entity type `table`. Always upright.

| Field | Meaning |
|---|---|
| `position` | Top-left corner |
| `textHeight` | Height of a capital letter in the cells |
| `font` | Font family name |
| `title` | Text across the full width above the columns; empty for none. The title row is 3 x `textHeight` high and its letters 1.25 x `textHeight` |
| `headerRow` | true: the first row is drawn bold and centred |
| `justify` | `Left`, `Center` or `Right` for the other cells |
| `columnWidths` | One number per column (1 to 50) |
| `rowHeights` | One number per row (1 to 200) |
| `cells` | An array of rows, each an array of strings (at most 256 characters, one line). Missing cells are empty |

### Blocks

`document.blocks` is an array, always written:

| Field | Meaning |
|---|---|
| `name` | 1 to 64 characters, unique without regard to case |
| `description`, `category` | Free text; the category is the library group a block came from |
| `entities` | The objects of the block, written like the drawing's entities, about the base point 0,0. Their `layerId` is the empty GUID (the contents take the layer of each place the block is inserted). A block does not contain `insert` or `viewport` entities |

Entity type `insert` places a block:

| Field | Meaning |
|---|---|
| `block` | Name of the block |
| `position` | Where the block's base point is |
| `scale` | Size against the block as made (same in both directions) |
| `rotation` | Radians, counter-clockwise, about the base point |

An `insert` whose block is not in the file is kept unmodified with the unsupported objects, with a warning.

Compatibility: a file that has at least one block is written with `minReaderVersion` 9. A file with text or tables
but no blocks and no layouts keeps the lower `minReaderVersion`: an older reader shows the text without its font
and keeps the tables unmodified as unsupported objects.

The user's block library is a folder of ordinary HCAD files, each holding one block in `document.blocks` and no
entities; `units` says what unit the block was drawn in.

### Additions of 18 Nov (still schema 9, all optional)

- `document.activeLayout`: the `id` of the layout whose tab was open when the drawing was saved; absent when it
  was the model. A reader that does not know it opens the drawing on the model.
- `viewport` entities: `title` (the view's name written under the frame; absent for none), `titleHeight` (its
  letter height on the sheet) and `locked` (true when the view and scale are locked). The scale line under the
  title is not stored: it is worked out from `scale` when the viewport is drawn.
- The order of `document.layouts` is the order of the tabs.


### Hatch (added 18 Nov, still schema 9)

A `hatch` entity is one filled area. An older reader does not know the type: it keeps the entity untouched
(unknown entities are preserved and written back) and warns.

| Property | Meaning |
|---|---|
| `loops` | Array of closed loops, each a flat array `x, y, x, y ...` of at least three points. Filled by the even-odd rule: a loop inside a loop is a hole. Curved boundaries are stored as many short sides (a full circle as 128) |
| `pattern` | Pattern name (`ANSI31`, `BRICK`, `CONCRETE` ...); `SOLID` is a fill. An unknown name is kept and the hatch is drawn as its outline |
| `scale` | Pattern size: pattern units times this are drawing units. More than 0 |
| `angle` | Rotation of the pattern in degrees, counter-clockwise, about `origin` |
| `origin` | `[x, y]` the pattern is laid out from |
| `islands` | `Normal`, `Outer` or `Ignore`: how closed shapes inside the area were treated |
| `associative` | True when the hatch follows its boundary objects |
| `sources` | Ids of the boundary objects (associative hatches) |
| `seed` | `[x, y]` inside the area, when it was found by a click; absent when it was made from selected objects |
| `gapTolerance` | The opening between boundary objects that was allowed; absent for 0 |
| `boundaryLost` | True when the boundary objects no longer close; the loops then hold the last area |

The pattern lines are never stored. They are worked out from `pattern`, `scale`, `angle` and `origin`, so a file
cannot hold lines that disagree with its definition. The pattern definitions themselves are part of the program,
not of the file.
