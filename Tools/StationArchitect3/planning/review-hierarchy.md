# Hierarchical solver review — proposal, not implementation

This review informs the next generator design. It does not change the v3 YAML
contract, select catalog instances, implement evaluators, or certify a layout.
It is based on `FORMAT.md`, `CONTRACT.md`, `validate.py::compile_document`, and the
current HoP, hospitality and TEG declarations in `catalog/standard-station.yaml`.

## Keep three scopes separate

1. **Authored ownership:** retain the room/group that declares each rule, its
   scoped ID, evidence and priority. A consumer's incoming power requirement stays
   on that consumer even when its provider is elsewhere.
2. **Evaluation scope:** the variables needed to prove the rule. A seam involves
   two room instances; an access prohibition may depend on a whole traversable
   component. This can exceed the owning subtree.
3. **Repair authority:** the smallest ancestor that can change the required
   geometry, selections, shared route reservation or interface allocation. This
   is the LCA of the affected **instantiated resource owners**, including shared
   infrastructure, rather than simply the LCA of endpoint template strings.

The instance tree remains the principal budget and edit boundary. Add a separate
constraint dependency graph for interactions across that tree. An interaction
does not reparent rooms or turn an entire department into a rigid placement block.
Mandatory seams form small coupled placement problems; service links and presence
dependencies do not imply physical adjacency.

The first attempt to repair an externally owned relationship can still be local:
move its source or change its local port while honoring the target's existing
contract. Coordination rises only when a change needs authority outside the
current scope. A request can be mediated without moving anything if existing
compatible offers already satisfy it.

## Instance selection precedes geometry but remains revisable

Build concrete IDs from template paths plus all repeated ancestor indices.
Resolve count choices, conditional feature/prototype branches and endpoint
matching before evaluating a selected layout. Preserve alternatives for
backtracking. `same_parent`, `same_owner`, `distinct` and choice cardinalities
operate on instances, exactly as documented; a template-level graph is
insufficient.

Selecting a source activates its `require_target` obligations. Deselecting an
optional parent deactivates its descendants; mandatory children do not activate
their absent parent. Mandatory children/targets cannot be dropped as a geometric
repair. An optional branch may change only within its actual count, choice and
dependency rules. Prototype-dependent containment support must follow the chosen
generator variant rather than activating all alternatives.

Search may revisit selection when geometry fails. Record this as a choice change,
not as a requirement waiver. Rebind and invalidate all affected concrete edges.
Retain actual chosen pairs for later evaluators, including artifact binding range
preferences and distinct matching; they must not independently rematch.

## A subtree exports a contract, not an area total

Each selected subtree maintains a versioned local state and a small portfolio of
candidate realizations. A parent-facing summary contains:

| Field | Required meaning |
| --- | --- |
| Identity and conditions | Instance ID, selected descendants/prototypes/bindings, active branch conditions and source rule IDs. |
| Geometry offer | Actual descendant footprint or conservative occupied mask, exclusive/aggregate area, supported surfaces, grid, prohibited volume and reserved clearances. A group has no invented floor. |
| Deformation offer | Moves or boundary bands the subtree can currently accept; conditional area/frontage slack with costs. These are offers to re-solve, not guarantees that arbitrary cells can be added or removed. |
| Boundary ports | Credential-specific walking entrances, service terminals, hull interfaces and geometric approach regions, with orientation, width, interval and role. |
| Unresolved needs | External endpoint/binding candidates, route and service obligations, frontage/berth/exterior needs, same-grid conditions, and remaining local failures. |
| Internal witnesses | IDs of proved active internal obligations and the external assumptions on which those proofs depend. |
| Scores and versions | Exact local cost contribution, aggregate sufficient statistics, geometry/network/selection/evaluator versions and witness dependencies. |

Candidate alternatives are correlated. A realization offering a north-facing
counter and an east staff entrance cannot be combined with the area or clearance
offer of a different realization unless that combination is solved. Preserve an
alternative ID tying ports, shape and witnesses together. A rectangle and area
interval are useful broad-phase estimates, never a feasibility certificate.

There are two kinds of upward communication:

- **Normal summary publication:** advertise external needs and verified internal
  results so parent packing can reserve useful space from the start. This does
  not promote or duplicate locally authored constraints.
- **Repair escalation:** ask for an external change only after local repair has
  failed, or when the proposed change is structurally outside local authority.

This distinction avoids waiting until the station is packed to discover that a
local room needs hull frontage. It also keeps an already satisfied HoP counter
out of the station optimizer's active variable set while its contract is stable.

Compose child summaries conservatively. A parent can hide internal machinery,
but must preserve unresolved exterior needs, aggregate bounds and cross-boundary
effects. Do not report a conditional proof as unconditional satisfaction. Shared
space, interface and route capacity require reservation ownership; two siblings
cannot spend the same slack or counter seam independently.

## Local-first repair transaction

For a failed rule or proposed improvement:

1. Locate its declaring instance, active bound participants, changed resources
   and current witnesses. Determine the smallest authorized repair scope.
2. Try feature pose changes, local rerouting, boundary smoothing, entrance shifts
   and room growth inside existing reservations. Evaluate the owning room's
   aggregate/containment constraints and affected neighbors, not just its area.
3. If needed, coordinate a small connected set of siblings: trade boundary tiles,
   slide a shared seam, exchange shape slack, rotate a suite or rebuild its local
   layout. Borrowed area must leave the donor feasible, including connectivity,
   feature clearances and active parent bounds.
4. If the available alternatives fail, publish a structured request to the next
   owner of the needed resource. Include the rule IDs, attempted moves/budget,
   boundary conditions, candidate remedies and smallest known conflict set.
   Examples: a wider frontier, a different public-hall port, a supported utility
   route, a hull segment, or permission to move a neighboring suite.
5. Parent first tries offers from affected siblings and shared circulation. It
   can reallocate reservations and ask children to re-solve without reopening
   their unaffected internals. Escalate again only for unresolved external needs.
6. Evaluate the whole affected dependency closure and ancestor aggregates before
   commit. Commit geometry, allocation, binding and witness changes atomically;
   otherwise restore the prior state. Downward callbacks refresh contracts after
   a parent move.

Growth bids therefore combine local room pressure, ancestor area/shape pressure,
interface usefulness, donor cost and circulation reservation cost. A department
above its aggregate maximum cannot grow merely because one child is small; the
first repair is redistribution inside that department. Neither parent nor child
preferred area becomes a hidden hard quota. Compute actual aggregate changes
once from disjoint physical floor; do not add parent and child area twice.

The local search works on bounded multi-step transactions. It may temporarily
violate a constraint inside an uncommitted transaction, which is necessary for
some doorway or seam moves. Keep the last feasible committed state separate from
the working repair state. A single-step greedy rule that rejects every temporary
regression will falsely trap otherwise repairable layouts.

## Invalidation and compact proofs

Every witness records the selected instance bindings, rules/evaluator versions,
and the geometry, boundary, connectivity and service state it actually reads.
Maintain reverse indices from changed resources to dependent witnesses and parent
summaries. A rigid subtree translation can reuse relative internal seam proofs
when all read dependencies transform with it; external routes, hull exposure and
world obstacles must be checked again.

Positive route witnesses are not enough for access policy. Adding a new door can
break `mustTraverse` or `throughTraffic` without touching the existing permitted
route. All-path rules need graph/cut certificates keyed to all relevant boundary
ports and credentials. New edges, changed door credentials and shortcuts must
invalidate those certificates. Initially, recompute the affected traversable
component conservatively; do not invent an incomplete compressed graph for speed.

Likewise, exterior-connected vacuum, berth approach lanes and collector exposure
depend on added obstacles, not just previous witness tiles. Evaluators must report
their exclusion region or use conservative spatial/network version dependencies.
Unsupported or dependency-opaque extensions cannot silently retain cached success.

Each rule is scored at its declaration exactly once. Parents aggregate child cost
and separately evaluate their own aggregate rules. Projecting a local need upward
must not multiply its weight at each level. Ancestor geometry pressure can be
passed down as a derivative or marginal cost, while exact acceptance uses the
original rule set.

## Hard rules, uncertainty and termination

Separate four outcomes:

- **Satisfied:** the active predicate has an evaluator witness for current state.
- **Violated:** the current candidate is known to fail; another candidate may work.
- **Unknown/search exhausted:** the configured search failed to find a witness.
- **Proven infeasible under assumptions:** exhaustive search, an exact solver or
  a sound bound proves no solution within a stated finite scope and boundary.

“Could not fit locally after 200 moves” is the third outcome, never the fourth.
Even an exact local infeasibility result applies only to its envelope, bindings,
external reservations and selection conditions. Parent changes may invalidate it.
Persist only sound conditional no-goods as infeasibility knowledge. Heuristic
failed-state records are retry/tabu hints, not pruning proofs.

Mandatory failures prevent acceptance. Their weights order repair effort, not
compensating tradeoffs. Desired costs and preferred numeric values optimize only
within feasible outcomes, respecting tiers and scales. During incomplete search,
separate the feasibility heuristic from the final objective and permit bounded
diversification. Never lower hard priorities or quietly drop a room to finish.

Bound each transaction, scope escalation and restart by configurable deterministic
work budgets. Carry a request ID and versions so the same unresolved request
cannot bounce unchanged between parent and child. Root exhaustion returns the
best diagnostic candidate plus unresolved obligations and search history, not a
usable-map success. Unknown mandatory evaluator semantics are a capability blocker
for certification, not a reason to increase optimization time.

## Concrete traces against the shipped catalog

### HoP office and queue

`command/personnel::hop_serves_queue` requires the two sibling instances to share
a two-tile controlled service seam. The group first reshapes those two rooms,
reserving approach floor on both sides. It separately maintains the office route
to Command circulation excluding the queue, and the queue's public-hall route.

If the queue loses public approach, try changing its own entrance, then reshaping
the pair within its allocation. If this needs an unavailable strip of Command
space, Command negotiates a sibling boundary. If the public-hall branch must
move, the owner of that hall resource coordinates the change. The repair does
not replace the counter by a corridor or treat it as the office's staff door.
Once solved, Command sees the suite's footprint and separate public/staff ports;
the witnessed internal seam need not be optimized globally.

### Hospitality

The kitchen and bar must serve the **same** commons in their hospitality instance,
with separate three- and four-tile counter seams. Cold storage has a kitchen door.
The group explores L-shaped, opposing or bent arrangements with compliant room
geometry and independent staff circulation. A seven-tile perimeter total is not
sufficient: each seam needs its own continuous run and approaches.

When a compacting move pinches a counter approach, move furniture locally, then
reshape the commons and either staff room together. Preserve cold-store access.
If the group cannot fit its current allocation, ask Service for a specific strip
or an alternative position; Service may move the whole suite while keeping its
internal proofs. Do not bind a bar to another hospitality instance or split one
mandatory seam merely to improve departmental compactness.

### TEG and external services

The TEG group locally arranges generator room, burn chamber and radiator, preserves
hot/cold native circuits, and binds the ignition button to all selected chamber
igniters in that instance. Its advertised alternatives include supported station-
grid geometry, habitable control access, separated vacuum exposure and fuel/startup
terminals. They do not collapse to “three adjacent Engineering rooms.”

`generator_room::teg_startup` remains a reverse-flow need at its TEG feature;
`burn_chamber::burn_fuel` remains a reverse-flow need at its fuel header. First try
compatible local terminals and routing against the current Engineering power and
Atmos offers. If a supported route or authorized exterior span cannot be obtained,
Engineering mediates a joint repair; hull reservation conflicts may then reach
the station level. Same department or nearby placement supplies no cable/pipe
proof. Moving the TEG invalidates world-relative exposure and incoming route
witnesses even when internal circuitry is unchanged.

The finite startup source and generator output can form a functional dependency
cycle without a presence contradiction. Selection activation uses a fixed point;
commissioning still needs the declared startup state/energy evaluator. Neither a
graph cycle nor ordinary utility connectivity proves successful startup.

## Interaction with density, variety and maintenance

Publish hard seam/clearance reservations and genuine exterior needs early, then
use flexible department allocations rather than rectangles. Try several feasible
local topologies, room orderings, interface orientations and growth frontiers before
committing. Preserve those candidate differences when packing departments; random
boundary noise alone will not create meaningful layout variety.

Treat public halls and maintenance as owned shared resources with explicit width,
connectivity and coverage obligations. Growth cannot consume a reserved two- or
three-tile maintenance corridor one tile at a time. A parent can reroute a corridor
in a transaction that proves replacement coverage and connectivity before commit.
Overwide maintenance residuals require either feasible transfer to nearby rooms
or selection of an explicit maintenance room with its own requirements. They are
not counted as passage slack.

Department clustering and station compactness should be configurable soft goals
unless explicitly authored as hard bounds. Maintenance coverage and narrow
passages must have explicit measurable predicates, not emerge from unused space.
Never silently exempt named departments: use authored tags/roles/selectors and
declared weights for distributed facilities or permitted splits. Hull hazards and
shuttle approach reservations retain their own semantics during compaction.

## Main risks to resolve before implementation

- Scalar area budgets and independent port intervals can accept impossible
  combinations; preserve candidate geometry and correlated offers.
- A local-first solver can repeatedly deadlock on circulation owned elsewhere;
  define resource ownership, reservation exchange and bounded escalation upfront.
- Witness caching can miss new bypasses and blocked exterior approaches; begin
  with conservative invalidation and optimize only with verified dependency sets.
- Hard-rule repair scores can masquerade as permission to violate low-weight
  requirements; keep certification completely separate from search guidance.
- Optional branches can disappear to make every seed trivial; honor authored
  preferred selections and report the chosen program alongside layout diversity.
- A fully supported geometric prototype is useful, but the current catalog's
  mandatory native-operation extensions still require evaluators before a final
  usable-map claim is justified.
