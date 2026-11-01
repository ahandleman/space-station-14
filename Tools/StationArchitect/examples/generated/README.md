# Generated station example

Open [station-14014.station.json](station-14014.station.json) with **Open project**
in the station designer. This is seed **14014**, ring topology, compactness **1**,
placement candidate **0**, run through every stage. It is an inspectable staged
example; **Build station** compares multiple candidates and can choose another.

- [PNG preview](station-14014.png) and [SVG plan](station-14014.svg).
- [SS14 map bundle](station-14014.zip), including matching resource paths.
- [Room program YAML](station-14014.program.yaml).
- [Systems manifest](station-14014.manifest.json) and [independent export audit](export-audit.json).
- [Verification results](verification.json), [reload check](reload-check.json)
  and [packing measurements](packing-comparison.json).

The example has 139 room instances and 4,879 owned floor tiles, 191 interfaces,
148 connection proofs, 1,748 equipment entities, 1,951 native device bindings
and 9,500 native network segments. Final validation passes all hard requirements;
all 2,578 selected native terminals have proved connections to their source or
receiving outlet. Reload preserves both the physical layout and its definition.

The independent bundle audit verifies all three shuttle berths and their exterior
approaches, all 1,673 actual electrical receivers, 372 controller UID lists and
all nine waste deliveries. It checks serialized map entities against native
prototype/component definitions, independently of the designer's report.

Chapel contains its confessional and crematorium. Cryosleep belongs to Commons;
evacuation has a separate public destination. Service and Commons can occupy
several groups. Arrival, evacuation and cargo berths remain empty space. Solar
arrays connect through maintenance. Parent requirements stay active after edits.

This is a structurally checked draft, not a balanced or operational station.
All seven default gameflow rules are measured, but this candidate still misses
their desired bounds. The conservative loop search finds no qualifying witnessed
loops; strategic approach balance, escape choices, arrival/evacuation travel and
chokepoints remain visible pressures. The bounded repair pass accepted no change
on this candidate. Automatic candidate comparison uses these costs; authors can
also edit the layout, rule weights, bounds, access profiles and repair budgets.

Maintenance occupies 2,365 passage tiles in nine controlled components, with 14
station transitions. Actual wrapping coverage is 23.2% against a desired 75%; the
shortfall remains reported. Components are checked for access and corridor width.

The native pass materializes power tiers, finite charged batteries, air/scrubber
pipes, canister connectors, lighting, sensors, air/fire controls and directional
waste tubes. Initial sources include 117.4 MJ in batteries and an 18,000-litre air
bank. Sustained engine/solar output, dynamic load and atmosphere regulation still
need SS14 testing. Fire alarms have no automatic Firelock outputs in this example;
the experiment chamber's custom initial gas mixture remains unspecified. The
manifest retains `simulationVerified: false` and `operational: false`.

To reproduce the staged workflow with the local server running:

```powershell
python Tools/StationArchitect/Analysis/probe_design.py .tmp/my-station --base http://127.0.0.1:8421 --seed 14014 --topology ring --compactness 1
```

The recorded alternate-seed/topology matrix checks geometry, access and contents.
Only the included default example is claimed here to complete the native system
passes and independent bundle audit. Tests and their scopes are recorded in
`verification.json`; they do not run an SS14 round.
