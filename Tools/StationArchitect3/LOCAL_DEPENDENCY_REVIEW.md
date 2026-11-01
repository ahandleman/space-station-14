# Third pass: relationships found in mapped installations

This report records an earlier research and authoring pass. Its native map
observations remain evidence; its then-current catalog counts and timed-cell
recommendation are historical. The [current custody policy](CATALOG.md#configurable-choices-and-deliberate-policy)
uses Sentencing lockers and a shared Brig, with independent Courtroom and Law
Office scopes. It does not select individual timed cells.

This pass inspected **Hike, Gear, Plasma, Relic, Snowball and Tram2**, looking at
saved entity arrangements and actual device links before checking the native
mechanics. That pass added **14 local relationships and 17 feature families**.
At that stage the catalog contained 139 spaces, 211 relationships (202 mandatory,
nine desired) and 147 feature families. Its new node requirements described local
operation and clearance.

## What the maps revealed

| Missing dependency | Concrete map evidence | Definition added |
| --- | --- | --- |
| A timed cell needs its own controlled gate. | Plasma timer **5396** links Start -> Close and Timer -> Open to gate **8661**; **14643** controls a different gate **8660**. | Historical addition, now superseded: `security/cell` owned a timer, gate, both signal phases and the requirement that they operate its existing Brig boundary, with controls reachable from outside custody. |
| Morgue trays and crematoria need clearance in their opening direction. | Gear morgues **4986/4988** and crematorium **4985** load toward their southern aisle. Relic crematorium **6678** is rotated east. | Each machine must have usable approach and opening/ejection space at its rotated native `EntityStorage.enteringOffset`. Spare area elsewhere does not satisfy it. |
| A PA console needs the rest of the accelerator and a beam approach. | Snowball control **8894** accompanies six other parts; Tram2 **19423** has the same complete footprint. Parts are often unfinished. | The PA room owns seven part roles, reserved native assembly geometry, construction/commissioning capability and separate APC/MV feeds. The selected engine locally requires a compatible particle approach. |
| An AME controller needs shielding, fuel and a buildable core. | Gear controller **9418** sits with fuel crate **9424** and shielding crates **9416/9422**. Snowball and Tram2 repeat this kit arrangement. | Local shielding/fuel inventory, a deployable connected core, controller adjacency, fuel-slot access and actual HV export. A crate itself is not a running core. |
| Containment needs active fields, emitter lanes and the correct collector type. | Tram2 fields **7850/7851/7852/7858** have aligned emitters, including **5100**. Snowball includes Tesla coils and grounding rods, with some machinery staged nearby. | Local emitter MV supplies, bolt lanes, field links and generator-specific collection. Tesla and singularity support are conditional alternatives, not random interchangeable furniture. |
| Room access does not necessarily grant AI upload-console access. | Gear upload **7745**, Snowball **3630** and Tram2 **5374** retain native ResearchDirector access. | The upload room declares an RD-capable approach and the console's local operator/input requirements. No fictitious physical upload cable is added. |
| Cargo receiving pads need approving-console links. | Gear consoles **2479/3967** share telepad **16597**; Plasma and Tram2 repeat shared-pad arrangements. | A selected optional pad locally requires an eligible console, saved OrderSender -> OrderReceiver association, power, anchoring and receiving/retrieval clearance. It remains optional alongside shuttle delivery. |
| A disposal unit needs an entry directly underneath it. | Gear unit **1540** and trunk **30502** share `(-10.5,2.5)`; Plasma **696/6371** and Tram2 **764/9296** repeat it. | A same-tile intake assembly and a named local terminal, with directional/tag-compatible routing and intentional discharge/service access. |
| Burn-chamber igniters need actual remote controls. | Plasma button **6068**, on the control-room side, has saved Pressed -> Trigger links to five chamber igniters, including **27024**. | The TEG suite owns a fan-out signal link; the generator room owns the safe operating approach. The chamber also declares each injector's connection to its fuel header. |

## Policy and initial state

Functional obligations are mandatory when their local facility is selected.
The then-selected timed-cell archetype was an explicit policy supported by Plasma, not a claim
that every station has timers. No universal flasher, locker link, AutoClose
behavior or fixed cage size was inferred from the examples.

PA, AME and containment setup explicitly distinguish `player_setup` from
`commissioned`. Existing maps provide unfinished or packed machinery. The former
mode requires available parts and a realizable completed footprint; it does not
require finished, firing machinery at round start. Target assemblies reuse the
declared part roles and kit contents, rather than spawning duplicate equipment.

The cargo pad does not become mandatory because an orders console exists. Its
obligation starts at the optional receiving pad, and multiple consoles may share
one pad. This differs from the artifact lab's exclusive analyzer pairing.

Disposal payload tag `waste` is native configuration on the chosen DisposalUnit,
not an inference from the service-medium name. Untagged variants can explicitly
use an empty tag list. The terminal's recoverable receiving-floor policy is
authored: existing maps also show hazardous or recycler-facing discharge paths.

No extra dependency was invented for the placeholder `ComputerBodyScanner`,
for shared Kitchen/Botany freezer access observed in only one arrangement, or
from a Theater beacon labeling a broader service/customer area. Those cases
need stronger evidence or an explicit alternative archetype.

## Locality and verification limits

Every added relationship is inside its room or functional suite, with relative
references. There is no new global relationship list, network pass or upward
propagation algorithm. Shared assembly/evaluator definitions are reusable
vocabulary; their use is required by local declarations.

The validator checks the authored structure and references. Typed operating,
construction, beam, access and clearance contracts still need future evaluators.
Map positions and saved links do not certify native commissioning or a generated
layout. Detailed records and counterexamples are in
[custody/body handling](research/local-custody-medical.md),
[engine machines](research/local-engine-machines.md),
[logistics](research/local-logistics-service.md), and
[thermal controls](research/local-thermal-controls.md).
