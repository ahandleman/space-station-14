# Implementation feasibility review

This is a proposed implementation sequence and acceptance contract, not an
implemented generator. It builds on the existing V3 symbolic compiler without
changing either earlier tool. The first deliverable should be a runnable,
inspectable layout solver for a deliberately small supported fixture; expanding
directly to the entire standard catalog would conceal missing evaluators behind
apparently successful pictures.

## Existing boundary

`validate.py:compile_document` already provides templates, local rule provenance,
symbolic source/target candidates, activation implications, choices and external
evaluator obligations. It does not select instances, prove matching, allocate
geometry or certify native operation. Its conservative area/count checks are
useful preflight checks, not a satisfiability oracle.

The standard catalog currently declares 26 extension types and 39 uses. Several
combine spatial and operational obligations in one rule. For example, a TEG
rule includes positive pressure drop and temperature difference, while a PA
setup rule distinguishes available construction parts from completed machinery.
Evaluating a rectangle or routing a line cannot complete those obligations.

## Interfaces needed before broad generation

| Interface | Required behavior |
| --- | --- |
| `ProgramAdapter` | Consume the current compiler output with a version check. Preserve authored IDs, declaration scope and source locations. Keep authored documents immutable. |
| `InstanceResolver` | Choose per-parent counts and choice branches; assign stable instance IDs containing ancestor indices; produce explicit target matches and activation obligations. Reject cardinality or choice conflicts before placement where possible. |
| `EvaluatorRegistry` | Register semantics by evaluator ID and version. Declare supported certification facets, parameter resolution, dependencies, lower bounds, exact checks and optional repair proposals. Unknown mandatory semantics remain unresolved. |
| `LayoutState` | Separate logical ownership from physical masks, grid identity, boundary interfaces, service layers and protected reservations. Include real walls and clearances in the occupancy model. |
| `RequirementInstance` | Preserve local declaration; bind actual subject/target instances; distinguish mandatory bounds, desired targets and inactive rules. Record all facts read by the evaluator. |
| `WitnessStore` | Store actual matched pairs, boundary segments, actor-specific route paths, native assembly poses and reservations. Record rule, instance and geometry revisions. Related rules reuse the same binding witness. |
| `RepairTransaction` | Describe proposed edits, affected geometry/instances, invalidated witnesses and inverse operations. Validate the complete impact set before committing; rollback must restore reservations and cached summaries too. |
| `ScopeSolver` | Try operators at the smallest authorized scope, then return a typed request or result to its parent. Do not silently expand the edit scope. |
| `RunReport` | Record seed, source/config/evaluator versions, work budget, decisions, escalation reasons, final checks, unresolved obligations and candidate artifacts. |

Opaque extension parameters currently retain their authored strings. Their
evaluator adapters must resolve local feature, relationship, room and port names
using the declaration scope; the core cannot safely guess that every string is
a room path. Make this a typed binding step before the first evaluation.

Evaluators should return `satisfied`, `violated`, `unknown` or `inactive`, with
measured values and a witness or explanation. `Unknown` is not a small numerical
penalty. Partial spatial evidence for a mixed spatial/runtime extension is useful,
but must not mark its entire mandatory rule satisfied.

## Geometry model and independent verification

Use tile-based exact masks for the first implementation; coarse planning may
produce suggestions, but acceptance checks operate on the realized tiles and
interfaces. Keep distinct layers for usable floor, walls/seams, objects,
movement clearance, pipes/cables and protected exterior envelopes. A same-tile
disposal intake/trunk is legal across compatible layers; overlapping unrelated
rooms is not.

Keep an independent final verifier that recomputes constraints from the candidate
without trusting cached solver scores. It need not use a separate geometry
library, but must not accept a previously cached `satisfied` flag as evidence.
Mutation tests should deliberately break accepted candidates and ensure that the
verifier detects the defect.

The verifier needs explicit scope:

| Facet | Evidence that can support acceptance | What it does not establish |
| --- | --- | --- |
| Selection and matching | All instantiated counts/choices, activation implications and scope-compatible matchings checked | Geometry or native operation |
| Layout geometry | Exact area, shape, containment, seams, clearances, exterior reservations and geometric extension obligations checked | Actual SS14 entities exist or operate |
| Traversal and access | Paths over realized interfaces for named actor profiles; containment and entry policies checked | Native door configuration unless exported and checked |
| Service topology | Compatible ports, supported routes, grid boundaries and distinct circuits checked | Adequate electrical supply, pressure, gas composition or startup |
| Native realization | Exported entities, exact poses/components/links and prototype inheritance checked | Runtime behavior or commissioning |
| Runtime operation | Declared operating/setup scenarios checked in the actual engine or a clearly scoped trusted evaluator | Untested scenarios or future player changes |

The existing native source audit supports provenance and prototype existence; it
is not a replacement for the last two facets. A geometric preview of the full
catalog is allowed only as an explicitly incomplete candidate listing every
unresolved rule. No single green "valid station" label should collapse these
facets.

## Result and termination contract

Use two fields rather than a single success boolean:

- `termination`: `completed`, `budget_exhausted`, `cancelled`, `invalid_input`,
  `unsupported_required`, or `proven_infeasible`.
- `candidateStatus`: `none`, `incomplete`, `verified_for_requested_scope`, or
  `verified_for_full_program`.

`verified_for_requested_scope` requires a documented capability profile with all
in-scope obligations evaluated and a separate unresolved-out-of-scope list. It
must never imply that the full program's mandatory rules were met. A request for
full-program certification with unsupported mandatory rules fails preflight;
the user can explicitly request a candidate-only run. A small supported fixture
can attain full-program acceptance without claiming SS14 runtime verification
that the fixture does not request.

Keep the best verified candidate even if optional improvement consumes the
remaining budget; its termination reason is still `budget_exhausted`. Conversely,
an attractive incomplete candidate is not accepted merely because time expired.
`Proven_infeasible` requires a proof within stated finite bounds, such as an
unsatisfiable instance matching or an exhaustively searched small region. A local
heuristic failing to find a fit returns `local_budget_exhausted` with attempted
operators, not an infeasibility claim.

Bound every run by deterministic work units: proposals per scope, parent
negotiations per conflict, backtracks, selection retries and restarts. Wall-clock
limits are an additional cancellation mechanism. Hash the relevant state and
request so that repeated cycles are detected; escalate or backtrack instead of
replaying an identical local search. Conflict requests must include the attempted
scope and the next scope needed. No retry automatically relaxes a mandatory rule.

Reproducibility requires source YAML hash, compiler version, evaluator versions,
settings hash, seed and deterministic tie-breaking. Derive independent random
streams from stable instance IDs and operation classes so a failed local attempt
does not arbitrarily reshuffle the whole station. Record the proposal count at
wall-clock cancellation; exact replay uses that count. Parallel proposals need a
deterministic commit order or must be explicitly reported as non-reproducible.

## Smallest useful runnable milestone

Use an explicit test program rather than deleting unsupported requirements from
the shipped catalog. Include two repeated service suites, each with kitchen,
bar and their own commons; a contained chapel room; a compact security suite;
one hull docking room; a public hall; a 2–3 tile maintenance network and two deep
maintenance rooms. Test features can be simple supported assembly footprints.

Implement count/matching resolution, integer occupancy, shared seams, basic actor
routes, area/shape/containment checks, docking reservations and maintenance width
before broad machine semantics. Generate several seeds and save a colored layout,
instance tree, requirement report and repair trace. A static SVG and JSON report
are sufficient for diagnosis; a stage editor is not a prerequisite.

Force three repair scenarios in this milestone: room-only success, sibling
redistribution success, and a hull/parent change. Observe that the same local
requirements survive each move and that higher levels are not edited when the
local operators succeed. Include an impossible fixture and a deliberately tiny
search budget so failure reporting is tested from the start.

Later milestones should expand in this order:

1. Full core-rule coverage and hierarchy repair, including repeated selection,
   optional target activation, distributed logical groups, and all actual
   interface/access semantics.
2. Spatial adapters for native assemblies and extensions: directional loading,
   safe operator positions, exterior exposure, engine footprints, beams,
   conditional equipment and docking/salvage reservations.
3. Service topology and explicit device associations, preserving local consumer
   ownership and the selected instance witnesses.
4. Native export and operation/setup checks, retaining `player_setup` versus
   `commissioned` and never counting packed kits as running machines.

Each milestone states which standard-catalog rules remain unsupported. A single
extension may need several facets implemented across milestones.

## Acceptance fixtures and benchmarks

| Fixture | Required observable result |
| --- | --- |
| Repeated hospitality | Both kitchens and bars meet their own suite's commons. Crossing to the other identical suite is rejected even if physically closer. |
| Matched local features | Analyzer reach is checked against the actual exclusive device-binding pair; independently finding a nearby analyzer cannot satisfy it. |
| Optional reverse supply | Absent optional consumer imposes no supply requirement; selecting it activates a compatible provider without reversing obligation ownership. |
| Contained room | Confessional can move inside chapel while parent exclusive floor remains disjoint. Entry cannot bypass the required host. |
| Local fitting slack | A misplaced feature or seam is repaired within its room/suite with zero parent or unrelated sibling edits. |
| Sibling pressure | Room expansion uses available sibling slack, respects both room minima and department aggregate bounds, and leaves unrelated departments unchanged. |
| Parent pressure | Exhausted local capacity produces a request with dimensions/frontage/clearance, not only area; a parent repair then rechecks all affected child witnesses. |
| Impossible interface | Two rooms with a short, broken or corner-only contact fail a continuous minimum seam even when their centers are close. |
| Protected exterior | Dock approach, berth and outboard setup/beam envelopes survive later growth; enclosed courtyard vacuum cannot satisfy exterior connectivity. |
| Maintenance width | Every corridor segment is 2–3 tiles wide under the defined junction convention. Larger voids become explicit rooms or are assigned to departments. |
| Maintenance access | Deep rooms require maintenance traversal; a hall shortcut is rejected. Wrap and route coverage remain above the configured thresholds after compaction. |
| Mutation invalidation | Moving one room invalidates its seams, adjacent routes, perimeter/exposure, parent metrics and dependent extension witnesses; unrelated summaries remain valid. |
| Unsupported extension | Unknown mandatory evaluator prevents requested full acceptance and is listed precisely; unknown desired evaluation is reported rather than treated as zero cost. |
| Hard/soft conflict | A high-weight compactness or variety objective cannot buy a mandatory room-size, interface or maintenance-width violation. |
| Cycle/budget | Oscillating requests terminate within budget with an actionable trace; no false infeasibility proof or implicit rule relaxation. |

Use a fixed seed suite at each supported scale and publish both successful and
failed runs. Report feasibility rate, work units, repair/escalation counts by
level, change radius, room/department size distributions, public/maintenance
route lengths, useful-floor density and coverage. Specify the density denominator:
station hull area, not a bounding box enlarged by shuttles or solar arrays.
Measure maintenance passage and maintenance-room area separately so adding large
maintenance rooms cannot fake corridor coverage.

For variation, compare room adjacency, relative locations and mask shape after
normalizing global rotation/reflection. A different seed, mirror image or
swapped palette is insufficient evidence. Compare clustering and varied shape
only among feasible candidates, then test several density/coverage settings to
show that the settings change the intended property without defeating mandatory
rules. Establish numeric performance targets from the first working baseline;
do not claim a success rate or generation time before measurement.

## Architectural traps to avoid

- Turning logical department/group nodes into permanent rectangular containers.
  They need resource envelopes and cluster pressure, while physical containment
  is reserved for authored `contained` relationships.
- Moving a direct-interface rule to an approximate department attraction. Parent
  summaries must retain a real unresolved boundary obligation until a concrete
  seam witness exists.
- Treating the declaring scope as the entire mutation scope. A local room may
  request an exterior hull change; its requirement stays local while the repair
  transaction escalates to the scope controlling the affected resources.
- Publishing independent minimum-area and minimum-frontage summaries as if they
  were jointly attainable. Use conservative bounds plus compatible alternatives;
  correlated constraints need a common realization witness.
- Assigning every service/data dependency to a packing adjacency edge, or
  mistaking a counter for an ordinary walking route.
- Filling every unallocated tile before reserving real wall thickness,
  maintenance circulation, docking envelopes and feature approach space.
- Making hard feasibility a finite weight in a global scalar score. Local
  exploration may keep explicit incomplete states, but accepted candidates cannot
  trade mandatory failures for dense packing or novelty.

