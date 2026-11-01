# Nested rooms and station placement

Room ownership and physical location are separate requirements. A department can
own several local complexes, and a room can contain further rooms. The runtime
tree retains the particular parent instance at every depth.

## Room complexes

```yaml
id: Chapel
area: { min: 25, target: 45, max: 90, weight: 1 }
complexArea: { min: 25, target: 65, max: 140, weight: 1.5 }
complexShape: { maxAspectRatio: 2.5, minFillRatio: 0.55, weight: 1 }
placement: { policy: distributed, group: Worship, anchor: any }
rooms:
- id: Confessional
  count: { min: 0, target: 1, max: 2, weight: 1 }
  area: { min: 2, target: 4, max: 10, weight: 1 }
  nesting:
    internal: true
    requireParentConnection: true
    allowExternalEntry: false
```

`area` is floor owned exclusively by that room. Its children own different floor
tiles. Optional `complexArea` bounds the sum of the room and all descendants,
counted once and excluding walls. Department totals include every owned tile
once, regardless of depth. Parent shape is measured over the actual complex
footprint, including its internal structural seams; `complexShape` overrides the
parent's ordinary shape settings when supplied.

Child count bounds apply **per parent instance**. For example,
`Service/Chapel#2/Confessional#1` belongs to the second chapel. Its template is
`Service/Chapel/Confessional`. The inspector can override a child count for one
parent without changing another parent's children. Template requirements remain
shared, and all ancestors continue to contribute their constraints and pressures.

Initial floor budgets are allocated through the whole tree before placement.
During growth, a proposed floor transfer is scored against the room, every
affected ancestor complex and its department. Hard bounds, connected floor,
physical containment and protected geometry must remain satisfied. Accepted wall
movements update the affected complex allocations; they do not rewrite authored
area targets or enlarge a reservation merely to conceal an unrelated room.

Local references support sibling names, `..` for the immediate parent,
`./Child` for a child, and `../Sibling` or further parent steps. Explicit canonical
paths remain valid. Local nested relationships resolve within the corresponding
ancestor instance; a door in another chapel cannot satisfy the requirement.

Internal placement requires an actual closed child boundary and membership in
the parent's physical complex. A child may share the complex's outer wall. By
default it must be reachable through its own parent, with no hall, maintenance or
crew-tube shortcut bypassing that parent. Set `allowExternalEntry: true` when a
particular design permits a secondary entrance, and configure the ordinary access
permissions for that entrance as well. A tree label or enlarged planning mask
alone does not establish physical containment or access.

The program editor has **+ Internal room**, a parent selector for moving a
subtree, and subtree duplication/removal. Dependencies follow renamed or moved
definitions. Deleted targets remain visible as broken dependencies. In an
existing generated project, a changed definition path preserves stale geometry
for review; it is not silently assigned to a different room. Moving a generated
parent room carries its descendants and attached equipment. Locks and manual
edits remain active through subsequent stages.

## Location policies

| Policy | Intended placement |
| --- | --- |
| `grouped` | Stay in the department's local group. |
| `distributed` | Form smaller functional clusters within a distributed owner. |
| `stationwide` | Use useful sites throughout the station while retaining the owner budget. |

`placement.group` is an optional author-defined grouping label. The default food
service group keeps kitchen, botany, cold storage and bar together, while chapel,
library and entertainment can form other clusters. Required relationships remain
constraints when selecting and placing these groups. Nested internal children
remain with their specific parent complex.

`placement.anchor: perimeter` expresses an edge-location preference. Actual space
frontage and exterior equipment still have their own measurable requirements.
The default program places crew cryosleep in Commons with a stationwide policy.
Evacuation is a required public destination with a perimeter preference and an
exterior emergency-shuttle interface. It is distinct from Arrivals.

Department area, shape and split rules are still authored requirements. The
Commons and Service defaults explicitly allow dispersed ownership. Their room
and complex limits continue to enforce local shape; their wider owner bounds do
not erase hard constraints on other departments.

## Packing and maintenance controls

The designer's **Generation settings → Station packing** controls are saved in
`settings.packing` in the project JSON:

| Setting | Effect |
| --- | --- |
| `compactness` | Controls clearance above the space required for physical circulation. Higher values pack groups closer. |
| `departmentBundling` | Weights how closely separated groups under the same owner stay together. |
| `interlock` | Fits neighboring groups into unused portions of their outlines, protecting walls, entrances and docking volumes. |
| `internalVariation` | Varies room proportions and arrangement reproducibly by seed. |
| `silhouetteVariation` | Varies department outlines and internal circulation shape. |
| `preferredAspectRatio` | Preferred width-to-height ratio of the overall packed plan. |
| `distributedClusters`, `maxRoomsPerCluster` | Control how distributed owners are divided into local groups. |
| `stationwideSpacing` | Preferred separation for stationwide facilities. |
| `boundaryGrowthBudget` | Bounds cross-owner floor negotiation while preserving local and ancestor requirements. |
| `maintenanceCoverage` | Target fraction of cluster boundary edges served by nearby maintenance. |
| `maintenanceWrapDistance` | Controls the distance of maintenance wrapping from cluster boundaries. |
| `maintenanceConnectionsPerCluster` | Target count of distinct maintenance entries into an eligible local cluster. |

Aspect-ratio and stationwide-spacing preferences influence compact ring packing.
Hub and spine retain their backbone arrangements and adjust group assignments
to respect distributed-owner shape limits. Compactness and department bundling
also apply to those topologies. Extreme spacing or large room programs can exceed
the supported 512-by-512 canvas; those cases remain editable drafts with capacity
diagnostics, rather than silently dropping rooms.

Public and maintenance floor remain separate. Maintenance passages retain the
configured two-to-three-tile width; larger areas require actual room definitions.
Wrapping is measured against physical clusters and built passages. Coverage and
entrance targets report limitations when other constraints prevent them;
mandatory route, access, width and nesting failures remain errors.

The layout choices are informed by decoded floor, doors and beacon locations in
existing maps, documented in [LAYOUT_EVIDENCE.md](LAYOUT_EVIDENCE.md). These are
planning and geometry checks. Exported drafts still need verification in SS14.
