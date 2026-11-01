# Additional utility dependency review

This pass inspected **Marathon, Exo and Elkridge**, beyond the first pass's four
focused stations. The [evidence JSON](additional-utilities.evidence.json) records
map hashes, selected entity UIDs/poses/inherited component data, source hashes
and proposed catalog evidence entries. Earlier analysis code was used read only
to inspect source data. No native simulation or electrical/pipe graph traversal
was run; counts and proximity are not operating certificates.

The authored follow-up expresses these as **local installation/room obligations**.
An installation declares its own features, needed inputs and offered outputs.
Upward propagation, station-wide coverage and network abstraction belong to the
later algorithm; the research observations below do not prescribe global rules.

## Radio channels are coverage obligations, not server counts

All three maps have one each of `TelecomServerFilledCommon`, `Cargo`, `Engineering`,
`Medical`, `Science`, `Security`, `Service` and `Command`. Marathon puts the eight
on grid **30** in a compact block: Common **9389** at `(-12.5,-30.5)`, Command
**9972** at `(-10.5,-30.5)`, Engineering **10550** at `(-8.5,-30.5)`, Security
**11161** at `(-6.5,-30.5)`, Service **19179** at `(-12.5,-28.5)`, Cargo **10551**
at `(-10.5,-28.5)`, Science **19185** at `(-8.5,-28.5)` and Medical **19181** at
`(-6.5,-28.5)`. Elkridge repeats all eight in a compact room, grid 2.

Exo is a useful counterexample to mandatory co-location. Its servers are spread
across grid **2**, including Cargo **5843** `(33.5,-94.5)`, Common **15359**
`(-33.5,-45.5)`, Science **17868** `(-24.5,-22.5)` and Medical **6591**
`(7.5,-25.5)`. The full eight positions are in the JSON.

`Content.Server/Radio/EntitySystems/RadioSystem.cs:116-126,143-158` requires a
powered server with the matching encryption channel on the **same map** for
non-exempt ordinary headset traffic. Long-range channels and exempt radios have
different conditions. This is not a same-grid requirement or physical telecom
cable requirement. `Resources/Prototypes/Entities/Structures/Machines/telecomms.yml`
defines the per-channel keys and the alternative all-channel `TelecomServerFilled`.

**Missing contract:** the original v3 `engineering/telecoms#telecom_servers` family
permits four to twelve independent selections from eight prototypes. Even eight
selected instances could all be Common. This baseline telecom installation offers
eight configured channels, with a powered/keyed local provider for each. Separate
mandatory feature families (one per channel) are a simple authored realization;
an extensible `x_radio_channel_coverage` requirement allows shared local providers.
Other installations can declare their own channels elsewhere. A single telecom
room and eight separate boxes are
map policies, not native necessities. Mandatory coverage is appropriate for the
selected installation's advertised service; another archetype can advertise a
different channel set. Native Cargo uses the channel ID **Supply**.

## Power conversion and local device coverage

The selected maps contain all three different roles:

| Map | SMESBasic / SMESAdvanced | SubstationBasic | APCBasic |
| --- | --- | --- | --- |
| Marathon | 9 / 6 | 18 | 66 |
| Exo | 6 / 7 | 18 | 65 |
| Elkridge | 6 / 6 | 15 | 108 |

Examples are Marathon SMES **3115** `(-56.5,-63.5)`, substation **1414**
`(-44.5,10.5)` and APC **406** `(-26.5,14.5)`, all on grid 30. These are
independent examples, **not a claim that those three form one traced circuit**.

Native `smes.yml` nodes are **HVPower input -> HVPower output**. Native
`substation.yml` nodes are **HVPower input -> MVPower output**. Native `apc.yml`
nodes are **MVPower input -> Apc output**, with an `ExtensionCableProvider`.
Therefore the original `engineering/substation#smes` feature does not implement
the named conversion role. Replace or supplement it with actual substations,
and keep optional SMES storage distinct.

`ExtensionCableSystem.cs:117-142,233-273` searches the **same grid** and requires
a connectable provider/receiver within both native ranges. The default provider
and receiver range is 3, and prototypes may override it. This coverage is
independent of ownership: a receiver in a neighboring room can be powered by
the same nearby provider. Room-level wiring alone does not prove device power.

**Missing local contracts:** the power installation's declared HV collection,
MV input and startup-APC output; a substation's HV input and real MV output;
each selected machine's own power need and native receiver coverage; finite
stored energy for declared startup loads. Power consumers include telecom, AI,
gravity, atmos devices and controls as well as ordinary machines. A mandatory
local `x_power_distribution` contract identifies this subject's features and
incoming/outgoing ports, requires native range/grid/topology tests and prevents
an empty abstract bus from satisfying its offered service. It does not enumerate
or aggregate all station consumers. Relationships between installations satisfy
the locally declared input needs; the later algorithm handles propagation.
Actual load margin and reserve duration are configurable design policies rather
than universal map-derived values. Do not impose the table's device counts.

## Habitable atmos needs separate flow and control networks

Each selected map has many vents, scrubbers and air alarms. An explicit saved
control association is Marathon alarm **2333**, grid 30 `(-44.5,59.5)`, whose
`DeviceList.devices` includes vent **448** `(-46.5,41.5)` and scrubber **2334**
`(-48.5,41.5)`. Both devices' `DeviceNetwork.deviceLists` refer back to 2333.
Elkridge alarm **488**, grid 2 `(2.5,-3.5)`, lists vent **486** `(1.5,-6.5)` and
scrubber **716** `(1.5,-5.5)`. Device lists may include several compartments or
other devices: proximity alone does not determine the control zone.

`GasVentPumpSystem.cs:70-155` requires power, an enabled device, a pipe node,
usable room atmosphere and suitable supply gas/pressure; ordinary releasing
vents cannot conjure breathable air from an empty pipe. `GasVentScrubberSystem.cs:
56-103` requires power, enabled state and an outlet, and refuses extraction when
the destination pressure is too high. `AirAlarmSystem.cs:85-110,185-204,317-324`
registers and addresses the configured device list. A placed alarm is not proof
of a control binding. A scrubber feeding a sealed full stub is not viable waste
processing, even though a pipe entity exists.

**Missing local contracts:** a vent needs a supplied gas input, a scrubber needs
a viable waste output, and controls need actual bindings and power. Preserve distinct
breathable-supply, contaminated-exhaust and process-gas circuits; define treatment
before any exhaust is returned to breathable supply. An environmental requirement
or a room being near Atmos does not establish these edges. The local
`x_atmos_plant_support` contract declares the plant's source input, breathable
output, waste return and control interfaces. It must not enumerate station zones.
Cold-storage cooling and cryogenic/process loops are separate local services.
Consumer assembly templates expose ports for future local room requirements;
their existence does not demand one identical vent/scrubber/alarm per YAML room.
Open rooms can share an atmosphere zone, while internal walls can subdivide one
room; the algorithm must handle that when propagating local needs. Native
air-alarm ownership remains separate from gas-pipe reachability.

## Gravity and AI are powered grid functions

Main-grid gravity generators appear at Marathon **5478**, grid 30 `(10.5,44.5)`;
Exo **3637**, grid 2 `(-39.5,-45.5)`; and Elkridge **2525**, grid 2 `(42.5,-3.5)`.
`GravitySystem.cs:24-36` requires an active generator directly parented to the
affected non-inherent-gravity grid. `GravityGeneratorSystem.cs:36-58` activates
gravity through charged-machine events. The prototype requests APC power and
has a 3x3 collision footprint. Merely putting one generator anywhere under
Engineering does not grant gravity to detached grids.

**Missing local contract:** a selected generator must operate, receive power
and offer gravity only on its host grid. Its local `x_grid_gravity` requirement
does not impose gravity on other grids or enumerate station grids. Inherent or
intentional-zero-gravity choices belong to their respective local definitions.
Do not require walking adjacency between Gravity and every served room.

New AI examples are Marathon **21587**, grid 30 `(-0.5,82.5)`; Exo **10852**,
grid 2 `(91.5,-45.5)`; and Elkridge **5395**, grid 2 `(-42.5,20.5)`. Their inherited
APC load is 500 W. They strengthen the already documented distinction between
a remote-looking wing and a separate grid. The existing same-grid AI contract
should remain; include the core's own incoming-power obligation. Vision
seed and occlusion coverage remains a later distinct requirement, not proved by
same-grid placement or these samples.

## Priority and limits

For each selected installation, its advertised radio channels, power inputs and
outputs, gas-processing services and gravity function are mandatory local
obligations. Particular server-room grouping, exact battery counts, power
redundancy margin and a fixed alarm count per room are configurable policies.
The findings justify local definitions and future evaluator obligations;
they do not certify generated connectivity, powered startup or runtime capacity.
