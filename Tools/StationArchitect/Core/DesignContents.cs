namespace Content.StationArchitect;

/// <summary>Places explicit equipment and initial atmosphere plans on already-routed room geometry.</summary>
public static class DesignContents
{
    public static StationDesignProject Run(StationDesignProject source)
    {
        var project = ProgramJson.Clone(source);
        var compilation = RequirementCompiler.Compile(project.Definition);
        var diagnostics = new List<ProgramDiagnostic>(compilation.Diagnostics);
        if (!compilation.Success) return Finish(project, diagnostics);
        var definitions = RequirementCompiler.Rooms(project.Definition).ToDictionary(pair => pair.Path, pair => pair.Room, StringComparer.Ordinal);
        var rooms = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && definitions.ContainsKey(room.DefinitionPath)).OrderBy(room => room.Id, StringComparer.Ordinal).ToList();
        var replaceablePorts = project.Layout.Equipment.Where(entity => !entity.Pinned && entity.InterfaceId.Length > 0).Select(entity => entity.InterfaceId).ToHashSet(StringComparer.Ordinal);
        foreach (var port in project.Layout.Interfaces.Where(port => replaceablePorts.Contains(port.Id) && port.Kind != "utility"))
            project.Layout.Walls.AddRange(port.Cells);
        project.Layout.Walls = project.Layout.Walls.Distinct().ToList();
        project.Layout.Interfaces.RemoveAll(port => replaceablePorts.Contains(port.Id));
        MaintenanceAirlockGates.Apply(project, diagnostics);
        var retained = project.Layout.Equipment.Where(entity => entity.Pinned).ToList();
        project.Layout.Equipment = retained;
        foreach (var dock in project.Layout.Docks.Where(dock => !dock.Edited && !dock.Locked))
        {
            var existing = retained.FirstOrDefault(entity => entity.RoomId == dock.RoomId && entity.RequirementId == dock.ContentId && entity.Position == dock.BoundaryCells.FirstOrDefault() && entity.Footprint.ToHashSet().SetEquals(dock.BoundaryCells) && DesignDockGeometry.OutwardForRotation(entity.Rotation) == dock.Outward);
            if (existing != null) { dock.EntityId = existing.Id; dock.InterfaceId = existing.InterfaceId; }
        }
        var dockingSpace = DesignDockGeometry.ReservedCells(project);
        var dockingBoundaries = project.Layout.Docks.SelectMany(dock => dock.BoundaryCells).ToHashSet();
        var transportTerminals = DesignTransport.EndpointFloorCells(project);
        var occupied = retained.SelectMany(entity => entity.Footprint).Concat(transportTerminals).ToHashSet();
        var routes = project.Layout.Routes.Where(route => route.Kind == "walking" && route.Mode is not ("disposalShortcut" or "mail")).SelectMany(route => route.Cells).ToHashSet();
        var approaches = project.Layout.Interfaces.SelectMany(port => new[] { port.FromApproach, port.ToApproach }).ToHashSet();
        var structural = project.Layout.Walls.Concat(project.Layout.Hull).Concat(project.Layout.Windows).ToHashSet();
        var exterior = DesignMeasurements.ExteriorSpace(project);
        var oldZones = project.Layout.EnvironmentZones.Where(zone => zone.Edited).ToList();
        project.Layout.EnvironmentZones = oldZones;
        foreach (var room in rooms)
        {
            var definition = definitions[room.DefinitionPath];
            if (room.Cells.Count == 0) { diagnostics.Add(new(room.Id, "contents-floor", "Grow this room before placing its contents.")); continue; }
            var floor = room.Cells.ToHashSet();
            var reservedWork = retained.Where(entity => entity.RoomId == room.Id).SelectMany(entity => entity.WorkArea.Count == 0 ? entity.Footprint : entity.WorkArea).Concat(transportTerminals.Where(floor.Contains)).ToHashSet();
            var boundary = floor.SelectMany(cell => Cell.Directions.Select(direction => cell + direction)).Except(floor).Where(structural.Contains).ToHashSet();
            var spaceBoundary = boundary.Where(cell => Cell.Directions.Any(direction => floor.Contains(cell - direction) && exterior.Contains(cell + direction))).ToHashSet();
            var minimumFailures = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entity in retained.Where(entity => entity.RoomId == room.Id))
            {
                var requirement = definition.Contents.FirstOrDefault(item => item.Id == entity.RequirementId);
                if (requirement == null || !requirement.Prototypes.Contains(entity.Prototype, StringComparer.Ordinal) || !DesignMeasurements.EntityPlacementValid(project, room, entity))
                    diagnostics.Add(new(entity.Id, "pinned-equipment", "Pinned equipment no longer meets its room, prototype or placement requirement; it remains visible for correction."));
            }
            foreach (var preferred in new[] { false, true })
            foreach (var requirement in definition.Contents.OrderBy(requirement => requirement.Placement == "spaceBoundary" ? 0 : requirement.Placement == "boundary" ? 1 : 2).ThenByDescending(requirement => requirement.MinArea).ThenBy(requirement => requirement.Id, StringComparer.Ordinal))
            {
                if (preferred && minimumFailures.Contains(requirement.Id)) continue;
                var dockRule = definition.Exterior.Docks.FirstOrDefault(rule => rule.ContentId == requirement.Id);
                var existing = project.Layout.Equipment.Where(entity => entity.RoomId == room.Id && entity.RequirementId == requirement.Id).ToList();
                if (!preferred && existing.Count > requirement.Count.Max) diagnostics.Add(new(room.Id + "/contents:" + requirement.Id, "equipment-count", "Pinned equipment exceeds the allowed maximum; remove or unpin surplus entities explicitly."));
                var requested = preferred ? Math.Max(requirement.Count.Min, requirement.Count.Target) : requirement.Count.Min;
                for (var number = existing.Count; number < requested; number++)
                {
                    var dock = dockRule == null ? null : project.Layout.Docks.Where(dock => dock.RoomId == room.Id && dock.RequirementId == dockRule.Id && dock.ContentId == requirement.Id && !project.Layout.Equipment.Any(entity => entity.Id == dock.EntityId)).OrderBy(dock => dock.Instance).FirstOrDefault();
                    if (dockRule != null && dock == null)
                    { diagnostics.Add(new(room.Id + "/contents:" + requirement.Id, "dock-plan-missing", "This docking content needs a declared berth and exterior approach before its native boundary hardware can be placed.", number < requirement.Count.Min ? "error" : "warning")); break; }
                    if (dock != null && DesignDockGeometry.ValidateDock(project, dock).Any(issue => issue.Severity == "error"))
                    { diagnostics.AddRange(DesignDockGeometry.ValidateDock(project, dock)); break; }
                    var prototype = requirement.Prototypes[(StableHash(room.Id + "/" + requirement.Id + "/" + number, project.Settings.Seed) & int.MaxValue) % requirement.Prototypes.Count];
                    var size = project.Settings.PrototypeFootprints.GetValueOrDefault(prototype) ?? new EntityFootprintSize();
                    if (size.Width is < 1 or > 16 || size.Height is < 1 or > 16 || size.OffsetX is < -16 or > 16 || size.OffsetY is < -16 or > 16)
                    { diagnostics.Add(new(room.Id, "equipment-profile", "Prototype footprint dimensions must be 1–16 tiles and offsets within ±16 tiles.")); break; }
                    var candidates = dock != null ? dock.BoundaryCells.ToHashSet() : requirement.Placement == "floor" ? floor : requirement.Placement == "spaceBoundary" ? spaceBoundary : boundary;
                    (Cell Position, List<Cell> Footprint, List<Cell> Work, int Rotation)? chosen = null;
                    foreach (var cell in candidates.OrderBy(cell => requirement.Placement != "floor" && project.Layout.Windows.Contains(cell) ? 1 : 0).ThenBy(cell => StableHash(room.Id + "/" + requirement.Id + "/" + cell.X + "," + cell.Y, project.Settings.Seed)).ThenBy(cell => cell.Y).ThenBy(cell => cell.X))
                    {
                        for (var rotation = 0; rotation < 4; rotation++)
                        {
                            if (dock != null && rotation * 90 != DesignDockGeometry.RotationForOutward(dock.Outward)) continue;
                            if (!DesignEquipmentGeometry.TryExpectedFootprint(project.Settings, prototype, cell, rotation * 90, out var footprint)) continue;
                            if (footprint.Any(part => !candidates.Contains(part) || occupied.Contains(part) || dockingSpace.Contains(part) || dock == null && dockingBoundaries.Contains(part) || requirement.Placement == "floor" && (routes.Contains(part) || approaches.Contains(part) || reservedWork.Contains(part)))) continue;
                            if (dock != null && !footprint.ToHashSet().SetEquals(dock.BoundaryCells)) continue;
                            var boundaryInterface = dock != null ? DockInterface(room, definition, dock, footprint, exterior) : requirement.InterfaceType.Length > 0 ? BoundaryInterface(project, room, footprint, requirement.InterfaceType, exterior) : null;
                            if (requirement.InterfaceType.Length > 0 && boundaryInterface == null) continue;
                            if (requirement.InterfaceType is "airlock" or "dockingPort")
                            {
                                var outward = boundaryInterface!.ToApproach - boundaryInterface.Cells[0];
                                var facing = outward.X > 0 ? 90 : outward.Y > 0 ? 180 : outward.X < 0 ? 270 : 0;
                                if (rotation * 90 != facing) continue;
                            }
                            if (requirement.InterfaceType.Length > 0 && requirement.InterfaceType is not ("window" or "pipe" or "cable") && floor.Sum(part => Cell.Directions.Count(direction => structural.Contains(part + direction) && !footprint.Contains(part + direction) && exterior.Contains(part + direction + direction))) < definition.Exterior.MinSpaceFrontage) continue;
                            var work = requirement.Placement == "floor" ? WorkingArea(cell, Math.Max(requirement.MinArea, footprint.Count), floor, reservedWork, footprint) : BoundaryWorkingArea(footprint, requirement.MinArea, floor, reservedWork);
                            if (work.Count < (requirement.Placement == "floor" ? Math.Max(requirement.MinArea, footprint.Count) : requirement.MinArea) || work.Any(dockingSpace.Contains)) continue;
                            if (requirement.Placement == "floor" && !Cell.Directions.Any(direction => floor.Contains(cell + direction) && !occupied.Contains(cell + direction))) continue;
                            if (requirement.Placement == "floor" && DesignMeasurements.Components(floor.Except(occupied).Except(footprint).ToHashSet()) != 1) continue;
                            chosen = (cell, footprint, work, rotation * 90); break;
                        }
                        if (chosen != null) break;
                    }
                    if (chosen == null)
                    {
                        if (number < requirement.Count.Min) minimumFailures.Add(requirement.Id);
                        diagnostics.Add(new(room.Id + "/contents:" + requirement.Id, number < requirement.Count.Min ? "equipment-fit" : "equipment-target", $"Placed {number} of preferred {requested} {requirement.Id} instances. Available floor/boundary, walking clearance or working area could not fit another.", number < requirement.Count.Min ? "error" : "warning"));
                        break;
                    }
                    var selected = chosen.Value;
                    var idNumber = 1;
                    while (project.Layout.Equipment.Any(entity => entity.Id == room.Id + "/" + requirement.Id + "#" + idNumber)) idNumber++;
                    var entity = new DesignEntity
                    {
                        Id = dock != null && dock.EntityId.Length > 0 ? dock.EntityId : room.Id + "/" + requirement.Id + "#" + idNumber, RoomId = room.Id, RequirementId = requirement.Id,
                        Prototype = prototype, Position = selected.Position, Rotation = selected.Rotation, Placement = requirement.Placement,
                        Footprint = selected.Footprint, WorkArea = selected.Work
                    };
                    if (requirement.InterfaceType.Length > 0)
                    {
                        var port = dock != null ? DockInterface(room, definition, dock, selected.Footprint, exterior)! : BoundaryInterface(project, room, selected.Footprint, requirement.InterfaceType, exterior)!;
                        port.Id = dock != null && dock.InterfaceId.Length > 0 ? dock.InterfaceId : "equipment:" + entity.Id; port.RequirementId = room.Id + "/contents:" + requirement.Id;
                        entity.InterfaceId = port.Id; project.Layout.Interfaces.Add(port);
                        if (dock != null) { dock.EntityId = entity.Id; dock.InterfaceId = port.Id; }
                        if (port.Kind == "walking") approaches.UnionWith([port.FromApproach, port.ToApproach]);
                        if (port.Kind != "utility")
                        {
                            project.Layout.Walls.RemoveAll(selected.Footprint.Contains);
                            project.Layout.Hull.RemoveAll(selected.Footprint.Contains);
                            project.Layout.Windows.RemoveAll(selected.Footprint.Contains);
                            structural.ExceptWith(selected.Footprint);
                        }
                    }
                    project.Layout.Equipment.Add(entity);
                    occupied.UnionWith(selected.Footprint); reservedWork.UnionWith(selected.Work);
                }
            }
            if (oldZones.All(zone => zone.RoomId != room.Id)) project.Layout.EnvironmentZones.Add(InitialZone(project, room, definition));
        }
        foreach (var entity in retained.Where(entity => rooms.All(room => room.Id != entity.RoomId)))
            diagnostics.Add(new(entity.Id, "orphaned-equipment", "Pinned equipment belongs to a removed or inactive room; remove or restore it explicitly."));
        AddNetworkZone("$public", project.Layout.PublicCorridors, false);
        AddNetworkZone("$maintenance", project.Layout.Maintenance.Where(project.Layout.Envelope.Contains).ToList(), false);
        AddNetworkZone("$exterior-maintenance", project.Layout.Maintenance.Where(cell => !project.Layout.Envelope.Contains(cell)).ToList(), true);
        diagnostics.Add(new("$station", "simulation-pending", "Equipment and initial gas/temperature plans are materialized, but a running SS14 atmosphere, power, access and hazard simulation has not been verified.", "warning"));
        return Finish(project, diagnostics);

        void AddNetworkZone(string id, List<Cell> cells, bool vacuum)
        {
            if (cells.Count == 0 || oldZones.Any(zone => zone.RoomId == id)) return;
            project.Layout.EnvironmentZones.Add(new()
            {
                Id = id + "/atmosphere", RoomId = id, Cells = cells.Distinct().ToList(),
                Pressure = vacuum ? "vacuum" : "standard", Placement = vacuum ? "exterior" : "interior", Sealed = !vacuum,
                TemperatureKelvin = 293.15, PressureKpa = vacuum ? 0 : 101.325,
                GasFractions = vacuum ? [] : new() { ["Oxygen"] = .21, ["Nitrogen"] = .79 }
            });
        }
    }

    private static DesignInterface? DockInterface(DesignRoom room, RoomDefinition definition, DesignDock dock, List<Cell> footprint, HashSet<Cell> exterior)
    {
        if (!footprint.ToHashSet().SetEquals(dock.BoundaryCells) || footprint.Count != 1) return null;
        var boundary = footprint[0];
        if (!room.Cells.Contains(boundary - dock.Outward) || !exterior.Contains(boundary + dock.Outward)) return null;
        return new() { From = room.Id, To = "$space", Kind = "walking", Type = "dockingPort", Access = definition.Access.Level, Width = 1, Cells = [.. footprint], FromApproach = boundary - dock.Outward, ToApproach = boundary + dock.Outward };
    }

    public static DesignEnvironmentZone InitialZone(StationDesignProject project, DesignRoom room, RoomDefinition definition)
    {
        var temperature = Math.Clamp(293.15, definition.Environment.MinTemperatureKelvin ?? 0, definition.Environment.MaxTemperatureKelvin ?? 100000);
        var pressure = definition.Environment.Pressure switch { "vacuum" => 0d, "low" => 20d, "standard" => 101.325, _ => (double?) null };
        return new()
        {
            Id = room.Id + "/atmosphere", RoomId = room.Id, Cells = room.Cells.Distinct().ToList(), Pressure = definition.Environment.Pressure,
            Placement = room.Cells.All(project.Layout.Envelope.Contains) ? "interior" : "exterior", Sealed = definition.Environment.Sealed,
            TemperatureKelvin = temperature, PressureKpa = pressure,
            GasFractions = pressure is > 0 ? new() { ["Oxygen"] = .21, ["Nitrogen"] = .79 } : []
        };
    }

    private static DesignInterface? BoundaryInterface(StationDesignProject project, DesignRoom room, List<Cell> cells, string type, HashSet<Cell> exterior)
    {
        var kind = type is "doorway" or "airlock" or "dockingPort" ? "walking" : type is "counter" or "window" ? "interaction" : "utility";
        var definition = RequirementCompiler.Rooms(project.Definition).First(pair => pair.Path == room.DefinitionPath).Room;
        foreach (var direction in Cell.Directions)
        {
            if (!cells.All(cell => room.Cells.Contains(cell - direction))) continue;
            var outside = cells.Select(cell => cell + direction).ToList();
            var other = project.Layout.Rooms.FirstOrDefault(candidate => candidate.Enabled && !candidate.Orphaned && candidate.Id != room.Id && outside.All(candidate.Cells.Contains));
            string? target = other?.Id;
            if (target == null && outside.All(project.Layout.PublicCorridors.Contains)) target = "$public";
            if (target == null && outside.All(project.Layout.Maintenance.Contains)) target = "$maintenance";
            if (target == null && outside.All(exterior.Contains)) target = "$space";
            if (target == null) continue;
            if (kind == "walking")
            {
                if (target == "$public" && !definition.Access.AllowPublicEntry || target == "$maintenance" && !definition.Access.AllowMaintenanceEntry) continue;
                if (other != null)
                {
                    var otherDefinition = RequirementCompiler.Rooms(project.Definition).First(pair => pair.Path == other.DefinitionPath).Room;
                    if (definition.Access.EntryOwners.Count > 0 && !definition.Access.EntryOwners.Contains(other.OwnerId, StringComparer.Ordinal) || otherDefinition.Access.EntryOwners.Count > 0 && !otherDefinition.Access.EntryOwners.Contains(room.OwnerId, StringComparer.Ordinal)) continue;
                }
            }
            return new() { From = room.Id, To = target, Kind = kind, Type = type, Access = definition.Access.Level, Cells = [.. cells], Width = cells.Count, FromApproach = cells[0] - direction, ToApproach = cells[0] + direction };
        }
        return null;
    }

    private static List<Cell> WorkingArea(Cell start, int count, HashSet<Cell> floor, HashSet<Cell> reserved, List<Cell> footprint)
    {
        var result = footprint.ToHashSet();
        if (result.Any(reserved.Contains)) return [];
        var seen = new HashSet<Cell>(result); var queue = new Queue<Cell>(result);
        while (queue.TryDequeue(out var cell) && result.Count < count)
            foreach (var direction in Cell.Directions)
            {
                var next = cell + direction;
                if (!floor.Contains(next) || reserved.Contains(next) || !seen.Add(next)) continue;
                result.Add(next); queue.Enqueue(next);
                if (result.Count == count) break;
            }
        return result.OrderBy(cell => cell.Y).ThenBy(cell => cell.X).ToList();
    }

    private static List<Cell> BoundaryWorkingArea(List<Cell> footprint, int count, HashSet<Cell> floor, HashSet<Cell> reserved)
    {
        if (count == 0) return [];
        foreach (var start in footprint.SelectMany(cell => Cell.Directions.Select(direction => cell + direction)).Where(cell => floor.Contains(cell) && !reserved.Contains(cell)).Distinct().OrderBy(cell => cell.Y).ThenBy(cell => cell.X))
        {
            var result = WorkingArea(start, count, floor, reserved, [start]);
            if (result.Count >= count) return result;
        }
        return [];
    }

    private static int StableHash(string text, int seed)
    {
        unchecked { uint hash = (uint) seed ^ 2166136261; foreach (var character in text) hash = (hash ^ character) * 16777619; return (int) hash; }
    }

    private static StationDesignProject Finish(StationDesignProject project, List<ProgramDiagnostic> diagnostics)
    {
        project.Stage = "contents";
        project.Report = new() { Stage = "contents", Complete = false, Diagnostics = diagnostics.Distinct().ToList() };
        ProjectTree.Refresh(project);
        return project;
    }
}
