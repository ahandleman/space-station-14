# Station Architect room-first program

Station Architect is a separate tool. It has its own `schemaVersion: 2` authoring
format and requirement model, with no dependency on the original Station Creator's
department envelopes, subdepartment allocations or saved geometry.

This document describes the authoritative authoring program and its scalar
requirement evaluator. The live [station designer](DESIGNER.md) uses that program
through selection, placement, room growth, circulation, contents and validation,
and exports SS14 format-7 map drafts. Project JSON also saves selected instances,
geometry, pins, edits and stage status; program YAML remains reusable requirement
data. The full starting program is
[examples/station-program.yaml](examples/station-program.yaml). Exported maps still
need in-engine review: SS14 atmosphere, power, access, machinery and hazard
operation have not been verified by this editor.

## Ownership and units

The requirement tree links **station, department, room template and room
instances, recursively through nested room templates and their instances**. The designer maintains this tree throughout the stages and rebuilds
requirements from the current program when geometry or definitions change.
True maintenance rooms have their own `_maintenance` owner alongside
departments. A room template may request several instances through its `count`
bounds. Each instance has its own area, shape, access and connection requirements.
Nested `rooms` apply their count bounds separately to every parent instance.
Canonical paths may have any supported depth, such as
`Service/Chapel/Confessional`; runtime IDs include each particular parent, such as
`Service/Chapel#2/Confessional#1`. The bounded reader supports up to 12 room levels
and 4,096 room templates. See [NESTING_AND_PLACEMENT.md](NESTING_AND_PLACEMENT.md).

A department consists entirely of its rooms. It has no anonymous floor left over
after allocating its important rooms. Filler rooms are explicitly defined usable
rooms that can take up space and support movement. They have the same count, size
and shape limits as activity rooms. `role: filler` does not waive any restrictions.

Areas count **usable floor tiles**, excluding wall tiles. A department's area is
the sum of its owned room-instance areas. Shared public halls, maintenance passages
and other departments' floors do not count toward that total. Rooms that lie on
opposite sides of a public hallway still belong to the same department, but make
it spatially split. Public halls are their own network rather than filler rooms
silently added to a department.

A parent room's `area` is its exclusive floor. Optional `complexArea` constrains
its own plus all descendant usable floor, counted once. Parent shape uses the
actual complex footprint and internal structural seams; `complexShape` can
override that limit. `placement.policy` (`grouped`, `distributed`, `stationwide`)
controls location without changing ownership or dropping ancestor requirements.

Maintenance passages are a distinct network, with width between two and three
tiles. True maintenance rooms may be larger because they are bounded rooms with
their own definitions. `maintenance.area` budgets true maintenance **room floor
only**; passage area is not included in that budget.

## Document structure

```yaml
schemaVersion: 2
id: ExampleStation
name: Example station
description: An author-defined station program.
settings:
  roomPressureWeight: 1
  departmentPressureWeight: 1.5
  connectionPressureWeight: 2
departments:
  - id: Service
    name: Service
    description: Civilian services.
    color: "#9cb86a"
    tags: [civilian]
    area: { min: 80, target: 160, max: 400, weight: 1 }
    shape: { maxAspectRatio: 3, minFillRatio: 0.35, weight: 2 }
    split:
      allow: true
      maxComponents: 3
      componentPenalty: 1500
      hallCrossingPenalty: 2500
      maintenanceCrossingPenalty: 2000
    rooms:
      - id: Kitchen
        name: Kitchen
        description: Food preparation and serving.
        role: activity
        tags: [food]
        count: { min: 1, target: 1, max: 1, weight: 2 }
        area: { min: 35, target: 60, max: 90, weight: 1 }
        shape: { maxAspectRatio: 2.5, minFillRatio: 0.6, weight: 1 }
        allowThroughTraffic: false
        access:
          level: service
          allowPublicEntry: false
          allowMaintenanceEntry: false
        exterior: { minSpaceFrontage: 0, allowSpaceExposure: true }
        connections:
          - id: OptionalCropDelivery
            to: [Botany]
            selection: all
            priority: desired
            weight: 12
            presence: ifPresent
            modes: [direct, department]
            maxDistance: 18
            description: Prefer a short internal crop route when Botany is selected.
      - id: Botany
        name: Botany
        role: activity
        count: { min: 0, target: 1, max: 1, weight: 2 }
        area: { min: 35, target: 60, max: 120, weight: 1 }
      - id: ServiceCirculation
        name: Service circulation
        description: Real service-owned working and circulation rooms.
        role: filler
        count: { min: 1, target: 2, max: 4, weight: 0.5 }
        area: { min: 12, target: 20, max: 60, weight: 0.5 }
        shape: { maxAspectRatio: 5, minFillRatio: 0.45, weight: 0.75 }
        allowThroughTraffic: true
        access:
          level: service
          allowPublicEntry: true
          allowMaintenanceEntry: true
connections: []
maintenance:
  passageWidth: { min: 2, max: 3 }
  area: { min: 0, target: 0, max: 100, weight: 1 }
  rooms: []
```

Omitted optional properties receive their typed defaults. Lists that are supplied
are complete replacement lists; an empty room list does not fetch hidden rooms
from the starting program. The exporter writes every field so a saved program is
easy to inspect and tune. `schemaVersion`, `id` and `departments` are required root
fields. A program must contain at least one department, each with at least one room
template.

IDs are case-sensitive, 1–64 characters, start with a letter or number, and then
use letters, numbers, spaces, underscores or hyphens. A department ID is unique
station-wide; room IDs are unique among siblings under the same owner or parent
room template. `_maintenance` is a reserved
owner, not a user department ID. Display names can change without changing IDs.

## Required, optional and filler rooms

Room presence is represented by a bounded count rather than separate overlapping
booleans:

| Count | Meaning |
| --- | --- |
| `min: 1, target: 1, max: 1` | One mandatory room. |
| `min: 2, target: 3, max: 5` | At least two instances, preferably three, at most five. |
| `min: 0, target: 1, max: 1` | An optional room with a preference to include it. |
| `min: 0, target: 0, max: 4` | Available when needed, without an inclusion preference. |
| `min: 0, target: 0, max: 0` | Disabled template; references remain available for editing. |

`role` is `activity` or `filler`. Both own real floor. A required circulation room
can use `role: filler` and `count.min: 1`; optional fillers can have a zero minimum.
`allowThroughTraffic` explicitly authorizes routing *through* a room to another
destination. Being a source or destination does not make that room a general
thoroughfare. An armory or private office can therefore be connected without
becoming a shortcut between unrelated rooms.

Use tags such as `atmos`, `food` or `culture` for authoring organization. They are
labels, not hidden generation rules. Atmos and TEG are rooms under Engineering;
there is no required intermediate rectangular Atmos container. If a collection
needs a real spatial restriction, express it with explicit connections and bounds.

## Area, shape and split requirements

`area` and `count` use ordered `min ≤ target ≤ max` bounds. Minima and maxima are
hard limits for a finished layout; the target creates a soft pressure. The weight
scales that target pressure. A zero weight removes a soft preference without
disabling its hard bounds. Counts are integers from 0 to 64; room area minima are
positive, with a maximum supported area of 1,000,000 tiles.

The compiler aggregates room count and area bounds into the parent department,
then intersects them with its own area bounds. A department cannot have a maximum
below its mandatory room minimum, or demand a minimum above all available room
capacity. Optional room choices can tighten those bounds when required links
activate them. Passing these arithmetic checks is a necessary condition; it is
not proof that a geometrically valid arrangement exists.

`shape.maxAspectRatio` limits how oblong a room or department can become:

```text
aspect ratio = longer axis-aligned bounding-box side / shorter side
fill ratio   = owned usable floor / axis-aligned bounding-box area
```

For example, a 3×12 floor has aspect ratio 4; it violates a maximum of 2.5. A thin
snake folded into a square may have a good aspect ratio but a low fill ratio.
`minFillRatio` prevents that loophole. Neither condition requires rooms to be
rectangles. Shape weight scales a preference for compact floor and a cost for
fill deficits; the limits remain final constraints. Department shape measures the union of its own
room floors, so hallway gaps do not count as owned floor.

`split.allow: false` requires one department component. If splitting is allowed,
`maxComponents` caps the total. Additional components, public hall crossings and
maintenance crossings carry their configured penalty costs. These costs are
separate from ordinary area pressure; a flexible department can split to resolve
other constraints, while a high cost makes that an expensive choice. The starting
program uses 1,500 per extra component, 2,500 per public hall crossing and 2,000 per
maintenance crossing. Security permits costly splitting for distributed
checkpoints; Command retains its no-split policy in the shipped catalog.

## Connections and optional targets

Connections can be declared at the station root or inside a room. At the root,
`from` is required. Inside a room, omitting `from` infers that owning room. Full
references are `Department/Room[/Child/...]`, or a department ID for department-level routing.
Within a room's local connection list, a bare room ID resolves to a sibling first,
then an immediate child. `..` refers to the parent, `./Child` explicitly to a child,
and `../Sibling` or additional parent steps reach other local scopes. Local nested
references stay within the corresponding common ancestor instance.
True maintenance room references are `_maintenance/DeepStore`, for example.
References resolve to IDs, never to display names or tags.

Each connection applies **from each existing source instance**, and its target
templates denote available target instances. It requires reaching a suitable
target instance rather than connecting every instance pair. A connection has:

| Field | Meaning |
| --- | --- |
| `to` | One or more explicit target paths. |
| `selection: all` | Apply the rule to every listed target template. |
| `selection: any` | At least one listed target template can satisfy the rule. This is not an exclusive choice. |
| `priority: required` | A hard final route requirement for an active rule. |
| `priority: desired` | A soft route preference, scaled by `weight`. |
| `priority: avoided` | A soft cost for the specified proximity or connection. |
| `presence: ifPresent` | Evaluate existing targets; absent optional templates do not force a room to appear. If all targets are absent, skip the rule. |
| `presence: requireTarget` | When the source exists, require target presence according to `selection` for a required rule. |
| `modes` | Allowed ways to satisfy the route; at least one allowed mode must work. `controlledMaintenance` is an explicit crew route with maintenance legs and authorized transitions; `utilityNetwork` is an explicit utility-only native circuit. Strict `maintenance` retains its meaning. |
| `kind` | `walking`, `interaction` or `utility`; a nonwalking interface cannot fulfill a crew-route requirement. |
| `interfaceTypes` | Optional allowed physical interfaces, such as doorway, airlock, counter, window, pipe or cable. |
| `minWidth` | Required measured interface width in tiles; zero leaves width unspecified. |
| `maxDistance` | Optional route-length ceiling for required links, or preferred distance for soft rules. |
| `description` | The design reason, shown to the author. |

An absent optional source never activates its outgoing rules. `ifPresent` makes
references to optional rooms safe without silently turning them mandatory. To
make a room choice mandatory, use a required `requireTarget` rule from a mandatory
source with `selection: any`. For example:

```yaml
id: AtLeastOneColdSupportRoom
from: Medical/Treatment
to: [Medical/Morgue, Medical/Cryogenics]
selection: any
priority: required
presence: requireTarget
weight: 10
modes: [department]
description: Treatment requires at least one of the optional cold-support rooms.
```

This demands at least one room from that pair. It does not forbid both, and it
does not prescribe which one to select. Their counts, area pressure and remaining
constraints help choose. A required `all` rule with `requireTarget` would activate
both instead. With `ifPresent`, neither would be forced to appear.

Supported route declarations are:

| Mode | Required route interpretation |
| --- | --- |
| `direct` | A direct room-to-room physical interface; its kind determines whether it carries crew movement. |
| `department` | Reachability wholly through rooms of the same department; no public hall or maintenance detour. |
| `publicHall` | A route through the public hallway network. |
| `maintenance` | A route through the maintenance passage network. |
| `disposalShortcut` | A passenger disposal shortcut network. |
| `mail` | A mail delivery network. |

The circulation stage builds and validates these routes from actual floor,
interfaces and network hardware. Passenger shortcuts and mail use configurable,
separate point-to-point pneumatic networks; mail cannot fulfill a walking rule.
Their physical pipe exits, rotations and endpoint units are checked, while
operating power and pneumatic behavior remain subject to in-engine verification.
A public hall does not silently replace an internal-only or maintenance-only
requirement. See [TRANSPORT.md](TRANSPORT.md) for hardware profiles and limits.

Access labels such as `engineering`, `armory` and `command` are author-defined.
`allowPublicEntry` and `allowMaintenanceEntry` govern entrance eligibility. They
grant permission; they do not demand an entrance. A necessary entrance or route
should also have a connection requirement. `allowThroughTraffic` is independent
of access level. These policy fields remain attached to runtime rooms. Map export
uses configurable access-label mappings (`settings.export.accessTags` and
`ownerAccessTags` in the design project) rather than choosing behavior from a room
name. Unmapped labels are reported for review.

A `publicHall` or `maintenance` route can reach a private room through its
department's circulation and an eligible gateway room. The route mode does not
imply that each endpoint has a direct doorway into that network. Direct-entry
restrictions are checked against the room's actual entrance measurements. A
`department` route must stay within one owner's rooms; a required cross-owner
connection cannot be satisfied using only that mode.

## Space frontage and maintenance

`exterior.minSpaceFrontage` measures a minimum number of space-facing boundary
edges. A space-facing boundary remains sealed by walls or windows; it is not an
open floor tile exposed to vacuum. `allowSpaceExposure: false` forbids that
frontage and therefore requires a zero minimum. Growth reserves actual Space-tile
pockets, circulation preserves them, and validation measures the resulting
boundaries. Enclosed unplated courtyards are Space too; envelope membership does
not secretly create floor.

`exterior.docks` adds shuttle-specific empty volumes. Each rule has a unique
`id`, a `contentId` bound to a `spaceBoundary` / `dockingPort` content requirement,
`berthWidth`, `berthDepth`, signed `berthOffset`, `clearance`, `approachLength`,
`approachWidth` (zero means padded berth width), allowed cardinal `directions`
and optional `sourceRefs`. The content's count controls dock multiplicity.
Validation reconstructs berth and straight exterior approach masks from the rule
and checks actual geometry and aligned hardware. Ordinary frontage or a small
vacuum pocket cannot satisfy a docking rule. See
[DOCKING_AND_VARIATION.md](DOCKING_AND_VARIATION.md) for coordinates and examples.

Each true maintenance room has the same room schema as a departmental room.
`allowPublicEntry: false`, `allowMaintenanceEntry: true`,
`entryOwners: [_maintenance]`, `requireNetworkApproach: [maintenance]` and a
required maintenance connection express deep-maintenance access. The direct-entry
policy excludes departmental doors. The approach rule additionally rejects
indirect bypasses: removing maintenance from the physical walking graph must
leave no approach from public or another owner's floor. Verified crew-tube jumps
are included, so a shortcut into a neighboring common room cannot bypass the rule.
An isolated room still needs its required route; absence of a bypass alone does
not prove reachability.

The shipped program includes bounded workshop, deep store, breakroom, exterior
access, solar control and exterior solar-array families. The contents stage places
their configured equipment and actual boundary airlocks. The exporter writes
those entities into a map draft; it does not certify functioning docking,
solar-power delivery or an operating atmosphere system.

The starting program also declares a maintenance-only backbone between all ten
departments. It is authored connection data and can be replaced with different
links. There is no special department-name check in the requirement compiler.

## Local and parent pressure

The compiled tree retains both room and department requirements. Room growth
compares tentative floor transfers using their effect on the donor room, the
recipient room and both owning department totals. Moving floor between two rooms
of the same department changes local pressure while leaving the parent area
unchanged. Moving floor across department ownership changes both parent totals.

`settings.roomPressureWeight`, `departmentPressureWeight` and
`connectionPressureWeight` set the relative levels. A room close to its maximum
should resist gaining tiles; a department that is too small can exert pressure
for its rooms to grow. Fillers give the solver a named, bounded place to put useful
space. They cannot absorb unlimited floor or hide a failure to satisfy mandatory
room budgets.

The current area and count cost is
`weight × ((measured − target) / max(1, target))²`, multiplied by the corresponding
room or department pressure setting. The shape cost is
`weight × ((aspectRatio − 1)² + max(0, minFillRatio − fillRatio)²)` at the same level.
Split costs are linear in additional components and crossings. Connection costs
are scaled separately by `connectionPressureWeight`. A hard violation remains a
violation even when its soft weight is zero.

Partial growth must be distinguished from final validation. A seed naturally has
less floor than its minimum, so the growth evaluator can measure the deficit and
prefer progress without treating every incomplete seed as a finished invalid
map. Partial shape deficits also remain visible costs while a room is growing.
Hard area/count maxima, individual room connectivity, split limits, forbidden
entrances/transit and malformed metrics still reject illegal transfers. Final
validation additionally enforces minima, shape limits, space frontage and
completed required route/presence requirements.

The pressure evaluator compares complete before/after **measurement snapshots**,
including donor, recipient and parent totals derived from the room areas. It
returns a cost difference and hard-validity result, without mutating the program.
A growth search can use a negative difference as an improvement; it can choose to
accept a positive difference to escape a local minimum. The API does not itself
move tiles, infer adjacency or prove supplied route measurements from geometry.

The scalar evaluator remains usable on supplied snapshots and in the pressure
sandbox. The live design pipeline separately grows real masks, resolves physical
ownership, builds networks and equipment, and derives observations through the
[geometry validator](DESIGN_VALIDATION.md). A scalar score alone does not prove
physical validity. Final design acceptance requires every mandatory geometry
check to pass; unmet or unproven Desired/Avoided preferences retain their costs
and a separate information message. In-engine operational verification remains
outside that geometry acceptance.

## Starting catalog

The default is the editable, map-based
[Catalog/standard-station.yaml](Catalog/standard-station.yaml), embedded at build
time and parsed with the same reader as imported programs. The full exported
example is [examples/station-program.yaml](examples/station-program.yaml).

The survey covers fourteen station maps, including all ten in the default map
pool. See [MAP_SURVEY.md](MAP_SURVEY.md) for scope, evidence, limitations and
reproduction commands, and [MAP_CATALOG.md](MAP_CATALOG.md) for room-policy choices.
Ownership labels are configurable authoring data; Silicon is a separate owner in
this program, with Robotics retained in Science.

The schema now also represents bounded equipment contents, room-selection
cardinality, environment and exterior placement, eligible entrance owners and
walking/interaction/utility interfaces. See [MAP_FEATURES.md](MAP_FEATURES.md) for
field definitions and examples. These requirements are carried into the runtime
tree and checked against observations derived from the generated or edited
geometry. Compiling YAML alone remains an arithmetic/reference check, not a
geometry-generation command.

## Validation and extension

Unknown fields are rejected so a misspelled restriction cannot appear to work.
The reader also rejects duplicate keys, unsupported schema versions, aliases,
anchors, explicit YAML tags, invalid/nonfinite numbers and dangling references.
Inputs are bounded to 2 MiB, 100,000 parser events and depth 40.

Adding departments, room templates, instances, tags, access labels, areas, shapes,
costs and dependencies is data-only. Adding a new *kind* of enforced restriction
requires a typed model field, validation, requirement-tree support and
corresponding observation and geometry validation. Free-form tags and descriptions are not substitutes for an
enforced rule. This keeps extension explicit and prevents constraints from being
silently ignored.
