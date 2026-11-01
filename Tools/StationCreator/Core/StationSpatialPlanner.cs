using System.Security.Cryptography;
using System.Text;

namespace Content.StationGeneration;

/// <summary>
/// Constructive station territory planner. Atomic parcels preserve usable room depth;
/// public streets and a few internal service seams are reserved before region growth.
/// Fine tile ownership, boundaries, ports and walkability are derived independently.
/// </summary>
public static class StationSpatialPlanner
{
    private const string PublicOwner = "\0public";
    private const string ServiceOwner = "\0service";
    private const string StructuralOwner = "\0structural";

    public static StationProject RunStage(StationProject project, string stage)
    {
        project.Result = null;
        var errors = Check(project);
        if (errors.Count != 0)
        {
            project.Result = Failure(project, errors);
            return project;
        }
        try
        {
            switch (stage)
            {
                case "placement": Place(project); break;
                case "growth": Grow(project); break;
                case "maintenance": BuildMaintenance(project); break;
                case "boundaries": RebuildBoundaries(project); break;
                case "validation": project.Result = Validate(project); if(project.SpatialPlan != null){project.Result.Attempts=project.SpatialPlan.PlacementAttempts;if(project.Result.Success)project.SpatialPlan.Stage="validation";} break;
                default: throw new PlanningException("placement", "stage", "Unknown spatial planning stage.");
            }
            if (stage is "growth" or "maintenance" or "boundaries")
            {
                var hierarchyIssues = StationHierarchyValidator.Validate(project);
                if (hierarchyIssues.Any(d => d.Severity == "error")) project.Result = Failure(project, hierarchyIssues);
            }
            project.Layout = null;
            project.Stage = stage == "boundaries" ? project.SpatialPlan!.Stage : stage;
        }
        catch (PlanningException e) { project.Result = Failure(project, [e.Diagnostic]); }
        return project;
    }

    /// <summary>Complete edited spatial work without discarding accepted territory edits.</summary>
    public static StationProject Generate(StationProject source)
    {
        var project = ProjectJson.Clone(source);
        var errors = Check(project);
        if(project.Topology==null&&errors.All(d=>d.Stage=="topology"&&d.Code=="missing"))
        {
            project.Topology=StationGenerator.GenerateTopology(project.Config);
            errors=Check(project);
        }
        if (errors.Count != 0) { project.Result = Failure(project, errors); return project; }
        if(source.SpatialPlan==null)return GenerateFreshCandidates(project);
        try
        {
            if (project.SpatialPlan == null || project.SpatialPlan.Envelope.Count==0 || project.SpatialPlan.PublicReservations.Count==0 ||
                !project.Config.Departments.Select(d=>d.Id).ToHashSet(StringComparer.Ordinal).SetEquals(project.SpatialPlan.Seeds.Select(s=>s.Id))) Place(project);
            if (project.SpatialPlan!.Regions.Count == 0) Grow(project,false);
            else RebuildBoundaries(project,false);
            if (project.SpatialPlan.Maintenance.Count == 0 && project.SpatialPlan.Stage is "placement" or "growth") BuildMaintenance(project);
            else StationHierarchyPlanner.Build(project);
            project.Result = Validate(project);
            project.Result.Attempts=project.SpatialPlan.PlacementAttempts;
            if(project.Result.Success)project.SpatialPlan.Stage="validation";
            project.Layout = null;
            project.Stage = "validation";
        }
        catch (PlanningException e) { project.Result = Failure(project, [e.Diagnostic]); }
        return project;
    }

    private static StationProject GenerateFreshCandidates(StationProject prepared)
    {
        StationProject? bestFailure=null;var bestProgress=-1;
        var unsupported=prepared.Topology!.Edges.Where(e=>e.Hard&&e.Relation!="adjacency"&&
            (e.Relation is not ("walking" or "delivery")||!e.AllowedModes.Any(m=>m is "publicHall" or "maintenance"))).ToList();
        if(unsupported.Count>0)
        {
            prepared.Result=Failure(prepared,unsupported.Select(e=>new Diagnostic("growth","unsupported-fulfillment",
                $"{e.From} → {e.To}: the required {e.Relation} dependency needs a physical method not constructed by this planning stage. Choose a built public/maintenance route or keep the future network as Desired.")).ToList());
            prepared.Layout=null;prepared.Stage="growth";
            return prepared;
        }
        var budget=prepared.Config.CandidateBudget;
        for(var attempt=0;attempt<budget;attempt++)
        {
            var candidate=ProjectJson.Clone(prepared);
            try
            {
                PlaceCandidate(candidate,attempt);
                Grow(candidate,false);
                BuildMaintenance(candidate);
                candidate.Result=Validate(candidate);
                candidate.Result.Attempts=attempt+1;
                candidate.Stage="validation";candidate.Layout=null;
                if(candidate.Result.Success){candidate.SpatialPlan!.Stage="validation";return candidate;}
                if(candidate.Result.Diagnostics.Any(d=>d.Severity=="error"&&d.Code=="unsupported-fulfillment"))return candidate;
            }
            catch(PlanningException e)
            {
                candidate.Result=Failure(candidate,[e.Diagnostic]);candidate.Result.Attempts=attempt+1;
                candidate.Stage=candidate.SpatialPlan?.Stage??e.Diagnostic.Stage;candidate.Layout=null;
            }
            var progress=candidate.SpatialPlan?.Regions.Count>0?2:candidate.SpatialPlan?.Envelope.Count>0?1:0;
            if(progress>=bestProgress){bestFailure=candidate;bestProgress=progress;}
        }
        bestFailure!.Result!.Attempts=budget;
        return bestFailure;
    }

    public static List<Diagnostic> Check(StationProject project)
    {
        // Rectangle width/height are legacy estimates, not the region's hard geometry.
        var errors = StationGenerator.CheckConfig(project.Config).Where(d => d.Code is not ("area" or "dimensions")).ToList();
        errors.AddRange(StationHierarchy.Validate(project.Config));
        if (project.Config.Departments != null)
            foreach (var d in project.Config.Departments.Where(d => d != null))
                if (d.MinArea < 1 || d.MaxArea < d.MinArea || d.MaxArea > project.Config.Width * project.Config.Height)
                    errors.Add(new("placement","area",$"{d.Id}: usable floor area needs positive min/max limits within the planning canvas."));
        var s = project.Config.Spatial;
        if (s == null || s.CellSize is < 4 or > 8 || s.CellSize < project.Config.CorridorWidth + 2 ||
            !double.IsFinite(s.FillRatio) || s.FillRatio is < .45 or > .95 ||
            !double.IsFinite(s.MaintenanceFraction) || s.MaintenanceFraction is < .02 or > .50 ||
            s.GrowthSteps is < 100 or > 1000000)
            errors.Add(new("placement", "spatial-settings", "Use parcel size 4-8 (at least public width + 2), fill ratio 0.45-0.95, maintenance fraction 0.02-0.50 and 100-1000000 growth steps."));
        if (s != null && (!ValidCounts(s.DeepMaintenanceMin,s.DeepMaintenanceMax) || !ValidCounts(s.SolarMin,s.SolarMax) || !ValidCounts(s.SpaceAccessMin,s.SpaceAccessMax)))
            errors.Add(new("maintenance", "feature-counts", "Deep maintenance, solar and space-access counts need minimum/maximum pairs between 0 and 12, with minimum no greater than maximum."));
        if(s!=null&&(!ValidCounts(s.CommonMaintenanceMin,s.CommonMaintenanceMax)||s.CommonMaintenanceRoomMinArea<4||s.CommonMaintenanceRoomMaxArea<s.CommonMaintenanceRoomMinArea||s.CommonMaintenanceRoomMaxArea>project.Config.Width*project.Config.Height||s.CommonMaintenanceRoomTargetArea<1||s.CommonMaintenanceRoomTargetArea>project.Config.Width*project.Config.Height))
            errors.Add(new("maintenance","common-maintenance-settings","Hall-accessible maintenance rooms need count bounds 0-12, usable area bounds from 4 to the canvas area, and a positive soft target within the canvas."));
        if (s != null && (project.Config.MaintenanceWidth < 2 || s.MaintenancePassageMaxWidth < project.Config.MaintenanceWidth || s.MaintenancePassageMaxWidth > 8))
            errors.Add(new("maintenance", "maintenance-width-setting", "Maintenance passage widths need a minimum of 2 or 3 and a maximum between that minimum and 8; the default maximum is 3."));
        if (s != null && (s.DeepMaintenanceRoomMinArea < 4 || s.DeepMaintenanceRoomMaxArea < s.DeepMaintenanceRoomMinArea || s.DeepMaintenanceRoomMaxArea > project.Config.Width*project.Config.Height))
            errors.Add(new("maintenance", "deep-maintenance-area", "Deep maintenance rooms need usable floor minimum/maximum limits, with minimum at least 4 and maximum within the canvas."));
        if(s != null && (s.DeepMaintenanceRoomTargetArea < 1 || s.DeepMaintenanceRoomTargetArea > project.Config.Width*project.Config.Height))
            errors.Add(new("maintenance","deep-maintenance-target","Deep maintenance room target area must be positive and fit within the planning canvas; it is a soft target clamped to the room limits."));
        if(s != null && s.MaintenancePocketPreference is not ("departments" or "rooms"))
            errors.Add(new("maintenance","maintenance-pocket-policy","Maintenance pocket preference must be departments or rooms."));
        if(s != null && s.DepartmentBoundaryMaxDepth is < 1 or > 3)
            errors.Add(new("growth","department-boundary-depth","Department boundary allowance depth must be between 1 and 3 tiles."));
        if (errors.Count != 0) return errors;
        if (project.Topology == null)
            errors.Add(new("topology", "missing", "Author the station topology before spatial planning."));
        else
        {
            StationGenerator.NormalizeTopology(project.Topology);
            errors.AddRange(StationValidator.CheckTopology(project.Config, project.Topology));
        }
        return errors;
    }

    private static GenerationResult Failure(StationProject p, List<Diagnostic> errors) => new()
    {
        Seed = p.Config.Seed, Attempts = 1, Diagnostics = errors
    };

    private static List<DepartmentDefinition> Definitions(StationGenerationConfig c) => c.Departments
        .OrderBy(d => d.PlacementOrder)
        .ThenBy(d => d.Id, StringComparer.Ordinal).ToList();

    private static bool ValidCounts(int min,int max) => min >= 0 && max >= min && max <= 12;
    private static int DefaultTarget(DepartmentDefinition d) => (int) Math.Clamp(Math.Max((long) d.Width * d.Height,StationHierarchy.RequiredFloorArea(d)),d.MinArea,d.MaxArea);

    private static Cell Parcel(Cell p, int size) => new((int) Math.Floor(p.X / (double) size), (int) Math.Floor(p.Y / (double) size));
    private static Cell Center(Cell p, int size) => new(p.X * size + size / 2, p.Y * size + size / 2);
    private static IEnumerable<Cell> Expand(Cell p, int size) => new Rect(p.X * size, p.Y * size, size, size).Cells();
    private static HashSet<Cell> Expand(IEnumerable<Cell> parcels, int size) => parcels.SelectMany(p => Expand(p, size)).ToHashSet();
    private static HashSet<Cell> Parcels(IEnumerable<Cell> tiles, int size) => tiles.Select(p => Parcel(p, size)).ToHashSet();
    private static IEnumerable<Cell> Neighbors(Cell p) => Cell.Directions.Select(d => p + d);
    private static List<Cell> Sorted(IEnumerable<Cell> cells) => StationGenerator.Sorted(cells);
    private static StationSpatialPlan NeedPlan(StationProject p) => p.SpatialPlan ?? throw new PlanningException("placement", "missing", "Place department seeds before growing the station.");

    public static void Place(StationProject project)
    {
        PlanningException? last=null;
        for(var attempt=0;attempt<project.Config.CandidateBudget;attempt++)
        {
            try{PlaceCandidate(project,attempt);return;}
            catch(PlanningException e){last=e;}
        }
        throw last??new PlanningException("placement","candidate-budget","No spatial placement candidate could be constructed.");
    }

    private static void PlaceCandidate(StationProject project,int attempt)
    {
        var c = project.Config;
        var previous = project.SpatialPlan;
        var size = c.Spatial.CellSize;
        var plan = new StationSpatialPlan { CellSize = size, EnvelopeEdited = previous?.EnvelopeEdited == true,PlacementAttempts=attempt+1 };
        var envelope = previous?.EnvelopeEdited == true ? Parcels(previous.Envelope, size) : MakeEnvelope(c, size,previous?.Seeds);
        if (previous?.EnvelopeEdited == true && !Expand(envelope, size).SetEquals(previous.Envelope))
            throw new PlanningException("placement", "envelope-parcels", "The envelope must contain whole growth parcels; paint it at the configured parcel size.");
        if (envelope.Count < c.Departments.Count * 3 || Expand(envelope, size).Any(p => !StationGenerator.Inside(c, p, 2)))
            throw new PlanningException("placement", "envelope-budget", "The envelope is too small or outside the planning canvas. Increase canvas/envelope size or reduce department programs.");
        if (StationValidator.Components(envelope).Count != 1)
            throw new PlanningException("placement", "envelope-connectivity", "The station envelope must be connected before allocating departments.");
        var publicParcels = MakeCirculation(c, envelope);
        // Choose protected room-fit kernels first, then negotiate service seams around
        // those anchors. Both remain explicit reservations before territory growth.
        var serviceParcels = new HashSet<Cell>();
        var free = new HashSet<Cell>(envelope); free.ExceptWith(publicParcels); free.ExceptWith(serviceParcels);
        var components = StationValidator.Components(free);
        if (components.Count > c.Departments.Count || components.Any(comp => !comp.Any(p => Neighbors(p).Any(publicParcels.Contains))))
            throw new PlanningException("placement", "circulation-partition", "Circulation reservations leave a disconnected department sector without public frontage.");
        var oldSeeds = (previous?.Seeds ?? []).ToDictionary(s => s.Id, StringComparer.Ordinal);
        var random = new SeedRandom(c.Seed,attempt);
        var candidates = Sorted(free.Where(p => Neighbors(p).Any(publicParcels.Contains)));
        if (candidates.Count < c.Departments.Count)
            throw new PlanningException("placement", "frontage-budget", "There are fewer public-frontage parcels than departments. Enlarge the envelope or simplify circulation.");
        var defs = Definitions(c).OrderByDescending(d => d.Exterior).ThenByDescending(d => d.Entrances)
            .ThenByDescending(DefaultTarget).ToList();
        var chosen = new Dictionary<string, Cell>(StringComparer.Ordinal);
        var cores = new Dictionary<string,HashSet<Cell>>(StringComparer.Ordinal);
        var componentOf = free.ToDictionary(p => p, p => components.FindIndex(comp => comp.Contains(p)));
        foreach (var d in defs.Where(d => oldSeeds.TryGetValue(d.Id, out var s) && s.Pinned))
        {
            var p = Parcel(oldSeeds[d.Id].Position, size);
            if (!free.Contains(p) || chosen.Values.Contains(p))
                throw new PlanningException("placement", "pinned-seed", $"Pinned seed {d.Id} is outside a free department parcel or overlaps another pinned seed.");
            var blocked=cores.Values.SelectMany(x=>x).Concat(defs.Where(other=>other.Id!=d.Id&&oldSeeds.TryGetValue(other.Id,out var old)&&old.Pinned).Select(other=>Parcel(oldSeeds[other.Id].Position,size))).ToHashSet();
            var core=FindCore(d,p,free,blocked,publicParcels,size,true,envelope);
            if(core==null)throw new PlanningException("placement","pinned-core",$"{d.Id}: the pinned seed cannot fit a usable room core and public frontage.");
            chosen.Add(d.Id, p);
            cores.Add(d.Id,core);
        }
        foreach (var d in defs.Where(d => !chosen.ContainsKey(d.Id)))
        {
            var remaining = defs.Count(x => !chosen.ContainsKey(x.Id));
            var uncovered = Enumerable.Range(0, components.Count).Where(i => !chosen.Values.Any(p => componentOf[p] == i)).ToHashSet();
            var occupied=cores.Values.SelectMany(x=>x).ToHashSet();
            var separated=cores.Where(kv=>!HasRequiredAdjacency(project.Topology!,d.Id,kv.Key)).SelectMany(kv=>kv.Value.SelectMany(p=>Neighbors(p).Append(p))).ToHashSet();
            separated.UnionWith(occupied);
            var list = candidates.Where(p => !chosen.Values.Contains(p) && (remaining > uncovered.Count || uncovered.Contains(componentOf[p])))
                .Where(p => !d.Exterior || ExteriorAnchor(envelope, free, p) != null)
                .Where(p => RequiredAdjacencyPossible(project.Topology!, d.Id, p, chosen))
                .Where(p=>FindCore(d,p,free,occupied,publicParcels,size,true,envelope)!=null).ToList();
            var spaced = list.Where(p => FindCore(d,p,free,separated,publicParcels,size,true,envelope)!=null).ToList();
            if(spaced.Count>0){list=spaced;occupied=separated;}
            random.Shuffle(list);
            if (list.Count == 0)
                throw new PlanningException("placement", "seed-constraints", $"Cannot place {d.Id} with its required adjacency/frontage. Move pinned seeds, increase the envelope or revise the relationship.");
            var best = list.OrderBy(p => SeedCost(c, project.Topology!, d, p, chosen, components, componentOf)).First();
            chosen.Add(d.Id, best);
            cores.Add(d.Id,FindCore(d,best,free,occupied,publicParcels,size,true,envelope)!);
        }
        var coreAtPosition=chosen.ToDictionary(kv=>kv.Value,kv=>cores[kv.Key]);
        var bestCost = SeedObjective(project.Topology!, chosen, size);
        for (var i = 0; i < c.OptimizationSteps; i++)
        {
            var a = defs[random.Next(defs.Count)]; var b = defs[random.Next(defs.Count)];
            if (a == b || oldSeeds.TryGetValue(a.Id, out var aa) && aa.Pinned || oldSeeds.TryGetValue(b.Id, out var bb) && bb.Pinned) continue;
            // Exterior kernels include a protected approach to the hull. Swapping an
            // exterior department onto an interior kernel would discard that guarantee.
            if(a.Exterior!=b.Exterior||CoreSide(a,size)!=CoreSide(b,size))continue;
            var ap = chosen[a.Id]; var bp = chosen[b.Id];
            if (a.Exterior && ExteriorAnchor(envelope, free, bp) == null || b.Exterior && ExteriorAnchor(envelope, free, ap) == null) continue;
            chosen[a.Id] = bp; chosen[b.Id] = ap;
            var cost = SeedObjective(project.Topology!, chosen, size);
            if (cost <= bestCost && defs.All(d => RequiredAdjacencyPossible(project.Topology!, d.Id, chosen[d.Id], chosen)&&coreAtPosition[chosen[d.Id]].Count(p=>Neighbors(p).Any(publicParcels.Contains))>=Math.Min(2,d.Entrances))) bestCost = cost;
            else { chosen[a.Id] = ap; chosen[b.Id] = bp; }
        }
        serviceParcels=MakeServiceReservations(c,envelope,publicParcels,attempt,coreAtPosition.Values.SelectMany(x=>x).ToHashSet(),chosen.Values.ToHashSet());
        foreach (var d in Definitions(c))
        {
            oldSeeds.TryGetValue(d.Id, out var old);
            plan.Seeds.Add(new DepartmentSeed
            {
                Id = d.Id, Position = old?.Pinned == true ? old.Position : Center(chosen[d.Id], size),
                Pinned = old?.Pinned == true, TargetArea = Math.Max(old?.TargetArea ?? DefaultTarget(d), Math.Min(d.MaxArea,StationHierarchy.RequiredFloorArea(d))),
                AllowMaintenanceAccess = old?.AllowMaintenanceAccess ?? d.Access != "secure",
                CoreFootprint=Sorted(Expand(coreAtPosition[chosen[d.Id]],size))
            });
        }
        var envelopeTiles = Expand(envelope,size);
        plan.Envelope = Sorted(envelopeTiles);
        plan.PublicReservations = Sorted(Expand(publicParcels, size));
        plan.ServiceReservations = Sorted(Expand(serviceParcels, size));
        plan.InitialServiceReservations=plan.ServiceReservations.ToList();
        plan.ExteriorBoundary = Sorted(envelopeTiles.Where(p => Neighbors(p).Any(q => !envelopeTiles.Contains(q))));
        plan.Stage = "placement";
        project.SpatialPlan = plan;
    }

    private static HashSet<Cell> MakeEnvelope(StationGenerationConfig c, int size,List<DepartmentSeed>? priorSeeds)
    {
        var target = c.Departments.Sum(d => Math.Clamp(Math.Max(priorSeeds?.FirstOrDefault(s => s.Id == d.Id)?.TargetArea ?? DefaultTarget(d),StationHierarchy.RequiredFloorArea(d)),d.MinArea,d.MaxArea));
        var desiredParcels = target / (c.Spatial.FillRatio * .68 * size * size * (1 - Math.Max(0,c.Spatial.MaintenanceFraction - .10)));
        var aspect = c.Archetype == "spine" ? 1.75 : 1.2;
        var cols = Math.Max(7, (int) Math.Ceiling(Math.Sqrt(desiredParcels * aspect)));
        var rows = Math.Max(7, (int) Math.Ceiling(desiredParcels / cols));
        if (cols % 2 == 0) cols++;
        if (rows % 2 == 0) rows++;
        // Room-fit kernels and service seams need room to negotiate. This allowance is
        // proportional to the station program, rather than filling the entire canvas.
        if(c.Departments.Count>=6){cols+=2;rows+=2;}
        if(c.Departments.Any(d=>StationHierarchy.RequiredFloorArea(d)>225)){cols+=2;rows+=2;}
        if (cols * size > c.Width - 2 * size || rows * size > c.Height - 2 * size)
            throw new PlanningException("placement", "program-budget", $"Department programs need approximately {cols * size}×{rows * size} tiles plus exterior clearance. Increase the canvas or reduce target areas.");
        var ox = (c.Width / size - cols) / 2; var oy = (c.Height / size - rows) / 2;
        var result = new HashSet<Cell>();
        var inset = Math.Max(cols >= 11 && rows >= 11 ? 3 : 2, c.Departments.Max(d=>CoreSide(d,size)));
        for (var y = 0; y < rows; y++)
        for (var x = 0; x < cols; x++)
        {
            var corner = Math.Min(x, cols - 1 - x) + Math.Min(y, rows - 1 - y) < 2;
            if (corner) continue;
            if (c.Archetype == "ring" && x > inset && x < cols - 1 - inset && y > inset && y < rows - 1 - inset) continue;
            if (c.Archetype == "spine" && (y == 0 || y == rows - 1) && x > 2 && x < cols - 3 && x % 4 == 0) continue;
            result.Add(new(ox + x, oy + y));
        }
        return result;
    }

    private static HashSet<Cell> MakeCirculation(StationGenerationConfig c, HashSet<Cell> envelope)
    {
        var minX = envelope.Min(p => p.X); var maxX = envelope.Max(p => p.X);
        var minY = envelope.Min(p => p.Y); var maxY = envelope.Max(p => p.Y);
        var cx = (minX + maxX) / 2; var cy = (minY + maxY) / 2;
        var result = new HashSet<Cell>();
        void Line(Cell a, Cell b)
        {
            var path = FindPath(a, p => p == b, envelope);
            if (path == null) throw new PlanningException("placement", "envelope-circulation", "The edited envelope cannot accommodate the selected circulation structure.");
            result.UnionWith(path);
        }
        if (c.Archetype == "ring")
        {
            var inset = Math.Max(maxX - minX >= 10 && maxY - minY >= 10 ? 3 : 2, c.Departments.Max(d=>CoreSide(d,c.Spatial.CellSize)));
            Cell[] corners = [new(minX + inset,minY + inset),new(maxX - inset,minY + inset),new(maxX - inset,maxY - inset),new(minX + inset,maxY - inset)];
            if (corners.All(envelope.Contains))
                for (var i = 0; i < 4; i++) Line(corners[i], corners[(i + 1) % 4]);
            else Line(Nearest(envelope, new(minX,cy)), Nearest(envelope,new(maxX,cy)));
        }
        else
        {
            Line(Nearest(envelope,new(minX,cy)),Nearest(envelope,new(maxX,cy)));
            if (c.Archetype == "hub") Line(Nearest(envelope,new(cx,minY + 1)),Nearest(envelope,new(cx,maxY - 1)));
            else
                foreach (var x in new[] { minX + (maxX - minX) / 3, maxX - (maxX - minX) / 3 })
                    Line(Nearest(envelope,new(x,minY + 1)),Nearest(envelope,new(x,maxY - 1)));
        }
        return result;
    }

    private static HashSet<Cell> MakeServiceReservations(StationGenerationConfig c, HashSet<Cell> envelope, HashSet<Cell> streets,int attempt,HashSet<Cell> protectedCores,HashSet<Cell> seeds)
    {
        var domain=new HashSet<Cell>(envelope);domain.ExceptWith(streets);
        var free = new HashSet<Cell>(domain); free.ExceptWith(protectedCores);
        var result = new HashSet<Cell>();
        var budget = Math.Max(3,(int) Math.Round(envelope.Count * c.Spatial.MaintenanceFraction));
        var random = new SeedRandom(c.Seed,31+attempt*17);
        var frontier = Sorted(free.Where(p => Neighbors(p).Any(streets.Contains)));
        var choices = new List<(Cell A,Cell B)>();
        foreach (var a in frontier)
        foreach (var b in frontier.Where(b => b.Y > a.Y || b.Y == a.Y && b.X > a.X))
            if (a.Distance(b) is >= 2 and <= 7 && Neighbors(a).Where(streets.Contains).All(sa => Neighbors(b).Where(streets.Contains).All(sb => sa.Distance(sb) >= 3)))
                choices.Add((a,b));
        random.Shuffle(choices);
        foreach (var (a,b) in choices)
        {
            if (result.Count >= budget || !free.Contains(a) || !free.Contains(b)) continue;
            var path = FindPath(a,p => p == b,free);
            if (path == null || path.Count < 3 || path.Count > Math.Min(8,budget - result.Count + 2)) continue;
            var remaining = new HashSet<Cell>(domain); remaining.ExceptWith(result);remaining.ExceptWith(path);
            var components = StationValidator.Components(remaining);
            if (components.Count > c.Departments.Count || components.Any(comp => !comp.Overlaps(seeds)||!UsableSector(comp,c,size:c.Spatial.CellSize) || !comp.Any(p => Neighbors(p).Any(streets.Contains)))) continue;
            result.UnionWith(path);free.ExceptWith(path);
        }
        if (result.Count == 0)
        {
            foreach (var start in frontier)
            {
                var path = FindPath(start,p => p.Distance(start) >= 2,free);
                if (path == null) continue;
                var remaining = new HashSet<Cell>(domain); remaining.ExceptWith(path);
                var components = StationValidator.Components(remaining);
                if (components.Count > c.Departments.Count || components.Any(comp => !comp.Overlaps(seeds)||!UsableSector(comp,c,size:c.Spatial.CellSize) || !comp.Any(p => Neighbors(p).Any(streets.Contains)))) continue;
                result.UnionWith(path); break;
            }
        }
        // Carry service branches all the way to the exterior before allocating the
        // remaining reserve. These become planned EVA and solar access frontages.
        var endpoints = new List<Cell>();
        foreach (var endpoint in Sorted(free.Where(p => Neighbors(p).Any(q => !envelope.Contains(q)))))
        {
            if (endpoints.Any(p => p.Distance(endpoint) < 4)) continue;
            var path=FindPath(endpoint,p=>result.Contains(p)||Neighbors(p).Any(streets.Contains),free);
            if(path==null)continue;
            var candidate=new HashSet<Cell>(result);candidate.UnionWith(path);
            if(!CanReserve(candidate))continue;
            result=candidate;endpoints.Add(endpoint);
            if(endpoints.Count>=Math.Max(4,c.Spatial.SolarMax+c.Spatial.SpaceAccessMax))break;
        }
        // More than a few short seams: fill connected service pockets and leave a
        // deliberate buffer around department kernels. Whole parcels retain width.
        while(result.Count<budget)
        {
            var additions=Sorted(free.Where(p=>!result.Contains(p)&&(Neighbors(p).Any(result.Contains)||Neighbors(p).Any(streets.Contains))));
            random.Shuffle(additions);
            additions=additions.OrderByDescending(p=>Neighbors(p).Count(result.Contains)*3+Neighbors(p).Count(protectedCores.Contains)).ToList();
            var progressed=false;
            foreach(var p in additions)
            {
                var candidate=new HashSet<Cell>(result){p};
                if(!CanReserve(candidate))continue;
                result=candidate;progressed=true;break;
            }
            if(!progressed)break;
        }
        if (result.Count == 0)
            throw new PlanningException("placement", "service-budget", "No usable internal service seam fits this envelope. Increase space or adjust the circulation structure.");
        return result;

        bool CanReserve(HashSet<Cell> candidate)
        {
            if(candidate.Overlaps(protectedCores))return false;
            var remaining=new HashSet<Cell>(domain);remaining.ExceptWith(candidate);
            foreach(var component in StationValidator.Components(remaining))
            {
                // A seedless leftover island is service space as well, avoiding
                // narrow scraps that can never become a usable department.
                if(!component.Overlaps(seeds)){candidate.UnionWith(component);continue;}
                if(!UsableSector(component,c,c.Spatial.CellSize)||!component.Any(p=>Neighbors(p).Any(streets.Contains)))return false;
            }
            return true;
        }
    }

    private static bool UsableSector(HashSet<Cell> cells,StationGenerationConfig c,int size)
    {
        var minimum=c.Departments.Min(d=>d.MinArea);
        if(ConservativeFloor(cells,size)<minimum)return false;
        var definition=c.Departments.OrderBy(d=>d.MinArea).First();
        return cells.Any(p=>FindCore(definition,p,cells,[],[],size,false)!=null);
    }

    private static HashSet<Cell>? FindCore(DepartmentDefinition d,Cell seed,HashSet<Cell> free,HashSet<Cell> blocked,HashSet<Cell> streets,int size,bool frontage=true,HashSet<Cell>? envelope=null)
    {
        var side=CoreSide(d,size);
        var choices=new List<HashSet<Cell>>();
        for(var y=seed.Y-side+1;y<=seed.Y;y++)
        for(var x=seed.X-side+1;x<=seed.X;x++)
        {
            var patch=new Rect(x,y,side,side).Cells().ToHashSet();
            if(!patch.All(free.Contains)||patch.Overlaps(blocked))continue;
            if(frontage&&patch.Count(p=>Neighbors(p).Any(streets.Contains))<Math.Min(2,d.Entrances))continue;
            if(d.Exterior&&envelope!=null)
            {
                var allowed=free.Where(p=>!blocked.Contains(p)).ToHashSet();
                var anchor=ExteriorAnchor(envelope,allowed,seed);
                if(anchor==null)continue;
                patch.UnionWith(anchor);
            }
            choices.Add(patch);
        }
        return choices.OrderByDescending(patch=>patch.Count(p=>Neighbors(p).Any(streets.Contains))).ThenBy(patch=>patch.Sum(p=>p.Distance(seed))).FirstOrDefault();
    }

    private static int CoreSide(DepartmentDefinition d,int size)
    {
        var minimumSide=Math.Max(2,Math.Max(Math.Min(5,(int)Math.Sqrt(d.MinArea)),(int)Math.Ceiling(Math.Sqrt(StationHierarchy.RequiredFloorArea(d)))));
        return Math.Max(d.Entrances>1?2:1,(minimumSide+2+size-1)/size);
    }

    private static double SeedCost(StationGenerationConfig c, StationTopology topology, DepartmentDefinition d, Cell candidate,
        Dictionary<string,Cell> chosen, List<HashSet<Cell>> components, Dictionary<Cell,int> componentOf)
    {
        var component = componentOf[candidate];
        var load = chosen.Where(kv => componentOf[kv.Value] == component).Sum(kv => DefaultTarget(c.Departments.First(def => def.Id == kv.Key)));
        var capacity = components[component].Count * c.Spatial.CellSize * c.Spatial.CellSize * .68;
        var cost = 100 * (load + DefaultTarget(d)) / capacity;
        foreach(var (otherId,p) in chosen)
        {
            var adjacentRequired=topology.Edges.Any(e=>e.Hard&&e.Relation=="adjacency"&&Pair(e.From,e.To,d.Id,otherId));
            if(!adjacentRequired)cost+=Math.Pow(Math.Max(0,3-candidate.Distance(p)),2)*1000;
        }
        foreach (var edge in topology.Edges.Where(e => e.From == d.Id || e.To == d.Id))
        {
            var other = edge.From == d.Id ? edge.To : edge.From;
            if (!chosen.TryGetValue(other,out var p)) continue;
            var distance = candidate.Distance(p) * c.Spatial.CellSize;
            cost += edge.Weight * (edge.Priority == "avoided" ? Math.Max(0,(edge.MaxDistance ?? 30) - distance) : distance) * (edge.Relation == "adjacency" ? 2 : .2);
        }
        return cost;
    }

    private static double SeedObjective(StationTopology t, Dictionary<string,Cell> seeds, int size) => t.Edges.Sum(e =>
    {
        var distance = seeds[e.From].Distance(seeds[e.To]) * size;
        return e.Weight * (e.Priority == "avoided" ? Math.Max(0,(e.MaxDistance ?? 30) - distance) :
            e.MaxDistance is { } max ? Math.Max(0,distance - max) : distance);
    });

    private static bool RequiredAdjacencyPossible(StationTopology t, string id, Cell candidate, Dictionary<string,Cell> chosen) =>
        t.Edges.Where(e => e.Hard && e.Relation == "adjacency" && (e.From == id || e.To == id))
            .All(e => !chosen.TryGetValue(e.From == id ? e.To : e.From,out var other) || other.Distance(candidate) == 1);

    private static List<Cell>? ExteriorAnchor(HashSet<Cell> envelope, HashSet<Cell> free, Cell seed)
    {
        var minX = envelope.Min(p => p.X); var maxX = envelope.Max(p => p.X);
        var minY = envelope.Min(p => p.Y); var maxY = envelope.Max(p => p.Y);
        return FindPath(seed,p => (p.X == minX || p.X == maxX || p.Y == minY || p.Y == maxY),free,4);
    }

    public static void Grow(StationProject project,bool planHierarchy=true)
    {
        var c = project.Config; var plan = NeedPlan(project); var size = plan.CellSize;
        if(plan.InitialServiceReservations.Count>0)plan.ServiceReservations=plan.InitialServiceReservations.ToList();
        else {plan.ServiceReservations=Sorted(Expand(Parcels(plan.ServiceReservations,size),size));plan.InitialServiceReservations=plan.ServiceReservations.ToList();}
        ValidateDraftMasks(project,true);
        var envelope = Parcels(plan.Envelope,size);
        var publicParcels = Parcels(plan.PublicReservations,size);
        var serviceParcels = Parcels(plan.ServiceReservations,size);
        var free = new HashSet<Cell>(envelope); free.ExceptWith(publicParcels); free.ExceptWith(serviceParcels);
        var owners = new Dictionary<Cell,string>();
        var regions = plan.Seeds.OrderBy(s => s.Id,StringComparer.Ordinal).ToDictionary(s => s.Id,s => new HashSet<Cell>(),StringComparer.Ordinal);
        var definitions = c.Departments.ToDictionary(d => d.Id,StringComparer.Ordinal);
        foreach (var seed in plan.Seeds.OrderByDescending(s=>s.CoreFootprint.Count>0).ThenByDescending(s => definitions[s.Id].Exterior).ThenByDescending(s => s.Pinned).ThenBy(s => s.Id,StringComparer.Ordinal))
        {
            var start = Parcel(seed.Position,size);
            if (!free.Contains(start) || owners.ContainsKey(start))
                throw new PlanningException("growth", "seed-overlap", $"{seed.Id}: seed must be on an unclaimed department parcel.");
            var others=plan.Seeds.Where(other=>other.Id!=seed.Id).Select(other=>Parcel(other.Position,size)).Concat(owners.Keys).ToHashSet();
            var stored=Parcels(seed.CoreFootprint,size);
            var core=stored.Count>0&&stored.Contains(start)&&stored.All(free.Contains)&&!stored.Overlaps(others)&&ConservativeFloor(stored,size)>=StationHierarchy.RequiredFloorArea(definitions[seed.Id])?stored:FindCore(definitions[seed.Id],start,free,others,publicParcels,size,true,envelope);
            if(core==null)throw new PlanningException("growth","seed-core",$"{seed.Id}: the seed has no room for a usable interior core and public frontage. Move it away from neighboring seeds or reservations.");
            foreach(var p in core)Claim(p,seed.Id);
            seed.CoreFootprint=Sorted(Expand(core,size));
        }
        foreach (var seed in plan.Seeds.OrderBy(s => s.Id,StringComparer.Ordinal))
        {
            var available = new HashSet<Cell>(free.Where(p => !owners.ContainsKey(p) || owners[p] == seed.Id));
            var start = Parcel(seed.Position,size);
            var publicAccess = FindPath(start,p => Neighbors(p).Any(publicParcels.Contains),available);
            if (publicAccess == null) throw new PlanningException("growth","seed-access",$"{seed.Id}: no free route to public circulation from its seed.");
            foreach (var p in publicAccess) Claim(p,seed.Id);
            // Reserve enough independent frontage parcels before competing regions grow.
            // This is a hard entrance capacity reservation, not a hope that later growth
            // happens to leave a second doorway along a single five-tile frontage.
            while (regions[seed.Id].Count(p => Neighbors(p).Any(publicParcels.Contains)) < definitions[seed.Id].Entrances)
            {
                var extra = FindPath(start,p => !regions[seed.Id].Contains(p) && Neighbors(p).Any(publicParcels.Contains),available);
                if (extra == null) throw new PlanningException("growth","entrance-frontage",$"{seed.Id}: no space for its required public entrance frontages; move nearby seeds.");
                foreach (var p in extra) Claim(p,seed.Id);
            }
            if (definitions[seed.Id].Exterior)
            {
                var anchor = ExteriorAnchor(envelope,available,start);
                if (anchor == null) throw new PlanningException("growth","exterior-access",$"{seed.Id}: no available exterior frontage; move the seed toward an outside edge.");
                foreach (var p in anchor) Claim(p,seed.Id);
            }
        }
        // Protect usable minimum footprints before the target-area competition. This
        // conservative one-cell erosion treats every boundary as a wall, so the final
        // shared-wall derivation can only leave at least this much usable floor.
        var minimumSteps=0;
        while(plan.Seeds.Any(seed=>ConservativeFloor(regions[seed.Id],size)<definitions[seed.Id].MinArea))
        {
            if(++minimumSteps>c.Spatial.GrowthSteps)throw new PlanningException("growth","growth-budget","Minimum-floor allocation exceeded the configured growth budget.");
            var progressed=false;
            foreach(var seed in plan.Seeds.OrderBy(seed=>ConservativeFloor(regions[seed.Id],size)/(double)definitions[seed.Id].MinArea).ThenBy(seed=>seed.Id,StringComparer.Ordinal))
            {
                if(ConservativeFloor(regions[seed.Id],size)>=definitions[seed.Id].MinArea)continue;
                var candidate=regions[seed.Id].SelectMany(Neighbors).Where(p=>free.Contains(p)&&!owners.ContainsKey(p)&&!ViolatesStrictAdjacency(project.Topology!,seed.Id,p,owners))
                    .Distinct().OrderByDescending(p=>Neighbors(p).Count(q=>regions[seed.Id].Contains(q))).ThenBy(p=>p.Distance(Parcel(seed.Position,size))).ThenBy(p=>p.Y).ThenBy(p=>p.X).Cast<Cell?>().FirstOrDefault();
                if(candidate==null)continue;
                Claim(candidate.Value,seed.Id);progressed=true;
            }
            if(!progressed)throw new PlanningException("growth","minimum-floor", "Department seeds are too crowded to reserve their minimum usable floors. Move seeds apart or enlarge the envelope.");
        }
        var steps = 0;
        var random = new SeedRandom(c.Seed,47);
        while (owners.Count < free.Count && steps++ < c.Spatial.GrowthSteps)
        {
            var choices = new List<(string Id,Cell Cell,double Cost)>();
            foreach (var seed in plan.Seeds.OrderBy(s => s.Id,StringComparer.Ordinal))
            {
                var cells = regions[seed.Id];
                if ((cells.Count + 1) * size * size > definitions[seed.Id].MaxArea + 2 * size * size) continue;
                var frontier = cells.SelectMany(Neighbors).Where(p => free.Contains(p) && !owners.ContainsKey(p)).Distinct();
                foreach (var cell in frontier)
                {
                    if (ViolatesStrictAdjacency(project.Topology!,seed.Id,cell,owners)) continue;
                    var same = Neighbors(cell).Count(p => owners.TryGetValue(p,out var owner) && owner == seed.Id);
                    var ratio = cells.Count * size * size / Math.Max(1.0,seed.TargetArea);
                    var distance = cell.Distance(Parcel(seed.Position,size));
                    var cost = ratio * 20 + distance * .25 - same * 2;
                    foreach (var other in Neighbors(cell).Where(owners.ContainsKey).Select(p => owners[p]).Distinct())
                    foreach (var edge in project.Topology!.Edges.Where(e => Pair(e.From,e.To,seed.Id,other)))
                        cost += edge.Priority == "avoided" ? edge.Weight * 2 : -edge.Weight * .4;
                    choices.Add((seed.Id,cell,cost));
                }
            }
            if (choices.Count == 0) break;
            random.Shuffle(choices);
            var best = choices.OrderBy(x => x.Cost).First();
            Claim(best.Cell,best.Id);
        }
        if (steps >= c.Spatial.GrowthSteps && owners.Count < free.Count)
            throw new PlanningException("growth","growth-budget",$"Growth stopped after {c.Spatial.GrowthSteps} bounded parcel expansions; increase the budget or simplify the program.");
        plan.Regions = plan.Seeds.OrderBy(s => s.Id,StringComparer.Ordinal).Select(seed => new DepartmentRegion
        {
            Id = seed.Id, Seed = seed.Position, TargetArea = seed.TargetArea, Territory = Sorted(Expand(regions[seed.Id],size))
        }).ToList();
        var leftover=free.Where(p=>!owners.ContainsKey(p)).ToHashSet();
        foreach(var component in StationValidator.Components(leftover))
            if(component.Any(p=>Neighbors(p).Any(q=>publicParcels.Contains(q)||serviceParcels.Contains(q))))serviceParcels.UnionWith(component);
        plan.ServiceReservations=Sorted(Expand(serviceParcels,size));
        plan.StructuralReserve = Sorted(Expand(leftover.Where(p=>!serviceParcels.Contains(p)),size));
        plan.PublicCorridors = plan.PublicReservations.ToList();
        plan.Maintenance.Clear();
        plan.DeepMaintenanceRooms.Clear();plan.SolarInstallations.Clear();plan.MaintenanceSpaceAccess.Clear();
        plan.CommonMaintenanceRooms.Clear();
        plan.Stage = "growth";
        RebuildBoundaries(project,planHierarchy);

        void Claim(Cell p,string id)
        {
            if (owners.TryGetValue(p,out var existing) && existing != id)
                throw new PlanningException("growth","anchor-overlap",$"{id} exterior/public access conflicts with {existing}; move one of the seeds.");
            owners[p] = id; regions[id].Add(p);
        }
    }

    private static int ConservativeFloor(HashSet<Cell> parcels,int size)
    {
        var tiles=Expand(parcels,size);
        return tiles.Count(p=>Neighbors(p).All(tiles.Contains));
    }

    private static bool HasRequiredAdjacency(StationTopology t,string a,string b) =>
        t.Edges.Any(e=>e.Hard&&e.Relation=="adjacency"&&Pair(e.From,e.To,a,b));

    private static bool ViolatesStrictAdjacency(StationTopology t,string id,Cell candidate,Dictionary<Cell,string> owners) =>
        Neighbors(candidate).Where(owners.ContainsKey).Select(p => owners[p]).Any(other => other != id &&
            (!HasRequiredAdjacency(t,id,other) || t.Edges.Any(e => e.Priority == "avoided" && e.StrictAvoidance && e.Relation == "adjacency" && Pair(e.From,e.To,id,other))));

    public static void BuildMaintenance(StationProject project)
    {
        var plan = NeedPlan(project);
        if (plan.Regions.Count == 0) throw new PlanningException("growth","missing","Grow department territories before maintenance.");
        plan.DeepMaintenanceRooms.Clear();plan.SolarInstallations.Clear();plan.MaintenanceSpaceAccess.Clear();
        plan.CommonMaintenanceRooms.Clear();
        plan.Maintenance = plan.ServiceReservations.ToList();
        plan.Stage = "maintenance";
        RebuildBoundaries(project,false);
        MaintenancePlanner.Create(project);
        RebuildBoundaries(project);
    }

    public static void RebuildBoundaries(StationProject project,bool planHierarchy=true)
    {
        var c = project.Config; var plan = NeedPlan(project);
        ValidateDraftMasks(project,false);
        var envelope = plan.Envelope.ToHashSet();
        var regionById = plan.Regions.ToDictionary(r => r.Id,StringComparer.Ordinal);
        var owners = new Dictionary<Cell,string>();
        foreach (var region in plan.Regions)
        foreach (var p in region.Territory)
        {
            if (!envelope.Contains(p)) throw new PlanningException("growth","outside-envelope",$"{region.Id}: territory extends outside the envelope.");
            if (!owners.TryAdd(p,region.Id)) throw new PlanningException("growth","territory-overlap",$"{region.Id}: territory overlaps another department.");
        }
        var publicReservation=plan.PublicReservations.ToHashSet();
        var serviceReservation=plan.ServiceReservations.ToHashSet();
        foreach(var p in plan.PublicCorridors.Where(p=>!publicReservation.Contains(p)))
        {
            if(!envelope.Contains(p)||owners.ContainsKey(p)||serviceReservation.Contains(p))throw new PlanningException("growth","public-overlap","Painted public circulation crosses a department, service reservation or exterior space.");
            publicReservation.Add(p);
        }
        foreach(var p in plan.Maintenance.Where(p=>!serviceReservation.Contains(p)))
        {
            if(!envelope.Contains(p)||owners.ContainsKey(p)||publicReservation.Contains(p))throw new PlanningException("maintenance","maintenance-overlap","Painted maintenance crosses a department, public circulation or exterior space.");
            serviceReservation.Add(p);
        }
        plan.PublicReservations=Sorted(publicReservation);plan.ServiceReservations=Sorted(serviceReservation);
        foreach (var p in plan.PublicReservations) AddOwner(p,PublicOwner);
        foreach (var p in plan.ServiceReservations) AddOwner(p,ServiceOwner);
        plan.StructuralReserve = Sorted(envelope.Where(p => !owners.ContainsKey(p) && !plan.Maintenance.Contains(p)));
        foreach (var p in plan.StructuralReserve) AddOwner(p,StructuralOwner);
        foreach (var p in plan.Maintenance.Where(p => !owners.ContainsKey(p))) AddOwner(p,ServiceOwner);
        var hull = ExpectedHull(c,plan);
        var walls = new HashSet<Cell>();
        foreach (var region in plan.Regions)
        foreach (var p in region.Territory)
        foreach (var q in Neighbors(p))
        {
            if (hull.Contains(p)) continue;
            if (!owners.TryGetValue(q,out var neighbor) || neighbor != region.Id && neighbor.StartsWith('\0')) walls.Add(p);
            else if (neighbor != region.Id)
                walls.Add(string.CompareOrdinal(region.Id,neighbor) > 0 ? p : q);
        }
        walls.ExceptWith(hull);
        var publicTiles = plan.PublicCorridors.ToHashSet(); publicTiles.ExceptWith(hull);
        var maintenance = plan.Maintenance.ToHashSet(); maintenance.ExceptWith(hull);
        plan.Ports.Clear();
        foreach (var region in plan.Regions) region.Cells = Sorted(region.Territory.Where(p => !walls.Contains(p) && !hull.Contains(p)));
        var usedPortCells = new HashSet<Cell>();
        foreach (var region in plan.Regions)
        {
            var definition = c.Departments.Single(d => d.Id == region.Id);
            var floor = region.Cells.ToHashSet();
            var candidates = PortCandidates(floor,publicTiles,walls,hull,c.CorridorWidth)
                .OrderBy(port => port.Center.Distance(region.Seed)).ThenBy(port => port.Center.Y).ThenBy(port => port.Center.X).ToList();
            var count = 0;
            foreach (var port in candidates)
            {
                if (port.Cells.Any(usedPortCells.Contains)) continue;
                port.Department = region.Id; port.Kind = "public"; port.Access = definition.Access;
                plan.Ports.Add(port); usedPortCells.UnionWith(port.Cells);
                if (++count == definition.Entrances) break;
            }
            if (count < definition.Entrances)
                throw new PlanningException("growth","entrance-frontage",$"{region.Id} needs {definition.Entrances} width-{c.CorridorWidth} public entrances; grow/paint more public frontage or move its seed.");
            var seed = plan.Seeds.Single(s => s.Id == region.Id);
            if (seed.AllowMaintenanceAccess && maintenance.Count > 0)
            {
                var service = PortCandidates(floor,maintenance,walls,hull,Math.Max(1,c.MaintenanceWidth))
                    .Where(port => port.Cells.All(p => !usedPortCells.Contains(p)))
                    .OrderBy(port => port.Center.Distance(region.Seed)).FirstOrDefault();
                if (service != null)
                {
                    service.Department = region.Id; service.Kind = "maintenance"; service.Access = definition.Access;
                    plan.Ports.Add(service); usedPortCells.UnionWith(service.Cells);
                }
            }
        }
        foreach (var edge in project.Topology!.Edges.Where(e => e.Hard && e.Relation == "adjacency"))
        {
            if (plan.Ports.Any(p => p.Kind == "adjacency" && Pair(p.Department,p.OtherDepartment ?? "",edge.From,edge.To))) continue;
            var a = regionById[edge.From]; var b = regionById[edge.To];
            var port = PortCandidates(a.Cells.ToHashSet(),b.Cells.ToHashSet(),walls,hull,c.CorridorWidth)
                .FirstOrDefault(candidate => candidate.Cells.All(p => !usedPortCells.Contains(p)));
            if (port == null) throw new PlanningException("growth","required-adjacency",$"{edge.From} and {edge.To} lack a usable shared boundary for their Required adjacency; move their seeds together or adjust territory.");
            port.Department = edge.From; port.OtherDepartment = edge.To; port.Kind = "adjacency";
            var accesses = c.Departments.Where(d => d.Id == edge.From || d.Id == edge.To).Select(d => d.Access).ToList();
            port.Access = accesses.Contains("secure") ? "secure" : accesses.Contains("restricted") ? "restricted" : "public";
            plan.Ports.Add(port); usedPortCells.UnionWith(port.Cells);
        }
        walls.ExceptWith(usedPortCells);
        MaintenancePlanner.ApplyBoundaries(plan,walls,maintenance);
        foreach (var region in plan.Regions) region.Cells = Sorted(region.Cells.Where(p => !usedPortCells.Contains(p)));
        plan.Walls = Sorted(walls); plan.Hull = Sorted(hull);
        plan.PublicCorridors = Sorted(publicTiles); plan.Maintenance = Sorted(maintenance);
        plan.ExteriorBoundary = Sorted(envelope.Where(p => Neighbors(p).Any(q => !envelope.Contains(q))));
        plan.ReservedExterior = DockingReservations(project);
        if(planHierarchy)StationHierarchyPlanner.Build(project);

        void AddOwner(Cell p,string id)
        {
            if (!envelope.Contains(p)) throw new PlanningException("placement","reservation-outside","Circulation/service reservations must stay inside the envelope.");
            if (!owners.TryAdd(p,id)) throw new PlanningException("growth","reservation-overlap","A region, circulation reservation or service reservation overlaps another ownership layer.");
        }
    }

    private static List<SpatialPort> PortCandidates(HashSet<Cell> room,HashSet<Cell> network,HashSet<Cell> walls,HashSet<Cell> hull,int width)
    {
        var result = new List<SpatialPort>();
        foreach (var center in Sorted(walls))
        foreach (var normal in Cell.Directions)
        {
            if (!room.Contains(center + normal * -1) || !network.Contains(center + normal)) continue;
            var tangent = new Cell(-normal.Y,normal.X);
            var cells = Enumerable.Range(-(width - 1) / 2,width).Select(i => center + tangent * i).ToList();
            if (cells.All(p => walls.Contains(p) && !hull.Contains(p) && room.Contains(p + normal * -1) && network.Contains(p + normal)))
                result.Add(new SpatialPort { Center = center,Cells = cells,Width = width });
        }
        return result;
    }

    public static HashSet<Cell> ExpectedHull(StationGenerationConfig c,StationSpatialPlan plan)
    {
        var envelope = plan.Envelope.ToHashSet();
        var boundary = envelope.Where(p => Neighbors(p).Any(q => !envelope.Contains(q))).ToHashSet();
        var result = new HashSet<Cell>(boundary);
        for (var i = 1; i < c.HullThickness; i++)
            result.UnionWith(result.SelectMany(Neighbors).Where(envelope.Contains).ToArray());
        return result;
    }

    private static List<Cell> DockingReservations(StationProject project)
    {
        var c = project.Config; var plan = project.SpatialPlan!; var envelope = plan.Envelope.ToHashSet();
        var reservations = new HashSet<Cell>();
        foreach (var definition in c.Departments.Where(d => d.Exterior))
        {
            var region = plan.Regions.Single(r => r.Id == definition.Id);
            var cells = region.Territory.ToHashSet();
            var found = false;
            foreach (var p in Sorted(cells))
            {
                if (!plan.Hull.Contains(p)) continue;
                foreach (var normal in Cell.Directions)
                {
                    if (envelope.Contains(p + normal)) continue;
                    var tangent = new Cell(-normal.Y,normal.X);
                    var strip = new HashSet<Cell>(); var legal = true;
                    for (var step = 1; step < Math.Max(c.Width,c.Height); step++)
                    {
                        var center = p + normal * step;
                        if (!StationGenerator.Inside(c,center)) break;
                        for (var w = -1; w <= 1; w++)
                        {
                            var q = center + tangent * w;
                            if (!StationGenerator.Inside(c,q) || envelope.Contains(q) || reservations.Contains(q)) { legal = false; break; }
                            strip.Add(q);
                        }
                        if (!legal) break;
                    }
                    if (!legal || strip.Count == 0) continue;
                    reservations.UnionWith(strip); found = true; break;
                }
                if (found) break;
            }
            if (!found) throw new PlanningException("growth","docking-frontage",$"{definition.Id} needs unobstructed outer frontage for a docking reservation. A courtyard window does not count as a docking approach.");
        }
        return Sorted(reservations);
    }

    private static void ValidateDraftMasks(StationProject project,bool parcels)
    {
        var plan = NeedPlan(project); var c = project.Config;
        if (plan.CellSize is < 4 or > 8 || plan.CellSize < c.CorridorWidth + 2 || plan.Envelope.Count == 0 || plan.Envelope.Count > c.Width * c.Height || plan.Envelope.Any(p => !StationGenerator.Inside(c,p,2)))
            throw new PlanningException("placement","envelope","The envelope is empty, out of bounds, oversized, or uses an invalid parcel size.");
        var expected = c.Departments.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        if (plan.Seeds.Count != expected.Count || !expected.SetEquals(plan.Seeds.Select(s => s.Id)) || plan.Seeds.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != plan.Seeds.Count)
            throw new PlanningException("placement","seeds","Every included department needs exactly one seed.");
        foreach (var seed in plan.Seeds)
        {
            var d = c.Departments.Single(d => d.Id == seed.Id);
            if (seed.TargetArea < d.MinArea || seed.TargetArea > d.MaxArea || !plan.Envelope.Contains(seed.Position))
                throw new PlanningException("placement","seed-area",$"{seed.Id}: seed target must satisfy its area limits and the position must be in the envelope.");
        }
        if (parcels)
        {
            foreach (var layer in new[] { plan.Envelope,plan.PublicReservations,plan.ServiceReservations })
                if (!Expand(Parcels(layer,plan.CellSize),plan.CellSize).SetEquals(layer))
                    throw new PlanningException("placement","reservation-parcels","Growth needs envelope and circulation reservations painted as whole parcels.");
        }
        if (StationValidator.Components(plan.Envelope.ToHashSet()).Count != 1)
            throw new PlanningException("placement","envelope-connectivity","The envelope must be one connected station footprint.");
        var publicTiles = plan.PublicReservations.ToHashSet();
        if (publicTiles.Count == 0 || StationValidator.Components(publicTiles).Count != 1)
            throw new PlanningException("placement","public-reservation","Public circulation reservations must form one connected network.");
        if (publicTiles.Overlaps(plan.ServiceReservations))
            throw new PlanningException("placement","reservation-overlap","Public and service reservations overlap.");
        if (plan.PublicReservations.Concat(plan.ServiceReservations).Any(p => !plan.Envelope.Contains(p)))
            throw new PlanningException("placement","reservation-outside","Public/service reservations must be within the envelope.");
    }

    private static Cell Nearest(IEnumerable<Cell> cells,Cell target) => cells.OrderBy(p => p.Distance(target)).ThenBy(p => p.Y).ThenBy(p => p.X).First();

    private static List<Cell>? FindPath(Cell start,Func<Cell,bool> goal,HashSet<Cell> allowed,int maxDistance = int.MaxValue)
    {
        if (!allowed.Contains(start)) return null;
        var queue = new Queue<Cell>(); queue.Enqueue(start);
        var parent = new Dictionary<Cell,Cell>(); var distances = new Dictionary<Cell,int> { [start] = 0 };
        while (queue.TryDequeue(out var current))
        {
            if (goal(current))
            {
                var result = new List<Cell> { current };
                while (parent.TryGetValue(current,out var previous)) { current = previous; result.Add(current); }
                result.Reverse(); return result;
            }
            if (distances[current] >= maxDistance) continue;
            foreach (var next in Neighbors(current))
                if (allowed.Contains(next) && distances.TryAdd(next,distances[current] + 1)) { parent[next] = current; queue.Enqueue(next); }
        }
        return null;
    }

    private static bool Pair(string a,string b,string x,string y) => a == x && b == y || a == y && b == x;

    public static GenerationResult Validate(StationProject project) => StationSpatialValidator.Validate(project);
}
