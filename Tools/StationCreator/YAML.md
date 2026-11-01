# Station authoring YAML

The YAML definition contains the station program and dependency rules. A project JSON
also contains the edited envelope, placement, grown floors and generated service
features. Importing YAML starts a new topology draft; importing project JSON restores
the geometry.

The root is `schemaVersion: 1`, `config`, and optional `topology`. Config uses the same
camelCase property names as project JSON, including `spatial`, `departments`, and
`hierarchyRelations`. Export a definition from the GUI to obtain every current field.

Each department contains `subdepartments`. Each subdepartment contains `rooms` for
enclosed rooms or reserved equipment features. IDs must be unique among siblings,
contain 1–48 letters, numbers, spaces, underscores or hyphens, and start with a letter
or number. A canonical reference joins IDs with `/`, for example:

```yaml
schemaVersion: 1
config:
  seed: 14014
  spatial:
    maintenanceFraction: 0.28
    maintenancePassageMaxWidth: 3
    maintenancePocketPreference: departments
    departmentBoundaryMaxDepth: 1
    deepMaintenanceMin: 2
    deepMaintenanceMax: 4
    deepMaintenanceRoomMinArea: 9
    deepMaintenanceRoomTargetArea: 49
    deepMaintenanceRoomMaxArea: 300
    commonMaintenanceMin: 0
    commonMaintenanceMax: 8
    commonMaintenanceRoomMinArea: 9
    commonMaintenanceRoomTargetArea: 49
    commonMaintenanceRoomMaxArea: 300
    solarMin: 2
    solarMax: 3
    spaceAccessMin: 2
    spaceAccessMax: 3
```

Omitted config properties use the current defaults. In particular, omitting
`departments` uses the full default department program. If supplied, the department
list replaces that default program; write every department you want to keep.

Each department's `placementOrder` is an integer from -10000 to 10000, default 100.
Lower values are considered first; equal values use the department ID as a stable
tie-breaker. This keeps placement independent of YAML list order. The built-in
program explicitly assigns Arrivals 0, Commons 1, Service 2, Medical 3, Science 4,
Engineering 5, Cargo 6, Security 7, and Command 8. Custom IDs use their authored
order value rather than selecting placement behavior by name.

Set a department's `topologyHub: true` to select it as the center of a hub topology
template. At most one department can be selected. If none is selected, the template
uses the first department in stable ID order. The built-in program selects Commons;
custom programs can assign this role to any department ID. The default is `false`.

`config.maintenanceWidth` is the minimum passage width (2 or 3). The independent
maximum-clear-width rule comes from `spatial.maintenancePassageMaxWidth`, default 3.
Large open pockets are reclaimed into nearby departments or enclosed bounded deep
rooms. `maintenanceFraction` budgets the initial service reservation before that
reclamation; it does not require that fraction to remain open passage floor.
Deep-room area bounds constrain usable enclosed floor as those rooms grow.
The preferred `deepMaintenanceRoomTargetArea` is clamped to those hard bounds and
can fall back toward the minimum when a larger room cannot fit.
`maintenancePocketPreference` is `departments` or `rooms`, selecting which kind of
legal expansion is attempted first. Room names and department IDs do not select
special generation behavior; their budgets, access policies and dependencies do.
`departmentBoundaryMaxDepth` limits how far department ownership may extend from
actual usable floor (Chebyshev distance, default one tile). It permits wall corners
while rejecting solid wall branches used as a substitute for useful room space.

Common maintenance rooms have a real enclosed interior, a doorway into a public
hall, and a doorway into maintenance. Their bounds use `commonMaintenanceMin/Max`
and `commonMaintenanceRoomMinArea/TargetArea/MaxArea`. They do not consume deep-room
slots because they have actual direct hall access. Route checks traverse their
floor and declared door cells. The width cap applies to passage floor; enclosed
rooms can be wider within their area bounds.

A department's `grouping` is `grouped`, `preferGrouped`, or `independent`.
`allowSplit` authorizes its subdepartments to be considered for dispersed placement.
These fields describe a placement policy; the spatial hierarchy pass reports any
policy it cannot satisfy. `requireInternalAccess` requires the subdepartment to
remain reachable from inside its parent department, without crossing public halls
or maintenance. Atmospherics and salvage use this policy by default.

A room/feature definition follows this shape inside `subdepartments[].rooms`:

```yaml
id: TEG
name: Thermoelectric generator
description: Generator and working clearance, accessible to Atmos and Engineering.
kind: feature
minCount: 1
maxCount: 1
minArea: 25
maxArea: 120
accessibleFrom:
  - Engineering
  - Engineering/Atmos
```

`minCount` and `maxCount` bound the number of instances. Zero minimum makes a feature
optional. The current planner fits minimum room counts; maximum counts cap later
optional additions. Deep maintenance, solars and EVA frontage counts start with a
deterministic preferred count and can adjust to fit within their hard minimum and
maximum bounds. Areas are usable tile
budgets. The parent budget also reserves space for
partitions and internal circulation. An impossible required program produces a
diagnostic. A `feature` reserves a footprint; functional SS14 equipment and utility
connections require a later materialization pass.

`accessibleFrom` lists required approach scopes. An empty list uses the owning
subdepartment's normal access. References can name departments, subdepartments, or
other rooms. Unknown references, self references and circular room-access
dependencies are rejected.

`config.hierarchyRelations` uses the same dependency fields as department topology,
with canonical scope paths as endpoints:

```yaml
- id: crops-to-kitchen
  from: Service/Botany
  to: Service/Kitchen
  priority: desired
  weight: 6
  description: Keep food deliveries convenient.
  relation: delivery
  purpose: items
  allowedModes: [staffPassage, mail]
  direction: both
  strictAvoidance: false
```

Priorities are `required`, `desired`, and `avoided`. Weights are greater than zero
and at most 1000. Relations are `walking`, `adjacency`, `delivery`, or `transport`;
purposes are `crew`, `items`, `patients`, or `waste`. Methods are `publicHall`,
`staffPassage`, `maintenance`, `disposalShortcut`, or `mail`. Mail only carries
items; passenger disposal shortcuts cannot carry patients or waste. A dependency
describes the intended connection; unsupported physical methods produce a planning
diagnostic rather than an assumed route.

`topology` preserves department graph nodes, edges, diagram positions and suppressed
template IDs. It is optional when authoring a fresh program; placement can create
the chosen graph template. It is included in exported definitions to preserve
custom and removed dependencies.

The importer accepts one document up to 2 MiB. It rejects unknown fields, duplicate
keys, aliases, custom YAML tags, nonfinite numbers and invalid references. Import
uses fixed data models and never constructs application types named by YAML tags.
