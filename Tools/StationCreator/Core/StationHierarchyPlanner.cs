namespace Content.StationGeneration;

public sealed partial class StationSpatialPlan
{
    public StationHierarchyPlan Hierarchy { get; set; } = new();
}

/// <summary>Planned subdivisions of existing department floor, before entity materialization.</summary>
public sealed class StationHierarchyPlan
{
    public List<SubdepartmentRegion> Subdepartments { get; set; } = [];
    public List<RoomFeaturePlacement> RoomFeatures { get; set; } = [];
    public List<DepartmentCirculation> InternalCirculation { get; set; } = [];
}

public sealed class SubdepartmentRegion
{
    public string Id { get; set; } = "";
    public string Department { get; set; } = "";
    public string HostDepartment { get; set; } = "";
    public bool IsSatellite { get; set; }
    public Cell Seed { get; set; }
    /// <summary>Connected subdivision territory; room partitions are included in its ownership.</summary>
    public List<Cell> Cells { get; set; } = [];
}

public sealed class RoomFeaturePlacement
{
    public string Id { get; set; } = "";
    public string Definition { get; set; } = "";
    public string Department { get; set; } = "";
    public string Subdepartment { get; set; } = "";
    public string HostDepartment { get; set; } = "";
    public string Kind { get; set; } = "room";
    /// <summary>Clear usable footprint, excluding its planned partitions and doorway.</summary>
    public List<Cell> Cells { get; set; } = [];
    /// <summary>Planned interior partitions overlaying the parent floor; existing outer walls are reused.</summary>
    public List<Cell> Walls { get; set; } = [];
    public List<Cell> Doors { get; set; } = [];
    public List<HierarchyAccessRoute> AccessRoutes { get; set; } = [];
}

public sealed class HierarchyAccessRoute
{
    public string From { get; set; } = "";
    public Cell Door { get; set; }
    /// <summary>Actual contiguous floor route from the declared origin to a planned doorway.</summary>
    public List<Cell> Cells { get; set; } = [];
}

public sealed class DepartmentCirculation
{
    public string Department { get; set; } = "";
    public List<Cell> Cells { get; set; } = [];
}

/// <summary>
/// Subdivides the grown station floor. Required rooms are packed before flexible
/// zones, with connected internal floor retained around their planned partitions.
/// Satellites use explicitly named, compatible host allocations and are forbidden
/// for subdivisions that require access wholly through their parent department.
/// </summary>
public static class StationHierarchyPlanner
{
    private const int SearchBudget = 6000;

    public static List<Diagnostic> Build(StationProject project)
    {
        var diagnostics = new List<Diagnostic>();
        var plan = project.SpatialPlan;
        if (plan == null || plan.Regions.Count == 0)
            return [new("hierarchy", "missing-floor", "Grow department floors before planning subdivisions and room programs.")];
        var output = new StationHierarchyPlan();
        plan.Hierarchy = output;
        var definitions = project.Config.Departments.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var hosts = plan.Regions.ToDictionary(r => r.Id, r => new Host(r.Id, r.Cells.ToHashSet(),
            plan.Ports.Where(p => p.Department == r.Id || p.OtherDepartment == r.Id)
                .SelectMany(p => p.Cells).SelectMany(Adjacent).Where(r.Cells.Contains).ToHashSet()), StringComparer.Ordinal);
        var structural = plan.Walls.Concat(plan.Hull).ToHashSet();
        var programs = project.Config.Departments.SelectMany(department => department.Subdepartments.Select(sub => (department, sub)))
            .OrderByDescending(p => p.sub.RequireInternalAccess || !p.department.AllowSplit || p.department.Grouping == "grouped")
            .ThenByDescending(p => p.sub.Rooms.Sum(r => (long) r.MinCount * r.MinArea))
            .ThenBy(p => p.department.Id, StringComparer.Ordinal).ThenBy(p => p.sub.Id, StringComparer.Ordinal).ToList();
        foreach (var (department, sub) in programs)
        {
            if (!hosts.TryGetValue(department.Id, out var own))
            {
                diagnostics.Add(new("hierarchy", "missing-parent", $"{department.Id}/{sub.Id}: its parent department has no grown floor."));
                continue;
            }
            var allowSatellite = department.AllowSplit && sub.AllowSplit && !sub.RequireInternalAccess && department.Grouping != "grouped";
            var choices = new List<Host> { own };
            if (allowSatellite)
                choices.AddRange(hosts.Values.Where(h => h.Id != own.Id && definitions[h.Id].Access == department.Access)
                    .OrderBy(h => h.Floor.Count - h.Zones.Sum(z => z.Cells.Count))
                    .ThenBy(h => h.Id, StringComparer.Ordinal));
            // Independent means dispersion is allowed immediately, while still preferring
            // available floor in the parent if no compatible satellite can fit.
            if (department.Grouping == "independent" && choices.Count > 1)
                choices = [.. choices.Skip(1), own];
            var fitted = false;
            foreach (var host in choices)
            {
                if (!TrySubdivision(project, department, sub, host, structural, out var zone, out var rooms)) continue;
                host.Zones.Add(zone);
                host.Rooms.AddRange(rooms);
                output.Subdepartments.Add(zone);
                output.RoomFeatures.AddRange(rooms);
                fitted = true;
                if (zone.IsSatellite)
                    diagnostics.Add(new("hierarchy", "satellite", $"{zone.Id} occupies an explicit satellite in the {host.Id} floor allocation.", "info"));
                break;
            }
            if (!fitted)
                diagnostics.Add(new("hierarchy", "program-fit", $"{department.Id}/{sub.Id}: cannot fit its minimum usable area, required room footprints, partitions and connected internal access. Increase its parent floor budget or change its grouping/split policy."));
        }
        // Grow each successful subdivision toward its soft target only after all
        // minimum programs have had an opportunity to claim floor.
        foreach (var host in hosts.Values)
        {
            foreach (var zone in host.Zones)
            {
                var definition = definitions[zone.Department].Subdepartments.Single(s => StationHierarchy.Path(zone.Department, s.Id) == zone.Id);
                var occupied = host.Zones.Where(z => z != zone).SelectMany(z => z.Cells).ToHashSet();
                var cells = zone.Cells.ToHashSet();
                var roomWalls = host.Rooms.SelectMany(r => r.Walls).ToHashSet();
                ExpandZone(cells, host.Floor, occupied, Math.Min(definition.TargetArea, definition.MaxArea), roomWalls,
                    p => RelationCost(project, zone.Id, [p], []));
                zone.Cells = Sorted(cells);
            }
            output.InternalCirculation.Add(new DepartmentCirculation { Department = host.Id, Cells = Sorted(Circulation(host.Floor, host.Rooms)) });
        }
        BuildAccessRoutes(project, diagnostics);
        return diagnostics;
    }

    private static bool TrySubdivision(StationProject project, DepartmentDefinition department, SubdepartmentDefinition sub,
        Host host, HashSet<Cell> structural, out SubdepartmentRegion zone, out List<RoomFeaturePlacement> rooms)
    {
        zone = new SubdepartmentRegion();
        rooms = [];
        var occupiedZones = host.Zones.SelectMany(z => z.Cells).ToHashSet();
        var available = host.Floor.Except(occupiedZones).ToHashSet();
        if (available.Count < sub.MinArea) return false;
        var specifications = sub.Rooms.SelectMany(room => Enumerable.Range(1, room.MinCount).Select(index => (room, index)))
            .OrderByDescending(r => r.room.MinArea).ThenBy(r => r.room.Id, StringComparer.Ordinal).ToList();
        var center = available.OrderBy(p => p.X).ThenBy(p => p.Y).ElementAt(available.Count / 2);
        var budget = SearchBudget;
        if (!PackRooms(project, 0, specifications, host, occupiedZones, structural, department.Id, sub.Id, center, rooms, ref budget)) return false;
        var zoneCells = rooms.SelectMany(r => r.Cells.Concat(r.Walls).Concat(r.Doors)).ToHashSet();
        // Reserve a floor tile beyond every door as part of the subdivision so a
        // shared room has a genuine access origin within its own subdivision.
        var allRooms = host.Rooms.Concat(rooms).ToList();
        var circulation = Circulation(host.Floor, allRooms);
        foreach (var door in rooms.SelectMany(r => r.Doors))
        {
            var outside = Adjacent(door).Where(circulation.Contains).Where(p => !occupiedZones.Contains(p))
                .OrderBy(p => p.Y).ThenBy(p => p.X).FirstOrDefault(door);
            zoneCells.Add(outside);
        }
        if (zoneCells.Count == 0)
        {
            var scope = StationHierarchy.Path(department.Id, sub.Id);
            var pendingRooms = rooms;
            var anchor = available.Where(circulation.Contains).OrderBy(p => p.Distance(center) * .1 + RelationCost(project, scope, [p], pendingRooms))
                .ThenBy(p => p.Y).ThenBy(p => p.X).FirstOrDefault();
            if (!available.Contains(anchor)) return false;
            zoneCells.Add(anchor);
        }
        if (!Join(zoneCells, available)) return false;
        var roomWalls = allRooms.SelectMany(r => r.Walls).ToHashSet();
        if (zoneCells.Count(p => !roomWalls.Contains(p)) > sub.MaxArea) return false;
        var growthRooms = rooms;
        ExpandZone(zoneCells, host.Floor, occupiedZones, sub.MinArea, roomWalls,
            p => RelationCost(project, StationHierarchy.Path(department.Id, sub.Id), [p], growthRooms));
        if (zoneCells.Count(p => !roomWalls.Contains(p)) < sub.MinArea) return false;
        zone = new SubdepartmentRegion
        {
            Id = StationHierarchy.Path(department.Id, sub.Id), Department = department.Id, HostDepartment = host.Id,
            IsSatellite = host.Id != department.Id, Seed = Sorted(zoneCells)[0], Cells = Sorted(zoneCells)
        };
        return true;
    }

    private static bool PackRooms(StationProject project, int index, List<(RoomFeatureDefinition room, int index)> specifications, Host host,
        HashSet<Cell> occupiedZones, HashSet<Cell> structural, string department, string subdepartment, Cell center,
        List<RoomFeaturePlacement> placed, ref int budget)
    {
        if (index == specifications.Count) return true;
        if (budget <= 0) return false;
        var (definition, instance) = specifications[index];
        var existing = host.Rooms.Concat(placed).ToList();
        var blockedFloor = existing.SelectMany(r => r.Cells).ToHashSet();
        var blockedWalls = existing.SelectMany(r => r.Walls).ToHashSet();
        var doors = existing.SelectMany(r => r.Doors).ToHashSet();
        var candidates = new List<(RoomFeaturePlacement room, double cost)>();
        var baseCirculation = Circulation(host.Floor, existing);
        foreach (var (width, height) in Shapes(definition.MinArea, definition.MaxArea))
        foreach (var anchor in host.Floor.OrderBy(p => p.Y).ThenBy(p => p.X))
        {
            var floor = new Rect(anchor.X, anchor.Y, width, height).Cells().ToHashSet();
            if (!host.Floor.IsSupersetOf(floor) || floor.Overlaps(occupiedZones) || floor.Overlaps(blockedFloor) || floor.Overlaps(blockedWalls) || floor.Overlaps(doors) || floor.Overlaps(host.Entrances)) continue;
            var ring = definition.Kind == "room" ? new Rect(anchor.X - 1, anchor.Y - 1, width + 2, height + 2).Cells().Except(floor).ToHashSet() : [];
            if (ring.Any(p => !host.Floor.Contains(p) && !structural.Contains(p)) || ring.Overlaps(blockedFloor) || ring.Overlaps(doors) || ring.Overlaps(occupiedZones) || ring.Overlaps(host.Entrances)) continue;
            var walls = ring.Where(host.Floor.Contains).ToHashSet();
            var free = baseCirculation.ToHashSet();
            free.ExceptWith(floor);
            free.ExceptWith(walls);
            if (!Connected(free)) continue;
            var openings = definition.Kind == "room"
                ? walls.Where(p => Adjacent(p).Any(floor.Contains) && Adjacent(p).Any(free.Contains)).ToList()
                : floor.SelectMany(Adjacent).Where(free.Contains).Distinct().ToList();
            if (openings.Count == 0) continue;
            var door = openings.OrderBy(p => p.Distance(center)).ThenBy(p => p.Y).ThenBy(p => p.X).First();
            walls.Remove(door);
            free.Add(door);
            if (!Connected(free)) continue;
            var placement = new RoomFeaturePlacement
            {
                Id = $"{StationHierarchy.Path(department, subdepartment, definition.Id)}#{instance}",
                Definition = StationHierarchy.Path(department, subdepartment, definition.Id), Department = department,
                Subdepartment = StationHierarchy.Path(department, subdepartment), HostDepartment = host.Id,
                Kind = definition.Kind, Cells = Sorted(floor), Walls = Sorted(walls), Doors = [door]
            };
            var cost = walls.Count * 12 + floor.Count + anchor.Distance(center) + RelationCost(project, placement.Definition, floor, placed);
            candidates.Add((placement, cost));
        }
        foreach (var candidate in candidates.OrderBy(c => c.cost).ThenBy(c => c.room.Cells[0].Y).ThenBy(c => c.room.Cells[0].X).Take(100))
        {
            if (--budget < 0) return false;
            placed.Add(candidate.room);
            if (PackRooms(project, index + 1, specifications, host, occupiedZones, structural, department, subdepartment,
                candidate.room.Cells[0], placed, ref budget)) return true;
            placed.RemoveAt(placed.Count - 1);
        }
        return false;
    }

    private static double RelationCost(StationProject project, string scope, IEnumerable<Cell> candidateCells, IEnumerable<RoomFeaturePlacement> pending)
    {
        var cells = candidateCells.ToList();
        var hierarchy = project.SpatialPlan!.Hierarchy;
        var pendingRooms = pending.ToList();
        var cost = 0d;
        foreach (var edge in project.Config.HierarchyRelations)
        {
            var from = scope == edge.From || scope.StartsWith(edge.From + "/", StringComparison.Ordinal);
            var to = scope == edge.To || scope.StartsWith(edge.To + "/", StringComparison.Ordinal);
            if (from == to) continue;
            var other = from ? edge.To : edge.From;
            IEnumerable<Cell> target = other.Split('/').Length switch
            {
                1 => project.SpatialPlan.Ports.Where(p => p.Department == other || p.OtherDepartment == other)
                    .SelectMany(p => p.Cells).SelectMany(Adjacent),
                2 => hierarchy.Subdepartments.Where(s => s.Id == other).SelectMany(s => s.Cells),
                _ => hierarchy.RoomFeatures.Concat(pendingRooms).Where(r => r.Definition == other).SelectMany(r => r.Cells)
            };
            var targets = target.ToList();
            if (targets.Count == 0) continue;
            var distance = cells.Min(c => targets.Min(c.Distance));
            var weight = edge.Weight * (edge.Priority == "required" ? 12 : 3);
            if (edge.Priority == "avoided") cost -= weight * Math.Min(distance, edge.MaxDistance ?? 40);
            else cost += weight * (edge.Relation == "adjacency" ? Math.Max(0, distance - 2) * 4 : distance);
        }
        return cost;
    }

    private static IEnumerable<(int width, int height)> Shapes(int minArea, int maxArea)
    {
        var side = (int) Math.Ceiling(Math.Sqrt(minArea));
        var shapes = new HashSet<(int, int)>();
        for (var width = Math.Max(2, side - 2); width <= side + 2; width++)
        {
            var height = Math.Max(2, (int) Math.Ceiling((double) minArea / width));
            if (width * height <= maxArea && Math.Max(width, height) <= Math.Min(width, height) * 2)
            {
                shapes.Add((width, height));
                shapes.Add((height, width));
            }
        }
        return shapes.OrderBy(s => s.Item1 * s.Item2).ThenBy(s => Math.Abs(s.Item1 - s.Item2)).ThenBy(s => s.Item1);
    }

    private static void BuildAccessRoutes(StationProject project, List<Diagnostic> diagnostics)
    {
        var plan = project.SpatialPlan!;
        var hierarchy = plan.Hierarchy;
        var circulation = hierarchy.InternalCirculation.ToDictionary(c => c.Department, c => c.Cells.ToHashSet(), StringComparer.Ordinal);
        foreach (var room in hierarchy.RoomFeatures)
        {
            var parts = room.Definition.Split('/');
            var definition = project.Config.Departments.Single(d => d.Id == parts[0]).Subdepartments.Single(s => s.Id == parts[1]).Rooms.Single(r => r.Id == parts[2]);
            IEnumerable<string> origins = definition.AccessibleFrom.Count == 0 ? new[] { room.Subdepartment } : definition.AccessibleFrom;
            foreach (var origin in origins.Distinct(StringComparer.Ordinal))
            {
                var originDepartment = OriginHost(hierarchy, origin);
                var walkable = circulation[room.HostDepartment].ToHashSet();
                if (originDepartment != room.HostDepartment)
                {
                    walkable.UnionWith(circulation.Values.SelectMany(c => c));
                    walkable.UnionWith(plan.PublicCorridors);
                    walkable.UnionWith(plan.Ports.Where(p => p.Kind is "public" or "adjacency").SelectMany(p => p.Cells));
                }
                var sources = OriginCells(project, origin, circulation).Where(walkable.Contains).ToHashSet();
                var path = Path(sources, room.Doors.ToHashSet(), walkable);
                if (path == null)
                    diagnostics.Add(new("hierarchy", "room-access", $"{room.Definition}: no physical floor route from {origin} to its planned doorway."));
                else room.AccessRoutes.Add(new HierarchyAccessRoute { From = origin, Door = path[^1], Cells = path });
            }
        }
    }

    internal static string OriginHost(StationHierarchyPlan hierarchy, string origin)
    {
        if (!origin.Contains('/')) return origin;
        var zone = hierarchy.Subdepartments.FirstOrDefault(s => s.Id == string.Join('/', origin.Split('/').Take(2)));
        return zone?.HostDepartment ?? origin.Split('/')[0];
    }

    internal static IEnumerable<Cell> OriginCells(StationProject project, string origin, Dictionary<string, HashSet<Cell>> circulation)
    {
        var hierarchy = project.SpatialPlan!.Hierarchy;
        var parts = origin.Split('/');
        if (parts.Length == 1)
        {
            if (!circulation.TryGetValue(origin, out var floor)) return [];
            // Begin department-origin routes at real department entrances, ensuring
            // the route demonstrates internal access from the parent department.
            var starts = project.SpatialPlan.Ports.Where(p => p.Department == origin || p.OtherDepartment == origin)
                .SelectMany(p => p.Cells).SelectMany(Adjacent).Where(floor.Contains).Distinct().ToList();
            return starts.Count > 0 ? starts : floor;
        }
        if (parts.Length == 2) return hierarchy.Subdepartments.FirstOrDefault(s => s.Id == origin)?.Cells ?? [];
        return hierarchy.RoomFeatures.Where(r => r.Definition == origin).SelectMany(r => r.Doors);
    }

    private static void ExpandZone(HashSet<Cell> cells, HashSet<Cell> floor, HashSet<Cell> occupied, int target, HashSet<Cell> walls,
        Func<Cell, double>? preference = null)
    {
        while (cells.Count(p => !walls.Contains(p)) < target)
        {
            var frontier = cells.SelectMany(Adjacent).Where(floor.Contains).Where(p => !occupied.Contains(p) && !cells.Contains(p)).Distinct()
                .OrderBy(p => -3 * Adjacent(p).Count(cells.Contains) + (preference?.Invoke(p) ?? 0)).ThenBy(p => p.Y).ThenBy(p => p.X).ToList();
            if (frontier.Count == 0) break;
            cells.Add(frontier[0]);
        }
    }

    private static bool Join(HashSet<Cell> cells, HashSet<Cell> available)
    {
        if (cells.Count == 0) return false;
        var connected = Flood(cells.First(), cells);
        while (connected.Count < cells.Count)
        {
            var route = Path(connected, cells.Except(connected).ToHashSet(), available);
            if (route == null) return false;
            cells.UnionWith(route);
            connected = Flood(cells.First(), cells);
        }
        return true;
    }

    internal static List<Cell>? Path(HashSet<Cell> starts, HashSet<Cell> goals, HashSet<Cell> walkable)
    {
        var queue = new Queue<Cell>();
        var previous = new Dictionary<Cell, Cell>();
        foreach (var start in Sorted(starts.Where(walkable.Contains))) { queue.Enqueue(start); previous[start] = start; }
        while (queue.TryDequeue(out var cell))
        {
            if (goals.Contains(cell))
            {
                var result = new List<Cell> { cell };
                while (previous[cell] != cell) { cell = previous[cell]; result.Add(cell); }
                result.Reverse();
                return result;
            }
            foreach (var next in Adjacent(cell))
                if (walkable.Contains(next) && previous.TryAdd(next, cell)) queue.Enqueue(next);
        }
        return null;
    }

    private static HashSet<Cell> Circulation(HashSet<Cell> floor, IEnumerable<RoomFeaturePlacement> rooms)
    {
        var list = rooms.ToList();
        var result = floor.Except(list.SelectMany(r => r.Cells.Concat(r.Walls))).ToHashSet();
        result.UnionWith(list.SelectMany(r => r.Doors));
        return result;
    }

    private static HashSet<Cell> Flood(Cell start, HashSet<Cell> cells)
    {
        var seen = new HashSet<Cell> { start };
        var queue = new Queue<Cell>(); queue.Enqueue(start);
        while (queue.TryDequeue(out var cell))
            foreach (var next in Adjacent(cell)) if (cells.Contains(next) && seen.Add(next)) queue.Enqueue(next);
        return seen;
    }

    private static bool Connected(HashSet<Cell> cells) => cells.Count > 0 && Flood(cells.First(), cells).Count == cells.Count;
    private static IEnumerable<Cell> Adjacent(Cell cell) => Cell.Directions.Select(d => cell + d);
    private static List<Cell> Sorted(IEnumerable<Cell> cells) => cells.Distinct().OrderBy(p => p.Y).ThenBy(p => p.X).ToList();

    private sealed class Host(string id, HashSet<Cell> floor, HashSet<Cell> entrances)
    {
        public string Id { get; } = id;
        public HashSet<Cell> Floor { get; } = floor;
        public HashSet<Cell> Entrances { get; } = entrances;
        public List<SubdepartmentRegion> Zones { get; } = [];
        public List<RoomFeaturePlacement> Rooms { get; } = [];
    }
}
