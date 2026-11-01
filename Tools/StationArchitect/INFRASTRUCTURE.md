# Native station infrastructure

Infrastructure is a set of editable passes after contents: **Service devices →
Power → Atmos → Disposals**. The station program owns the profiles and rules;
the project owns actual device bindings, rotated segments and controller links.
The GUI edits both requirements and placed hardware. Moving or rotating a utility
segment locks that network for review; “Allow automatic rerouting” releases it.

`systems.enabled: false` preserves older programs. When enabled, missing devices,
unconnected ports, insufficient initial reserves and unconfigured controls remain
visible requirements. A successful geometric graph check does not run SS14.

## YAML vocabulary

| Collection | Purpose |
| --- | --- |
| `profiles` | Native prototype, required components, actual named ports, transfers, initial source facts and explicit component overrides. |
| `devices` | Select rooms or service zones, choose coverage scope, place bounded content or bind existing machinery/door hardware. |
| `networks` | Name the system, eligible segment profiles and actual source/consumer/inlet/outlet ports; specify scope and length limits. |
| `controls` | Bind controller rules to device rules in a scope; export actual entity UID lists. |
| `generation` | Bound placement searches, native route searches and generated hardware. |

Selectors support exact room-template paths, `Owner/*` prefixes, tags, roles,
pressure/placement categories and nesting. `networks` can explicitly include
`rooms`, `publicHall`, `maintenance` and `exteriorMaintenance`. Scope is `room`,
`compartment`, `cluster`, `owner` or `station`. A compartment is measured from
actual air-connected floor, rather than assumed from a department name.
Scopes group service requirements; compatible native wiring can connect across
them. The router reuses an existing compatible segment without duplicating its
map entity or changing protected hardware. Declared supply banks are physically
connected before their combined reserves can satisfy pressure requirements.

Generated device rules reuse content counts, placement and working-area
requirements. `maxCoverageDistance` allows additional devices up to the count
maximum to cover a larger service area. If the hardware cannot fit, the pass
reports the unmet requirement; it does not erase existing rooms or machinery.

Native profiles also declare `occupancyLayer` and `blocksCrew`. These describe
placement exclusivity and movement obstruction, so a buried non-colliding vent
does not consume the same solid furniture space as a canister. Devices on the
same placement layer remain exclusive, and two solid floor devices cannot overlap
by giving them different layer names. `content.minArea` still reserves any
explicit working-floor demand. The default profiles derive non-collision facts
from native components; placement layer names are editable authoring policy.

`bindExisting` binds all selected matching room hardware. `profileIds` permits
explicit native variants such as basic/advanced SMES. `bindInterfaces` binds
the actual exported walking doors, including bare generated doorways, without
creating another obstructing door on the same tile.
`bindTransport` binds the actual crew-tube and mail endpoints for power while
keeping their pneumatic paths separate from the waste network.

```yaml
systems:
  enabled: true
  profiles:
  - id: mv-wire
    prototype: CableMV
    kind: cable
    requiredComponents: [NodeContainer]
    ports:
    - {id: power, kind: power, connection: cable, nodeGroup: MVPower}
  # Device profiles must also describe the native Substation/APC ports.
  networks:
  - id: MediumVoltage
    kind: power
    scope: owner
    priority: required
    segmentProfiles: [mv-wire]
    terminals:
    - {deviceRuleId: Substations, portId: output, role: source}
    - {deviceRuleId: AreaPower, portId: input, role: consumer}
  controls:
  - id: CompartmentAirControls
    controllerRuleId: AirAlarms
    deviceRuleIds: [AirSupplyVents, AirScrubbers, AirSensors]
    scope: compartment
    component: DeviceList
    maxDevices: 192
    priority: required
```

This excerpt requires the referenced device rules and complete native profiles.
[`Catalog/infrastructure-defaults.yaml`](Catalog/infrastructure-defaults.yaml)
contains the full editable policy.
For network terminal selectors, `required: false` permits the selected hardware
to be absent. Every selected endpoint that is present must still have a proved
native connection; optional presence does not excuse an unpowered machine or a
disposal inlet that ejects into an unfinished branch.

## What the physical checks mean

**Power:** HV, MV and APC/LV are different native node groups. Substation and
APC transfers are explicit. SMES charging requires a correctly rotated adjacent
`CableTerminal`; an ordinary cable under the input is insufficient. Terminals
also block cable adjacency across their facing boundary, preventing a false
input/output short. Extension receivers need an actual provider in native range.
The initial charged batteries are finite storage; a wire is not evidence of
successful TEG, AME, singularity, solar or fuel-generator startup.

**Atmos:** adjacent pipe ports must face each other and have compatible native
layers. A supply pipe lying under a vent does not connect it. The standard policy
keeps breathing-air supply and scrubber exhaust on separate layers, uses actual
canisters and connector ports, and checks initial pressure after dilution into
connected pipe volume. The editable air reserve uses ordinary finite canisters,
not an invisible or unlimited gas source. Scrub storage is also finite.
Bulk reserve canisters are mandatory room contents, so their working-floor demand
affects the room and department tree before placement. The later systems pass
binds those already-placed machines; it does not try to squeeze a tank bank into
whatever floor remains after furnishing.

**Controls:** `DeviceList.devices` contains emitted map entity UIDs. Sharing a
frequency or room does not configure an alarm; an empty allow-list suppresses
native broadcasts. Air alarms link sensors, vents and scrubbers. Fire-alarm
inputs and actual Firelock outputs are distinct. Ordinary Airlocks do not become
Firelocks because they receive power or share a room. Automatic fire containment
requires authored real Firelock boundary hardware; the current passes do not
invent extra compartment boundaries.

**Waste:** native disposal units connect to same-tile trunks, then rotated tube
segments. Junction exit order matters. The receiving endpoint is an actual
unoccupied trunk with receiving floor, not a fictional `DisposalOutlet` prototype.
Waste transport remains separate from crew shortcut tubes and mail.

## Evidence and practical limits

[`Catalog/native-profiles.json`](Catalog/native-profiles.json) records native
prototype/code evidence; the default policy composer is
[`Analysis/build_infrastructure_defaults.py`](Analysis/build_infrastructure_defaults.py).
The native survey includes Box, Packed and Exo and their actual cables, machines
and alarm UID lists. The prototype definitions and server node implementations
in this checkout govern the modeled contacts.

Native exports still record `simulationVerified: false` and `operational: false`.
Before treating a draft as round-ready, load it in SS14 and test atmosphere,
power demand and recharge, generator/TEG startup, disposal movement, alarm and
firelock behavior, access, shuttles and gameplay. The editor checks declared
geometry and native connection structure; it cannot certify runtime capacity or
replace the remaining station furnishing and playtesting work.
