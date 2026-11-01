# Engineering, exterior services and grid identity

This is research for the independent third architect. It defines no generator or GUI. Both earlier tools were read only. The companion [engineering-evidence.json](engineering-evidence.json) contains map hashes, exact entity UIDs, grid-local poses, native component facts, support tiles, saved initial gas mixtures, conservative room-component observations and proposed evidence-catalog entries.

The station examples are `bagel.yml`, `box.yml`, `saltern.yml` and `packed.yml`. They demonstrate authored arrangements; they do not establish that every station must copy their counts, dimensions or startup state. No atmosphere, combustion, sunlight or power-load simulation was run.

## What must be distinct in the format

| Fact | Consequence for authored constraints | Basis |
|---|---|---|
| Ordinary pipe contacts search neighboring anchored nodes on one engine grid, with matching node group, layer and reciprocal directions. | Declare grid identity, actual ports and a supported route. Spatial proximity across two grids supplies no pipe connection. | Native mechanic |
| Anchoring rejects an empty tile; `Lattice` is nonempty but has `isSpace: true`. | Grid support, walkability, enclosure and atmosphere are independent. An exposed pipe/radiator can be on the station grid, supported by lattice, in vacuum. | Native mechanic |
| Docking rejects ports on the same grid. | A shuttle counterpart is a different grid; an exterior docking room remains on the station grid. | Native mechanic |
| TEG center/circulators require an exact adjacent assembly. | An assembly is more specific than a room containing three machine prototypes. | Native mechanic |
| A hot burn chamber and exposed cold loop are required for the requested TEG program. | Make these mandatory activation dependencies when TEG is selected. Label the requirement `user_requirement`, supported by map patterns. | User requirement, not a universal engine restriction |
| A remote physical wing can remain on the same engine grid. | Logical nesting, physical enclosure, outboard placement and engine-grid identity must not be aliases. | Map pattern and native services |

Ordinary pipe behavior is in `Content.Server/NodeContainer/Nodes/PipeNode.cs:160–247`. Internal gas valves/manifolds explicitly connect their own nodes through `AddAlwaysReachable`; this is not a general cross-grid pipe bridge. Nonempty anchoring support is checked in `RobustToolbox/Robust.Shared/GameObjects/Systems/SharedMapSystem.Grid.cs:1286–1294`. Lattice's vacuum/support properties are in `Resources/Prototypes/Tiles/plating.yml:67–82`.

## TEG: exact assembly and separate thermal circuits

An observed, convenient canonical pose is Box's assembly. Coordinates below are relative tile offsets. Native rotation zero faces south; +90° faces east, +180° north and +270° west.

| Part | Prototype | Offset | Rotation | Box evidence on grid 8364 |
|---|---|---|---|---|
| Center | `TegCenter` | `(0,0)` | `90°` | UID 4052 at `(18.5,-76.5)` |
| Hot circulator, native B in this pose | `TegCirculator` | `(-1,0)` | `180°` | UID 27023 at `(17.5,-76.5)` |
| Cold circulator, native A in this pose | `TegCirculator` | `(+1,0)` | `0°` | UID 27025 at `(19.5,-76.5)` |

Box's hot sensor UID 13067 is at `(17.5,-77.5)`, next to the hot circulator's inlet. Cold sensor UID 4177 is at `(19.5,-75.5)`, next to the cold inlet. Both labels are explicit `GasPipeSensorTEGHot` / `GasPipeSensorTEGCold` prototypes. The native algorithm can exchange which side is hot: `TegSystem` compares incoming mixture temperatures. A/B are assembly sides, not universally hot/cold roles.

For this canonical pose:

| Authored endpoint | Native binding | External direction | Service |
|---|---|---|---|
| `hot_in` | hot circulator `NodeContainer.inlet` | south | Gas into TEG hot side |
| `hot_out` | hot circulator `NodeContainer.outlet` | north | Gas returning to the heat source |
| `cold_in` | cold circulator `NodeContainer.inlet` | north | Gas into TEG cold side |
| `cold_out` | cold circulator `NodeContainer.outlet` | south | Gas returning to the cooling source |
| `hv_output` | center `NodeContainer.output` | same-tile cable device | `HVPower` output |
| `startup_power` | center `ApcPowerReceiver`, inherited `ExtensionCableReceiver` | powered receiver/provider coverage | Low-voltage operating power |

`startup_power` is a **component receiver**, not a native node named `power`. The assembly format needs `nativeComponent` as an alternative to `nativeNode`, or it must explicitly label a separate abstract service header. The center's actual native nodes are `output` and `teg`.

Sources: `Content.Server/Power/Generation/Teg/TegNodeGroup.cs:34–55,123–173`; `Resources/Prototypes/Entities/Structures/Power/Generation/teg.yml:1–108,110–197`.

The native operational conditions are stronger than “hot room beside cold room”:

- The center's APC receiver must be powered; otherwise native output is set to zero.
- Each circulator transfers gas only when its inlet pressure exceeds outlet pressure and valid gas/temperature/volume conditions exist.
- Both sides must deliver gas; useful power requires a temperature difference. Native efficiency is reduced at low differences through `tanh(deltaTemperature / 700)`.
- The TEG exchanges thermal energy between the two sides; it does not mix their gas streams together.
- A pump, a closed-loop return, initial gas inventories and operating/control state are separate requirements. A drawn pipe route or a machine count does not prove any of them.

The details are in `TegSystem.cs:99–177,324–349`. A thermodynamic loop is not the same object as one permanently mixed native pipe network. Device transfers and control states can separate pressure domains. Do not connect hot and cold endpoints into one common gas circuit merely because both use `NodeGroupID: Pipe`.

## Burn chambers and exposed radiators observed in maps

All four examples keep the TEG, hot apparatus and exposed cold apparatus on the **same station grid**.

| Map / station grid | Hot-side example | Approximate enclosed hot work floor | Exposed cold-side example |
|---|---|---|---|
| Bagel / 60 | `HeatExchanger` 14905 `(-21.5,52.5)` and 15285 `(-19.5,52.5)`; injector 15293 `(-20.5,52.5)` | 9 tiles, 3×3, component 135 | `HeatExchanger` 15483 `(-13.5,51.5)` and 15505 `(-11.5,49.5)` on **Lattice**, saved immutable vacuum |
| Box / 8364 | `HeatExchanger` 27067 `(14.5,-87.5)`; injector 26062 `(14.5,-86.5)` | 9 tiles, 3×3, component 263 | `HeatExchanger` 24960 `(31.5,-75.5)` on **Lattice**, saved immutable vacuum |
| Saltern / 31 | injector 11045 `(48.5,12.5)`; igniter 6548 `(48.339073,12.198484)`; radiators 3508, 3675, 3888, 4275, 4289, 4292, 4469, 4470, 11047 | 15 tiles, 5×3, component 191 | Radiators 7227/7228/7352/6906 at `(50.5,20.5..23.5)` on **Lattice**, saved immutable vacuum |
| Packed / 2 | injector 12956 `(48.5,-45.5)`; igniter 16406 `(48.5,-47.5)` and others; radiators 12959/13028/15257 | 20 tiles, 4×5, component 175 | Radiators 4492/12861/12862 at `(42.5,-51.5..-53.5)` on **Lattice**, saved immutable vacuum |

The floor counts come from conservative closed-door floor components, not a native “room” object. They exclude wall cells and furniture clearance; directional partial walls/windows are treated as whole blocked tiles. They are useful sizing evidence, not mandatory minimums.

Saltern and Packed explicitly contain `Igniter` items in the enclosed hot component. `Igniter` is an 800 K `IgnitionSource` with `IgniteOnTrigger`, wireless `DeviceNetwork` and `DeviceLinkSink.Trigger`. It is not a gas node, and its presence does not prove a working remote-control binding or automatic startup. See `Resources/Prototypes/Entities/Objects/Devices/Electronics/igniter.yml:1–34`.

Useful actual prototype endpoints:

| Prototype | Actual gas nodes at native rotation zero | Meaning / caveat |
|---|---|---|
| `HeatExchanger` | `inlet` north; `outlet` south | Transfers heat with its actual surroundings. Internal flow can reverse with pressure. |
| `GasPressurePump` | `inlet` north; `outlet` south | Requires an authored operating state and suitable pressure configuration. |
| `GasVolumePump` | `inlet` north; `outlet` south | Requires an authored operating state and throughput configuration. |
| `GasOutletInjector` | `pipe` south | Injects into the containing environment; distinct from a closed heat-exchange loop. |
| `GasThermoMachineHeater` / `GasThermoMachineFreezer` | `pipe` south | Active thermal equipment, not an exposed radiator substitute by definition. |
| `GasPipeSensorTEGHot` / `GasPipeSensorTEGCold` | `monitored`, longitudinal | Labels support circuit-role evidence; sensors do not create heating/cooling. |

Several mapped pump components are saved `enabled: false`. Hot chambers are often saved with empty room air. Therefore these observations establish an intentional startup arrangement, not a continuously burning or self-starting power plant.

The requested catalog can make all of the following mandatory whenever TEG is selected: an enclosed process chamber outboard from normal department floor; controlled service access; a vacuum separation policy; a cold radiator region open to exterior-connected vacuum; distinct hot/cold service circuits; and floor-or-lattice links on the station grid. Those are explicit user/design requirements. The engine itself does not require a named burn room, a fixed gap size, a particular chamber area or one fixed hot side.

`HeatExchangerSystem.cs:34–46,75–123` checks actual air blocking, surrounding gas heat capacity, radiation background and convection. A radiator inside a pressurized room can heat that room. In exposed vacuum it radiates against the cosmic background. A generic neighboring “space” label alone proves neither exposure nor cooling capacity.

## Cargo and shuttle interfaces

Box cargo has station-side `AirlockExternalGlassShuttleCargo` UIDs 17677 and 18039 at `(-44.5,-28.5)` / `(-44.5,-30.5)`, grid 8364. Console 17687 is at `(-41.5,-29.5)`. Bagel similarly has station-grid 60 ports 5477/6768 at `(59.5,12.5)` / `(59.5,14.5)`; Packed grid 2 has ports 451/607 at `(3.5,24.5)` / `(3.5,26.5)`.

The native cargo shuttle map is a different grid: `Resources/Maps/Shuttles/cargo.yml`, grid UID 173. Dock UID 50 is `AirlockGlassShuttle` at `(-5.5,1.5)`, rotation −90°. The map's non-lattice floor bounds are 7×15 in its local orientation. Arrivals is grid 292, floor bounds 9×18; emergency is grid 410, floor bounds 11×21. These **floor bounds are not full collider/berth certificates**. A future docking evaluator must include structures/fixtures, port alignment, actual clear approach and exterior connection.

Native docking requires different grids, anchored ports, a common map, range and facing alignment (`DockingSystem.cs:422–443`; `Content.Shared/Shuttles/Systems/SharedDockingSystem.cs:30–59`). A hull perimeter, a cargo console, a window to space or an arbitrary exterior door cannot satisfy docking on its own. Multiple docking-port features and a clearance reservation should remain separate from cargo reception's ordinary item/passenger access.

## Solar installations

Bagel solar UID 65 at `(37.5,-64.5)`, grid 60, is on lattice in saved immutable vacuum. The four inspected maps have 401, 240, 102 and 131 solar-panel components on their main station grids respectively. Bagel additionally has 10 solar panels on secondary grid 7536; that is a separate electrical/grid context, not evidence that all arrays may bridge grids implicitly. These are whole-map observations, not recommended per-array counts.

Panels use a same-grid `NodeContainer.output` cable-device node in `HVPower`. They need a real native power path. Their output depends on angle and an opaque-collision ray toward the sun out to 20 tiles (`PowerSolarSystem.cs:132–178`). Exterior placement alone does not guarantee output; an occluding hull can shadow an installation.

Mapped tracker examples include Bagel 12702 `(31.5,-78.5)` and Saltern 6710 `(32.5,-31.5)`, both lattice-supported. However, `SolarTracker`'s prototype is a physical marker/machine without a dedicated solar-control/power-node component. Current native solar consoles change global target panel rotation/velocity (`PowerSolarControlConsoleSystem.cs:35–69`). Requiring a tracker per authored array is a recognizable map/design policy, not a necessary native generation connection. The schema should support that policy without pretending a tracker physically wires a specific array.

## AI core and remote wings

Actual `PlayerStationAi` job-ready cores:

| Map | Core UID / native grid | Grid-local position | Saved room air |
|---|---|---|---|
| Bagel | 8146 / 60 | `(-111.5,25.5)` | Ordinary 293.15 K station mix |
| Box | 27979 / 8364 | `(-0.5,-14.5)` | Ordinary 293.15 K station mix |
| Packed | 14506 / 2 | `(94.5,30.5)` | Ordinary 293.15 K station mix |

Bagel's remote AI satellite beacon 18704 and upload computers 7413/7444 remain on station grid 60. “Satellite” does not imply a detached engine grid. `SharedStationAiSystem.cs:206–222` explicitly rejects cross-grid remote interaction. `StationAiVisionSystem.cs:59–116` separately checks vision seeds, their enabled/power/anchor conditions and occlusion. A same-grid relation is necessary but is not complete camera/interaction coverage.

The core prototype declares a 500 W APC receiver, extension receiver and 300,000 J starting battery. `PlayerStationAi` adds the Station AI job container spawn to `PlayerStationAiEmpty` (`Resources/Prototypes/Entities/Mobs/Player/silicon.yml:235–257,331–340`). No universal cryogenic AI-room requirement was found in these prototype/code paths or sample initial atmospheres. A cooled AI room may be authored as policy; do not silently import an SS13 cooling requirement.

## Authoring handoff and limits

The JSON's `canonicalTegAssembly` is ready to translate into the agreed assembly schema, including the Box hot/cold role assignment and the LV component endpoint. Its `proposedEvidenceCatalog` contains stable suggested evidence IDs for the catalog author. No schema or catalog file was modified by this research.

Use native/mechanical constraints for exact assemblies, support, grid affinity, compatible ports and basic operating dependencies. Use map-pattern evidence for representative sizes, equipment grouping and access organization. Use `user_requirement` for the requested mandatory outboard burn chamber, exposed cold loop and separation/access policy when TEG is selected. Use explicit abstract service headers where complete control, reaction, fuel supply, pump regulation, sustained heat rejection or native runtime behavior remains unspecified.

Saved atmosphere is a startup snapshot. In particular, some exterior plating under solar equipment has ordinary saved air; this is not proof of an airtight solar room or sustainable outdoor pressure. Exterior-connected vacuum, enclosing boundaries and supported native grid tiles need their own declarations and future geometric verification.
