# Requirements added from the station maps

These schema-2 fields are optional when importing older Station Architect
programs. Export writes their defaults explicitly. They are validated and retained
in the compiled requirement tree; supplied observations are checked by the
pressure evaluator. They do not place equipment or construct networks yet.

## Machinery and useful floor

Room contents express functional equipment, rather than relying on a room name:

```yaml
contents:
  - id: CryogenicTreatment
    prototypes: [CryoPod]
    count: {min: 2, target: 2, max: 4, weight: 2}
    minArea: 4
```

Counts apply to each room instance. Several `prototypes` in one requirement are
alternatives; their total count fulfills that requirement. Matching uses exact
prototype IDs, so inheritance does not silently include unrelated machines.
Separate requirements are independent. `minArea` is a per-item allowance for
footprint and working floor, not a bounding-box or clearance-shape claim.

The sum of `count.min × minArea` raises the room's effective minimum floor and is
propagated into its department budget. It cannot exceed the room's maximum.
Observed equipment counts also reserve floor, including items above the minimum.
A small nominal room budget cannot conceal a larger mandatory equipment program.

Content `placement` is `floor` (default), `boundary`, or `spaceBoundary`. Boundary
accepts a room boundary facing either another room or space; spaceBoundary
specifically requires a space-facing edge. A shuttle airlock prototype only
satisfies a docking-entrance requirement when observed at the required space
boundary. Its presence in the middle of a room is insufficient. This expresses
the entrance requirement without inventing a shuttle department as a target.

A boundary fixture can also declare `interfaceType`, for example `airlock`. The
contents stage then creates the actual physical opening and the validator checks
that the materialized hardware covers it. Empty `interfaceType` makes no opening:
a wall-mounted machine can coexist with its structural wall. Underfloor utility
interfaces also preserve the wall instead of creating an air leak.

The independent core validates identifiers and constraints. The developer catalog
audit additionally resolves actual prototypes in this repository. A custom
server can provide different prototype IDs without rebuilding the core model.

## Choices between room types

```yaml
choices:
  - id: MainPowerSystems
    description: Permit combined power systems without demanding every option.
    rooms: [Engineering/TEG, Engineering/AME, Engineering/Containment]
    count: {min: 1, target: 2, max: 3, weight: 4}
```

This counts distinct active room templates, not their number of instances. Three
copies of one candidate still count as one selection. Candidate rooms keep their
own instance counts, contents and conditions. A group can span departments, and
room paths can participate in more than one group. The compiler checks required
selections against candidate availability and necessary floor budgets. It does
not claim to solve every combination of overlapping groups and route constraints.

In particular, the mapped presence of several generator prototypes does not
prove that they are simultaneously operational or in separate chambers. The
catalog's choices are configurable generation policy informed by those maps.

## Environment and direct room entrances

```yaml
environment:
  pressure: vacuum
  placement: exterior
  sealed: false
```

Pressure categories are `standard`, `low`, `vacuum` and `custom`. Placement is
`interior`, `exterior` or `either`; `sealed: true` requires an airtight air-connected compartment. Several rooms can
share one through an open serving counter; a counter opening into space does not
provide a seal.
Defaults are standard pressure, interior placement and sealing. The scalar
evaluator checks supplied categories. Optional `minTemperatureKelvin` and
`maxTemperatureKelvin` provide numeric temperature bounds; absent bounds impose no
temperature requirement. A configured bound needs an actual temperature
observation for final evaluation. The food cold store uses a maximum of 273.15 K
as an authored design goal.

These fields do not prescribe gas species, pressure in kPa, pipenet construction
or airlock sequencing; those need later atmosphere and utility specifications.

Open solar arrays can therefore be exterior, vacuum and unsealed, while their
control room is interior and sealed. An external location is not merely an
interior room with a space-facing wall.

```yaml
access:
  level: maintenance
  allowPublicEntry: false
  allowMaintenanceEntry: true
  entryOwners: [_maintenance]
```

`entryOwners` restricts direct doors into other room owners. Empty permits any
declared owner. Public hall and maintenance passage entrances use their separate
flags. `_maintenance` identifies true maintenance room ownership. This expresses
the direct-entry restriction for a deep maintenance room without checking for a
special room name. To enforce indirect access too, set
`requireNetworkApproach: [maintenance]`. Each listed network must occur on every
external crew approach. The designer checks the actual physical graph with that
network removed; public or foreign-owner floor must not still reach the room.
Crew disposal shortcuts also count as possible bypasses; item-only mail does not.
A required maintenance connection independently ensures that the room is reachable.

## Walkable, service and utility interfaces

```yaml
id: KitchenServingCounter
from: Service/Kitchen
to: [Service/Dining]
selection: all
priority: required
presence: ifPresent
weight: 8
kind: interaction
modes: [direct]
interfaceTypes: [counter, window]
minWidth: 2
description: Serve customers across a shared boundary without a crew shortcut.
```

Connection `kind` is `walking` (default), `interaction` or `utility`. An
interaction is a direct shared interface, not a walking path. Utility requirements
can describe pipe or cable interfaces independently of crew circulation.
`interfaceTypes` allows `doorway`, `airlock`, `counter`, `window`, `dockingPort`,
`pipe` and `cable`. An empty list leaves type unspecified; `minWidth: 0` leaves
width unspecified. Interface width is measured in tiles.

Counters, windows, pipes and cables cannot fulfill walking requirements. Declaring
a counter never establishes a crew route through a kitchen. Docking and solar
airlock connections can require their own interface while remaining distinct from
the equipment and exterior-placement declarations. A utility requirement does
not certify that a working power grid, atmosphere system or shuttle dock exists.

## Sources and incomplete observations

Station, department and room declarations accept `sourceRefs`, for example:

```yaml
sourceRefs:
  - Resources/Maps/box.yml#uid=123
  - Resources/Prototypes/Entities/Structures/Machines/example.yml#id=ExampleMachine
```

The syntax above illustrates the reference format; replace it with actual source
paths and IDs. Sources are bounded provenance text, not instructions or file
includes. The core does not open them. The standard catalog audit verifies its
references against this checkout and records prototype sources and map hashes.

Final evaluation requires observations for a room's environment and configured
equipment or entrance restrictions, and for constrained connection interfaces.
Missing observations leave growth incomplete and make final validation fail.
An omitted observation is not treated as proof that a constraint passed. Hard
limits remain independent of weights, and the geometry engine must eventually
derive these observations from actual cells, entities, entrances and routes.
