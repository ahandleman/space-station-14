# Native map comparison — fresh spatial audit

This audit reads current map tile chunks and mapped entity UIDs directly. Native room ownership is **not** inferred from beacons. Generated views are isolated department experiments, not completed station layouts.

| Native map / grid | Floor tiles | Shared amenity markers | Width / height span | Furniture-family entities | Large public-floor holes |
| --- | ---: | ---: | --- | ---: | ---: |
| box / 8364 | 15104 | 6 | 63% / 33% | 1454 | 2 |
| packed / 2 | 8122 | 7 | 22% / 30% | 761 | 0 |
| saltern / 31 | 5930 | 7 | 63% / 48% | 650 | 0 |
| bagel / 60 | 12531 | 7 | 27% / 39% | 1194 | 2 |

Shared markers screen dorms, libraries, chapels, bars, janitor closets, tools, cryosleep, observatories and other social/crew amenities. This is a declared screening vocabulary, not a complete commons ontology or a public-access classification. Furniture counts match the explicit prototype-family regex in the script; carried/container contents are excluded. Native whole-grid floor totals are all mapped non-space/non-lattice tiles, not exclusive usable room floors. Native furniture includes matching interface furnishings and spawners, while generated totals count selected feature parts; these are coverage indicators rather than identical inventories.

## Direct comparison to generated experiments

- `commons[0]`: 18 rooms, 581 usable tiles, 21 native entity parts, 10 furniture-family entities, including 0 seating/table entities. Source: `.tmp\architect3-departments-variety\commons-581-seed7\layout.json`.
- `service[0]`: 16 rooms, 568 usable tiles, 25 native entity parts, 1 furniture-family entities, including 1 seating/table entities. Source: `.tmp\architect3-departments-variety\service-568-seed7\layout.json`.

### Current local placement snapshots

The following are the distribution-aware local tests after furnishing changes. They do not claim that separated amenities have already been positioned into a whole station.

- `commons/concourse[0]`: 90 floor tiles, 4 native entity parts, 4 furniture-family entities; `.tmp\architect3-furnishing-groups\commons-concourse[0]-90-seed1\layout.json`.
- `commons/lounge[0]`: 45 floor tiles, 7 native entity parts, 7 furniture-family entities; `.tmp\architect3-furnishing-groups\commons-lounge[0]-45-seed1\layout.json`.
- `service/hospitality[0]`: 200 floor tiles, 16 native entity parts, 9 furniture-family entities; `.tmp\architect3-furnishing-groups\service-hospitality[0]-200-seed1\layout.json`.
- `service/library[0]`: 45 floor tiles, 7 native entity parts, 7 furniture-family entities; `.tmp\architect3-furnishing-groups\service-library[0]-45-seed1\layout.json`.

These generated snapshots are explicitly hashed comparison inputs. The initial snapshots predate the targeted furnishing correction: nine local room templates received local furnishings, adding 43 native entity parts through 30 feature instances by default, or 51 native parts through 34 feature instances when two optional concourse plazas are explicitly selected. Table/seat assemblies preserve all parts and their facing; grouping does not remove chairs. The plazas are not forced into distributed commons. See `../comparative-furnishing-baseline.json`. This correction does not itself complete station utilities or furnish every other department.

| Native component example | Beacon UID / grid | Floor area | Bounding fill | Seating/table entities |
| --- | --- | ---: | ---: | ---: |
| box: Cargo | 27714 / 8364 | 38 | 0.84 | 7 |
| box: Dorms | 10196 / 8364 | 63 | 0.48 | 12 |
| box: Bar | 27409 / 8364 | 96 | 0.57 | 38 |
| packed: Library | 13136 / 2 | 25 | 0.83 | 7 |
| packed: Cargo | 12387 / 2 | 20 | 1.00 | 5 |
| packed: Bar | 11974 / 2 | 48 | 1.00 | 18 |
| packed: Security | 15908 / 2 | 38 | 0.54 | 0 |
| saltern: Security | 1136 / 31 | 66 | 0.55 | 10 |
| saltern: Bar | 11319 / 31 | 88 | 0.89 | 30 |
| saltern: Library | 11325 / 31 | 53 | 0.84 | 17 |
| saltern: Cargo | 2494 / 31 | 67 | 0.56 | 6 |
| bagel: Dorms | 18705 / 60 | 94 | 0.90 | 6 |
| bagel: Security | 12356 / 60 | 33 | 0.92 | 4 |
| bagel: Library | 23375 / 60 | 69 | 0.82 | 14 |
| bagel: Bar | 16141 / 60 | 96 | 0.62 | 31 |
| bagel: Cargo | 13973 / 60 | 98 | 0.37 | 3 |

## Source-level examples

### box — grid 8364

- Bar: UID **27409**, `DefaultStationBeaconBar`, grid position `(23.5, -6.5)`; `Resources/Maps/box.yml#uid=27409`.
- Chapel: UID **5736**, `DefaultStationBeaconChapel`, grid position `(69.5, -4.5)`; `Resources/Maps/box.yml#uid=5736`.
- Cryosleep: UID **9541**, `DefaultStationBeaconCryosleep`, grid position `(24.5, 18.5)`; `Resources/Maps/box.yml#uid=9541`.
- Dorms: UID **10196**, `DefaultStationBeaconDorms`, grid position `(9.5, 9.5)`; `Resources/Maps/box.yml#uid=10196`.
- Janitor: UID **27723**, `DefaultStationBeaconJanitorsCloset`, grid position `(4.5, -34.5)`; `Resources/Maps/box.yml#uid=27723`.
- Tools: UID **27734**, `DefaultStationBeaconToolRoom`, grid position `(-41.5, 3.5)`; `Resources/Maps/box.yml#uid=27734`.
- One public-credential graph cycle uses door UIDs **862, 389, 8877, 7744** on this grid. This is a closed-floor-component graph witness, not a simulated chase route.

### packed — grid 2

- Bar: UID **11974**, `DefaultStationBeaconBar`, grid position `(32.5, -3.5)`; `Resources/Maps/packed.yml#uid=11974`.
- Chapel: UID **12827**, `DefaultStationBeaconChapel`, grid position `(36.5, 7.5)`; `Resources/Maps/packed.yml#uid=12827`.
- Cryosleep: UID **15113**, `DefaultStationBeaconCryosleep`, grid position `(21.5, 18.5)`; `Resources/Maps/packed.yml#uid=15113`.
- Dorms: UID **15912**, `DefaultStationBeaconDorms`, grid position `(15.5, 14.5)`; `Resources/Maps/packed.yml#uid=15912`.
- Janitor: UID **13133**, `DefaultStationBeaconJanitorsCloset`, grid position `(3.5, -5.5)`; `Resources/Maps/packed.yml#uid=13133`.
- Library: UID **13136**, `DefaultStationBeaconLibrary`, grid position `(10.5, 5.5)`; `Resources/Maps/packed.yml#uid=13136`.
- Tools: UID **13174**, `DefaultStationBeaconToolRoom`, grid position `(30.5, -16.5)`; `Resources/Maps/packed.yml#uid=13174`.
- One public-credential graph cycle uses door UIDs **6953, 1080, 1079** on this grid. This is a closed-floor-component graph witness, not a simulated chase route.

### saltern — grid 31

- observatory: UID **1374**, `DefaultStationBeacon`, grid position `(44.5, -24.5)`; `Resources/Maps/saltern.yml#uid=1374`.
- Bar: UID **11319**, `DefaultStationBeaconBar`, grid position `(-5.5, -5.5)`; `Resources/Maps/saltern.yml#uid=11319`.
- Chapel: UID **11324**, `DefaultStationBeaconChapel`, grid position `(-36.5, 14.5)`; `Resources/Maps/saltern.yml#uid=11324`.
- Cryosleep: UID **9207**, `DefaultStationBeaconCryosleep`, grid position `(-41.5, -4.5)`; `Resources/Maps/saltern.yml#uid=9207`.
- Janitor: UID **11323**, `DefaultStationBeaconJanitorsCloset`, grid position `(-18.5, -11.5)`; `Resources/Maps/saltern.yml#uid=11323`.
- Library: UID **11325**, `DefaultStationBeaconLibrary`, grid position `(9.5, -26.5)`; `Resources/Maps/saltern.yml#uid=11325`.
- Tools: UID **11317**, `DefaultStationBeaconToolRoom`, grid position `(-27.5, 9.5)`; `Resources/Maps/saltern.yml#uid=11317`.
- One public-credential graph cycle uses door UIDs **2138, 1475, 7111, 6587** on this grid. This is a closed-floor-component graph witness, not a simulated chase route.

### bagel — grid 60

- Bar: UID **16141**, `DefaultStationBeaconBar`, grid position `(18.5, -33.5)`; `Resources/Maps/bagel.yml#uid=16141`.
- Chapel: UID **13601**, `DefaultStationBeaconChapel`, grid position `(-25.5, 17.5)`; `Resources/Maps/bagel.yml#uid=13601`.
- Cryosleep: UID **3128**, `DefaultStationBeaconCryosleep`, grid position `(11.5, -15.5)`; `Resources/Maps/bagel.yml#uid=3128`.
- Dorms: UID **18705**, `DefaultStationBeaconDorms`, grid position `(-34.5, 24.5)`; `Resources/Maps/bagel.yml#uid=18705`.
- Janitor: UID **22485**, `DefaultStationBeaconJanitorsCloset`, grid position `(-5.5, -27.5)`; `Resources/Maps/bagel.yml#uid=22485`.
- Library: UID **23375**, `DefaultStationBeaconLibrary`, grid position `(-9.5, 19.5)`; `Resources/Maps/bagel.yml#uid=23375`.
- Tools: UID **24427**, `DefaultStationBeaconToolRoom`, grid position `(11.5, 13.5)`; `Resources/Maps/bagel.yml#uid=24427`.
- One public-credential graph cycle uses door UIDs **22015, 22014, 22017, 22016, 22019, 22051, 22088, 22018** on this grid. This is a closed-floor-component graph witness, not a simulated chase route.

## Interpretation and implementation direction

- Treat commons as station-owned amenity instances placed into multiple host neighborhoods. Keep local requirements and identifiers, but remove a global commons circulation room or shared envelope as a default placement prerequisite.
- Test amenity placement in a station context with multiple separated public circulation segments and department anchors. An isolated commons suite passing its own geometry does not test whether libraries, dormitories and arcades are distributed usefully.
- Add authored furnishing/operational kits: seating, tables, beds, lockers, counters, lights, APCs, air alarms, disposal intakes and complete machine dependencies. Preserve circulation clearance independently of furnishing coverage. Current inventory completeness means completeness of the selected YAML, which is much smaller than native map contents.
- The current correction adds exact local table/facing-seat assemblies for libraries, lounges, dining commons and optional plazas. Stage/audience orientation, counter customer positions and broader decoration still need authored arrangements; baseline counts alone do not recreate every social layout.
- Measure corridor alternatives using actual access-qualified graph paths and geometry. The public-floor-hole statistic below is only a topology screening proxy; it is not a sec-versus-antag chase simulation.
- Compare shapes at matching scales: native beacon floor components can join several functions, while generated rooms have explicit ownership. Use native examples to motivate nested suites, bent shared circulation and nonuniform room outlines rather than copying a single bounding rectangle.

## Method and limits

- Native tile chunks use `RobustToolbox/Robust.Shared/EntitySerialization/MapChunkSerializer.cs` (versions 6/7 integer tile IDs, flags, variant and optional rotation/mirror byte). Current prototype inheritance and migration aliases are resolved from this checkout.
- Geometry treats mapped wall/window/grille entities as full blocked tiles and closes doors for component measurements. Directional walls/windows therefore overpartition some geometry. Equipment collision and runtime door states are not simulated.
- Public floor opens only doors whose effective authored reader permits an empty credential set. Container-delegated readers resolve the mapped board or native `ContainerFill` board prototype according to `AccessReaderSystem.IsAllowed`; missing providers are never treated as public. Bolts, power, runtime board construction, emagging, atmospheric conditions and intermediate furniture collision remain unverified.
- Large public-floor holes are enclosed nonwalkable regions of at least nine tiles and at least three tiles on each bounding side, inside the largest eligible public component. They screen for potential circulation around substantial obstacles; they are not a count of designed chase loops.
- Shape/equipment examples in the JSON include exact beacon UIDs and furniture UIDs, but ownership remains unknown. Empty or duplicated labels do not become inferred rooms.
- PNG positions are true grid-local tile coordinates. The station overview panels have different extents; the room comparison uses equal tile scales. None is a traced native ownership map.
