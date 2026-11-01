# Additional medical and service dependencies

This pass inspected **Marathon, Exo and Sushi**, extending the previous Bagel,
Box, Packed and Saltern review. The companion
[evidence JSON](additional-medical-service.evidence.json) records 40 selected map
entities, source hashes, native references and proposed catalog evidence IDs.
It does not modify the older tools or certify a generated station.

Grid-local positions and inherited prototype components were inspected. Room
partitions conservatively block entire door/window tiles; they are not native
room objects or runtime pathfinding. Saved gas temperatures were decoded from
`GridAtmosphere` using the native `TileAtmosCollectionSerializer` convention.
Nearby equipment demonstrates an authored arrangement, not by itself a proven
connected or operating gas network. Several mapped pumps and filters start disabled.

## Cryogenic treatment requires a gas circuit, not just two machine counts

The initial v3 definition of `medical/cryogenics` supplies `cryo_pods` and
`cryo_cooling` but no relationship between them, no breathable-gas supply, no
pressure regulation, and no waste-gas destination. A room containing those two
feature families could satisfy the initial symbolic catalog while being unusable.

| Map / grid | Clinical pod examples | Supporting equipment |
| --- | --- | --- |
| Marathon / 30 | 6712 `(-33.5,-9.5)`, 6784 `(-31.5,-9.5)` | Freezers 901 `(-36.5,-9.5)` and 7598 `(-34.5,-13.5)`; filter 7602 `(-32.5,-11.5)`; pressure pump 7140 `(-36.5,-11.5)`; oxygen canister 7970 on gas port 6757 at `(-38.5,-9.5)`. |
| Exo / 2 | 1044 `(14.5,-22.5)`, 7884 `(15.5,-22.5)` | Freezer 17411 `(14.5,-25.5)`, filter 19476 `(15.5,-24.5)`, pressure pump 17412 `(17.5,-23.5)`. |
| Sushi / 2 | 9671 `(129.5,46.5)`, 9672 `(125.5,46.5)` | Freezer 14953 `(127.5,44.5)`, flipped filter 11955 `(127.5,46.5)`, pressure pump 14934 `(130.5,46.5)`. |

The native facts are stronger than proximity:

- `CryoPod` has an APC receiver and a single `PortablePipeNode` named **`port`**.
  The native server mixes the pod's internal air with its connected pipe network.
  The contained patient's inhalation, exhalation and atmosphere exposure use that
  internal mixture rather than the surrounding medical room.
- A gas freezer has one bidirectional gas node named **`pipe`**. It cools the
  connected gas; it is not a two-port serial inlet/outlet device. A functional
  header can connect freezer and all pod ports without pretending gas passes
  through a nonexistent second freezer node.
- `PortablePipeNode` requires anchoring and a grid; it supports an appropriate
  same-tile port and ordinary compatible neighboring pipe contacts. The pod's
  default connection points south. Component presence is not connectivity proof.
- The guidebook describes supply from distro or a tank, pressure regulation,
  cooling, filtration and a waste destination. It also explicitly permits
  different network layouts.
- **The current Cryoxadone reagent code uses a maximum patient temperature of
  273.15 K for its healing effect.** The guidebook's separate 170 K statement
  disagrees with this checkout's reagent definition and should not become a hard
  native constant. Cryoxadone also adds heat during metabolism, so initial cold
  gas alone does not establish maintained treatment conditions.

**Recommended mandatory additions:** connect every selected pod to a regulated,
breathable cryogenic gas circuit; connect its cooling equipment; give that circuit
an explicit gas source and waste-removal destination; supply power and state the
required operating conditions. Keep the room itself habitable. Supply chemistry
consumables or an accessible medicine pickup workflow as an authored staffing
policy; staff need not be able to walk into a Chemistry-locked laboratory.
Cold-treatment commissioning is a separate proof from a service-link graph.

The native `GasFilterSystem.OnFilterUpdated` removes a volume from its inlet,
separates the selected gas into the filter branch, and returns retained gas through
its outlet. It has no same-network inlet/outlet rejection or required inlet
pressure-drop guard. A recirculating clinical header is therefore a supported
contract for CO2 removal. This does not permit bypassing the **supply pressure
regulator**: upstream supply, treatment gas and waste remain separate contracts,
and working filter settings, receiver capacity and actual commissioning still need
evaluation. A shared medium name is not permission to merge device pressure
domains indiscriminately.

Canonical locations are `medical/cryogenics#cryo_pods` and
`medical/cryogenics#cryo_cooling`. Use all-instance obligations for the pods, so a
second or third pod cannot be left disconnected. Do not force a dedicated long
pipe to Atmos if a supported local canister supply is an allowed alternative.

Relevant native sources are the CryoPod prototype, `CryoPodSystem`,
`InsideCryoPodSystem`, `PortablePipeNode`, `GasThermoMachineSystem`, and
`Resources/Prototypes/Reagents/medicine.yml`; the JSON records exact paths/hashes.

## A cold store needs enforceable thermal requirements and room-gas exchange

The initial `service/hospitality/cold_store` description promised a 273.15 K
maximum but had a habitable environment, no cooling feature and no thermal
requirement. Prose alone does not impose a constraint.

| Map / grid | Enclosed kitchen-adjacent cold component | Machinery and kitchen interface |
| --- | --- | --- |
| Marathon / 30 | Coarse component 169, 24 usable cells, saved air **235 K** | Freezer 482 `(-16.5,17.5)`, passive vents 368 `(-17.5,16.5)` / 481 `(-15.5,16.5)`; door 460 `(-16.5,12.5)` connects it directly to the kitchen component. |
| Exo / 2 | Component 218, 18 cells, saved air **235 K** | Freezer 5297 `(44.5,-36.5)`, passive vent 6353 `(44.5,-37.5)`; kitchen door 606 `(42.5,-39.5)`. |
| Sushi / 2 | Component 136, 17 cells, saved air **235 K** | Enabled freezer 14956 `(73.5,88.5)`, freezer vent pump 15136 `(74.5,87.5)`; kitchen door 308 `(71.5,85.5)`. |

These are cold **rooms**, not just sealed refrigerator-item inventories. A gas
freezer must exchange cooled gas with the room through a suitable circuit/vent
arrangement. Putting an isolated gas freezer in a room cannot cool its floor air.

**Recommended mandatory additions when the cold store is selected:** explicit
cold-room thermal bounds, cooling equipment, a room-gas exchange endpoint, power
and a maintained-temperature obligation. The exact 235 K setpoint is a repeated
map example, not a universal requirement; the requested maximum of 273.15 K is
editable author policy. Shared-boundary containment and doorway airtightness
remain separate from continuous cooling capacity.

The `food_cold_store` boundary should bind **`same_parent`** to its hospitality
instance, and normal staffing should include **Kitchen** access. Its initial
generic Service actor lacked that credential even though the prescribed door was
Kitchen-locked. These fixes prevent another repeated cluster's cold store, or an
unauthorized generic actor, from satisfying the intended kitchen workflow.

## Botany needs a reachable water source

All three new station examples place water supplies with growing work areas:

| Map / grid | Growing hardware | Water supplies in the same conservative floor component |
| --- | --- | --- |
| Marathon / 30 | Tray 328 `(-19.5,12.5)` | High-capacity full water tank 13365 `(-23.5,5.5)`; sink 400 `(-23.5,13.5)`. |
| Exo / 2 | Tray 5029 `(-2.5,-52.5)` | High-capacity full water tank 19013 `(-3.5,-54.5)`. |
| Sushi / 2 | Tray 16804 `(56.5,82.5)` | High-capacity full water tank 24488 `(56.5,80.5)`; sink 19829 `(58.5,82.5)`. |

Native `PlantGrowthSystem` consumes tray water and penalizes thirsty plants.
`WaterTankHighCapacity` contains 3840 units of Water by default; a normal water
sink has an initial inventory and regeneration. This supports a required,
reachable `service/botany` water-source feature, with suitable containers/working
access. It does **not** justify inventing a native pressurized-water pipe service.
A finite tank is a supported initial supply, not a promise of indefinite refill.
Nutrients and seeds are additional stocked gameplay inputs; the room's existing
biogenerator and seed extractor do not alone constitute all initial inventory.

Kitchen adjacency is still a preference. Sushi's coarse kitchen/Botany partition
can even merge through its service arrangement; the maps do not support forcing
every Botany into a particular rectangular suite or one mandatory shared door.

## Morgue access has cross-department consumers

Marathon morgue doors **583** `(-20.5,-5.5)`, **907** `(-26.5,-5.5)`, **1757**
`(-22.5,0.5)`; Exo **3714** `(28.5,-25.5)` and **9409** `(29.5,-25.5)`; Sushi
**526** `(119.5,60.5)`, **529** `(115.5,62.5)` and **491** `(119.5,64.5)` all
inherit `DoorElectronicsMorgue`. The actual policy is OR across
**`[[Medical], [Detective], [Chapel]]`**, not all three credentials together.
Sushi additionally illustrates that one edge can lead through Surgery while a
different edge reaches maintenance; the neighboring room matters.

The initial catalog only required a Medical staff route. This misses the
Detective and Chaplain consumers represented by the native door policy.

**Recommended explicit station policy:** add a `morgue_door` interface preserving
those OR clauses and mandatory routes from `public_hall` to `medical/morgue` for
the existing `detective` and `chaplain` profiles. Exclude private Medical
circulation/treatment rooms from these approaches, or prove another permitted
route with those actors' actual credentials. Keep the ordinary Medical route.
Do not solve the problem by granting Medical credentials to the Chaplain or
Detective, or by making the entire clinical department public.

This is a functional multi-role access policy supported by repeated hardware,
not a claim that every sampled map already proves the complete route. The
conservative partition and door inventory do not certify whole-station access.
Morgue-to-chapel/crematorium travel can carry a weighted short-route preference;
the evidence does not require a shared wall or make the morgue a chapel child.
Likewise, no mandatory disposal-pipe route for bodies follows from these maps.

## Integration priorities

1. All-pod cryogenic gas/cooling links with regulated supply and waste handling.
2. A real cold-room thermal contract and cooling-to-room-gas relationship.
3. Mandatory Botany water-source availability and usable working access.
4. Explicit Morgue door policy and credential-correct Detective/Chaplain access.

These additions repair concrete missing dependencies. Layout-specific adjacency,
exact equipment counts, unlimited supplies and startup automation should not be
silently elevated from examples into native invariants.
