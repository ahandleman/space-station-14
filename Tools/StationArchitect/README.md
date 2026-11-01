# Station Architect

A separate room-first SS14 station planning tool. Departments are made entirely
of required, optional and filler rooms. Editable bounds, shape limits, connection
rules and split costs compile into a station → department → room requirement tree.
The original tool in `Tools/StationCreator` remains separate.

The default GUI now runs the complete staged design pipeline: room selection,
placement, room growth, halls/maintenance, gameflow, contents, service devices,
power, atmos, disposals, validation and map
export. A persistent station → owner → room template → room instance tree keeps
requirements attached to every stage. Edits retain protected geometry and mark
dependent layers stale instead of weakening constraints.

See **[DESIGNER.md](DESIGNER.md)** for the workflow, pins/locks, manual painting,
interface editing, complete project files and map bundles. Export produces actual
SS14 format-7 map drafts with a descriptor and systems manifest; operational
simulation still requires verification in SS14.

Rooms now support recursive nesting, per-parent instance counts and physical
parent access. Commons and Service can spread into smaller groups, with
stationwide room policies, adjustable packing and maintenance wrapping. See
[NESTING_AND_PLACEMENT.md](NESTING_AND_PLACEMENT.md) for the controls and
[LAYOUT_EVIDENCE.md](LAYOUT_EVIDENCE.md) for the existing-map observations.

Packing now uses room outlines to bring neighboring departments closer, with
separate controls for internal arrangements and department shape variation.
Arrivals, evacuation and cargo reserve shuttle-sized berths and clear exterior
approaches. See [DOCKING_AND_VARIATION.md](DOCKING_AND_VARIATION.md) for the
settings, editable dock rules and native shuttle measurements.

[GAMEFLOW.md](GAMEFLOW.md) describes YAML-defined chase loops, escape routes,
target balance, travel and chokepoint pressures. [INFRASTRUCTURE.md](INFRASTRUCTURE.md)
describes the native hardware passes, editable utility networks, controller UID
bindings and the boundary between connection checks and actual SS14 simulation.

An editable [generated example](examples/generated/README.md) includes the saved
requirement tree, PNG/SVG previews and map bundle. Geometry for the default ring,
hub, spine and two additional seeds is covered by the recorded
[verification results](examples/generated/verification.json), alongside core
tests, frontend/API checks and a manual browser review. The included default
example also completes the native passes and independent export audit. Gameflow
and maintenance preferences still have reported shortfalls; actual SS14 runtime
simulation remains unverified.

## Launch

Install the .NET 10 SDK, then run `Start-Station-Architect.cmd` in this directory.
It opens the local GUI at **http://127.0.0.1:8421**. From the repository root:

```powershell
dotnet run --project Tools/StationArchitect/StationArchitect.csproj -- --open
```

Use `--port 8422` to choose another local port. Stop the server with Ctrl+C in its
console. The application binds to loopback and uses its own browser origin.

## Author a program

Open **Program editor** from the designer header for the detailed room catalog.
Use **Use in station designer** to apply a program to the current project.

1. Select a department or room in the requirement tree. Edit counts, usable floor
   bounds, shape, entrances, traffic policy and descriptions in **Requirements**.
2. Add or remove departments, room templates and dependencies. Repeated rooms use
   `count.min / target / max`; filler rooms have ordinary bounded floor ownership.
   Renaming a stable ID updates references. Removing a definition keeps surviving
   references visible as diagnostics so a required rule is not silently discarded.
3. Inspect the aggregated department budgets and run checks. Required, desired and
   avoided connections have explicit target selection, optional-room behavior,
   allowed route modes, weights and distance limits. **Export requirement tree**
   saves the compiled tree, canonical connections and remaining optional choices.
4. Use **YAML source** for direct editing, then **Apply YAML**. Invalid source stays
   available for correction. **Export YAML** saves the current source; **Save
   draft** also preserves unfinished source alongside the form document.
5. Use **Growth pressures** to compare a hypothetical transfer of floor between
   room instances. It is a scalar measurement sandbox. Missing geometry and route
   evidence remain incomplete, and a lower score does not certify a station map.

The browser keeps a local draft. Use explicit exports for a portable copy.
**Default program** replaces the current document and can be undone.

The shipped program is based on fourteen station maps in this checkout,
including all ten default-pool maps. Its canonical source is
[Catalog/standard-station.yaml](Catalog/standard-station.yaml), embedded at build
time and parsed with the same strict reader as imported YAML. You can add rooms,
departments and restrictions by editing data; names do not select hidden behavior.

The program includes the usual departments plus a separate Silicon owner,
departmental heads' offices, cryopods and crew cryosleep, utility rooms, repeated
cells/dorms, service interfaces, power choices, exterior solars and maintenance.
Room machinery, environment, eligible entrance owners and source references have
GUI controls as well as YAML fields. Room choices count selected types separately
from their instance counts.

Use **Default program** to load the updated catalog if an older saved browser
draft is restored. It remains undoable. See [MAP_SURVEY.md](MAP_SURVEY.md) for
source scope and limitations, [MAP_CATALOG.md](MAP_CATALOG.md) for the authored
room policy, and [MAP_FEATURES.md](MAP_FEATURES.md) for the added requirements.
Floor ranges are generation policy informed by the maps, not a claim of exact
room-area reconstruction.

See [FORMAT.md](FORMAT.md) for the schema and precise semantics, and
[examples/station-program.yaml](examples/station-program.yaml) for the full program.
This independent schema is not an import format for the original tool's project
JSON or YAML.

## Validate a program or build a station from the command line

From the repository root:

```powershell
dotnet run --project Tools/StationArchitect/StationArchitect.csproj -- --validate Tools/StationArchitect/examples/station-program.yaml
dotnet run --project Tools/StationArchitect/StationArchitect.csproj -- --validate Tools/StationArchitect/examples/station-program.yaml --tree room-requirements.json
dotnet run --project Tools/StationArchitect/StationArchitect.csproj -- --write-default station-program.yaml
dotnet run --project Tools/StationArchitect/StationArchitect.csproj -- --build .tmp/my-station --seed 14014 --topology ring
```

Validation returns a nonzero exit code for invalid definitions. The optional tree
file includes compiled canonical connections, remaining optional presence choices
and diagnostics. A successfully compiled budget is a necessary arithmetic check,
not proof of a feasible floor plan.

## Development

The GUI and localhost API are in this directory. `Core` contains the independent
typed schema, bounded YAML reader, structural validator, requirement compiler,
pressure evaluator, persistent project tree, staged generators, physical validator
and exporters. It has no project reference to the
original generator or game ECS. `Tests` exercises the authoring contract directly.

```powershell
dotnet build Tools/StationArchitect/StationArchitect.csproj
dotnet test Tools/StationArchitect/Tests/StationArchitect.Tests.csproj
```

With the local server running, the optional
Node harnesses `Tests/frontend-checks.mjs` and `Tests/designer-frontend-checks.mjs`
exercise the real authoring/designer handlers and HTTP endpoints. These functional checks do not cover browser rendering.
`Tests/nested-authoring-checks.mjs` checks recursive definition editing, and
`Tests/nested_api_integration.py` exercises per-parent counts, complex moves and
fresh requirement reconstruction through the live API.

New departments, room families, access labels and relationships are data changes.
New kinds of enforced restrictions must also extend the typed model, compiler
and validation. Unknown YAML fields are rejected to expose misspelled rules.

The curated program explicitly uses `maintenance.connectivity.policy: controlled` with `maxComponents: 12`. This permits real service areas separated by public circulation and shuttle approaches, with configured maintenance-access transitions for every component. Explicit `maintenance` routes remain maintenance-only; the curated station-wide links also allow `controlledMaintenance`. Lower authored component caps remain hard constraints, and `continuous` retains a single maintenance-only network. Component counts, transitions and wrapping coverage are measured from actual geometry; the desired wrapping coverage remains a soft objective.
