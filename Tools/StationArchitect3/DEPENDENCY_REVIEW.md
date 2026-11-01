# Second dependency pass

This pass examined Marathon, Exo, Sushi and Elkridge and checked the corresponding
native machine and access behavior. After this pass the catalog had **139 spaces (123 rooms),
197 relationships and 130 feature families**. This adds 25 relationships and two
rooms. There are 188 mandatory relationships and nine desired relationships;
local node requirements also describe operating and access conditions. The later
[third map pass](LOCAL_DEPENDENCY_REVIEW.md) records subsequent additions and current counts.

## Local ownership

All relationships and choices in the shipped catalog and small example are now
declared on a room or functional suite. There is no root relationship list.

- `command/personnel` owns its HoP office/queue interface.
- `service/hospitality` owns the kitchen/bar/shared-commons and cold-store interfaces.
- `science/artifact_lab` owns its analyzer/console binding.
- Individual research rooms declare their need for a sibling research server.
- Each substation declares its own HV input; the power room declares its MV input.

Relative references preserve that ownership when inspecting the YAML. A flat
compiler index retains declaration scope and instance-binding information; it
does not replace the local definitions. Parent-level propagation, abstraction of
satisfied internal constraints, layout optimization and network construction are
left for the algorithm. The validator still checks authoring consistency and
provably impossible references/counts.

## Added or corrected obligations

| Local owner | Requirement and reason | Priority |
| --- | --- | --- |
| Artifact lab | Persist a distinct console/analyzer binding using the native sender/receiver ports. Every selected analyzer needs a console. Co-location alone is insufficient. | Mandatory |
| Artifact lab | Prefer placing the actual bound pairs within their selected source's configurable linking range, for repairability. Saved links can restore outside this range. | Desired |
| Research, robotics, artifact and anomaly rooms | Selected research clients require an eligible same-grid research server and actual registration. Server operating power is distinct from mere registration. | Mandatory |
| Salvage | Require independent two-stage exterior access and expose the magnet's oriented debris-placement clearance locally. A cargo docking berth does not serve both functions. | Mandatory |
| Cargo material processing | Optional work room; when selected, require handling routes to Salvage/Warehouse, usable input/output/service positions, power and a crew approach outside the recycler footprint. | Mandatory when selected |
| Cryogenics | Connect every pod and cooler to the local gas circuit; provide regulated breathing gas, waste removal, actual portable connections, medicine and patient cooling. The staff room stays habitable. | Mandatory |
| Hospitality cold store | Require powered gas cooling and room-gas exchange, maintained temperature and Kitchen access. Bind each cold store to its own hospitality instance. | Mandatory |
| Botany | Provide locally reachable water inventory. Native trays consume water; this does not invent a station water-pipe network. | Mandatory |
| Morgue | Require Medical OR Detective OR Chapel admission at routine entrances, plus credential-correct approaches avoiding private clinical floor. This is an explicit cross-role policy supported by the sampled doors. | Mandatory |
| Telecom installation | Offer each configured channel through powered, keyed local providers. Eight random choices from a prototype family could otherwise all serve Common. Native Cargo radio channel is `Supply`. | Mandatory |
| Power room and substations | Use actual HV-to-MV converters, preserve SMES as HV storage, and declare local incoming/outgoing services, native APC coverage, capacity and finite startup energy. | Mandatory |
| Atmos plant | Declare its source, breathable output, contaminated return and controls separately. Require usable inventory/pressure, waste treatment and control bindings. | Mandatory |
| Gravity room | Require the selected generator to operate on its own host grid, with native charge/power and footprint requirements. | Mandatory |

For incoming services, `flow: reverse` keeps the consuming feature as the
obligated source even though physical supply travels from the provider target.
An absent optional consumer therefore imposes no supplier obligation. No central
power or atmos coverage pass is encoded here: local offers/needs can later be
matched, propagated or summarized by the generation algorithm.

## Deliberate limits

The additional native behaviors are declared through closed, typed extension
contracts. They require later evaluators; the present authoring checks do not
certify powered operation, pressure, medicine administration, safe airlock cycling,
clearance or a playable map. Native realization templates are reusable definitions,
not silently spawned equipment.

The pass did **not** turn these observations into universal requirements:

- Telecom servers need not share a room; Exo distributes them.
- Research clients need not border the server room.
- An anomaly generator need not border a vessel, and no active anomaly is required
  at round start merely to define a usable laboratory.
- Mail teleporters do not imply a disposal-tube network.
- Recycler feeding need not use conveyors.
- Magnet search defaults are not a fixed empty rectangle or a universal distance.
- Saved cold-store temperature is evidence for a design target, not a native constant.

The researched facts, counterexamples, exact entity records and source hashes are in
[medical/service](research/additional-medical-service.md),
[science/cargo](research/additional-science-cargo.md), and
[utilities](research/additional-utilities.md). The independent
[source audit](research/source-audit.json) checks references and source identity;
it does not prove geometric or runtime satisfaction.
