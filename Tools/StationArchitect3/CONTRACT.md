# V3 authoring contract (implementation coordination)

This directory is independent. The first deliverable is YAML, a strict authoring
validator/compiler, map evidence and documentation. There is no layout generator,
stage editor, imported v2 project state or requirement to preserve stage edits.

## Root

`format: ss14-station-architect`, `schemaVersion: 3`, `id`, `name`, `description`.
Collections: `defaults`, `accessProfiles`, `environments`, `grids`, `domains`,
`interfaces`, `assemblies`, `spaces`, `extensions`,
`evidence`.

The current authoring form declares `relationships` and `choices` on each space,
not at the root. Root lists remain optional compatibility inputs. Relative refs
are `.`, `#feature`, `./child`, `../sibling` and `/absolute/path`; bare canonical
paths remain accepted for earlier fixtures. Resolution uses declaration scope.
Local source endpoints must be inside that scope; external targets are allowed.
IDs are scoped; canonical/compiler views retain authored IDs and declaration scope.
Upward propagation, abstraction and global network inference are deferred.

IDs are lowercase snake_case, unique within their collection. Space IDs are unique
among siblings; slash-separated paths are canonical. A space has `id`, `name`,
`kind` (department/group/room/passage), `description`, `tags`, `count`
(`min`, `preferred`, `max`), `placement` (`nesting`: owned/contained,
`zone`: interior/hull/outboard/any, `grid`: grid ID, optional physical `surface`:
floor/lattice/any), `environment` (environment ID
for physical room/passage), `access` (profile ID for physical spaces),
`requirements`, `features`, `spaces`, `evidence`.
Departments/groups are nonphysical aggregates. Rooms own exclusive floor; their
contained descendants own disjoint floor inside the parent's complex envelope.
`owned` descendants have logical ownership but no containment implication.
Counts under repeated parents are per parent instance.

`defaults.requirement` is `{priority: mandatory, weight: 100}`. Every rule may
override it with `priority: mandatory|desired`, positive `weight`, and optional
`tier` (nonnegative integer, default 0). No weighted tradeoff satisfies a violated
mandatory rule. Desired penalty = weight * normalized violation; a target in
metric bounds adds soft pressure without weakening mandatory min/max.

## Endpoint and relationship

Endpoint is EXACTLY ONE of `{space: canonical/path}`, `{domain: id}`,
`{feature: canonical/path#feature_id}`, or
`{selector: {tagsAll: [...], tagsAny: [...], kinds: [...], within: canonical/path}}`.
A selector must specify at least one nonempty filter. Filters combine AND, tagsAny
combines OR. `within` includes the named space and descendants. Unknown exact paths
are errors even when an endpoint is currently optional.

Relationship: `id`, `type`, `from`, `to`, `matching`, `priority`, `weight`, `tier`,
`description`, `parameters`, `evidence`, `basis`.
`matching`: `targets: one|all|distinct`,
`scope: station|same_owner|same_parent`,
`presence: require_target|if_both_present`.
Each selected source instance imposes an edge. Absent optional sources impose no
edge. `require_target` forces compatible target presence (never silently vacuous).
`distinct` requires injective matching; `same_parent` means the same instantiated
common containing parent, not just equal template strings. `all` means every
selected eligible target. Compile symbolic instance bindings; do not invent one
arbitrary endpoint for an unresolved `one` choice. Domains have no instance count.

Types and parameters (closed dictionaries):

- `shared_boundary`: `interface` ID, `minLength`, optional `preferredLength`,
  `maxLength`; `minSegments`/`maxSegments` (default 1); `continuous` bool.
  Length counts occupied interface seam tiles, not walking-path distance. The
  authored interface capabilities determine whether this also supplies passage.
- `route`: `mode` crew/maintenance/eva, `accessProfile`, `minWidth`, optional
  `maxLength`, `through` list of exact space paths, `within` owner/parent/station,
  `avoid` list of space paths. Empty `through` means no required waypoint.
  Ordinary crew routes cannot use vacuum without an EVA-capable profile.
- `separation`: `minDistance`, optional `preferredDistance`, `maxDistance`,
  `metric` boundary_manhattan/walking; optional `accessProfile` for walking.
- `service_link`: optional `flow` forward/reverse (default forward),
  `system` gas/power/disposal/data, `fromPort`, `toPort`,
  `medium`, `sameGrid` bool, `support` floor_or_lattice/any, optional `maxLength`,
  `distinctCircuit` bool. Endpoints must be features with declared compatible ports.
  Reverse flow makes `from` the consumer and `to` the supplier; source presence
  still owns the obligation. This preserves local incoming-service declarations.
- `grid_relation`: `relation` same/different and optional `bridgeAssembly` ID;
  proximity on different engine grids never implies native pipe/cable connectivity.
- `dependency`: `reason` string (presence only, supplies no spatial proof).

Node requirement: `id`, `type`, `priority`, `weight`, `tier`, `description`,
`parameters`, `evidence`, `basis`. Its subject is the containing space.

- `metric`: `metric` area.exclusive/area.aggregate/shape.aspect_ratio/
  shape.fill_ratio/shape.components/corridor.clear_width/maintenance.wrap_fraction,
  `min`, `preferred`, `max` (at least one), `scale` positive.
- `perimeter`: `domain` ID, `minLength`, optional `preferredLength`, `interface`
  ID, `continuous` bool. Exterior-connected space is distinct from an enclosed
  vacuum courtyard. This proves frontage, never a docking lane or gas circuit.
- `access_policy`: `entryFrom` list of exact space paths, `allowPublicEntry` bool,
  `allowMaintenanceEntry` bool, `allowExteriorEntry` bool, `throughTraffic` bool,
  `mustTraverse` list of exact space paths. Explicit entryFrom is an allow-list
  including parent floor for nested rooms, independent of route requirements.
- `docking`: `interface` ID, `berthWidth`, `berthDepth`, `portOffset`, `clearance`,
  `approachWidth`, `approachLength`, `approachToExterior` bool, `source` evidence ID.
- `environment`: `profile` ID; optional `minVacuumGap` (outboard hazard separation).

Features: `id`, `description`, `count`, `prototypes` list, optional `assembly` ID,
`ports` list `{id, system, medium, direction: in|out|bidirectional, grid: host}`,
`clearance` `{workingTiles, approachWidth}`, `evidence`. Features may be abstract
service ports (no prototypes) but must declare that in description. An assembly
binds actual parts and exact native local pose, distinct from room-area requirements.

Assembly: `id`, `description`, `parts` list `{id, prototypes, x, y, rotation}`,
`ports` list `{id, part, nativeNode|nativeComponent, system, medium, direction}`, `sameGrid` bool,
`evidence`. Coordinates are relative native tile offsets (+X east, +Y north).
Rotations are degrees counterclockwise from native south: 90 faces east.
Never equate shared logical parent with same grid.
Exactly one nativeNode or nativeComponent is specified; APC receivers are component
endpoints rather than fictitious NodeContainer nodes.

Choice: `id`, `description`, `members` canonical space paths,
`minSelected`, `preferredSelected`, `maxSelected`, `priority`, `weight`, `evidence`.
Counts selected TEMPLATE families, not number of instances. Choices apply per
common parent instance; members must share their direct parent. A mandatory
dependency can force an optional target and must still respect choice maxima.

`accessProfiles`: list `{id, description, levels: [...], canTraverseVacuum: bool}`.
`environments`: list `{id, description, pressure: standard|vacuum|process,
enclosure: sealed|open, temperature: ambient|hot|cold|custom}`.
`grids`: list `{id, description, kind: station|detached, support: solid|lattice|mixed}`.
`domains`: list `{id, description, kind: vacuum|station_hull,
connectedToExterior: bool}`.
`interfaces`: list `{id, description, capabilities: [crew_passage,item_transfer,
visibility,speech,gas_transfer,power_transfer,docking], construction: [prototype IDs],
normalState: open|closed|controlled, access: {anyOf: [[native credentials]]}, airtight: bool}`.
Each inner credential list is AND; alternatives are OR. An empty `anyOf` is public.
Capability `crew_passage` means a routine designed route, not a claim that a
counter can never be climbed or destroyed in gameplay. An airtight controlled
interface is airtight when closed, not during a transfer/open cycle.

`evidence`: list `{id, kind: map|native|user|policy, source, description}`.
Map source is repo-relative `path#uid=N` (may identify a landmark rather than a
proven boundary); source descriptions distinguish observations from authored policy.
`basis`: user_requirement/native_mechanic/map_pattern/design_policy.
New spaces/interfaces/relationships need only data. New evaluator semantics use
`extensions`: `{id: x_namespace_name, appliesTo: node|relationship, description,
evaluator, parametersSchema}`; strict JSON Schema validates extension parameters.
Declared extensions can be authoring-valid but must be listed as requiring an
external evaluator. A future generator must refuse unsupported mandatory types.

## Validation scope

Reject unknown fields, duplicate YAML keys, aliases, unknown references, invalid
bounds, nonsensical rule/endpoint combinations, false capability claims (e.g.
space domain as crew room), mandatory dependencies impossible under count/choice
bounds, and invalid nested-count/budget arithmetic. Do not claim geometric
satisfiability: no layout exists in this deliverable. Compiler emits a symbolic
tree, binding candidates, activation implications and remaining choices.
