using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Content.StationArchitect;

/// <summary>Bounded observations of real walking geometry under author-declared access assumptions.</summary>
public static class GameflowEvaluator
{
    public static GameflowReport Evaluate(StationDesignProject project)
    {
        var report = new GameflowReport { GeometryHash = GeometryHash(project), DefinitionHash = Hash(project.Definition) };
        var validation = ProgramValidator.Validate(project.Definition);
        if (validation.Any(issue => issue.Severity == "error")) { report.Diagnostics = validation; return report; }
        var program = project.Definition.Gameflow; var settings = program.Settings;
        var missingPenalty = Math.Max(1, program.Rules.Where(rule => rule.Enabled).Sum(rule => Math.Max(1, rule.Weight))) * 1e15;
        var budget = new GameflowWorkBudget(settings); var graphs = new Dictionary<string, GameflowGraph>(StringComparer.Ordinal);
        foreach (var rule in program.Rules.Where(rule => rule.Enabled))
        {
            var result = new GameflowRuleResult { Id = rule.Id, Kind = rule.Kind, AccessProfile = rule.AccessProfile };
            report.Rules.Add(result);
            void Issue(string code, string message) => result.Diagnostics.Add(new("gameflow.rules." + rule.Id, code, message, rule.Priority == "required" ? "error" : "warning"));
            var profile = program.AccessProfiles.Single(profile => profile.Id == rule.AccessProfile);
            if (!graphs.TryGetValue(profile.Id, out var graph)) graphs[profile.Id] = graph = new(project, profile, settings, budget);
            result.Assumptions = [.. graph.Assumptions];
            if (graph.Walk.Count > settings.MaxGraphCells)
            { Issue("gameflow-budget", "Actual traversable floor exceeds MaxGraphCells; this rule has no verified metric. Increase its analysis budget or narrow its access profile."); Finish(); continue; }
            var from = graph.Select(rule.From); var to = graph.Select(rule.To);
            if (from.Terminals.Count == 0 || rule.Kind != "chaseLoops" && to.Targets.Count == 0)
            { Issue("gameflow-selection", "A selector has no actual traversable target under this access profile. Check selected paths/tags, built gateways and the profile's explicit access levels/networks."); Finish(); continue; }
            var sameSelector = rule.Kind == "chaseLoops" && Empty(rule.To);
            var terminalCoverage = from.Terminals.Count <= settings.MaxTerminals && (sameSelector || to.Terminals.Count <= settings.MaxTerminals);
            var sources = Spread(from.Terminals, settings.MaxTerminals);
            var targets = Spread(to.Terminals, settings.MaxTerminals);
            if (rule.Kind == "escapeRoutes")
            {
                result.CoverageComplete = terminalCoverage;
                var counts = new List<double>();
                foreach (var source in sources)
                {
                    var forbidden = new HashSet<Cell>(); var count = 0; var stopped = false;
                    for (; count < settings.MaxIndependentRoutes; count++)
                    {
                        var path = graph.Search(source.Cell, to.Targets, forbidden);
                        if (path == null) { stopped = true; break; }
                        AddWitness(Witness("escape", source.Id, "selector", path));
                        var runs = graph.DoorRuns.Where(run => path.Cells.Any(run.Contains)).ToList();
                        if (runs.Count == 0) { count++; stopped = true; break; } // an open corridor's grid lanes are one route
                        forbidden.UnionWith(GameflowGraph.PathHalo(path.Cells, rule.Routes.MinSeparation, source.Cell, [path.Cells[^1]], rule.Routes.EndpointRadius));
                        foreach (var run in runs) forbidden.UnionWith(run);
                    }
                    if (!stopped) result.CoverageComplete = false;
                    counts.Add(count);
                    if (budget.Exhausted) break;
                }
                result.PairsTested = counts.Count; result.PairsTotal = from.Terminals.Count;
                result.Value = counts.Count > 0 ? counts.Min() : null;
                result.Observed = counts.Count == from.Terminals.Count && !budget.Exhausted && (result.CoverageComplete || rule.Bounds.Max == null);
            }
            else
            {
                var same = sameSelector;
                if (same) targets = sources;
                var pairs = (from a in sources from b in targets where a.Cell != b.Cell && (!same || string.CompareOrdinal(a.Id, b.Id) < 0) select (A: a, B: b))
                    .OrderByDescending(pair => pair.A.Cell.Distance(pair.B.Cell)).ThenBy(pair => pair.A.Id, StringComparer.Ordinal).ThenBy(pair => pair.B.Id, StringComparer.Ordinal).ToList();
                result.PairsTotal = pairs.Count;
                result.CoverageComplete = terminalCoverage && pairs.Count <= settings.MaxPairTests;
                var values = new List<double>(); var loops = new List<HashSet<Cell>>();
                foreach (var pair in pairs.Take(settings.MaxPairTests))
                {
                    result.PairsTested++;
                    var primary = graph.Search(pair.A.Cell, [pair.B.Cell], allowTubes: rule.Kind != "chaseLoops");
                    if (primary == null)
                    {
                        if (!budget.Exhausted && rule.Kind != "chaseLoops") Issue("gameflow-unreachable", $"No physical route connects {pair.A.Id} and {pair.B.Id} under profile '{profile.Id}'. Add a usable gateway/passage or revise the explicit access assumption.");
                        if (rule.Kind != "chaseLoops") result.CoverageComplete = false;
                        continue;
                    }
                    if (rule.Kind == "chaseLoops")
                    {
                        if (primary.Cells.Count - 1 < rule.Routes.MinPathLength) continue;
                        var forbidden = GameflowGraph.PathHalo(primary.Cells, rule.Routes.MinSeparation, pair.A.Cell, [pair.B.Cell], rule.Routes.EndpointRadius);
                        var alternate = graph.Search(pair.A.Cell, [pair.B.Cell], forbidden, allowTubes: false);
                        if (alternate == null || alternate.Cells.Count - 1 < rule.Routes.MinPathLength || alternate.Cells.Count - 1 > (primary.Cells.Count - 1) * rule.Routes.MaxDetourRatio || primary.Cells.Count + alternate.Cells.Count - 2 < rule.Routes.MinLoopLength)
                        { Hints(primary); continue; }
                        var enclosed = graph.EnclosedBlocked(primary.Cells, alternate.Cells);
                        if (enclosed == null) break;
                        if (enclosed.Count < rule.Routes.MinEnclosedBlockedArea) { Hints(primary); continue; }
                        var enclosedSet = enclosed.ToHashSet();
                        if (loops.Any(existing => existing.Count(cell => enclosedSet.Contains(cell)) > Math.Min(existing.Count, enclosedSet.Count) / 2d)) continue;
                        loops.Add(enclosedSet);
                        var witness = Witness("chase-loop", pair.A.Id, pair.B.Id, primary); witness.AlternatePath = alternate.Cells; witness.AlternateLength = alternate.Cells.Count - 1; witness.AlternateTravelCost = alternate.Cost;
                        witness.Separation = rule.Routes.MinSeparation; witness.EnclosedBlockedArea = enclosed.Count; witness.EnclosedBlockedCells = enclosed;
                        AddWitness(witness);
                    }
                    else if (rule.Kind == "chokepoints")
                    {
                        var cuts = new List<Cell>(); Cell? lastCritical = null;
                        for (var index = 0; index < primary.Cells.Count; index++)
                        {
                            var center = primary.Cells[index];
                            if (center.Distance(pair.A.Cell) <= rule.Routes.EndpointRadius || center.Distance(pair.B.Cell) <= rule.Routes.EndpointRadius) continue;
                            var cut = DesignRouting.Stamp(center, rule.Routes.ChokepointWidth).Where(graph.Walk.Contains).ToHashSet();
                            if (graph.Search(pair.A.Cell, [pair.B.Cell], cut) != null) continue;
                            if (budget.Exhausted) break;
                            var separateRegion = lastCritical == null || lastCritical.Value.Distance(center) > rule.Routes.DeduplicationRadius;
                            lastCritical = center;
                            if (!separateRegion) continue;
                            cuts.Add(center); var witness = Witness("chokepoint", pair.A.Id, pair.B.Id, primary); witness.CutCells = LayoutGeometry.Sorted(cut); AddWitness(witness); Hints(primary);
                        }
                        values.Add(cuts.Count);
                    }
                    else
                    { values.Add(primary.Cost); AddWitness(Witness("travel", pair.A.Id, pair.B.Id, primary)); Hints(primary); }
                    if (budget.Exhausted) break;
                }
                if (rule.Kind == "chaseLoops")
                {
                    result.Value = loops.Count;
                    result.Observed = !budget.Exhausted && pairs.Count > 0 && (result.CoverageComplete || rule.Bounds.Max == null);
                    if (!result.CoverageComplete) result.Assumptions.Add("Loop count is a witnessed lower bound from bounded anchor-pair sampling; it is not an exhaustive cycle count.");
                }
                else
                {
                    result.Value = values.Count == 0 ? null : rule.Kind == "travelBalance" ? values.Max() - values.Min() : rule.Aggregation == "min" ? values.Min() : rule.Aggregation == "mean" ? values.Average() : values.Max();
                    result.Observed = result.CoverageComplete && !budget.Exhausted && values.Count == pairs.Count && pairs.Count > 0;
                }
            }
            if (budget.Exhausted || !result.Observed)
                Issue("gameflow-unverified", "This metric lacks complete required coverage or exhausted its bounded search. Increase analysis budgets, narrow selectors or repair disconnected physical paths; no successful observation was inferred.");
            Finish();

            GameflowWitness Witness(string kind, string a, string b, GameflowPath path) => new()
            { Kind = kind, From = a, To = b, AccessProfile = profile.Id, Assumptions = [.. graph.Assumptions], Path = [.. path.Cells], Length = path.Cells.Count - 1, TravelCost = path.Cost, CrewTubeNetworks = [.. path.Tubes] };
            void AddWitness(GameflowWitness witness)
            {
                if (result.Witnesses.Count >= settings.MaxWitnesses) return;
                witness.GeometryTruncated = witness.Path.Count > settings.MaxWitnessCells || witness.AlternatePath.Count > settings.MaxWitnessCells || witness.EnclosedBlockedCells.Count > settings.MaxWitnessCells;
                witness.Path = witness.Path.Take(settings.MaxWitnessCells).ToList(); witness.AlternatePath = witness.AlternatePath.Take(settings.MaxWitnessCells).ToList(); witness.EnclosedBlockedCells = witness.EnclosedBlockedCells.Take(settings.MaxWitnessCells).ToList();
                result.Witnesses.Add(witness);
            }
            void Hints(GameflowPath path)
            {
                foreach (var network in settings.RepairNetworks)
                {
                    var candidates = path.Cells.Where(graph.Networks[network].Contains).ToList();
                    if (candidates.Count < 2 || candidates[0].Distance(candidates[^1]) < 4) continue;
                    var hint = new GameflowRepairHint { Network = network, From = candidates[0], To = candidates[^1], Reason = "Try a separated physical bypass between these existing passage regions, preserving all room/access requirements." };
                    if (!result.RepairHints.Any(old => old.Network == network && old.From == hint.From && old.To == hint.To)) result.RepairHints.Add(hint);
                }
            }
            void Finish()
            {
                if (!result.Observed || result.Value == null)
                { result.Cost = missingPenalty; result.Satisfied = false; result.TargetMet = false; }
                else
                {
                    var value = result.Value.Value; var scale = Math.Max(1, rule.Bounds.Target ?? rule.Bounds.Min ?? rule.Bounds.Max ?? 1);
                    var below = Math.Max(0, (rule.Bounds.Min ?? value) - value); var above = Math.Max(0, value - (rule.Bounds.Max ?? value));
                    result.Satisfied = below == 0 && above == 0; result.TargetMet = rule.Bounds.Target == null || Math.Abs(value - rule.Bounds.Target.Value) < .000001;
                    result.Cost = rule.Weight * Math.Min(1e9, 100 * Math.Pow((below + above) / scale, 2) + (rule.Bounds.Target == null ? 0 : Math.Pow((value - rule.Bounds.Target.Value) / scale, 2)));
                    if (!result.Satisfied) Issue("gameflow-bound", $"Observed {rule.Kind} = {value:0.###} violates the configured bounds. Inspect witnessed routes/cuts and adjust nearby passage geometry or target placement.");
                    else if (!result.TargetMet) result.Diagnostics.Add(new("gameflow.rules." + rule.Id, "gameflow-target", $"Observed {rule.Kind} = {value:0.###}; preferred target is {rule.Bounds.Target:0.###}. Physical bypass or placement candidates are scored against this preference.", "warning"));
                }
                report.TotalCost += result.Cost; report.Diagnostics.AddRange(result.Diagnostics);
            }
        }
        report.Assumptions = report.Rules.SelectMany(rule => rule.Assumptions).Distinct(StringComparer.Ordinal).ToList();
        report.Complete = report.Rules.All(rule => rule.Observed);
        return report;
    }

    public static string GeometryHash(StationDesignProject project) => Hash(new
    {
        project.Settings.Width, project.Settings.Height, project.Layout.Envelope,
        Rooms = project.Layout.Rooms.Select(room => new { room.Id, room.Enabled, room.Orphaned, room.Cells }),
        project.Layout.PublicCorridors, project.Layout.Maintenance, project.Layout.Walls, project.Layout.Hull, project.Layout.Windows,
        project.Layout.Interfaces, project.Layout.Equipment, project.Layout.TransportNetworks
    });

    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, ProgramJson.Options)))).ToLowerInvariant();
    private static bool Empty(GameflowSelector selector) => selector.Paths.Count + selector.Tags.Count + selector.Owners.Count + selector.Roles.Count + selector.Networks.Count == 0;
    private static List<GameflowTerminal> Spread(List<GameflowTerminal> terminals, int limit)
    {
        if (terminals.Count <= limit) return terminals;
        var selected = new List<GameflowTerminal> { terminals[0] }; var remaining = terminals.Skip(1).ToList();
        while (selected.Count < limit)
        {
            var next = remaining.OrderByDescending(terminal => selected.Min(old => old.Cell.Distance(terminal.Cell))).ThenBy(terminal => terminal.Id, StringComparer.Ordinal).First();
            selected.Add(next); remaining.Remove(next);
        }
        return selected;
    }
}
