using System.Security.Cryptography;
using System.Text;

namespace Content.StationGeneration;

public static class StationValidator
{
    public static List<Diagnostic> CheckTopology(StationGenerationConfig c, StationTopology t)
    {
        var errors = new List<Diagnostic>();
        void Error(string code, string message) => errors.Add(new("topology", code, message));
        var expected = c.Departments.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        if (t.Nodes == null || t.Edges == null)
        {
            Error("missing", "Topology node and edge lists are required.");
            return errors;
        }
        if (!expected.SetEquals(t.Nodes) || t.Nodes.Count != expected.Count)
            Error("nodes", "Topology must contain every included department exactly once.");
        if (t.Nodes.Count > 16 || t.Edges.Count > 512)
            Error("budget", "Topology supports at most 16 departments and 512 dependencies.");
        var edgeIds = new HashSet<string>(StringComparer.Ordinal);
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        var modes = new HashSet<string>(["publicHall", "staffPassage", "maintenance", "disposalShortcut", "mail"], StringComparer.Ordinal);
        foreach (var e in t.Edges)
        {
            if (e == null) { Error("edge", "Dependencies cannot be null."); continue; }
            if (!expected.Contains(e.From) || !expected.Contains(e.To) || e.From == e.To)
                Error("edge", "Dependencies require two distinct included departments.");
            if (e.Id == null || e.Id.Length > 100 || (e.Id.Length > 0 && !edgeIds.Add(e.Id)))
                Error("dependency-id", "Dependency IDs must be unique and at most 100 characters.");
            if (e.Origin is not ("template" or "custom"))
                Error("dependency-origin", "Dependency origin must be template or custom.");
            if (e.Priority is not ("required" or "desired" or "avoided"))
                Error("priority", "Dependency priority must be Required, Desired or Avoided.");
            if (!double.IsFinite(e.Weight) || e.Weight <= 0 || e.Weight > 1000)
                Error("weight", "Dependency weights must be finite numbers greater than 0 and at most 1000.");
            if (e.Description == null || e.Description.Length > 2000)
                Error("description", "Dependency descriptions must be at most 2000 characters.");
            if (e.Relation is not ("walking" or "adjacency" or "delivery" or "transport"))
                Error("relation", "Choose walking, adjacency, delivery or transport as the relationship.");
            if (e.Purpose is not ("crew" or "items" or "patients" or "waste"))
                Error("purpose", "Choose crew, items, patients or waste as the dependency purpose.");
            if (e.Direction is not ("both" or "forward"))
                Error("direction", "Dependency direction must be both or forward.");
            if (e.StrictAvoidance && e.Priority != "avoided")
                Error("strict-avoidance", "Never allow can only be set on Avoided dependencies.");
            if (e.MaxDistance is < 1 or > 4096)
                Error("distance", "Distance must be absent or between 1 and 4096 tiles.");
            if (e.AllowedModes == null || e.AllowedModes.Count == 0 || e.AllowedModes.Count > modes.Count ||
                e.AllowedModes.Distinct(StringComparer.Ordinal).Count() != e.AllowedModes.Count || e.AllowedModes.Any(m => !modes.Contains(m)))
                Error("modes", "Choose at least one distinct supported connection method.");
            else
            {
                if (e.AllowedModes.Contains("mail") && e.Purpose != "items")
                    Error("mode-purpose", "Mail transports items and cannot satisfy crew, patient or waste access.");
                if (e.AllowedModes.Contains("disposalShortcut") && e.Purpose is "patients" or "waste")
                    Error("mode-purpose", "Passenger disposal shortcuts support crew/items; patient access and waste routing need their own appropriate methods.");
                var a = e.From; var b = e.To;
                if (e.Direction == "both" && string.CompareOrdinal(a, b) > 0) (a, b) = (b, a);
                var signature = ProjectJson.Serialize(new { a, b, e.Priority, e.Relation, e.Purpose, e.Direction, Modes = e.AllowedModes.Order(StringComparer.Ordinal).ToList() });
                if (!signatures.Add(signature))
                    Error("duplicate-dependency", $"Duplicate {e.Priority} {e.Relation} dependency between {e.From} and {e.To}; edit its weight or description instead.");
            }
        }
        if (t.NodePositions != null)
            foreach (var (id, p) in t.NodePositions)
                if (!expected.Contains(id) || p.X is < 0 or > 256 || p.Y is < 0 or > 256)
                    Error("node-position", "Saved graph positions must reference included departments and stay within 0–256.");
        if (t.SuppressedTemplateIds == null || t.SuppressedTemplateIds.Count > 512 ||
            t.SuppressedTemplateIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 100 || !id.StartsWith("template-", StringComparison.Ordinal)) ||
            t.SuppressedTemplateIds.Distinct(StringComparer.Ordinal).Count() != t.SuppressedTemplateIds.Count)
            Error("suppressed-template", "Suppressed template IDs must be unique template-prefixed IDs, at most 100 characters each and 512 entries total.");
        if (errors.Count != 0) return errors;
        // Dependency graphs may be sparse: ordinary station circulation is an independent
        // requirement. Authors need not mark every connection Required merely to save a plan.
        foreach (var required in t.Edges.Where(e => e.Hard))
        {
            var matchingAvoidances = t.Edges.Where(avoided => avoided.Priority == "avoided" && avoided.StrictAvoidance &&
                Pair(avoided.From, avoided.To, required.From, required.To) && avoided.Relation == required.Relation &&
                avoided.Purpose == required.Purpose &&
                (avoided.Direction == "both" || required.Direction == "both" || avoided.From == required.From)).ToList();
            var prohibitedModes = matchingAvoidances.SelectMany(e => e.AllowedModes).ToHashSet(StringComparer.Ordinal);
            // Allowed methods are alternatives: an avoided hallway does not contradict
            // a delivery requirement that can still be fulfilled by an allowed mail route.
            if (matchingAvoidances.Count > 0 && (required.Relation == "adjacency" || required.AllowedModes.All(prohibitedModes.Contains)))
                Error("conflicting-dependency", $"Required and Never allow dependencies conflict between {required.From} and {required.To} for {required.Relation}/{required.Purpose}.");
        }
        // Department relationship lists are legacy defaults expanded by GenerateTopology.
        // Once an explicit graph exists, its dependencies are the sole source of truth:
        // deleting or downgrading a dependency must not leave a hidden requirement here.
        return errors;
    }

    /// <summary>
    /// Conceptual topology can describe future station systems. The old rectangle preview
    /// must refuse requirements it cannot implement rather than call a hallway a mail route.
    /// </summary>
    public static List<Diagnostic> CheckPrototypeSupport(StationTopology t)
    {
        var diagnostics = new List<Diagnostic>();
        foreach (var edge in t.Edges)
        {
            if (edge.Hard && (edge.Relation is not ("walking" or "delivery") || !edge.AllowedModes.Contains("publicHall")))
                diagnostics.Add(new("circulation", "unsupported-fulfillment", $"{edge.From} → {edge.To}: this dependency is valid for topology planning, but the current geometry preview only implements public-hall walking/delivery. Its {edge.Relation} requirement cannot be fulfilled by the selected methods yet."));
            if (edge.Priority == "avoided" && edge.StrictAvoidance && edge.Relation != "adjacency")
                diagnostics.Add(new("circulation", "unsupported-fulfillment", $"{edge.From} → {edge.To}: Never allow {edge.Relation} is preserved in the topology, but is not implemented by the current geometry preview."));
        }
        return diagnostics;
    }

    public static GenerationResult Validate(StationProject project)
    {
        var c = project.Config;
        var result = new GenerationResult { Seed = c.Seed };
        var errors = result.Diagnostics;
        void Error(string stage, string code, string message) => errors.Add(new(stage, code, message));
        errors.AddRange(StationGenerator.CheckConfig(c));
        if (errors.Count != 0) return result;
        if (project.Topology == null || project.Layout == null)
        {
            Error("validation", "missing", "Topology and layout must exist before validation.");
            return result;
        }
        var t = project.Topology;
        var l = project.Layout;
        errors.AddRange(CheckTopology(c, t));
        if (errors.Count != 0) return result;
        errors.AddRange(CheckPrototypeSupport(t));
        if (errors.Count != 0) return result;
        if (l.Departments == null || l.Doors == null || l.PublicCorridors == null || l.Maintenance == null || l.Hull == null || l.ReservedExterior == null || l.Routes == null)
        {
            Error("validation", "layers", "All layout layer lists are required.");
            return result;
        }
        var definitions = c.Departments.ToDictionary(d => d.Id, StringComparer.Ordinal);
        if (l.Departments.Any(d => d == null) || l.Departments.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() != l.Departments.Count ||
            !definitions.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(l.Departments.Select(d => d.Id)))
        {
            Error("placement", "departments", "Every configured department must be placed exactly once.");
            return result;
        }
        foreach (var d in l.Departments)
        {
            var r = d.Bounds;
            var definition = definitions[d.Id];
            if (r.Width is < 1 or > 64 || r.Height is < 1 or > 64 || r.X < 0 || r.Y < 0 || r.Right > c.Width || r.Bottom > c.Height)
                Error("placement", "bounds", $"{d.Id}: invalid or out-of-bounds rectangle.");
            if ((long) r.Width * r.Height < definition.MinArea || (long) r.Width * r.Height > definition.MaxArea)
                Error("placement", "area", $"{d.Id}: area violates configured limits.");
            if (d.Rotation is not (0 or 90 or 180 or 270) || d.EntranceSide is < -1 or > 3)
                Error("placement", "rotation", $"{d.Id}: invalid rotation or entrance side.");
        }
        if (errors.Count != 0) return result;
        if (!StationGenerator.IsLegalPlacement(c, l.Departments))
            Error("placement", "overlap-clearance", "Room, wall, corridor clearance or exterior constraints are violated.");
        HashSet<Cell> expectedReserved;
        try { expectedReserved = StationGenerator.ExteriorReservations(c, l.Departments); }
        catch (PlanningException e) { errors.Add(e.Diagnostic); return result; }
        if (!expectedReserved.SetEquals(l.ReservedExterior))
            Error("placement", "docking", "Docking reservations do not match current exterior departments.");
        var interiors = l.Departments.SelectMany(d => d.Bounds.Cells()).ToHashSet();
        var walls = l.Departments.SelectMany(d => d.Bounds.Expand(1).Cells()).ToHashSet();
        var publicTiles = l.PublicCorridors.ToHashSet();
        var maintenance = l.Maintenance.ToHashSet();
        var hull = l.Hull.ToHashSet();
        var reserved = l.ReservedExterior.ToHashSet();
        foreach (var (name, cells) in new[] { ("public", l.PublicCorridors), ("maintenance", l.Maintenance), ("hull", l.Hull), ("exterior", l.ReservedExterior) })
        {
            if (cells.Count != cells.Distinct().Count()) Error("validation", "duplicates", $"{name} contains duplicate cells.");
            if (cells.Any(p => !StationGenerator.Inside(c, p))) Error("validation", "out-of-bounds", $"{name} has out-of-bounds tiles.");
        }
        if (publicTiles.Count == 0) Error("circulation", "missing", "Public circulation is empty.");
        var doorCells = new HashSet<Cell>();
        foreach (var door in l.Doors)
        {
            var room = l.Departments.FirstOrDefault(d => d.Id == door.Department);
            if (room == null || door.Side is < 0 or > 3 || door.Cells == null || door.Throat == null)
            {
                Error("circulation", "port", "Invalid door definition.");
                continue;
            }
            var expected = StationGenerator.MakeDoor(room, door.Side, c.CorridorWidth, definitions[room.Id].Access);
            if (door.Width != expected.Width || door.Center != expected.Center || door.Approach != expected.Approach ||
                door.Access != expected.Access || !door.Cells.ToHashSet().SetEquals(expected.Cells) || !door.Throat.ToHashSet().SetEquals(expected.Throat))
                Error("circulation", "port-transform", $"{room.Id}: door geometry, width, direction or access differs from its footprint.");
            if (door.Cells.Any(p => !walls.Contains(p) || interiors.Contains(p)) || !publicTiles.IsSupersetOf(door.Throat) || !publicTiles.Contains(door.Approach))
                Error("circulation", "entrance", $"{room.Id}: entrance/throat is not present and traversable.");
            doorCells.UnionWith(expected.Cells);
        }
        foreach (var d in c.Departments)
        {
            var ports = l.Doors.Where(p => p.Department == d.Id).ToList();
            if (ports.Select(p => p.Side).Distinct().Count() != ports.Count || ports.Count != d.Entrances)
                Error("circulation", "entrance-count", $"{d.Id}: expected {d.Entrances} distinct entrances.");
        }
        if (publicTiles.Overlaps(interiors) || publicTiles.Any(p => walls.Contains(p) && !doorCells.Contains(p)))
            Error("circulation", "room-crossing", "Public corridors cross a department interior or undeclared wall port.");
        if (publicTiles.Overlaps(reserved)) Error("circulation", "dock-crossing", "Public corridors overwrite docking clearance.");
        var widthCovered = new HashSet<Cell>(l.Doors.SelectMany(d => d.Throat));
        foreach (var center in publicTiles)
        {
            var square = StationGenerator.Brush(center, c.CorridorWidth).ToArray();
            if (square.All(publicTiles.Contains)) widthCovered.UnionWith(square);
        }
        if (!widthCovered.IsSupersetOf(publicTiles))
            Error("circulation", "public-width", "Public corridor edits introduce tiles without full-width clearance outside declared entrance throats.");
        var components = Components(publicTiles);
        if (components.Count != 1) Error("circulation", "reachability", $"Public tiles form {components.Count} components; every entrance must share a legal tile route.");
        foreach (var edge in t.Edges.Where(e => e.Hard))
            if (!l.Routes.Any(r => Pair(r.From, r.To, edge.From, edge.To)))
                Error("circulation", "required-route", $"Missing realized route {edge.From}–{edge.To}.");
        foreach (var edge in t.Edges.Where(e => e.Priority == "avoided" && e.StrictAvoidance && e.Relation == "adjacency"))
        {
            var a = l.Departments.Single(d => d.Id == edge.From).Bounds;
            var b = l.Departments.Single(d => d.Id == edge.To).Bounds;
            if (a.Expand(2).Intersects(b))
                Error("placement", "avoided-adjacency", $"{edge.From} and {edge.To} share a boundary despite Never allow adjacency.");
        }
        foreach (var edge in t.Edges.Where(e => e.Hard && e.MaxDistance != null))
        {
            var a = l.Doors.FirstOrDefault(d => d.Department == edge.From);
            var b = l.Doors.FirstOrDefault(d => d.Department == edge.To);
            if (a == null || b == null || !publicTiles.Contains(a.Approach)) continue;
            var distances = new Dictionary<Cell, int> { [a.Approach] = 0 };
            var queue = new Queue<Cell>(); queue.Enqueue(a.Approach);
            while (queue.TryDequeue(out var p))
                foreach (var direction in Cell.Directions)
                    if (publicTiles.Contains(p + direction) && distances.TryAdd(p + direction, distances[p] + 1)) queue.Enqueue(p + direction);
            if (distances.TryGetValue(b.Approach, out var distance) && distance > edge.MaxDistance)
                Error("circulation", "required-distance", $"{edge.From} → {edge.To}: shortest public route is {distance} tiles, exceeding the required {edge.MaxDistance}.");
        }
        foreach (var route in l.Routes)
        {
            if (route.Centerline == null || route.Centerline.Count < 1 || !definitions.ContainsKey(route.From) || !definitions.ContainsKey(route.To))
            {
                Error("circulation", "route-data", "Invalid route metadata.");
                continue;
            }
            if (!l.Doors.Any(p => p.Department == route.From && p.Approach == route.Centerline[0]) ||
                !l.Doors.Any(p => p.Department == route.To && p.Approach == route.Centerline[^1]))
                Error("circulation", "route-endpoint", $"{route.From}–{route.To}: route endpoints do not match declared ports.");
            for (var i = 1; i < route.Centerline.Count; i++)
                if (route.Centerline[i].Distance(route.Centerline[i - 1]) != 1)
                { Error("circulation", "route-jump", $"{route.From}–{route.To}: route is not cardinally contiguous."); break; }
            if (route.Centerline.SelectMany(p => StationGenerator.Brush(p, c.CorridorWidth)).Any(p => !publicTiles.Contains(p) || walls.Contains(p) || reserved.Contains(p)))
                Error("circulation", "route-width", $"{route.From}–{route.To}: widened corridor is missing or obstructed, including a bend/junction.");
        }
        if (maintenance.Count == 0) Error("shell", "maintenance-missing", "Generate a deliberate maintenance network.");
        if (maintenance.Overlaps(walls) || maintenance.Overlaps(publicTiles) || maintenance.Overlaps(reserved))
            Error("shell", "maintenance-overlap", "Maintenance overlaps a room/wall, public tile or docking clearance.");
        foreach (var segment in Components(maintenance))
            if (!segment.Any(p => Cell.Directions.Any(d => publicTiles.Contains(p + d))))
                Error("shell", "maintenance-access", "A maintenance segment has no public access branch.");
        var expectedHull = StationGenerator.ExpectedHull(c, l);
        if (hull.Count == 0 || !hull.SetEquals(expectedHull))
            Error("shell", "hull-boundary", "Hull must equal the configured boundary of occupied geometry; missing tiles and isolated additions are invalid.");
        if (hull.Overlaps(walls) || hull.Overlaps(publicTiles) || hull.Overlaps(maintenance) || hull.Overlaps(reserved))
            Error("shell", "hull-overlap", "Hull overlaps an occupied or reserved tile.");
        result.Metrics = Metrics(project);
        result.Success = errors.All(d => d.Severity != "error");
        errors.Add(new("validation", "scope", "Geometric floor plan only. Atmosphere sealing, access simulation, equipment, utility networks, and SS14 entity materialization are deferred.", "info"));
        if (result.Metrics.CirculationArticulations > 0)
            errors.Add(new("validation", "bottlenecks", $"Public tile graph has {result.Metrics.CirculationArticulations} articulation tiles; review alternate paths.", "warning"));
        return result;
    }

    public static List<HashSet<Cell>> Components(HashSet<Cell> cells)
    {
        var remaining = new HashSet<Cell>(cells);
        var result = new List<HashSet<Cell>>();
        foreach (var start in cells.OrderBy(p => p.Y).ThenBy(p => p.X))
        {
            if (!remaining.Remove(start)) continue;
            var component = new HashSet<Cell> { start };
            var queue = new Queue<Cell>(); queue.Enqueue(start);
            while (queue.TryDequeue(out var p))
            foreach (var d in Cell.Directions)
                if (remaining.Remove(p + d)) { component.Add(p + d); queue.Enqueue(p + d); }
            result.Add(component);
        }
        return result;
    }

    public static LayoutMetrics Metrics(StationProject project)
    {
        var c = project.Config;
        var t = project.Topology!;
        var l = project.Layout!;
        var tiles = l.PublicCorridors.ToHashSet();
        var cells = StationGenerator.Sorted(tiles);
        var indices = cells.Select((p, i) => (p, i)).ToDictionary(p => p.p, p => p.i);
        var adjacency = cells.Select(p => Cell.Directions.Where(d => indices.ContainsKey(p + d)).Select(d => indices[p + d]).ToList()).ToList();
        var tileCuts = GraphCuts(adjacency);
        var nodeIds = t.Nodes.Order(StringComparer.Ordinal).ToList();
        var graph = nodeIds.Select(id => t.Edges.Where(e => e.Hard && (e.From == id || e.To == id))
            .Select(e => nodeIds.IndexOf(e.From == id ? e.To : e.From)).Distinct().ToList()).ToList();
        var cuts = GraphCuts(graph);
        var longest = 0;
        foreach (var port in l.Doors)
        {
            if (!tiles.Contains(port.Approach)) continue;
            var distance = new Dictionary<Cell, int> { [port.Approach] = 0 };
            var queue = new Queue<Cell>(); queue.Enqueue(port.Approach);
            while (queue.TryDequeue(out var p))
            foreach (var d in Cell.Directions)
                if (tiles.Contains(p + d) && distance.TryAdd(p + d, distance[p] + 1)) queue.Enqueue(p + d);
            foreach (var other in l.Doors)
                if (distance.TryGetValue(other.Approach, out var travel)) longest = Math.Max(longest, travel);
        }
        var canonical = new
        {
            Nodes = nodeIds,
            Edges = t.Edges.OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal).ThenBy(e => e.Hard).ToList(),
            Departments = l.Departments.OrderBy(d => d.Id, StringComparer.Ordinal).ToList(),
            Doors = l.Doors.OrderBy(d => d.Department, StringComparer.Ordinal).ThenBy(d => d.Side).ToList(),
            Public = cells, Maintenance = StationGenerator.Sorted(l.Maintenance), Hull = StationGenerator.Sorted(l.Hull),
            Exterior = StationGenerator.Sorted(l.ReservedExterior),
            Routes = l.Routes.OrderBy(r => r.From, StringComparer.Ordinal).ThenBy(r => r.To, StringComparer.Ordinal)
                .ThenBy(r => ProjectJson.Serialize(r.Centerline), StringComparer.Ordinal).ToList()
        };
        return new LayoutMetrics
        {
            InteriorArea = l.Departments.Sum(d => d.Bounds.Area), PublicArea = tiles.Count,
            MaintenanceArea = l.Maintenance.Count, HullArea = l.Hull.Count,
            FilledArea = l.Departments.Sum(d => d.Bounds.Expand(1).Area) + tiles.Count + l.Maintenance.Count + l.Hull.Count - l.Doors.Sum(d => d.Cells.Count),
            LongestEntranceTravel = longest, TopologyBridges = cuts.Bridges, TopologyArticulations = cuts.Articulations,
            CirculationArticulations = tileCuts.Articulations, RouteTurns = l.Routes.Sum(r => GridRouter.Turns(r.Centerline)),
            Score = Math.Round(StationGenerator.PlacementScore(c, t, l) + c.Weights.Corridor * tiles.Count + c.Weights.Turns * l.Routes.Sum(r => GridRouter.Turns(r.Centerline)), 3),
            Hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ProjectJson.Serialize(canonical))))
        };
    }

    // Iterative Tarjan traversal avoids recursion depth depending on the generated tile grid.
    private static (int Articulations, int Bridges) GraphCuts(List<List<int>> graph)
    {
        var n = graph.Count;
        var discovery = new int[n]; var low = new int[n]; var parent = Enumerable.Repeat(-1, n).ToArray();
        var children = new int[n]; var next = new int[n]; var cuts = new HashSet<int>(); var time = 0; var bridges = 0;
        for (var root = 0; root < n; root++)
        {
            if (discovery[root] != 0) continue;
            var stack = new Stack<int>(); stack.Push(root); discovery[root] = low[root] = ++time;
            while (stack.TryPeek(out var v))
            {
                if (next[v] < graph[v].Count)
                {
                    var u = graph[v][next[v]++];
                    if (discovery[u] == 0)
                    {
                        parent[u] = v; children[v]++; discovery[u] = low[u] = ++time; stack.Push(u);
                    }
                    else if (u != parent[v]) low[v] = Math.Min(low[v], discovery[u]);
                    continue;
                }
                stack.Pop();
                var p = parent[v];
                if (p == -1) { if (children[v] > 1) cuts.Add(v); continue; }
                low[p] = Math.Min(low[p], low[v]);
                if (low[v] > discovery[p]) bridges++;
                if (parent[p] != -1 && low[v] >= discovery[p]) cuts.Add(p);
            }
        }
        return (cuts.Count, bridges);
    }

    private static bool Pair(string a, string b, string x, string y) => (a == x && b == y) || (a == y && b == x);
}
