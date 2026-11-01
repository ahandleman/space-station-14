namespace Content.StationArchitect;

/// <summary>Explicit mixed routes with real maintenance legs at both endpoints. Strict maintenance is unchanged.</summary>
public static class DesignControlledMaintenance
{
    private sealed record State(Cell Cell, int Phase);
    private sealed class Geometry
    {
        public HashSet<Cell> Source = [], Target = [], Transit = [], Maintenance = [], Public = [], MaintenancePorts = [], PublicPorts = [], Blocked = [];
    }

    private static Geometry Build(StationDesignProject project, DesignRoom from, DesignRoom to)
    {
        var definitions = RoomHierarchy.Templates(project.Definition).ToDictionary(item => item.Path, item => item.Room, StringComparer.Ordinal);
        var rooms = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && definitions.ContainsKey(room.DefinitionPath)).ToList();
        bool Eligible(DesignRoom room, DesignRoom endpoint) => room.Id == endpoint.Id || definitions[room.DefinitionPath].AllowThroughTraffic || DesignNestedGeometry.IsAncestor(project, room.Id, endpoint);
        var sourceRooms = rooms.Where(room => room.OwnerId == from.OwnerId && Eligible(room, from)).ToList();
        var targetRooms = rooms.Where(room => room.OwnerId == to.OwnerId && Eligible(room, to)).ToList();
        var transitRooms = rooms.Where(room => room.Id == from.Id || room.Id == to.Id ||
            (definitions[room.DefinitionPath].AllowThroughTraffic && DesignNestedGeometry.EntryAllowed(project, room, null, definitions)) ||
            DesignNestedGeometry.IsAncestor(project, room.Id, from) || DesignNestedGeometry.IsAncestor(project, room.Id, to)).ToList();
        var sourceIds = sourceRooms.Select(room => room.Id).ToHashSet(StringComparer.Ordinal);
        var targetIds = targetRooms.Select(room => room.Id).ToHashSet(StringComparer.Ordinal);
        var transitIds = transitRooms.Select(room => room.Id).ToHashSet(StringComparer.Ordinal);
        bool Maintenance(string id) => id is "$maintenance" or "$exterior-maintenance";
        bool AllowedEndpoint(string id) => Maintenance(id) || id == "$public" && project.Definition.Maintenance.Connectivity.AllowPublicTransitions || transitIds.Contains(id);
        var ports = DesignMeasurements.ValidInterfaces(project).Where(port => port.Kind == "walking" && port.Type is "doorway" or "airlock" or "dockingPort" && AllowedEndpoint(port.From) && AllowedEndpoint(port.To)).ToList();
        var geometry = new Geometry
        {
            Source = sourceRooms.SelectMany(room => room.Cells).ToHashSet(), Target = targetRooms.SelectMany(room => room.Cells).ToHashSet(),
            Transit = transitRooms.SelectMany(room => room.Cells).Concat(ports.SelectMany(port => port.Cells)).ToHashSet(),
            Maintenance = project.Layout.Maintenance.Concat(ports.Where(port => Maintenance(port.From) && Maintenance(port.To)).SelectMany(port => port.Cells)).ToHashSet(),
            Public = project.Definition.Maintenance.Connectivity.AllowPublicTransitions ? project.Layout.PublicCorridors.ToHashSet() : [],
            MaintenancePorts = ports.Where(port => Maintenance(port.From) || Maintenance(port.To)).SelectMany(port => port.Cells).ToHashSet(),
            PublicPorts = ports.Where(port => port.From == "$public" || port.To == "$public").SelectMany(port => port.Cells).ToHashSet(),
            Blocked = project.Layout.Equipment.Where(entity => DesignSystems.BlocksCrew(project, entity)).SelectMany(entity => entity.Footprint).Concat(DesignTransport.EndpointFloorCells(project)).ToHashSet()
        };
        geometry.Source.UnionWith(ports.Where(port => (sourceIds.Contains(port.From) && (sourceIds.Contains(port.To) || Maintenance(port.To))) || (sourceIds.Contains(port.To) && (sourceIds.Contains(port.From) || Maintenance(port.From)))).SelectMany(port => port.Cells));
        geometry.Target.UnionWith(ports.Where(port => (targetIds.Contains(port.From) && (targetIds.Contains(port.To) || Maintenance(port.To))) || (targetIds.Contains(port.To) && (targetIds.Contains(port.From) || Maintenance(port.From)))).SelectMany(port => port.Cells));
        geometry.Transit.UnionWith(geometry.Source.Concat(geometry.Target).Concat(geometry.Public));
        return geometry;
    }

    private static int Phase(Geometry geometry, int phase, Cell cell)
    {
        if (geometry.Blocked.Contains(cell)) return -1;
        if (geometry.Maintenance.Contains(cell)) return 1;
        if (phase == 0) return geometry.Source.Contains(cell) ? 0 : -1;
        if (phase == 1 && geometry.Target.Contains(cell)) return 3;
        if (phase == 3) return geometry.Target.Contains(cell) ? 3 : -1;
        return geometry.Transit.Contains(cell) ? 2 : -1;
    }

    private static bool Edge(Geometry geometry, Cell from, Cell to)
    {
        if (from.Distance(to) != 1) return false;
        if (geometry.Maintenance.Contains(from) != geometry.Maintenance.Contains(to) && !geometry.MaintenancePorts.Contains(geometry.Maintenance.Contains(from) ? to : from)) return false;
        if (geometry.Public.Contains(from) != geometry.Public.Contains(to) && !geometry.PublicPorts.Contains(geometry.Public.Contains(from) ? to : from)) return false;
        return true;
    }

    public static List<Cell>? FindRoute(StationDesignProject project, DesignRoom from, DesignRoom to, int budget = 100000)
    {
        var geometry = Build(project, from, to); var target = to.Cells.ToHashSet();
        var pending = new Queue<State>(); var previous = new Dictionary<State, State?>();
        foreach (var cell in from.Cells.OrderBy(cell => cell.Y).ThenBy(cell => cell.X).Where(cell => !geometry.Blocked.Contains(cell)))
        { var start = new State(cell, 0); previous[start] = null; pending.Enqueue(start); }
        while (pending.TryDequeue(out var state) && budget-- > 0)
        {
            if (state.Phase == 3 && target.Contains(state.Cell))
            {
                var path = new List<Cell>(); State? cursor = state;
                while (cursor != null) { path.Add(cursor.Cell); cursor = previous[cursor]; }
                path.Reverse(); return path;
            }
            foreach (var direction in Cell.Directions)
            {
                var cell = state.Cell + direction; var phase = Phase(geometry, state.Phase, cell);
                if (phase < 0 || !Edge(geometry, state.Cell, cell)) continue;
                var next = new State(cell, phase);
                if (previous.TryAdd(next, state)) pending.Enqueue(next);
            }
        }
        return null;
    }

    public static bool RouteValid(StationDesignProject project, DesignRoute route)
    {
        var from = project.Layout.Rooms.FirstOrDefault(room => room.Id == route.From && room.Enabled && !room.Orphaned);
        var to = project.Layout.Rooms.FirstOrDefault(room => room.Id == route.To && room.Enabled && !room.Orphaned);
        if (route.Kind != "walking" || from == null || to == null || route.Cells.Count == 0 || !from.Cells.Contains(route.Cells[0]) || !to.Cells.Contains(route.Cells[^1])) return false;
        var geometry = Build(project, from, to); var phase = 0;
        if (geometry.Blocked.Contains(route.Cells[0])) return false;
        for (var index = 1; index < route.Cells.Count; index++)
        {
            if (!Edge(geometry, route.Cells[index - 1], route.Cells[index])) return false;
            phase = Phase(geometry, phase, route.Cells[index]); if (phase < 0) return false;
        }
        return phase == 3;
    }
}
