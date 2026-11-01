# Evidence from the station maps

`analyze_maps.py` reads this checkout's map descriptors, map files, entity prototype
inheritance, generated variants, map-load migrations, English station-beacon labels and serialized floor tiles. The checked
report is reproducible and has no current-time field or random input.

The sample contains all **14 root station maps**, excluding CentComm, tests,
arenas, shuttles, ruins and salvage maps. It distinguishes the **10 maps in
`DefaultMapPool`** from fallback Saltern and supplemental Hike, Relic and gear.
Default-pool membership is evidence about the checked-in pool, rather than a
claim about every SS14 server or an upstream release.

Files:

- `map-evidence.json`: compact machine-readable report, including 830 mapped
  navigation beacons, named corroborating landmarks, explicit machine prototypes,
  UIDs, grid positions, job slots, frequencies and conservative floor-component
  estimates. Main-grid and other-grid observations are explicitly distinguished.
- `room-marker-frequency.csv`: readable per-label map frequency, map IDs and
  closed-door component areas. This is a marker frequency table, not a room census.
- `map_features.json`: editable exact prototype queries for system observations.
  Adding another queried machine does not require changing the extraction code.
- `test_analyze_maps.py`: tests the chunk byte layout, inheritance override,
  grid-coordinate attribution and closed-door component transformation.

From the repository root, with Python 3 and PyYAML installed:

```powershell
python Tools/StationArchitect/Analysis/analyze_maps.py
python -m unittest discover -s Tools/StationArchitect/Analysis -p test_analyze_maps.py
```

For a more verbose investigation, use `--pretty --include-boundaries --output
.tmp/station-map-evidence.pretty.json`. The default checked report excludes every
individual doorway adjacency to keep it reasonably small. The script never
changes maps or prototypes.

## How the observations are obtained

The generated designer example also has an independent export audit:

```powershell
python Tools/StationArchitect/Analysis/audit_design_export.py Tools/StationArchitect/examples/generated/station-14014.zip --output .tmp/export-audit.json
python Tools/StationArchitect/Analysis/render_design.py Tools/StationArchitect/examples/generated/station-14014.station.json .tmp/station-plan.png
```

The audit checks the serialized bundle against local prototype/tile definitions,
format-7 grid data and native version-2 atmosphere records. It does not run game
simulation. The mask renderer uses Pillow and is useful for inspecting actual
saved floor, interfaces and equipment without a browser.

`layout_evidence.py` extracts the smaller, inspectable sample used for nesting
and distributed placement. Its exact beacon, door and equipment observations
are in `layout-evidence.json` and explained in [LAYOUT_EVIDENCE.md](../LAYOUT_EVIDENCE.md).

With the current tool running on port 8423, retain every actual generation stage
and exercise nested edits through its HTTP API:

```powershell
python Tools/StationArchitect/Analysis/probe_design.py .tmp/station-probe --base http://127.0.0.1:8423 --topology ring --seed 14014
python Tools/StationArchitect/Tests/nested_api_integration.py --base-url http://127.0.0.1:8423
```

The probe also accepts `--compactness`, `--bundling` and `--stop-after`. It uses
the same stage endpoints as the GUI and saves actual masks and diagnostics; it
does not independently generate or repair geometry.
`measure_design.py PROJECT` measures the saved room floor and built station
footprints separately, excluding the unbuilt envelope from density figures.

Entity definitions are indexed from **all** `Resources/Prototypes` folders,
including `Catalog/Fills`; filled lockers and suit-storage prototypes are not
confined to `Entities`. The fixed `!type:CreateVariants` data transform expands
ID `values`, matching parent/property `values` and nested `sequences` at the same
variant index, following the engine's variantization implementation. Other YAML
tags remain inert data; no named runtime type is instantiated.

Prototype components are inherited, then map component fields override them.
This matters because most beacons obtain their text from a parent prototype and
the English Fluent resource instead of storing it on the map entity. Each marker
can be traced to `Resources/Maps/<map>.yml#uid=<uid>`. A marker text and a prototype
ID are distinct observations: a room can have several markers, share a floor
component with other named destinations, or have no marker at all.

Before applying defaults, `Resources/migration.yml` renames or removes historical
mapped prototypes using the engine's single-alias map-load behavior. For example,
`hydroponicsTray` becomes `HydroponicsTray`, and `computerBodyScanner` becomes
`ComputerBodyScanner`. Each observation retains `rawPrototype`, while `prototype`
and `mainGridPrototypeCounts` use the effective current ID. Raw main-grid counts
and applied migrations are also retained, so source UIDs remain auditable. The
reported active entity count excludes migration-deleted groups; `rawEntityCount`
retains the original map count.

The largest grid by mapped non-space/non-lattice tiles is treated as the main
station grid. Machine frequency counts use that grid. Other-grid observations
are retained separately instead of inflating station counts with detached shuttle
contents. Nested entities retain their ancestor grid; their reported position
uses parent translations. Only direct grid-child beacons receive geometric
component estimates, avoiding guesses about rotated nested coordinate systems.

Chunk decoding follows
`RobustToolbox/Robust.Shared/EntitySerialization/MapChunkSerializer.cs`: version 7
has a four-byte little-endian tile ID followed by flags, variant and
rotation/mirroring bytes, in row order. Older supported chunk versions use their
corresponding shorter record. Empty space and lattice are excluded from the floor
sample.

For sizing observations, doors are considered closed and structural wall,
window and grille tiles block flood-fill. Furniture and equipment do not remove
floor. Directional walls and windows are conservatively treated as whole blocked
tiles. The resulting area, aspect and fill observations are **closed-door floor
components**, not an engine-certified room decomposition. An observation's
`labels` list exposes shared named spaces; `openEdgesToMissingFloor` exposes
components that are not sealed by this approximation. Public circulation and
maintenance labels must not be mistaken for single enclosed activity rooms.

Map-level machine presence does not establish which room contains the machine,
that the system is operational, or that its contents will spawn at runtime.
An unfinished particle-accelerator controller is recorded as unfinished. Exact
prototype patterns exclude similarly named circuitboards, toys and clothing.

## Findings used to author the catalog

Every default-pool map has a mapped `TegCenter`, `AmeController`, `CryoPod`,
`OperatingTable`, `MachineArtifactAnalyzer`, `MachineAnomalyGenerator`,
`ResearchAndDevelopmentServer`, `PlayerStationAi`, `ComputerSolarControl` and
`SalvageMagnet` on its main grid. TEG/AME presence describes installed starting
hardware; it does not make those the only acceptable future power-room designs.

All 10 maps have a janitor destination, heads' office destinations, EVA storage,
cryosleep, disposals, dorms and a vault destination, in addition to the earlier
catalog's broad department rooms. Surgery is explicitly marked on seven maps,
while operating tables occur on all ten; cryopod markers occur on eight while the
pods themselves occur on all ten. This illustrates why beacon names alone cannot
decide mandatory room presence or count.

No explicit `CloningPod` or `MedicalScanner` prototype is mapped in this sample.
That is an observed absence of those exact prototypes, not proof that an entire
medical capability cannot exist. The ordinary catalog should not make a cloning
suite mandatory merely because the repository contains a cloning prototype.
`ComputerBodyScanner` is a distinct mapped prototype and is not counted as the
cloning-related `MedicalScanner` machine.

Mail hardware also differs: `MailingUnit` occurs on six default-pool maps, while
`CargoMailTeleporter` and `CargoTelepad` occur on all ten. A general room program
therefore needs distinct delivery hardware and route declarations rather than a
single assumed mail-tube implementation.

## Provenance and limits

The report records SHA-256 hashes of each map and descriptor; the default pool,
beacon definitions, localization and feature-query inputs; the engine tile
serializer, variantization implementation and map migration table; and a
deterministic rollup of **every prototype YAML file** under
`Resources/Prototypes`. Re-run the
extractor after any of those inputs change. Authored catalog requirements remain
design choices informed by this evidence. Room budgets, enforced adjacency and
access policies should retain that distinction rather than copying map labels
as if they were complete generation constraints.

## Gameflow and native infrastructure evidence

`gameflow_evidence.py` samples named strategic destinations and approximate
all-doors-open floor distances on Bagel, Box, Marathon, Packed and Saltern.
`gameflow-evidence.json` records the exact source entities and limitations. The
gameflow thresholds are editable authored policy, not a claim of measured combat
balance or round outcomes.

`survey_native_systems.py` inspects native cable/pipe/disposal hardware and actual
alarm device UID lists. `write_native_profiles.py` rebuilds the profile library
from resolved native prototypes and audits node groups, directions, pipe layers
and component support. `--check` verifies the committed artifact without writing.

`build_infrastructure_defaults.py` composes the editable infrastructure fragment
from those profiles and the current room catalog. Its explicit `--apply` option
installs that policy into the two canonical room programs. The policy adds finite
initial power/gas storage and native connectivity requirements; it does not claim
to start generators or simulate ongoing station operation.

`probe_design.py` runs the actual local designer API and saves every intermediate
project. Use its `--start-at` and `--stop-after` options to inspect gameflow,
service devices, power, atmos or disposals without losing earlier geometry.
