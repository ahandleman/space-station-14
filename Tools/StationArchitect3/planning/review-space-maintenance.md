# Spatial and maintenance review for the V3 generator plan

Status: planning recommendations, not implemented generation behavior. This review
reads the current V3 definitions and the earlier tools as references. It changes
neither the catalog nor either earlier tool. The authored semantics in
[FORMAT.md](../FORMAT.md) remain authoritative.

## Recommended model

Use **negotiable room patches and reserved circulation**, solved recursively over
the instance tree. A department receives capacity, frontage and circulation
opportunities, not a prescribed rectangle. Children may exchange boundary strips
and move within those opportunities. A parent can change the allocation only
when a child cannot repair an obligation within its current contract.

Keep three structures separate:

1. The authored ownership/containment tree, including repeated instance identity.
2. A spatial interaction graph: required seams, approaches, exclusion envelopes,
   route terminals and supported service corridors.
3. A physical cell/edge realization with actual circulation and structural space.

The first structure explains responsibility; the second explains what must fit
together; the third provides witnesses. None can substitute for the others.
Maintenance is an active participant in allocation, not the final complement of
department floor. Public circulation and maintenance have different route graphs,
widths, access semantics and budgets.

## Physical representation

At the detailed level, use the native one-tile grid with an explicit cardinal
edge layer and independent occupancy layers:

| Layer | Stored information |
| --- | --- |
| Surface | Usable owner instance, structural-only cells, unassigned buildable cells, supported lattice, or vacuum. Exactly one usable owner per cell. |
| Interfaces | Seam runs, normal/tangent, interface family, neighboring instances, reserved structural footprint, access expressions and approach clearances. |
| Installations | Feature/assembly footprints, rotations, directional loading, operation and construction envelopes, and ports. |
| Circulation | Public, departmental and maintenance route corridors; feasible terminals; minimum-clear-width witnesses. |
| Exclusions | Dock berth/approach, deployment area, exterior exposure, vacuum gaps, service clearances and their originating requirement IDs. |
| Networks | Grid identity and typed service channels; these may coexist with allowed floor/structure but do not imply a crew route. |

The seam abstraction must reserve the actual structural thickness before room
areas are committed. A full-tile wall or counter cannot materialize later for
free between two already allocated floor cells. A directional thin interface
uses its own supported geometry and collision/approach rule. Count usable area
separately from these structural reservations. A seam is satisfied only after
its construction and the two sides' clear approaches fit; corner contact and
adjacent bounding boxes are insufficient.

Store each room as a connected orthogonal polyomino with a small set of editable
boundary runs. A contained child's cells remain separate from its parent's
exclusive floor. The parent complex envelope includes its selected contained
descendants and their enclosing structures, without double-counting floor.
Logical groups and departments have derived unions, not additional floor masks.

Parent allocations can be represented by a sparse claim field plus reservations:
allowed build region, feasible area interval, preferred center/side, candidate
boundary runs and exterior obligations. Claims are negotiation aids, not immutable
ownership. Two sibling claims can overlap provisionally; committed usable floor
cannot. A parent transaction resolves overlap before accepting a child placement.

## Coarse-to-fine solve, with return paths

The following are internal solver passes, not a new requirement to expose manual
editing stages. Every pass can request a repair of an earlier decision.

1. **Choose an instance configuration.** Resolve counts and required activation,
   retaining alternative target matches. Compute bottom-up area intervals and
   lower bounds for known assembly, seam and approach capacity. Record unknown
   evaluator bounds as unknown; do not replace them with zero required area.
2. **Build local motif candidates.** For each small suite, generate several
   arrangements of mandatory seam relationships, entrances and large fixtures.
   Hospitality can place Kitchen and Bar on different sides of the same Commons;
   HoP can rotate or move its line while preserving one service interface and a
   separate staff entry. Candidates are variable shapes with boundary contracts,
   not hand-painted fixed room templates. A required seam cycle remains a graph
   embedding problem; do not collapse the whole cycle into a rigid rectangle.
3. **Reserve scarce exterior opportunities.** Fit provisional docking lanes,
   outboard process/cooling regions, solar approaches and deployment clearances
   against a bounded search canvas with an exterior boundary. These reservations
   can move as coherent alternatives. They cannot be eaten by compaction.
4. **Place department clusters and a public route scaffold together.** Coarse
   cells, initially four tiles across as a search setting, are suitable for
   centers, broad claims and alternate arrangements. They do not certify a seam,
   a two-tile passage, a hull window or a docking clearance. Preserve multiple
   nondominated placements rather than committing every room to one parcel.
5. **Route a maintenance scaffold through inter-cluster gaps.** Reserve a narrow
   connected network, relevant gateway approaches and candidate wrap arcs at the
   same time as department claims. Reserve bounded pockets for mandatory true
   maintenance rooms. The first scaffold is deformable, not a permanent rectangular
   ring around every department.
6. **Refine each department and suite to tile geometry.** Place large fixtures
   and required seam runs early. Grow coherent room patches toward feasible area
   targets, respecting local routes, contained children, frontages and reservations.
   Route internal circulation alongside growth. Re-estimate real structural and
   circulation cost; send capacity deficits upward before all neighboring space
   is consumed.
7. **Negotiate boundaries and compact.** Exchange room strips locally; relocate
   suite blocks or shift a circulation segment when strip changes are insufficient.
   At the parent scope, move adjacent clusters together with their maintenance
   separator. Fill suitable residual pockets with permitted room growth or selected
   true maintenance rooms. Unsupported fragments revert to exterior or trigger
   a reshape; they do not become anonymous maintenance floor.
8. **Validate full geometry and route policies.** Recheck widths, exits, mandatory
   seams, all-path restrictions, docking/exterior connection and nested/aggregate
   sizes. Retain geometry-valid candidates while reporting unevaluated native or
   runtime obligations. Later utility installation may reopen the affected local
   solve; it cannot silently overwrite a certified route or operating clearance.

Use a small beam of alternatives at department boundaries and a bounded local
search inside each alternative. A reasonable initial experimental setting is
four alternatives per parent and three topology alternatives per compact suite;
these are tunable search budgets, not feasibility limits. Exhaustion means
`search_exhausted`, not `infeasible`. Whole-station reseeding is the last search
level after local/sibling alternatives, and still does not prove impossibility.

## Local moves and escalation

Prefer moves that transfer a coherent strip or patch rather than adding random
single-cell noise:

| Scope | Useful transactions | Protected checks |
| --- | --- | --- |
| One room | Reorient a feature, slide its approach, fill a shallow notch, trade area between boundary runs, reshape around a contained child. | Exclusive area, usable clearance, connectivity, authored aspect/fill limits, existing seam/entry witnesses. |
| One suite | Move a shared seam, give floor from one sibling to another, swap two flexible children, rotate the whole motif, replace its local route. | All suite obligations and ancestor aggregate bounds; same-parent matches remain unchanged. |
| One department | Shift a suite, change internal circulation, allocate an optional filler room, trade capacity between suites, create an allowed second wing. | Department bounds, access restrictions, sibling minima and boundary contracts. |
| Neighboring departments plus circulation | Transfer a build strip, move both clusters with their separating maintenance, switch exterior-facing candidate, reroute a wrap arc. | Every affected owner's budget and witnesses; circulation continuity, width and exclusion envelopes. |
| Station | Change the broad layout alternative, move a dock lane, expand the core canvas, redistribute stationwide clusters. | Authored global physical obligations and the affected local contracts; no mandatory relaxation. |

An owned room cannot independently take another owner's floor. It asks for a
specific patch or missing boundary opportunity; the smallest common spatial
coordinator makes a transaction involving both parties and maintenance. The
authored relationship stays with its source or suite. Returning a request such
as “need a four-tile counter-facing side and two-tile staff approach” is more
useful than returning only “need 12 extra tiles.”

Maintenance can reject a transfer which disconnects a route or drops wrap below
its promised level. It can propose a replacement bend or shifted strip within
the same transaction. Department growth must not remove a maintenance strip and
hope a later pass finds a different path. Likewise, a maintenance repair cannot
silently shrink a room below its minimum or cut a required counter in half.

Within a tentative repair, temporary hard violations may exist in private search
state. A commit must restore all previously protected hard witnesses affected by
the transaction or explicitly return the unresolved obligations to the caller.
Cross-scope repairs should have a disruption budget and hysteresis so two
departments do not repeatedly reclaim the same boundary strip.

## Cluster departments without forcing every owner into one blob

Physical cohesion should operate on **functional clusters**, with a separate
penalty for splitting an owner across public or maintenance routes.

- Mandatory shared-boundary groups and contained complexes are strong local
  clusters. Preserve their flexibility: three rooms with two mandatory seams are
  not necessarily a rigid L or a rectangle.
- Internal-only routes and strong local walking preferences favor grouping.
  A common power provider, gas medium, station grid or data server does not by
  itself attract every consumer toward that provider. Service constraints affect
  placement through actual range, routing and capacity costs.
- Ordinary departments begin with one cluster, with high but finite cost for
  allowed splits. A mandatory component limit remains hard. Splitting is allowed
  only if every selected room still has its required internal/access route.
- `distributed` is already a catalog tag on Commons and Service. An explicit
  solver-profile selector may give tagged owners lower split cost and target
  multiple destination clusters. Tags have no hidden algorithmic effect. Keep
  Hospitality together while permitting Chapel, Library and other independent
  local complexes to occupy different public-route neighborhoods.
- Distribution should improve walking coverage of destinations. Use a soft
  distance-to-nearest-selected-destination objective across public-hall samples,
  not pairwise repulsion of every room. Repulsion alone makes a sparse station.
- `maintenance_rooms` may be distributed by an explicit profile selector too.
  A deep store is not free department filler: it has its own count, area and
  entry restrictions.

There is an evaluator-definition issue to settle before implementation:
`shape.components` currently operates on the descendants' union, while usable
floor excludes wall cells. A raw flood fill would therefore count separated
rooms as owner fragmentation even when they share an internal wall. Define and
document the metric's exact raster/contact convention before using it. One
possible explicit new cohesion metric counts components of a same-owner room
contact graph, where actual internal seams connect nodes but intervening public
or maintenance floor does not. Keep this distinct from both raw usable-floor
components and credential-aware route connectivity; do not silently substitute
one for the existing authored metric.

## Reserve and realize maintenance

Represent maintenance as a cardinal route graph whose edges expand to two- or
three-tile clear strips. Turns and junctions use checked local templates. A
three-way junction can have a bounded three-by-three meeting region; it does
not license a general open chamber. Retain the route graph as a construction
aid, then verify width against the realized unblocked passage floor. A stored
`width: 2` is not proof once machines, walls or door frames are installed.

Use two checks together:

1. Every required route admits the requested clear footprint through its complete
   path, including bends and entrances. A one-tile doorway does not satisfy a
   two-tile route unless the authored semantics explicitly allow that exception.
2. Every maintenance floor patch is explained by a permitted strip or bounded
   junction template. Detect excessive open pockets, including maximal inscribed
   axis-aligned squares wider than three tiles. A square test alone is insufficient
   to characterize arbitrary branched shapes; retain the strip/junction witness.

The route graph should connect authored maintenance gateways, deep rooms and
external vestibules, with optional cycle pressure. Prefer arcs along department
rear/side boundaries, some cross-links between them, and shorter shared separators
between two departments. It need not create a complete ring around every owner;
that would consume too much floor, over-regularize shapes and isolate departments.
Allow public-hall crossings only through explicit compatible transition geometry
and access policy, never by treating all public floor as maintenance.

Track a maintenance area budget and reserved route capacity separately from
room budgets. The current catalog already assigns `/maintenance` an exclusive
area interval of 300–5000 and preferred value 1600; `/public_hall` has the same
area interval/preference, with different width bounds. These preferences must
remain visible in optimization. A proposed percentage budget is an additional
policy or an allocation heuristic, not permission to overwrite authored targets.

For residual areas, in order:

1. Absorb a coherent patch into an adjacent room that can use it within its bounds
   and without losing circulation/frontage/approach witnesses.
2. Instantiate or enlarge an allowed true maintenance room, within its count and
   area bounds, with entry through the maintenance network.
3. Reshape neighboring rooms or routes to eliminate a sliver.
4. Leave deliberate exterior vacuum if compatible with enclosure and frontier
   policies, or report the remaining packing deficit.

Deep maintenance counts and internal entry policies apply before optional pocket
filling. The current `deep_store` has min 2, preferred 3, max 6: reserve capacity
for its minimum early, rather than hoping two good leftover pockets exist. Its
`mustTraverse: /maintenance` condition is a whole-graph obligation. A new public
door, shortcut tube or crew-carrying transport can invalidate it even if its
original maintenance door remains.

External vestibules and solar controls are destinations reached through
maintenance. Reserve their approaches and connections without making a routine
maintenance route pass through vacuum. Hull windows and access points should be
explicit frontage allocations. A radiator's exposed vacuum, a shuttle's approach,
and a space-facing hall window are not interchangeable uses of the same frontier.

## Metrics and tuning controls

Keep mandatory violations separate from lexicographic weighted desired costs.
Measure each desired pressure once at its defined scope; a child area deficit
must not be charged identically again at every ancestor as though it were several
independent problems. Parent capacity scarcity can still be a distinct pressure.
All new metric names below are proposals, not existing schema functionality.

| Pressure or diagnostic | Definition and guard against a misleading score |
| --- | --- |
| Core compactness | Radius of gyration or pairwise-cluster distance of selected interior/hull usable floor, normalized by a fixed reference area from the instance configuration. Exclude declared outboard installations from this core metric, while scoring their access separately. Do not divide by freely added filler area. |
| Bounding fill | Report usable core floor divided by an explicitly identified core bounding rectangle as a secondary diagnostic. Report gross station extent separately. Do not make this the only objective: docking, solar structures and legitimate wings can dominate a rectangle. |
| Unused interior pockets | Count unallocated buildable pockets inside a clearly defined planning envelope; classify required vacuum/berth/deployment reservations separately with originating rule IDs. An envelope defined as exactly the occupied cells would trivially score 100% and is unusable. |
| Maintenance wrap | For each functional cluster, maintenance-facing boundary length divided by eligible outer cluster boundary length. A counted segment needs the actual structural separator and reachable two- or three-wide maintenance alongside it. Exclude internal same-cluster seams. Declare any hull/dock-frontage exclusions explicitly and report excluded length; never let the solver invent exclusions to improve its score. |
| Wrap balance | Penalize the worst deficient clusters as well as the station average. One richly wrapped department must not compensate for three isolated departments. |
| Maintenance reach | Credential-aware walking distance from each required department gateway to connected maintenance and from maintenance to each deep room/vestibule. Nearest Euclidean distance does not prove access. |
| Maintenance useful connectivity | Fraction of required gateways in the intended component, plus optional alternate-route/cycle pressure. Dead ends serving a single deep room are not automatically errors. Cycles through unauthorized rooms or vacuum do not count. |
| Direct foreign-owner contact | Length of shared boundary between distinct departments lacking an authored requirement for that contact, normalized by their eligible outer boundary. Use this to favor halls/maintenance between departments without forcing a thick empty moat. Explicit mandatory seams take precedence. |
| Cohesion and splits | Number and area of secondary physical clusters and excess internal travel, respecting distributed-owner policy. Report contact cohesion and access connectivity separately. |
| Shape usability | Authored aspect/fill bounds plus optional short-boundary/noise, narrow-neck and dead-floor penalties evaluated on usable geometry. A room with adequate area but no valid machine approaches remains invalid. |
| Destination distribution | Coverage distances from public circulation samples to selected destination classes. Use configurable selectors and weights; do not hardcode room names. |
| Diversity across seeds | Changes in room-neighbor graph, gateway order, suite topology, cluster arrangement and normalized silhouette. Canonicalize translation/rotation/reflection where appropriate so merely mirroring a station is not counted as a new topology. |

A useful initial tuning vocabulary is: core compaction weight, ordinary-owner
split penalty, distributed-owner split penalty, maintenance wrap preference and
per-cluster shortfall weight, maintenance cycle preference, room-shape stiffness,
suite topology alternative count, optional-room selection distribution, local
repair budget and parent disruption budget. Use selector-based profiles and
explicit objective tiers. Do not bind any of these controls to `medical`, `cargo`
or another identifier in generator code.

Begin with modest geometric diversity: draw feasible room targets near their
preferred areas, sample suite orientations/topologies and shift room proportions.
Reconcile the sampled targets bottom-up with department intervals before growth.
Hard minima/maxima are never randomized. Shape variation should come mostly from
different room arrangements, unequal room sizes, fitted circulation and some
coherent alcoves/recesses, rather than forcing every small room to be nonrectangular.

Randomness must be reproducible by stable instance ID, operator and seed, not by
dictionary iteration order. Optional selection, topology alternatives and geometric
noise should have separate random streams so changing a minor furnishing rule does
not arbitrarily rearrange the whole station. Retain several near-optimal candidates
with different structural signatures instead of always choosing the same compact
rectangle with different wall noise.

## Failure cases to include in the first spatial benchmark suite

| Case | Expected behavior |
| --- | --- |
| Kitchen and Bar both need one Commons frontage | Fit two real serving seams to the same Commons instance; rotating the suite is allowed; using two independent Commons or a corridor is not. |
| Small suite has enough total area but insufficient frontage | Escalate a boundary-shape request; adding floor on the wrong side must not be reported as repair. |
| Every room can satisfy its own minimum but department maximum cannot | Reject/reselect the instance configuration before geometry growth; local floors do not bypass aggregate limits. |
| One room expands into a maintenance articulation point | Repair within the room first, then try a joint route shift; reject the transfer if connectivity/width cannot be restored. |
| Four-tile maintenance pocket remains after compaction | Grow a permitted neighbor or create a named maintenance room; do not certify a four-wide maintenance passage. |
| Deep stores have good maintenance doors and an accidental public bypass | All-path check fails and requests removal/reconfiguration of the bypass. |
| Three departments wrap well and one has no maintenance | Per-cluster deficit remains visible despite acceptable average wrap. |
| Engineering requires outboard TEG apparatus and Cargo requires a berth | Compaction keeps their clearance envelopes and real exterior connection; their vacuum is not treated as wasted interior floor. |
| Two-tile coarse gap disappears after adding walls | Tile refinement returns a structural-capacity deficit; never certify the coarse gap as a clear two-tile passage. |
| Service/Commons has separated functional destinations | Preserve local suites, improve public-route coverage and avoid repelling them to distant isolated wings. |
| High area or perimeter pressure produces tentacles | Reject nonfunctional narrow necks and unequipped slivers; reshape or request capacity rather than counting unusable tendrils as a good room. |
| New optional machine blocks a locally valid doorway | Invalidate and repair its room/approach and affected routes before accepting installation. |
| Twenty seeds are rotations of one arrangement | Diversity report identifies equivalent signatures; generate alternate adjacency/topology choices rather than more random notches. |
| A bounded local search finds no valid packing | Escalate with the exhausted search domain and tried alternatives; do not claim mathematical infeasibility. |

Benchmark comparisons should hold the selected instance configuration and its
reference area fixed when testing packing or maintenance controls. Otherwise a
smaller station obtained by omitting optional rooms can masquerade as better
packing. Separately measure diversity from optional count/choice variation.

## Reference interpretation

The V3 [room relationship review](../research/room-relationships.md) documents
different hospitality layouts, a distant HoP suite within logical Command,
separate staff/customer entries and nested access concerns. These motivate local
motifs and distributed ownership without prescribing one station layout.
The [engineering review](../research/engineering-findings.md) documents same-grid
outboard apparatus, lattice support, native assembly footprints and the distinction
between docking frontage and a usable berth/approach.

The earlier [layout evidence](../../StationArchitect/LAYOUT_EVIDENCE.md) reports
43–53% main-grid floor fill in the bounding rectangles of its sampled maps. That
is a coarse observation including wings and docking structures, not a target
for department density or an estimate of maintenance share. Its conservative
closed-door components are not valid runtime navigation graphs.

The earlier `RoomGrowth` source is useful evidence of the limitations to avoid:
growth bounded by a prior parcel can leave a local repair unable to reach needed
frontage, while later wall/circulation adjustments can consume already-budgeted
space. V3 should reserve the real interfaces and circulation capacity during the
allocation transaction and retain an explicit path for revising parent claims.
