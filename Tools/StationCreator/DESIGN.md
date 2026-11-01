# Station Creator design

This is an opt-in, local station authoring tool. Stage 1 is the topology and
dependency workbench. The current GUI then places physical seeds, grows department
territories, builds internal maintenance and validates their actual masks.
It exports editable project JSON and SVG, not a playable SS14 map. No round flow
or map-selection code is changed.

## Hierarchy and maintenance revision

The early authoring model now contains departments, subdepartments, bounded room
and feature programs, shared access origins and scoped Required/Desired/Avoided
relations. Definitions use canonical paths (`Engineering/Atmos/TEG`) and versioned
YAML. Project JSON additionally stores generated and edited masks. YAML parsing is
restricted to the fixed schema and rejects aliases, custom tags, unknown fields,
duplicate keys and oversized input; YamlDotNet is the core's only added dependency.

Program budgets feed placement before region growth. Compact usable floor kernels
are protected before service reservations. Maintenance receives a substantial
reservation budget and buffers unrelated department territories; explicit Required
adjacency is the exception. The hierarchy pass packs room footprints and partitions
into grown parent floor while preserving connected internal circulation. Internal
subdivisions cannot become satellites. Flexible subdivisions may occupy a compatible
named host allocation, with logical ownership retained in the result. A separate
read-only validator checks the program and access routes on the actual masks.

Deep maintenance rooms have service-owned floor, enclosure and door masks outside
the ordinary maintenance floor. Their door requires maintenance travel and cannot
open onto a hall. External solars have distinct panel and catwalk masks and planned
closed hull connections to maintenance. Exterior maintenance access frontages retain
window/wall barriers. All three have deterministic bounded counts and independent
geometry/access checks. These are planning reservations; working machinery, solar
power, engine entities and pressure behavior remain later materialization work.

The older notes below describe the earlier stages and baseline; the hierarchy and
maintenance model above supersedes statements that full room programs are deferred.

Maintenance passage floor is bounded by configurable minimum/maximum clear widths,
defaulting to 2–3. Oversized service pockets become actual department floor or
enclosed rooms. Both department and maintenance-room walls have a configurable
maximum depth from usable floor, preventing wall-only ownership spurs. Hall-fronted
maintenance rooms have independently checked public and service doors and do not
consume deep-room slots. Reachability traverses their actual floor/door masks.

Fresh generation evaluates full placement/growth/maintenance/room candidates across
the existing bounded candidate budget, with the same seed and authored topology.
Edited spatial plans keep their placement; regeneration never silently resamples
accepted seeds or envelopes. Preferred room sizes and counts can adjust within hard
bounds. Department order and hub role are data properties, not ID-based solver rules.

## Topology authoring revision

Dependencies are distinct from the physical routes that eventually fulfill them.
Each has stable identity, provenance (template/custom), Required/Desired/Avoided
priority, description, weight, relationship, purpose, permitted methods, direction,
optional distance target, and optional mandatory avoidance. Weight guides search;
it never makes a Required constraint optional. A dependency graph may be sparse:
ordinary crew circulation remains a separate station-wide requirement.

Methods distinguish public halls, staff passages, maintenance, passenger disposal
shortcuts and item mail. Concept validation checks compatibility and conflicts;
it does not claim that pipes, endpoints or security access have been built. The
legacy physical preview explicitly refuses unsupported Required fulfillment.
Desired/Avoided weights contribute to placement distance/separation costs.

Topology presets use stable station-purpose ordering rather than random department
cycles. Generated dependency IDs derive from normalized endpoints and semantics,
excluding description/weight; editing one marks it custom. Preset changes preserve
custom dependencies and their overrides. Explicit reset replaces the whole list.
Removed template dependency IDs are recorded as suppressions and survive preset
changes. The explicit reset clears them; Undo restores both links and suppressions.
Missing provenance in old JSON is treated as custom to protect existing authoring.
Legacy hard booleans import as Required/Desired. Graph node positions and department
descriptions also round-trip through project JSON.

The editor supports dependency forms, two-click node connection, graph/card
selection, search/filter, weight presets/slider, department rename/removal with link
updates, undo/redo, concept checks and graph SVG export. Functional rooms,
infrastructure, windows/pressure zones and engine validation remain later milestones.

## Spatial planning revision

`StationProject.SpatialPlan` is independent of the legacy rectangular `Layout`.
The GUI and default CLI use `StationSpatialPlanner`; legacy rectangle code remains
only for compatibility/testing and explicitly requested legacy exports.

The station envelope is a connected program-sized silhouette, not the whole canvas.
Physical seeds have their own positions, usable-floor targets, pins and service
access policy; topology diagram positions do not place rooms. Seed placement uses
bounded deterministic candidate restarts and topology weights. It protects a basic
room-fit kernel per department and negotiates selected internal service seams
around those kernels before growth. Public streets are reserved as a station network,
not generated by independently joining every pair of department blocks.

Regions grow by connected atomic parcels into the envelope. `Territory` is ownership
including its boundary allowance; `Cells` is derived walkable floor. Floors, shared
walls, hull, public halls, maintenance and port cells are disjoint physical layers.
Territory ownership can include walls/hull, so these two representation levels must
not be confused. Minimum/maximum area limits apply to actual floor. Default major
departments must contain a clear 5×5 basic room block, alongside minimum-width and
connectivity checks. This does not yet guarantee a full department room program.

Maintenance uses internal service reservations and declared access points. It has
no implicit permission to cross department floors. Eligible service ports inherit
department access; secure departments default to disabled maintenance access.
Walking/delivery dependencies can use actual public and/or maintenance networks
according to their allowed methods. Their distance and reachability checks use
the corresponding real port and floor cells. Tube/mail operation remains deferred.

Boundary refresh preserves manually edited territories and walking layers. Released
territory becomes structural reserve; legal painted service routes acquire service
ownership. Painted envelopes and pinned seeds survive placement replacement.
Dependency-only changes preserve placement while invalidating dependent growth.
Changing canvas/raster scale is an explicit reversible reset. Complete generation
does not replace edited territories or deliberately erased maintenance with defaults.

`StationSpatialValidator` independently checks tile ownership, disjoint physical
layers, room fit, area, connectivity, entrance geometry and access, widened paths,
dependency fulfillment, service access, hull, docking clearance and deterministic
hashes. Exterior frontage is measured per department for the later window/pressure
stage. The older prototype discussion below records repository research and the
initial rectangle baseline; its packing/outer-maintenance algorithm is superseded.

## Repository assessment

The checked-out branch is `vibeslopmapgenerationtools`; the checkout uses .NET 10.
DungeonSystem owns engine jobs, room atlas loading, transforms, tile/entity spawning,
and seeded template selection. Its prefab pass already sorts prototypes before
sampling. Its corridor helper uses grid search, but lives on an EntitySystem and
does not supply the prototype's full width-aware authoring/validation contract.
MiddleConnection explicitly leaves loops as future work. AutoCabling is a useful
later LV-routing example, not a general utility-network solver.

The independent geometry core uses BCL collections and its own integer geometry;
the authoring YAML adapter uses YamlDotNet.
Future materialization should reuse SpawnRoom/atlas transforms and the current
SharedMapSystem.SetTiles(Entity<MapGridComponent>, List<(Vector2i, Tile)>) API.
ServerStationSystem.InitializeNewStation and PostGameMapLoad are later integration
boundaries. Map format serialization must go through the engine adapter rather
than treating this prototype's JSON as Robust map YAML. Existing station spawning,
job definitions, access and atmosphere simulation are deliberately deferred.

## Algorithm choice and sources

| Strategy | Benefits | Cost / decision |
| --- | --- | --- |
| Rectangles, constructive slots, local search, explicit routing | Easy tile ownership, immediate output, bounded work, mapper-editable geometry | Selected baseline; shapes and fitted modules come later |
| Template configuration spaces and graph decomposition | Fixed outlines and authored door compatibility; stronger geometric expressivity | Defer until real room modules exist; avoid a general solver prerequisite |

[Ma et al. 2014](https://chongyangma.com/publications/gl/index.html) describe
connectivity-constrained building blocks, feasible relative placements, and graph
decomposition. That motivates explicit ports and the graph/geometry boundary here;
this is not an implementation of their solver. Their project page was available
through search, but the linked full paper could not be retrieved in this session.
[Merrell et al. 2010, sections 5–5.2](https://paulmerrell.org/floorplan-final.pdf)
motivate a supplied architectural program, local moves, and explicit weighted costs.
We use deterministic hill climbing over swaps/rotations/translations and bounded
restarts, with validity as a gate rather than a tradeable score penalty.
[Dormans 2010, sections 4–6](https://pcgworkshop.com/archive/dormans2010adventures.pdf)
motivates independent structural and spatial representations: hub, ring, and spine
are reusable graph/slot rules rather than saved tilemaps.
[Sfikas et al. 2022, section 3](https://www.um.edu.mt/library/oar/handle/123456789/102388)
motivates two-tier design/implementation and diversity alongside feasibility.
We measure seed/archetype diversity; FI-MAP-Elites is future work.
[Edgar's template abstraction](https://ondrejnepozitek.github.io/Edgar-DotNet/docs/introduction/)
supports outlines plus available doors. No Edgar dependency or source is copied.
WFC/DeBroglie would fit local decoration, not station topology.

## Contracts

Coordinates use integer tiles, x right and y down in the editor. Rectangles are
half-open [x,x+width) × [y,y+height) and own only department interior. Their one-tile
expanded bounds own reserved walls. Doors are explicit wall cells, with an outward
cardinal direction and width; public routes never enter department interiors,
including Commons. Restricted access remains a property on ports; no maintenance
doors into departments are generated. Exterior departments reserve a straight,
unoccupied clearance strip to the canvas edge, for later docking adapters.

Corridors use four-neighbor direction-aware A* with deterministic tie breaking,
reuse preference and turn cost. Each path center expands to a width × width square,
including at bends; every expanded tile is checked before acceptance. Door throats
are explicit width-sized rectangles. Hard graph edges mean corridor connections,
not necessarily shared walls. Desired edges contribute distance costs and do not
become mandatory shared-wall adjacency. Maintenance is a deliberate outer service
ring with routed access branches to public circulation. Hull is the one-tile
eight-neighbor boundary of occupied geometry, excluding reserved exterior cells.
Pressure sealing, physical docking airlocks and entity/access simulation are deferred.

All generation randomness uses a specified xorshift32 stream derived from seed and
candidate index. Department IDs and edge lists are ordinally canonicalized before
sampling. Hashes include canonical graph, geometry, ports, routes and tile layers.
Generation returns structured seed/stage/code diagnostics; candidate attempts and
search iterations are bounded. Failed edited projects remain editable drafts.
Only full independent validation can mark a project accepted. Export preserves
the validation status and diagnostics, including when a draft is invalid.

JSON is the authoring format: custom departments, dimensions, access, entrances,
exterior requirements and relationship edges need no C# edits. Later sublayouts
can reference department IDs and local coordinates; authored modules can replace
rectangles with outlines/ports without coupling the optimizer to ECS. Blueprint
and materializer placeholders are intentionally not added before they do work.

Regression checks cover deterministic replay, 100-seed batches across archetypes,
widths, illegal edits, infeasibility, topology, rotation/ports and meaningful variance.
SVG is an inspection artifact; validation reads actual layer and route cell sets.
