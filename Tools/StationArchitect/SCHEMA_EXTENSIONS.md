# Encoding features found in station maps

These optional schema-2 fields distinguish a functional station program from a
list of room names. They are independent of map-specific department IDs.
The [live designer](DESIGNER.md) consumes them throughout its editable stages;
the scalar evaluator can also inspect supplied snapshots independently. The
program is authoritative, while project JSON additionally stores selected room
instances, geometry, pins and stage state. See [DESIGN_VALIDATION.md](DESIGN_VALIDATION.md)
for checks derived from actual generated or edited geometry.

Recursive `rooms`, `nesting`, `placement`, optional `complexArea` and
`complexShape` now describe physical room complexes and distributed ownership.
Their count, floor and reference semantics are documented in
[NESTING_AND_PLACEMENT.md](NESTING_AND_PLACEMENT.md), with concrete existing-map
geometry in [LAYOUT_EVIDENCE.md](LAYOUT_EVIDENCE.md).

## Sources

The station, each department and each room can carry `sourceRefs`. References
identify repository files or particular source entities, for example
`Resources/Maps/box.yml#uid=1234`. They explain an authored decision; they are not
conditions and do not prove that a room boundary or exact floor area was measured.
The pure core bounds and preserves these references without opening arbitrary
files. The catalog audit checks them against repository evidence separately.

## Functional contents and reserved floor

```yaml
contents:
  - id: ChemistryDispenser
    prototypes: [ChemDispenser]
    count: { min: 1, target: 1, max: 2, weight: 1 }
    minArea: 4
    placement: floor
```

Each equipment family accepts any of its listed entity prototypes. Counts are
aggregated across these alternatives. `minArea` is reserved usable floor per
equipment instance, including its intended footprint and working clearance. It
is an authored planning allowance, not a measurement automatically extracted
from a prototype sprite. Reservations for separate equipment requirements add.

`placement` is `floor`, `boundary` or `spaceBoundary`. A docking airlock can
require `spaceBoundary`, so merely placing that prototype inside the room cannot
fulfill its condition. `boundary` accepts either an internal boundary or a space
boundary; `spaceBoundary` specifically requires the latter. These conditions
need actual placement observations, not just a prototype count.

An optional `interfaceType`, such as `airlock`, additionally requires an actual
associated boundary interface. The designer checks its endpoint contacts, kind,
width and equipment coverage; copying the requirement text into an entity does
not satisfy it. Pipes and cables are overlays and keep the structural wall under
them. Configurable design-project footprint profiles supply width, height and
offsets, rotated around the entity origin. Missing profiles use an explicitly
unverified one-tile planning footprint. Working-floor reservations are checked
against actual room floor and other equipment.

The effective minimum room floor is the greater of its authored area minimum and
the sum of mandatory equipment reservations. This minimum feeds the department
budget. Required contents that cannot fit the room maximum are rejected. Actual
equipment observations additionally cannot reserve more floor than the measured
room owns. A missing equipment observation leaves growth incomplete and fails
final evaluation rather than pretending machinery was placed.

Equipment requirements support counts through 100,000; room instance counts
remain limited to 64. Prototype existence is verified against local repository
data by the catalog audit, not by a game-ECS dependency in the core.

## Room alternatives

```yaml
choices:
  - id: PrimaryPower
    description: Permit combined power arrangements without demanding every option.
    rooms: [Engineering/TEG, Engineering/AME, Engineering/ContainmentEngine]
    count: { min: 1, target: 2, max: 3, weight: 5 }
```

This counts distinct active room templates, not their repeated instances. Each
member normally has `count.min: 0`. Choosing two TEG instances still activates
one template. The example permits several power families together. If changed to
a maximum of one, it would exclude selecting a second family without removing
that family's definition from the catalog.

The compiler checks canonical references, mandatory member conflicts, viable
alternatives and obvious owner-floor contradictions. If every remaining
alternative must be present to satisfy a choice minimum, it raises those
effective room minima without changing authored counts. Unresolved alternatives
remain in the compiled `choices` list. These bounded arithmetic checks do not
solve all overlapping room-choice combinations or prove geometric feasibility.
The pressure evaluator scores the actual selected-template count and enforces
its maximum even during growth; its minimum becomes hard in final evaluation.

## Environment and exclusive owner entries

```yaml
environment:
  pressure: vacuum
  placement: exterior
  sealed: false
access:
  level: maintenance
  allowPublicEntry: false
  allowMaintenanceEntry: true
  entryOwners: [_maintenance]
  requireNetworkApproach: [maintenance]
```

Pressure is `standard`, `low`, `vacuum` or `custom`; these are planning classes,
not gas-mixture or numerical pressure simulations. Placement is `interior`,
`exterior` or `either`. A final observation must state an actual interior or
exterior placement. `sealed: true` requires sealing; `false` permits an unsealed
area such as an external solar platform. It does not require a seal to be absent.

The contents stage creates explicit initial atmosphere assignments, and the map
draft exports them where composition and pressure are specified. Sealing is
derived from the actual air-connected compartment: an open counter can join two
rooms inside one sealed compartment, but a counter facing Space cannot seal it.
Closed doors and windows separate compartments. Incompatible initial pressure or
temperature assignments in an open compartment are reported. A `custom` category
does not invent an unspecified gas mixture. None of these plans certify a running
SS14 atmosphere simulation.

Optional `minTemperatureKelvin` and `maxTemperatureKelvin` set numeric temperature
bounds independently of pressure class. Values must be finite, between zero and
100,000 Kelvin, and ordered when both are supplied. A freezer can, for example,
require `maxTemperatureKelvin: 273.15`. Its observation must provide an actual
`temperatureKelvin`; absent temperature observations remain pending during
growth and fail final evaluation. Out-of-range temperature also remains pending
while growing and becomes a hard final violation. The observation is a separate
`RoomEnvironmentObservation`, not a pair of claimed requirement bounds.

`entryOwners` restricts direct room-to-room entrances to named department owners.
An empty list permits any declared owner. Public-hall and maintenance-passage
entrances remain governed by their separate flags. A deep-maintenance room can
therefore exclude direct departmental doors while requiring maintenance network
access through an ordinary connection rule. Unknown owners are rejected, and a
restricted room requires explicit entrance observations for final validation.

`requireNetworkApproach` independently restricts indirect access. Every listed
network (`publicHall`, `maintenance`, or both) must occur on every approach from
public or another owner's station floor. The geometry validator removes each
required network's floor and searches actual walking interfaces and verified
crew-tube jumps for a bypass. This catches a public common-room entrance or a
shortcut into a neighboring room that would otherwise bypass deep maintenance.
`RoomMeasurement.VerifiedApproachNetworks` is an observation derived from that
graph; missing configured observations fail final evaluation. The required-route
rule separately checks that the room can actually be reached.

Room environment observations are also mandatory for final evaluation. Growth
can retain an unfinished environment while reporting that it is incomplete.

## Walking, interaction and utility connections

```yaml
kind: interaction
modes: [direct]
interfaceTypes: [counter, window]
minWidth: 2
```

Connection `kind` is `walking`, `interaction` or `utility`. Walking declares a
crew-route requirement. A kitchen serving counter, chemistry hatch or public Cargo counter
can supply an interaction without making a secure workspace a public hallway.
Interaction interfaces must be direct. Utility connections separately describe
relationships such as a pipe or cable interface.

`utilityNetwork` is an explicit utility-only mode. Its planning route may use actual
room/public/maintenance floor and supported boundary cells. Once the corresponding
native power or atmos stage runs, fulfillment and distance come from fresh native
port contacts with verified initial source/sink reachability, rather than the saved
planning line. Before that stage the line remains unfulfilled and is explicitly
reported as pending; final acceptance requires the actual native evidence.
It grants no crew access and cannot fulfill a walking or interaction
rule. Existing `department` and `maintenance` utility modes retain their spatial
restrictions; their inspector searches within those restrictions for an actual
simple witness instead of rejecting only one unconstrained shortest path.

The default solar walking link explicitly allows `maintenance` or
`controlledMaintenance`: an array retains a real maintenance entrance, while
configured authorized transitions may join separate maintenance regions. Its
separate required cable link uses `utilityNetwork`. User-authored strict
maintenance-only links are not reinterpreted. A connected native circuit and an
explicit initial battery/gas reserve do not verify sustained generation or SS14
runtime behavior.

`interfaceTypes` can contain `doorway`, `airlock`, `counter`, `window`,
`dockingPort`, `pipe` or `cable`. An empty list leaves the interface unconstrained.
`minWidth: 0` leaves width unconstrained; a positive value requires a measurement.
A walking definition needs at least one traversable interface alternative, and
an observed counter, window, pipe or cable cannot satisfy walking. An interaction
or utility observation cannot satisfy a walking rule with the same endpoints.
Missing configured interface observations remain incomplete during growth and
fail final checks. The live circulation and contents stages construct physical
interfaces, equipment and utility paths from these declarations; compilation of
YAML alone does not construct them. Mail and passenger shortcuts use separate,
configurable point-to-point pneumatic hardware checked by [transport validation](TRANSPORT.md).
Their underfloor segments do not count as walking through intermediate rooms,
and mail cannot substitute for a crew route. Map drafts still need in-engine
verification of power delivery, pneumatic operation, docking, hazards and access.
Crew-capable hardware remains relevant to access-bypass checks even when its
authored connection is a utility link; relabeling a link does not stop a crew
member from physically using its endpoint unit.

Native disposal profiles also declare `carriesCrew` (null/omitted uses the native
unit/filter facts). Fresh, powered, directionally valid waste delivery contributes
forward-only edges to deep-maintenance and nested-room bypass checks; item-only
mail does not. Every possible native disposal branch must terminate safely before
its crew edge is observed.

Gameflow's optional `allowCrewTubes` currently evaluates configured passenger
transport networks. Native waste travel is included in the accessibility proofs
above and is not yet a gameflow travel shortcut. Default gameflow profiles exclude
crew tubes, so their distance and loop observations do not claim that capability.
