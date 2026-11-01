# Implemented hierarchical layout solver

V3 has two construction paths: explicit catalog placement scopes tested in isolation,
and a smaller station fixture for the hierarchy/repair core. Both consume the
nested YAML without loading either earlier tool. Outputs include exact tile masks,
physical interfaces, circulation, exterior reservations, local contracts and an
independently computed report. The department path additionally places selected
equipment from native-derived spatial descriptors. Native map export and machinery
operation remain separate, unimplemented facets.

## Local placement experiments

```powershell
python Tools/StationArchitect3/workbench.py --port 8435
python Tools/StationArchitect3/departments.py command --scope command/core --scales 0.85,1,1.15 --seed 1 --output .tmp/architect3-command
python Tools/StationArchitect3/departments.py commons --scope 'commons/lounge[0]' --scales 0.85,1,1.15 --output .tmp/architect3-lounge
python Tools/StationArchitect3/departments.py arrivals --area 235 --aspect 1.2 --output .tmp/architect3-arrivals
python Tools/StationArchitect3/departments.py --all --output .tmp/architect3-departments
```

The workbench at `http://127.0.0.1:8435` selects a catalog group and an explicit
local test: department, core, suite, room or installation. Area/aspect apply only
to that scope. Distributed owners have no owner-wide packing target; mixed owners
separate their core from independent facilities. The UI shows inventory, an SVG,
local constraint failures and advisory layout quality separately from pending
parent, station and machinery requirements. Resizing preserves the selected local
program. A failed proposal gets its own artifacts while
the previous accepted plan remains visible. The server binds only to loopback,
serializes generation requests, and serves a fixed source and confined output tree.

The CLI accepts an owner template ID with `--scope` to select a local test, or
`--all` to expand all authored scopes. Its default scales are
`0.85,1,1.15` of the preferred usable-floor area; `--area` requests one exact area.
`--program`, `--settings`, `--seed`, `--aspect` and `--output` are configurable.
The resulting gallery links every proposal and a `matrix.json` report. The exit
status is zero only if all requested cases pass their local layout checks.

Calling the low-level `generate_department` API without a scope on a distributed
or mixed owner remains available for constraint research. Its result is labeled
`collection_constraint_experiment`; the GUI and placement CLI use authored scopes.

The scope APIs are:

```python
from architect.instances import resolve_instances
from architect.scenarios import all_placement_scopes, isolate_scope, scope_bounds
from architect.departments import generate_department, resize_department, verify_department

full = resolve_instances(document, seed=1)
scopes = all_placement_scopes(full)
local, owner, contracts, descriptor = isolate_scope(full, "commons/lounge[0]")
bounds = scope_bounds(local, owner)
state, report = generate_department(document, "commons", target_area=bounds["preferred"],
    aspect=1, seed=1, resolved=full, scenario=descriptor["id"])
accepted_state, resize_report = resize_department(state, target_area=round(bounds["preferred"] * 1.15))
checked = verify_department(accepted_state, accepted_state.metadata["departmentInstance"])
```

Isolation retains every selected descendant, feature and locally declared rule
inside the chosen scope. A contained room and its children stay together;
mandatory border components and authored installations also remain intact.
`report.scope` identifies the concrete scope, its owner, organization mode, member
templates and `wholeOwner` status. Partial tests cannot claim their parent's entire
inventory. Original ancestor requirements are retained as pending
`owner_organization` checks, not reduced to the specimen's smaller floor budget.
Targets outside the selected subtree become explicit station contracts. Modest
outside landing patches test the local part of required routes and borders without
pretending to construct the target department. These patches are excluded from
the selected scope's area and inventory counts.

Where a local route requires a continuous external maintenance network, its test
landings are joined by actual two-tile passages around the owned rooms. EVA ports
have real exterior airlocks; geometric EVA checks use exterior-connected vacuum,
credentials, route width and declared waypoints. They leave suit state, pressure
cycling and process shutdown/cooling pending. Unknown geometric predicates block
local acceptance, including a canvas too large for the bounded exterior evaluator.

The packer allocates exact usable floor among the selected rooms, retaining room
and complete-local-ancestor bounds. Incomplete-owner bounds stay pending for
composition. Mandatory contact graphs form room components, contained
children reserve parent space, and declared access policies control internal
connections. Bounded alternatives vary local proportions and arrangement. Every
candidate receives exterior reservations, boundary approaches and its selected
equipment before the verifier checks the combined result. A larger numerical area
range is not a promise that every requested shape can be packed; failed searches
remain visible and never remove selected rooms or equipment to manufacture a pass.

Aspect is a soft shape/orientation preference, not an exact bounding-box promise.
The report and workbench expose the actual department dimensions separately from
the requested preference; physical access and equipment fit take priority.

`resize_department` returns the original state when a proposal fails or changes
selection. Its accompanying report describes the rejected proposal, so callers
must not save that report beside the original geometry as if they matched. The
workbench generates and saves a separate candidate before choosing which accepted
state remains visible.

`verification.inventory` separates physical `selectedFeatures`/`realizedFeatures`
from `abstractServices` and `totalDeclaredFeatures`. Abstract power/gas/service
demands have no invented entity pose. Native-derived descriptors distinguish
floor, underfloor, wall-mounted, interface-mounted and portable objects, preserve
exact assembly offsets and quarter turns, and provide collision and working/loading
clearance. Independent checks reconstruct descriptors from source instead of
trusting serialized occupied cells. See [fixture-geometry.md](research/fixture-geometry.md).

`localLayoutVerified` covers local geometry, declared inventory and supported boundary
approaches. `pendingRequirements` and `externalContracts` remain explicit;
`fullDepartmentVerified` stays false when they remain. A spatially placed TEG,
cryo machine or disposal entry does not prove power, gas, routing, device bindings
or runtime operation. Those native requirements are not waived by the department
scope, and outside vacuum/approaches must be checked again during station assembly.

`verification.layoutQuality` is a separate advisory profile with status
`passes_profile` or `needs_review`, a score and individually measured checks. The
profile can influence candidate ranking without redefining authored hard
requirements. A geometry pass and a quality warning can coexist. Neither status
certifies station distribution, complete native furnishing programs or runtime
gameflow; those exclusions remain explicit in `notEvaluated`.

The editable [layout-quality.yaml](data/layout-quality.yaml) defines profiles for
departments, cores, suites, rooms and installations. Current measurements include
core aspect/fill, circulation fraction and mean room fill. Bounds and weights are
design targets, not universal measurements inferred from native maps. Outboard
installations are assessed by their room masks rather than penalized for required
vacuum separation.

The earlier 33 owner/size passes were local constraint and inventory results.
They did not establish that a packed Commons collection resembles native station
organization. Commons amenities now receive separate placement tests; Service's
functional suites stay local; mixed owners keep independent facilities outside
their core test. Generic Commons concourses and Service circulation are optional
templates rather than forced default filler. Public circulation and maintenance
distribution require station context. A sheet of independently valid room specimens
is not a generated station.

The [fresh map comparison](research/comparative-map-audit/audit.md) and
[side-by-side image](research/comparative-map-audit/native-vs-generated.png) document
the distinction using native tile/entity data and saved earlier experiments.
Beacon positions do not define room ownership, furniture-family counts are a
screening measure, and public-floor holes are not certified chase loops. Declared
inventory completeness cannot establish that the YAML contains a complete native
furnishing/operating program.

## Run and inspect

```powershell
python Tools/StationArchitect3/generate.py --seed 11 --output .tmp/architect3-layout
python Tools/StationArchitect3/generate.py --verify .tmp/architect3-layout/layout.json
python -m unittest discover -s Tools/StationArchitect3/tests -v
```

`generate.py` defaults to [station-layout.yaml](examples/station-layout.yaml), with
one hospitality suite. [hierarchy-station.yaml](examples/hierarchy-station.yaml)
explicitly selects two suites as a repeated-binding regression fixture.
Pass another program as its positional argument. `--settings` accepts the flat,
strict [runtime settings](examples/generation-settings.yaml), not the earlier
`planning/search-policy.proposed.yaml` sketch. Unknown settings, booleans passed
as numbers, invalid widths and contradictory gap widths are rejected.

Artifacts in the output directory:

| Artifact | Use |
| --- | --- |
| `index.html` | Browser inspection with zoom, labels, room search/highlighting, checks, metrics and trace. Works as a local file. |
| `layout.svg` | Portable vector view of actual floor, structure, interfaces and reservations. |
| `layout.json` | Geometry plus the original source document and instance seed. |
| `report.json` | Status, capability gaps, checks, soft costs, quality measurements, seeds and work used. |
| `contracts.json` | Current subtree footprints, ports, local checks and external needs. |

Saved verification reconstructs the instance tree and requirements from the
embedded authored program. It checks its source hash and recomputes geometry;
saved `satisfied` flags and summaries cannot certify a modified layout. Verification
is for that embedded program, not authentication against an external source file.

Exit status is zero for a verified requested layout scope, nonzero for incomplete
or rejected generation and invalid input. The JSON `termination` explains the
difference. `budget_exhausted` or `search_exhausted` is not an infeasibility proof.
A verified incumbent can survive exhaustion of an optional improvement budget.

## Algorithm and scope of repair

1. The resolver validates authoring, selects counts and variants, activates needed
   optional providers, enforces local family choices and binds concrete targets.
   IDs include every repeated ancestor index. `same_parent`, `same_owner` and
   injective matching use those identities. Alternative eligible targets remain
   recorded; the current packing search does not exhaustively backtrack bindings.
2. Mandatory border graphs form functional suites. Several arrangements and room
   proportions are tried within a deterministic budget. Contained children reserve
   actual host-envelope space with disjoint floor. Alternatives retain correlated
   masks and frontage obligations, rather than independent area/frontage promises.
3. The constructive packer arranges department clusters along a public spine,
   reserving narrow maintenance at side/rear boundaries. The current station-level
   proposal family is two banks along that spine. It varies ownership arrangement,
   suite topology and room proportions; arbitrary station circulation topologies
   are a later expansion of the same proposal interface.
4. Interfaces and exterior berths are placed before final growth. Coherent notches
   and free-boundary growth change initial rectangles. Growth checks room and
   ancestor area pressures, structural separation, circulation and the structural
   halo around exterior reservations. Sampled targets guide proposals; scoring
   retains the original YAML preferences.
5. `repair_area` first tries free floor inside the room's allocation, then shifts
   shared walls to borrow sibling slack, then asks ancestors/station for external
   space. An absent logical-parent envelope does not grant ownership of arbitrary
   station space. `repair_feature` first moves a supported pose within its room.
   Unresolved minimum-area deficits invoke repair during generation; the APIs also
   support explicit subsequent edits and repairs.
6. Each transaction edits a private copy. Its scope restricts changed resource
   owners. Before commit, the verifier recomputes protected pre-edit obligations,
   affected geometry and access. Invalidating a witness cannot remove its rule
   from the protected set. Rejected candidates leave original geometry unchanged.
7. Several bounded station candidates are compared. Mandatory failures cannot be
   bought off by soft density or variety costs. The best candidate receives fresh
   subtree summaries and a verification report.

Local declarations never migrate into a global rule list in the authored document.
Contracts report ports, exact footprint and external needs with provenance.
Growth/repair trace records affected scope, attempts and escalation. Internal
checks are recomputed conservatively; optimized selective witness caching is not
yet implemented. Construction alternatives are proposals in local coordinates,
not certified replacements for an already packed suite.

## Exact geometry conventions

- Tiles and half-open bounding boxes use integer coordinates. Floors have one
  physical owner. Logical groups/departments have no invented floor.
- Usable floor area is the declared surface excluding structural tiles. Fixture
  footprints block movement separately; placing furniture does not change that
  area convention. Aggregate area counts unique owned descendant floor once.
- Structural walls and interfaces occupy full tiles between opposite floor
  approaches. An explicitly declared `geometry: open_edge` instead joins adjacent
  floor cells with no intervening structure. Its profile must be open, walkable,
  uncredentialed and non-airtight. Undeclared direct floor contacts are rejected.
  A service counter is not a walking door.
- `shape.short_side` can fix a strip's depth independently of area. HoP's queue
  uses depth one and an open public-hall edge while retaining its controlled
  office counter and separate staff route; enlarging it cannot make a square room.
- For logical `shape.components`, include a structural seam where the same owner's
  floor exists on opposite sides. This counts connected departmental complexes
  across their internal walls. Physical room components use their floor mask.
  Credential-aware reachability is a different check.
- `boundary_manhattan` measures distance between closed tile boundaries: one full
  intervening wall tile gives distance one. Walking distance uses actual routes.
- Passage width uses connected clear square-footprint anchors and rectangular
  strip witnesses. Every passage cell must be explained, and an open square larger
  than the maximum width is rejected. Turns and junctions cannot hide chambers.
  A required two-wide route also needs a two-wide doorway; no implicit bottleneck
  exception is enabled.
- Actor credentials apply to interfaces. Routes honor scope, required waypoints,
  avoided spaces and blocked fixtures. Entry and `mustTraverse` policies are
  checked against all relevant paths; a new bypass invalidates acceptance even
  when an older permitted path still exists.
- Exterior vacuum must connect to the padded canvas boundary. Docking checks
  reconstruct berth/approach from the physical interface, rotation, offset and
  clearance, then check the straight padded lane to exterior. An enclosed courtyard
  or a narrow escape around an obstacle cannot supply a shuttle approach.
- Maintenance wrap counts connected, valid-width maintenance outside a cluster's
  actual boundary. Area in a deep maintenance room does not count as passage wrap.
  Density is usable core floor over its convex-hull envelope; intentional voids
  remain visible and declared outboard floor is excluded from core measurements.

## Implemented controls

| Runtime setting | Behavior |
| --- | --- |
| `suite_alternatives`, `parent_alternatives` | Number of local arrangements explored and correlated alternatives retained. |
| `root_restarts` | Maximum station candidate attempts, including the first. |
| `max_proposals` | Global generation/repair work budget. Zero intentionally produces an exhausted run. |
| `local_proposals` | Maximum area/feature repair proposals per request. |
| `room_growth_steps` | Bounded free-boundary growth work. |
| `maintenance_width` | Two or three clear tiles, still intersected with authored requirements. |
| `department_gap` | Minimum packing separation, including maintenance and two structural tiles. Larger gaps remain explicit rather than becoming overwide passages. |
| `area_jitter` | Variation in proposal targets, clamped to hard bounds; never replaces authored preferences. |
| `compactness_weight`, `wrap_weight` | Added tier-zero preference for compact core geometry and connected maintenance wrap. |
| `split_weight` | Additional spatial fragmentation cost for ordinary departments. |
| `distributed_split_weight` | Cost for owners explicitly tagged `distributed`. Functional suites remain coupled. |
| `maintenance_split_weight` | Cost for `maintenance_owner` facilities, allowing deep rooms/vestibules to spread. |

Names such as Kitchen or Security have no behavior in solver code. Relationships,
placement, access profiles, requirements and explicit tags drive the construction.
Runtime tag policies are limited to the listed settings; a generic selector-policy
DSL from the design sketch is not implemented. Existing authored cohesion costs
still apply in addition to these policy costs. All extra soft costs share tier zero
by default so they can trade with untiered authored preferences.

## Capability boundaries

| Supported now | Reported unresolved / future work |
| --- | --- |
| Counts, nested ownership/containment, conditional activation, choices and concrete matching. | Exhaustive selection/rebinding backtracking across spatial failures. |
| Exact area/shape/cohesion, border seams, route width/access, entry policies, separation, docking/perimeter, grid identity and presence dependencies. | Native bridge construction, EVA hazard routing and operational environment requirements. |
| Maintenance strips, wrap diagnostics, deep-room restrictions, compactness and varied suite placement. | Arbitrary public-network topology search, general joint corridor relocation and complete repacking of every unsolved constraint type. |
| Atomic area/pose repair, immutable local rules, scope escalation and independent rechecking. | Incremental evaluator dependency caches and a general extension-provided repair registry. |
| Authored department/core/suite/room/installation scopes, native-derived equipment placement, local spatial adapters and resize verification. | Distributing facilities and joining local scopes into one station while maintaining all parent/outside contracts. |
| Serialized candidate layouts and inspection artifacts. | Native `.yml` entity export, power/atmos/disposal simulation and engine commissioning tests. |

The department path has native-derived collision/working footprints and spatial
adapters for supported local access, loading, binding-distance and exposure rules.
Service operation and unsupported `x_*` semantics remain pending. No machinery is
certified by its room rectangle or icon. Strict **full-station** preflight continues
to reject the full standard catalog for remaining unsupported obligations. To
inspect that incomplete station geometry intentionally:

```powershell
python Tools/StationArchitect3/generate.py Tools/StationArchitect3/catalog/standard-station.yaml --candidate-only --output .tmp/architect3-catalog-candidate
```

That run can have both geometric failures and unsupported obligations; it is not
a playable-map export. A program also needs declared walking-interface profiles
for the packer to create doors. The older `serving-clusters.yaml` deliberately only
declares serving interfaces, so it validates as authoring but does not supply every
construction choice needed by this packer. No door/access semantics are invented.

## Executable checks

`test_departments.py` checks full selected inventories, physical versus abstract
features, exact smaller/larger areas, stable selection, rejected-resize retention
and independent saved-department verification. `test_department_workbench.py`
checks API validation, output confinement and pairing failed proposals with their
own reports while retaining the accepted preview. Equipment and local spatial
rules have additional focused tests in `test_fixture_geometry.py` and
`test_department_rules.py`.

`test_placement_scopes.py` checks complete nonoverlapping inventory partitions,
atomic Hospitality/Chapel/solar scopes, mixed cores, generic renamed owners,
mandatory-border scope merging, local resizing and recovery of deferred ancestor
obligations from saved source. The pure DOM/API simulation in
`tests/test_workbench_ui.mjs` checks scope selection and payloads, distributed-owner
gating, advisory quality status and rejected-resize retention without a browser.

The station generator tests cover repeated/local target identity, optional provider
activation, distinct matching, nested access, exact seams, movement credentials,
width/overwide pockets, docking and courtyard failures, new access bypasses,
transaction rollback, local/sibling/station authority, hard bounds, unknown
evaluators, deterministic runs, finite budgets and saved-layout corruption.

Run `test_generator_*.py` for these checks or the full test command above to include
the existing YAML authoring/compiler tests. The design's
[acceptance cases](planning/acceptance-cases.yaml) remain a roadmap; this document
and executable tests identify what is implemented rather than marking every
planned scenario complete.

The supported fixture passed all 20 default seeds in the initial benchmark, with
20 different geometric partitions after removing room labels and normalizing
translation, rotation and reflection. Width-three and gap-eight variants each
passed three seeds. This measures the fixture's geometry/access behavior, not
arbitrary programs or distinct gameplay topology. Reproduce the measurement with:

```powershell
python Tools/StationArchitect3/benchmark.py --seeds 20 --variants --out .tmp/architect3-benchmark.json
```

The benchmark records implementation/source hashes, fixed-selection checks,
work used, failures and density/wrap ranges. Increased maintenance area can raise
the reported density, so compare room packing within the same selected program
and circulation policy rather than interpreting that number alone.
