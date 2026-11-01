using System.Security.Cryptography;
using System.Text;

namespace Content.StationGeneration;

public sealed class PlanningException(string stage, string code, string message) : Exception(message)
{
    public Diagnostic Diagnostic { get; } = new(stage, code, message);
}

public static class StationGenerator
{
    public static List<Diagnostic> CheckConfig(StationGenerationConfig c)
    {
        var errors = new List<Diagnostic>();
        void Error(string code, string message) => errors.Add(new("specification", code, message));
        if (c.Width is < 32 or > 256 || c.Height is < 32 or > 256)
            Error("canvas", "Canvas dimensions must be between 32 and 256 tiles.");
        if (c.CorridorWidth is < 2 or > 5 || c.MaintenanceWidth is < 1 or > 3 || c.HullThickness is < 1 or > 3)
            Error("width", "Public width must be 2–5, maintenance width 1–3 and hull thickness 1–3.");
        if (c.CandidateBudget is < 1 or > 16 || c.OptimizationSteps is < 0 or > 2000)
            Error("budget", "Use 1–16 candidates and 0–2000 optimization steps.");
        if (c.Archetype is not ("hub" or "ring" or "spine"))
            Error("archetype", "Choose hub, ring or spine.");
        if (c.Weights == null || new[] { c.Weights.Distance, c.Weights.Compactness, c.Weights.Corridor, c.Weights.Turns }
                .Any(w => !double.IsFinite(w) || w < 0 || w > 1000))
            Error("weights", "Score weights must be finite numbers between 0 and 1000.");
        if (c.Departments == null || c.Departments.Count is < 3 or > 16)
        {
            Error("departments", "Include between 3 and 16 departments.");
            return errors;
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in c.Departments)
        {
            if (d == null)
            {
                Error("department", "Department definitions cannot be null.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(d.Id) || d.Id.Length > 48 || !ids.Add(d.Id))
                Error("id", "Department IDs must be nonempty, unique and at most 48 characters.");
            if (d.Description == null || d.Description.Length > 2000)
                Error("description", $"{d.Id}: description must be at most 2000 characters.");
            if (d.Width < c.CorridorWidth + 4 || d.Height < c.CorridorWidth + 4 || d.Width > 64 || d.Height > 64)
                Error("dimensions", $"{d.Id}: dimensions must be at least corridor width + 4 and at most 64.");
            if (d.MinArea < 1 || d.MaxArea < d.MinArea || (long) d.Width * d.Height < d.MinArea || (long) d.Width * d.Height > d.MaxArea)
                Error("area", $"{d.Id}: target dimensions must satisfy min/max area.");
            if (d.Entrances is < 1 or > 4 || d.Access is not ("public" or "restricted" or "secure"))
                Error("access", $"{d.Id}: use 1–4 entrances and public/restricted/secure access.");
            if (d.RequiredConnections == null || d.PreferredNeighbors == null)
                Error("relationships", $"{d.Id}: relationship lists cannot be null.");
        }
        foreach (var d in c.Departments.Where(d => d != null))
        foreach (var id in (d.RequiredConnections ?? []).Concat(d.PreferredNeighbors ?? []))
            if (!ids.Contains(id) || id == d.Id)
                Error("relationship", $"{d.Id}: unknown or self relationship '{id}'.");
        return errors;
    }

    public static StationProject Generate(StationGenerationConfig config)
        => Generate(new StationProject { Config = ProjectJson.Clone(config) });

    /// <summary>Generate geometry while preserving the mapper's authored topology.</summary>
    public static StationProject Generate(StationProject source)
    {
        var config = source.Config;
        var project = new StationProject
        {
            Config = ProjectJson.Clone(config),
            Topology = source.Topology == null ? null : ProjectJson.Clone(source.Topology),
            Stage = source.Stage
        };
        var errors = CheckConfig(config);
        if (errors.Count != 0)
        {
            project.Result = new() { Seed = config.Seed, Diagnostics = errors };
            return project;
        }
        var topology = source.Topology == null ? GenerateTopology(config) : ProjectJson.Clone(source.Topology);
        NormalizeTopology(topology);
        var topologyErrors = StationValidator.CheckTopology(config, topology);
        if (topologyErrors.Count != 0)
        {
            project.Topology = topology;
            project.Result = new() { Seed = config.Seed, Diagnostics = topologyErrors };
            return project;
        }
        var failures = new List<Diagnostic>();
        for (var attempt = 0; attempt < config.CandidateBudget; attempt++)
        {
            project = new() { Config = ProjectJson.Clone(config), Topology = ProjectJson.Clone(topology) };
            try
            {
                project.Layout = Place(config, project.Topology, attempt);
                Route(config, project.Topology, project.Layout);
                BuildShell(config, project.Layout);
                project.Stage = "validation";
                project.Result = StationValidator.Validate(project);
                project.Result.Attempts = attempt + 1;
                if (project.Result.Success)
                {
                    project.Result.Diagnostics.InsertRange(0, failures.Select(d => d with { Severity = "info" }));
                    return project;
                }
                failures.AddRange(project.Result.Diagnostics.Where(d => d.Severity == "error")
                    .Select(d => d with { Message = $"Candidate {attempt + 1}: {d.Message}" }));
            }
            catch (PlanningException e)
            {
                failures.Add(e.Diagnostic with { Message = $"Candidate {attempt + 1}: {e.Message}" });
            }
        }
        project.Result = new() { Seed = config.Seed, Attempts = config.CandidateBudget, Diagnostics = failures };
        return project;
    }

    /// <summary>Give legacy dependency records stable IDs without changing their meaning.</summary>
    public static void NormalizeTopology(StationTopology topology)
    {
        topology.NodePositions ??= new(StringComparer.Ordinal);
        topology.SuppressedTemplateIds ??= [];
        if (topology.Edges == null) return;
        var used = topology.Edges.Where(e => e != null && !string.IsNullOrWhiteSpace(e.Id))
            .Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var sequence = 1;
        foreach (var edge in topology.Edges)
        {
            if (edge == null || !string.IsNullOrWhiteSpace(edge.Id)) continue;
            if (!string.IsNullOrWhiteSpace(edge.From) && !string.IsNullOrWhiteSpace(edge.To) && edge.From != edge.To &&
                edge.Priority is "required" or "desired" or "avoided" &&
                edge.Relation is "walking" or "adjacency" or "delivery" or "transport" &&
                edge.Purpose is "crew" or "items" or "patients" or "waste" &&
                edge.Direction is "both" or "forward" && edge.AllowedModes is { Count: > 0 } &&
                edge.AllowedModes.Count <= 5 && edge.AllowedModes.Distinct(StringComparer.Ordinal).Count() == edge.AllowedModes.Count &&
                edge.AllowedModes.All(m => m is "publicHall" or "staffPassage" or "maintenance" or "disposalShortcut" or "mail"))
            {
                // Old projects did not distinguish generated and authored dependencies.
                // Stable semantic IDs allow deletion tombstones to match future presets,
                // while Origin stays custom so imported descriptions/weights are protected.
                edge.Id = TemplateDependencyId(edge);
                used.Add(edge.Id);
                continue;
            }
            while (!used.Add($"dependency-{sequence}")) sequence++;
            edge.Id = $"dependency-{sequence++}";
        }
    }

    /// <summary>Rebuild only the requested stage, using the mapper's upstream edits.</summary>
    public static StationProject RunStage(StationProject project, string stage)
    {
        project.Result = null;
        var errors = CheckConfig(project.Config);
        if (errors.Count != 0)
        {
            project.Result = new() { Seed = project.Config.Seed, Diagnostics = errors };
            return project;
        }
        try
        {
            var c = project.Config;
            if (stage == "topology")
            {
                project.Topology = GenerateTopology(c);
                project.Layout = null;
            }
            else
            {
                if (project.Topology == null)
                    throw new PlanningException("topology", "missing", "Build topology first.");
                NormalizeTopology(project.Topology);
                var topologyErrors = StationValidator.CheckTopology(c, project.Topology);
                if (topologyErrors.Count != 0)
                {
                    project.Result = new() { Seed = c.Seed, Diagnostics = topologyErrors };
                    return project;
                }
                if (stage == "placement")
                {
                    for (var attempt = 0; attempt < c.CandidateBudget; attempt++)
                    {
                        try { project.Layout = Place(c, project.Topology, attempt); break; }
                        catch (PlanningException) when (attempt + 1 < c.CandidateBudget) { }
                    }
                }
                else if (project.Layout == null)
                    throw new PlanningException("placement", "missing", "Place departments first.");
                else if (stage == "circulation")
                    Route(c, project.Topology, project.Layout);
                else if (stage == "shell")
                    BuildShell(c, project.Layout);
                else if (stage == "validation")
                    project.Result = StationValidator.Validate(project);
                else
                    throw new PlanningException("specification", "stage", "Unknown stage.");
            }
            project.Stage = stage;
        }
        catch (PlanningException e)
        {
            project.Result = new() { Seed = project.Config.Seed, Diagnostics = [e.Diagnostic] };
        }
        return project;
    }

    public static StationTopology GenerateTopology(StationGenerationConfig c)
    {
        var nodes = c.Departments.Select(d => d.Id).Order(StringComparer.Ordinal).ToList();
        // Templates express a repeatable station structure. The seed varies geometry,
        // not which departments are arbitrarily declared to depend on one another.
        var order = c.Departments.OrderBy(d => d.PlacementOrder).ThenBy(d => d.Id, StringComparer.Ordinal).Select(d => d.Id).ToList();
        var t = new StationTopology { Nodes = nodes };
        void Add(string a, string b, bool hard, double weight = 1)
        {
            if (string.CompareOrdinal(a, b) > 0) (a, b) = (b, a);
            if (a != b && !t.Edges.Any(e => e.From == a && e.To == b && e.Hard == hard))
            {
                var edge = new TopologyEdge(a, b, hard, weight)
                {
                    Origin = "template",
                    Description = hard ? "Public walking connection in the selected station structure." : "Prefer a convenient connection between these departments."
                };
                edge.Id = TemplateDependencyId(edge);
                t.Edges.Add(edge);
            }
        }
        if (c.Archetype == "hub")
        {
            var hub = c.Departments.FirstOrDefault(d => d.TopologyHub)?.Id ?? nodes[0];
            var others = order.Where(n => n != hub).ToList();
            foreach (var n in others) Add(hub, n, true);
            for (var i = 0; i < others.Count; i++) Add(others[i], others[(i + 1) % others.Count], true);
        }
        else
        {
            for (var i = 1; i < order.Count; i++) Add(order[i - 1], order[i], true);
            Add(order[^1], order[0], true);
            if (c.Archetype == "spine" && order.Count > 4)
                Add(order[1], order[^2], true);
        }
        foreach (var d in c.Departments.OrderBy(d => d.Id, StringComparer.Ordinal))
        {
            foreach (var id in d.RequiredConnections.Order(StringComparer.Ordinal)) Add(d.Id, id, true);
            foreach (var id in d.PreferredNeighbors.Order(StringComparer.Ordinal)) Add(d.Id, id, false, 2);
        }
        t.Edges = t.Edges.OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal).ThenByDescending(e => e.Hard).ToList();
        NormalizeTopology(t);
        return t;
    }

    /// <summary>
    /// Stable across presets, seed and department-list ordering for the same dependency.
    /// Weight/description are omitted so an edited template retains its override identity.
    /// </summary>
    public static string TemplateDependencyId(TopologyEdge edge)
    {
        var from = edge.From; var to = edge.To;
        if (edge.Direction == "both" && string.CompareOrdinal(from, to) > 0) (from, to) = (to, from);
        var signature = ProjectJson.Serialize(new
        {
            From = from, To = to, edge.Priority, edge.Relation, edge.Purpose, edge.Direction,
            Modes = edge.AllowedModes.Order(StringComparer.Ordinal).ToList()
        });
        return "template-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(signature)))[..32];
    }

    public static StationLayout Place(StationGenerationConfig c, StationTopology t, int attempt)
    {
        var random = new SeedRandom(c.Seed, attempt);
        var defs = c.Departments.OrderBy(d => d.Id, StringComparer.Ordinal).ToList();
        var count = defs.Count;
        var cols = c.Archetype == "spine" ? (count + 1) / 2 : (int) Math.Ceiling(Math.Sqrt(count));
        var rows = c.Archetype == "spine" ? 2 : (count + cols - 1) / cols;
        if (c.Archetype == "ring")
        {
            cols = Math.Max(3, (count + 3) / 4 + 1);
            rows = Math.Max(3, (count + 3 - 2 * cols) / 2 + 2);
        }
        var margin = Math.Max(defs.Max(d => Math.Max(d.Width, d.Height)) / 2 + 10, 18);
        var slots = new List<Cell>();
        for (var y = 0; y < rows; y++)
        for (var x = 0; x < cols; x++)
        {
            if (c.Archetype == "ring" && x != 0 && y != 0 && x != cols - 1 && y != rows - 1)
                continue;
            slots.Add(new(margin + x * (c.Width - 2 * margin) / Math.Max(1, cols - 1),
                margin + y * (c.Height - 2 * margin) / Math.Max(1, rows - 1)));
        }
        random.Shuffle(slots);
        random.Shuffle(defs);
        // Exterior blocks get exposed slots first; candidate restarts vary the assignment.
        defs = defs.OrderByDescending(d => d.Exterior).ToList();
        var layout = new StationLayout();
        foreach (var d in defs)
        {
            var rotation = random.Next(2) * 90;
            var w = rotation == 0 ? d.Width : d.Height;
            var h = rotation == 0 ? d.Height : d.Width;
            var found = false;
            foreach (var slot in slots.ToArray())
            {
                var rect = new Rect(slot.X - w / 2, slot.Y - h / 2, w, h);
                var p = new PlacedDepartment { Id = d.Id, Bounds = rect, Rotation = rotation };
                layout.Departments.Add(p);
                if (IsLegalPlacement(c, layout.Departments, false) && (!d.Exterior || IsExposedSlot(slot)))
                {
                    slots.Remove(slot);
                    found = true;
                    break;
                }
                layout.Departments.RemoveAt(layout.Departments.Count - 1);
            }
            if (!found)
                throw new PlanningException("placement", "packing", $"Cannot fit {d.Id} with wall/corridor clearance on {c.Width}×{c.Height}; increase canvas or reduce footprints.");
        }
        if (!IsLegalPlacement(c, layout.Departments))
            throw new PlanningException("placement", "exterior", "Exterior clearance is obstructed; try another candidate or a larger canvas.");
        var score = PlacementScore(c, t, layout);
        for (var i = 0; i < c.OptimizationSteps; i++)
        {
            var a = layout.Departments[random.Next(count)];
            var b = layout.Departments[random.Next(count)];
            var oldA = a.Bounds;
            var oldB = b.Bounds;
            var oldRotation = a.Rotation;
            switch (random.Next(3))
            {
                case 0:
                    if (a == b) continue;
                    a.Bounds = oldA with { X = oldB.Center.X - oldA.Width / 2, Y = oldB.Center.Y - oldA.Height / 2 };
                    b.Bounds = oldB with { X = oldA.Center.X - oldB.Width / 2, Y = oldA.Center.Y - oldB.Height / 2 };
                    break;
                case 1:
                    a.Bounds = new(oldA.Center.X - oldA.Height / 2, oldA.Center.Y - oldA.Width / 2, oldA.Height, oldA.Width);
                    a.Rotation = (a.Rotation + 90) % 360;
                    break;
                default:
                    a.Bounds = oldA with { X = oldA.X + random.Next(5) - 2, Y = oldA.Y + random.Next(5) - 2 };
                    break;
            }
            var next = PlacementScore(c, t, layout);
            if (next <= score && IsLegalPlacement(c, layout.Departments))
                score = next;
            else
            {
                a.Bounds = oldA;
                if (a != b) b.Bounds = oldB;
                a.Rotation = oldRotation;
            }
        }
        layout.Departments = layout.Departments.OrderBy(d => d.Id, StringComparer.Ordinal).ToList();
        layout.ReservedExterior = Sorted(ExteriorReservations(c, layout.Departments));
        return layout;

        bool IsExposedSlot(Cell p) => p.X == margin || p.Y == margin || p.X == c.Width - margin || p.Y == c.Height - margin;
    }

    public static bool IsLegalPlacement(StationGenerationConfig c, List<PlacedDepartment> rooms, bool exterior = true)
    {
        foreach (var a in rooms)
        {
            var r = a.Bounds;
            if (r.Width < c.CorridorWidth + 4 || r.Height < c.CorridorWidth + 4 || r.Width > 64 || r.Height > 64 || r.X < 7 || r.Y < 7 || r.X > c.Width || r.Y > c.Height || r.Right > c.Width - 7 || r.Bottom > c.Height - 7)
                return false;
            if (rooms.Any(b => a != b && r.Expand(c.CorridorWidth + 5).Intersects(b.Bounds)))
                return false;
        }
        if (!exterior) return true;
        try { ExteriorReservations(c, rooms); return true; }
        catch (PlanningException) { return false; }
    }

    public static HashSet<Cell> ExteriorReservations(StationGenerationConfig c, List<PlacedDepartment> rooms)
    {
        var reservations = new HashSet<Cell>();
        foreach (var d in c.Departments.Where(d => d.Exterior).OrderBy(d => d.Id, StringComparer.Ordinal))
        {
            var room = rooms.FirstOrDefault(r => r.Id == d.Id);
            if (room == null) continue;
            var r = room.Bounds;
            var width = c.CorridorWidth + 2;
            var x = r.Center.X - width / 2;
            var y = r.Center.Y - width / 2;
            Rect[] strips = [new(r.Right + 1, y, c.Width - r.Right - 1, width),
                new(x, r.Bottom + 1, width, c.Height - r.Bottom - 1),
                new(0, y, r.X - 1, width), new(x, 0, width, r.Y - 1)];
            var strip = strips.OrderBy(s => s.Area).FirstOrDefault(s => s.Width > 0 && s.Height > 0 &&
                !rooms.Any(b => b != room && b.Bounds.Expand(2).Intersects(s)) && !s.Cells().Any(reservations.Contains));
            if (strip.Area == 0)
                throw new PlanningException("placement", "exterior", $"{d.Id} has no unobstructed docking clearance to an exterior edge.");
            reservations.UnionWith(strip.Cells());
        }
        return reservations;
    }

    public static double PlacementScore(StationGenerationConfig c, StationTopology t, StationLayout l)
    {
        var byId = l.Departments.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var distance = t.Edges.Where(e => byId.ContainsKey(e.From) && byId.ContainsKey(e.To)).Sum(e =>
        {
            var travel = byId[e.From].Bounds.Center.Distance(byId[e.To].Bounds.Center);
            // Weight scales preference strength; it never downgrades a Required dependency.
            var cost = e.Priority == "avoided" ? Math.Max(0, (e.MaxDistance ?? 32) - travel) :
                e.MaxDistance is { } target ? Math.Max(0, travel - target) : travel;
            return cost * e.Weight * (e.Hard ? 0.3 : 1);
        });
        var area = l.Departments.Count == 0 ? 0 :
            (l.Departments.Max(d => d.Bounds.Right) - l.Departments.Min(d => d.Bounds.X)) *
            (l.Departments.Max(d => d.Bounds.Bottom) - l.Departments.Min(d => d.Bounds.Y));
        return c.Weights.Distance * distance + c.Weights.Compactness * area;
    }

    public static DoorPort MakeDoor(PlacedDepartment room, int side, int width, string access)
    {
        var r = room.Bounds;
        var center = side switch
        {
            0 => new Cell(r.Right, r.Center.Y), 1 => new Cell(r.Center.X, r.Bottom),
            2 => new Cell(r.X - 1, r.Center.Y), _ => new Cell(r.Center.X, r.Y - 1)
        };
        var direction = Cell.Directions[side];
        var tangent = side % 2 == 0 ? new Cell(0, 1) : new Cell(1, 0);
        var cells = Enumerable.Range(-(width - 1) / 2, width).Select(i => center + tangent * i).ToList();
        return new DoorPort
        {
            Department = room.Id, Side = side, Width = width, Access = access, Center = center,
            Approach = center + direction * (width + 1), Cells = cells,
            Throat = cells.SelectMany(p => Enumerable.Range(0, width + 2).Select(i => p + direction * i)).ToList()
        };
    }

    public static void Route(StationGenerationConfig c, StationTopology t, StationLayout l)
    {
        var unsupported = StationValidator.CheckPrototypeSupport(t);
        if (unsupported.Count != 0)
            throw new PlanningException(unsupported[0].Stage, unsupported[0].Code, unsupported[0].Message);
        if (!IsLegalPlacement(c, l.Departments))
            throw new PlanningException("placement", "clearance", "Edited departments violate bounds, spacing or exterior clearance.");
        l.Doors.Clear(); l.PublicCorridors.Clear(); l.Maintenance.Clear(); l.Hull.Clear(); l.Routes.Clear();
        l.ReservedExterior = Sorted(ExteriorReservations(c, l.Departments));
        var walls = l.Departments.SelectMany(d => d.Bounds.Expand(1).Cells()).ToHashSet();
        var reserved = l.ReservedExterior.ToHashSet();
        var occupied = new HashSet<Cell>(walls);
        occupied.UnionWith(reserved);
        var publicTiles = new HashSet<Cell>();
        foreach (var room in l.Departments)
        {
            var definition = c.Departments.Single(d => d.Id == room.Id);
            var candidates = Enumerable.Range(0, 4).Select(s => MakeDoor(room, s, c.CorridorWidth, definition.Access))
                .Where(p => p.Throat.All(cell => Inside(c, cell, 3) && !reserved.Contains(cell) &&
                    !l.Departments.Any(other => other != room && other.Bounds.Expand(1).Contains(cell))) &&
                    Brush(p.Approach, c.CorridorWidth).All(cell => Inside(c, cell, c.HullThickness + 2) && !occupied.Contains(cell)))
                .OrderBy(p => p.Approach.Distance(new(c.Width / 2, c.Height / 2))).ThenBy(p => p.Side).ToList();
            if (room.EntranceSide >= 0)
                candidates = candidates.OrderBy(p => p.Side == room.EntranceSide ? 0 : 1).ToList();
            if (candidates.Count < definition.Entrances || (room.EntranceSide >= 0 && candidates.All(p => p.Side != room.EntranceSide)))
                throw new PlanningException("circulation", "entrance", $"{room.Id}: insufficient legal entrances; move the block or change its entrance side.");
            foreach (var port in candidates.Take(definition.Entrances))
            {
                l.Doors.Add(port);
                publicTiles.UnionWith(port.Throat);
            }
        }
        var forbidden = new HashSet<Cell>(occupied);
        // Paths operate outside all room walls. Only authored throats penetrate walls.
        foreach (var edge in t.Edges.Where(e => e.Hard))
        {
            var a = l.Doors.First(d => d.Department == edge.From);
            var b = l.Doors.First(d => d.Department == edge.To);
            Connect(a.Approach, b.Approach, edge.From, edge.To);
        }
        foreach (var room in l.Departments)
        {
            var doors = l.Doors.Where(d => d.Department == room.Id).ToList();
            foreach (var door in doors.Skip(1))
                Connect(doors[0].Approach, door.Approach, room.Id, room.Id);
        }
        // An explicit circulation cycle visits entrances in spatial order, independently of
        // the semantic graph. Search discourages reuse here to make alternative routes useful.
        var loop = l.Departments.OrderBy(d => Math.Atan2(d.Bounds.Center.Y - c.Height / 2.0, d.Bounds.Center.X - c.Width / 2.0))
            .ThenBy(d => d.Id, StringComparer.Ordinal).Select(d => l.Doors.First(p => p.Department == d.Id)).ToList();
        for (var i = 0; i < loop.Count; i++)
            Connect(loop[i].Approach, loop[(i + 1) % loop.Count].Approach, loop[i].Department, loop[(i + 1) % loop.Count].Department, false);
        l.PublicCorridors = Sorted(publicTiles);

        void Connect(Cell start, Cell end, string from, string to, bool reuse = true)
        {
            var path = GridRouter.Find(c, start, end, forbidden, publicTiles, c.CorridorWidth, reuse);
            if (path == null)
                throw new PlanningException("circulation", "route", $"No width-{c.CorridorWidth} route between {from} and {to}.");
            foreach (var p in path) publicTiles.UnionWith(Brush(p, c.CorridorWidth));
            l.Routes.Add(new() { From = from, To = to, Centerline = path, Turns = GridRouter.Turns(path) });
        }
    }

    public static void BuildShell(StationGenerationConfig c, StationLayout l)
    {
        if (l.PublicCorridors.Count == 0)
            throw new PlanningException("circulation", "missing", "Route public circulation first.");
        l.Maintenance.Clear(); l.Hull.Clear();
        var occupied = l.Departments.SelectMany(d => d.Bounds.Expand(1).Cells()).ToHashSet();
        occupied.UnionWith(l.PublicCorridors);
        var forbidden = new HashSet<Cell>(occupied);
        forbidden.UnionWith(l.ReservedExterior);
        var maintenance = new HashSet<Cell>();
        // A deliberate outer service ring. Docking reservations cut service bays out of it.
        var margin = c.HullThickness + 2;
        var outer = new Rect(margin, margin, c.Width - 2 * margin, c.Height - 2 * margin);
        var inner = new Rect(margin + c.MaintenanceWidth, margin + c.MaintenanceWidth,
            outer.Width - 2 * c.MaintenanceWidth, outer.Height - 2 * c.MaintenanceWidth);
        foreach (var p in outer.Cells())
            if (!inner.Contains(p) && !forbidden.Contains(p)) maintenance.Add(p);
        // Every service segment gets a public access branch. It never enters a room or port.
        foreach (var segment in StationValidator.Components(maintenance))
        {
            var start = segment.OrderBy(p => p.Distance(new(c.Width / 2, c.Height / 2))).ThenBy(p => p.Y).ThenBy(p => p.X).First();
            var targets = l.PublicCorridors.Where(p => !l.Doors.Any(d => d.Throat.Contains(p))).ToList();
            var path = GridRouter.FindToAny(c, start, targets.ToHashSet(),
                l.Departments.SelectMany(d => d.Bounds.Expand(2).Cells()).Concat(l.ReservedExterior).ToHashSet());
            if (path == null)
                throw new PlanningException("shell", "maintenance", "An outer maintenance segment has no legal public access branch.");
            maintenance.UnionWith(path.Where(p => !occupied.Contains(p)));
        }
        l.Maintenance = Sorted(maintenance);
        l.Hull = Sorted(ExpectedHull(c, l));
    }

    public static HashSet<Cell> ExpectedHull(StationGenerationConfig c, StationLayout l)
    {
        var occupied = l.Departments.SelectMany(d => d.Bounds.Expand(1).Cells())
            .Concat(l.PublicCorridors).Concat(l.Maintenance).ToHashSet();
        var hull = new HashSet<Cell>();
        var reserved = l.ReservedExterior.ToHashSet();
        foreach (var p in occupied)
        for (var y = -c.HullThickness; y <= c.HullThickness; y++)
        for (var x = -c.HullThickness; x <= c.HullThickness; x++)
        {
            var q = p + new Cell(x, y);
            if (Inside(c, q) && !occupied.Contains(q) && !reserved.Contains(q)) hull.Add(q);
        }
        return hull;
    }

    public static bool Inside(StationGenerationConfig c, Cell p, int margin = 0) =>
        p.X >= margin && p.Y >= margin && p.X < c.Width - margin && p.Y < c.Height - margin;

    public static IEnumerable<Cell> Brush(Cell center, int width)
    {
        var low = -(width - 1) / 2;
        for (var y = low; y < low + width; y++)
        for (var x = low; x < low + width; x++)
            yield return center + new Cell(x, y);
    }

    public static List<Cell> Sorted(IEnumerable<Cell> cells) => cells.Distinct().OrderBy(p => p.Y).ThenBy(p => p.X).ToList();
}
