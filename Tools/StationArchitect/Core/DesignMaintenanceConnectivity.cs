namespace Content.StationArchitect;

public sealed record MaintenanceComponentObservation(string Id, HashSet<Cell> Cells, List<string> Transitions);

/// <summary>Reports separate maintenance floor components and their actual controlled crew access.</summary>
public static class DesignMaintenanceConnectivity
{
    private static bool Maintenance(string id) => id is "$maintenance" or "$exterior-maintenance";

    public static List<MaintenanceComponentObservation> Measure(StationDesignProject project)
    {
        var definitions = RoomHierarchy.Templates(project.Definition).ToDictionary(item => item.Path, item => item.Room, StringComparer.Ordinal);
        var ports = DesignMeasurements.ValidInterfaces(project).Where(port => port.Kind == "walking" && port.Type is "doorway" or "airlock" or "dockingPort").ToList();
        var transitRooms = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && room.OwnerId != RequirementCompiler.MaintenanceOwner && definitions.TryGetValue(room.DefinitionPath, out var definition) &&
            definition.AllowThroughTraffic && DesignNestedGeometry.EntryAllowed(project, room, null, definitions)).ToList();
        var ids = transitRooms.Select(room => room.Id).ToHashSet(StringComparer.Ordinal);
        var normal = transitRooms.SelectMany(room => room.Cells).Concat(project.Layout.PublicCorridors).ToHashSet();
        normal.UnionWith(ports.Where(port => (ids.Contains(port.From) || port.From == "$public") && (ids.Contains(port.To) || port.To == "$public")).SelectMany(port => port.Cells));
        var reached = Flood(normal, project.Layout.PublicCorridors);
        var remaining = project.Layout.Maintenance.Concat(ports.Where(port => Maintenance(port.From) && Maintenance(port.To)).SelectMany(port => port.Cells)).ToHashSet();
        var result = new List<MaintenanceComponentObservation>();
        while (remaining.Count > 0)
        {
            var first = remaining.OrderBy(cell => cell.Y).ThenBy(cell => cell.X).First();
            var component = Flood(remaining, [first]); remaining.ExceptWith(component);
            var transitions = ports.Where(port =>
            {
                if (!Maintenance(port.From) && !Maintenance(port.To) || !port.Cells.Any(cell => Cell.Directions.Any(direction => component.Contains(cell + direction)))) return false;
                var other = Maintenance(port.From) ? port.To : port.From;
                if (other == "$public") return project.Definition.Maintenance.Connectivity.AllowPublicTransitions && port.Access == project.Definition.Maintenance.Connectivity.TransitionAccess && port.Cells.Any(cell => Cell.Directions.Any(direction => reached.Contains(cell + direction)));
                return ids.Contains(other) && project.Layout.Rooms.Any(room => room.Id == other && room.Cells.Any(reached.Contains));
            }).Select(port => port.Id).Distinct(StringComparer.Ordinal).ToList();
            result.Add(new("maintenance:" + first.X + "," + first.Y, component, transitions));
        }
        return result;
    }

    /// <summary>Pure preview: an optional new service island must have real station access, not only a second department door.</summary>
    public static bool AdditionalComponentCanReachStation(StationDesignProject project, IEnumerable<Cell> proposedFloor, DesignInterface entrance)
    {
        var trial = ProgramJson.Clone(project); trial.Layout.Maintenance = proposedFloor.Distinct().ToList();
        trial.Layout.Interfaces.Add(ProgramJson.Clone(entrance));
        AddTransitions(trial, []);
        var touched = Measure(trial).Where(component => entrance.Cells.Any(cell => Cell.Directions.Any(direction => component.Cells.Contains(cell + direction)))).ToList();
        return touched.Count > 0 && touched.All(component => component.Transitions.Count >= project.Definition.Maintenance.Connectivity.MinTransitionsPerComponent);
    }

    public static void AddTransitions(StationDesignProject project, List<ProgramDiagnostic> diagnostics)
    {
        var policy = project.Definition.Maintenance.Connectivity;
        if (policy.Policy != "controlled" || !policy.AllowPublicTransitions) return;
        var publicFloor = project.Layout.PublicCorridors.ToHashSet(); var service = project.Layout.Maintenance.ToHashSet();
        var width = project.Settings.MaintenanceWidth;
        var centers = service.Where(center => DesignRouting.Stamp(center, width).All(service.Contains)).ToHashSet();
        var envelope = project.Layout.Envelope.ToHashSet();
        foreach (var component in Measure(project))
        {
            var count = component.Transitions.Count;
            foreach (var candidate in DesignCirculationPlanning.TransitionCandidates(publicFloor, centers.Where(center => DesignRouting.Stamp(center, width).Any(component.Cells.Contains)).ToHashSet(), width))
            {
                if (count >= policy.MinTransitionsPerComponent) break;
                if (candidate.Door.Any(service.Contains) || candidate.Door.Any(cell => project.Layout.Rooms.Any(room => room.Cells.Contains(cell))) ||
                    candidate.Door.Any(cell => project.Layout.Interfaces.Any(port => port.Kind != "utility" && port.Cells.Contains(cell))) || candidate.Door.Any(cell => project.Layout.Equipment.Any(entity => entity.Footprint.Contains(cell)))) continue;
                var first = candidate.Door[0]; var id = "transition:maintenance:" + first.X + "," + first.Y + ":" + candidate.Normal.X + "," + candidate.Normal.Y;
                var exterior = candidate.Throat.Any(cell => !envelope.Contains(cell));
                project.Layout.Interfaces.Add(new()
                {
                    Id = id, From = "$public", To = exterior ? "$exterior-maintenance" : "$maintenance", Kind = "walking", Type = exterior ? "airlock" : "doorway",
                    Access = policy.TransitionAccess, Width = candidate.Door.Count, Cells = candidate.Door,
                    FromApproach = first - candidate.Normal, ToApproach = first + candidate.Normal
                });
                count++;
            }
            if (count < policy.MinTransitionsPerComponent) diagnostics.Add(new(component.Id, "maintenance-transition", $"Only {count} physical station transitions fit this maintenance component; configured minimum is {policy.MinTransitionsPerComponent}."));
        }
    }

    private static HashSet<Cell> Flood(HashSet<Cell> allowed, IEnumerable<Cell> seeds)
    {
        var reached = seeds.Where(allowed.Contains).ToHashSet(); var pending = new Queue<Cell>(reached);
        while (pending.TryDequeue(out var cell)) foreach (var direction in Cell.Directions)
        { var next = cell + direction; if (allowed.Contains(next) && reached.Add(next)) pending.Enqueue(next); }
        return reached;
    }
}
