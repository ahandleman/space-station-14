# Native room relationships for StationArchitect3

The custody design recommendations below predate the requested shared-Brig and
Sentencing-locker baseline. The [current catalog policy](../CATALOG.md#configurable-choices-and-deliberate-policy)
also places Courtroom and Law Office independently. This changes the authored
program, not the native observations recorded in this report.

This research informs the independent v3 authoring format. It records physical relationships in existing maps; it does not import the old generator, define a layout, or prove a future plan geometrically feasible. The companion [evidence JSON](room-relationships.evidence.json) contains 14 cases, 156 selected native entity records, current source hashes, grid-local positions, default door-electronics access expressions, and sampled floor paths.

## How to read the evidence

A reference such as `Packed / grid 2 / UID 1626 / (35.5,-8.5)` identifies one entity in [Resources/Maps/packed.yml](../../../Resources/Maps/packed.yml). UIDs are scoped to that map. Positions are grid-local tile coordinates; integer floor cell `(35,-9)` contains a centered entity at `(35.5,-8.5)`. Rotation is the map's authored `Transform.rot` in radians. The records resolve inherited prototypes and map overrides, and apply current migration aliases.

The sampled maps are [Box](../../../Resources/Maps/box.yml), [Packed](../../../Resources/Maps/packed.yml), [Saltern](../../../Resources/Maps/saltern.yml), and [Bagel](../../../Resources/Maps/bagel.yml). Their main grids are respectively **8364, 2, 31, and 60**. The JSON stores each map's SHA-256 and each selected entity's effective prototype source. Source facts can be checked by finding the UID in its map, then following that prototype's parents and `ContainerFill` electronics.

Beacons establish local names, not ownership polygons. Closed-door floor components are a conservative audit partition: walls, windows, grilles and all door entities occupy entire blocked tiles. Thin directional windoors/windows, co-located doors, normally open curtains and firelocks make this deliberately unsuitable as runtime pathfinding or a room-count oracle. Sample customer paths additionally block tables, but do not simulate movable objects, other machinery, actor collision or door operation. No game server, atmospherics or interaction simulation was run.

## HoP office and the public HoP line

Three maps repeat a useful service assembly:

| Map / grid | Office landmark | Shared service boundary | Separate staff walking entries |
| --- | --- | --- | --- |
| Box / 8364 | HoP beacon **27722**, `(-10.5,-25.5)` | Reinforced table **5585**, HoP-locked windoor **6248**, and opposed unlocked windoor **6249** named **Reception Window**, all at `(-12.5,-22.5)` | HoP airlocks **5647** at `(-7.5,-20.5)` and **5648** at `(-7.5,-28.5)` |
| Packed / 2 | HoP beacon **13131**, `(17.5,-4.5)` | Reinforced table **564**, HoP windoor **10272**, and opposed unlocked windoor **563**, all at `(16.5,-2.5)` | HoP airlock **222** at `(13.5,-3.5)`; HoP maintenance airlock **577** at `(14.5,-6.5)` |
| Bagel / 60 | HoP beacon **21779**, `(5.5,-28.5)` | Reinforced table **1339**, HoP windoor **1340**, and opposed unlocked windoor **1341**, all at `(7.5,-26.5)` | HoP airlocks **1374** at `(2.5,-29.5)` and **3500** at `(6.5,-31.5)` |

Box's two windoors face opposite directions, as do Packed's and Bagel's pairs. Default `DoorElectronicsHeadOfPersonnel` supplies `[[HeadOfPersonnel]]` on the staff-controlled leaf. The unlocked leaf defaults to `DoorElectronics`. Directional windows also bound waiting/queue space in Packed and Bagel. There is **no need to invent a native beacon literally named HoP Line**: that is a useful explicit v3 commons-room role supported by the observed service geometry.

**V3 policy:** require HoP office and HoP line to share a usable counter/service-window interface, with ordinary crew access from public circulation to the line. Require staff walking access to the office separately. The service interface needs usable positions on its two sides; placing a windoor somewhere in the office or connecting the office to the line by a corridor does not establish this relationship. Reinforced tables are climbable in native gameplay, so a service counter's omission of routine `crew_passage` capability must not be described as absolute physical impossibility to climb or breach it.

## Kitchen and bar serving a common customer room

**Packed provides a particularly clear joint layout.** Bar beacon **11974** is at `(32.5,-3.5)` and Kitchen beacon **13134** at `(35.5,-9.5)`, grid 2.

- Kitchen reinforced counters **5813 / 1626 / 1618** occupy `(34.5,-8.5)`, `(35.5,-8.5)`, `(36.5,-8.5)`. The kitchen working side is south; customer floor is north.
- Bar reinforced counters **572 / 1651 / 1659 / 1598** occupy `x=37.5`, `y=-7.5,-6.5,-5.5,-4.5`. The bar working side is east; customer floor is west. Bar stools **1627** and **1615** help identify the customer side.
- The JSON records an actual four-neighbor customer-floor path `(35,-8) -> (36,-8) -> (36,-7) -> (36,-6)` between these fronts, with walls, closed-door tiles and tables excluded. This is one common seating/customer floor.
- Staff door **562** at `(33.5,-8.5)` uses `[[Kitchen]]`; bar staff door **2132** at `(37.5,-3.5)` uses `[[Bar]]`. The shared customer room does not make the staff workspaces public.

**Saltern repeats the shared-room pattern with a different shape.** Kitchen beacon **11318** is at `(-12.5,-0.5)` and Bar beacon **11319** at `(-5.5,-5.5)`, grid 31. Kitchen counters **900 / 4245 / 901** run at `x=-10.5`, `y=-0.5,0.5,1.5`, facing east toward stools **9921 / 9920 / 9877**. Bar wood counters **4061 / 936 / 4063 / 2251 / 2250** turn along the north/east edges of the bar workspace. A sampled floor path connects customer cell `(-10,0)` to `(-7,-4)` without crossing a table or door. Kitchen staff airlock **3219** at `(-10.5,-1.5)` and bar staff airlock **2272** at `(-8.5,-6.5)` retain separate access.

**Box demonstrates why this should be an authored requirement rather than a universal claim about native maps.** Kitchen's Desk windoor **10451** shares reinforced table **7271** at `(32.5,-7.5)` and faces the bar's staff workspace. The main bar counter is at `x=28.5`; customer seating is west. Kitchen also has a south counter, tables **7234 / 7166 / 7058 / 7144** at `y=-10.5`, `x=35.5..38.5`, facing a public hall. Unlocked airlocks **6998 / 6999** at `(22.5,-11.5)` and `(24.5,-11.5)` connect the bar customer room to that hall. This is a staff-transfer edge plus a separate customer-facing edge, with door-connected public spaces.

**V3 policy requested for this station:** give `BarCommons` its own physical room and require **both** Kitchen and BarWorkspace to share serving boundaries with the **same instantiated** BarCommons. Two independent existential edges could accidentally choose different commons instances; binding to the same containing service complex or an explicit common instance is necessary. A beacon named Bar often lies in customer seating, so it must not be used to conflate bar workspace, customer commons and department ownership.

## Security: custody, staff circulation and stronger internal gates

Native layouts support several access layers and deliberate interaction boundaries:

| Evidence | Actual edge | Implication for authoring |
| --- | --- | --- |
| Box / grid 8364 | Security Entrance airlocks **7759 / 8907**, `(-1.5,34.5)` / `(0.5,34.5)`, default `[[Brig]]` | Public approach and controlled staff/custody access are separate roles. |
| Box | Warden airlocks **8676 / 8932**, `(-5.5,35.5)` / `(-8.5,30.5)`, default `[[Armory]]` | A room's department does not determine its strongest credential. |
| Box | Red Armory high-security door **28705**, `(-11.5,44.5)`, `[[Armory]]`; opposed plasma windoors **28503 / 26285** at `(-13.5,44.5)` use Armory / Security boards | A secure room can have a walking gate and a separately controlled interaction edge. Co-located leaves retain individual policies. |
| Packed / grid 2 | Security-colored outer door **156**, `(28.5,27.5)`, default unrestricted; Brig door **265**, `(31.5,27.5)`, leads from that anteroom into the Security-beacon compartment | Color, prototype family and nearby beacon are insufficient to infer access. |
| Packed | Armory beacon **15787**, `(41.5,34.5)`; internal airlock **8550**, `(40.5,32.5)`, uses `[[Security]]`; high-security door **19**, `(42.5,34.5)`, uses `[[Armory]]` | Do not infer a universal Armory-only rule from the room name. A stricter v3 policy can still require it explicitly. |
| Saltern / grid 31 | Armory beacon **10088**, `(-12.5,19.5)`; door **573**, `(-10.5,20.5)`, borders the HOS-labeled compartment and uses `[[HeadOfSecurity]]` | Access through a head's office is one observed suite pattern. |
| Saltern | Warden **762**, `(-1.5,8.5)`, connects to Security by door **1411**, `(0.5,9.5)`, `[[Security]]`; Perma **11316**, `(-17.5,9.5)`, connects through door **7786**, `(-14.5,10.5)`, also `[[Security]]` | Custody and office rooms require individual entry rules and routes; department grouping supplies neither. |

Recommended authored constraints are a controlled custody route into Cells/Perma, normal Security staff circulation reaching offices and equipment rooms, explicit entry allow-lists for high-security rooms, and no unrelated public through-route through custody or armory floor. Public reception/processing, interview rooms and secure cells need distinct policies even when grouped under one department. Neither a globally fixed separation distance from Command nor a fixed room-to-room ordering follows from this evidence. Exact arrest processing, sentencing, evacuation and prisoner escape policies need explicit design decisions.

## Command: direct suite edges, distributed ownership and secondary exits

Box's Bridge beacon **27707** at `(-1.5,-7.5)` directly borders the Captain office through Captain-locked airlock **5693** at `(6.5,-10.5)`. The Captain beacon is **27711** at `(9.5,-15.5)`. Captain room door **5692** at `(7.5,-19.5)` and Captain maintenance door **2380** at `(12.5,-19.5)` are additional Captain-locked edges. Briefing door **5619** at `(-7.5,-10.5)` borders the Bridge and uses Command access.

Packed repeats a direct Bridge/Captain edge: Bridge **12106**, `(26.5,45.5)`; Captain **12385**, `(31.5,40.5)`; Captain airlock **2616**, `(32.5,44.5)`. Bridge doors **2758 / 2759** at `(26.5,43.5)` / `(28.5,43.5)` use Command access, whereas **7766 / 7767** at `(22.5,43.5)` / `(23.5,43.5)` are unrestricted doors onto another local compartment. That is not evidence that the entire station public can reach Bridge: the neighboring compartment's upstream gates also matter.

The HoP landmarks are far from these Bridge landmarks: Box `(-10.5,-25.5)` and Packed `(17.5,-4.5)`. Logical Command ownership therefore need not force every office into one contiguous enclosed department.

**V3 policy:** require secure access to Bridge, the Captain office and other command functions at the appropriate credential level; direct Bridge/Captain and Bridge/Meeting edges are supported layout preferences that may be promoted to mandatory author policy. A contained suite may share the outer hull. Internal-only access through a parent, direct adjacency to the parent and optional controlled maintenance exits are independent requirements. Box supplies a counterexample to silently assigning every command child an absolute no-secondary-entry rule.

## Medical: patient frontage and controlled specialized spaces

Box's Chemistry beacon **27715** is `(20.5,-19.5)` and Medbay beacon **27727** `(30.5,-17.5)`, grid 8364. Chemistry Desk windoors **1758 / 2563** at `(23.5,-17.5)` / `(23.5,-19.5)` share the boundary between their coarse floor compartments. Chemistry staff airlock **1757**, `(19.5,-23.5)`, uses `[[Chemistry]]`. Medical door **2505**, `(25.5,-21.5)`, connects Medbay to another medical circulation compartment and uses `[[Medical]]`. CMO doors **19284 / 20392** at `(37.5,-44.5)` / `(38.5,-39.5)` use `[[ChiefMedicalOfficer]]`.

Packed's Medical beacon **15914** at `(51.5,3.5)` sits in a small reception-like compartment; Medical windoors **12159 / 12237 / 14991** run at `y=1.5`, `x=51.5,50.5,49.5` toward adjacent floor. Staff door **5012**, `(48.5,3.5)`, uses Medical access. Surgery beacon **15920**, `(65.5,-5.5)`, has Medical doors **1146** at `(63.5,-5.5)` and **850** at `(66.5,-7.5)`. CMO door **5039**, `(56.5,-8.5)`, borders the Chem-labeled compartment in this particular map. A CMO-via-Chemistry ordering is therefore possible, not a universal medical-suite rule.

Saltern explicitly has separate Medical **1215**, `(9.5,-2.5)`, and Medbay **5767**, `(9.5,-9.5)`, compartments, connected by Medical doors **44 / 124** at `y=-6.5`. Chemistry counter windoors **5086 / 9227** share `(13.5,-0.5)` on the Medical/Chemistry boundary; staff Chemistry door **2128**, `(17.5,-3.5)`, is separate.

Morgue is an important cross-department exception. Box morgue door **18321**, `(44.5,-15.5)`, and Packed doors **1025 / 11848**, `(72.5,0.5)` / `(71.5,-6.5)`, use `DoorElectronicsMorgue`, whose access is **`[[Medical], [Detective], [Chapel]]`**. Saltern uses Medical-only doors **4146 / 4906** on its morgue. The schema must preserve this authored OR expression and allow differing policies for the same room role.

**V3 policy:** patient arrival/treatment routes, a pharmacy/chemistry service edge to an intended public-or-clinical recipient room, staff routes to Surgery/Cryogenics/Morgue, and separate specialized access are useful requirements. Routine public transit through Surgery, CMO office or drug storage should require explicit permission. None of the map samples proves that every Medical room must be a contained child of one physically enclosed parent, or that a patient must traverse one fixed sequence of rooms.

## Suggested explicit edge list for the new catalog

These are proposed semantic roles, **not final canonical paths**. Catalog authoring should map them to its actual paths. Mandatory means the selected v3 design must satisfy the rule; map examples alone do not make a rule mandatory. Optional sources impose edges only when selected. Targets that support mandatory sources must be present, or an explicit allowed alternative must be selected.

| Suggested rule | Type and subject | Basis / recommended priority |
| --- | --- | --- |
| HoP service | HoPOffice -> HoPLine shared counter/window boundary, usable from both sides | User requirement / mandatory |
| HoP line approach | PublicCirculation -> HoPLine crew route with public credentials | Supports the public service function / mandatory design policy |
| HoP staff approach | Authorized staff -> HoPOffice crew route independent of its counter | Native door roles + design policy / mandatory |
| Kitchen service | Kitchen -> the bound BarCommons shared serving boundary | Explicit user policy, Packed/Saltern pattern / mandatory |
| Bar service | BarWorkspace -> the **same** BarCommons instance shared serving boundary | Explicit user policy, Packed/Saltern pattern / mandatory |
| Customer approach | PublicCirculation -> BarCommons public crew route; public through-access excluded from staff workspaces | Supports the public service function / mandatory design policy |
| Service staff approach | StaffCirculation -> Kitchen and BarWorkspace separate authorized crew routes | Native access pattern / mandatory design policy |
| Security reception | PublicCirculation -> SecurityReception public route; reception -> secure circulation through a controlled staff/custody gate | Design policy supported by Packed anteroom / mandatory if reception selected |
| Custody access | Processing/Sentencing -> Cells/Perma custody route; allowed entries and no unrelated public transit explicitly restricted | Design policy; exact room ordering is not inferred / mandatory when selected |
| Security internal access | SecurityCirculation -> Armory/Warden/HOS crew routes, each with authored credentials and allowed entry sources | Map pattern + security design policy / mandatory when selected |
| Armory control | Armory entry allow-list and no public through-traffic; optional separately controlled issue counter to intended recipient room | Security design policy / mandatory access; counter desired unless explicitly required |
| Bridge approach | CommandCirculation -> Bridge with Command credentials, no unrelated public transit | Security design policy / mandatory |
| Captain approach | Bridge/CommandCirculation -> CaptainOffice with Captain credentials | Map pattern + secure access policy / mandatory route; **direct shared door desired** unless explicitly required |
| Meeting approach | Bridge/CommandCirculation -> MeetingRoom with intended authorized access | Map pattern / desired direct adjacency; mandatory usable access when selected |
| Medical patient approach | PublicCirculation -> MedicalReception/Treatment public patient route | Patient service policy / mandatory |
| Pharmacy service | Chemistry/Pharmacy -> intended recipient room shared service counter/window; separate staff door | Map pattern / mandatory when pharmaceutical service is selected |
| Medical internal access | MedicalCirculation -> Surgery, Cryogenics, Morgue and CMO, with their actual access profiles and permitted secondary entries | Map pattern + patient/staff policy / mandatory routes when selected |
| Morgue cross-role access | Explicit allowed Medical/Detective/Chapel profiles where that policy is selected | Native option / desired default or explicit policy, not universal |
| Contained child approach | A contained room -> its **own parent instance** crew access, with `mustTraverse` and entry allow-list if internal-only | Explicit author policy / mandatory for that child; containment alone is insufficient |

No row implies that an interaction window is a routine walking door, that a routing edge proves shared border, that shared departmental ownership proves accessible circulation, or that two distinct room instances can satisfy a same-parent relationship interchangeably. Specify minimum serving run, number of separate segments, physical interface capability and access policy together. A connection label alone is insufficient.

## Access semantics and classification

Native [AccessReaderComponent](../../../Content.Shared/Access/Components/AccessReaderComponent.cs) and [AccessReaderSystem](../../../Content.Shared/Access/Systems/AccessReaderSystem.cs) define access groups as **OR across sets and AND within a set**. `[[Medical], [Detective], [Chapel]]` admits any one of those roles; `[[Medical, Detective]]` requires both. An empty access list grants ordinary access while the reader is enabled and no denial applies. When `containerAccessProvider` is set, the contained reader supplies policy and the outer entity's own access list is ignored. Default electronics live in [door_access.yml](../../../Resources/Prototypes/Entities/Objects/Devices/Electronics/door_access.yml).

Keep four bases distinct in v3 evidence:

- **User requirement:** a requested relation such as the HoP line or both food workspaces serving one BarCommons. This can legitimately be stricter than sampled maps.
- **Native mechanic:** actual door-board access semantics, rotated edge hardware, physical interaction capabilities and native collisions. These constrain how an authored relationship can be realized.
- **Map pattern:** repeated direct Captain/Bridge edges or serving-counter layouts. These support defaults, without proving a universal minimum size, entry ordering or department distance.
- **Design policy:** no public through-traffic through Armory, internal-only child entry, secure custody routing or a selected department-separation distance. State it explicitly and validate every relevant path; a high weight does not turn it into a native fact.

A future geometric consumer will need directed edge geometry, approach/working clearance, room and parent-instance identity, per-profile traversal, and whole-graph bypass checks for `mustTraverse`/entry restrictions. The current v3 authoring deliverable should describe those obligations symbolically and clearly report that no physical layout has yet been generated or validated.

## Independent catalog source audit

Run the standalone audit from the repository root after catalog edits:

```powershell
python -B Tools/StationArchitect3/research/audit_sources.py --output Tools/StationArchitect3/research/source-audit.json
```

The [audit](audit_sources.py) imports no earlier tool. It safely reads native YAML tags as inert data, inventories actual entity IDs including `CreateVariants` IDs, checks every declared interface/feature/assembly prototype, checks native access-level references while identifying `public` as a planning marker, verifies map UIDs and source files, and records their SHA-256 hashes. It also checks the map/native-source hashes stored in the research JSON. A tile prototype such as `Lattice` cannot pass as an entity prototype. Its [JSON report](source-audit.json) is tied to the audited catalog hash. These checks establish source identity and reference existence; interface capability, physical access, geometry and operating behavior remain separate proof obligations.
