# Local logistics and service dependencies found in maps

This pass screened Hike, Gear, Plasma, Relic, Snowball and Tram2. Detailed cases
below use Gear, Plasma, Tram2 and Hike. The [evidence JSON](local-logistics-service.evidence.json)
records 33 selected entities, their grid-local positions/components, four map
hashes and 13 native source hashes. Earlier analysis helpers were read only.
Neither a complete disposal graph nor operating game state was simulated.

## Cargo telepads require the approving console's actual link

The maps contain explicit `OrderSender -> OrderReceiver` links, not just nearby
computers and a telepad:

| Map/grid | Approving console | Receiving pad |
| --- | --- | --- |
| Gear / 12 | `ComputerCargoOrders` **2479**, `(50.5,-24.5)`, and **3967**, `(63.5,-15.5)` | `CargoTelepad` **16597**, `(57.5,-22.5)` |
| Plasma / 2 | `ComputerCargoOrders` **9996**, `(-101.5,12.5)`, and **17840**, `(-98.5,13.5)` | `CargoTelepad` **27157**, `(-88.5,19.5)` |
| Tram2 / 15 | `ComputerCargoOrders` **3611**, `(61.5,-10.5)`, and **3612**, `(63.5,-12.5)` | `CargoTelepad` **5095**, `(60.5,-3.5)` |

Each pair of consoles in a row shares one pad. Many departmental request
computers are linked to those same pads too. These are not one-to-one artifact
console/analyzer associations. Their configured source range is 200, further
showing why generic five-tile computer adjacency would be wrong.

`Content.Server/Cargo/Systems/CargoSystem.Telepad.cs` only handles an order when
the telepad is idle and powered, belongs to the order's station, and has the
approving console in `DeviceLinkSink.LinkedSources`. Anchoring is also required
for the enabled state. Hike contains pad **10520**, grid 1 `(-2.5,31.5)`, but the
examined raw local console **7364** has no saved `linkedPorts` entry; mere mapped
presence is therefore not evidence that this branch is commissioned.

**Local authored dependency:** an optional receiving-pad feature owns its need
for at least one eligible, configured approving console. The physical signal runs
console -> pad; the **pad remains the obligated source** so selecting a mandatory
orders computer cannot force an optional telepad. Multiple consoles may share a
pad. Require same station, power, anchoring and usable crate-retrieval space when
the pad is selected. The cargo shuttle remains an independent fulfilment route;
the map examples do not make telepads universally mandatory.

## A disposal unit needs an entry trunk at the same tile

Actual examples pair two distinct entities at one location:

| Map/grid | Disposal unit | `DisposalTrunk` | Shared position |
| --- | --- | --- | --- |
| Gear / 12 | **1540** | **30502** | `(-10.5,2.5)` |
| Plasma / 2 | **696** | **6371** | `(-113.5,-20.5)` |
| Tram2 / 15 | **764** | **9296** | `(12.5,-57.5)` |
| Hike / 1 | **7452** | **8140** | `(8.5,-7.5)` |

`SharedDisposalUnitSystem.TryFlush` searches the unit's grid tile for an entity
with both `DisposalEntry` and `DisposalTube`. Without one, flushing fails. The
unit must also be powered, anchored and ready. `DisposalTrunk` supplies those
entry/tube components; the visible unit does not include a hidden pipe entity.

**Local authored dependency:** realize each selected disposal unit as the unit
plus a co-located entry trunk. Expose the trunk's tube connection as a local
service endpoint, preserving orientation and same-grid support. A generic room
containing a `DisposalUnit` feature is insufficient.

## Entry connectivity does not prove an intentional discharge

Maps also contain termination trunks without a unit on their tile, in local
waste-processing areas:

- Hike **7756**, grid 1 `(48.5,-13.5)`, is two tiles west of recycler **2905**
  `(50.5,-13.5)`. The trunk is rotated 270 degrees and the recycler 90 degrees.
- Gear **13076**, grid 12 `(37.5,19.5)`, is four tiles north of recycler **16766**
  `(37.5,15.5)`.
- Plasma **19731**, grid 2 `(-102.5,24.5)`, is near recycler **24821**
  `(-100.5,20.5)`; this observation alone does not prove their connecting feed path.
- Tram2 **11560**, grid 15 `(80.5,-7.5)`, is near recycler **16587**
  `(82.5,-8.5)`.

These records establish local discharge/processing arrangements, not end-to-end
pipe reachability. `DisposalTubeSystem` uses rotated directional exits and
anchored same-grid neighbors. Routers choose branches using configured tags.
The selected native `DisposalUnit` really declares `DisposalTagger.tag: waste`
in `units.yml`; `DisposalTaggerSystem.OnDisposalFlush` adds that string to the
flush event's tags, and `TryFlush` transfers it into the holder. `DisposalRouterWaste`
in `pipes.yml` tests the same tag. This native **payload tag** is distinct from
the YAML's abstract service-medium name, even though both are spelled `waste`.
The local contract must compare its expected initial tags with the selected
intake's effective configuration. Its schema also permits `routingTags: []` for
an explicitly authored untagged intake variant; no tag is inferred from a medium.
A disconnected tube may eject
its contents: this must not be accepted as an intentional destination merely
because the contents eventually leave a pipe.

The server holder system determines ejection direction from incoming travel.
Without a receiving disposal unit it can pry up the terminal tile and tile in
front, knock down occupants and throw contents. A bare trunk's drawing direction
is therefore not itself a complete discharge-clearance description. Native
fixtures, travel direction and selected holder settings matter.

**Local authored dependency:** the selected processing installation needs a
declared terminal and a valid directed/tag-compatible path from its intake.
Require usable receiving/discharge space and independent servicing access. A
controlled recycler feed is a deliberate policy option; it is not a safe ordinary
crew passage. The definitions expose this installation's inlet/terminal needs;
they do not solve or enumerate the station-wide waste network. The draft patch
uses a recoverable receiving-floor policy rather than claiming that every map's
observed recycler arrangement is safe for transported crew.

## A useful optional produce-transfer pattern

Tram2 has freezer doors **183**, grid 15 `(2.5,2.5)`, and **21023**,
`(-1.5,1.5)`, both `AirlockFreezerKitchenHydroLocked`. They bound the same coarse
17-tile cold-store component, containing freezer **19894** `(1.5,3.5)` and meat
spikes. Door 183 connects toward the 67-tile kitchen component, containing
Kitchen beacon **18988** `(7.5,2.5)`; door 21023 opens toward a 13-tile intermediate
area. The nearby Hydroponics door **867** `(-1.5,-1.5)` and trays such as **7355**
`(-0.5,-4.5)` identify the supply side.

The native electronics admit **Kitchen OR Hydroponics**, not both credentials
together. This supports a configurable Botany -> shared cold-store produce
handoff route without giving Botanists general entry to the cooking workspace.
It does not establish a universal direct Botany/Kitchen shared border. The coarse
components close whole door/window tiles and do not constitute actor pathfinding.
This single detailed arrangement is recorded as a candidate preference; the
patch does not promote it to a universal mandatory interface.
