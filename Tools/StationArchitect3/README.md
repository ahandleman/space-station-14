# Station Architect 3 — hierarchical layout generator

This is a separate architecture. `Tools/StationCreator` and
`Tools/StationArchitect` remain references and are not runtime dependencies.
The Python generator builds and verifies tile layouts from the nested V3 program.
The workbench tests the selected rooms and equipment from the standard catalog
at an explicit local scope: a department, its core, a suite, an individual room or
an installation. Logical ownership does not require physical clustering. The
earlier station fixture remains available for hierarchy, maintenance
and repair experiments. Saved layouts can be independently rechecked. Native SS14
map export and machinery operation are separate, unfinished work.

## Build and resize local placement tests

From the repository root, using the dependencies in [requirements.txt](requirements.txt):

```powershell
python Tools/StationArchitect3/workbench.py
```

Open **http://127.0.0.1:8435**. Choose a catalog group, then a local test. Clustered
departments can be tested together. Commons and other distributed collections
offer individual rooms or installations; they have no owner-wide packing target.
Mixed groups offer a core and independent facilities, such as Command's HoP suite.
Set the chosen test's usable floor area, preferred width/height ratio and seed.

Generate shows its actual room/equipment inventory, a selectable SVG plan, local
constraint results, advisory layout-quality checks and outstanding parent/station
obligations. A constraint pass does not certify layout quality, station distribution,
complete native furnishing or gameplay. Resize preserves that test's selected
rooms and features; a rejected change retains its last passing layout and provides
a separate failed-proposal report. Equipment and abstract service demands are
counted separately. `--port`, `--program`, `--settings` and `--output` configure
the server at launch; the browser cannot choose arbitrary files. Requested usable
floor area is exact on a passing layout. Width/height is a soft preference: room
requirements and packing can produce a different ratio, so the workbench shows
the actual footprint and achieved ratio.

For repeatable size experiments and a static gallery:

```powershell
python Tools/StationArchitect3/departments.py command --scope command/core --scales 0.85,1,1.15 --seed 1 --output .tmp/architect3-command
python Tools/StationArchitect3/departments.py commons --scope 'commons/lounge[0]' --scales 0.85,1,1.15 --output .tmp/architect3-lounge
python Tools/StationArchitect3/departments.py arrivals --area 235 --aspect 1.2 --output .tmp/architect3-arrivals
python Tools/StationArchitect3/departments.py --all --output .tmp/architect3-departments
```

Each gallery contains `index.html` and `matrix.json`, with full artifacts for every
case. `--all` expands the authored local scopes, rather than packing every logical
owner into a department. Use a case's `layout.json` with `generate.py --verify` to
recompute its checks. Bounds describe necessary area limits; some shapes or smaller sizes can still
fail because of equipment, interfaces or access. These failures are reported rather
than dropping selected rooms. A local pass checks the department and its outside
approach patches. Those patches are test connections, not constructed neighboring
departments, and a pass does not certify a whole station or operating machinery.
Parent area/cohesion requirements stay pending for partial scopes and are rebuilt
from the source on reload, so smaller local tests cannot waive them.

The current authored organization is configurable in YAML:

| Organization | Catalog groups and local tests |
| --- | --- |
| Clustered | Arrivals, Medical, Science, Cargo and Silicon test their selected department/facility subtree. |
| Mixed | Command separates its HoP suite; Security separates checkpoints, Courtroom and Law Office; Engineering separates substations from their respective cores. |
| Distributed | Commons exposes individual amenities; Service keeps Hospitality and Chapel suites intact while separating other facilities; Maintenance rooms includes independent rooms and the explicitly bound solar installation. |

See [FORMAT.md](FORMAT.md#organization-and-local-placement-scopes) for the authored
`organization` fields. These choices do not weaken a room's local interfaces,
equipment or access rules.

Earlier results reported 33 passing owner/size cases. Those checks demonstrated
selected-inventory, geometric and access predicates, not 33 usable station layouts.
In particular, the old packed Commons picture was a sheet of local amenities,
not evidence that they belong in one department. Commons concourses and generic
Service circulation remain configurable optional templates; the default no longer
forces placeholder plazas or private service halls. Actual public/maintenance
circulation and distribution require the station integration stage.

## Generate a station layout

From the repository root, using the dependencies in [requirements.txt](requirements.txt):

```powershell
python Tools/StationArchitect3/generate.py --seed 11 --output .tmp/architect3-layout
python Tools/StationArchitect3/generate.py --verify .tmp/architect3-layout/layout.json
```

Open `.tmp/architect3-layout/index.html` in a browser. The viewer supports room
selection, search, zoom and labels, with requirement results, metrics and a repair
trace. The same directory contains `layout.svg`, `layout.json`, `report.json` and
`contracts.json`.

The default [small station fixture](examples/station-layout.yaml) includes
one hospitality suite, a one-tile-deep HoP queue open directly to the hall, nested
chapel/confessional, security, a hull dock, public circulation, narrow maintenance
and two deep maintenance rooms. It is deliberately a smaller integration fixture,
not the complete standard-station inventory. Change its
YAML to change the program; change [runtime settings](examples/generation-settings.yaml)
to tune search and layout pressure:

```powershell
python Tools/StationArchitect3/generate.py Tools/StationArchitect3/examples/hierarchy-station.yaml --settings Tools/StationArchitect3/examples/generation-settings.yaml --seed 27 --output .tmp/architect3-seed27
```

`hierarchy-station.yaml` explicitly selects two hospitality suites to test repeated
local bindings. It is not the default or the standard catalog's normal selection.

[LAYOUT_SOLVER.md](LAYOUT_SOLVER.md) documents implemented behavior, geometry
semantics, APIs, settings, checks and current limits. Department generation now
places native-derived equipment footprints and checks supported local spatial
rules. Full-station generation still reports remaining native/service capability
gaps before packing. `--candidate-only` permits an explicitly incomplete station
preview; it never waives unsupported requirements.

The [hierarchical generator plan](GENERATOR_PLAN.md) describes flexible
department packing, room growth, local-first repair and upward negotiation,
maintenance coverage and layout variation. Its [proposed search policy](planning/search-policy.proposed.yaml)
and [acceptance cases](planning/acceptance-cases.yaml) remain design artifacts.
Use the runtime settings file above for implemented controls. Executable generator
checks now live alongside the authoring tests in [tests](tests).

Start with [catalog/standard-station.yaml](catalog/standard-station.yaml). It defines
the nested space vocabulary, local typed relationships and configurable feature
families, including:

- A kitchen and bar workspace with mandatory counter borders onto the **same**
  bar commons instance, plus separate staff access.
- A HoP office and public queue with a mandatory controlled service boundary.
- Nested chapel facilities, optional private quarters, departmental rooms, stationwide
  commons, custody rooms, deep maintenance and exterior installations.
- A TEG complex with an exact native machine assembly, outboard sealed burn
  chamber, exposed radiator, independent pumped hot/cold circuits, fuel supply,
  startup power and HV export, plus a required Atmos connection.
- Arrival, evacuation and cargo docking envelopes; maintenance space access;
  lattice-supported solar installations and distinct grid relationships.
- Local artifact/computer bindings, research-service needs, clinical cryo gas and
  cooling circuits, maintained cold stores, Botany water and cross-role Morgue access.
- Independent Salvage EVA access and deployment clearance, optional material
  processing, and local power, atmos, radio-channel and gravity service contracts.
- Public-facing Sentencing with native sentence/property lockers, shared Brig
  living floor and beds, separate Brig entry/exit turnstiles, a controlled
  Sentencing-to-Perma connection, and independently placed Courtroom and Law
  Office rooms. Individual timed cells are not part of the baseline; native
  sentence expiry and linked-property retrieval remain runtime checks.
- Directional body-storage loading, full PA/AME setup
  obligations, containment collectors/beam lanes, optional cargo receiving links,
  same-tile disposal entries and remote burn-chamber ignition controls.

Relationships live with their room or functional suite. HoP/queue edges belong
to `command/personnel`, serving counters to `service/hospitality`, and artifact
bindings to `science/artifact_lab`. Relative references keep these definitions
local; external service needs remain declarations by the consuming room.
The generator preserves this locality while publishing subtree contracts and
negotiating repairs. The catalog has no root relationship or choice list.

[FORMAT.md](FORMAT.md) specifies the semantics, [CATALOG.md](CATALOG.md) explains
the authored policies, and [examples/serving-clusters.yaml](examples/serving-clusters.yaml)
is a smaller complete program for the serving relationships. The JSON Schema is
[schema/program.schema.json](schema/program.schema.json).

## Validate and inspect

Use Python 3 with the dependencies in [requirements.txt](requirements.txt):

```powershell
python -m pip install -r Tools/StationArchitect3/requirements.txt
python Tools/StationArchitect3/validate.py Tools/StationArchitect3/catalog/standard-station.yaml
python Tools/StationArchitect3/validate.py Tools/StationArchitect3/catalog/standard-station.yaml --compile .tmp/station-v3.json --json
python -m unittest discover -s Tools/StationArchitect3/tests -v
python Tools/StationArchitect3/research/audit_sources.py
```

Validation checks authoring structure, references, bounds, count/choice arithmetic,
endpoint compatibility and explicit extension parameter schemas. Compilation emits
a symbolic requirement tree, activation implications and candidate instance
bindings. It does not choose positions or certify a physical interface, complete
thermodynamic circuit, native operating state or playable map.

Native operating conditions, configured device bindings and local service contracts
are deliberately declared as typed extensions. The CLI lists their required evaluators. A future
generator must implement them or refuse to certify the program; it must never
silently ignore them. The department report separates implemented spatial checks
from pending native operation and station integration; `fullDepartmentVerified`
remains false when those obligations remain. The layout solver implements the geometric subset listed in
[LAYOUT_SOLVER.md](LAYOUT_SOLVER.md). The checked-in
[symbolic compilation](catalog/standard-station.compiled.json) remains a declaration
artifact and does not itself certify a layout.

## Evidence

The focused review inspected Bagel, Box, Packed and Saltern, their native entities
and relevant engine code. A second focused pass inspected Marathon, Exo, Sushi
and Elkridge; [DEPENDENCY_REVIEW.md](DEPENDENCY_REVIEW.md) records the additions
and their local ownership. The [third map pass](LOCAL_DEPENDENCY_REVIEW.md) examined
Hike, Gear, Plasma, Relic, Snowball and Tram2 for further local installation
dependencies. Additional room vocabulary and fixture references were
carried forward as source references and independently checked against this checkout.
The detailed reports distinguish user requirements, native mechanics, repeated map
patterns and authored policy:

- [Service, command, security and medical relationships](research/room-relationships.md).
- [Engineering, grids, docking, solars and AI](research/engineering-findings.md).
- [Additional medical/service dependencies](research/additional-medical-service.md).
- [Additional science/cargo dependencies](research/additional-science-cargo.md).
- [Additional utility dependencies](research/additional-utilities.md).
- [Schema review](research/schema-review.md).
- [Department connector-policy review](research/department-validation-review.md).
- [Native-derived equipment geometry](research/fixture-geometry.md).
- [Fresh native-map comparison and earlier generated-output baseline](research/comparative-map-audit/audit.md), with a [comparison image](research/comparative-map-audit/native-vs-generated.png).
- [Source audit](research/source-audit.json).

To reproduce the comparison figures, install the optional plotting dependency and
point the audit at the current local placement gallery:

```powershell
python -m pip install -r Tools/StationArchitect3/research/requirements.txt
python Tools/StationArchitect3/research/comparative-map-audit.py --updated-scenarios .tmp/architect3-placement-resize-tests
```

Dimensions and preferred counts are editable design policy, not universal measured
standards. Existing maps are examples, not authority to override the requested
shared commons or outboard burn-room pattern. No prior-tool source is loaded by
the validator or source auditor.
