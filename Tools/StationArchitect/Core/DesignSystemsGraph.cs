using System.Text.Json;

namespace Content.StationArchitect;

public static partial class DesignSystems
{
    private sealed record NativeInstance(string Id, NativeSystemProfile Profile, Cell Position, int Rotation);
    private sealed record NativeSupply(string PortId, string Kind, double Energy, double Watts, double Volume, double Pressure, double Temperature);

    private sealed class NativeGraph
    {
        public Dictionary<string, SystemPortGeometry> Ports = new(StringComparer.Ordinal);
        public Dictionary<string, HashSet<string>> Edges = new(StringComparer.Ordinal);
        public Dictionary<string, HashSet<string>> Physical = new(StringComparer.Ordinal);
        public List<NativeSupply> Supplies = [];
        public HashSet<string> Power = new(StringComparer.Ordinal);
        public HashSet<string> Gas = new(StringComparer.Ordinal);
        public HashSet<string> Sink = new(StringComparer.Ordinal);
        public Dictionary<string, NativeInstance> Instances = new(StringComparer.Ordinal);
        private HashSet<string> _poweredInstances = new(StringComparer.Ordinal);

        public NativeGraph(List<NativeInstance> instances, List<ProgramDiagnostic> issues)
        {
            foreach (var instance in instances)
            {
                if (!Instances.TryAdd(instance.Id, instance)) continue;
                foreach (var port in DesignSystems.Ports(instance.Profile, instance.Position, instance.Rotation, instance.Id)) { Ports[port.Id] = port; Edges[port.Id] = []; Physical[port.Id] = []; }
                foreach (var source in instance.Profile.Sources)
                {
                    var supply = Supply(instance, source);
                    if (supply == null) issues.Add(new(instance.Id, "native-source-data", "A declared source/sink is not supported by its actual explicit Battery/PowerSupplier/GasCanister component data and native port kind."));
                    else Supplies.Add(supply);
                }
            }
            var index = Ports.Values.GroupBy(port => port.Position).ToDictionary(group => group.Key, group => group.ToList());
            var terminals = Ports.Values.Where(port => port.ProfilePort.Kind == "power" && port.ProfilePort.Connection == "terminal").ToList();
            foreach (var port in Ports.Values)
            {
                IEnumerable<SystemPortGeometry> possible;
                if (port.ProfilePort.Connection == "rangeProvider")
                {
                    var radius = (int)Math.Ceiling(port.ProfilePort.Range);
                    possible = new GridBounds(port.Position.X - radius, port.Position.Y - radius, radius * 2 + 1, radius * 2 + 1).Cells().SelectMany(cell => index.GetValueOrDefault(cell) ?? []).Where(other => other.ProfilePort.Connection == "rangeReceiver");
                }
                else if (port.ProfilePort.Connection == "rangeReceiver") continue;
                else possible = Cell.Directions.Append(new()).SelectMany(direction => index.GetValueOrDefault(port.Position + direction) ?? []);
                foreach (var other in possible)
                {
                    if (!Contacts(port, other)) continue;
                    if (port.ProfilePort.Kind == "power" && port.ProfilePort.Connection == "cable" && other.ProfilePort.Connection == "cable" && port.Position != other.Position)
                    {
                        var delta = other.Position - port.Position;
                        if (terminals.Any(terminal => terminal.Position == port.Position && terminal.Directions.Contains(delta) || terminal.Position == other.Position && terminal.Directions.Contains(new(-delta.X, -delta.Y)))) continue;
                    }
                    Physical[port.Id].Add(other.Id); Physical[other.Id].Add(port.Id);
                    Edges[port.Id].Add(other.Id);
                    if (port.ProfilePort.Connection != "rangeProvider") Edges[other.Id].Add(port.Id);
                }
            }
            foreach (var instance in Instances.Values) foreach (var transfer in instance.Profile.Transfers.Where(transfer => !transfer.RequiresPower)) Transfer(instance, transfer);
            var changed = true;
            while (changed)
            {
                var previous = Power.Count; Power = Reach(Supplies.Where(source => source.Kind == "power").Select(source => source.PortId), Edges);
                _poweredInstances = Power.Select(id => Ports[id].InstanceId).ToHashSet(StringComparer.Ordinal);
                foreach (var instance in Instances.Values.Where(instance => _poweredInstances.Contains(instance.Id))) foreach (var transfer in instance.Profile.Transfers.Where(transfer => transfer.RequiresPower)) Transfer(instance, transfer);
                changed = Power.Count > previous;
            }
            Gas = Reach(Supplies.Where(source => source.Kind == "gas").Select(source => source.PortId), Edges);
            Sink = Reach(Supplies.Where(source => source.Kind == "gasSink").Select(source => source.PortId), Reverse(Edges));
        }

        private void Transfer(NativeInstance instance, NativeSystemTransfer transfer)
        {
            var from = instance.Id + ":" + transfer.FromPort; var to = instance.Id + ":" + transfer.ToPort;
            if (!Ports.ContainsKey(from) || !Ports.ContainsKey(to) || Ports[from].ProfilePort.Kind != Ports[to].ProfilePort.Kind) return;
            Edges[from].Add(to); if (transfer.Bidirectional) Edges[to].Add(from);
        }
        public bool Powered(string id) => Instances.TryGetValue(id, out var instance) && (!instance.Profile.RequiresPower || _poweredInstances.Contains(id));
        public List<string> Witness(string target, string kind, bool sink = false)
        {
            var edges = sink ? Reverse(Edges) : Edges; var roots = Supplies.Where(source => source.Kind == (sink ? "gasSink" : kind)).Select(source => source.PortId).ToList();
            var visited = roots.ToHashSet(StringComparer.Ordinal); var queue = new Queue<string>(roots); var previous = new Dictionary<string, string>(StringComparer.Ordinal);
            while (queue.TryDequeue(out var current))
            {
                if (current == target) { var path = new List<string> { target }; while (previous.TryGetValue(path[^1], out var parent)) path.Add(parent); path.Reverse(); return path; }
                foreach (var next in edges.GetValueOrDefault(current) ?? []) if (visited.Add(next)) { previous[next] = current; queue.Enqueue(next); }
            }
            return [];
        }
        public double Pressure(string port)
        {
            var component = Reach([port], Physical); var sources = Supplies.Where(source => source.Kind == "gas" && component.Contains(source.PortId)).ToList();
            if (sources.Count == 0) return 0;
            var volume = sources.Sum(source => source.Volume) + component.Sum(id => Ports[id].ProfilePort.Kind == "gas" ? Ports[id].ProfilePort.VolumeLitres : 0);
            return volume <= 0 ? 0 : sources.Min(source => source.Temperature) * sources.Sum(source => source.Pressure * source.Volume / source.Temperature) / volume;
        }
    }

    private static HashSet<string> Reach(IEnumerable<string> roots, Dictionary<string, HashSet<string>> edges)
    {
        var reached = roots.Where(edges.ContainsKey).ToHashSet(StringComparer.Ordinal); var queue = new Queue<string>(reached);
        while (queue.TryDequeue(out var current)) foreach (var next in edges[current]) if (reached.Add(next)) queue.Enqueue(next);
        return reached;
    }
    private static Dictionary<string, HashSet<string>> Reverse(Dictionary<string, HashSet<string>> edges)
    {
        var reverse = edges.Keys.ToDictionary(id => id, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var pair in edges) foreach (var next in pair.Value) reverse[next].Add(pair.Key);
        return reverse;
    }

    private static NativeSupply? Supply(NativeInstance instance, NativeSystemSource source)
    {
        var port = instance.Profile.Ports.FirstOrDefault(port => port.Id == source.PortId);
        if (port == null || (source.Kind == "power" ? port.Kind != "power" : port.Kind != "gas")) return null;
        var components = instance.Profile.Components;
        if (source.Kind == "power")
        {
            var battery = components.TryGetValue("Battery", out var storage) ? Math.Min(Number(storage, "startingCharge"), Number(storage, "maxCharge")) : 0;
            var watts = components.TryGetValue("PowerSupplier", out var supplier) ? Number(supplier, "supplyRate") : 0;
            if (source.StoredEnergyJoules > battery + .0001 || source.SupplyWatts > watts + .0001 || source.StoredEnergyJoules <= 0 && source.SupplyWatts <= 0) return null;
            return new(instance.Id + ":" + source.PortId, source.Kind, source.StoredEnergyJoules, source.SupplyWatts, 0, 0, 0);
        }
        if (!components.TryGetValue("GasCanister", out var canister) || !canister.TryGetProperty("air", out var air) || air.ValueKind != JsonValueKind.Object) return null;
        var volume = Number(air, "volume"); var temperature = Number(air, "temperature");
        if (volume <= 0 || temperature <= 0 || Math.Abs(volume - source.VolumeLitres) > Math.Max(.001, volume * .0001) || Math.Abs(temperature - source.TemperatureKelvin) > .01 || !air.TryGetProperty("moles", out var moles) || moles.ValueKind != JsonValueKind.Object) return null;
        var gases = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var gas in moles.EnumerateObject()) { if (gas.Value.ValueKind != JsonValueKind.Number || !gas.Value.TryGetDouble(out var amount) || !Finite(amount, 0, 1e12)) return null; gases[gas.Name] = amount; }
        var total = gases.Values.Sum(); var pressure = total * 8.31446261815324 * temperature / volume;
        if (source.Kind == "gasSink") return total <= .000001 && source.PressureKpa == 0 ? new(instance.Id + ":" + source.PortId, source.Kind, 0, 0, volume, 0, temperature) : null;
        if (total <= 0 || source.PressureKpa <= 0 || Math.Abs(pressure - source.PressureKpa) > Math.Max(.1, pressure * .01) || gases.Any(pair => !source.GasFractions.TryGetValue(pair.Key, out var fraction) || Math.Abs(fraction - pair.Value / total) > .001) || source.GasFractions.Any(pair => !gases.ContainsKey(pair.Key))) return null;
        return new(instance.Id + ":" + source.PortId, source.Kind, 0, 0, volume, pressure, temperature);
    }
    private static double Number(JsonElement mapping, string key) => mapping.ValueKind == JsonValueKind.Object && mapping.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : 0;

    public static bool ContextualContact(SystemPortGeometry first, SystemPortGeometry second, IEnumerable<SystemPortGeometry> allPorts)
    {
        if (!Contacts(first, second)) return false;
        if (first.ProfilePort.Kind != "power" || first.ProfilePort.Connection != "cable" || second.ProfilePort.Connection != "cable" || first.Position == second.Position) return true;
        var delta = second.Position - first.Position;
        return !allPorts.Any(port => port.ProfilePort.Kind == "power" && port.ProfilePort.Connection == "terminal" &&
            (port.Position == first.Position && port.Directions.Contains(delta) || port.Position == second.Position && port.Directions.Contains(new(-delta.X, -delta.Y))));
    }

    public static bool HardwarePortConflict(NativeSystemPort hardware, NativeSystemPort segment)
    {
        if (hardware.Kind != segment.Kind || hardware.NodeGroup != segment.NodeGroup) return false;
        if (hardware.Kind == "power") return hardware.Connection == "terminal" && segment.Connection == "terminal";
        if (hardware.Kind == "disposal") return hardware.Connection != "disposalUnit" || segment.Connection != "disposalEntry";
        if (hardware.Kind == "gas")
        {
            if (hardware.Connection == "portable" && segment.Connection == "pipePort" || hardware.Connection == "pipePort" && segment.Connection == "portable") return false;
            return hardware.PipeLayer == segment.PipeLayer && hardware.Connection != "portable";
        }
        return false;
    }
}
