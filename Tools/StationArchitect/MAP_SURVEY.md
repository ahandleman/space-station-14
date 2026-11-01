# Map survey and catalog provenance

The standard program is based on the station maps in this checkout, rather than a
generic dungeon room list. The survey covers **14 root-level station maps**:
Bagel, Box, Elkridge, Exo, Gear, Hike, Marathon, Packed, Plasma, Relic, Saltern,
Snowball, Sushi and Tram2. The default pool contains Bagel, Box, Elkridge, Exo,
Marathon, Packed, Plasma, Snowball, Sushi and Tram2. Saltern is the fallback;
Gear, Hike and Relic are supplementary examples outside that pool.

The source of pool membership is
[default.yml](../../Resources/Prototypes/Maps/Pools/default.yml). Map descriptors
under [Resources/Prototypes/Maps](../../Resources/Prototypes/Maps) supply map paths,
player limits and job slots. CentComm, arenas, ruins, tests and standalone shuttles
are outside this station sample.

## Evidence and interpretation

[Analysis/map-evidence.json](Analysis/map-evidence.json) records source hashes,
map scope, inherited and overridden beacon labels, entity UIDs, grid positions,
mapped equipment, job slots and conservative floor-component observations.
[Analysis/room-marker-frequency.csv](Analysis/room-marker-frequency.csv) provides
a smaller list of named destinations and their frequency.

Prototype defaults matter: most named station beacons do not repeat their text in
the map file. The extractor resolves entity inheritance and English beacon text,
then applies map-specific overrides. A simple text search of map overrides alone
would miss many of the station's rooms.

Among the ten default-pool main grids, the survey finds mapped `CryoPod`,
`OperatingTable`, `Morgue`, `MachineArtifactAnalyzer`, `MachineAnomalyGenerator`
and `PlayerStationAi` entities on all ten. `CloningPod` is not explicitly mapped
there. The resulting catalog distinguishes medical cryopods from crew cryosleep,
includes departmental heads' offices, utility rooms and an AI room program, and
does not infer cloning facilities merely because a cloning prototype exists.

`TegCenter` and `AmeController` also occur across the pool. Containment machinery
can include several installed or spare generator types. An entity's presence
does not establish an active power plant, a separate room or a station requirement.
Power choices in the program are authored configuration that permits combined
systems, rather than a claim that every map uses exactly one generator.

Room names, equipment families and their source references are observations.
Required/optional status, floor ranges, split penalties, access ownership and
placement rules are **authored generation policy informed by those observations**.
The catalog is a reusable station program, not an exact reconstruction of one map.

## What the geometry estimates mean

The extractor decodes the engine's grid tile format and closes doors while
finding floor components. It treats walls and windows conservatively as whole
blocked tiles and ignores furniture. Labels can share one component; department
beacons often mark circulation rather than an enclosed room. These observations
help check plausible scale, but do not establish exact room areas, door access,
atmosphere zones or traversable routes. They are not used to claim that marker
counts equal room counts.

The largest grid is used for aggregate machine statistics. Other grid locations
remain labeled separately. Detached shuttles, nullspace inventory, construction
results and runtime-spawned equipment do not silently inflate main-grid counts.
Read the limitations stored in the evidence file before using the observations
as measurements for another purpose.

## Reproduce and audit

Python 3 and PyYAML are needed only for this developer survey. The GUI itself
does not require Python. From the repository root:

```powershell
python Tools/StationArchitect/Analysis/analyze_maps.py
python Tools/StationArchitect/Analysis/audit_catalog.py
dotnet run --project Tools/StationArchitect/StationArchitect.csproj -- --validate Tools/StationArchitect/Catalog/standard-station.yaml
```

The first command refreshes map observations. Detailed door-boundary observations
can be requested with `--include-boundaries`; they remain approximations. The
second verifies every catalog map UID and prototype reference against actual
files, checks that content prototype IDs exist, and reports whether those IDs
were explicitly observed on surveyed main grids. Its result is saved in
[Analysis/catalog-audit.json](Analysis/catalog-audit.json). The C# validator then
checks the schema, reference graph, room choices, counts and floor budgets.

The canonical editable program is
[Catalog/standard-station.yaml](Catalog/standard-station.yaml). It is embedded in
the tool at build time and loaded through the same YAML parser as imported
programs; department names do not select special behavior in the core.
