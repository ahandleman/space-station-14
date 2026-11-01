using System.Security.Cryptography;
using System.Text;

namespace Content.StationGeneration;

/// <summary>Checks actual planning masks and declared ports without regenerating the layout.</summary>
public static class StationSpatialValidator
{
    public static GenerationResult Validate(StationProject project)
    {
        var result = new GenerationResult { Seed = project.Config.Seed, Attempts = 1 };
        var diagnostics = result.Diagnostics;
        void Error(string stage, string code, string message) => diagnostics.Add(new(stage, code, message));
        diagnostics.AddRange(StationSpatialPlanner.Check(project));
        if (diagnostics.Count != 0) return result;
        var c = project.Config;
        var t = project.Topology!;
        var p = project.SpatialPlan;
        if (p == null) { Error("placement", "missing", "Place department seeds before validating the spatial plan."); return result; }
        if (p.Envelope == null || p.Seeds == null || p.Regions == null || p.Ports == null ||
            p.PublicReservations == null || p.ServiceReservations == null || p.InitialServiceReservations == null || p.PublicCorridors == null ||
            p.Maintenance == null || p.Walls == null || p.Hull == null || p.ReservedExterior == null ||
            p.ExteriorBoundary == null || p.StructuralReserve == null || p.DeepMaintenanceRooms == null ||
            p.SolarInstallations == null || p.MaintenanceSpaceAccess == null || p.CommonMaintenanceRooms == null)
        { Error("validation", "spatial-data", "All spatial layer lists are required."); return result; }
        if (p.DeepMaintenanceRooms.Any(r => r == null || r.Cells == null || r.Walls == null || r.PortCells == null) ||
            p.SolarInstallations.Any(s => s == null || s.Cells == null || s.Catwalk == null || s.ConnectionCells == null) ||
            p.MaintenanceSpaceAccess.Any(a => a == null || a.BarrierCells == null || a.ExteriorCells == null))
        { Error("maintenance", "feature-data", "Maintenance room and exterior installation masks must be present."); return result; }
        if (p.CommonMaintenanceRooms.Any(r => r == null || r.Cells == null || r.Walls == null || r.PublicDoorCells == null || r.MaintenanceDoorCells == null))
        { Error("maintenance", "feature-data", "Hall-connected maintenance room masks must be present."); return result; }
        if (p.CellSize is < 4 or > 8 || p.CellSize < c.CorridorWidth + 2)
            Error("placement", "parcel-size", "Parcel size must be 4–8 and at least public corridor width + 2.");
        var envelope = p.Envelope.ToHashSet();
        var publicReservations = p.PublicReservations.ToHashSet();
        var serviceReservations = p.ServiceReservations.ToHashSet();
        var publicTiles = p.PublicCorridors.ToHashSet();
        var maintenance = p.Maintenance.ToHashSet();
        var walls = p.Walls.ToHashSet();
        var hull = p.Hull.ToHashSet();
        var structural = p.StructuralReserve.ToHashSet();
        var exterior = p.ReservedExterior.ToHashSet();
        foreach (var (name, cells) in new[] { ("envelope", p.Envelope), ("public reservation", p.PublicReservations),
                     ("service reservation", p.ServiceReservations), ("public circulation", p.PublicCorridors),
                     ("maintenance", p.Maintenance), ("walls", p.Walls), ("hull", p.Hull),
                     ("exterior clearance", p.ReservedExterior), ("structural reserve", p.StructuralReserve),
                     ("exterior boundary", p.ExteriorBoundary) })
        {
            if (cells.Count != cells.Distinct().Count()) Error("validation", "duplicate-cells", $"The {name} layer contains duplicate cells.");
            if (cells.Any(cell => !StationGenerator.Inside(c, cell))) Error("validation", "out-of-bounds", $"The {name} layer extends outside the canvas.");
            if (name != "exterior clearance" && name != "envelope" && cells.Any(cell => !envelope.Contains(cell)))
                Error("validation", "outside-envelope", $"The {name} layer extends outside the station envelope.");
        }
        if (envelope.Count == 0 || StationValidator.Components(envelope).Count != 1)
            Error("placement", "envelope-connectivity", "The station envelope must be nonempty and connected.");
        if (exterior.Overlaps(envelope)) Error("growth", "docking-overlap", "Docking clearance must remain outside the station envelope.");
        if (publicReservations.Count == 0 || StationValidator.Components(publicReservations).Count != 1)
            Error("placement", "public-reservation", "Public circulation reservations must form one connected network.");
        var definitions = c.Departments.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var ids = definitions.Keys.ToHashSet(StringComparer.Ordinal);
        if (p.Seeds.Any(s => s == null) || p.Seeds.Count != ids.Count ||
            !ids.SetEquals(p.Seeds.Select(s => s.Id)) || p.Seeds.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != ids.Count)
        { Error("placement", "seeds", "Each configured department needs exactly one seed."); return result; }
        var seeds = p.Seeds.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var occupiedSeedParcels = new HashSet<Cell>();
        foreach (var seed in p.Seeds)
        {
            var definition = definitions[seed.Id];
            if (!envelope.Contains(seed.Position) || publicReservations.Contains(seed.Position) || serviceReservations.Contains(seed.Position))
                Error("placement", "seed-position", $"{seed.Id}: seed must occupy a free department parcel inside the envelope.");
            if (seed.TargetArea < definition.MinArea || seed.TargetArea > definition.MaxArea)
                Error("placement", "seed-area", $"{seed.Id}: seed target violates its configured area limits.");
            if (p.CellSize > 0 && !occupiedSeedParcels.Add(new(seed.Position.X / p.CellSize, seed.Position.Y / p.CellSize)))
                Error("placement", "seed-overlap", $"{seed.Id}: two department seeds occupy the same growth parcel.");
        }
        if (p.Seeds.Any(seed => !StationGenerator.Inside(c, seed.Position)))
        {
            result.Metrics = new() { EnvelopeArea = envelope.Count };
            return result;
        }
        if (p.Regions.Count == 0)
        {
            Error("growth", "missing", "Grow department territories before validating the finished spatial plan.");
            result.Metrics = new() { EnvelopeArea = envelope.Count, PublicArea = publicTiles.Count, UnallocatedArea = envelope.Count };
            return result;
        }
        if (p.Regions.Any(r => r == null || r.Territory == null || r.Cells == null) || p.Regions.Count != ids.Count ||
            !ids.SetEquals(p.Regions.Select(r => r.Id)) || p.Regions.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != ids.Count)
        { Error("growth", "departments", "Each configured department needs exactly one region with territory and floor masks."); return result; }
        var regions = p.Regions.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var floors = p.Regions.ToDictionary(r => r.Id, r => r.Cells.ToHashSet(), StringComparer.Ordinal);
        var territories = p.Regions.ToDictionary(r => r.Id, r => r.Territory.ToHashSet(), StringComparer.Ordinal);
        if (p.Ports.Any(port => port == null || port.Cells == null))
        { Error("growth", "port-data", "Every port needs a valid definition and cell list."); return result; }
        var owners = new Dictionary<Cell, string>();
        foreach (var region in p.Regions)
        {
            var territory = territories[region.Id]; var floor = floors[region.Id]; var definition = definitions[region.Id];
            if (territory.Count != region.Territory.Count || floor.Count != region.Cells.Count)
                Error("growth", "duplicate-cells", $"{region.Id}: territory or floor contains duplicate cells.");
            if (territory.Count == 0 || StationValidator.Components(territory).Count != 1)
                Error("growth", "territory-connectivity", $"{region.Id}: territory must be nonempty and connected.");
            if (floor.Count == 0 || StationValidator.Components(floor).Count != 1)
                Error("growth", "floor-connectivity", $"{region.Id}: usable floor must be nonempty and connected.");
            if (!territory.IsSupersetOf(floor) || territory.Any(cell => !envelope.Contains(cell)))
                Error("growth", "territory-bounds", $"{region.Id}: territory is outside the envelope or does not contain its floor.");
            if (floor.Count < definition.MinArea || floor.Count > definition.MaxArea)
                Error("growth", "floor-area", $"{region.Id}: usable floor area {floor.Count} must be within {definition.MinArea}–{definition.MaxArea}.");
            if (!territory.Contains(seeds[region.Id].Position) || region.Seed != seeds[region.Id].Position)
                Error("growth", "region-seed", $"{region.Id}: territory must contain its declared planning seed.");
            if (!CoveredBySquares(floor, 2).IsSupersetOf(floor))
                Error("growth", "floor-width", $"{region.Id}: usable floor contains one-tile slivers or an unusably narrow neck.");
            var depth = c.Spatial.DepartmentBoundaryMaxDepth;
            var nearFloor = floor.SelectMany(cell => new Rect(cell.X - depth, cell.Y - depth, depth * 2 + 1, depth * 2 + 1).Cells()).ToHashSet();
            var wallSpurs = territory.Where(cell => !hull.Contains(cell) && !nearFloor.Contains(cell)).ToList();
            if (wallSpurs.Count != 0)
                Error("growth", "department-boundary-depth", $"{region.Id}: {wallSpurs.Count} territory tiles near ({wallSpurs[0].X}, {wallSpurs[0].Y}) exceed the {depth}-tile boundary allowance around usable floor. Reclaimed space must add floor or an enclosed room, not solid wall branches.");
            var roomSide = Math.Max(2, Math.Min(5, (int) Math.Floor(Math.Sqrt(definition.MinArea))));
            if (!floor.Any(anchor => new Rect(anchor.X, anchor.Y, roomSide, roomSide).Cells().All(floor.Contains)))
                Error("growth", "room-fit", $"{region.Id}: usable territory needs at least one {roomSide}×{roomSide} floor block for a basic room; corridor-shaped area alone is insufficient.");
            foreach (var cell in territory)
                if (!owners.TryAdd(cell, region.Id)) ErrorOnce("growth", "territory-overlap", "Department territories overlap.");
        }
        foreach (var (name, layer) in new[] { ("\0public", publicReservations), ("\0service", serviceReservations), ("\0structural", structural) })
        foreach (var cell in layer)
            if (!owners.TryAdd(cell, name)) ErrorOnce("growth", "ownership-overlap", "Department, public, service and structural ownership layers overlap.");
        var unallocated = envelope.Except(owners.Keys).Count();
        if (unallocated != 0) Error("growth", "unallocated-envelope", $"{unallocated} envelope tiles have no department, circulation, service or structural ownership.");
        var expectedHull = InnerHull(envelope, c.HullThickness);
        var boundary = envelope.Where(cell => Neighbors(cell).Any(n => !envelope.Contains(n))).ToHashSet();
        if (!boundary.SetEquals(p.ExteriorBoundary)) Error("growth", "exterior-boundary", "Exterior boundary does not match the station envelope.");
        if (hull.Count == 0 || !hull.SetEquals(expectedHull))
            Error("growth", "hull-boundary", "Hull must match the configured inner boundary of the station envelope.");
        var expectedWalls = new HashSet<Cell>();
        foreach (var (cell, owner) in owners.Where(kv => !kv.Value.StartsWith('\0')))
        foreach (var neighbor in Neighbors(cell))
        {
            if (expectedHull.Contains(cell)) continue;
            if (!owners.TryGetValue(neighbor, out var other) || other.StartsWith('\0') && other != owner) expectedWalls.Add(cell);
            else if (other != owner) expectedWalls.Add(string.CompareOrdinal(owner, other) > 0 ? cell : neighbor);
        }
        expectedWalls.ExceptWith(expectedHull);
        var portCells = new HashSet<Cell>();
        foreach (var port in p.Ports)
        {
            if (port == null || port.Cells == null || !definitions.ContainsKey(port.Department) ||
                port.Kind is not ("public" or "maintenance" or "adjacency") || port.Width < 1 || port.Width > 5)
            { Error("growth", "port-data", "A port has an invalid department, kind, width or cell list."); continue; }
            var cells = port.Cells.ToHashSet();
            if (cells.Count != port.Width || cells.Count != port.Cells.Count || !cells.Contains(port.Center) || !cells.IsSubsetOf(expectedWalls))
                Error("growth", "port-boundary", $"{port.Department}: port must own its declared width of planned wall cells.");
            foreach (var cell in cells)
                if (!portCells.Add(cell)) ErrorOnce("growth", "port-overlap", "Ports overlap one another.");
            var room = floors[port.Department];
            HashSet<Cell> network;
            var access = definitions[port.Department].Access;
            var width = port.Kind == "maintenance" ? c.MaintenanceWidth : c.CorridorWidth;
            if (port.Kind == "adjacency")
            {
                if (port.OtherDepartment == null || port.OtherDepartment == port.Department || !floors.TryGetValue(port.OtherDepartment, out network!))
                { Error("growth", "port-neighbor", $"{port.Department}: adjacency port needs another configured department."); continue; }
                var otherAccess = definitions[port.OtherDepartment].Access;
                access = access == "secure" || otherAccess == "secure" ? "secure" : access == "restricted" || otherAccess == "restricted" ? "restricted" : "public";
            }
            else network = port.Kind == "public" ? publicTiles : maintenance;
            if (port.Width != width || port.Access != access)
                Error("growth", "port-policy", $"{port.Department}: port width or access does not match its declared connection.");
            if (port.Kind == "maintenance" && !seeds[port.Department].AllowMaintenanceAccess)
                Error("maintenance", "maintenance-access-policy", $"{port.Department}: maintenance port exists although service access is disabled.");
            if (!Cell.Directions.Any(normal => cells.All(cell => room.Contains(cell + normal * -1) && network.Contains(cell + normal)) &&
                    IsStraight(cells, port.Center, normal)))
                Error("growth", "port-traversability", $"{port.Department}: every port tile needs department floor on one side and its declared destination on the other.");
        }
        expectedWalls.ExceptWith(portCells);
        expectedWalls.UnionWith(p.DeepMaintenanceRooms.Where(room => room?.Walls != null).SelectMany(room => room.Walls));
        expectedWalls.UnionWith(p.CommonMaintenanceRooms.SelectMany(room => room.Walls));
        if (!walls.SetEquals(expectedWalls)) Error("growth", "wall-boundary", "Planned walls do not match actual department boundaries and declared ports.");
        foreach (var region in p.Regions)
        {
            var expectedFloor = new HashSet<Cell>(territories[region.Id]);
            expectedFloor.ExceptWith(expectedHull); expectedFloor.ExceptWith(walls); expectedFloor.ExceptWith(portCells);
            if (!expectedFloor.SetEquals(floors[region.Id])) Error("growth", "floor-ownership", $"{region.Id}: usable floor does not match its territory, walls, hull and ports.");
            if (p.Ports.Count(port => port != null && port.Kind == "public" && port.Department == region.Id) != definitions[region.Id].Entrances)
                Error("growth", "entrance-count", $"{region.Id}: expected {definitions[region.Id].Entrances} public entrances.");
        }
        var actual = new HashSet<Cell>();
        var deepFloor = p.DeepMaintenanceRooms.Where(room => room?.Cells != null).SelectMany(room => room.Cells).ToHashSet();
        var deepPorts = p.DeepMaintenanceRooms.Where(room => room?.PortCells != null).SelectMany(room => room.PortCells).ToHashSet();
        var commonFloor = p.CommonMaintenanceRooms.SelectMany(room => room.Cells).ToHashSet();
        var commonDoors = p.CommonMaintenanceRooms.SelectMany(room => room.PublicDoorCells.Concat(room.MaintenanceDoorCells)).ToHashSet();
        foreach (var (name, cells) in floors.Select(kv => (kv.Key, kv.Value)).Concat(new[] { ("public", publicTiles), ("maintenance", maintenance), ("walls", walls), ("hull", hull), ("ports", portCells), ("deep maintenance", deepFloor), ("deep maintenance doors", deepPorts), ("maintenance rooms", commonFloor), ("maintenance room doors", commonDoors) }))
        foreach (var cell in cells)
        {
            if (!envelope.Contains(cell)) ErrorOnce("validation", "outside-envelope", $"The {name} physical layer extends outside the envelope.");
            if (!actual.Add(cell)) ErrorOnce("validation", "layer-overlap", "Floor, public, maintenance, wall, hull or port layers overlap.");
        }
        if (!publicReservations.IsSupersetOf(publicTiles)) Error("growth", "public-ownership", "Public circulation extends outside its reservation.");
        if (!serviceReservations.IsSupersetOf(maintenance)) Error("maintenance", "maintenance-ownership", "Maintenance extends outside its service reservation.");
        if (publicTiles.Count == 0 || StationValidator.Components(publicTiles).Count != 1)
            Error("growth", "public-connectivity", "Public circulation must form one connected walkable network.");
        if (!CoveredBySquares(publicTiles, c.CorridorWidth).IsSupersetOf(publicTiles))
            Error("growth", "public-width", "Public circulation has insufficient width at a bend, junction or edited edge.");
        var publicPorts = p.Ports.Where(port => port != null && port.Kind == "public" && port.Cells != null).ToList();
        var publicWalk = new HashSet<Cell>(publicTiles); publicWalk.UnionWith(publicPorts.SelectMany(port => port.Cells));
        if (StationValidator.Components(publicWalk).Count != 1)
            Error("growth", "entrance-reachability", "Public entrances are not all connected through public circulation.");
        var distancesByDepartment = new Dictionary<string, Dictionary<Cell, int>>(StringComparer.Ordinal);
        foreach (var id in ids)
            distancesByDepartment[id] = Distances(publicPorts.Where(port => port.Department == id).SelectMany(port => port.Cells), publicWalk);
        var networks = new Dictionary<int, (List<SpatialPort> Ports, HashSet<Cell> Walk, Dictionary<string, Dictionary<Cell, int>> Distances)>
        {
            [1] = (publicPorts, publicWalk, distancesByDepartment)
        };
        foreach (var edge in t.Edges)
        {
            var adjacent = territories[edge.From].Any(cell => Neighbors(cell).Any(territories[edge.To].Contains));
            if (edge.Hard && edge.Relation == "adjacency" && (!adjacent || !p.Ports.Any(port => port != null && port.Kind == "adjacency" && Pair(port.Department, port.OtherDepartment ?? "", edge.From, edge.To))))
                Error("growth", "required-adjacency", $"{edge.From} and {edge.To} need a usable shared boundary and declared adjacency port.");
            if (edge.Priority == "avoided" && edge.StrictAvoidance && edge.Relation == "adjacency" && adjacent)
                Error("growth", "avoided-adjacency", $"{edge.From} and {edge.To} share a boundary prohibited by Never allow.");
            if (edge.Relation == "adjacency") continue;
            var methods = (edge.AllowedModes.Contains("publicHall") ? 1 : 0) | (edge.AllowedModes.Contains("maintenance") ? 2 : 0);
            var supportedWalking = edge.Relation is "walking" or "delivery" && methods != 0;
            if (edge.Hard && !supportedWalking)
                Error("growth", "unsupported-fulfillment", $"{edge.From} → {edge.To}: the required {edge.Relation} dependency needs a physical method not constructed by this planning stage.");
            if (!supportedWalking) continue;
            var network = Network(methods);
            var sourceDistances = network.Distances[edge.From];
            var distance = network.Ports.Where(port => port.Department == edge.To).SelectMany(port => port.Cells)
                .Where(sourceDistances.ContainsKey).Select(cell => sourceDistances[cell]).DefaultIfEmpty(int.MaxValue).Min();
            var methodName = methods == 1 ? "public hall" : methods == 2 ? "maintenance" : "public hall / maintenance";
            if (edge.Hard && distance == int.MaxValue)
            {
                var hint = methods == 2 ? "Enable service access at both departments and connect their maintenance entrances on the same service network." :
                    "Provide declared department entrances onto connected allowed networks.";
                Error("growth", "required-route", $"{edge.From} → {edge.To}: no legal {methodName} route realizes the required dependency. {hint}");
            }
            else if (edge.Hard && edge.MaxDistance is { } maximum && distance > maximum)
                Error("growth", "required-distance", $"{edge.From} → {edge.To}: {methodName} entrance travel is {distance} tiles, exceeding the required {maximum}.");
            if (edge.Priority == "avoided" && edge.StrictAvoidance && distance != int.MaxValue)
                Error("growth", "avoided-route", $"{edge.From} → {edge.To}: a {methodName} route violates a Never allow relationship.");
        }
        if (maintenance.Count == 0) Error("maintenance", "missing", "Build the deliberate maintenance network before final validation.");
        var serviceAccess = new HashSet<Cell>(maintenance); serviceAccess.UnionWith(commonFloor); serviceAccess.UnionWith(commonDoors);
        foreach (var component in StationValidator.Components(serviceAccess))
            if (!component.Any(cell => Neighbors(cell).Any(publicTiles.Contains)))
                Error("maintenance", "maintenance-reachability", "A maintenance segment has no route to public circulation through passages or declared hall-connected room doors.");
        var outside = ExteriorSpace(c, envelope);
        foreach (var definition in definitions.Values.Where(d => d.Exterior))
            if (!HasDockingStrip(c, territories[definition.Id], expectedHull, outside, exterior))
                Error("growth", "docking-clearance", $"{definition.Id}: no reserved, straight three-tile docking approach reaches the canvas exterior.");
        foreach (var component in StationValidator.Components(exterior))
            if (!component.Any(cell => cell.X == 0 || cell.Y == 0 || cell.X == c.Width - 1 || cell.Y == c.Height - 1))
                Error("growth", "docking-fragment", "An exterior reservation does not reach the outside edge of the planning canvas.");
        diagnostics.AddRange(MaintenancePlanner.Validate(project));
        diagnostics.AddRange(StationHierarchyValidator.Validate(project));
        result.Metrics = Metrics(project, floors, territories, publicWalk, distancesByDepartment, outside, unallocated, actual.Count);
        result.Success = diagnostics.All(d => d.Severity != "error");
        diagnostics.Add(new("validation", "scope", "Station and program geometry checked. Equipment entities, operational solar power, window/airlock entities, pressure sealing, role credentials, utilities and SS14 materialization remain later stages.", "info"));
        return result;

        void ErrorOnce(string stage, string code, string message)
        { if (!diagnostics.Any(d => d.Code == code)) Error(stage, code, message); }

        (List<SpatialPort> Ports, HashSet<Cell> Walk, Dictionary<string, Dictionary<Cell, int>> Distances) Network(int methods)
        {
            if (networks.TryGetValue(methods, out var existing)) return existing;
            // A method authorizes only its network and matching entrance kind. Department
            // interiors and unselected public/service layers never become routing shortcuts.
            var ports = p.Ports.Where(port => port.Kind == "public" && (methods & 1) != 0 ||
                port.Kind == "maintenance" && (methods & 2) != 0).ToList();
            var walk = new HashSet<Cell>();
            if ((methods & 1) != 0) walk.UnionWith(publicTiles);
            if ((methods & 2) != 0) walk.UnionWith(maintenance);
            if ((methods & 2) != 0)
            {
                walk.UnionWith(commonFloor);
                walk.UnionWith(p.CommonMaintenanceRooms.SelectMany(room => room.MaintenanceDoorCells));
                if ((methods & 1) != 0) walk.UnionWith(p.CommonMaintenanceRooms.SelectMany(room => room.PublicDoorCells));
            }
            walk.UnionWith(ports.SelectMany(port => port.Cells));
            var distances = ids.ToDictionary(id => id,
                id => Distances(ports.Where(port => port.Department == id).SelectMany(port => port.Cells), walk), StringComparer.Ordinal);
            var resultNetwork = (ports, walk, distances);
            networks[methods] = resultNetwork;
            return resultNetwork;
        }
    }

    private static IEnumerable<Cell> Neighbors(Cell cell) => Cell.Directions.Select(direction => cell + direction);
    private static bool Pair(string a, string b, string x, string y) => a == x && b == y || a == y && b == x;
    private static bool IsStraight(HashSet<Cell> cells, Cell center, Cell normal)
    {
        var tangent = new Cell(-normal.Y, normal.X);
        return cells.SetEquals(Enumerable.Range(-(cells.Count - 1) / 2, cells.Count).Select(i => center + tangent * i));
    }
    private static HashSet<Cell> CoveredBySquares(HashSet<Cell> cells, int width)
    {
        var covered = new HashSet<Cell>();
        foreach (var p in cells)
        {
            var square = new Rect(p.X, p.Y, width, width).Cells().ToArray();
            if (square.All(cells.Contains)) covered.UnionWith(square);
        }
        return covered;
    }
    private static HashSet<Cell> InnerHull(HashSet<Cell> envelope, int thickness)
    {
        var hull = envelope.Where(p => Neighbors(p).Any(q => !envelope.Contains(q))).ToHashSet();
        for (var i = 1; i < thickness; i++) hull.UnionWith(hull.SelectMany(Neighbors).Where(envelope.Contains).ToArray());
        return hull;
    }
    private static Dictionary<Cell, int> Distances(IEnumerable<Cell> starts, HashSet<Cell> walkable)
    {
        var distances = starts.Where(walkable.Contains).Distinct().ToDictionary(p => p, _ => 0);
        var queue = new Queue<Cell>(distances.Keys);
        while (queue.TryDequeue(out var current))
        foreach (var next in Neighbors(current))
            if (walkable.Contains(next) && distances.TryAdd(next, distances[current] + 1)) queue.Enqueue(next);
        return distances;
    }
    private static HashSet<Cell> ExteriorSpace(StationGenerationConfig c, HashSet<Cell> envelope)
    {
        var empty = new Rect(0, 0, c.Width, c.Height).Cells().Where(p => !envelope.Contains(p)).ToHashSet();
        var perimeter = empty.Where(p => p.X == 0 || p.Y == 0 || p.X == c.Width - 1 || p.Y == c.Height - 1);
        return Distances(perimeter, empty).Keys.ToHashSet();
    }
    private static bool HasDockingStrip(StationGenerationConfig c, HashSet<Cell> territory, HashSet<Cell> hull, HashSet<Cell> outside, HashSet<Cell> reserved)
    {
        foreach (var anchor in territory.Where(hull.Contains))
        foreach (var normal in Cell.Directions)
        {
            if (!outside.Contains(anchor + normal)) continue;
            var tangent = new Cell(-normal.Y, normal.X); var valid = true; var any = false;
            for (var distance = 1; distance <= Math.Max(c.Width, c.Height); distance++)
            {
                var center = anchor + normal * distance;
                if (!StationGenerator.Inside(c, center)) break;
                for (var width = -1; width <= 1; width++)
                {
                    var cell = center + tangent * width;
                    if (!outside.Contains(cell) || !reserved.Contains(cell)) { valid = false; break; }
                }
                if (!valid) break;
                any = true;
            }
            if (valid && any) return true;
        }
        return false;
    }

    private static LayoutMetrics Metrics(StationProject project, Dictionary<string, HashSet<Cell>> floors,
        Dictionary<string, HashSet<Cell>> territories, HashSet<Cell> publicWalk,
        Dictionary<string, Dictionary<Cell, int>> distances, HashSet<Cell> outside, int unallocated, int filled)
    {
        var p = project.SpatialPlan!; var t = project.Topology!;
        var publicFloor = p.PublicCorridors.ToHashSet();
        var longest = 0;
        foreach (var source in distances)
        foreach (var target in p.Ports.Where(port => port.Kind == "public" && port.Department != source.Key))
        {
            var travel = target.Cells.Where(source.Value.ContainsKey).Select(cell => source.Value[cell]).DefaultIfEmpty(0).Min();
            longest = Math.Max(longest, travel);
        }
        var nodes = t.Nodes.Order(StringComparer.Ordinal).ToList();
        var graph = nodes.Select(id => t.Edges.Where(e => e.Hard && (e.From == id || e.To == id))
            .Select(e => nodes.IndexOf(e.From == id ? e.To : e.From)).Distinct().ToList()).ToList();
        var graphCuts = GraphCuts(graph);
        var publicCells = StationGenerator.Sorted(publicWalk);
        var indices = publicCells.Select((cell, index) => (cell, index)).ToDictionary(x => x.cell, x => x.index);
        var tileCuts = GraphCuts(publicCells.Select(cell => Neighbors(cell).Where(indices.ContainsKey).Select(next => indices[next]).ToList()).ToList());
        var canonical = new
        {
            nodes, Edges = t.Edges.OrderBy(e => e.Id, StringComparer.Ordinal).ToList(),
            Envelope = StationGenerator.Sorted(p.Envelope),
            Seeds = p.Seeds.OrderBy(s => s.Id, StringComparer.Ordinal).Select(seed => new
            {
                seed.Id, seed.Position, seed.TargetArea, seed.Pinned, seed.AllowMaintenanceAccess,
                CoreFootprint = StationGenerator.Sorted(seed.CoreFootprint ?? [])
            }).ToList(),
            Regions = p.Regions.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => new { r.Id, r.Seed, r.TargetArea,
                Territory = StationGenerator.Sorted(r.Territory), Cells = StationGenerator.Sorted(r.Cells) }).ToList(),
            PublicReservations = StationGenerator.Sorted(p.PublicReservations), ServiceReservations = StationGenerator.Sorted(p.ServiceReservations),
            InitialServiceReservations = StationGenerator.Sorted(p.InitialServiceReservations),
            Public = StationGenerator.Sorted(p.PublicCorridors), Maintenance = StationGenerator.Sorted(p.Maintenance),
            Walls = StationGenerator.Sorted(p.Walls), Hull = StationGenerator.Sorted(p.Hull), Exterior = StationGenerator.Sorted(p.ReservedExterior),
            Structural = StationGenerator.Sorted(p.StructuralReserve),
            Departments = project.Config.Departments.OrderBy(d => d.Id, StringComparer.Ordinal),
            Settings = project.Config.Spatial, project.Config.MaintenanceWidth, project.Config.CorridorWidth, project.Config.HullThickness,
            HierarchyRelations = project.Config.HierarchyRelations.OrderBy(e => e.Id, StringComparer.Ordinal),
            p.Hierarchy, p.DeepMaintenanceRooms, p.CommonMaintenanceRooms, p.SolarInstallations, p.MaintenanceSpaceAccess,
            Ports = p.Ports.OrderBy(port => port.Department, StringComparer.Ordinal).ThenBy(port => port.Kind, StringComparer.Ordinal)
                .ThenBy(port => port.Center.Y).ThenBy(port => port.Center.X).Select(port => new { port.Department, port.OtherDepartment,
                    port.Kind, port.Center, port.Width, port.Access, Cells = StationGenerator.Sorted(port.Cells) }).ToList()
        };
        var score = t.Edges.Sum(edge =>
        {
            var source = p.Seeds.Single(seed => seed.Id == edge.From).Position;
            var target = p.Seeds.Single(seed => seed.Id == edge.To).Position;
            var distance = source.Distance(target);
            return edge.Weight * (edge.Priority == "avoided" ? Math.Max(0, (edge.MaxDistance ?? 30) - distance) : distance);
        });
        return new LayoutMetrics
        {
            EnvelopeArea = p.Envelope.Count, InteriorArea = floors.Values.Sum(cells => cells.Count), PublicArea = p.PublicCorridors.Count,
            MaintenanceArea = p.Maintenance.Count, HullArea = p.Hull.Count, StructuralArea = p.StructuralReserve.Count,
            UnallocatedArea = unallocated, FilledArea = filled, LongestEntranceTravel = longest,
            TopologyBridges = graphCuts.Bridges, TopologyArticulations = graphCuts.Articulations, CirculationArticulations = tileCuts.Articulations,
            NonRectangularRegions = territories.Count(kv => kv.Value.Count > 0 &&
                kv.Value.Count != (kv.Value.Max(cell => cell.X) - kv.Value.Min(cell => cell.X) + 1) * (kv.Value.Max(cell => cell.Y) - kv.Value.Min(cell => cell.Y) + 1)),
            MaintenanceAccessPoints = StationValidator.Components(p.Maintenance.Where(cell => Neighbors(cell).Any(publicFloor.Contains)).ToHashSet()).Count,
            DeepMaintenanceRooms = p.DeepMaintenanceRooms.Count, SolarInstallations = p.SolarInstallations.Count,
            CommonMaintenanceRooms = p.CommonMaintenanceRooms.Count,
            MaintenanceSpaceAccess = p.MaintenanceSpaceAccess.Count, SubdepartmentAreas = p.Hierarchy?.Subdepartments.Count ?? 0,
            PlannedRoomFeatures = p.Hierarchy?.RoomFeatures.Count ?? 0,
            TouchingDepartmentPairs = territories.Keys.SelectMany((id, i) => territories.Keys.Skip(i + 1).Select(other => (id, other)))
                .Count(pair => territories[pair.id].Any(cell => Neighbors(cell).Any(territories[pair.other].Contains))),
            ExteriorFrontageByDepartment = territories.ToDictionary(kv => kv.Key, kv => kv.Value.Count(cell => Neighbors(cell).Any(outside.Contains)), StringComparer.Ordinal),
            Score = Math.Round(score, 3), Hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ProjectJson.Serialize(canonical))))
        };
    }

    private static (int Articulations, int Bridges) GraphCuts(List<List<int>> graph)
    {
        var n = graph.Count; var discovered = new int[n]; var low = new int[n];
        var parent = Enumerable.Repeat(-1, n).ToArray(); var children = new int[n]; var next = new int[n];
        var cuts = new HashSet<int>(); var time = 0; var bridges = 0;
        for (var root = 0; root < n; root++)
        {
            if (discovered[root] != 0) continue;
            var stack = new Stack<int>(); stack.Push(root); discovered[root] = low[root] = ++time;
            while (stack.TryPeek(out var v))
            {
                if (next[v] < graph[v].Count)
                {
                    var u = graph[v][next[v]++];
                    if (discovered[u] == 0) { parent[u] = v; children[v]++; discovered[u] = low[u] = ++time; stack.Push(u); }
                    else if (u != parent[v]) low[v] = Math.Min(low[v], discovered[u]);
                    continue;
                }
                stack.Pop(); var p = parent[v];
                if (p == -1) { if (children[v] > 1) cuts.Add(v); continue; }
                low[p] = Math.Min(low[p], low[v]);
                if (low[v] > discovered[p]) bridges++;
                if (parent[p] != -1 && low[v] >= discovered[p]) cuts.Add(p);
            }
        }
        return (cuts.Count, bridges);
    }
}
