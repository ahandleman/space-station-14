# Staged station designer

Run `Start-Station-Architect.cmd` or:

```powershell
dotnet run --project Tools/StationArchitect/StationArchitect.csproj -- --open
```

The default page at `http://127.0.0.1:8421` is the station designer. **Program
editor** opens the detailed department/room authoring interface. Its **Use in
station designer** button applies that program to the design while retaining
existing geometry for review. The designer also has a complete YAML editor.

## One requirement tree throughout

The runtime tree is station → department/maintenance owner → room template → room
instance. A template such as a detention cell or dormitory can have several
instances. Instance IDs (`Security/Cell#1`, for example) retain their parent
definition and constraints through placement, growth, routing and furnishing.
Room instances can contain further room templates and instances. For example,
`Service/Chapel#2/Confessional#1` retains a particular chapel parent. Child counts
in the inspector apply to that parent; the form editor also exposes common
template count bounds, internal-room creation, reparenting and subtree duplication.

Room floor is owned by individual room instances. Department area is the sum of
their usable floor; walls, public halls and maintenance passages are separate.
The inspector shows the local budget and lets you navigate upward to the parent
requirements. Floor transfers affect both participating rooms and their owners.
Room and parent shape/area pressures influence generation, while hard limits
remain independent of their soft weights.
The inspector distinguishes exclusive room floor from total complex floor.
Optional whole-complex budgets apply to a room and its descendants together.
See [NESTING_AND_PLACEMENT.md](NESTING_AND_PLACEMENT.md) for recursive ownership,
stationwide facilities, functional groups and the packing/maintenance controls.

Requirement trees and acceptance reports are rebuilt from the program and actual
geometry on import. A saved green report cannot bypass current requirements.
Temporarily invalid scalar edits retain the hierarchy so you can correct the next
field. Changing an ID is a definition removal/addition; use display names for a
label change, or reconcile retained stale geometry explicitly.

## Stages and editing

| Stage | Generated state and available edits |
| --- | --- |
| Program | Edit room/department bounds and the full YAML. Existing physical state remains visible and dependent stages become stale. |
| Room selection | Resolve required, preferred, optional and repeated rooms, including room-choice groups. Override template instance counts and inspect conflicts. |
| Placement | Arrange department planning regions and room seeds. Ring, hub and spine produce different arrangements and public-routing guides. Drag a room or owner to pin it; paint the allowed build envelope or cut forbidden space. |
| Room growth | Grow actual room floor, including bounded filler/circulation rooms. Paint or erase individual room tiles, set local growth targets, lock floors or unlock them for regeneration. |
| Halls & maintenance | Build room interfaces, public circulation and narrow maintenance. Paint/erase the networks; inspect, move, resize, protect or remove physical interfaces. Counters and windows do not provide walking routes. |
| Gameflow | Measure chase loops, escape routes, target travel balance and chokepoints for configured access profiles. Edit YAML-backed rules, weights and repair budgets; inspect path witnesses. |
| Contents | Place equipment families and working-floor reservations, boundary-mounted airlocks, transport hardware, and initial environment plans. Move/pin machinery and edit planned temperatures. |
| Service devices | Place native lighting, APCs, air devices and controls by editable scope and coverage rules; bind existing machinery and actual doors. |
| Power | Connect native HV, MV and LV ports and extension receivers. Inspect source paths; move, rotate or remove cable segments. |
| Atmos | Connect separate supply and scrub pipe layers and finite reserves, respecting native ports, rotations and initial gas pressure. |
| Disposals | Route units through native trunks/tubes to a real receiving trunk. Inspect or edit segments while retaining separate crew and mail networks. |
| Validate & export | Recompute independent geometry, equipment, environment and route checks. Select an issue to locate its tree node. Export JSON, SVG, YAML or a complete SS14 map-draft bundle. |

**Build station** runs the stages in order. **Run stage** rebuilds only the chosen
stage and any missing prerequisites. **Next stage** changes the view without
regenerating anything. A build can stop after its current stage.

The **Gameflow** and **Infrastructure** panels edit program requirements directly.
Utility segment edits protect the affected network; release it with **Allow
automatic rerouting** when the solver should rebuild it. See [GAMEFLOW.md](GAMEFLOW.md)
and [INFRASTRUCTURE.md](INFRASTRUCTURE.md) for the exact checks and limits.

The planning envelope is an allowed build region, not floor or atmosphere.
Unbuilt cells—including vacuum courtyards—remain space. Reserving a large region
does not turn its leftovers into broad maintenance. Maintenance passages must
retain the configured two-to-three-tile width; larger maintenance areas belong to
explicit room instances. Solar arrays remain exterior vacuum footprints, with
controlled access from interior maintenance.

## Pins, locks and retained conflicts

A seed pin preserves position; a floor lock preserves the actual cell mask.
Painted room floor is locked so an automatic rerun cannot silently erase it.
Protected interfaces and pinned equipment also survive their downstream stages.
Moving an owner translates its children and their associated geometry together.

Editing a hard requirement does not relax it to fit the current design. A locked
room below its new minimum stays visible with a violation until you enlarge it,
unlock/regenerate it, or change the authored requirement. Reducing counts or
removing definitions preserves existing floor in **Preserved stale geometry**;
remove or restore those instances explicitly before accepting a final design.

Downstream layers are retained and marked dirty/stale. This makes an edit
reviewable, but it does not certify that old routes or equipment still fit the
changed room. Rerun the affected stages or repair them manually, then validate.
Undo/redo works across stage runs, requirement edits and physical edits.

Automatic whole-station retries use the configured candidate budget and preserve
the authored seed. The selected placement attempt is saved for replay. Protected
geometry suppresses automatic resampling; the generator reports a conflict rather
than finding a different unrequested placement for a pinned room.

## Physical checks and configurable profiles

The validator reads actual floor, boundary/interface runs, equipment footprints,
working areas and network paths. It checks route mode and purpose; a service
counter or underfloor pipe cannot stand in for a crew doorway. Required
maintenance approaches also reject indirect public bypasses, including crew
transport shortcuts. See [DESIGN_VALIDATION.md](DESIGN_VALIDATION.md) for the
distinction between mandatory acceptance, soft costs and simulation.

Open serving counters can join several rooms into one sealed air compartment.
An actual opening to space, or incompatible atmosphere plans in an open
compartment, is a violation. Maintenance transitions into exterior catwalks have
physical airlock gates. Pipes and cables can pass under a wall without removing
that wall from the plan or exported map.

Advanced generation settings expose prototype footprint dimensions and offsets,
material/tile prototypes, access-tag mappings, job slots and transport profiles.
These remain editable project data. Missing physical collider profiles and access
or job mappings are reported in the export manifest. Pneumatic shortcuts and
item-only mail have separate hardware and proof rules; see [TRANSPORT.md](TRANSPORT.md).

## Saving and exports

**Save project** produces a portable `.station.json` with the definition,
selection, settings, locks and every generated layer. The browser also attempts
to retain a local draft; use explicit files for reliable storage and sharing.
Program YAML contains reusable requirements rather than generated cells.

The ZIP export contains:

- The format-7 map under `Resources/Maps/Generated/`.
- A matching `gameMap` descriptor under `Resources/Prototypes/Maps/Generated/`.
- The editable project, room-program YAML and SVG plan.
- A systems manifest with validation, unresolved materialization and operational
  review items.

The exported initial atmosphere and hardware are map planning data. The tool does
not run SS14's atmosphere, power, disposal, docking or job simulation. The manifest
therefore keeps `simulationVerified: false` and `operational: false`; test the
bundle in the map editor and a server before using it in a round.

From the repository root, generate a bundle without the browser:

```powershell
dotnet run --project Tools/StationArchitect/StationArchitect.csproj -- --build .tmp/my-station --seed 14014 --topology ring
```

Use `--program FILE` for a YAML program or `--project FILE` for an edited design.
The command retains reviewable outputs even when mandatory validation fails and
returns a nonzero exit code in that case.
Loose map and descriptor files use the same matching `Resources/…` paths as the
ZIP. Keep those paths when copying the draft into an SS14 checkout.
