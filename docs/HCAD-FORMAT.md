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
  "schemaVersion": 4,
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
{ "id": "9d2e...", "name": "REBAR", "color": "#FF0000", "linetype": "Solid",
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
  are never stored: `Solid`, `Dash` (0.5, 0.25), `Short Dash` (0.25, 0.125), `Long Short` (1.25, 0.25, 0.25, 0.25),
  `Long Short Short` (1.25, 0.25, 0.25, 0.25, 0.25, 0.25), `Dash Dot` (0.5, 0.25, 0, 0.25), `Dotted` (0, 0.25) and
  `Dash Dash Dot` (0.5, 0.25, 0.5, 0.25, 0, 0.25). A stored linetype whose name is a built-in one is skipped with a
  warning when the file is read (the built-in is used). A reader that does not know a linetype name draws it
  solid and keeps the name.
- Earlier names: files written before 17 Nov 2026 name the solid linetype `Continuous` (every layer of every
  schema 1-3 file), and development builds of schema 4 used `Dashed`, `Hidden`, `Center`, `Phantom`, `DashDot`,
  `Dot` and `Border`. A reader maps these to `Solid`, `Dash`, `Short Dash`, `Long Short`, `Long Short Short`,
  `Dash Dot`, `Dotted` and `Dash Dash Dot` on layers and entities, unless the file itself defines a linetype
  with that name. Writers use the current names only.
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
