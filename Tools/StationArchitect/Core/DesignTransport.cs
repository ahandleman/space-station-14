namespace Content.StationArchitect;

/// <summary>Configurable physical pneumatic hardware. Directions are prototype-local engine directions.</summary>
public sealed class DesignTransportProfile
{
    public string Id { get; set; } = "PneumaticCrew";
    public string Mode { get; set; } = "disposalShortcut";
    public bool CarriesCrew { get; set; } = true;
    public string EntryUnitPrototype { get; set; } = "DisposalUnit";
    public string ExitUnitPrototype { get; set; } = "DisposalUnit";
    public string TrunkPrototype { get; set; } = "DisposalTrunk";
    public string StraightPrototype { get; set; } = "DisposalPipe";
    public string BendPrototype { get; set; } = "DisposalBend";
    public List<string> TrunkExits { get; set; } = ["South"];
    public List<string> StraightExits { get; set; } = ["South", "North"];
    public List<string> BendExits { get; set; } = ["South", "West"];
    public string SupplyCablePrototype { get; set; } = "CableApcExtension";
    public int EndpointWidth { get; set; } = 1;
    public int EndpointHeight { get; set; } = 1;
    public int EndpointRotation { get; set; }
    public int MinApproachTiles { get; set; } = 1;
    public int MaxLength { get; set; } = 4096;
    public bool AllowUnderStructural { get; set; } = true;
    public bool AllowExteriorFloor { get; set; }
    public int MailFrequencyMin { get; set; } = 40000;
    public int MailFrequencyMax { get; set; } = 65535;
    public List<string> SourceRefs { get; set; } = [];
}

public sealed class DesignTransportSegment
{
    public string Id { get; set; } = "";
    public string Role { get; set; } = "straight";
    public string Prototype { get; set; } = "";
    public Cell Position { get; set; }
    public int Rotation { get; set; }
}

public sealed class DesignTransportTerminal
{
    public string Id { get; set; } = "";
    public string RoomId { get; set; } = "";
    public string Prototype { get; set; } = "";
    public Cell Position { get; set; }
    public int Rotation { get; set; }
    public List<Cell> Footprint { get; set; } = [];
    public string Tag { get; set; } = "";
    public string TargetTag { get; set; } = "";
    public int? DeviceFrequency { get; set; }
}

/// <summary>Separate underfloor network; its pipe cells are not crew-walkable corridor floor.</summary>
public sealed class DesignTransportNetwork
{
    public string Id { get; set; } = "";
    public string RequirementId { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Mode { get; set; } = "disposalShortcut";
    public string ProfileId { get; set; } = "PneumaticCrew";
    public int? DeviceFrequency { get; set; }
    public List<Cell> Cells { get; set; } = [];
    public List<DesignTransportSegment> Segments { get; set; } = [];
    public List<DesignTransportTerminal> Terminals { get; set; } = [];
    public bool Edited { get; set; }
    public bool Locked { get; set; }
}

public static class DesignTransportDefaults
{
    public static List<DesignTransportProfile> Profiles() =>
    [
        new() { SourceRefs = ["Resources/Maps/exo.yml#uid=5833", "Resources/Maps/exo.yml#uid=16550", "Resources/Prototypes/Entities/Structures/Piping/Disposal/pipes.yml"] },
        new() { Id = "PneumaticMail", Mode = "mail", CarriesCrew = false, EntryUnitPrototype = "MailingUnit", ExitUnitPrototype = "MailingUnit",
            SourceRefs = ["Resources/Maps/sushi.yml#uid=87", "Resources/Prototypes/Entities/Structures/Piping/Disposal/units.yml"] }
    ];
}

/// <summary>
/// Builds dedicated, bidirectional point-to-point pneumatic tubes. A trunk at each endpoint
/// accepts a flush; arrival from its sole tube direction causes the holder to exit into the unit.
/// No junctions are invented, and independent lines cannot accidentally connect to each other.
/// Hardware geometry is verified; operating power, gas and simulation remain an export review.
/// </summary>
public static class DesignTransport
{
    public static HashSet<Cell> EndpointFloorCells(StationDesignProject project) =>
        (project.Layout.TransportNetworks ?? []).Where(network => network != null).SelectMany(network => network.Terminals ?? []).Where(terminal => terminal != null).SelectMany(terminal => terminal.Footprint ?? []).ToHashSet();

    /// <summary>Actual crew shortcut edges for access-bypass checks, never atmosphere connectivity.</summary>
    public static IEnumerable<(Cell From, Cell To)> CrewEdges(StationDesignProject project)
    {
        if (project.Layout.TransportNetworks is not { Count: > 0 }) yield break;
        if (ValidateConfiguration(project).Count > 0) yield break;
        var compilation = RequirementCompiler.Compile(project.Definition);
        if (!compilation.Success) yield break;
        var requirements = compilation.Connections.ToDictionary(connection => connection.Id, StringComparer.Ordinal);
        foreach (var network in project.Layout.TransportNetworks ?? [])
        {
            if (network == null || !requirements.TryGetValue(network.RequirementId, out var requirement)) continue;
            var profile = project.Settings.TransportProfiles?.FirstOrDefault(profile => profile != null && profile.Id == network.ProfileId);
            if (profile?.CarriesCrew != true || !RouteValid(project, Route(network, requirement.Kind))) continue;
            yield return (network.Terminals[0].Position, network.Terminals[1].Position);
        }
    }

    public static List<ProgramDiagnostic> ValidateConfiguration(StationDesignProject project)
    {
        var diagnostics = new List<ProgramDiagnostic>();
        var profiles = project.Settings.TransportProfiles;
        if (profiles == null || profiles.Count > 16) { diagnostics.Add(new("settings.transportProfiles", "transport-profile", "Use at most 16 non-null transport profiles.")); return diagnostics; }
        var ids = new HashSet<string>(StringComparer.Ordinal); var modes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in profiles)
        {
            if (profile == null) { diagnostics.Add(new("settings.transportProfiles", "transport-profile", "Transport profiles cannot be null.")); continue; }
            var path = "settings.transportProfiles:" + profile.Id;
            void Error(string message) => diagnostics.Add(new(path, "transport-profile", message));
            if (string.IsNullOrWhiteSpace(profile.Id) || profile.Id.Length > 64 || !ids.Add(profile.Id)) Error("Profile IDs must be unique and contain 1–64 characters.");
            if (profile.Mode is not ("disposalShortcut" or "mail") || !modes.Add(profile.Mode)) Error("Provide at most one hardware profile per supported pneumatic mode.");
            if (profile.Mode == "mail" && profile.CarriesCrew) Error("Mailing units carry items, not crew.");
            if (profile.MailFrequencyMin is < 1 or > 65535 || profile.MailFrequencyMax < profile.MailFrequencyMin || profile.MailFrequencyMax > 65535)
                Error("Mail frequency bounds must be ordered within the numeric device frequency range 1–65535.");
            foreach (var prototype in new[] { profile.EntryUnitPrototype, profile.ExitUnitPrototype, profile.TrunkPrototype, profile.StraightPrototype, profile.BendPrototype, profile.SupplyCablePrototype })
                if (string.IsNullOrWhiteSpace(prototype) || prototype.Length > 128) Error("Transport hardware needs nonempty bounded prototype IDs.");
            if (profile.EndpointWidth is < 1 or > 4 || profile.EndpointHeight is < 1 or > 4 || profile.EndpointRotation is < 0 or >= 360 || profile.EndpointRotation % 90 != 0 || profile.MinApproachTiles is < 0 or > 8 || profile.MaxLength is < 2 or > 65536)
                Error("Endpoint dimensions, rotation, approach and maximum length exceed their bounds.");
            if (!ExitsValid(profile.TrunkExits, 1) || !ExitsValid(profile.StraightExits, 2) || !ExitsValid(profile.BendExits, 2)) Error("Provide one cardinal trunk exit and two unique cardinal exits for straight and bend prototypes.");
            else
            {
                if (Vector(profile.StraightExits[0]) + Vector(profile.StraightExits[1]) != new Cell(0, 0)) Error("The straight prototype's exits must oppose each other.");
                if (Vector(profile.BendExits[0]) + Vector(profile.BendExits[1]) == new Cell(0, 0)) Error("The bend prototype's exits must be perpendicular.");
            }
            if (profile.SourceRefs == null || profile.SourceRefs.Count > 128 || profile.SourceRefs.Any(source => string.IsNullOrWhiteSpace(source) || source.Length > 2048)) Error("Source references must be bounded nonempty text.");
        }
        return diagnostics;
    }

    public static DesignRoute? TryBuild(StationDesignProject project, DesignRoom from, DesignRoom to, CompiledConnection requirement, string mode)
    {
        if (project.Layout.TransportNetworks == null || mode is not ("disposalShortcut" or "mail") || from.Id == to.Id || requirement.Kind == "interaction" || requirement.MinWidth > 1 || ValidateConfiguration(project).Count > 0) return null;
        var profile = project.Settings.TransportProfiles.SingleOrDefault(candidate => candidate.Mode == mode);
        if (profile == null || requirement.Kind == "walking" && (!profile.CarriesCrew || mode == "mail")) return null;
        if (requirement.InterfaceTypes.Count > 0 && (requirement.Kind == "walking" || !requirement.InterfaceTypes.Contains("pipe", StringComparer.Ordinal))) return null;
        if (!RequirementCompiler.Compile(project.Definition).Success) return null;
        var definitions = LayoutGeometry.Definitions(project);
        if (!definitions.TryGetValue(from.DefinitionPath, out var fromDefinition) || !definitions.TryGetValue(to.DefinitionPath, out var toDefinition)) return null;
        if (fromDefinition.Access.RequireNetworkApproach.Count > 0 && !fromDefinition.Access.RequireNetworkApproach.Contains(mode, StringComparer.Ordinal) ||
            toDefinition.Access.RequireNetworkApproach.Count > 0 && !toDefinition.Access.RequireNetworkApproach.Contains(mode, StringComparer.Ordinal)) return null;
        var id = "transport:" + requirement.Id + "@" + from.Id + ":" + to.Id + ":" + mode;
        var existing = project.Layout.TransportNetworks.FirstOrDefault(network => network.Id == id);
        if (existing != null)
        {
            var previous = Route(existing, requirement.Kind);
            if (RouteValid(project, previous)) return previous;
            if (existing.Edited || existing.Locked) return null;
            project.Layout.TransportNetworks.Remove(existing);
        }
        var support = SupportedFloor(project, profile);
        var other = project.Layout.TransportNetworks.SelectMany(network => network.Cells).ToHashSet();
        var otherHalo = other.Concat(other.SelectMany(cell => Cell.Directions.Select(direction => cell + direction))).ToHashSet();
        support.ExceptWith(otherHalo);
        var blockedEquipment = project.Layout.Equipment.SelectMany(entity => entity.Footprint).Concat(EndpointFloorCells(project)).ToHashSet();
        var walking = project.Layout.Routes.Where(route => route.Kind == "walking" && route.Mode is not ("disposalShortcut" or "mail")).SelectMany(route => route.Cells)
            .Concat(project.Layout.Interfaces.SelectMany(port => new[] { port.FromApproach, port.ToApproach })).ToHashSet();
        var sources = TerminalCandidates(from, profile, blockedEquipment, walking).Where(cell => support.Contains(cell)).OrderBy(cell => cell.Distance(to.Seed)).ThenBy(cell => cell.Y).ThenBy(cell => cell.X).Take(48).ToList();
        var targets = TerminalCandidates(to, profile, blockedEquipment, walking).Where(cell => support.Contains(cell)).ToHashSet();
        if (sources.Count == 0 || targets.Count == 0) return null;
        List<Cell>? best = null;
        foreach (var start in sources)
        {
            var path = Search(start, targets, support, profile.MaxLength);
            if (path == null || path.Count < 3 || requirement.MaxDistance != null && path.Count - 1 > requirement.MaxDistance) continue;
            if (best == null || path.Count < best.Count) best = path;
            if (path.Count - 1 == targets.Min(target => target.Distance(start))) break;
        }
        if (best == null) return null;
        var network = new DesignTransportNetwork { Id = id, RequirementId = requirement.Id, From = from.Id, To = to.Id, Mode = mode, ProfileId = profile.Id, Cells = best };
        if (mode == "mail")
        {
            network.DeviceFrequency = AllocateMailFrequency(project, profile, id);
            if (network.DeviceFrequency == null) return null;
        }
        for (var index = 0; index < best.Count; index++)
        {
            var desired = new HashSet<Cell>();
            if (index > 0) desired.Add(best[index - 1] - best[index]);
            if (index + 1 < best.Count) desired.Add(best[index + 1] - best[index]);
            var role = index == 0 || index == best.Count - 1 ? "trunk" : desired.Aggregate(new Cell(), (sum, direction) => sum + direction) == new Cell() ? "straight" : "bend";
            var local = role == "trunk" ? profile.TrunkExits : role == "straight" ? profile.StraightExits : profile.BendExits;
            var rotation = FitRotation(local, desired);
            if (rotation == null) return null;
            network.Segments.Add(new() { Id = id + ":segment:" + index, Position = best[index], Role = role, Prototype = role == "trunk" ? profile.TrunkPrototype : role == "straight" ? profile.StraightPrototype : profile.BendPrototype, Rotation = rotation.Value });
        }
        var tagStem = "StationMail " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)))[..12];
        network.Terminals =
        [
            Terminal(id + ":entry", from, best[0], profile.EntryUnitPrototype, profile, tagStem + " A", tagStem + " B"),
            Terminal(id + ":exit", to, best[^1], profile.ExitUnitPrototype, profile, tagStem + " B", tagStem + " A")
        ];
        foreach (var terminal in network.Terminals) terminal.DeviceFrequency = network.DeviceFrequency;
        project.Layout.TransportNetworks.Add(network);
        var route = Route(network, requirement.Kind);
        if (RouteValid(project, route)) return route;
        project.Layout.TransportNetworks.Remove(network);
        return null;
    }

    public static bool RouteValid(StationDesignProject project, DesignRoute route)
    {
        if (route == null || route.Cells == null || project.Layout.TransportNetworks == null || route.Mode is not ("disposalShortcut" or "mail") || ValidateConfiguration(project).Count > 0) return false;
        var compilation = RequirementCompiler.Compile(project.Definition);
        if (!compilation.Success) return false;
        var network = project.Layout.TransportNetworks.FirstOrDefault(network => network != null && network.Id == route.TransportNetworkId);
        if (network == null || network.Mode != route.Mode || network.From != route.From || network.To != route.To || network.RequirementId != route.RequirementId) return false;
        if (network.Cells == null || network.Segments == null || network.Terminals == null || network.Segments.Any(segment => segment == null) || network.Terminals.Any(terminal => terminal == null || terminal.Footprint == null)) return false;
        if (!network.Cells.SequenceEqual(route.Cells)) return false;
        var profile = project.Settings.TransportProfiles.SingleOrDefault(candidate => candidate.Id == network.ProfileId && candidate.Mode == network.Mode);
        if (profile == null || route.Kind == "walking" && (!profile.CarriesCrew || network.Mode == "mail")) return false;
        if (network.Mode == "mail" && !MailFrequencyValid(project, network, profile)) return false;
        if (network.Cells.Count is < 3 or > 65537 || network.Cells.Count - 1 > profile.MaxLength || network.Cells.Distinct().Count() != network.Cells.Count || network.Segments.Count != network.Cells.Count || network.Terminals.Count != 2) return false;
        var support = SupportedFloor(project, profile);
        if (network.Cells.Any(cell => !support.Contains(cell))) return false;
        var definitions = LayoutGeometry.Definitions(project);
        var from = project.Layout.Rooms.FirstOrDefault(room => room.Enabled && !room.Orphaned && room.Id == network.From);
        var to = project.Layout.Rooms.FirstOrDefault(room => room.Enabled && !room.Orphaned && room.Id == network.To);
        if (from == null || to == null || !definitions.ContainsKey(from.DefinitionPath) || !definitions.ContainsKey(to.DefinitionPath)) return false;
        var requirement = compilation.Connections.FirstOrDefault(connection => connection.Id == route.RequirementId);
        if (requirement == null || requirement.Kind != route.Kind || !requirement.Modes.Contains(route.Mode, StringComparer.Ordinal) || requirement.From != from.DefinitionPath && requirement.From != from.OwnerId ||
            !requirement.To.Contains(to.DefinitionPath, StringComparer.Ordinal) && !requirement.To.Contains(to.OwnerId, StringComparer.Ordinal) || requirement.MaxDistance != null && route.Cells.Count - 1 > requirement.MaxDistance || requirement.MinWidth > 1 ||
            requirement.InterfaceTypes.Count > 0 && (requirement.Kind == "walking" || !requirement.InterfaceTypes.Contains("pipe", StringComparer.Ordinal))) return false;
        if (definitions[from.DefinitionPath].Access.RequireNetworkApproach.Count > 0 || definitions[to.DefinitionPath].Access.RequireNetworkApproach.Count > 0) return false;
        var otherPipes = project.Layout.TransportNetworks.Where(other => other.Id != network.Id).SelectMany(other => other.Cells).ToHashSet();
        if (network.Cells.Any(cell => otherPipes.Contains(cell) || Cell.Directions.Any(direction => otherPipes.Contains(cell + direction)))) return false;
        var terminals = network.Terminals;
        if (terminals[0].RoomId != from.Id || terminals[1].RoomId != to.Id || terminals[0].Position != network.Cells[0] || terminals[1].Position != network.Cells[^1] ||
            terminals[0].Prototype != profile.EntryUnitPrototype || terminals[1].Prototype != profile.ExitUnitPrototype) return false;
        foreach (var terminal in terminals)
        {
            var room = terminal.RoomId == from.Id ? from : to;
            var expected = Footprint(terminal.Position, profile).ToHashSet();
            if (terminal.Rotation != profile.EndpointRotation || !expected.SetEquals(terminal.Footprint) || !expected.IsSubsetOf(room.Cells)) return false;
            if (project.Layout.Equipment.Any(entity => entity.Footprint.Any(expected.Contains))) return false;
            if (expected.SelectMany(cell => Cell.Directions.Select(direction => cell + direction)).Except(expected).Distinct().Count(room.Cells.Contains) < profile.MinApproachTiles) return false;
        }
        if (network.Mode == "mail" && (terminals.Any(terminal => !TagValid(terminal.Tag) || !TagValid(terminal.TargetTag)) || terminals[0].Tag == terminals[1].Tag || terminals[0].TargetTag != terminals[1].Tag || terminals[1].TargetTag != terminals[0].Tag)) return false;
        for (var index = 0; index < network.Cells.Count; index++)
        {
            var segment = network.Segments[index]; if (segment.Position != network.Cells[index] || segment.Rotation is < 0 or >= 360 || segment.Rotation % 90 != 0) return false;
            var expected = new HashSet<Cell>();
            if (index > 0) { if (network.Cells[index].Distance(network.Cells[index - 1]) != 1) return false; expected.Add(network.Cells[index - 1] - network.Cells[index]); }
            if (index + 1 < network.Cells.Count) expected.Add(network.Cells[index + 1] - network.Cells[index]);
            var role = index == 0 || index == network.Cells.Count - 1 ? "trunk" : expected.Aggregate(new Cell(), (sum, direction) => sum + direction) == new Cell() ? "straight" : "bend";
            var prototype = role == "trunk" ? profile.TrunkPrototype : role == "straight" ? profile.StraightPrototype : profile.BendPrototype;
            var local = role == "trunk" ? profile.TrunkExits : role == "straight" ? profile.StraightExits : profile.BendExits;
            if (segment.Role != role || segment.Prototype != prototype || !local.Select(exit => Rotate(Vector(exit), segment.Rotation)).ToHashSet().SetEquals(expected)) return false;
        }
        return true;
    }

    public static ConnectionMeasurement? Measure(StationDesignProject project, DesignRoute route)
    {
        if (!RouteValid(project, route)) return null;
        var requirement = RequirementCompiler.Compile(project.Definition).Connections.FirstOrDefault(connection => connection.Id == route.RequirementId);
        var from = project.Layout.Rooms.First(room => room.Id == route.From); var to = project.Layout.Rooms.First(room => room.Id == route.To);
        if (requirement == null) return null;
        return new()
        {
            Id = route.RequirementId, SourceInstance = requirement.From == from.OwnerId ? from.OwnerId : from.Id,
            TargetInstance = requirement.To.Contains(to.OwnerId, StringComparer.Ordinal) ? to.OwnerId : to.Id,
            Kind = route.Kind, Mode = route.Mode, Distance = route.Cells.Count - 1, Width = 1,
            InterfaceType = route.Kind == "utility" ? "pipe" : ""
        };
    }

    public static List<ProgramDiagnostic> Validate(StationDesignProject project)
    {
        var diagnostics = ValidateConfiguration(project);
        if (project.Layout.TransportNetworks == null || project.Layout.TransportNetworks.Count > 4096)
        { diagnostics.Add(new("layout.transportNetworks", "transport-limit", "Use at most 4096 transport networks.")); return diagnostics; }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var compilation = RequirementCompiler.Compile(project.Definition);
        if (!compilation.Success) { diagnostics.AddRange(compilation.Diagnostics); return diagnostics; }
        var requirements = compilation.Connections.ToDictionary(connection => connection.Id, StringComparer.Ordinal);
        foreach (var network in project.Layout.TransportNetworks)
        {
            if (network == null || network.Cells == null || network.Segments == null || network.Terminals == null || network.Segments.Any(segment => segment == null) || network.Terminals.Any(terminal => terminal == null || terminal.Footprint == null))
            { diagnostics.Add(new("layout.transportNetworks", "transport-data", "Transport networks need complete non-null hardware and geometry.")); continue; }
            if (!ids.Add(network.Id) || !requirements.TryGetValue(network.RequirementId, out var requirement))
            { diagnostics.Add(new(network.Id, "transport-reference", "Transport network IDs must be unique and refer to a current connection requirement.")); continue; }
            var profile = project.Settings.TransportProfiles?.FirstOrDefault(profile => profile != null && profile.Id == network.ProfileId);
            if (network.Mode == "mail" && (profile == null || !MailFrequencyValid(project, network, profile)))
                diagnostics.Add(new(network.Id, "transport-mail-frequency", "Each dedicated mail pair needs one unique numeric device frequency, shared by both terminal units and within its configured bounds. Retained edited or locked networks require explicit repair."));
            if (!RouteValid(project, Route(network, requirement.Kind))) diagnostics.Add(new(network.Id, "transport-geometry", "The endpoint units, trunks, rotated tube exits or built-floor support do not form the declared physical transport connection."));
        }
        return diagnostics;
    }

    private static DesignTransportTerminal Terminal(string id, DesignRoom room, Cell position, string prototype, DesignTransportProfile profile, string tag, string target) =>
        new() { Id = id, RoomId = room.Id, Position = position, Prototype = prototype, Rotation = profile.EndpointRotation, Footprint = Footprint(position, profile).ToList(), Tag = tag, TargetTag = target };

    private static int? AllocateMailFrequency(StationDesignProject project, DesignTransportProfile profile, string id)
    {
        var used = project.Layout.TransportNetworks.Where(network => network != null)
            .SelectMany(network => new[] { network.DeviceFrequency }.Concat((network.Terminals ?? []).Where(terminal => terminal != null).Select(terminal => terminal.DeviceFrequency)))
            .Where(frequency => frequency.HasValue).Select(frequency => frequency!.Value).ToHashSet();
        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id));
        var span = profile.MailFrequencyMax - profile.MailFrequencyMin + 1;
        var offset = ((int)digest[0] << 8 | digest[1]) % span;
        for (var probe = 0; probe < span; probe++)
        {
            var frequency = profile.MailFrequencyMin + (offset + probe) % span;
            if (!used.Contains(frequency)) return frequency;
        }
        return null;
    }

    private static bool MailFrequencyValid(StationDesignProject project, DesignTransportNetwork network, DesignTransportProfile profile)
    {
        if (network.DeviceFrequency is not int frequency || frequency < profile.MailFrequencyMin || frequency > profile.MailFrequencyMax || network.Terminals == null || network.Terminals.Count != 2 ||
            network.Terminals.Any(terminal => terminal == null || terminal.DeviceFrequency != frequency)) return false;
        return !project.Layout.TransportNetworks.Any(other => other != null && !ReferenceEquals(other, network) &&
            (other.DeviceFrequency == frequency || (other.Terminals ?? []).Any(terminal => terminal != null && terminal.DeviceFrequency == frequency)));
    }

    private static DesignRoute Route(DesignTransportNetwork network, string kind) => new()
    {
        Id = network.Id + ":route", RequirementId = network.RequirementId, From = network.From, To = network.To,
        Mode = network.Mode, Kind = kind, Cells = [.. (network.Cells ?? [])], TransportNetworkId = network.Id
    };

    private static HashSet<Cell> SupportedFloor(StationDesignProject project, DesignTransportProfile profile)
    {
        var result = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned).SelectMany(room => room.Cells)
            .Concat(project.Layout.PublicCorridors).Concat(project.Layout.Maintenance).Concat(project.Layout.Interfaces.SelectMany(port => port.Cells)).ToHashSet();
        if (profile.AllowUnderStructural) result.UnionWith(project.Layout.Walls.Concat(project.Layout.Hull).Concat(project.Layout.Windows));
        if (!profile.AllowExteriorFloor) result.IntersectWith(project.Layout.Envelope);
        result.ExceptWith(project.Layout.ReservedSpace);
        return result;
    }

    private static IEnumerable<Cell> TerminalCandidates(DesignRoom room, DesignTransportProfile profile, HashSet<Cell> occupied, HashSet<Cell> walking)
    {
        var floor = room.Cells.ToHashSet();
        foreach (var cell in room.Cells)
        {
            var footprint = Footprint(cell, profile).ToHashSet();
            if (!footprint.IsSubsetOf(floor) || footprint.Overlaps(occupied) || footprint.Overlaps(walking)) continue;
            if (footprint.SelectMany(point => Cell.Directions.Select(direction => point + direction)).Except(footprint).Distinct().Count(point => floor.Contains(point) && !occupied.Contains(point)) < profile.MinApproachTiles) continue;
            yield return cell;
        }
    }

    private static IEnumerable<Cell> Footprint(Cell origin, DesignTransportProfile profile) => new GridBounds(origin.X, origin.Y, profile.EndpointWidth, profile.EndpointHeight).Cells();

    private static List<Cell>? Search(Cell start, HashSet<Cell> targets, HashSet<Cell> support, int maxLength)
    {
        var queue = new PriorityQueue<Cell, (int Cost, int Y, int X)>(); var costs = new Dictionary<Cell, int> { [start] = 0 }; var previous = new Dictionary<Cell, Cell>();
        var goalBounds = LayoutGeometry.Bounds(targets);
        int Heuristic(Cell cell) => Math.Max(0, Math.Max(goalBounds.X - cell.X, cell.X - goalBounds.Right + 1)) + Math.Max(0, Math.Max(goalBounds.Y - cell.Y, cell.Y - goalBounds.Bottom + 1));
        queue.Enqueue(start, (Heuristic(start), start.Y, start.X)); var work = 200000;
        while (queue.TryDequeue(out var cell, out _) && work-- > 0)
        {
            if (targets.Contains(cell))
            {
                var path = new List<Cell> { cell }; while (previous.TryGetValue(cell, out var parent)) { cell = parent; path.Add(cell); }
                path.Reverse(); return path;
            }
            if (costs[cell] >= maxLength) continue;
            foreach (var direction in Cell.Directions)
            {
                var next = cell + direction; var cost = costs[cell] + 1;
                if (!support.Contains(next) || costs.TryGetValue(next, out var old) && old <= cost) continue;
                costs[next] = cost; previous[next] = cell; queue.Enqueue(next, (cost + Heuristic(next), next.Y, next.X));
            }
        }
        return null;
    }

    private static bool ExitsValid(List<string>? exits, int count) => exits != null && exits.Count == count && exits.Distinct(StringComparer.Ordinal).Count() == count && exits.All(exit => exit is "South" or "East" or "North" or "West");
    private static bool TagValid(string? tag) => tag != null && tag.Length is > 0 and <= 64 && tag.All(character => char.IsAsciiLetterOrDigit(character) || character == ' ');
    private static Cell Vector(string direction) => direction switch { "South" => new(0, -1), "East" => new(1, 0), "North" => new(0, 1), "West" => new(-1, 0), _ => new() };
    private static Cell Rotate(Cell direction, int rotation)
    {
        for (var quarter = 0; quarter < rotation / 90; quarter++) direction = new(-direction.Y, direction.X);
        return direction;
    }
    private static int? FitRotation(List<string> local, HashSet<Cell> desired)
    {
        for (var rotation = 0; rotation < 360; rotation += 90)
            if (local.Select(exit => Rotate(Vector(exit), rotation)).ToHashSet().SetEquals(desired)) return rotation;
        return null;
    }
}
