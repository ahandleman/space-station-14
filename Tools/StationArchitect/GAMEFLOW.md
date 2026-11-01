# Gameflow pressures

Gameflow evaluates the generated walking geometry between circulation and contents.
Its YAML rules belong to the station program, so they travel with imports, exports,
saved projects and the requirement tree. They describe structural gameplay
preferences, not a simulation of combat or proof that a round is balanced.

The designer has an editor for access profiles, selectors, rule weights, bounds
and analysis budgets. Select a result to inspect its actual paths and cuts.
Rooms and passages remain editable; changing either invalidates the observations.

```yaml
gameflow:
  accessProfiles:
  - id: public
    accessPolicy: levels
    allowedLevels: [public]
    networks: [publicHall]
  - id: infiltrator
    description: Assumes physical walking doors can be opened; no space traversal.
    accessPolicy: allDoors
    networks: [publicHall, maintenance]
    allowExterior: false
    allowCrewTubes: false
    doorPenalty: 2
  rules:
  - id: PublicChaseLoops
    kind: chaseLoops
    priority: desired
    weight: 12
    accessProfile: public
    from:
      networks: [publicHall]
    bounds: {min: 1, target: 2}
    routes:
      minSeparation: 4
      endpointRadius: 5
      minPathLength: 12
      minLoopLength: 40
      minEnclosedBlockedArea: 16
      maxDetourRatio: 4
  - id: StrategicTargetSeparation
    kind: travelDistance
    aggregation: min
    priority: desired
    weight: 8
    accessProfile: infiltrator
    from:
      paths: [Command/Bridge, Command/Vault, Silicon/AICore, Security/Armory]
    to:
      paths: [Command/Bridge, Command/Vault, Silicon/AICore, Security/Armory]
    bounds: {min: 16}
  settings:
    allowRepairs: true
    repairBudget: 2
    candidateBudget: 12
    maxRepairLength: 48
    repairNetworks: [publicHall, maintenance]
```

The example paths must exist in the imported program. Names have no special
meaning to the evaluator: selectors can use other room paths, tags, owners or
roles. Room-filter dimensions combine using `match: any` or `match: all`;
`networks` adds passage targets. An empty selector selects eligible rooms.

| Property | Observation |
| --- | --- |
| `chaseLoops` | Distinct witnessed loops with separated alternate paths, minimum route lengths and substantial enclosed non-walkable geometry. A wide hall's small grid cycles do not qualify. |
| `escapeRoutes` | Minimum witnessed independent approaches from selected sources to the destination area. Shared doorway runs are blocked when searching for another route. |
| `travelDistance` | Traversal cost for selected pairs, combined with `aggregation: min`, `max` or `mean`. |
| `travelBalance` | Difference between the largest and smallest observed pair traversal costs. Choose source and target sets deliberately. |
| `chokepoints` | Witnessed narrow cuts whose removal disconnects a selected pair, combined with the configured aggregation. |

Bounds are optional: `min` and `max` define acceptable values, while `target`
adds a preference inside those bounds. `weight` scales the pressure. Required
bound failures are errors; desired failures remain visible costs. A missing
route, missing selector target or exhausted analysis budget is not a passed
observation. Bounded loop sampling can provide a witnessed lower bound; the
report distinguishes that from complete coverage.

Access profiles specify room access labels and usable networks. `allDoors`
explicitly assumes access through every physical walking door. Exterior and
crew-tube traversal require explicit opt-in; mail never counts as crew travel.
Door and crew-tube costs are authored planning costs. Editor pins and locks
protect geometry and do not grant or deny an actor's in-game access.

Repairs are bounded physical passage candidates. They must lower the configured
pressure while preserving room requirements, protected geometry, shuttle space
and previously verified mandatory routes. Generation also compares placement
candidates using their requirement and gameflow costs. An unrepaired preference
remains in the report rather than being silently relaxed.

## Reference evidence

[`Analysis/gameflow-evidence.json`](Analysis/gameflow-evidence.json) records 39
strategic and travel landmarks from Bagel, Box, Marathon, Packed and Saltern.
It includes exact beacon UIDs, closed-door component boundaries and approximate
all-doors-open floor distances. Reproduce it with:

```powershell
python Tools/StationArchitect/Analysis/gameflow_evidence.py
```

These maps support checking both routes and target placement, but do not imply
one universal spacing rule. For example, the observed Bridge-to-Vault floor
distance ranges from 10 tiles on Bagel to 44 on Box under that approximation.
Some AI destinations are disconnected in this coarse model and remain recorded
as unobserved. The extractor ignores furniture and combat and treats directional
walls as whole blocked tiles; its observations are not native pathfinding proofs.

The standard program's thresholds are editable design policy. Source references
document why a feature is considered, not a claim that existing stations satisfy
every proposed preference.
