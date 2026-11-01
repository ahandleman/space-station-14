# Mapped local thermal controls

The third pass inspected Hike, Gear, Plasma, Relic, Snowball and Tram2. This
specific addition follows **Plasma's saved ignition links**, not a guess that an
igniter item automatically starts a burn chamber. The [evidence JSON](local-thermal-controls.evidence.json)
records the selected map entities, the saved source/sink associations and hashes.

On Plasma grid **2**, `SignalButtonDirectional` **6068** at `(-145.5,-1.5)` is
named “signal button (Ignite Chamber)”. Its saved `DeviceLinkSource.linkedPorts`
connects **Pressed -> Trigger** to igniters **27024, 27025, 27023, 5982 and 6071**.
Those igniters occupy the hot apparatus around `(-147.5,2.5)`. Heat exchangers
**6018/6038/6019** run at x `-147.5`, y `1.5/2.5/3.5`; injector **6058** is at
`(-146.5,2.5)`. TEG center **674** is farther south at `(-147.5,-7.5)`.

Inspection of the native floor/wall/window neighborhood puts the control at the
working-room boundary south of the separate hot enclosure. The chamber's front
window row is at y `0`; the control-room boundary is at y `-2`. This supports an
operator position outside the hot enclosure. It is not a native collision,
atmospheric safety or control-range certification.

The native signal button exports `Pressed`; the igniter exposes `Trigger` and
uses `TriggerOnSignal`/`IgniteOnTrigger` with an 800 K ignition source. It has no
gas port. A configured link is therefore a real functional relationship missing
from the previous catalog. The GasOutletInjector separately has a native gas
node `pipe`: the abstract fuel header must actually feed that input.

The authored TEG suite now owns a button-to-igniter **fan-out** edge, while the
generator room owns the safe operating-approach requirement. The burn chamber
owns its injector's incoming fuel obligation. All remain conditional on the
selected local TEG instance. This does not auto-start combustion or simulate
fuel chemistry, nor does it substitute for the existing hot/cold-loop contracts.

Plasma has another button **27016** linked to five other igniters near a separate
gas apparatus. This pass does not label that apparatus a second TEG. Relic has
igniter entities but no matching saved button links in this raw scan; an item
count alone was not treated as evidence of a configured control circuit.

The room-layout screen also found direct Captain/Bridge access already covered
by the catalog. Plasma's Theater beacon labels a larger customer/service area
bordering several functions; that label alone does not establish a new mandatory
Theatre/Kitchen adjacency. No extra relationship was invented from those names.
