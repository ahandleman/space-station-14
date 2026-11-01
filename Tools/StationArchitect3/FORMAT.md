# V3 room and relationship format

The root is `format: ss14-station-architect`, `schemaVersion: 3`. It contains named
collections of spaces, interfaces, actor access profiles, environments, grids,
domains, assemblies, evidence and optional evaluator
extensions. Unknown keys and undeclared rule types are rejected. YAML duplicate
keys, anchors, aliases, unsafe tags and non-finite numbers are rejected rather
than normalized into something different from what the author wrote.

There are no generator stages, pins, locks or generated geometry in this document.
Requirements, relationships and choices are declared on their owning space.
The generator may propagate layout consequences or summarize internal obligations
at a parent; those generated summaries are not authored YAML declarations.
Authoring checks reject
provably impossible references, counts and dependencies; symbolic bindings are
not witnesses of physical constraint satisfaction.

## Hierarchy without rectangular department envelopes

Space kinds are `department`, `group`, `room` and `passage`. Departments and groups
are nonphysical aggregates. Rooms and passages own usable surface. A logical
department can therefore include several spatial groups without becoming one
large prescribed rectangle.

IDs are lowercase snake_case and unique among siblings. Canonical paths are
slash-separated, for example `service/hospitality/kitchen`. Feature paths append
`#feature_id`, such as `engineering/teg/generator_room#teg`.

## Local declarations and references

Each space may contain `relationships` and `choices` alongside its `requirements`.
Put an internal pair at the smallest functional scope that owns it: a room for
its machines, or a group for sibling rooms such as HoP and its queue. A room's
need for an external service remains a relationship declared by that room.
Shared interfaces, assemblies and evaluator definitions are reusable vocabulary;
declaring them does not impose station-wide constraints.

References resolve against the **declaring space**, not the `from` endpoint:

| Reference | Meaning |
| --- | --- |
| `.` | The declaring room or group. |
| `#analysis_console` | A feature of the declaring room. |
| `./hop_office` | A child of the declaring group. |
| `../server_room#research_server` | A sibling room's feature. |
| `/public_hall` | An explicit external path from the document root. |

The obligated `from` endpoint must lie in the declaring space or its descendants.
The target can be external. `through`, `avoid`, `entryFrom`, `mustTraverse` and
choice and organization members use the same relative syntax. Traversing above the document root
is invalid. Local IDs need only be unique within their declaration scope.

Root relationship/choice lists and bare canonical paths remain accepted for
legacy v3 fixtures; the shipped catalog and examples use local declarations.
`resolve_document()` and `flatten_relationships()` produce non-mutating canonical
analysis views. The compiler records `authoredId`, `declarationScope` and each
tree node's `declaredRelationships`/`declaredChoices`; it does not discard locality
when showing a flat index. These analysis views contain internal metadata and
are not YAML round-trip exports. Use the authored document for editing.

Every space has a `count: {min, preferred, max}` **per parent instance**. Zero
minimum makes a template optional, not nonexistent. Zero maximum disables it.
Optional parents do not instantiate mandatory children unless that parent is
selected. A `group` may be repeated to reproduce a whole functional cluster.

`placement.nesting` is independent of logical ownership:

- `owned`: the child belongs to the parent but need not lie inside its footprint.
- `contained`: a child room lies inside its physical parent room's complex
  envelope. Parent and child usable floor remain disjoint. Containment does not
  itself create a doorway or forbid a secondary entrance.

`area.exclusive` counts a physical node's own usable surface. `area.aggregate`
counts that node and all descendants exactly once. Walls, unbuilt vacuum and
another owner's floor are excluded. Department shape/component metrics operate
on the union of descendant footprints, not on an invented rectangular owner mask.

The layout evaluator's exact tile, structural-seam, usable-area and passage-width
conventions are documented in [LAYOUT_SOLVER.md](LAYOUT_SOLVER.md). In particular,
logical cohesion includes internal full-tile seams between same-owner floor; it
does not confuse separate enclosed rooms with separate departmental wings.

`placement.zone` is `interior`, `hull`, `outboard` or `any`.
`placement.grid` names an engine grid; `placement.surface` is `floor`, `lattice`
or `any`. An open-vacuum radiator can have lattice support on the same station
grid. Vacuum is not a synonym for missing grid, missing support or detached grid.
Bare Space belongs to a domain and is not counted as usable room floor.

Rooms also name an `environment` and an intended `access` actor profile. A
habitable or process environment does not prove actual gas inventories or control
systems. Environment profiles describe pressure class, enclosure and thermal role.

## Organization and local placement scopes

Logical department nodes may declare `organization` independently of ownership,
containment and access. Its default is `{mode: clustered}`. The workbench and
placement-test CLI interpret the modes as follows:

| Mode | Local tests |
| --- | --- |
| `clustered` | The selected owner subtree is one department test. |
| `distributed` | Each selected direct child is an independently placed room or suite, except explicitly grouped installation members. There is no whole-owner packing test. |
| `mixed` | A core contains the remaining children; `independent` children and explicit suites have separate tests. |

For example, Command keeps a core while testing its public-facing personnel suite
separately, and Commons exposes independently placed amenities:

```yaml
# On command:
organization:
  mode: mixed
  independent:
  - ./personnel

# On commons:
organization:
  mode: distributed
```

Explicit suites can gather several direct-child templates into one installation:

```yaml
# On maintenance_rooms:
organization:
  mode: distributed
  suites:
  - id: solar_installations
    name: Solar installations
    members:
    - ./solar_control
    - ./solar_array
    - ./external_access
```

Members must resolve to direct children of the declaring owner. Suite IDs are
unique and cannot collide with child IDs. Suites cannot overlap, and a suite
member cannot also be listed as independent. `independent` is valid only for
`mixed`; explicit suites require `distributed` or `mixed`. A member template selects
all its concrete instances. The solar example currently keeps all selected arrays,
controllers and access vestibules together because their local bindings and
waypoint obligations span those instances.

A child group stays intact, and a physical child brings its contained descendants.
Thus Hospitality retains both serving counters onto its one BarCommons, and Chapel
retains its own Confessional and Crematorium. Mandatory shared borders between
independently selected roots merge those roots into a local suite; unrelated rooms
are not joined merely because they share an owner. Cross-scope requirements remain
explicit contracts, and existing internal-route requirements are never weakened
by the organization label. The catalog must author an appropriate station route
when a facility is intended to be reachable through public or maintenance areas.
Security's Courtroom and Law Office are independent room scopes under this rule.
The Sentencing-to-Courtroom relationship is a station route, so it preserves
required access without requiring a shared wall or joining their placement tests.

Scope IDs identify concrete selected units, such as `commons/restrooms[0]`,
`command/core` and `service/hospitality[0]`. Each scope reports its kind, owner,
member templates and whether it covers the whole owner. Isolation retains local
requirements and selected inventory. An incomplete ancestor's original aggregate
or organization requirements remain `owner_organization` obligations for station
integration; they are not rescaled to fit a room specimen or declared satisfied.
Saved-layout verification reconstructs the scope and these obligations from the
authored document, rather than trusting serialized summaries.

`core.components` measures cohesion only among the owner's core rooms. It excludes
declared `independent` children and explicit organization-suite members (including
their descendants), outboard rooms and boundary test patches. Internal structural
seams between core-owned floors count toward connectedness as they do for logical
`shape.components`. This prevents a cohesion preference from pulling an explicitly
independent HoP suite, checkpoint or substation back into the core. The metric's
integer bounds and weight use the ordinary metric rules. It remains active in a
partial core test; the whole owner's `area.aggregate` and other incomplete-owner
obligations still wait for integration.

Organization does not specify a station-wide distribution algorithm or certify
station placement. `localLayoutVerified` is separate from advisory layout quality,
owner-wide constraints, native machinery operation and runtime gameflow.

## Mandatory rules and weighted preferences

Rules inherit `defaults.requirement` unless they specify their own `priority`
and `weight`. The shipped default is mandatory, weight 100.

**Mandatory** min/max bounds, edges, dependencies and physical conditions are
feasibility obligations. No amount of improvement elsewhere can buy permission
to violate them. Their weights may guide repair ordering; a failed mandatory rule
still prevents acceptance. **Desired** rules contribute an optimization pressure.

A metric's `preferred` value adds soft pressure even when its min/max are
mandatory. For numeric bounds, the intended normalized violation is distance
outside the accepted interval divided by positive `scale`; target deviation is
distance from the preferred value divided by that scale. Weight multiplies that
cost. A solver must report hard violations separately from preference cost.
Binary obligations use an unsatisfied-instance count. Optional integer `tier`
orders pressure groups lexicographically, lower numbers first; it does not change
mandatory feasibility. Authoring validation preserves these values; the implemented
solver and its remaining capability limits are described in [LAYOUT_SOLVER.md](LAYOUT_SOLVER.md).

`basis` identifies `user_requirement`, `native_mechanic`, `map_pattern` or
`design_policy`. `evidence` refers to root evidence IDs. A native-mechanic basis
does not make every chosen dimension a native constant: range/size/clearance
values remain authored unless the source explicitly establishes their value.

## A relationship means a particular physical or functional edge

Inside `service/hospitality.relationships`:

```yaml
- id: kitchen_serves_shared_commons
  type: shared_boundary
  from: {space: ./kitchen}
  to: {space: ./bar_commons}
  matching: {targets: one, scope: same_parent, presence: require_target}
  priority: mandatory
  weight: 100
  description: Kitchen serves the customer side of this hospitality cluster.
  basis: user_requirement
  evidence: [requested_service_edges]
  parameters:
    interface: service_counter
    minLength: 3
    preferredLength: 4
    minSegments: 1
    maxSegments: 1
    continuous: true
```

The referenced paths, interface and evidence must exist. A second Bar-to-Commons
edge uses the same parent scope. Each hospitality parent contains exactly one
commons, so both edges must select that particular commons instance. Repeating the
parent makes another complete cluster; it cannot cross-bind kitchens and bars to
another parent's customer room. The small complete example demonstrates this.

Endpoints name exactly one space, feature, domain, or selector. Space and feature
references may be relative as above. Selectors accept
`tagsAll`, `tagsAny`, `kinds` and `within` (the named path and descendants). Filter
dimensions combine with AND; tagsAny combines its entries with OR. Empty selectors
are invalid. Exact typos remain errors even when the referenced room is optional.

Each selected source instance imposes its rule. `matching.targets` controls the
other endpoint:

| Value | Obligation |
| --- | --- |
| `one` | At least one eligible selected target; keep alternatives unresolved until solving. |
| `all` | Every selected eligible target must satisfy the edge. |
| `distinct` | Different source instances require different target instances (injective matching). |

Scopes are `station`, `same_owner` or `same_parent`. Same-owner means the same
department **instance**. Same-parent equates the repeated instance indices through
the deepest common ancestor, including child-to-parent and grandchild-to-uncle
relationships. Template path equality alone cannot establish an instance match.

With `presence: require_target`, an active source forces compatible target
presence. It cannot claim success because an optional target was omitted.
`if_both_present` instead activates only for selected endpoint pairs. An absent
optional source creates no obligation; therefore a mandatory consumer should
explicitly require an optional source it needs. Atmos's source-room dependency
illustrates this. The compiler emits implications and candidates, not an arbitrary
choice among unresolved targets. Its interval checks are not a complete SAT solver.

## Relationship types

| Type | Required witness in a future realization |
| --- | --- |
| `shared_boundary` | Actual common interface seam, required length/segments and usable approach on both sides. Corner contact, a nearby room or a corridor between rooms is insufficient. |
| `route` | Continuous usable path under the actor's credentials, minimum width, allowed owner/parent/station scope, required waypoints and excluded spaces. |
| `separation` | Declared boundary Manhattan distance or accessible walking distance. The metric is explicit. |
| `service_link` | Typed feature ports connected by the proper medium on compatible supported geometry. Physical flow, grid identity and separate circuits must be respected. |
| `grid_relation` | Same or different engine-grid identity, independent of spatial adjacency. A named bridge assembly is necessary when that mechanism is authored. |
| `dependency` | Required compatible target presence only. It supplies no boundary, walking or utility proof. |

Shared boundaries use interface profiles. Profiles declare routine crew passage,
item transfer, visibility, speech, gas/power transfer or docking capabilities and
native construction candidates. A solid wall legitimately has no capabilities.
An open lattice edge can have no entity construction: lattice is a surface.
Counter length counts interface seam tiles, not route length. Distinct segments
and contiguous serving runs are separate constraints.

`geometry: structural_tile` is the default: the interface replaces one full tile
of structure between endpoint floors. `geometry: open_edge` instead declares
direct contact between adjacent endpoint floor tiles; its recorded cells are on
the source floor and its normal points one tile toward the target floor. It must
be open, non-airtight, public, carry crew, and have no construction entities. It
cannot represent a lock or docking port. The HoP queue uses this open boundary
along its public side and a separate controlled counter along the office side.
Its mandatory `shape.short_side` metric is exactly one tile (the shorter bounding
box dimension), with `shape.components: 1` and an independent area bound.

An item-transfer counter is not a routine crew doorway. This says nothing about
climbing, breaking or bypassing furniture in actual play. A controlled airtight
windoor is sealed when closed; its service cycle may open it. Its normal state
and access credentials are explicit. The HoP relation uses a controlled service
interface plus a staff route that excludes the public queue.

Interface `access.anyOf` preserves native credential logic: OR between inner lists,
AND within each list. `[[Medical], [Detective], [Chapel]]` differs from
`[[Medical, Detective]]`. An empty list means ordinary public access. Actor profiles
state credentials assumed for routing; they are not edits to native job definitions.
The `public` actor-level marker denotes uncredentialed floor, not an SS14 access ID.

An interface profile can constrain walking direction and its usable gate width:

```yaml
passage:
  direction: a_to_b
  minWidth: 1
  maxWidth: 1
```

`direction` is `a_to_b`, `b_to_a` or `bidirectional`, relative to the realized
interface's stored `a`/`b` endpoints and its normal from `a` toward `b`. Omitting
`passage` retains the existing bidirectional/default doorway-width behavior.
Sentencing's two Brig boundaries use separate one-tile gates: entry toward Brig
is `a_to_b`, and exit toward Sentencing is `b_to_a`. Their cells must be disjoint.
Directional passage and native access credentials are both required; an actor
with Security credentials cannot reverse a one-way gate. A route's moving
footprint must fit its permitted crossing, so adjacent opposite-direction lanes
do not become a two-tile-wide crossing. This is local layout evidence; temporary
prisoner-card expiry and machinery operation still require native runtime checks.

Crew routes cannot silently traverse vacuum. EVA is an explicit route mode and
actor capability; EVA capability does not prove survival in a burning chamber.
Through traffic, allowed entrance neighbors and `mustTraverse` gates require
whole-graph checks, including any crew-carrying shortcuts introduced later.

## Node requirements

| Type | Meaning |
| --- | --- |
| `metric` | Bounds/preferences on exclusive or aggregate area, aspect ratio, fill ratio, connected components, clear passage width or maintenance wrapping fraction. |
| `perimeter` | Required interface frontage onto a named domain. Exterior-connected vacuum differs from an enclosed courtyard. |
| `access_policy` | Permitted entry spaces, public/maintenance/exterior ingress, through traffic and spaces that must be traversed. |
| `docking` | Bound interface, empty berth, port offset, padded approach width/length and exterior connectivity. |
| `environment` | A required environment profile, optionally a minimum vacuum gap from inhabited hull for an outboard hazard. |

An empty access `entryFrom` list leaves neighbor identity unrestricted; other flags
still apply. A nonempty list limits interior physical-space entrances. Exterior
domain interfaces are controlled independently by `allowExteriorEntry`. The list
permits neighbors; it does not construct doors to all of them. `mustTraverse`
restricts approach paths. An EVA vestibule deliberately allows exterior entry;
a deep maintenance store requires traversing maintenance instead.

Dock offsets use the docking boundary's tangent; width runs along that boundary,
depth outward. `clearance` pads the berth and its approach. With
`approachToExterior: true`, a short empty pocket is insufficient: the lane must
remain clear to unbounded exterior. The visiting shuttle is a distinct engine
grid. Docking and frontage have separate proofs.

## Features, assemblies and circuits

Feature counts are per room instance. Each ordinary feature instance chooses one
compatible prototype from its family. `clearance` reserves working/approach area.
A feature with an empty prototype list is an explicitly described abstract service
contract, not an invisible spawned entity. The gas reserve collector refers to
the real canisters already listed in that room rather than adding duplicate banks.

An assembly references actual parts with local tile offsets and quarter-turn
rotations. Native +X is east, +Y north; zero rotation faces south and positive
degrees rotate counterclockwise (90 faces east). Ports bind exactly one
`nativeNode` or `nativeComponent`; the TEG's startup APC receiver is a component,
not a nonexistent gas/power node. Feature ports must match the assembly exports.
When a feature names an assembly, its parts are the realization of that feature.
Its prototype family identifies the anchor role; it is not an extra entity to
spawn in addition to the assembly's anchor part.

Service `medium` identifies a compatible circuit contract (`hot_loop`,
`cold_loop`, `HVPower`, etc.), not a chemical composition inferred from a name.
`distinctCircuit: true` forbids merging different medium identities. It does not
require each leg of the same circuit to be electrically or pneumatically isolated.
Hot and cold TEG returns stay separate. `targets: all` on the radiator return
requires every selected radiator to receive its return leg. Pump delivery links
are independent from the supply/return route and working-gas requirements.

Service `flow` defaults to `forward` (`from` provider -> `to` consumer).
Use `flow: reverse` to declare an incoming service need **at its consumer**:
the obligated `from` feature has an input port, and the target provider has an
output port. Physical supply runs target -> source; presence obligations still
run from the selected consumer to an eligible provider. An omitted optional
consumer does not force a supplier. Substation HV input and the power room's MV
input use this form. This authored distinction does not infer a global network.

## Choices and extensions

Local choices count selected sibling **template families**, not their instance counts.
Their limits apply per common parent instance. Dependencies still have to respect
disabled targets and choice maxima. Mandatory children of an absent optional
suite do not force the suite into existence.

New rooms, groups, interfaces, edges and combinations of built-in constraints
are data changes. New physical semantics require a declared extension and an
evaluator implementation. Extensions name `appliesTo`, `evaluator` and a closed
JSON `parametersSchema`. Unknown undeclared types fail. Declared extensions have
their parameters checked and are listed as external evaluator requirements.

The catalog uses this mechanism for local TEG/clinical/cold-store operation,
device binding, research-service association, input/output handling, airlock
cycling, and local utility services as well as vacuum/sunlight exposure. These
rules do not enumerate station-wide consumers or prescribe an upward propagation
algorithm. A local offered service and an external consumer need are independent
declarations until a later algorithm connects and verifies them.

The later evaluator must also resolve parameter-local
feature/port names and prove the physical condition. Structural validation of
those strings does not provide that proof. The shipped catalog's local names are
covered by catalog tests. An unsupported mandatory extension must prevent future
generator acceptance; setting a low weight cannot make it optional.

An extension's opaque parameters are not rewritten by the core reference resolver.
Local feature/port names refer to its subject; the artifact reach preference names
the local `analyzer_binding` relationship and evaluates **that binding's actual
pairs**, rather than independently rematching a convenient analyzer. Standard
relationship endpoints always use the core relative-reference rules.

## Local setup and operation patterns

The [third map pass](LOCAL_DEPENDENCY_REVIEW.md) uses several additional local
patterns without changing the core hierarchy:

- `x_signal_binding` persists source/sink signal ports and permits fan-out. It
  does not inherit the artifact mechanism's one-to-one exclusivity. Burn-chamber
  ignition is one such explicit signal use. Native genpop custody does not use
  timer-to-gate signal wiring: Sentencing's property lockers create their own
  linked IDs, and expiry changes ID access for the directional Brig turnstiles.
  The catalog's local custody requirement retains independent locker/ID pairing
  and runtime expiry/retrieval obligations; it does not create individual
  timed-cell rooms or a static one-locker/one-gate binding.
- Cargo receiving requirements start at the optional pad and refer to an
  approving console. The physical signal runs console -> pad, while the local
  obligation remains owned by the pad. Multiple consoles may share it.
- `player_setup` versus `commissioned` distinguishes available construction
  inventory and reserved completed geometry from initially completed machinery.
  Native target assemblies reuse existing feature roles or deployed kit contents;
  they do not add duplicate machines. Neither state implies firing at round start.
- Conditional containment profiles name the generator prototype and its required
  collector/grounding families. Unused families remain optional; the later local
  evaluator must activate and prove the matching branch for each selected instance.
- Loading/ejection and operator approaches are relative to actual native poses
  and host spaces. A generic floor-area budget alone supplies no directional
  clearance proof.

These are declared evaluator obligations, not implementations of machine startup,
physics, layout propagation or network construction.
