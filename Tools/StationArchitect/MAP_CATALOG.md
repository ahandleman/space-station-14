# Map-informed station catalog

The starting program is now data in [Catalog/standard-station.yaml](Catalog/standard-station.yaml).
`StationProgramDefaults` reads that embedded YAML and returns a fresh editable
document. Adding departments, rooms, equipment families, connections or power
choices does not require editing a department-name switch in C#.

The catalog contains **10 authored owners, 111 departmental room templates and
six true-maintenance/exterior templates**. Its preferred owned floor total is
4,750 tiles; public halls and maintenance passages are additional floor.
Template counts are not a census of rooms on any particular source map.

## Sources and method

The comparison covers the ten entries in the local
[DefaultMapPool](../../Resources/Prototypes/Maps/Pools/default.yml): Bagel, Box,
Elkridge, Exo, Marathon, Packed, Plasma, Snowball, Sushi and Tram2. Saltern, Hike,
Relic and Gear provide four additional comparisons. Pool membership and map
contents are those of this repository checkout, not a claim about every SS14
server or future upstream versions.

The reproducible extractor, [Analysis/analyze_maps.py](Analysis/analyze_maps.py),
resolves navigation-beacon prototype defaults and map overrides, records actual
entity IDs and positions, reads job slots, counts explicit mapped system
prototypes, and estimates closed-door floor components. Its output is
[Analysis/map-evidence.json](Analysis/map-evidence.json), with input SHA-256 hashes
and per-map observations. [Analysis/room-marker-frequency.csv](Analysis/room-marker-frequency.csv)
is a compact marker summary. The separate catalog audit script can check that
catalog equipment prototypes occur in the maps and that `#uid=…` references resolve.

`sourceRefs` on rooms and departments point to repository map paths and actual
entity UIDs, for example `Resources/Maps/packed.yml#uid=…`. A reference may denote
a room beacon, named air alarm, holopad, or corroborating machine. It establishes
where the room vocabulary or function came from; it is not a polygon boundary
or proof that every room in that template family has the same shape.

## What the maps establish

Across the ten default-pool maps, the extractor finds actual mapped CryoPod,
OperatingTable, Morgue, ChemDispenser, research-server, artifact/anomaly machinery,
station-AI hardware, gravity machinery and solar panels on every station's main
grid. Named destinations repeatedly include the department heads' offices,
janitor, dorms, crew cryosleep, EVA storage, general tools, disposals and vault.
The old generic catalog omitted several of those functional spaces.

Some distinctions matter:

| Observation | Catalog decision |
| --- | --- |
| Medical cryopods and crew cryosleep both occur throughout the sample. | `Medical/Cryogenics` and stationwide `Commons/Cryosleep` are different rooms with different machines. Cryosleep is independent of Arrivals. |
| Chapel door, booth and crematorium geometry occurs in Bagel, Box, Packed and Saltern. | `Service/Chapel` contains separately bounded `Confessional` and `Crematorium` child rooms, with per-parent counts and configurable secondary access. |
| Operating tables occur on all ten maps; only seven have a Surgery navigation label. | Keep a required surgery-capable room, but do not infer its instance count from marker count. |
| No explicit `CloningPod` is mapped in the sampled stations. | Do not introduce cloning as a standard mandatory facility merely because a prototype exists. |
| TEG centers and AME controllers occur on all ten default maps; Tesla and singularity generator hardware also appear frequently. | Permit combined power programs. A cardinality choice selects one or more TEG/AME/containment room families; the catalog does not treat these as universally exclusive engines. |
| All six department-head offices recur as named destinations. | Put CE, CMO, RD, HOS and QM offices in their own departments; the HoP and Captain remain in Command. |
| AI core and upload/support spaces form a distinct cluster. | Add the data-defined `Silicon` owner. This ownership is an authoring decision, not an inferred game department or hard-coded solver role. |
| Public serving/order surfaces coexist with restricted workspaces. | Model counters/windows as interaction interfaces, separately from walking connections. |
| Solar equipment lives outside the normal pressurized station footprint. | Separate interior solar control rooms from vacuum, exterior, unsealed solar-array footprints. |

Mapped machinery can be inactive, unfinished, stocked for later construction,
or duplicated in a shared chamber. Its presence does not establish that a safe,
working power system is running. For example, the containment-room equipment
family allows singularity or Tesla hardware without claiming that each source
station operates both at once.

## Room program

| Owner | Room types | Preferred floor | Principal additions or corrections |
| --- | ---: | ---: | --- |
| Arrivals | 5 | 204 | Separate reception/waiting and optional docking control; cryosleep belongs to stationwide Commons. |
| Commons | 13 | 581 | Distributed cryosleep, repeated dorms/restrooms, EVA, tools, required perimeter evacuation, pod vestibules and recreation. |
| Service | 13 | 488 | Distributed functional groups; nested chapel confessional/crematorium, food-production group, entertainment, library and support. |
| Medical | 14 | 469 | Required cryo and morgue, actual surgery equipment, CMO, supplies, paramedic, psychology, optional isolation/stasis/staff rooms. |
| Engineering | 17 | 734 | TEG/AME/containment options, SMES distribution, gravity, anchor, telecoms, gas storage, tech vault, waste/recycling, substations. |
| Science | 9 | 410 | Actual R&D/robotics/artifact/anomaly machines, distinct research server, RD office, optional storage/test chamber. |
| Security | 17 | 643 | Repeated cells, detective, courtroom/law office, checkpoints, brig medical, officer preparation, head office. |
| Cargo | 8 | 425 | Real order/bounty/shuttle/expedition systems, mail support, QM and equipment rooms. |
| Command | 10 | 369 | Vault, captain office/bedroom/bathroom options, bridge, HoP, meetings and optional gateway. |
| Silicon | 5 | 89 | AI core, upload, optional power/satellite, bounded service foyer. |
| True maintenance and exterior | 6 | 338 | Workrooms, deep stores, breakrooms, EVA transitions, solar controls and actual exterior arrays. |

Each owner still has explicit bounded circulation/filler rooms. They absorb useful
space and carry movement according to their own traffic policies. They do not
stand for all unclassified station floor. Maintenance passages retain the authored
two-to-three-tile clear-width restriction.

The detention-cell and dormitory counts are configurable room-program decisions,
not counts obtained by equating lockers, beds or beacons to separate rooms.
Sentencing and the explicit meeting room preserve the requested design vocabulary;
their references corroborate the surrounding brig/command program rather than
claiming a dedicated identically named marker on every map. Other optional rooms
can also be integrated into a larger source-map room instead of being partitioned
exactly as this authored program proposes.

Security can accrue high split penalties for distributed checkpoints. This is
deliberate: a no-split rule would prohibit a common layout pattern before the
generator could weigh its costs. Secure detention and local access requirements
remain explicit. Command retains its stricter no-split policy.

## Size calibration and limits

Closed-door components provide a useful scale check, not authoritative room
measurements. Conservative default-pool samples containing only one named label
and no open edge to missing floor give examples such as:

| Named component | Observations | Median floor | Observed range |
| --- | ---: | ---: | ---: |
| Kitchen | 8 | 47.5 | 20–74 |
| Botany | 8 | 59 | 41–71 |
| Surgery | 8 | 15 | 12–21 |
| Morgue | 10 | 34 | 17–44 |
| Armory | 8 | 19.5 | 9–32 |
| Bridge | 9 | 79 | 41–145 |
| Janitor | 10 | 20 | 15–30 |
| AME | 8 | 37.5 | 30–50 |
| TEG | 9 | 106 | 75–205 |
| Robotics | 10 | 36.5 | 25–62 |

The catalog therefore gives TEG substantially more floor than the first draft,
while surgery can be a compact room rather than a large generic lab. Bounds are
deliberately broader than a particular median so machinery clearance, room
grouping and neighboring pressures can be negotiated. Parent targets equal the
sum of each child room's target area times its target instance count.

TEG gas support and TEG/AME/solar power distribution have explicit `utility`
requirements with pipe or cable interfaces. These authored functional conditions
remain separate from the walking routes. They do not claim to reproduce source-map
pipe layouts or construct working utility networks in this authoring stage.

The extractor closes doors, approximates directional walls/windows as whole
tiles, ignores furniture for floor connectivity, and can group several functions
inside a single component. It does not measure trustworthy exact department
polygons or discover every unlabeled room. Do not interpret these intervals as
an automatic copy of a playable map.

The new schema records equipment counts and working-floor reservations,
walking/interaction/utility interfaces, room choices, direct-entry owner limits,
and pressure/enclosure/placement and temperature conditions. Docking and exterior
airlock contents require `placement: spaceBoundary`, so an airlock in the middle
of an ordinary room cannot satisfy them. The food cold store has an authored
maximum of 273.15 K; this is a design goal rather than a claim that all source-map
freezers run at that exact temperature. Temperature checks require independently
supplied atmosphere observations.

These are requirements for later geometry and entity stages. A room with
`pressure: custom` is not a complete gas-mixture specification. Source-reference metadata also does not
waive final geometric, atmosphere, network or gameplay checks.

For the full semantics and extension boundary, see [FORMAT.md](FORMAT.md).
