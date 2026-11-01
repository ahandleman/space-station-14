# Additional dependency review: science and cargo

This pass examined **Marathon, Exo and Sushi**, extending the earlier Bagel/Box/Packed/Saltern review. The accompanying [evidence](additional-science-cargo.evidence.json) records map hashes, selected entities, saved device bindings, grid-local positions, local tile samples and native source hashes. Earlier tools were read only. No server, physics, device-network or geometry simulation was run. This report proposes definitions; the catalog is the authoritative authored policy.

## Artifact analysis needs a configured pair

The existing catalog places `artifact_analyzer` and `analysis_console` in `science/artifact_lab`, but mere co-location does not make them functional.

| Map / grid | Console | Analyzer | Authored connection |
| --- | --- | --- | --- |
| Marathon / 30 | **15282** `(37.5,10.5)` | **22233** `(36.5,12.5)` | Saved `DeviceLinkSource.linkedPorts`: `ArtifactAnalyzerSender` to `ArtifactAnalyzerReceiver`; source range 5. |
| Exo / 2 | **5049** `(-21.5,-27.5)` | **5037** `(-19.5,-32.5)` | Same explicit ports; range overridden to **8**, with actual center distance about 5.39 tiles. |
| Sushi / 2 | **9243** `(124.5,70.5)` and **9244** `(124.5,73.5)` | **17036** `(128.5,68.5)` and **17035** `(128.5,72.5)` respectively | Two separate saved pairings, not either console controlling an arbitrary analyzer. |

`Content.Shared/Xenoarchaeology/Equipment/SharedArtifactAnalyzerSystem.cs` restores the configured analyzer from the console's linked sink, rejects a second link on either already-bound machine, and requires the analyzer to be powered in `TryGetAnalyzer`. `Content.Shared/DeviceLinking/Systems/DeviceLinkSystem.Links.cs` checks map-coordinate distance against the source's configured range when making a link. Saved links are restored separately; proximity alone does not create a link. The existing prototype's default source range is 5, but Exo demonstrates why a generator must evaluate the **selected machine configuration**, not claim 5 is an immutable universal radius.

**Recommended mandatory contract:** each selected analysis console binds its `ArtifactAnalyzerSender` to a distinct selected analyzer's `ArtifactAnalyzerReceiver` in its lab instance. Require both machines' native operating power and reserve analyzer working/item-placement access. Represent the actual binding as a typed relationship extension, with source/target native components and port names, and check selected-machine range. Ordinary `dependency` establishes presence only; `route` does not encode this connection. A generated file should persist the binding, not leave the player to repair a decorative pair.

## Research clients need an eligible research server

The catalog lists a server room and research machinery, but does not declare their functional association. Native `ResearchSystem.GetServers` (`Content.Server/Research/Systems/ResearchSystem.cs`) discovers research servers on the **client's engine grid**. `ResearchSystem.Client.cs` selects an available server at map initialization/anchoring. `ResearchSystem.Server.cs` registers clients and raises point-production events on registered clients; its ordinary point update requires server power. `Content.Server/Xenoarchaeology/Equipment/ArtifactAnalyzerSystem.cs` cannot extract artifact points without the analysis console's associated research server. Lathe technology synchronization is in `ResearchSystem.Technology.cs`.

New examples all place the server and these clients on one grid:

- Marathon / grid 30: server **12746** `(33.5,15.5)`, analysis console **15282**, circuit imprinter **12805**, protolathe **12864**, exosuit fabricator **12799**, anomaly vessels **9523/9531**.
- Exo / grid 2: server **12845** `(-20.5,-16.5)`, analysis console **5049**, imprinter **8791**, protolathe **7432**, fabricator **15404**, vessels **12833/12834**.
- Sushi / grid 2: server **19311** `(125.5,81.5)`, analysis consoles **9243/9244**, imprinter **8878**, protolathe **18194**, fabricators **11383/11384**, vessels **17030/17031**.

**Recommended mandatory contracts:** selected research clients require `science/server_room#research_server`, same-grid compatibility, and valid runtime client/server association. Apply this to research console, protolathe, imprinter, robotics fabricator, analysis console and anomaly vessels. This must activate the supporting optional room if the source is mandatory. No shared border or server-room adjacency follows from this mechanic. Ordinary owned room placement can stay distributed.

An anomaly vessel is assigned by using a scanner containing a scanned anomaly; its source is not established by placing it beside an anomaly generator. `Content.Server/Anomaly/AnomalySystem.Vessel.cs` requires a powered, assigned vessel for point production and rejects assigning an already-connected anomaly. Do not invent mandatory vessel/generator adjacency or require an active anomaly at round start. For the facility definition, power, access, research-server availability, and equipment needed for player setup are appropriate dependencies.

## Salvage requires its own exterior handling access

A cargo shuttle dock is not the same function as retrieving magnet debris or working outside the hull. The current `cargo/salvage` internal route to Warehouse is useful, but supplies no space access.

- Marathon / grid 30 has separate external Cargo doors **11800** `(34.5,-12.5)` and **11658** `(34.5,-15.5)`. They are mutually linked `DoorStatus -> DoorBolt`. The south edge has plating through `y=-16` and bare Space at `y=-17` beside it. Cargo shuttle port **1083** is separately at `(38.5,-5.5)`. Salvage staff airlock **15622** is `(33.5,-8.5)`.
- Sushi / grid 2 has Salvage-locked inner doors **11464/11631** at `(97.5,25.5)/(97.5,26.5)`, leading toward a small exterior vestibule with Cargo-locked outer doors **18829/20617** at `(99.5,24.5)/(100.5,24.5)`. Cells `(99,23)` and `(100,23)` are bare Space. These doors have explicit status/logic bindings. The cargo shuttle port **299** is elsewhere, `(82.5,30.5)`.
- Exo / grid 2 also has a distinct exterior side access at Cargo doors **6883/6884/6885/6886** around `x=46.5/48.5, y=-98.5/-99.5`, with logic bindings. Its south shuttle-facing door arrangement is a separate local function.

The relevant door prototypes inherit Cargo or Salvage access boards from `Resources/Prototypes/Entities/Structures/Doors/Airlocks/access.yml`; `door_access.yml` defines those credentials. The catalog's Salvage actor already carries both, so this route need not weaken the doors to public access.

**Recommended mandatory policy:** add an exterior handling/EVA vestibule for selected Salvage, reachable by an authorized indoor route from `cargo/salvage`, followed by an explicit EVA route to exterior. Avoid requiring passage through the cargo shuttle berth. Its enclosure, exterior frontage and two-stage isolation should be explicit. This is a supported functional design policy, not a claim that all maps use identical airlock controls or that a pair of doors simulates pressure equalization. Do not require a fixed distance to a particular magnet tile.

## A magnet also needs somewhere to put debris

Marathon magnet **2040** `(44.5,-18.5)` faces east (rotation +90 degrees); Exo **17227** `(53.5,-101.5)` and Sushi **19323** `(94.5,22.5)` face south (rotation 0). These are station-grid devices with exposed deployment directions, not docking ports.

`Content.Server/Salvage/SalvageSystem.Magnet.cs`, `TryGetSalvagePlacementLocation`, derives an origin using the attached grid's bounds, offsets along the magnet's facing direction plus randomized lateral displacement, randomly rotates the selected debris bounds, and rejects candidates intersecting existing grids. Native component defaults are `MagnetSpawnDistance = 64` and `LateralOffset = 16`; the search starts at half the distance and increases it on collisions. These are **search inputs**, not a guaranteed fixed empty rectangle or a mandatory 64-tile distance from the magnet.

**Recommended mandatory contract:** selected magnet feature requires a compatible orientation and an exterior deployment region with viable nonintersecting placements for the configured debris set; reserve that region against future station construction and other authored reservations such as shuttle approach lanes. A generic feature-relative clearance extension can carry this policy. Native placement must remain the final feasibility witness; checking one empty ray or the room's `zone: hull` is insufficient. Keep hazard/collision clearance distinct from the EVA crew route.

## Material handling belongs near cargo work, with a safe service route

The only current catalog recycler is in Engineering waste. New maps independently place material processing beside Salvage work:

- Exo recycler **11474** `(50.5,-96.5)`, rotation -90 degrees, has aligned belts **6868** `(51.5,-96.5)` and **6918** `(49.5,-96.5)` on opposite sides. Salvage equipment lockers **9177/17329/18765** are nearby inside the working area.
- Sushi recycler **18576** `(97.5,32.5)`, rotation 180 degrees, has the straight south belt run **9469/9470/9471** at `y=31.5/30.5/29.5`. The protected Salvage entries and exterior handling vestibule are in this same work area.
- Marathon recycler **21582** `(32.5,-11.5)` is beside the Salvage/exterior handling cluster; a separate engineering/waste recycler **12974** is elsewhere `(49.5,23.5)`.

The native Recycler prototype has `CollideMaterialReclaimer`, `MaterialStorage`, an internal `Conveyor`, and `Reverse`/`Forward`/`Off` sink ports. `Content.Server/Materials/MaterialReclaimerSystem.cs` gates operation by power and broken state. Conveyor feeds and an output-handling side are supported layout patterns; they are not mandatory native prerequisites for manual item feeding.

**Recommended configurable addition:** an optional `cargo/material_processing` room or contained work zone, with a recycler and mandatory authorized handling routes to `cargo/salvage` and `cargo/warehouse` when selected. Reserve feature-relative input/service/output access and a crew route that does not require walking through the recycler. Conveyors remain optional. Do not repurpose Engineering's waste facility as a necessary cargo transit room or impose an unsupported fixed room distance.

## Limits and rejected shortcuts

This pass does not infer adjacency from beacon labels or turn every observed machine into a mandatory standard. Native mail teleporters are not evidence for a mandatory disposal-tube network. No new universal prison/warden ordering is established by these samples. A generic DeviceLink mechanism is useful for future cell timers, but the current map extracts did not establish a repeated, fully linked timer pattern strongly enough to author one here. Current layouts and arbitrary feature counts remain author policy; exact operating state and geometry must be verified later.
