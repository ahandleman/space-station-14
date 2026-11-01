# Authored standard station

The catalog is a new V3 program. Room vocabulary and source references informed by
the earlier catalog were retained where useful; old stage state, geometry,
connection solver assumptions and utility plans were not imported. The new
relationships were authored around functions and physical interfaces.

The [second dependency pass](DEPENDENCY_REVIEW.md) adds locally owned equipment,
access and service obligations. Relationships and choices now live inside their
room or suite; relative references identify local children, siblings and features.
The catalog contains no global relationship or choice list. Future parent-level
propagation and abstraction are not prescribed by these definitions.

The [third map pass](LOCAL_DEPENDENCY_REVIEW.md) adds local machine assemblies,
signal and receiving links, oriented loading access and setup-state contracts.
Optional power facilities can begin as player-buildable kits with reserved
working geometry; their required support is still explicit.

## What is necessary, and why

The baseline treats usable room area, required room presence, access, serving
interfaces, internal specialized-department routes, docking clearance and selected
service dependencies as **mandatory**. Weights cannot waive these. Preferred
areas, departmental cohesion and some travel distances remain weighted desires.
The preferred size of a room is not asserted to be a measured native minimum.

| Area | Baseline contracts |
| --- | --- |
| Arrivals | Public reception/waiting; staff support; real arrivals berth and clear exterior approach. Crew cryosleep is separate. |
| Commons | Public concourses, dorms/restrooms, storage/EVA, recreation, independent cryosleep and evacuation with its own shuttle berth. |
| Hospitality | Kitchen and bar workspace share one public bar/dining commons per cluster, through separate mandatory counters. Staff routes exclude the customer commons. Cold storage and nearby botany support food production. |
| Chapel | Confessional and crematorium are nested physical rooms, reached through their own chapel instance. The crematorium is a machine facility, not a permanently hot public atmosphere. |
| Medical | Public triage, clinical staff circulation, treatment/surgery/cryogenics access, chemistry pickup interface, cross-role morgue access, CMO and optional specialized rooms. Clinical pods have local gas/cooling/waste/medicine obligations; staff rooms are not public shortcuts. |
| Engineering | Internal workshop/atmos access, a required Atmos-to-TEG connection, power distribution, gas storage, waste, telecommunications, anchor/gravity and the thermal complex. Required Atmos explicitly activates the source bank. |
| TEG complex | Generator room at the space boundary; sealed outboard burn chamber and exposed cold radiator on the same grid; real center/circulator assembly, separate pumped circuits, fuel, startup power and HV output. |
| Science | Research/robotics/labs/server, storage and controlled experiment facilities, with departmental staff routes and a private RD office. Artifact pairs have explicit device bindings; research clients declare their own server associations. |
| Security | Controlled shared Brig and Perma, public-facing Sentencing, armory/evidence/interrogation/warden/HOS and staff support. Sentencing connects to Perma and contains native sentence/property lockers. Brig and Sentencing have separate entry/exit turnstiles. Courtroom and Law Office are independent rooms with station routes, not members of the packed Security core. |
| Cargo | Orders counter, warehouse/loading shared access, actual cargo docking space, internally reachable salvage, independent Salvage EVA vestibule, optional local material processing, equipment/mail and private QM office. |
| Command | Bridge/captain interface, meeting/records/vault and optional private quarters; an independent personnel group includes HoP office plus public queue and service window. |
| Silicon | Core/upload/support rooms with controlled entry and explicit same-grid affinity; a remote-looking AI wing need not be a detached grid or cryogenic room. |
| Maintenance | Two-to-three-wide passage network, bounded true rooms and deep stores, intentional EVA vestibules, solar controls and exterior arrays reached through maintenance. |

Head-office access is explicit (CMO, CE, RD, HOS and QM) rather than a generic
"staff" fallback. Credentials for an actor include the department needed for its
ordinary routes. An access profile is a planning assumption, not a claim that the
tool has created or modified an in-game job/ID card.

## Shared interfaces are not generic dependencies

Box and Packed provide direct HoP counter/windoor examples with separate staff
doors. Packed and Saltern provide kitchen and bar fronts serving one common
customer area. Box also shows a different food-service arrangement. The shared
bar commons is mandatory here because it is the requested archetype, not because
every existing map follows it. Two/four/three-tile serving dimensions are editable
author policy rather than measured universal requirements.

Hospitality and personnel are nonphysical groups. Repeating a group repeats its
rooms and binds their mandatory interfaces within that parent instance. The
customer commons/queue is a sibling of the workspace, not private floor nested
inside it. Logical Service/Command ownership does not turn public customer floor
into restricted staff circulation.

## Exterior machinery needs more than a space label

The researched burn chambers are small sealed hazard spaces: coarse observed
components range from 9 to 20 tiles. The authored chamber range is 9/16/30 tiles.
It is outboard but on the station grid, separated from inhabited hull by vacuum
except supported service/EVA connections. Exterior maintenance into it assumes
shutdown and safe temperature/gas state; an EVA suit is not a combustion-survival
guarantee.

The cold radiator and solar surfaces require lattice support, open vacuum and
per-feature exposure. Native pipes require compatible anchored same-grid contacts;
a detached grid merely sitting next to another does not supply a pipe connection.
The TEG assembly uses native local part poses. Hot/cold labels describe the chosen
circuit assignment, since the native machine determines which side is hotter.

Working gas, positive pressure drop, temperature difference and startup power are
required in the declared `x_teg_operating_conditions` contract. This deliverable
does not simulate or certify them. The exposure extension requires all selected
radiators/panels to meet the exposure contract; sunlight sampling is an authored
clearance policy, not guaranteed continuous solar wattage.

AI references show ordinary air, not a universal cryogenic core requirement.
Cargo/arrival/evacuation use different-grid shuttle counterparts with berth and
approach geometry distinct from ordinary space-facing windows.

## Configurable choices and deliberate policy

The requested TEG suite is the standard baseline. AME and containment families
remain optional; selecting particle-accelerator controls activates the associated
engine family. Counts and family choice bounds can be edited for another station
archetype. Children of optional parents remain conditional on that parent.

Deep stores currently allow 2–6 instances, preferably 3. Solar arrays and their
controls allow 2–4, preferably 2; matching requires a distinct control target for
each selected array. These are author preferences, not native-map census counts.

Security's allowed entry neighbors, internal-only nested-room access, captain
adjacency and area/shape bounds are explicit policy. Existing maps sometimes have
additional controlled maintenance doors or different armory neighbors. Authors
can change those policies without changing evaluator code. Required access still
needs a later all-path bypass check; a single good doorway is insufficient.

The custody baseline uses shared Brig living floor and beds, without individual
timed cells. The bed count and Sentencing locker bank are configurable features;
a locker is a prisoner's property/sentence facility, not their living room.
Sentencing admits public approaches so released prisoners can retrieve belongings
and leave. Locker configuration still requires Security credentials, and separate
controlled boundaries protect the staff circulation and Perma. Brig entry is
restricted to Sentencing, Warden and secure circulation; a public Sentencing
approach does not make Brig public floor.

The native lockers create and retain their own linked prisoner IDs. Sentence
expiry changes a card's admission access to release access; it does not send
Open/Close signals to gates. Separate directed entry and exit turnstiles occupy
the local Sentencing/Brig boundary. Card creation, expiry and linked-property
retrieval remain native runtime obligations, not simulated results of a local
layout pass. See the [current native custody audit](research/custody-flow-correction.md).
Perma has a required controlled Sentencing connection. The Courtroom and Law Office retain logical Security
ownership but each has an independent room placement scope. A station route to
Courtroom must not be replaced with a mandatory shared border that would force
adjacency to the Security core. These are requested authoring policies, not claims
that every native map uses the same arrangement. Earlier timed-cell observations
remain recorded in the [custody research](research/local-custody-medical.md);
their former catalog recommendation is superseded by this baseline.

The two source research reports record both repeated patterns and counterexamples.
Beacon references establish vocabulary/location, not an exact polygon or proof
of operating machinery. The source audit checks reference existence and hashes,
not the truth of arbitrary prose or generated-layout feasibility.
