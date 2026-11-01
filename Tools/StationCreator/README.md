# SS14 Station Creator

A local station planning GUI, starting with **topology and department dependencies**.
The first stage describes station relationships independently of their future
physical routes. Placement creates a station envelope and physical seeds;
departments grow into connected territories before internal maintenance is built.
The core is independent C# with YamlDotNet for authoring definitions; the GUI uses
ASP.NET Core and plain JavaScript.

## Start the GUI

Install the .NET 10 SDK if needed (the repository's `global.json` applies).
Double-click **[Start-Station-Creator.cmd](Start-Station-Creator.cmd)** in this folder.
It builds the tool, starts a loopback server, and opens your default browser.
Keep its terminal open; close it or press Ctrl+C to stop the server.

Alternatively, from the repository root:

```powershell
dotnet run --project Tools/StationCreator/StationCreator.csproj -- --open
```

The default address is **http://127.0.0.1:8420**. Add `--port 8421` after `--` to
use another port. No Node, Python, SS14 server, normal round, or external hosting is
needed to use the GUI. Projects stay in memory until you save them to a JSON file.

## Editable stages

| Stage | Controls |
| --- | --- |
| **1. Topology** | Hub/ring/spine; edit department links; switch to **Departments & rooms** to add/remove subdepartments, room/feature requirements, shared access origins and scoped weighted dependencies; import/export reusable YAML |
| **2. Placement** | Shape the envelope and space cuts, reserve halls/service seams, move physical seeds, pin positions, set floor targets and hard area limits |
| **3. Department growth** | Grow separated department territories; allocate subdepartments, planned room partitions, doors and internal access; edit parent territories and inspect nested overlays |
| **4. Maintenance** | Reserve substantial service space; bound deep-maintenance room, external solar and space-access counts; paint/erase routes, control department entrances, choose closed window/wall frontages |
| **5. Validate & export** | Check masks, room programs, internal reachability, maintenance-only access and external solar connections; export SVG, definition YAML or full editable project JSON |

The tool opens directly on the topology graph. **Check topology** validates
dependency rules. **Set up physical placement** creates the spatial draft.
**Generate spatial plan** completes the planning passes using your authored topology
and current edits, rather than returning to rectangle packing.
Changes invalidate downstream geometry. Invalid concepts remain editable and saveable.
The project importer recomputes checks rather than trusting a saved acceptance status.

In **Departments & rooms**, IDs form stable paths such as `Engineering/Atmos/TEG`.
Rename and removal update the relevant dependencies. Subdepartments have minimum,
target and maximum floor budgets; rooms/features have area and count bounds.
Use **Required access origins** to make a shared feature reachable from multiple
scopes. The default TEG is reachable from both Engineering and Atmos. Security
includes Armory, Perma, Sentencing, Warden's Office and Interrogation; Command
includes Bridge, Captain's Room, HoP Office and Meeting Room.

Atmos and Salvage require access entirely through their parent department.
Flexible groups prefer their parent allocation and can use a named satellite in
another compatible floor allocation when the grouping and split settings allow it.
The preview identifies a satellite's logical owner and actual host. Allocations
reserve geometry; equipment and SS14 entities are still a later stage.

Maintenance defaults to a 30% initial reservation budget, with separation between
unrelated department territories. Its final passages are 2–3 tiles wide; larger
pockets expand nearby departments or become enclosed maintenance rooms within the
configured count and floor-area bounds. Required adjacency can explicitly request a shared boundary.
Deep maints have enclosing walls and a door that requires traversing maintenance;
they do not open directly onto public halls. Solar panel reservations and catwalks
sit outside the station, clear of docking approaches, with a planned hull connection
from maintenance. Space-access frontages retain a closed window or wall barrier.
Counts are sampled deterministically within the authored bounds. A program that
does not fit returns diagnostics instead of silently dropping required instances.

Hall-connected maintenance rooms are a separate configurable type with real hall
and maintenance doorways. Their enclosed floor can resolve a service junction or
larger pocket without widening the passages. They do not count as deep maints,
because they have direct hall access. Boundary-depth checks also prevent surplus
space from being concealed as long solid wall branches instead of usable floor.

**Export definition YAML** saves the reusable configuration, topology, hierarchy
and relationships without generated cells. **Import definition YAML** starts a new
topology draft. Save project JSON when you also want to preserve manual mask edits.
See [YAML.md](YAML.md) for the schema and example definition. CLI export accepts
`--definition path/to/station.yaml` and writes JSON, SVG and YAML.

## Author topology dependencies

1. Choose **Hub**, **Ring**, or **Spine**. Templates use stable department ordering;
   seeds vary later geometry. Switching presets replaces template links while keeping
   your custom and edited dependencies. Removed template dependencies stay removed
   across preset changes. The explicit **Replace links with topology
   template** action resets the whole list; Undo restores it.
2. Click **+ Dependency**, or **Connect nodes** followed by two departments. Select
   the endpoints, then choose **Required**, **Desired**, or **Avoided**.
3. Describe the dependency's reason and choose its relationship: walking route,
   shared boundary, delivery connection, or passenger transport. Specify crew,
   patients, items/goods, or waste, and one-way or bidirectional travel.
4. Set a weight with the slider, number field, or **Low / Normal / High** presets.
   Larger weights strengthen Desired/Avoided preferences. Required dependencies stay
   mandatory at every weight. The numeric field supports 0.1–1000; the slider covers
   the common 0.1–10 range.
5. Choose permitted methods: public hall, staff passage, maintenance, disposal
   passenger shortcut, or mail. Optional distance targets describe upper route
   limits or preferred separation for Avoided links. **Never allow this relationship**
   makes an Avoided constraint mandatory.
6. Click **Add dependency** or **Apply changes**. Select any graph line or dependency
   card to edit it. Remove it with the card's × or the editor's Remove button. Search
   by department/description and filter by priority in the left panel.

Drag graph nodes to arrange the diagram; their positions are saved in project JSON.
Colors and line styles distinguish priority, line thickness represents weight, and
arrows show direction. Department names and descriptions are editable by selecting
a node. Renaming preserves its links; removing it removes its incoming/outgoing
dependencies. Undo/redo covers these operations.

Department ordering and the hub role are authored properties (`placementOrder`,
`topologyHub`). The built-in definitions provide station defaults; solvers use
these properties rather than department-name checks. Rebuild placement to apply
ordering changes, or apply a topology template to use a changed hub role. Sorting
uses authored order and then ID, so input-list order does not alter replay.

**New dependency defaults** can enable passenger disposal shortcuts or mail for new
item delivery relationships. Each dependency retains its own method choices. Mail
cannot fulfill crew/patient movement. Patient and waste requirements do not use
passenger disposal shortcuts.

The **CONCEPT CHECKED** status only confirms valid dependency rules and absence of
contradictions. A sparse dependency graph is allowed: general station circulation
is a separate requirement. Duplicate links, unknown departments, invalid weights,
incompatible methods and contradictory Required/Never allow rules are diagnosed.

Save/Open project preserves all topology fields. Existing version-1 `hard` edges
migrate to Required/Desired and receive stable IDs. Export SVG on the topology
screen saves the dependency diagram; later stages export the geometric preview.

Open [topology-workbench.json](examples/topology-workbench.json) for a topology-only
example with Required, Desired and Avoided dependencies, explanations, weights and
an item-delivery relationship permitting mail.

Disposal/mail settings are planning requirements. Physical tube/mail construction
belongs to later work. The spatial planner reports unsupported fulfillment
when a Required dependency cannot be implemented; it does not substitute an unrelated
hallway. Nonrequired dependencies use their weights as placement and boundary preferences.

## Place, grow and refine the station

**Placement:** the canvas is editing space; the envelope is the actual station
footprint. The automatic envelope follows the selected topology and department
program budget. Rectangles are only rough seed estimates. Drag a physical seed or
edit its X/Y coordinates, set its desired usable floor area, and pin it to retain
its position during automatic placement replacement. Minimum/maximum areas apply to
derived walkable department floor, not a rectangular bounding box.

Paint envelope additions or cut actual exterior space with the planning-cell tools.
Public hall and internal service reservations are visible before growth and can be
painted or erased. Whole planning cells keep the growth geometry usable. Rebuilding
placement preserves a painted envelope and pinned seeds; Reset envelope explicitly
returns to the topology silhouette. Dependency edits preserve this upstream work
while invalidating grown territories. A changed station topology rebuilds route
reservations on the next placement pass.

**Department growth:** grow connected ownership masks into the envelope around the
reserved routes. The planner budgets room cores, expands territories, and derives
separate floor, shared walls, hull and declared ports. Each default department must
fit at least one 5×5 clear floor block; area alone cannot validate a narrow ribbon.
Required room budgets enlarge protected floor cores before service reservations.
The hierarchy pass then allocates subdepartments, planned partitions, doors and
shared access routes. Assigned ownership can form L shapes, steps and wings.

Select a department and use Assign territory or Release territory to edit the
allocation. Stroke completion rederives boundaries and ports without secretly
regrowing the station. Regrow all departments explicitly replaces manual ownership.
Public halls remain outside department interiors. The graph overlay shows dependency
intent; walking occurs on the actual hall and port layers.

**Maintenance:** service seams are reserved inside the station during placement.
This pass opens and connects those spaces instead of adding a ring around the
canvas. Paint or erase maintenance, then inspect its public connections and
department service ports. Legal painted service space becomes service ownership;
erasing the walking layer leaves the underlying reservation available for revision.
Maintenance cannot overwrite department floors or create undeclared wall openings.
Secure departments default to no maintenance entrance; enabled service ports inherit
the department's declared access policy.

Passage minimum/maximum widths and deep-room minimum/maximum areas are editable
settings, included in YAML alongside count and access rules. The passage brush uses
the configured tile width and joins fast drag samples continuously. Placement keeps
its original service-reservation layer separately from the final reclaimed ownership,
so explicit regrowth can reuse the mapper's upstream reservations.

Space-facing boundaries and straight docking approaches are retained explicitly
for later window, airlock and pressure-zone work. Window/airlock entities, equipment,
utility simulation and playable-map materialization are not implemented by these
spatial passes. A GEOMETRY CHECKED result only covers the stated mask/access checks.

Scroll to zoom, drag empty space to pan, and press **F** to fit. At Placement, drag
a department to move it. **Ctrl+Z** and **Ctrl+Y** undo/redo. The graph/grid toggles
change overlays. The panel on the right shows constraint failures, travel length,
areas, score, bottlenecks, and a canonical SHA256 layout hash.

## CLI exports

Run these from **this folder** (`Tools/StationCreator`):

```powershell
dotnet run --project StationCreator.csproj -- --export examples/spatial --seed 14014 --archetype ring
dotnet run --project StationCreator.csproj -- --export examples/spatial --seed 0 --archetype hub
dotnet run --project StationCreator.csproj -- --export examples/spatial --seed 17 --archetype spine
```

Each command writes `<archetype>-<seed>.svg`, a full editable project `.json`, and
the reusable authoring definition `.yaml`.
`--config config.json` accepts a serialized `StationGenerationConfig` object (the
`config` property inside an exported project). `--seed` and `--archetype` override
that config. `--project project.json` completes a saved edited project. Failure still
writes an explicitly marked draft and exits with code 1.

Saved examples:

- [Hub, seed 0](examples/spatial/hub-0.svg), [editable project](examples/spatial/hub-0.json).
- [Ring, seed 14014](examples/spatial/ring-14014.svg), [editable project](examples/spatial/ring-14014.json).
- [Spine, seed 17](examples/spatial/spine-17.svg), [editable project](examples/spatial/spine-17.json).

Open their JSON files in the GUI to continue editing them. SVG previews include
labels for departments, circulation, maintenance, hull, doors, seed and status.
Colors and coordinates are the same in both exports.

## Verification

From the repository root:

```powershell
dotnet build Tools/StationCreator/StationCreator.csproj
dotnet test Tools/StationCreator/Tests/StationGeneration.Tests.csproj
```

The standalone NUnit runner compiles the same test source included in the existing
`Content.Tests` project, without loading/building the entire game. The tests can
also be run through the game test project with a `StationGenerationTests` filter.
No ECS integration test is needed for this independent milestone.

From this folder, run the deterministic batch command:

```powershell
dotnet run --project StationCreator.csproj -- --batch 100 --report examples/spatial/batch-validation.json
```

Add `--parallel 4` to validate independent seeds on four workers. The report stays
in original seed order and includes every attempted seed, including failures.

This attempts exactly seeds 0–99, alternating hub/ring/spine. Every failure includes
its original seed and stage; failed seeds are not replaced. A report records each
successful seed's hash/metrics and all failures. The command exits nonzero on any
failure or absent layout variance. Tests cover repeated replay and reversed input
ordering, legal widened corridors, ports/rotation, hard topology, deliberate invalid
edits, bounded infeasibility, JSON round trips and stage edits.

An optional browser-independent frontend workflow check uses Node 22+ and the local
server running on port 8420:

```powershell
node Tests/frontend-checks.mjs
node Tests/spatial-frontend-checks.mjs
node Tests/hierarchy-frontend-checks.mjs
node Tests/passage-editor-checks.mjs
```

It executes the real frontend event handlers against the C# API with a minimal DOM;
it does **not** verify browser rendering, pointer geometry or responsive screenshots.

## Implementation and scope

- `Core/`: topology, seeded spatial placement, region growth, planned boundaries,
  internal maintenance, hierarchy and room programs, YAML definitions, independent
  tile-mask validation and SVG exports.
- `Program.cs`: local API plus CLI generation/export.
- `wwwroot/`: stage editor, map canvas, project files and undo/redo;
  `topology-editor.js` implements the dependency workbench; `workbench.js` and
  `spatial-editor.js` implement placement, growing territories and maintenance;
  `hierarchy-editor.js` implements the department/subdepartment/room program.
- `RegressionChecks.cs`, `Tests/`, `Content.Tests/Shared/Procedural/Station*Tests.cs`:
  deterministic batch validation and NUnit/frontend checks.
- [DESIGN.md](DESIGN.md): repository assessment, research links, algorithm tradeoffs,
  coordinate/tile ownership contracts, hard/soft constraints and extension points.

`StationSpatialPlanner` uses a program-sized station envelope, coarse atomic
parcels, constructive room cores, topology-weighted seed placement and connected
territory growth. `StationSpatialValidator` independently inspects the resulting
fine tile masks. `workbench.js` and `spatial-editor.js` implement the five-stage GUI.
The original rectangle generator remains a legacy code/test path; it is not used
by the current GUI. CLI export can select it with `--legacy-rectangles`.

This is **not a playable station exporter**. Functional room modules, utility
networks, physical hull/atmos sealing, docking airlocks, equipment,
access simulation, job spawns and SS14 entity materialization are later milestones.
Canvas sizes are limited to 256×256 tiles and 3–16 departments. A cramped specification
can fail even when a more advanced solver might find a solution. Diagnostics expose
that bounded-search limit. Ordinary map selection and round startup are unchanged.

## Verified results in this checkout

- .NET 10 build: passed, 0 warnings / 0 errors.
- NUnit: 134 cases passed (133 in the complete suite plus the final detached-wall
  regression), including hierarchy programs, shared internal access,
  satellites, weighted relations, deep-maintenance clearance, solar/EVA geometry,
  YAML parsing, topology, spatial masks, editing, imports and deterministic replay.
- Fresh generation searches complete candidates within the authored attempt budget.
  Saved/edited plans retain their accepted placement and report unresolved constraints.
- Batch: 100/100 original seeds accepted with 100 distinct spatial layouts;
  see [report](examples/spatial/batch-validation.json). These geometric checks are
  not proof of station readiness.
- API: all five generation stages, SVG export and imported-project hash replay passed;
  GUI HTML/JavaScript/CSS served successfully.
- Frontend workflow: topology priorities/descriptions/weights, dependency CRUD,
  transport defaults, department lifecycle, undo/redo, template switching, concept
  validation, JSON persistence and downstream graph preservation passed. Additional
  checks cover graph dragging, captured-pointer selection, node connections,
  preserved template deletions and renamed dependencies across preset changes.
- Spatial editor: seed taps/drags/pins, area budgets, envelope additions/cuts,
  ownership painting, boundary refresh, service painting, one-step undo/redo,
  imported geometry and pins surviving upstream edits passed.
- Program editor: department/subdepartment/room CRUD, scoped path renaming/removal,
  imported relation methods, shared room access, program undo and YAML API round trips passed.
- Generated region previews were rendered and visually inspected as planning masks.
- Visual browser QA remains unverified: no browser is connected to the browser tool.
  The full game build/test suite was not run; the new independent tool and tests were.
