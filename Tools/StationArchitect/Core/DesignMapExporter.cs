using System.Globalization;
using System.Text;
using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Content.StationArchitect;

public sealed class DesignMapExport
{
    public string MapYaml { get; set; } = "";
    public string DescriptorYaml { get; set; } = "";
    public string ManifestJson { get; set; } = "";
    public string MapFileName { get; set; } = "";
    public string DescriptorFileName { get; set; } = "";
    public string ManifestFileName { get; set; } = "";
    public List<ProgramDiagnostic> Diagnostics { get; set; } = [];
}

/// <summary>
/// Writes an actual format-7 SS14 map, initial atmosphere data and descriptor. The manifest
/// distinguishes materialized planning data from an unexecuted game simulation.
/// </summary>
public static class DesignMapExporter
{
    public static string ExportMap(StationDesignProject project) => Export(project).MapYaml;

    public static DesignMapExport Export(StationDesignProject project)
    {
        project = ProgramJson.Clone(project);
        var profile = project.Settings.Export;
        var stem = Sanitize(project.Definition.Id).ToLowerInvariant() + "-" + project.Settings.Seed.ToString(CultureInfo.InvariantCulture);
        var stationId = "Generated" + Sanitize(project.Definition.Id);
        var result = new DesignMapExport { MapFileName = stem + ".yml", DescriptorFileName = stem + ".yml", ManifestFileName = stem + ".manifest.json" };
        var diagnostics = result.Diagnostics;
        var validation = DesignGeometryValidator.Validate(project, true, refreshReports: true);
        var nativeValidation = DesignSystems.Validate(project, true);
        validation.Diagnostics = validation.Diagnostics.Concat(ProjectEdits.Review(project)).Distinct().ToList();
        diagnostics.AddRange(validation.Diagnostics.Concat(nativeValidation.Diagnostics));
        var definitions = RequirementCompiler.Rooms(project.Definition).ToDictionary(pair => pair.Path, pair => pair.Room, StringComparer.Ordinal);
        var rooms = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && definitions.ContainsKey(room.DefinitionPath)).OrderBy(room => room.Id, StringComparer.Ordinal).ToList();
        var equipment = project.Layout.Equipment.OrderBy(entity => entity.Id, StringComparer.Ordinal).ToList();
        var tileNames = new Dictionary<int, string> { [0] = "Space", [1] = profile.FloorTile, [2] = profile.MaintenanceTile, [3] = profile.ExteriorTile };
        var tiles = new Dictionary<Cell, int>();
        foreach (var room in rooms)
            foreach (var cell in room.Cells) tiles[cell] = definitions[room.DefinitionPath].Environment.Placement == "exterior" ? 3 : 1;
        foreach (var cell in project.Layout.PublicCorridors) tiles[cell] = 1;
        foreach (var cell in project.Layout.Maintenance) tiles[cell] = 2;
        foreach (var cell in project.Layout.Walls.Concat(project.Layout.Hull).Concat(project.Layout.Windows).Concat(project.Layout.Interfaces.SelectMany(port => port.Cells)))
            if (!tiles.ContainsKey(cell)) tiles[cell] = 1;
        if (tiles.Count == 0) diagnostics.Add(new("$station", "export-empty", "This project has no materialized floor. Generate room growth and circulation before using the map."));

        var mapEntity = Entity(1, "", [Component("MetaData", ("name", project.Definition.Name)), Component("Transform"), Component("Map", ("mapPaused", true)), Component("GridTree"), Component("Broadphase"), Component("OccluderTree")]);
        var gridComponents = new List<object>
        {
            Component("MetaData", ("name", project.Definition.Name)), Component("Transform", ("parent", 1)),
            Component("MapGrid", ("chunks", Chunks(tiles))), Component("Physics", ("bodyType", "Dynamic"), ("bodyStatus", "InAir")),
            Component("Fixtures", ("fixtures", new Dictionary<string, object>())), Component("BecomesStation", ("id", stationId)),
            Component("Gravity"), Component("GridPathfinding"), Component("SpreaderGrid"), Component("NavMap"), Component("GasTileOverlay"), Component("ImplicitRoof")
        };
        var atmos = Atmosphere(project, rooms, diagnostics);
        gridComponents.Add(Component("GridAtmosphere", ("version", 2), ("data", atmos)));
        var entities = new List<MapEntity> { mapEntity, Entity(2, "", gridComponents) };
        var uid = 3;
        var emitted = new HashSet<(string Prototype, Cell Cell)>();
        var emittedEntities = new Dictionary<(string Prototype, Cell Cell), MapEntity>();
        var nativeEntityIds = new Dictionary<string, MapEntity>(StringComparer.Ordinal);
        var nativeProfiles = project.Definition.Systems.Profiles.GroupBy(item => item.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var materializedDockingHardware = new HashSet<(string Prototype, Cell Cell)>();
        var equipmentPorts = equipment.Where(entity => entity.InterfaceId.Length > 0).Select(entity => entity.InterfaceId).ToHashSet(StringComparer.Ordinal);
        var portCells = project.Layout.Interfaces.Where(port => port.Kind != "utility").SelectMany(port => port.Cells).ToHashSet();
        var physicalBoundaryPorts = project.Layout.Interfaces.Where(port => port.Kind != "utility").Select(port => port.Id).ToHashSet(StringComparer.Ordinal);
        var boundaryEquipment = equipment.Where(entity => physicalBoundaryPorts.Contains(entity.InterfaceId)).SelectMany(entity => entity.Footprint).ToHashSet();
        var windowCells = project.Layout.Windows.ToHashSet();
        foreach (var cell in project.Layout.Walls.Concat(project.Layout.Hull).Distinct().Except(portCells).Except(windowCells).Except(boundaryEquipment).OrderBy(cell => cell.Y).ThenBy(cell => cell.X))
            Add(profile.WallPrototype, cell);
        foreach (var cell in windowCells.Except(portCells).Except(boundaryEquipment).OrderBy(cell => cell.Y).ThenBy(cell => cell.X)) Add(profile.WindowPrototype, cell);
        foreach (var port in project.Layout.Interfaces.Where(port => !equipmentPorts.Contains(port.Id)).OrderBy(port => port.Id, StringComparer.Ordinal))
        {
            if (port.Kind == "utility" && NativeUtilityReplaced(port.Type)) continue;
            var prototype = port.Type switch
            {
                "counter" => profile.CounterPrototype, "window" => profile.WindowPrototype, "dockingPort" => profile.DockingPortPrototype,
                "pipe" => profile.PipePrototype, "cable" => profile.CablePrototype,
                _ => port.From is "$space" or "$exterior-maintenance" || port.To is "$space" or "$exterior-maintenance" ? profile.SpaceAirlockPrototype : profile.DoorPrototype
            };
            foreach (var cell in port.Cells)
            {
                var designerId = DesignSystems.InterfaceEntityId(port.Id, cell);
                var components = NativeComponents(designerId, prototype);
                if (port.Kind == "walking") AddAccess(components, port.Access, port.From, port.To);
                if (port.Type == "dockingPort") AddDocking(components);
                var rotation = port.Kind == "walking" ? DesignSystems.InterfaceRotation(port) : 0;
                if (Add(prototype, cell, rotation, components, designerId) && port.Type == "dockingPort") materializedDockingHardware.Add((prototype, cell));
            }
        }
        foreach (var entity in equipment)
        {
            var components = NativeComponents(entity.Id, entity.Prototype);
            var port = project.Layout.Interfaces.FirstOrDefault(port => port.Id == entity.InterfaceId);
            if (port?.Kind == "walking") AddAccess(components, port.Access, port.From, port.To);
            if (port?.Type == "dockingPort") AddDocking(components);
            if (Add(entity.Prototype, entity.Position, entity.Rotation, components, entity.Id) && port?.Type == "dockingPort") materializedDockingHardware.Add((entity.Prototype, entity.Position));
        }
        foreach (var group in project.Layout.Utilities.GroupBy(utility => utility.Type, StringComparer.Ordinal))
        {
            if (project.Definition.Systems.Enabled && project.Definition.Systems.Networks.Any(network => network.Kind == (group.Key == "pipe" ? "gas" : "power")))
            { diagnostics.Add(new("systems", "legacy-utility-suppressed", "Configured native networks replace generic " + group.Key + " route materialization; only actual native segment profiles are emitted.", "info")); continue; }
            var cells = group.SelectMany(utility => utility.Cells).ToHashSet();
            foreach (var cell in cells.OrderBy(cell => cell.Y).ThenBy(cell => cell.X))
            {
                if (group.Key != "pipe") { Add(profile.CablePrototype, cell); continue; }
                var neighbors = Cell.Directions.Select((direction, index) => (direction, index)).Where(item => cells.Contains(cell + item.direction)).Select(item => item.index).ToList();
                var prototype = neighbors.Count >= 4 ? profile.PipeCrossPrototype : neighbors.Count == 3 ? profile.PipeJunctionPrototype : neighbors.Count == 2 && (neighbors[0] + 2) % 4 != neighbors[1] ? profile.PipeBendPrototype : profile.PipePrototype;
                var rotation = PipeRotation(neighbors, prototype == profile.PipeBendPrototype, prototype == profile.PipeJunctionPrototype);
                Add(prototype, cell, rotation);
            }
        }
        foreach (var network in project.Layout.SystemNetworks.OrderBy(network => network.Id, StringComparer.Ordinal))
        foreach (var segment in network.Segments.OrderBy(segment => segment.Id, StringComparer.Ordinal))
        {
            if (!nativeProfiles.TryGetValue(segment.ProfileId, out var nativeProfile) || nativeProfile.Kind == "device")
            { diagnostics.Add(new(segment.Id, "native-export-profile", "A native segment has no current underfloor materialization profile.")); continue; }
            Add(nativeProfile.Prototype, segment.Position, segment.Rotation, ProfileComponents(nativeProfile), segment.Id, distinctInstance: true);
        }
        foreach (var network in project.Layout.TransportNetworks.OrderBy(network => network.Id, StringComparer.Ordinal))
        {
            var transportProfile = project.Settings.TransportProfiles.FirstOrDefault(item => item.Id == network.ProfileId);
            foreach (var segment in network.Segments.OrderBy(segment => segment.Id, StringComparer.Ordinal)) Add(segment.Prototype, segment.Position, segment.Rotation);
            foreach (var terminal in network.Terminals.OrderBy(terminal => terminal.Id, StringComparer.Ordinal))
            {
                var components = NativeComponents(terminal.Id, terminal.Prototype);
                if (network.Mode == "mail")
                {
                    components.Add(Component("MailingUnit", ("tag", terminal.Tag), ("target", terminal.TargetTag), ("targetList", new[] { terminal.TargetTag })));
                    if (terminal.DeviceFrequency is { } frequency)
                        components.Add(Component("DeviceNetwork", ("receiveFrequency", frequency), ("transmitFrequency", frequency)));
                    components.Add(Component("Configuration", ("config", new Dictionary<string, string> { ["tag"] = terminal.Tag })));
                }
                Add(terminal.Prototype, terminal.Position, terminal.Rotation, components, terminal.Id);
            }
            if (transportProfile != null && !(project.Definition.Systems.Enabled && project.Definition.Systems.Networks.Any(rule => rule.Kind == "power")))
                foreach (var cell in network.Cells) Add(transportProfile.SupplyCablePrototype, cell);
        }
        if (project.Layout.TransportNetworks.Count > 0)
            diagnostics.Add(new("transport", "transport-operation-unverified", "Endpoint units, trunks and rotated tube segments are materialized separately from walking floor. Unit power, flushing, pressure behavior and in-engine routing still need simulation verification.", "warning"));
        if (profile.IncludeRoomBeacons)
            foreach (var room in rooms.Where(room => room.Cells.Count > 0))
                Add(profile.BeaconPrototype, room.Cells[0], 0, [Component("NavMapBeacon", ("text", definitions[room.DefinitionPath].Name.Length == 0 ? room.DefinitionPath : definitions[room.DefinitionPath].Name))]);
        var occupied = equipment.SelectMany(entity => entity.Footprint).Concat(DesignTransport.EndpointFloorCells(project)).ToHashSet();
        var spawn = project.Layout.PublicCorridors.Concat(rooms.SelectMany(room => room.Cells)).Where(cell => !occupied.Contains(cell)).Select(cell => (Cell?) cell).FirstOrDefault();
        if (spawn != null) Add(profile.LatejoinPrototype, spawn.Value);
        else diagnostics.Add(new("$station", "export-spawn", "No clear floor tile is available for a latejoin marker."));

        foreach (var control in project.Layout.SystemControls)
        {
            var binding = project.Layout.SystemDevices.FirstOrDefault(device => device.Id == control.ControllerDeviceId);
            var rule = project.Definition.Systems.Controls.FirstOrDefault(rule => rule.Id == control.RequirementId);
            if (binding == null || rule?.Component != "DeviceList" || !nativeEntityIds.TryGetValue(binding.EntityId, out var controller))
            { diagnostics.Add(new(control.Id, "native-export-control", "The native controller has no actual emitted entity UID or supported control component.")); continue; }
            var deviceUids = new List<int>();
            foreach (var id in control.DeviceIds)
            {
                var device = project.Layout.SystemDevices.FirstOrDefault(device => device.Id == id);
                if (device == null || !nativeEntityIds.TryGetValue(device.EntityId, out var materialized))
                { diagnostics.Add(new(control.Id, "native-export-control", "Controlled device " + id + " has no actual emitted native entity UID.")); continue; }
                deviceUids.Add(materialized.Uid);
            }
            MergeComponent(controller.Components, Component("DeviceList", ("devices", deviceUids.Distinct().Order().ToList()), ("isAllowList", true), ("deviceLimit", rule.MaxDevices)));
        }

        var groups = entities.GroupBy(entity => entity.Prototype, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new Dictionary<string, object> { ["proto"] = group.Key, ["entities"] = group.Select(entity => new Dictionary<string, object> { ["uid"] = entity.Uid, ["components"] = entity.Components }).ToList() }).ToList();
        result.MapYaml = Yaml(new Dictionary<string, object>
        {
            ["meta"] = new Dictionary<string, object> { ["format"] = 7, ["category"] = "Map", ["engineVersion"] = profile.EngineVersion, ["forkId"] = "", ["forkVersion"] = "", ["entityCount"] = entities.Count },
            ["maps"] = new[] { 1 }, ["grids"] = new[] { 2 }, ["orphans"] = Array.Empty<int>(), ["nullspace"] = Array.Empty<int>(),
            ["tilemap"] = tileNames.ToDictionary(pair => pair.Key.ToString(CultureInfo.InvariantCulture), pair => pair.Value), ["entities"] = groups
        }, "Generated SS14 format-7 map draft. Review its manifest before use in a round.");
        var stationComponents = new List<object>();
        if (profile.Jobs.Count > 0)
        {
            stationComponents.Add(Component("StationJobs", ("availableJobs", profile.Jobs)));
            diagnostics.Add(new("$station", "export-job-spawns", "Job slots are configured, but role-specific spawn markers still require in-engine review; the generic latejoin marker is not a complete job-spawn program.", "warning"));
        }
        else diagnostics.Add(new("$station", "export-jobs", "No job-slot profile is configured. Add Settings.Export.Jobs and job spawn points before round deployment.", "warning"));
        result.DescriptorYaml = Yaml(new[] { new Dictionary<string, object>
        {
            ["type"] = "gameMap", ["id"] = stationId, ["mapName"] = project.Definition.Name,
            ["mapPath"] = "/Maps/Generated/" + result.MapFileName,
            ["stations"] = new Dictionary<string, object> { [stationId] = new Dictionary<string, object> { ["stationProto"] = profile.StationPrototype, ["components"] = stationComponents } }
        } }, "Companion descriptor. Install alongside Resources/Maps/Generated/" + result.MapFileName + ".");
        var unprofiled = equipment.Select(entity => entity.Prototype).Where(prototype => !project.Settings.PrototypeFootprints.ContainsKey(prototype)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (unprofiled.Count > 0) diagnostics.Add(new("$station", "export-footprints", "Some equipment uses one-tile planning footprints. Validate actual prototype colliders and clearances in the map editor.", "warning"));
        diagnostics.Add(new("$station", "simulation-unverified", "This is a map-editor draft. Power delivery, pipenet behavior, hazard containment, docking, lighting, jobs and atmosphere simulation require in-engine verification.", "warning"));
        result.ManifestJson = JsonSerializer.Serialize(new
        {
            format = "StationArchitectMapDraft", version = 1, mapFile = result.MapFileName, descriptorFile = result.DescriptorFileName,
            schema = 7, sourceProgram = project.Definition.Id, seed = project.Settings.Seed,
            simulationVerified = false, operational = false, stage = project.Stage,
            geometryAccepted = validation.Success && validation.Complete && nativeValidation.Success && nativeValidation.Complete,
            entities = entities.Count, floorTiles = tiles.Count, roomInstances = rooms.Count,
            plannedEnvironmentZones = project.Layout.EnvironmentZones.Count,
            gameflow = project.Gameflow,
            systems = nativeValidation,
            nativeMaterialization = new
            {
                profiles = project.Definition.Systems.Profiles,
                devices = project.Layout.SystemDevices.Select(device => new { binding = device, entityUid = nativeEntityIds.GetValueOrDefault(device.EntityId)?.Uid }).ToList(),
                networks = project.Layout.SystemNetworks,
                segmentUids = project.Layout.SystemNetworks.SelectMany(network => network.Segments).GroupBy(segment => segment.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => (int?) nativeEntityIds.GetValueOrDefault(group.Key)?.Uid, StringComparer.Ordinal),
                controls = project.Layout.SystemControls.Select(control => new
                {
                    control.Id, control.RequirementId,
                    controllerUid = nativeEntityIds.GetValueOrDefault(project.Layout.SystemDevices.FirstOrDefault(device => device.Id == control.ControllerDeviceId)?.EntityId ?? "")?.Uid,
                    deviceUids = control.DeviceIds.Select(id => project.Layout.SystemDevices.FirstOrDefault(device => device.Id == id)?.EntityId ?? "").Select(id => nativeEntityIds.GetValueOrDefault(id)?.Uid).Where(id => id != null).Distinct().ToList()
                }).ToList(),
                repositoryAuditVerified = false, simulationVerified = false
            },
            dockingReservations = project.Layout.Docks.OrderBy(dock => dock.Id, StringComparer.Ordinal).Select(dock =>
            {
                var entity = equipment.FirstOrDefault(entity => entity.Id == dock.EntityId);
                return new
                {
                    dock.Id, dock.RoomId, dock.RequirementId, dock.ContentId, dock.Instance,
                    dock.BoundaryCells, dock.Outward, dock.BerthCells, dock.ApproachCells,
                    dock.EntityId, dock.InterfaceId,
                    geometryAndHardwareAccepted = !DesignDockGeometry.ValidateDock(project, dock, hardwareRequired: true).Any(issue => issue.Severity == "error"),
                    prototype = entity?.Prototype, position = entity?.Position, rotation = entity?.Rotation,
                    nativeDockingComponentsMaterialized = entity != null && materializedDockingHardware.Contains((entity.Prototype, entity.Position)),
                    inheritedPrototypeSettingsPreserved = true,
                    simulationVerified = false
                };
            }).ToList(),
            unverifiedFootprintPrototypes = unprofiled,
            diagnostics = diagnostics.Distinct().ToList()
        }, ProgramJson.Options);
        return result;

        void AddDocking(List<object> components)
        {
            // Native map loading combines these explicit component mappings with
            // prototype data, retaining inherited priority/legend configuration.
            components.Add(Component("Docking"));
            components.Add(Component("DockingSignalControl"));
        }

        List<object> ProfileComponents(NativeSystemProfile nativeProfile) => nativeProfile.Components.Select(pair =>
        {
            var component = Component(pair.Key);
            if (pair.Value.ValueKind == JsonValueKind.Object)
                foreach (var property in pair.Value.EnumerateObject().Where(property => property.Name != "type")) component[property.Name] = property.Value.Clone();
            return (object) component;
        }).ToList();
        List<object> NativeComponents(string designerId, string prototype)
        {
            var binding = project.Layout.SystemDevices.FirstOrDefault(device => device.EntityId == designerId && nativeProfiles.TryGetValue(device.ProfileId, out var item) && item.Prototype == prototype);
            return binding != null && nativeProfiles.TryGetValue(binding.ProfileId, out var item) ? ProfileComponents(item) : [];
        }
        bool NativeUtilityReplaced(string type) => project.Definition.Systems.Enabled && project.Definition.Systems.Networks.Any(network => network.Kind == (type == "pipe" ? "gas" : type == "cable" ? "power" : ""));
        bool Add(string prototype, Cell cell, int rotation = 0, List<object>? extra = null, string? designerId = null, bool distinctInstance = false)
        {
            if (string.IsNullOrWhiteSpace(prototype)) { diagnostics.Add(new("$station", "export-profile", "An export prototype is empty; configure the materialization profile.")); return false; }
            if (!emitted.Add((prototype, cell)) && !distinctInstance)
            {
                if (designerId != null && emittedEntities.TryGetValue((prototype, cell), out var existing)) nativeEntityIds[designerId] = existing;
                return false;
            }
            var transform = Component("Transform", ("parent", 2), ("pos", FormattableString.Invariant($"{cell.X + .5},{cell.Y + .5}")), ("anchored", true));
            if (rotation != 0) transform["rot"] = rotation.ToString(CultureInfo.InvariantCulture) + " deg";
            var components = new List<object> { transform }; if (extra != null) foreach (var component in extra) MergeComponent(components, (Dictionary<string, object>) component);
            var materialized = Entity(uid++, prototype, components); entities.Add(materialized); emittedEntities[(prototype, cell)] = materialized;
            if (designerId != null) nativeEntityIds[designerId] = materialized;
            return true;
        }
        void AddAccess(List<object> components, string label, string from, string to)
        {
            var endpoints = rooms.Where(room => room.Id == from || room.Id == to).ToList();
            var combined = new HashSet<string>(StringComparer.Ordinal);
            if (endpoints.Count > 0)
            {
                foreach (var endpoint in endpoints)
                {
                    var authored = definitions[endpoint.DefinitionPath].Access?.Level ?? "unassigned";
                    var tags = profile.AccessTags.GetValueOrDefault(authored) ?? profile.OwnerAccessTags.GetValueOrDefault(endpoint.OwnerId);
                    if (tags == null) diagnostics.Add(new(authored, "export-access", "This room's authored access label has no export tag mapping; its door needs access review.", "warning"));
                    else combined.UnionWith(tags);
                }
            }
            else
            {
                var tags = profile.AccessTags.GetValueOrDefault(label);
                if (tags == null) diagnostics.Add(new(label, "export-access", "This network interface has no export access-tag mapping; its door needs access review.", "warning"));
                else combined.UnionWith(tags);
            }
            if (combined.Count > 0) components.Add(Component("AccessReader", ("access", new[] { combined.Order(StringComparer.Ordinal).ToList() })));
        }
    }

    private static object Chunks(Dictionary<Cell, int> tiles)
    {
        var chunks = new Dictionary<string, object>();
        foreach (var group in tiles.GroupBy(pair => new Cell(FloorDivide(pair.Key.X, 16), FloorDivide(pair.Key.Y, 16))).OrderBy(group => group.Key.Y).ThenBy(group => group.Key.X))
        {
            var bytes = new byte[16 * 16 * 7];
            using var stream = new MemoryStream(bytes); using var writer = new BinaryWriter(stream);
            var lookup = group.ToDictionary(pair => pair.Key, pair => pair.Value);
            for (var y = 0; y < 16; y++) for (var x = 0; x < 16; x++)
            { writer.Write(lookup.GetValueOrDefault(new(group.Key.X * 16 + x, group.Key.Y * 16 + y))); writer.Write((byte) 0); writer.Write((byte) 0); writer.Write((byte) 0); }
            var key = group.Key.X + "," + group.Key.Y;
            chunks[key] = new Dictionary<string, object> { ["ind"] = key, ["tiles"] = Convert.ToBase64String(bytes), ["version"] = 7 };
        }
        return chunks;
    }

    private static object Atmosphere(StationDesignProject project, List<DesignRoom> rooms, List<ProgramDiagnostic> diagnostics)
    {
        var mixes = new List<object>(); var signatures = new Dictionary<string, int>(StringComparer.Ordinal);
        var assigned = new Dictionary<Cell, int>();
        foreach (var zone in project.Layout.EnvironmentZones.OrderBy(zone => zone.Id, StringComparer.Ordinal))
        {
            if (zone.PressureKpa == null)
            { diagnostics.Add(new(zone.RoomId, "export-atmosphere", "Custom pressure or gas composition is unresolved; the map draft leaves this zone uninitialized.", "warning")); continue; }
            var temperature = zone.TemperatureKelvin ?? 293.15;
            if (temperature <= 0 || !double.IsFinite(temperature) || !double.IsFinite(zone.PressureKpa.Value) || zone.PressureKpa < 0)
            { diagnostics.Add(new(zone.RoomId, "export-atmosphere", "Initial gas pressure and temperature must be physically finite; this zone was not serialized.")); continue; }
            var moles = zone.PressureKpa.Value * 2500 / (8.314462618 * temperature);
            if (zone.GasFractions.Any(pair => !DesignEnvironmentGases.Supported.Contains(pair.Key) || !double.IsFinite(pair.Value) || pair.Value is < 0 or > 1) || zone.GasFractions.Count > 0 && Math.Abs(zone.GasFractions.Values.Sum() - 1) > 1e-6 || zone.PressureKpa > 0 && zone.GasFractions.Count == 0)
            { diagnostics.Add(new(zone.RoomId, "export-atmosphere", "Gas fractions are unknown, non-finite or do not normalize to one; this invalid mixture was not serialized.")); continue; }
            var gases = zone.GasFractions.Where(pair => pair.Value > 0 && double.IsFinite(pair.Value)).ToDictionary(pair => pair.Key, pair => pair.Value * moles);
            var mix = new Dictionary<string, object> { ["temperature"] = temperature, ["volume"] = 2500, ["moles"] = gases };
            var signature = JsonSerializer.Serialize(mix);
            if (!signatures.TryGetValue(signature, out var index)) { index = mixes.Count; signatures[signature] = index; mixes.Add(mix); }
            foreach (var cell in zone.Cells) assigned[cell] = index;
        }
        // Explicit initial normal air for built public/maintenance circulation. Other missing
        // room zones remain missing instead of silently copying their requirements.
        var ambient = new Dictionary<string, object> { ["temperature"] = 293.15, ["volume"] = 2500, ["moles"] = new Dictionary<string, double> { ["Oxygen"] = 21.824879, ["Nitrogen"] = 82.10312 } };
        var ambientIndex = mixes.Count; mixes.Add(ambient);
        foreach (var cell in project.Layout.PublicCorridors.Concat(project.Layout.Maintenance).Concat(project.Layout.Interfaces.Where(port => port.To != "$space" && port.From != "$space").SelectMany(port => port.Cells))) assigned.TryAdd(cell, ambientIndex);
        foreach (var room in rooms.Where(room => room.Cells.Any(cell => !assigned.ContainsKey(cell))))
            diagnostics.Add(new(room.Id, "export-atmosphere", "Some room floor lacks an explicit environment zone; no atmosphere was invented for it.", "warning"));
        var chunks = new Dictionary<string, Dictionary<string, uint>>();
        foreach (var (cell, mix) in assigned)
        {
            var origin = new Cell(FloorDivide(cell.X, 4), FloorDivide(cell.Y, 4));
            var key = origin.X + "," + origin.Y;
            if (!chunks.TryGetValue(key, out var flags)) chunks[key] = flags = [];
            var index = cell.X - origin.X * 4 + (cell.Y - origin.Y * 4) * 4;
            var mixKey = mix.ToString(CultureInfo.InvariantCulture); flags[mixKey] = flags.GetValueOrDefault(mixKey) | 1u << index;
        }
        return new Dictionary<string, object> { ["uniqueMixes"] = mixes, ["tiles"] = chunks, ["chunkSize"] = 4 };
    }

    private static int PipeRotation(List<int> neighbors, bool bend, bool junction)
    {
        if (junction)
        {
            var missing = Enumerable.Range(0, 4).First(index => !neighbors.Contains(index));
            // Default TSouth omits north (direction index 1).
            return (missing + 3) % 4 * 90;
        }
        if (bend)
            // Default SWBend connects west/south (direction indices 2,3).
            for (var quarter = 0; quarter < 4; quarter++) if (neighbors.Contains(quarter) && neighbors.Contains((quarter + 1) % 4)) return (quarter + 2) % 4 * 90;
        return neighbors.Any(index => index is 0 or 2) ? 90 : 0;
    }
    private static int FloorDivide(int value, int divisor) => (int) Math.Floor((double) value / divisor);
    private static string Sanitize(string text) { var result = new string(text.Where(char.IsAsciiLetterOrDigit).ToArray()); return result.Length == 0 ? "Station" : result; }
    private static MapEntity Entity(int uid, string prototype, List<object> components) => new(uid, prototype, components);
    private sealed record MapEntity(int Uid, string Prototype, List<object> Components);
    private static void MergeComponent(List<object> components, Dictionary<string, object> component)
    {
        var existing = components.OfType<Dictionary<string, object>>().FirstOrDefault(item => item.GetValueOrDefault("type")?.ToString() == component.GetValueOrDefault("type")?.ToString());
        if (existing == null) components.Add(component);
        else foreach (var (key, value) in component) existing[key] = value;
    }
    private static Dictionary<string, object> Component(string type, params (string Key, object Value)[] fields)
    { var result = new Dictionary<string, object> { ["type"] = type }; foreach (var (key, value) in fields) result[key] = value; return result; }
    private static string Yaml(object data, string comment)
    {
        var json = JsonSerializer.SerializeToElement(data, ProgramJson.Options);
        var stream = new YamlStream(new YamlDocument(ToYaml(json)));
        using var writer = new StringWriter(CultureInfo.InvariantCulture); writer.WriteLine("# " + comment); stream.Save(writer, false); return writer.ToString();
    }
    private static YamlNode ToYaml(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => new YamlMappingNode(value.EnumerateObject().Select(property => new KeyValuePair<YamlNode, YamlNode>(new YamlScalarNode(property.Name), ToYaml(property.Value)))),
        JsonValueKind.Array => new YamlSequenceNode(value.EnumerateArray().Select(ToYaml)),
        JsonValueKind.String => new YamlScalarNode(value.GetString()) { Style = ScalarStyle.DoubleQuoted },
        JsonValueKind.Null => new YamlScalarNode("null") { Style = ScalarStyle.Plain },
        _ => new YamlScalarNode(value.GetRawText()) { Style = ScalarStyle.Plain }
    };
}
