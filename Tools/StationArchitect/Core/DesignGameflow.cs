namespace Content.StationArchitect;

/// <summary>Applies only improving, independently revalidated physical bypasses; definitions and user protection flags are unchanged.</summary>
public static class DesignGameflow
{
    public static StationDesignProject Run(StationDesignProject source, bool allowRepairs = true)
    {
        var project = ProgramJson.Clone(source); var originalDiagnostics = source.Report?.Diagnostics.Where(issue => !issue.Code.StartsWith("gameflow-", StringComparison.Ordinal)).ToList() ?? [];
        var evaluation = GameflowEvaluator.Evaluate(project); var history = new List<GameflowRepair>();
        var settings = project.Definition.Gameflow?.Settings ?? new GameflowSettings();
        if (allowRepairs && settings.AllowRepairs && evaluation.Rules.Count > 0)
        {
            project.Stage = "gameflow";
            var baselineErrors = PhysicalErrors(project);
            for (var repair = 0; repair < settings.RepairBudget && evaluation.TotalCost > .000001; repair++)
            {
                StationDesignProject? best = null; GameflowReport? bestReport = null; GameflowRepair? bestRepair = null;
                var bestCost = evaluation.TotalCost;
                foreach (var candidate in Candidates(project, evaluation).Take(settings.CandidateBudget))
                {
                    var trial = ProgramJson.Clone(project); var floor = candidate.Network == "publicHall" ? trial.Layout.PublicCorridors : trial.Layout.Maintenance;
                    var before = floor.ToHashSet(); var width = candidate.Network == "publicHall" ? trial.Settings.PublicWidth : trial.Settings.MaintenanceWidth;
                    var added = candidate.Centerline.SelectMany(cell => DesignRouting.Stamp(cell, width)).Except(before).ToHashSet();
                    if (added.Count == 0) continue;
                    var proposed = before.Concat(added).ToHashSet();
                    if (candidate.Network == "maintenance" && DesignRouting.HasOpenSquare(proposed, trial.Settings.MaintenanceMaxWidth + 1)) continue;
                    if (candidate.Network == "publicHall") trial.Layout.PublicCorridors = LayoutGeometry.Sorted(proposed);
                    else trial.Layout.Maintenance = LayoutGeometry.Sorted(proposed);
                    DesignRouting.RebuildBoundaries(trial);
                    var next = GameflowEvaluator.Evaluate(trial);
                    if (next.TotalCost >= bestCost - .000001 || next.Rules.Any(rule => !rule.Observed && evaluation.Rules.Any(old => old.Id == rule.Id && old.Observed))) continue;
                    if (!PhysicalErrors(trial).IsSubsetOf(baselineErrors)) continue;
                    best = trial; bestReport = next; bestCost = next.TotalCost;
                    bestRepair = new()
                    {
                        Network = candidate.Network, Centerline = [.. candidate.Centerline], CellsAdded = LayoutGeometry.Sorted(added), CostBefore = evaluation.TotalCost, CostAfter = next.TotalCost,
                        RuleIds = next.Rules.Where(rule => evaluation.Rules.Any(old => old.Id == rule.Id && old.Cost > rule.Cost + .000001)).Select(rule => rule.Id).ToList()
                    };
                }
                if (best == null || bestReport == null || bestRepair == null) break;
                project = best; evaluation = bestReport; history.Add(bestRepair);
            }
        }
        evaluation.Repairs = history; project.Gameflow = evaluation; project.Stage = "gameflow";
        project.Report = new()
        {
            Stage = "gameflow", Complete = false,
            Diagnostics = originalDiagnostics.Concat(evaluation.Diagnostics).Distinct().ToList(),
            Metrics = new() { ["gameflow.cost"] = evaluation.TotalCost, ["gameflow.rules"] = evaluation.Rules.Count, ["gameflow.verified"] = evaluation.Rules.Count(rule => rule.Observed), ["gameflow.repairs"] = history.Count }
        };
        return project;
    }

    private sealed record Candidate(string Network, List<Cell> Centerline);

    private static IEnumerable<Candidate> Candidates(StationDesignProject project, GameflowReport report)
    {
        var settings = project.Definition.Gameflow.Settings; var envelope = project.Layout.Envelope.ToHashSet();
        foreach (var network in settings.RepairNetworks)
        {
            if (network == "publicHall" ? project.Layout.PublicEdited : project.Layout.MaintenanceEdited) continue;
            var floor = (network == "publicHall" ? project.Layout.PublicCorridors : project.Layout.Maintenance).ToHashSet();
            if (floor.Count == 0) continue;
            var width = network == "publicHall" ? project.Settings.PublicWidth : project.Settings.MaintenanceWidth;
            var equipment = project.Layout.Equipment.SelectMany(entity => entity.Footprint.Concat(entity.WorkArea)).Concat(DesignTransport.EndpointFloorCells(project));
            var legal = DesignNetworkGeometry.LegalCenters(project, network, additionalBlocked: equipment);
            legal.RemoveWhere(center => !DesignRouting.Stamp(center, width).All(envelope.Contains));
            var anchors = legal.Where(center => DesignRouting.Stamp(center, width).All(floor.Contains)).ToList();
            if (anchors.Count < 2) continue;
            Cell Nearest(Cell point) => anchors.OrderBy(cell => cell.Distance(point)).ThenBy(cell => cell.Y).ThenBy(cell => cell.X).First();
            var pairs = report.Rules.Where(rule => rule.Cost > 0).SelectMany(rule => rule.RepairHints).Where(hint => hint.Network == network)
                .Select(hint => (From: Nearest(hint.From), To: Nearest(hint.To))).Where(pair => pair.From != pair.To).Distinct().ToList();
            var samples = anchors.OrderBy(cell => cell.Y).ThenBy(cell => cell.X).Where((_, index) => index % Math.Max(1, anchors.Count / 16) == 0).Take(16).ToList();
            pairs.AddRange(from a in samples from b in samples where a.Y < b.Y || a.Y == b.Y && a.X < b.X select (a, b));
            foreach (var pair in pairs.Distinct().Where(pair => pair.From.Distance(pair.To) <= settings.MaxRepairLength).Take(settings.CandidateBudget * 2))
            {
                bool Passable(Cell cell) => legal.Contains(cell) && cell.Distance(pair.From) + cell.Distance(pair.To) <= settings.MaxRepairLength;
                int Cost(Cell cell) => DesignRouting.Stamp(cell, width).All(floor.Contains) ? 20 : 1;
                var path = DesignRouting.Search(pair.From, cell => cell == pair.To, Passable, new(0, 0, project.Settings.Width, project.Settings.Height), [pair.To], settings.MaxSearchVisits, Cost);
                if (path is { Count: > 1 } && path.Count - 1 <= settings.MaxRepairLength && path.SelectMany(cell => DesignRouting.Stamp(cell, width)).Any(cell => !floor.Contains(cell))) yield return new(network, path);
            }
        }
    }

    private static HashSet<ProgramDiagnostic> PhysicalErrors(StationDesignProject project) => DesignGeometryValidator.Validate(project, includeGameflow: false).Diagnostics
        .Where(issue => issue.Severity == "error" && !issue.Code.StartsWith("gameflow-", StringComparison.Ordinal)).ToHashSet();
}
