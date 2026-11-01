# StationArchitect3 schema review

This is a design review, not the normative schema or an implementation plan. The earlier tools were inspected read-only. V3 should describe the station that must exist without inheriting their layout algorithms, runtime masks, or compatibility constraints.

## Semantics to settle before authoring the catalog

| Concern | Recommended contract |
| --- | --- |
| Ownership versus containment | A department is a logical owner, not necessarily one enclosing room or rectangle. Spatial nesting is a separate relation: a confessional belongs inside its chapel complex; a remote security checkpoint still belongs to Security. Permit explicit logical ownership where it differs from the containing suite. |
| Exclusive area | Every usable floor tile belongs to exactly one physical room. A parent's own usable area excludes child floor. Its subtree usable area is the union of its own and descendant floor, counted once. A gross envelope may additionally include walls and voids, but must have a separately named measure. Never compare a usable-floor budget with a bounding rectangle. |
| Nested access | Child counts apply per parent instance. Internal containment, required access through the parent, and permitted secondary entrances are distinct conditions. An internal child may share the outer hull; surrounding it with parent floor on every side is unnecessary. A child connected only to another chapel instance does not satisfy its own parent requirement. |
| Shape and splitting | Define whether each shape limit measures exclusive floor, subtree union, or gross envelope. Define department components by physical connectivity, not template grouping. A bounding-box aspect ratio alone does not prevent snakes, holes, or narrow bottlenecks. Door-connected and directly touching floor are different component notions. |
| Hard requirements versus preferences | Required counts, containment, bounds, forbidden adjacencies, and required edges are feasibility conditions. Weights rank feasible alternatives or express desired/avoided relationships; a sufficiently high score cannot buy permission to violate a mandatory edge. Give “avoided” and “forbidden” separate meanings. |

Keep author bounds and preferred targets intact. Later consumers may compute allocations, but must report infeasibility rather than silently expanding maximums, deleting optional-but-selected rooms, or converting required relationships to preferences.

## Edges need instance-level binding

A list of template paths and `all`/`any` is insufficient. Specify these independently:

- **Scope:** station, same owner, same parent complex, or a named instance-binding scope. Relative references resolve lexically and unambiguously.
- **Source quantifier:** each selected source instance, at least one source, or an aggregate group requirement.
- **Target matching:** each source needs one, several, or all targets; whether targets must be distinct; whether one target may serve many sources. Define whether “all” means all templates or all instantiated rooms.
- **Presence:** both endpoints already selected, source requires target selection, or an explicit conditional dependency. A desired connection must not unexpectedly make an optional facility mandatory.
- **Direction and alternatives:** source-to-target, bidirectional, AND requirements, and OR alternatives are explicit. Two required TEG gas loops are not one generic pipe alternative.

For example, two chapels with two confessionals each need four local parent bindings, not four arbitrary paths to either chapel. Multiple solar arrays need an explicit controller-sharing policy. A single physical interface may satisfy several compatible statements, but consumes its physical capacity only once.

Choices must state whether they count distinct template families, room instances, or complete assemblies, and whether they apply stationwide or per parent. Conditional support rooms must follow the selected assembly. Reject contradictory choices, dependency cycles with no feasible selection, dangling references, and impossible distinct-target matchings before geometry.

## Physical relationship types

Do not let a generic “connection” erase the following distinctions:

| Relationship | Required evidence described by the architecture |
| --- | --- |
| Shared border | Cardinal, contiguous boundary length; wall/separator thickness; permitted structure. Corner contact and two short disjoint runs do not satisfy one continuous counter run. |
| Walking | Actual passage width, standing/turning clearance, permitted intermediate spaces, actor access profile, direction, and optional path-length bound. |
| Interaction | Counter, service window, observation window, or other typed interface with usable positions on both sides. It need not allow movement or gas exchange. |
| Utility | Named endpoint ports and a typed network: gas circuit, cable class, waste, mail, control, etc. Direction, layers, incompatible circuits, and crossing adapters matter. Room proximity does not prove continuity. |
| Separation | Minimum clearance, forbidden shared border, pressure isolation, or hazard exclusion zone. These are different conditions. |

Opening width, total shared-border length, number of interfaces, and usable border capacity need separate units. Reserve enough border for required doors, counters, windows, and machinery together; do not overbook one wall segment. Mail transports items; a crew shortcut carries people. Neither automatically substitutes for a walking route, an airtight boundary, or a gas circuit.

Access labels should reference defined credential/actor profiles with explicit AND/OR semantics. “Can reach using maintenance” is existential; “every station approach traverses maintenance” is a stronger deep-maintenance restriction. Controlled crossings and continuous maintenance-only routes must remain explicitly different policies.

## Space, grids, and the TEG

Model grid membership separately from environment and surface. A vacuum region can be `Space` tiles on the station grid, a lattice platform, an unsealed floored compartment, or a different grid. These are not interchangeable. Give grids stable identities and local coordinate frames; cross-grid relationships require explicit docking/bridging/transport capability. A detached grid cannot satisfy a same-grid pipe adjacency merely by being nearby. An enclosed courtyard of actual space can be vacuum without a flood path to the map edge.

The TEG should be a functional assembly containing a service/machinery area, hot-side heat source or burn chamber, and cold-side heat rejection region, with two distinct directed gas circuits, safe staff access, and appropriate enclosure/material constraints. A burn chamber's hot atmosphere is distinct from hot gas inside a sealed pipe. A radiator area's vacuum requirement is distinct from the staffed TEG room's breathable atmosphere. Do not require a separate grid merely because a region is exposed to space.

The native three-machine arrangement also matters: [TegNodeGroup.cs](../../../Content.Server/Power/Generation/Teg/TegNodeGroup.cs) requires anchored, same-grid, immediately adjacent circulators on opposite sides with specific relative rotations. [teg.yml](../../../Resources/Prototypes/Entities/Structures/Power/Generation/teg.yml) declares their inlet/outlet ports and the center's HV output and LV operating receiver. [HeatExchangerSystem.cs](../../../Content.Server/Atmos/EntitySystems/HeatExchangerSystem.cs) exchanges heat with the containing environment and refuses an air-blocked tile. Therefore equipment counts and a generic “TEG connects to Atmos” edge do not encode a functioning layout. Architectural requirements can reserve these roles and interfaces now; operating performance remains a separate future validation concern.

## Catalog audit: preserve functions, repair underspecification

The [old catalog](../../StationArchitect/Catalog/standard-station.yaml) already includes the major department heads, cryosleep distinct from medical cryogenics, surgery/morgue, AI core/upload, chapel children, detention/legal spaces, telecoms, waste, and solar controls/arrays. Do not replace it with a smaller list of generic department boxes. The important gaps are functional subdivisions and conditions:

- **TEG/Atmos:** no explicit burn chamber, hot/cold loop regions, radiator space, or circuit separation in the old TEG room. Direct map evidence includes Box's Burn Chambers camera UID 4263 and TEG Cool Loop camera UID 7004; Packed's burn-chamber blast-door control UID 6937. These establish real functions, not exact room polygons or universal dimensions.
- **Engine variants:** singularity and Tesla machinery cannot be interchangeable equipment inside one otherwise identical chamber. Each assembly needs its own containment geometry, accelerator/support requirements, hazard margins, and startup/operating assumptions. The old power choice counts families, not operational readiness.
- **Cargo/exterior:** salvage needs a reserved functional operating/arrival volume, not only room area and a magnet count. Arrivals, evacuation, cargo, and escape pods need actual berth/approach constraints and relationships to the intended shuttle/grid, distinct from ordinary exterior frontage.
- **Atmos/waste/service:** distinguish gas supply/storage, distribution, reclaim/exhaust, and hazardous plant from operator workspace; disposal receiving/recycling from crew shortcut/mail networks. These functions may share a room when allowed, but require separate endpoints and space budgets.
- **Secure and public interfaces:** cells/perma need controlled custody routes and boundary rules; pharmacy, kitchen, bar, cargo orders, and HoP need explicit public-facing interfaces plus separate staff access. A department-local path alone does not encode either condition.
- **Usable capacity:** machinery footprint, rotation, working space, aisle continuity, and boundary requirements must be expressible independently. The old scalar `minArea` cannot alone guarantee a canister bank, morgue tray, or dense laboratory is usable.

Treat source-map entities and labels as evidence with map path, entity UID, grid, observed role, and confidence. Authored minima/preferences are policy, not inferred facts. Optional isolation rooms, recreation, and unusual engine variants should not become universal necessities solely because one source map contains them. The existing [map-method limitations](../../StationArchitect/MAP_CATALOG.md) are particularly relevant: beacons and closed-door components are not authoritative room counts or department polygons.

## Units and extension behavior

Declare tile length/area and whether dimensions are integer cells, world metres, or directed boundary edges; use Kelvin, kPa, litres/moles, watts, and explicit path-distance metrics where applicable. Distinguish omitted values, zero, unbounded, and inherited values. Missing atmosphere composition is unknown, not automatically breathable or a successful “custom” condition.

Use versioned core types and namespaced extension payloads. Unknown core keys, relationship kinds, units, and mandatory extensions must fail with a path-specific diagnostic; preserve recognized optional extension data for round trips. Definitions and source evidence are independent from later observed/generated results. A future consumer must report an unsupported requirement rather than silently treating it as satisfied.
