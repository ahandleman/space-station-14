# Local custody and body-handling dependencies

**Current catalog policy:** the timed-cell recommendation below is historical.
The requested baseline now uses timed lockers in Sentencing, separate Brig
entry/exit turnstiles, a Sentencing-to-Perma connection, and independent Courtroom
and Law Office placement. See [the current catalog policy](../CATALOG.md#configurable-choices-and-deliberate-policy).
The recorded Plasma entity links below are unchanged observations, not evidence
for that replacement locker arrangement.

This pass inspected Hike, Gear, Plasma, Relic, Snowball and Tram2. It looked first
at saved entities, device bindings and local placement, then checked the native
mechanisms behind those arrangements. The [evidence JSON](local-custody-medical.evidence.json)
records exact map UIDs, grid-local poses, selected inherited components, rotated
loading positions, sampled floor/occupants and current source hashes. No game
server, collision simulation or whole-room reachability test was run.

## Timed cells need their own control pair

Plasma has two explicit timed-cell pairs on grid **2**:

| Timer | Gate | Saved bindings |
| --- | --- | --- |
| `BrigTimer` **5396**, `(-22.5,4.5)` | `AirlockSecurityGlassLocked` **8661**, `(-22.5,5.5)` | `Start -> Close`; `Timer -> Open`; `Timer -> AutoClose` |
| `BrigTimer` **14643**, `(-22.5,8.5)` | `AirlockSecurityGlassLocked` **8660**, `(-22.5,7.5)` | The same three bindings to its own gate |

These are saved `DeviceLinkSource.linkedPorts`, not inferred links between nearby
objects. The two timers do not point at an arbitrary other cell's gate. The gate
electronics supply Security access, and the timer's own AccessReader also requires
Security. The extracted native APC loads are 100 W for the timer and 20 W for the
gate; a timer's mere presence does not establish an operating release path.

`SignalTimerSystem` emits `Start` when started and `Timer` when completed or
cancelled through its UI. `DoorSignalControlSystem` handles `Close` and `Open`.
The observed `AutoClose` signal has a more specific meaning: it disables an
airlock's automatic closing until it closes again. It is not a general bolt
toggle, and `Open` does not prove a bolted, unpowered or obstructed gate will open.

**Suggested local addition:** the timed-cell archetype in `security/cell` should
own one timer and its entry-gate feature, with start/finish bindings to the
**same gate instance**. The gate realizes the cell's existing Brig interface,
not a second unrelated door placed inside the cell. Require powered controls,
authorized operator access and a commissioned close/release cycle. Persist the
bindings. Separate independent matching for the start and finish phases must not
choose different gates when a room or feature is repeated.

The simplest standard policy is `Start -> Close` and `Timer -> Open`; copying
Plasma's `Timer -> AutoClose` is an optional release-hold behavior. Native signal
links are not universally one-to-one: a timer can have several sinks. A local
timed-cell policy may deliberately bind one gate or a declared gate set, but
should not reuse the artifact analyzer's native one-to-one semantics as a claim
about all devices. No flasher or prisoner-locker timer bindings were found in
these selected pairs, so none should be invented.

Only Plasma among these six samples contained `SignalTimer` entities. Thus timed
cells are a supported authored baseline or selectable cell subtype, **not a
universal rule observed on every station**. This pass does not establish a new
global Brig/Warden/Sentencing room ordering.

## Morgue and crematorium loading depends on orientation

The existing catalog reserves generic feature working area but does not specify
the actual loading/ejection position. Native `Morgue` and `Crematorium` both have
`EntityStorage.enteringOffset: 0,-1` in entity-local coordinates. This position
rotates with the machine; it cannot always mean the tile south of the room.

| Map / grid | Machine pose | Rotated loading position and sampled floor |
| --- | --- | --- |
| Gear / **12** | Morgues **4986**, **4988** at `(37.5,30.5)` and `(38.5,30.5)`, zero rotation | `(37.5,29.5)` and `(38.5,29.5)`, `FloorDarkMini` |
| Gear / **12** | Crematorium **4985** at `(39.5,30.5)`, zero rotation | `(39.5,29.5)`, `FloorDarkMini`, no co-located entity in sample |
| Relic / **2** | Crematorium **6678** at `(-5.5,17.5)`, +90 degrees | `(-4.5,17.5)`, `FloorSteel`, no co-located entity in sample |
| Relic / **2** | Morgue **459** at `(4.5,14.5)`, +90 degrees | `(5.5,14.5)`, `FloorSteel`; pipes/cables occupy the tile but are not automatically blocking machinery |
| Snowball / **1** | Morgue **10064**, native pose recorded in JSON | `(70.5,32.5)`, `FloorWhite` |
| Tram2 / **15** | Crematorium **10437**, native pose recorded in JSON | `(-62.5,-11.5)`, `FloorLino`, no co-located entity in sample |

The Gear row is a concrete common handling aisle for adjacent body stores and a
crematorium. Relic provides the rotated counterexample. Other samples emphasize
why a floor-name or empty-cell check alone is insufficient: Hike uses catwalks at
some loading positions, while a directional window occupies Plasma's sampled
crematorium loading tile. These were not collision-simulated; the precise native
shape and approach still have to be checked.

`SharedEntityStorageSystem.CanOpen` explicitly rejects an obstructed nonzero
entering offset for non-wallmounted storage. Its removal operation rotates that
offset and ejects the contained entity there. Therefore this is a functional
dependency, stronger than decorative facing or total room area.

**Suggested local addition:** on `medical/morgue#morgue_trays` and
`service/chapel/crematorium#crematorium`, require each selected instance's native
rotated loading position and authorized handling approach to remain usable.
A reusable local `x_native_loading_access` contract can name the local features,
the native offset field and all-instance coverage. Do not require every machine
to face the same compass direction or impose a universal fixed aisle length.
Do not count an inaccessible spare corner as the required loading area.

These machines do not establish a new mandatory vent, fuel or electrical circuit
in this checkout. `SharedCrematoriumSystem.TryCremate` requires closed storage
with contents and prevents opening during active cremation; it does not require
an Atmos burn-chamber hookup. The absence of such a native dependency is important
when distinguishing crematorium body handling from the TEG's process chamber.

## Avoid a false medical-scanner dependency

Gear's **10977** and Plasma's **3700/3897/4048** include `ComputerBodyScanner`, but
the native prototype is a base computer with its board, light and ambience marker.
It has no body-scanning or medical-scanner DeviceLink component. The separate
`MedicalScanner`/cloning-console system must not be inferred from that name.

The existing `medical/treatment#body_scanner` family should not be presented as
proof of a functioning patient scanner. A later functional equipment revision can
choose an actual health-analyzer workflow; this pass does not fabricate a missing
paired machine or a mandatory cloning suite from the placeholder computer.

## Recommended priority

The selected timed-cell's persisted control cycle and all selected body-storage
machines' native loading access are mandatory functional obligations. Choosing a
timed cell versus another staffed custody arrangement, copying an auto-close
hold behavior, exact device count and aisle shape remain authored policy. All
definitions belong in the room or its local suite. Upward propagation and global
layout consequences are deliberately left for the future algorithm.
