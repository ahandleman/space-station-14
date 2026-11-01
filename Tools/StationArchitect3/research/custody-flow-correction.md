# Genpop custody: current native mechanics and authoring correction

The current design is the user's requested flow: Sentencing contains prisoner-property lockers that time sentences; separate Brig entrance and exit turnstiles connect Sentencing and Brig; Sentencing also connects Perma. Individual timed cells are absent by default. Courtroom and Law Office are independent placements. The earlier Plasma timed-cell evidence remains a valid observation about that map, but does not prescribe this generator's default custody design.

## Native mechanism

`LockerPrisoner` and numbered variants inherit `GenpopLocker` from `GenpopBase`. The numbered variants change door artwork, not their timing or pairing semantics. `LockerWallBasePrisoner` and its numbered variants provide the corresponding wall-mounted implementation. The access reader requires Security **for the configuration UI**; `Lock.useAccess` is false because the genpop system handles property-locker access itself.

The native sequence is:

1. An authorized operator configures a prisoner's name, crime and sentence through `GenpopLockerIdConfiguredMessage`. The shared system locks and closes the locker, then the server spawns a `PrisonerIDCard` and stores that exact entity as this locker's `LinkedId`.
2. The spawned card initially grants `GenpopEnter`. For a positive sentence, `GenpopSystem.CreateId` sets expiration to current time plus the requested number of minutes. A zero sentence leaves the expiration unset and is permanent.
3. `SharedIdCardSystem.Update` handles expiration, accounting for paused time. `ExpireId` **replaces** the card's access with `GenpopLeave`; it does not broadcast an Open/Close signal to a gate. The server makes the card announce its expiration.
4. Property retrieval requires the actor to carry this specific locker's linked ID and that ID to be expired. Unlocking resets and opens the locker. Authorized verbs can end a sentence early, clear a locker, or reset an unexpired sentence.

Consequently, repeated lockers have independent runtime ID pairings. Their prisoners share the same access-controlled gate roles. There is no required static locker-to-gate pairing, shared `SignalTimer`, or `DeviceLinkSource`/`DeviceLinkSink` wiring in this native genpop mechanism. Native execution of the UI, expiration and property retrieval remains a runtime obligation; spatial tests alone cannot certify it.

Sources:

- `Resources/Prototypes/Entities/Structures/Storage/Closets/Lockers/lockers.yml` (`GenpopBase`, `LockerPrisoner`)
- `Resources/Prototypes/Entities/Structures/Storage/Closets/wall_lockers.yml`
- `Resources/Prototypes/Entities/Objects/Misc/identification_cards.yml` (`PrisonerIDCard`)
- `Content.Shared/Security/Components/GenpopLockerComponent.cs`
- `Content.Shared/Security/Systems/SharedGenpopSystem.cs`
- `Content.Server/Security/GenpopSystem.cs`
- `Content.Shared/Access/Components/ExpireIdCardComponent.cs`
- `Content.Shared/Access/Systems/SharedIdCardSystem.cs`
- `Content.Server/Access/Systems/IdCardSystem.cs`

## Gate geometry and access

The actual prototypes are `EnergyGateGenpopEnter` and `EnergyGateGenpopLeave`. They inherit `Turnstile`, **not** `Door`. Their native hard fixture occupies one tile, with a separate non-hard sensor fixture. Their respective access clauses are `GenpopEnter OR Security` and `GenpopLeave OR Security`. Neither native genpop variant is airtight; the separate `EnergyGateAirtight` prototype has that component.

`SharedTurnstileSystem.CanPassDirection` compares the actor's approach vector against the gate's world-facing vector with a strict 45-degree tolerance. Rotation zero travels south; the quarter-turn mapping is:

| Rotation | Allowed travel |
| ---: | --- |
| 0 | South `(0, -1)` |
| 90 | East `(1, 0)` |
| 180 | North `(0, 1)` |
| 270 | West `(-1, 0)` |

Security credentials do not reverse a one-way gate. The entry and release lanes need their own correctly oriented interfaces and actor-specific paths. Native collision exceptions also account for an admitted actor's pulled entity and pull chains; those details are not a certificate of simulated anti-tailgating behavior. Entities outside the native `processWhitelist` bypass its processing.

Gate placement must bind the selected native prototype to its intended interface and independently check the actual rotation. Treating a turnstile as ordinary floor machinery blocks the lane; treating it as a freely reversible door falsely certifies custody routes. Wall-mounted lockers additionally eject the ID one tile along their mounted facing and need a usable floor there.

The fixture adapter now applies the configurable `Turnstile` classification in `data/fixture-footprints.yaml`. It requires the selected prototype in the realized profile's `construction` list and derives the native rotation from `passage.direction` and the actual interface normal. An absent or bidirectional declaration cannot accept a directional gate. Saved poses are independently reconstructed, so changing both a gate's rotation and its serialized part poses does not conceal reversed movement. Local loading/control access and EVA paths use the verifier's directed traversal, including ingress-seed checks; a release-only gate cannot manufacture an entrance behind itself.

`x_genpop_custody::spatial` is a partial geometry check. It verifies the selected entry/exit fixtures on the declared boundary, native card and gate credential phases, admission/release paths, absence of unserved escape or released re-entry, and operating access for every locker part from staff and released-prisoner approaches. The original `x_genpop_custody` remains pending for native operation. In particular, geometry does not prove runtime ID creation/pairing, sentence expiration, zero-duration permanence or property-locker retrieval. The native-fact tests verify the source declarations; they do not replace a native runtime integration test.

Sources: `Resources/Prototypes/Entities/Structures/Doors/EnergyGates/energy-gate.yml`, `Content.Shared/Doors/Systems/SharedTurnstileSystem.cs`, `Content.Shared/Doors/Components/TurnstileComponent.cs`, and `RobustToolbox/Robust.Shared.Maths/Angle.cs` (`ToWorldVec`).

## Exact mapped examples

All positions below are native grid-local coordinates. These observations identify actual entities; nearby coordinates do not assign named room ownership.

| Map / grid | Entry gate | Release gate | Nearby prisoner locker |
| --- | --- | --- | --- |
| Box / 8364 | UID8703 `(-16.5,27.5)`,270deg | UID771 `(-16.5,29.5)`,90deg | UID9058 `(-15.5,30.5)` |
| Packed / 2 | UID2312 `(33.5,22.5)`,0deg | UID3179 `(30.5,22.5)`,180deg | UID10454 `(29.5,25.5)` |
| Bagel / 60 | UID13624 `(-17.5,-16.5)`,0deg | UID13631 `(-19.5,-16.5)`,180deg | UID2166 `(-20.5,-11.5)` |
| Plasma / 2 | UID159 `(-89.5,-42.5)`,90deg | UID3173 `(-89.5,-44.5)`,270deg | UID2360 `(-90.5,-38.5)` |

Box, Bagel and Plasma each contain eight mapped prisoner lockers; Packed contains four. Each of these four maps has one genpop entry gate and two leave gates. This is evidence of native arrangements, not a requirement to force exactly three gates in every generated installation. Saltern has no matching genpop gates/lockers in this snapshot, demonstrating that map variation does not override the current requested design.

The additional leave gates are Box UID8739 `(-6.5,27.5)`, Packed UID3212 `(28.5,24.5)`, Bagel UID8271 `(-21.5,-14.5)` and Plasma UID20806 `(-97.5,-43.5)`. Their separation from the paired entry/leave frontage near the lockers suggests an additional release boundary; that is a layout inference, not inferred room ownership. Independently of gate count, the generated contract needs a route for a released prisoner from Brig to their property locker and onward to public circulation. A Security-only sentencing front door would leave a prisoner with only `GenpopLeave` credentials trapped after retrieving belongings. The outward public release can use an appropriate directed gate or another explicitly authored access policy.

The complete UID inventory, inherited component facts and source hashes are in [custody-flow-correction.json](custody-flow-correction.json). Regenerate it with `python Tools/StationArchitect3/research/custody-flow-audit.py`.
