# Department inventory geometry

`architect/fixtures.py` resolves every selected feature from the local native
prototype tree. The standard catalog currently selects 303 feature instances;
all selected prototypes, alternative family prototypes, and declared assembly
parts have supported spatial descriptors. This is source-version-specific
coverage, not a promise that arbitrary future native components are supported.

The adapter resolves parent components, generated prototype variants, typed
fixture shapes, collision masks, exact assembly offsets and quarter-turn poses.
Nominal AABB, circle and polygon bounds become conservative occupied tile sets.
Hard collision participates in walking obstruction according to the native
collision-group test. Sensors do not consume construction floor. Opaque sensor
fixtures do participate in solar shadow checks.

The policy file `data/fixture-footprints.yaml` classifies native component types
as floor equipment, wall mounts, underfloor devices, carried inventory, and doors
mounted to existing interfaces. It also defines allowed layer sharing, placement
search limits, rotation choices, and protected entrance width. No department or
room names determine fixture geometry. Abstract utility contracts have no
physical pose; their operating requirements remain pending.

## Placement and checking

`estimate_room(program, room_id)` returns selected inventory, unknown adapters,
footprint area, minimum dimensions and a recommended packing area. The footprint
minimum is only a necessary bound. Recommended area is a packing hint, not a
proof that every room shape of that area is feasible. Dense solar arrays need
operating aisles in addition to their occupied tiles; the regression uses 81
floor tiles for 46 physical installations.

`place_features(state, room_id, seed)` uses actual room floor, interface approaches
and wall support. Working floor may be shared while unobstructed. Native storage
opening/ejection offsets rotate with the machine. The placement search preserves
connectivity from room entrance approaches to every already promised working
position, so later equipment cannot seal an earlier operator into a pocket.
Only interfaces with the configured crew, item-transfer, or docking capabilities
reserve operating approaches. Visibility-only exposed lattice segments do not
invent a row of crew doors around an otherwise open solar platform.

`assess_features(state)` reconstructs descriptors independently of saved cell
sets. It checks selected instances, declared ownership, support, layers, exact
assembly parts, collision footprints, loading/working clearance and entrance
reachability. Corrupting a saved footprint cannot hide a machine. Missing selected
equipment remains a failure.

`architect/department_rules.py` evaluates local entrance policies, native loading
approaches, control access, feature exposure and repairability distance to the
actual declared device binding. Vacuum exposure permits lattice support but
requires connection to exterior vacuum. Sunlight uses native Opaque collision
layers; neighboring solar panels do not incorrectly shadow one another.
`environment.minVacuumGap` is only a separate spatial check, leaving the full
environment requirement pending.

EVA routes likewise receive a separate `::spatial` check. Their actual equipped
actor must have vacuum capability and the credentials for realized exterior
interfaces. Routing uses complete width-by-width footprints through exterior-
connected vacuum and open lattice; sealed courtyards, walls, generic hull holes,
and non-crew counters cannot substitute for a route. Avoided rooms and required
waypoints remain binding. Process rooms may be conditional endpoints, but cannot
be used as intermediate shortcuts. Shutdown, cooling, pressure cycling, suit
state, and survival remain unverified in the original route requirement. Scoped
owner/parent exterior domains remain unsupported until their bounds are defined.

## Source basis and limits

- `Content.Shared/Physics/CollisionGroup.cs`: collision bit definitions.
- `RobustToolbox/Robust.Shared/Physics/Systems/SharedPhysicsSystem.Contacts.cs`:
  fixture layer/mask contact rules.
- `RobustToolbox/Robust.Shared/Physics/Collision/Shapes/PhysShapeCircle.cs` and
  `PhysShapeAabb.cs`: native nominal shape defaults.
- `Content.Server/Solar/EntitySystems/PowerSolarSystem.cs`: Opaque ray obstruction.
- `Content.Shared/DeviceLinking/Components/DeviceLinkSourceComponent.cs`: source
  repair-link range default of 30 map units.
- The selected entity prototype files supply inherited `Fixtures`, `Physics`,
  `EntityStorage.enteringOffset`, and component classifications directly.

This adapter does not execute engine physics, reproduce contact skin, simulate
pressure/temperature, commission machinery, create saved native entity UIDs,
persist device bindings, or prove power/atmos/disposal operation. Native ports are
logical assembly obligations; their operation is not inferred from nearby floor.
Unsupported shapes or subtile loading offsets remain explicit unknowns.
Exterior exposure must be checked again when departments join the full station.

Focused regressions are in `tests/test_fixture_geometry.py` and
`tests/test_department_rules.py`.
