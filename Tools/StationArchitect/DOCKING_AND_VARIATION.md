# Denser layouts, variation and shuttle clearances

The generation settings separate three controls:

| Control | Purpose |
| --- | --- |
| Internal layout variation | Seeded choices of room proportions, arrangement and internal circulation. |
| Department shape variation | Vary the outlines formed by the rooms and circulation. |
| Interlock department shapes | Fit neighboring groups into unused portions of their outlines while protecting entrances and circulation. |

These are saved as `settings.packing.internalVariation`, `silhouetteVariation`
and `interlock`, each between 0 and 1. The seed remains reproducible. Room,
ancestor and department limits still apply; variation does not remove required
facilities or replace their requirements. Pins, locks and manual geometry remain
protected. Public and maintenance approach reservations guide packing but do not
count as constructed hallways or verified entrances.

## Shuttle space is different from window frontage

`exterior.minSpaceFrontage` can describe a sealed window onto an internal vacuum
courtyard. A shuttle instead needs a body-sized berth and a full-width straight
approach to open exterior space. Define it under `exterior.docks` and bind it to
a room content requirement:

```yaml
exterior:
  allowSpaceExposure: true
  docks:
  - id: ArrivalsBerth
    contentId: ArrivalDockAirlocks
    berthWidth: 18
    berthDepth: 9
    berthOffset: -2
    approachLength: 16
    approachWidth: 0
    clearance: 1
    directions: [north, east, south, west]
    sourceRefs:
    - Resources/Maps/Shuttles/arrivals.yml#uid=179
contents:
- id: ArrivalDockAirlocks
  prototypes: [AirlockExternalGlassShuttleArrivals]
  count: { min: 1, target: 1, max: 1, weight: 2 }
  placement: spaceBoundary
  interfaceType: dockingPort
```

Width runs along the hull; depth runs outward from the docking boundary.
`berthOffset` shifts the centered body sideways along the tangent
`(-outward.y, outward.x)`. The berth must include its own port along that axis.
Clearance adds side and far-edge padding while allowing contact at the docking
port. An approach width of zero uses the berth width including side clearance.
Approach length is a minimum: the reserved lane extends to the canvas exterior,
and validation also checks for obstructions beyond the canvas.

The bound content controls minimum, target and maximum dock count. Each selected
dock gets its own reservation and exact hardware anchor. The geometry check
reconstructs the required masks from the authored rule; editing a saved mask or
painting a small vacuum pocket cannot establish clearance. Floor, hull, windows,
machinery, other docks, transport hardware and utilities cannot occupy the berth
or approach. The contents stage must use the specified docking prototype at the
reserved boundary and orientation.

The default profiles use the local arrivals, emergency and cargo shuttles:

| Shuttle | Width | Depth | Sideways offset | Source port UID |
| --- | ---: | ---: | ---: | ---: |
| Arrivals | 18 | 9 | -2 | 179 |
| Emergency | 21 | 11 | -1 | 6 |
| Cargo | 15 | 7 | 1 | 50 |

These are conservative body bounds before the additional one-tile clearance.
The source measurements include non-space tiles and resolved collision fixtures;
see [docking evidence](Analysis/docking-evidence.json). These profiles are authored
data, so another shuttle can have different dimensions and supported directions.
Changing a source reference does not automatically resize the profile.

## Editing and review

In **Program editor**, select a room and use **Shuttle berths & space access** to
add, remove or bind dock requirements. Counts and native prototypes are edited in
**Room contents**. Renaming a content ID updates its dock bindings; deleting it
leaves the unmet dependency visible.

The stage inspector can adjust berth dimensions and directions. Rebuild placement
after changing them. **Clearances** overlays distinguish empty berths and
approaches from built floors, and **Fit** includes those reservations when shown.
Moving a room or department carries its reservations and invalidates dependent
stages for review.

The exported manifest records docking plans and the actual bound hardware. The
map keeps those volumes as Space. These geometry and serialization checks do not
replace loading and testing docking behavior in SS14.

## Measuring the result

`Analysis/measure_design.py` reports room and materialized station bounds
separately. Its owner signatures remove translation, rotation and reflection;
the room-arrangement signature also records room types. Repositioning or rotating
an otherwise identical department therefore does not count as layout variety.
Compare the same program and seed with interlocking disabled to isolate its
packing effect, and compare several seeds to inspect actual shape variation.
