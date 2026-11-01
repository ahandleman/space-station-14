namespace Content.StationArchitect;

/// <summary>Allocates exclusive room goals under the laminar owner/room-complex area requirements.</summary>
public static class RoomAreaAllocation
{
    private sealed class Variable
    {
        public required DesignRoom Room;
        public long Min, Max, Value, Target;
        public double Weight;
        public List<Constraint> Constraints = [];
    }

    private sealed class Constraint
    {
        public required string Path;
        public required AreaRequirement Area;
        public required List<Variable> Variables;
        public double Weight;
        public long Value;
    }

    private sealed class Node
    {
        public required string Id;
        public Variable? Own;
        public AreaRequirement? Budget;
        public double BudgetWeight;
        public List<Node> Children = [];
        public long Min, Max, Preferred;
        public double Coefficient;
        public List<Variable> Variables = [];
        public bool Feasible = true;
    }

    public static List<ProgramDiagnostic> Allocate(StationDesignProject project, bool preserveGeometricAllocation = false)
    {
        var compiled = RequirementCompiler.Compile(project.Definition);
        var diagnostics = new List<ProgramDiagnostic>();
        if (!compiled.Success) return [.. compiled.Diagnostics];
        var definitions = RequirementCompiler.Rooms(project.Definition).ToDictionary(pair => pair.Path, pair => pair.Room, StringComparer.Ordinal);
        var rooms = project.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned && definitions.ContainsKey(room.DefinitionPath)).OrderBy(room => room.Id, StringComparer.Ordinal).ToList();
        if (rooms.Count > 4096) return [new("allocation", "allocation-limit", "Area allocation supports at most 4096 room instances.")];
        var mandatory = preserveGeometricAllocation ? RoomGrowth.CollectMandatoryFloors(project, rooms, compiled) : null;
        var nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (var room in rooms)
        {
            var definition = definitions[room.DefinitionPath];
            long min = RequirementCompiler.EffectiveMinimumArea(definition), max = definition.Area.Max;
            var target = room.TargetAreaOverride ?? definition.Area.Target;
            if (room.TargetAreaOverride is { } selected && (selected < min || selected > max)) diagnostics.Add(new(room.Id, "allocation-target", "The explicit growth target lies outside the room's hard floor bounds."));
            var fixedFloor = room.FloorLocked || room.FloorEdited;
            var fixedReservation = preserveGeometricAllocation && (room.GrowthSide == "complex" || definition.Role == "filler" && room.GrowthSide == "spine");
            if (fixedFloor || fixedReservation)
            {
                var retained = fixedFloor ? room.Cells.Distinct().Count() : room.GrowthReservation.Distinct().Count();
                if (retained < min || retained > max) diagnostics.Add(new(room.Id, "allocation-preserved-area", "Preserved floor or mandatory circulation allocation does not meet the room's hard area bounds; it was retained."));
                min = max = retained;
            }
            else if (preserveGeometricAllocation)
            {
                max = Math.Min(max, room.GrowthReservation.Count > 0 ? room.GrowthReservation.Distinct().Count() : room.Parcel.Area);
                var allowed = (room.GrowthReservation.Count > 0 ? room.GrowthReservation : room.Parcel.Cells()).ToHashSet();
                if (allowed.Count > 0 && mandatory != null)
                {
                    var seed = allowed.Contains(room.Seed) ? room.Seed : allowed.OrderBy(cell => cell.Distance(room.Seed)).ThenBy(cell => cell.Y).ThenBy(cell => cell.X).First();
                    var retained = new HashSet<Cell> { seed };
                    foreach (var anchor in mandatory[room.Id])
                    {
                        var path = RoomGrowth.AllocationPath(seed, anchor, allowed);
                        if (path == null) diagnostics.Add(new(room.Id, "allocation-anchor", "The declared frontage, dock or circulation standing floor has no connected path inside this planning allocation."));
                        else retained.UnionWith(path);
                    }
                    min = Math.Max(min, retained.Count);
                    if (definition.Rooms.Count == 0)
                    {
                        var shape = definition.ComplexShape ?? definition.Shape; var bounds = LayoutGeometry.Bounds(retained);
                        var width = Math.Max(bounds.Width, (int)Math.Ceiling(bounds.Height / shape.MaxAspectRatio));
                        var height = Math.Max(bounds.Height, (int)Math.Ceiling(bounds.Width / shape.MaxAspectRatio));
                        min = Math.Max(min, (long)Math.Ceiling((long)width * height * shape.MinFillRatio));
                    }
                }
                if (max < min)
                { diagnostics.Add(new(room.Id, "allocation-capacity", "The available planning allocation cannot hold the required own floor, connected frontage/entrance anchors and shape minimum.")); min = max = Math.Max(0, max); }
            }
            var variable = new Variable { Room = room, Min = min, Max = max, Target = target, Value = Math.Clamp((long) target, min, max), Weight = definition.Area.Weight * project.Definition.Settings.RoomPressureWeight };
            var node = new Node { Id = room.Id, Own = variable, Budget = definition.ComplexArea, BudgetWeight = (definition.ComplexArea?.Weight ?? 0) * project.Definition.Settings.RoomPressureWeight };
            if (!nodes.TryAdd(room.Id, node)) diagnostics.Add(new(room.Id, "duplicate-instance", "Area allocation requires unique room instance identities."));
        }
        var roots = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (var owner in compiled.Tree!.Children)
            roots[owner.Path] = new Node { Id = owner.Path, Budget = owner.Area, BudgetWeight = owner.Area!.Weight * project.Definition.Settings.DepartmentPressureWeight };
        foreach (var node in nodes.Values)
        {
            var room = node.Own!.Room;
            if (room.ParentInstanceId.Length > 0 && nodes.TryGetValue(room.ParentInstanceId, out var parent) && RoomHierarchy.ParentTemplatePath(room.DefinitionPath) == parent.Own!.Room.DefinitionPath)
                parent.Children.Add(node);
            else if (roots.TryGetValue(room.OwnerId, out var owner))
            {
                owner.Children.Add(node);
                if (room.ParentInstanceId.Length > 0 || RoomHierarchy.ParentTemplatePath(room.DefinitionPath) != null) diagnostics.Add(new(room.Id, "allocation-parent", "A nested allocation needs its own active immediate parent instance."));
            }
            else diagnostics.Add(new(room.Id, "allocation-owner", "A room allocation needs a declared owner."));
        }
        var constraints = new List<Constraint>(); var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots.Values)
        {
            Prepare(root, 0);
            if (root.Feasible) Assign(root, Math.Clamp(root.Preferred, root.Min, root.Max));
        }
        foreach (var node in nodes.Values.Where(node => !visited.Contains(node.Id))) diagnostics.Add(new(node.Id, "allocation-parent", "The runtime parent links do not form a bounded room tree."));
        foreach (var constraint in constraints)
        {
            constraint.Value = constraint.Variables.Sum(variable => variable.Value);
            foreach (var variable in constraint.Variables) variable.Constraints.Add(constraint);
        }
        // Hard feasibility is established above without tile-by-tile search. This bounded
        // pass refines the exact local + every ancestor pressure cost without weakening bounds.
        var variables = nodes.Values.Select(node => node.Own!).ToList();
        var work = Math.Clamp(project.Settings.GrowthIterations, 0, 2000000);
        while (work > 0 && diagnostics.All(item => item.Severity != "error"))
        {
            Variable? best = null; var bestChange = 0; var bestDelta = -1e-12;
            foreach (var variable in variables)
            foreach (var change in new[] { -1, 1 })
            {
                if (work-- <= 0) break;
                var next = variable.Value + change;
                if (next < variable.Min || next > variable.Max || variable.Constraints.Any(constraint => constraint.Value + change < constraint.Area.Min || constraint.Value + change > constraint.Area.Max)) continue;
                var delta = Cost(next, variable.Target, variable.Weight) - Cost(variable.Value, variable.Target, variable.Weight);
                foreach (var constraint in variable.Constraints) delta += Cost(constraint.Value + change, constraint.Area.Target, constraint.Weight) - Cost(constraint.Value, constraint.Area.Target, constraint.Weight);
                if (delta < bestDelta) { best = variable; bestChange = change; bestDelta = delta; }
            }
            if (best == null) break;
            best.Value += bestChange;
            foreach (var constraint in best.Constraints) constraint.Value += bestChange;
        }
        foreach (var variable in variables) variable.Room.PlannedArea = checked((int) variable.Value);
        return diagnostics;

        void Prepare(Node node, int depth)
        {
            if (depth > RoomHierarchy.MaxDepth + 1 || !visited.Add(node.Id))
            { node.Feasible = false; diagnostics.Add(new(node.Id, "allocation-parent", "Area allocation requires an acyclic bounded room tree.")); return; }
            foreach (var child in node.Children) Prepare(child, depth + 1);
            node.Variables = (node.Own is { } owned ? new[] { owned } : Enumerable.Empty<Variable>()).Concat(node.Children.SelectMany(child => child.Variables)).ToList();
            var min = (node.Own?.Min ?? 0) + node.Children.Sum(child => child.Min);
            var max = (node.Own?.Max ?? 0) + node.Children.Sum(child => child.Max);
            var preferred = (node.Own?.Value ?? 0) + node.Children.Sum(child => child.Preferred);
            node.Feasible &= node.Children.All(child => child.Feasible);
            if (node.Budget is { } budget)
            {
                constraints.Add(new() { Path = node.Id, Area = budget, Variables = node.Variables, Weight = node.BudgetWeight });
                var possibleMin = min; var possibleMax = max;
                min = Math.Max(min, budget.Min); max = Math.Min(max, budget.Max);
                if (min > max)
                { node.Feasible = false; diagnostics.Add(new(node.Id, "allocation-complex-capacity", $"Exclusive floor capacity {possibleMin} to {possibleMax} cannot meet this owner's or room complex's required {budget.Min} to {budget.Max} usable tiles.")); }
                if (node.BudgetWeight > 0) preferred = budget.Target;
            }
            node.Min = min; node.Max = max;
            node.Preferred = min <= max ? Math.Clamp(preferred, min, max) : preferred;
            var coefficients = node.Children.Where(child => child.Min < child.Max).Select(child => child.Coefficient).ToList();
            if (node.Own is { } own && own.Min < own.Max) coefficients.Add(own.Weight / Math.Pow(Math.Max(1, own.Target), 2));
            var flexible = coefficients.Count == 0 || coefficients.Any(coefficient => coefficient <= 0) ? 0 : 1 / coefficients.Sum(coefficient => 1 / coefficient);
            node.Coefficient = flexible + (node.Budget == null ? 0 : node.BudgetWeight / Math.Pow(Math.Max(1, node.Budget.Target), 2));
        }

        void Assign(Node node, long total)
        {
            var components = new List<(long Min, long Max, long Target, double Coefficient, Node? Child, Variable? Own)>();
            if (node.Own is { } own) components.Add((own.Min, own.Max, own.Value, own.Weight / Math.Pow(Math.Max(1, own.Target), 2), null, own));
            components.AddRange(node.Children.Select(child => (child.Min, child.Max, child.Preferred, child.Coefficient, (Node?) child, (Variable?) null)));
            if (components.Count == 0) return;
            var low = components.Min(component => (component.Min - component.Target) * Math.Max(1e-12, component.Coefficient));
            var high = components.Max(component => (component.Max - component.Target) * Math.Max(1e-12, component.Coefficient));
            for (var iteration = 0; iteration < 64; iteration++)
            {
                var middle = (low + high) / 2;
                var sum = components.Sum(component => Math.Clamp(component.Target + middle / Math.Max(1e-12, component.Coefficient), component.Min, component.Max));
                if (sum < total) low = middle; else high = middle;
            }
            var values = components.Select(component => (long) Math.Floor(Math.Clamp(component.Target + (low + high) / 2 / Math.Max(1e-12, component.Coefficient), component.Min, component.Max))).ToArray();
            var remaining = total - values.Sum();
            foreach (var index in Enumerable.Range(0, components.Count).Where(index => values[index] < components[index].Max)
                         .OrderBy(index => components[index].Coefficient * (2 * (values[index] - components[index].Target) + 1)))
            {
                if (remaining <= 0) break;
                var amount = Math.Min(remaining, Math.Max(1, total - values.Sum() - components.Count));
                amount = Math.Min(amount, components[index].Max - values[index]); values[index] += amount; remaining -= amount;
            }
            for (var index = 0; index < components.Count; index++)
                if (components[index].Own is { } variable) variable.Value = values[index];
                else Assign(components[index].Child!, values[index]);
        }
    }

    private static double Cost(long value, long target, double weight) => weight * Math.Pow((double) (value - target) / Math.Max(1, target), 2);
}
