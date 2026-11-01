# Local engine-machine relationships found in station layouts

This pass examined Hike, Gear, Snowball and Tram2. The companion
[evidence JSON](local-engine-machines.evidence.json) records 145 selected entities,
map hashes, source hashes and proposed evidence IDs. Plasma and Relic were also
screened for machinery, but the recommendations below use the four documented
samples. This is direct saved-entity/layout inspection followed by native-code
verification; no power tracing, collision raycast or live commissioning was run.

## Startup state is part of the observed pattern

These maps deliberately provide player setup work. Parts are often unfinished,
flatpacked or stored unanchored near the working installation. A usable engine
room must supply its required parts and a realizable working footprint, but it
need not contain a running reactor at round start. Treat `player_setup` versus
`commissioned` as an explicit local policy. Neither initial assembly state nor
initial operating state should be silently inferred from an equipment count.

This distinction is particularly important for AME shielding and PA parts. It
also prevents treating a staged Tesla generator's storage position as its final
operating position inside a containment ring.

## A PA console belongs to a complete local multipart assembly

The current catalog has only `engineering/particle_accelerator_control#pa_control`.
Mapped installations consistently supply the remaining six parts and reserve
their physical relationship:

| Map / grid | Control | Other mapped parts |
| --- | --- | --- |
| Snowball / 1 | Control **8894**, `(52.5,110.5)`, rotation -90 degrees | End cap **2618** `(51.5,109.5)`; fuel **3575** `(51.5,110.5)`; power box **2807** `(51.5,111.5)`; port/fore/starboard emitters **2712/2617/2804**, x `50.5/51.5/52.5`, y `112.5`. All six are unfinished and face north. |
| Tram2 / 15 | Unfinished control **19423**, `(-22.5,-54.5)`, rotation -90 degrees | End **7608** `(-23.5,-55.5)`; fuel **7605** `(-23.5,-54.5)`; power **7606** `(-23.5,-53.5)`; port/fore/starboard **10612/151/10712**, x `-24.5/-23.5/-22.5`, y `-52.5`. All six are unfinished and face north. |
| Gear / 12 | Unfinished control **18266**, `(11.5,4.5)`, saved default rotation | End **5291**, fuel **4582**, power **4669**, and emitters **5288/4581/4722** repeat the same neighboring footprint around x `10.5`, with emitter row y `6.5`. The stored control orientation is not proof of a completed matching assembly. |

Hike supplies PA crate **8421** on grid 1 at `(-41.5,-6.5)` instead. A packed
starter kit is another valid initial realization; it still needs working floor.

`Resources/Prototypes/Entities/Structures/Power/Generation/PA/control_box.yml`
declares exact `MultipartMachine.parts` offsets/rotations. For a control facing
native rotation zero, the finished assembly is:

| Part | Local offset | Rotation |
| --- | --- | --- |
| Control | `(0,0)` | 0 |
| End cap | `(1,-1)` | 270 |
| Fuel chamber | `(0,-1)` | 270 |
| Power box | `(-1,-1)` | 270 |
| Port emitter | `(-2,-2)` | 270 |
| Fore emitter | `(-2,-1)` | 270 |
| Starboard emitter | `(-2,0)` | 270 |

The JSON includes these as `canonicalPaAssembly`. Native
`MultipartMachineSystem.Rescan` requires anchored parts on the same grid, matching
offsets, rotations, components and completed construction nodes. The PA's control
uses APC power, whereas `ParticleAcceleratorPowerBox` uses **MVPower node `input`**.
A single generic power need for the console does not supply the high-load box.
The emitter system launches particles along each emitter's orientation; for the
selected containment-engine use there must be a usable beam path into that
engine's intended operating region.

**Local definition recommendation:** the PA control room owns a complete assembly
or starter-kit requirement, its reserved native assembly footprint and its two
distinct incoming power needs. The chosen engine chamber locally requires a
compatible accelerator/beam approach. A room-presence dependency alone does not
establish assembly, orientation or beam delivery. Selecting `player_setup` must
preserve the ability to complete the assembly rather than falsely demanding that
all parts be finished at spawn. The exact parts and ranges belong in data.

## AME rooms provide shielding and fuel as well as a controller

Three layouts show the local supply relationship explicitly:

| Map / grid | Controller | Local fuel/shielding inventory |
| --- | --- | --- |
| Gear / 12 | **9418** `(9.5,-4.5)` | Fuel crate **9424** `(9.5,-5.5)`; shielding crates **9416/9422** `(12.5,-6.5)/(12.5,-2.5)`; AME beacon **28798** `(11.5,-4.5)`. |
| Snowball / 1 | **6922** `(69.5,105.5)` | Fuel crate **6994** `(71.5,104.5)` and shielding crate **6997** `(70.5,103.5)` beside beacon **6757** `(70.5,104.5)`. |
| Tram2 / 15 | **7862** `(-65.5,-52.5)` | Fuel crate **11634** `(-63.5,-52.5)` and shielding crates **11633/24290** `(-64.5,-52.5)/(-61.5,-52.5)`; AME beacon **19012** `(-63.5,-58.5)`. |

`Catalog/Fills/Crates/engines.yml` fills a shielding crate with **nine
`AmePartFlatpack`** and a fuel crate with **three `AmeJar`**. The flatpack produces
`AmeShielding`. `AmeNodeGroup.LoadNodes` recognizes a core only when a shield has
at least eight surrounding shielding neighbors on its grid; a conventional 3x3
block supplies one core. The controller joins through its `AMEngine` adjacency
node. `InjectFuel` returns zero with no fuel or no cores, and
`AmeControllerSystem` consumes an inserted `AmeFuelContainer` from its fuel slot.
The controller's native HV output is unusually named **`input`**; do not invent
a native node called `output` merely because the logical service is an output.

**Local recommendation:** add shielding-kit and fuel inventory requirements to
`engineering/ame`, with a reserved buildable connected-core footprint, controller
adjacency, fuel-slot service access and an HV export terminal. The floor can start
clear with packed inventory, matching these stations. Validate the eventual core
using native neighbor rules rather than just requiring nine arbitrary scattered
shield entities. The selected safe commissioning policy can require injection
not to exceed two units per core; this is separate from the layout and does not
forbid players deliberately overloading it later.

## Containment is a local set of beam lanes, field links and collectors

Tram2 supplies a particularly clear field/beam layout on grid 15. Field generators
**7850/7851/7852/7858** are the four corners of the rectangle x `-27.5/-19.5`,
y `-46.5/-38.5`. Eight emitters point toward those corners:

- West-facing **5100/20060** at x `-17.5`, y `-46.5/-38.5`.
- East-facing **20062/20063** at x `-29.5`, y `-38.5/-46.5`.
- South-facing **19715/20061** at y `-36.5`, x `-27.5/-19.5`.
- North-facing **5249/20064** at y `-48.5`, x `-19.5/-27.5`.

Snowball and Gear have smaller field-generator rectangles and both placed and
staged coils/rods. Snowball's four corner generators **2672/5275/5330/5331** use
x `50.5/52.5`, y `117.5/119.5`; six placed Tesla coils run across y `122.5`
(**5326/5335/5442/5446/5447/5448**), with grounding rods nearby. Its Tesla generator
**2333** is instead staged at `(70.5,110.5)`, beside unanchored equipment. That is
not evidence for an already operating ball in the cage. Tram2 likewise includes
staged Tesla equipment and both ordinary/full-tank radiation collectors.

Native field generators gain buffer energy when **EmitterBolt** projectiles
collide with the correct fixture. They are not powered by an APC wire. Native
field generation requires anchored/static peers on the same parent grid, cardinal
clear sight lines and the selected generator's `MaxLength` (default 8). Energy
drains over time. Emitters themselves require **MVPower**. The normal singularity/
Tesla generator startup failsafe checks for containment fields in cardinal
directions; placing four machines with no working field is insufficient.

Energy collection is also engine-specific. A Tesla coil charges when struck by
lightning and exports through HVPower; radiation collectors require irradiation,
enabled state and a loaded reactive-gas tank. Merely selecting a Tesla/Singularity
generator and four field generators does not define a useful power facility.

**Local recommendation:** keep the selected engine's variant-specific requirements
inside `engineering/containment_engine`: field network, reachable emitter-bolt
lanes, MV supplies, safe operating envelope, and the appropriate collector family
with local working/output access. Do not choose arbitrary unrelated alternatives
from one mixed generator family and expect the collector family to match by
chance. Starter inventory versus deployed initial hardware should be explicit.
Final beam/ray/field feasibility is for a later evaluator, not a claim established
by these map positions. A fixed eight-emitter count is a Tram2 arrangement, not
a native minimum.

## AI upload has a local operator dependency, not a physical upload cable

Gear upload consoles **7745/30375** at `(-7.5,78.5)/(-11.5,78.5)`, Snowball
**3630/3716** at `(64.5,13.5)/(69.5,13.5)`, and Tram2 **5374/5379** at
`(28.5,-12.5)/(28.5,-9.5)` have no saved access override. Their inherited
`StationAiUploadComputer` prototype requires **ResearchDirector** access, powered
slot interaction and a compatible law-provider item. The catalog currently only
declares Command room approaches, whose actor profile lacks ResearchDirector.

**Local recommendation:** declare an RD-capable approach and local operating
access for the upload console; keep room entry and console unlocking separate.
The native updater queries entities matching `StationAiHeld` without a grid or
distance filter in `SiliconLawSystem.OnUpdaterInsert`. Consequently these samples
do **not** justify inventing a same-grid cable or direct upload/core adjacency
dependency. Existing core interaction-grid constraints concern another mechanic.

## Scope

These are local authored obligations and commissioning options. None requires
global layout propagation or network construction now. Most functional support
should be mandatory when the optional facility is selected; mapped device counts,
part staging locations and exact cage dimensions remain editable policy. Evidence
establishes identities and arrangements, not safe continuous power output.
