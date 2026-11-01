# Physical design checks

Runtime validation rebuilds observations from actual room masks, interfaces,
passage floor, reserved space, equipment footprints and atmosphere assignments.
Saved reports and copied requirement values are not acceptance evidence.

The room tree retains station → owner → room template → instance relationships.
Floor area is counted at instances and summed at owners. Door cells connect
owned rooms without being counted as anonymous department floor. Room count,
area, shape, ownership, split and route rules remain attached to the authored
definitions when the geometry is edited.

Nested rooms retain their complete instance lineage, such as
`Service/Chapel#1/Confessional#1`. Each owns exclusive usable floor. A parent's
subtree budget sums its own and descendant floors once; its complex shape uses
actual subtree floor and internal structural seams, never empty planning area.
Room-local sibling and parent connections stay within the corresponding real
parent instance.

By default an internal child has a closed physical boundary, belongs to the
actual connected parent complex, and is reachable from its parent within that
complex. It may share the suite's outer wall. A large `complexReservation`
alone proves none of these conditions.
The validator removes the parent's floor and searches station approaches,
including indirect configured passenger jumps and forward-only native waste
travel, to reject bypasses into an internal child.
Authored nesting settings can permit externally attached children or secondary
entries. Partial measurements keep missing physical proofs pending; final
evaluation requires the configured proofs.

Public passages and maintenance are separate floor layers. Maintenance must have
its configured two-to-three-tile clear width and no larger open squares. A larger
space needs an actual bounded room definition. Public topology backbones are
actual full-width paths; a ring must close through a nondegenerate loop. Painting
a passage preserves the painted floor and rebuilds legal entrances against that
floor; it does not silently restore the generator's former mask.

Passage entrances are generated per physically connected room cluster, so
distributed rooms keep their department ownership while receiving local
access. `packing.maintenanceCoverage` targets the fraction of exposed cluster
boundary edges with actual nearby maintenance floor;
`maintenanceWrapDistance` measures from room floor to the first passage strip,
including the intervening wall. `maintenanceConnectionsPerCluster` targets
separated maintenance entrances. These are layout preferences: unavailable
coverage or entrances produce measured warnings, while width, overlap,
connectivity, nested access and mandatory route checks remain hard constraints.
The report includes actual coverage, edge counts and entrance counts. Empty
voids and gross parcel rectangles contribute no passage coverage.

Automatic circulation reserves the public backbone and eligible public entrance
throats, builds primary maintenance access, then adds public branches. Secondary
maintenance entrances and wrapping follow. This keeps an early public loop from
stranding an entire department's service entrances. If service branches isolate
a public throat, a bounded negotiation reserves a full-width public escape and
reroutes automatic service around it. That retry is disabled for painted
networks, edited or locked routes/interfaces, and pinned equipment. Protected
geometry is preserved and conflicting constraints remain visible.

Wrapping extends existing passages while respecting the maximum open width.
It does not erase an existing parallel strip merely to improve coverage. This
can leave coverage below its target where public routes, reserved Space or
width-safe joins prevent expansion. True maintenance rooms may also remain in
perimeter rows with longer connecting passages. These limitations are reported
as actual geometry and preference warnings, rather than counted as achieved
coverage.

Placement connects full-width public planning arteries while preserving a common
maintenance component, then reserves a connected maintenance tree in the
remaining space. Both sets publish together only when all expected entry guides
are present and connected. Growth preserves those reservations. They are
planning clearances, not built floor or proof of a fulfilled route; circulation
still constructs and independently validates actual passage cells and doors.

`allowPublicEntry` and `allowMaintenanceEntry` permit entrances; they do not
require one. Unfinished automatic gateway goals therefore produce warnings,
recomputed from actual eligible clusters and interfaces at circulation and every
later stage. Moving to furnishing or validation does not hide a missing gateway.
Authored Required routes, forbidden access and invalid physical geometry still
produce errors. The default generator's full-budget regression separately checks
that its expected gateways were actually built.

Unmaterialized cells are SS14 Space tiles, including enclosed vacuum courtyards.
The planning envelope does not secretly fill those cells. Space frontage is
measured across actual walls or windows into those empty tiles. Exterior pockets
are reserved before passage routing so later floors and walls do not cover them.

Sealing is measured over an air-connected compartment. An open serving counter
can join two rooms that share one sealed compartment. A counter facing Space
does not seal that compartment. Closed doors, airlocks and windows provide
airtight separators; utility pipes and cables preserve their underlying walls.
Incompatible initial pressure or temperature assignments in an open compartment
are reported. Initial plans have not been verified in a running SS14 simulation.

`access.requireNetworkApproach` can contain `publicHall`, `maintenance`, or both.
Every listed network must be traversed on **every approach** from public passage
or another owner's station floor. The validator removes each required network's
floor in turn and searches the remaining physical walking graph for bypasses.
A common maintenance room with a public entrance therefore cannot provide an
unnoticed shortcut into a deep room. Validated passenger terminal jumps and
powered native waste delivery edges are also searched; an underfloor shortcut into a neighboring common room is still a
bypass, even though the tube is not ordinary walking floor. The normal
required-route check separately
prevents an isolated, unreachable room from passing merely because it has no
bypass. Missing approach observations fail final pressure evaluation.

Floor entities must match their configurable footprint profiles, including
offsets and quarter-turn rotation. Working-floor reservations stay inside their
room, cover required space, and cannot overlap another entity's reserved space.
Boundary equipment must physically cover its associated interface. Equipment
that blocks a stored walking route invalidates that route.

Declared docks reserve a berth and a full-width exterior approach tied to one
specific boundary airlock. The helper reconstructs both masks from the authored
dimensions, outward direction and actual anchor, and rejects occupied or stale
clearances. A dock counts only with its exact configured native docking hardware,
aligned physical interface, orientation and clear interior standing tile. An
unrelated exterior door cannot satisfy it. Export preserves that prototype's
native docking components and inherited arrival, emergency or cargo settings.
The independent serialized-map audit checks actual tiles and native colliders
throughout the berth, approach and hardware standing space; reservation masks
alone are insufficient evidence.

A final design is accepted when all mandatory configured geometry checks pass.
Unfulfilled or unproven Desired/Avoided preferences retain their costs and are
reported separately; they do not become fictitious fulfilled connections and do
not prevent mandatory geometry acceptance. Operational atmosphere, power,
machinery, hazards and access behavior remain explicitly unverified by this
planning editor.
