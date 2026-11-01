# Hierarchical station generation plan

Status: design reference. The first implementation is documented in
[LAYOUT_SOLVER.md](LAYOUT_SOLVER.md), including its supported subset and remaining
work. This plan builds on the V3 local
definitions and symbolic compiler. The catalog, current format and both earlier
tools remain unchanged. The first implementation target is a small, fully checked
layout fixture; the full catalog needs additional evaluators before it can produce
a certified station. Internal passes below are revisitable solver operations, not
mandatory manual-editing stages.

## Design decision

Use **hierarchical constrained region growth with transactional repair**. Small
functional suites explore several arrangements, departments negotiate flexible
territories, and station packing reserves circulation and exterior opportunities
alongside those territories. Rooms grow from required interfaces and installations
into coherent tile patches. A repair starts with the smallest change and involves
ancestors only when it needs resources outside that scope.

Keep three structures:

1. **Instance tree:** ownership, containment, counts and aggregate requirements.
   Every repeated ancestor contributes to a room's concrete identity.
2. **Interaction graph:** actual bound interfaces, routes, services, clearances and
   dependencies. This graph can cross ownership boundaries without reparenting rooms.
3. **Physical realization:** tile surfaces, walls, interfaces, equipment, circulation
   and reserved exterior volumes. This supplies evidence for requirement checks.

A department receives adjustable space claims, not a permanent rectangular parcel.
Small suites joined by mandatory borders are strong placement units, but remain
deformable. A power or research connection does not force all consumers into one
physical cluster. Public halls and maintenance are participants in allocation,
with their own space and connectivity requirements.

## Local rules and parent contracts

Distinguish the rule's **declaring owner**, its **evaluation dependencies**, and
the **authority needed for a repair**. HoP's counter remains a personnel-suite
rule even if repairing its public approach requires moving a hall. The coordinator
is the lowest ancestor owning all resources that must change, including shared
circulation. It is not necessarily the ancestor of just the two room endpoints.

Each subtree publishes a versioned contract with a bounded portfolio of alternatives:

| Contract content | Meaning |
| --- | --- |
| Identity and selection | Concrete descendants, prototypes, branch conditions, bound targets and originating local rule IDs. |
| Geometry | Occupied footprint, structural capacity, exclusive/aggregate area, containment envelope, grid and surface needs. |
| Boundary offers | Available entrance/interface runs, orientations, approach regions, utility terminals and exterior frontage. |
| Flexibility | Specific boundary strips, rotations, relocations or area ranges it can attempt, with estimated disruption costs. |
| External needs | Unresolved routes, target matches, hull/berth/exposure reservations and other cross-boundary obligations. |
| Evidence | Internal witnesses, external assumptions, evaluator versions and geometry/network revisions. |
| Cost | Locally owned preference cost, child totals and sufficient statistics for ancestor objectives. |

Alternatives stay correlated: a north-facing counter, narrow footprint and large
east-side entrance must all belong to the same realization. Do not combine the
smallest area from one candidate with the best frontage from another. A bound or
promise to re-solve is not proof that arbitrary extra cells are usable.

**Publish needs early; escalate changes only when needed.** A TEG's exterior
requirement is visible while Engineering is first being placed. This normal summary
publication does not move the declaration out of the TEG. Once a suite is solved,
ancestors normally manipulate its footprint and ports, leaving internal choices
alone. If a parent invalidates an assumption, the affected local problem reopens.

Shared strips, interface capacity and route capacity have explicit reservation
owners; siblings cannot both spend the same apparent slack. Parent contracts pass
resource allocations and marginal costs downward, not copies of local requirements.
Score each authored rule once. An independently authored department area preference
still applies in addition to its children's preferences.

## Exact geometry and accounting

Use the native one-tile grid for acceptance. An optional coarse grid only proposes
department positions and broad routes. Maintain separate layers for:

- Exclusive usable floor/lattice ownership and unbuilt vacuum.
- Structural wall/interface footprints and cardinal boundary runs.
- Feature assemblies, collision, loading, operator and construction clearances.
- Public, departmental and maintenance circulation and entrances.
- Berths, approach lanes, exposure, vacuum gaps and deployment exclusions.
- Grid identity, credentials and typed cable/pipe/disposal/device networks.

Walls occupy real space. A two-tile planning gap does not imply two clear walking
tiles after walls are installed. Shared borders need a continuous physical interface
run and usable approaches on both sides; nearby centers or corner contact do not
satisfy them. Feature assemblies are reserved before room growth consumes their
space, including directional loading and safe operator access.

Room patches are orthogonal tile sets, connected where required by the program.
Contained children and parent-exclusive floor remain disjoint. The parent's complex
envelope includes its contained children and enclosing structure. Logical departments
and groups own no additional floor. Aggregate area counts each descendant floor
cell once; parent area cannot conceal a child's shortage or include a public hall.

Before writing geometry evaluators, resolve these contract details explicitly in
the format and fixtures (M0), with any compatibility change documented:

| Detail | Proposed decision |
| --- | --- |
| Usable area versus movement | Use a declared surface mask for area and a separate movement-clearance mask. Document which structure/fixture cells exclude usable area; furnishing must not silently change the area convention. |
| Department cohesion | Define cluster contact across actual internal structural seams. Raw floor flood-fill would count every walled room separately. Keep this distinct from credential-aware route connectivity. Document the exact meaning of existing `shape.components`, or introduce a separate metric rather than silently replacing it. |
| Passage width | Check clear strips and bounded junction geometry on realized floor. For the first fixture, no bottleneck exception: interfaces crossed by a required two-wide route must preserve that width. Any narrower authored doorway needs an explicit, separately evaluated exception before broader catalog support. |
| Exterior | Flood-fill vacuum connected to the outside of a padded finite canvas; reserve a route to that outside boundary. Closed courtyards cannot satisfy exterior access. Canvas expansion is a bounded station-level operation. |
| Soft cost ordering | Retain authored tiers and normalization. Proposed search-policy costs declare their own tier and scale; they cannot silently replace authored preferences. |

## Solving cycle

The solver alternates allocation, realization and repair; it can return to any
earlier choice when necessary.

1. **Resolve the selected program.** Use the current compiler's conservative
   checks, then select concrete counts, choices, variants and target bindings.
   Preserve alternatives. Enforce `same_parent`, `same_owner`, `distinct` and
   conditional activation on instances. Optional parents activate their children
   only when selected. A geometric failure cannot drop a mandatory room.
2. **Build local alternatives bottom-up.** Reserve large installations, contained
   rooms, mandatory seam runs and separate staff/customer routes. Explore room
   ordering, interface side and proportion alternatives. Reconcile sampled growth
   targets with ancestor intervals. These targets guide proposals; acceptance still
   scores the original authored preferred values. Publish external needs and capacity bounds;
   unknown evaluator requirements are unknown, not zero space.
3. **Pack clusters and reserve access together.** Place department/suite claims
   with provisional public routes, maintenance separators, deep-room pockets and
   scarce hull opportunities. Reserve docks, outboard apparatus and solars early.
   Retain a small beam of candidate arrangements instead of one irreversible packing.
4. **Grow and negotiate room patches.** Start at mandatory seam and fixture cores.
   Expand by coherent strips/patches, with internal circulation reserved. Bid for
   neighboring unassigned space or request a donor exchange. Evaluate affected
   room and ancestor pressures together; refine all accepted geometry to unit tiles.
5. **Realize circulation and interfaces.** Refine passages, walls, entrances and
   feature poses. A structural shortage returns a precise request to allocation;
   it is not fixed by silently reducing corridor width or room area.
6. **Repair and compact.** Apply the local-first ladder below, including joint
   movement of departments and their maintenance separators. Revisit topology or
   selection alternatives when smaller changes fail. Preserve several feasible
   solutions with different structural signatures.
7. **Verify independently.** Recompute requested checks from the realized state,
   without trusting cached success flags. Save the instance tree, layout, unresolved
   requirements, measured quality and repair trace.

Proposals use boundary runs and useful patches rather than unrestricted pixel
noise. Typical moves are notch filling, strip transfer, seam sliding, entrance
shifting, suite rotation, local route replacement and cluster relocation. Narrow
tentacles do not earn useful growth when they fail room clearance or shape rules.

For a candidate transaction, compute the exact change in locally owned costs,
affected ancestor aggregate costs and affected neighboring/circulation costs.
Use scarcity estimates to propose moves, then exact checks to accept them.
Room shortage first seeks slack inside its department; increasing a department
already at its maximum cannot be accepted merely because one room wants more area.

## Repair ladder and termination

| Scope | First moves | Reason to involve the next scope |
| --- | --- | --- |
| Feature / room | Reorient installations, move entrances, rearrange free floor, reshape inside the current claim. | The needed boundary, clearance or area is unavailable locally. |
| Siblings / suite | Trade strips, shift a shared seam, change room order, rebuild local circulation. | The suite's allocated capacity or frontage cannot support tried alternatives. |
| Department | Reallocate between suites, relocate a cluster, alter internal routes, use permitted filler or an allowed split. | Repair needs another department's space, a shared route or exterior reservation. |
| Common ancestor / station | Negotiate affected neighbors and circulation, switch hull position, repack a region or expand the canvas within limits. | Bounded alternatives exhausted; return a diagnostic candidate or a sound infeasibility proof if available. |

At each level, first try the affected neighborhood, not all children of the parent.
Known nonlocal changes can be routed directly to the resource owner with a reason;
there is no benefit in trying to create external vacuum inside an enclosed room.
External constraints can often still be satisfied locally by using an existing
compatible parent offer, without moving the external endpoint.

```text
repair(request, scope):
    reuse compatible offers without changing external resources
    for bounded alternative in local proposals(scope, request):
        fork tentative transaction
        apply coherent multi-step edit
        invalidate dependency closure and recompute ancestor totals
        if protected hard obligations hold and repair/improvement is acceptable:
            commit state + reservations + bindings + witnesses together
            publish changed contracts; return repaired
        rollback the complete transaction
    return request to smallest owner of the needed external resources
           with attempted alternatives, assumptions, budget and missing capacity
```

Construction has explicit pending mandatory obligations. A committed repair must
preserve already protected obligations and discharge its promised repair set;
an incomplete candidate is never called feasible. Temporary violations are allowed
inside an uncommitted multi-step transaction so doors and seams can move together.
Keep a feasible incumbent separately once one exists. Reopening a solved subtree
invalidates its affected witnesses before further search, but does not remove its
protected obligations. Capture the protected set from the pre-edit commitment
state, independently of cache validity. A repair cannot pass by invalidating a
witness and then omitting its rule from commit checks. Only a legal change in
selection/activation can deactivate an obligation, with dependencies rechecked.

Requests carry provenance, instance bindings, dimensions/frontage/orientation needs,
read/write resources, current assumptions and attempted moves. Use request/state
hashes, hysteresis and bounded negotiations to prevent two siblings repeatedly
reclaiming the same strip. Budgets cover proposals, backtracks, selection retries,
canvas growth and root restarts. A local budget failure means **unresolved locally**,
not mathematically impossible. Proven infeasibility requires a sound bound or an
exhausted finite search, and states its boundary/selection assumptions.

Random streams derive from seed, stable instance ID and operator class. Commit
ordering and ties are deterministic. Store source/settings/evaluator hashes and
work counters; wall-clock cancellation records the last work count for replay.

## Witnesses and safe abstraction

Each successful check records its concrete bindings and all state it depends on.
Keep reverse indices to invalidate affected checks and summaries after an edit.
Translations/rotations can reuse internal proofs only when all their assumptions
transform compatibly; exterior exposure and incoming routes need new checks.

Access restrictions require more than a remembered valid path. A new door or
crew-carrying shortcut can bypass maintenance or a secure entrance without touching
that path. Initially recompute the relevant credential-aware connected component
for all-path policies. Likewise, added obstacles can break an exterior lane that
was previously empty. Track exclusion regions or conservatively invalidate the
affected spatial/network checks. An opaque extension cannot retain an unverified
cached success. Feature-link range checks reuse the actual selected device pairing.

## Dense departments with useful maintenance

Normally place each department as one compact cluster. Give allowed splits a high
soft cost and preserve required internal access. `distributed` tags have meaning
only through explicit profile selectors: Service and Commons may distribute
functional suites along public destinations while Hospitality itself stays together.
Do not spread individual rooms with a repulsion force or use department names in
solver code. A separate `maintenance_owner` selector distributes deep rooms,
vestibules and solar controls along maintenance destinations, rather than pulling
them into one ordinary department cluster. Logical ownership and aggregate budgets
remain unchanged by clustering.

Build maintenance as a connected graph expanded into **2–3 clear tile strips**,
with verified turns and junctions. It wraps useful rear/side boundaries and links
clusters, deep rooms and exterior vestibules. Prefer halls/maintenance between
different departments, while permitting authored direct interfaces and shared
walls within a department. Avoid a compulsory complete ring around every department.

Maintenance growth has its own area allocation, continuity checks and boundary
commitments. A department cannot eat an articulation point and hope a later pass
repairs it. A joint transaction may move or reroute the strip only when replacement
width, reach and promised coverage are checked. Count an actual reachable passage,
not an empty gap or nearby disconnected floor.

Every maintenance floor patch must be explained by an allowed strip or bounded
junction. Detect oversized open pockets as well as narrow bottlenecks; a route
centerline labelled `width: 2` is insufficient. Larger leftovers are, in order:
usable growth for a nearby room, an allowed named maintenance room within its
count/area bounds, or a reason to reshape the packing. Deliberate exterior vacuum
is allowed only when compatible with the program and exterior policies.

Reserve mandatory deep-room capacity early. Deep rooms require maintenance
traversal on all relevant public-to-room paths, including crew transport links.
Solar and space-access vestibules connect through maintenance without making normal
routes cross vacuum. Windows, berths and exposed machinery consume different
frontage resources and cannot substitute for one another.

The current catalog gives both `public_hall` and `maintenance` preferred area 1600,
with bounds 300–5000. Those preferences remain visible. A compactness setting does
not silently change them; any later catalog tuning must be explicit.

## Quality controls and meaningful variation

Mandatory feasibility is a separate gate. Among feasible candidates, minimize
normalized weighted soft costs with authored tier ordering. The proposed profile
in [planning/search-policy.proposed.yaml](planning/search-policy.proposed.yaml)
contains experimental knobs, not supported runtime settings or measured defaults.

| Measure | Definition / protection against misleading results |
| --- | --- |
| Department clustering | Extra spatial clusters and normalized spread of same-owner functional units; separate from credential-aware reachability. Report distribution policies explicitly. |
| Core compactness | Squared radius of gyration of core floor about its centroid, divided by reference area fixed for that selected program. Core includes all selected physical floor except explicitly declared outboard installations. Reference area comes from reconciled authored floor targets before random growth. Never remove rooms to improve a packing comparison. |
| Density diagnostic | Core usable floor / area of the convex hull of core floor and structural tile corners. Include enclosed voids in the denominator and report required vacuum/reservations separately. Also report gross bounds including outboard installations. This is a diagnostic, not permission to fill required voids. |
| Maintenance wrap | Connected passage-facing length / eligible outer boundary of each functional cluster. Account for the actual separating structure. Exclude internal cluster seams and only explicit required dock/exterior/direct-interface runs; report excluded length. |
| Coverage balance | Penalize individual deficient clusters as well as average wrap. Protected rooms can have maintenance outside their wall without granting a new entrance. |
| Reach and redundancy | Real credential-compatible routes from required gateways to maintenance, deep rooms and exterior vestibules; optional cycles and bounded dead-end pressure. A justified terminal branch is allowed. |
| Foreign-owner contact | Unrequired shared boundary between departments, favoring narrow circulation separators without empty moats. |
| Public destination coverage | Public-credential walking distance from public-hall samples to selected publicly accessible rooms or the publicly reachable side of service interfaces. Staff rooms and inaccessible terminals do not count. Penalize unreachable samples; never grant new access to improve this score. |
| Shape and variety | Aspect/fill/clearance compliance, bounded notches, different suite adjacency, gateway ordering, room proportions and cluster arrangements. Normalize global rotation/reflection before comparing seeds. |

Choose a room's sampled growth target and directional growth bias once per search
alternative. Keep variations within authored bounds and reconcile aggregate
budgets; never replace an authored preferred value with the sampled target.
Prefer changes caused by room sizes, interface choices and circulation
over artificially rough walls. Small rooms may remain rectangular. Keep a small
portfolio of near-quality candidates with distinct topology/shape signatures.

Benchmark packing with a fixed selected program, then separately measure variety
from optional selections. Report infeasible and exhausted runs too. Calibrate
numeric density/wrap targets from the first working baseline; existing-map bounding
box fill percentages are not direct maintenance or core-density targets.

The pilot policy uses tier 0, matching unoverridden catalog preferences, so density,
coverage and program targets can trade soft cost. A later policy tier would make
these goals only tie-breakers behind earlier tiers, regardless of weight. Explicit
authored tiers remain unchanged. Calibrate added split penalties alongside existing
cohesion preferences to avoid unintentionally making the same pressure dominate.
Destination/coverage metrics declare their actor, eligible endpoints, sample spacing,
distance scale and unreachable penalty in the evaluator contract. Maintenance
distribution uses maintenance access and required maintenance destinations, not
the public coverage objective. Pilot weights need calibration against authored
cohesion costs; a low extra split penalty alone does not cancel those costs.

## Worked local-to-parent examples

- **HoP and queue:** move furniture/entrances, then slide or reshape their shared
  counter within the personnel suite. If public approach still cannot fit, Command
  negotiates a neighboring strip; a hall relocation goes to its owning scope.
  The counter and separate staff access remain exact local obligations.
- **Kitchen, bar and commons:** explore opposing, adjacent or bent arrangements
  around the same commons instance. Repair counter approaches within Hospitality
  first, then request specific frontage from Service. Repeating the suite never
  permits a bar to bind to the other suite's commons.
- **Nested chapel:** reshape the confessional within the chapel complex first;
  then rearrange chapel floor/children. Additional external capacity is a parent
  request. Containment alone does not prove the required entrance policy.
- **TEG:** publish correlated hull, burn-chamber, radiator and vacuum-gap offers
  early. Try local machine/port routing before asking Engineering for a different
  exterior span or utility route. A station-level dock conflict may require another
  hull location. Preserved internal assembly geometry does not prove thermal startup
  or keep world-relative exposure valid after moving the complex.

## Implementation sequence and completion checks

Use a Python generator core separate from UI, consuming a version-checked compiler
view without mutating authored YAML. Suggested boundaries are `InstanceResolver`,
`EvaluatorRegistry`, `LayoutState`, `ScopeSolver`, `RepairTransaction`,
`WitnessStore` and `RunReport`; see the [implementation review](planning/review-implementation.md).
Typed extension adapters resolve their own local parameters rather than treating
every string as a path. New evaluators provide capability facets, dependency sets,
exact checks and optional bounds/repair operators. No special room-ID branches.

| Milestone | Deliverable and exit criterion |
| --- | --- |
| M0: semantics and contracts | Settle geometry/metric conventions above; define instance IDs, contract/transaction/result types and evaluator capability reporting. Turn proposed acceptance cases into explicit fixtures. |
| M1: first working hierarchy | Small supported fixture with repeated Hospitality, personnel, nested chapel, another department, a hull dock, public hall, 2–3-wide maintenance and two deep rooms. Generate tile layouts and independently verify all fixture obligations. Demonstrate room-only repair, sibling exchange and parent/exterior escalation. Save SVG/JSON and a repair trace; no stage editor prerequisite. |
| M2: packing and variety | Flexible department shapes, distributed suites, coverage-controlled compaction and bounded alternatives. Run fixed-program seed batches, measure feasibility/diversity and show settings affect density/wrap without breaking hard rules. |
| M3: catalog spatial support | Native assembly footprints and extension geometry: loading, operation space, exposure, beam/setup areas, actual docking/deployment, conditional machines and binding semantics. Enumerate unsupported parts of every catalog rule. |
| M4: services and realization | Power/atmos/disposals, explicit associations, native map export and engine checks. Preserve distinctions between provided kits, player setup and commissioned operation. Utility edits reopen local geometry when needed. |

The [acceptance cases](planning/acceptance-cases.yaml) specify planned checks,
including failed searches, transactional rollback, new bypasses, overly wide
maintenance and repeated-instance matching. They are not tests already executed.

Separate verification facets: selection/matching, layout geometry, traversal/access,
service topology, native realization and runtime operation. The current catalog's
26 extension types include operational requirements: geometric success alone cannot
certify them. Unsupported mandatory semantics prevent full-program acceptance;
partial previews list every unresolved rule and their requested verification scope.

Return `termination` separately from `candidateStatus`. Budget exhaustion can retain
an independently verified incumbent, while an attractive incomplete layout remains
incomplete. Distinguish invalid input, unsupported required evaluators, cancellation,
budget exhaustion and sound proven infeasibility. Report the seed, versions,
instance choices, work used, repairs by level, unresolved obligations and quality
measurements. Set performance and success-rate targets only after measurement.

Supporting design reviews: [hierarchy and propagation](planning/review-hierarchy.md),
[space and maintenance](planning/review-space-maintenance.md), and
[implementation feasibility](planning/review-implementation.md). Existing map evidence
in [the room review](research/room-relationships.md) and
[the engineering review](research/engineering-findings.md) informs the constraints;
this plan adds no new claim of map inspection or measured generator performance.
