namespace Content.StationGeneration;

/// <summary>Planning geometry for service rooms and sealed exterior service access.</summary>
public static partial class MaintenancePlanner
{
    private static IEnumerable<Cell> Neighbors(Cell p) => Cell.Directions.Select(d=>p+d);
    private static List<Cell> Sorted(IEnumerable<Cell> cells) => StationGenerator.Sorted(cells);

    public static void Create(StationProject project)
    {
        var plan=project.SpatialPlan!;var c=project.Config;var s=c.Spatial;
        var random=new SeedRandom(c.Seed,701);
        var roomCount=s.DeepMaintenanceMin+random.Next(s.DeepMaintenanceMax-s.DeepMaintenanceMin+1);
        var solarCount=s.SolarMin+random.Next(s.SolarMax-s.SolarMin+1);
        var accessCount=s.SpaceAccessMin+random.Next(s.SpaceAccessMax-s.SpaceAccessMin+1);
        var roomCounts=Enumerable.Range(roomCount,s.DeepMaintenanceMax-roomCount+1).Concat(Enumerable.Range(s.DeepMaintenanceMin,roomCount-s.DeepMaintenanceMin)).ToList();
        var baseline=ProjectJson.Clone(plan);PlanningException? last=null;
        for(var attempt=0;attempt<c.CandidateBudget;attempt++)
        {
            if(attempt>0)project.SpatialPlan=ProjectJson.Clone(baseline);
            var choiceRandom=attempt==0?random:new SeedRandom(c.Seed,701+attempt*31);
            try
            {
                PlaceCommonRooms(project,s.CommonMaintenanceMin,choiceRandom);
                PlaceRooms(project,roomCounts[attempt%roomCounts.Count],choiceRandom,attempt%2==1);
                NarrowAndReclaim(project,attempt);
                PlaceExteriorWithinBounds(project,solarCount,accessCount,choiceRandom,attempt);
                return;
            }
            catch(PlanningException e) when(e.Diagnostic.Code is "maintenance-pocket-fit" or "maintenance-approach-width" or "deep-maintenance-fit" or "common-maintenance-fit" or "solar-fit" or "space-access-fit"){last=e;}
        }
        throw last!;
    }

    private static void PlaceRooms(StationProject project,int count,SeedRandom random,bool minimumFirst=false)
    {
        if(count==0)return;
        var plan=project.SpatialPlan!;var maintenance=plan.Maintenance.ToHashSet();
        var halls=plan.PublicCorridors.ToHashSet();var hull=plan.Hull.ToHashSet();
        var portClearance=plan.Ports.SelectMany(p=>p.Cells).SelectMany(p=>Neighbors(p).Append(p)).ToHashSet();
        foreach(var port in plan.Ports.Where(p=>p.Kind=="maintenance"))
        foreach(var normal in Cell.Directions)
        {
            var patch=Enumerable.Range(1,project.Config.MaintenanceWidth).SelectMany(depth=>port.Cells.Select(p=>p+normal*depth)).ToHashSet();
            if(patch.IsSubsetOf(maintenance)){portClearance.UnionWith(patch);break;}
        }
        var size=plan.CellSize;
        var minArea=project.Config.Spatial.DeepMaintenanceRoomMinArea;var maxArea=project.Config.Spatial.DeepMaintenanceRoomMaxArea;
        var target=Math.Clamp(project.Config.Spatial.DeepMaintenanceRoomTargetArea,minArea,maxArea);
        var dimensionChoices=new[]{target,minArea}.Distinct().SelectMany(area=>Enumerable.Range(2,Math.Max(2,area)).Select(width=>(Width:width,Height:Math.Max(2,(area+width-1)/width)))
            .Where(d=>(long)d.Width*d.Height<=maxArea).OrderBy(d=>Math.Abs(d.Width-d.Height)).ThenBy(d=>d.Width*d.Height).Take(1)).Distinct().ToList();
        var alignedSide=((int)Math.Ceiling((Math.Sqrt(target)+2)/size))*size-2;
        if(alignedSide>=2&&(long)alignedSide*alignedSide>=minArea&&(long)alignedSide*alignedSide<=maxArea&&!dimensionChoices.Contains((alignedSide,alignedSide)))
            dimensionChoices.Insert(Math.Min(1,dimensionChoices.Count),(alignedSide,alignedSide));
        if(minimumFirst)dimensionChoices=dimensionChoices.OrderBy(d=>(long)d.Width*d.Height).ToList();
        if(dimensionChoices.Count==0)throw new PlanningException("maintenance","deep-maintenance-area","The requested deep-room area limits cannot fit a usable two-dimensional room floor.");
        var parcels=maintenance.Select(p=>new Cell(p.X/size,p.Y/size)).Distinct().OrderBy(p=>p.Y).ThenBy(p=>p.X).ToList();random.Shuffle(parcels);
        foreach(var dimensions in dimensionChoices)
        {
        var existingStructures=plan.Walls.Concat(plan.Hull).Concat(plan.DeepMaintenanceRooms.SelectMany(r=>r.Walls)).Concat(plan.CommonMaintenanceRooms.SelectMany(r=>r.Walls)).ToHashSet();
        var origins=parcels.Select(p=>new Cell(p.X*size,p.Y*size)).Concat(maintenance.Where(p=>Neighbors(p).Any(existingStructures.Contains))
            .SelectMany(p=>new[]{new Cell(p.X-1,p.Y-1),new Cell(p.X-dimensions.Width,p.Y-1),new Cell(p.X-1,p.Y-dimensions.Height),new Cell(p.X-dimensions.Width,p.Y-dimensions.Height)})).Distinct().ToList();
        random.Shuffle(origins);
        origins=origins.OrderByDescending(p=>new Rect(p.X,p.Y,dimensions.Width+2,dimensions.Height+2).Cells().Count(existingStructures.Contains)).ToList();
        foreach(var origin in origins)
        {
            if(plan.DeepMaintenanceRooms.Count==count)break;
            var bounds=new Rect(origin.X,origin.Y,dimensions.Width+2,dimensions.Height+2);
            var floor=new Rect(bounds.X+1,bounds.Y+1,dimensions.Width,dimensions.Height).Cells().ToHashSet();
            var ring=bounds.Cells().Except(floor).ToHashSet();
            if(!floor.IsSubsetOf(maintenance)||ring.Any(p=>!maintenance.Contains(p)&&!existingStructures.Contains(p)))continue;
            // Existing physical walls/hull are shared enclosure, not a second
            // ownership layer or extra floor allocation.
            var walls=ring.Where(maintenance.Contains).ToHashSet();
            var footprint=floor.Concat(walls).ToHashSet();
            if(footprint.Overlaps(portClearance))continue;
            var remainder=new HashSet<Cell>(maintenance);remainder.ExceptWith(footprint);
            if(remainder.Count==0||!RetainsWidth(remainder,footprint,project.Config.MaintenanceWidth)||!PublicReachable(remainder,halls,plan))continue;
            var distances=MaintenanceDistances(remainder,halls,plan);
            (List<Cell> Door,Cell Approach,HashSet<Cell> Clearance)? selected=null;var bestDoorDistance=int.MaxValue;
            foreach(var normal in Cell.Directions)
            foreach(var center in Sorted(walls.Where(p=>floor.Contains(p+normal*-1)&&remainder.Contains(p+normal))))
            {
                var tangent=new Cell(-normal.Y,normal.X);
                var door=Enumerable.Range(-(project.Config.MaintenanceWidth-1)/2,project.Config.MaintenanceWidth).Select(offset=>center+tangent*offset).ToList();
                var clearance=Enumerable.Range(1,project.Config.MaintenanceWidth).SelectMany(depth=>door.Select(p=>p+normal*depth)).ToHashSet();
                if(door.All(p=>walls.Contains(p)&&floor.Contains(p+normal*-1)&&remainder.Contains(p+normal)&&distances.GetValueOrDefault(p+normal,-1)>=2))
                {
                    if(!clearance.IsSubsetOf(remainder))continue;
                    var distance=distances[center+normal];if(distance>=bestDoorDistance)continue;
                    bestDoorDistance=distance;selected=(door,center+normal,clearance);
                }
            }
            if(selected==null)continue;
            walls.ExceptWith(selected.Value.Door);
            plan.DeepMaintenanceRooms.Add(new DeepMaintenanceRoom{Id=$"DeepMaintenance{plan.DeepMaintenanceRooms.Count+1}",Cells=Sorted(floor),Walls=Sorted(walls),PortCells=selected.Value.Door,MaintenanceApproach=selected.Value.Approach});
            // Later rooms must retain every earlier doorway's actual service
            // approach, including both tiles of its opening.
            portClearance.UnionWith(selected.Value.Door.SelectMany(p=>Neighbors(p).Append(p)));
            portClearance.UnionWith(selected.Value.Clearance);
            maintenance=remainder;
            existingStructures.UnionWith(walls);
        }
        }
        if(plan.DeepMaintenanceRooms.Count!=count)
            throw new PlanningException("maintenance","deep-maintenance-fit",$"Requested {count} deep-maintenance rooms, but only {plan.DeepMaintenanceRooms.Count} enclosed maintenance-only rooms fit. Increase maintenance space, enlarge the envelope or reduce the count bounds.");
        plan.Maintenance=Sorted(maintenance);
    }

    private static Dictionary<Cell,int> MaintenanceDistances(HashSet<Cell> maintenance,HashSet<Cell> halls,StationSpatialPlan? plan=null,CommonMaintenanceRoom? additional=null)
    {
        var entrances=new HashSet<Cell>(halls);
        foreach(var room in (plan?.CommonMaintenanceRooms??[]).Concat(additional==null?[]:[additional]))entrances.UnionWith(room.MaintenanceDoorCells);
        var distances=new Dictionary<Cell,int>();var queue=new Queue<Cell>();
        foreach(var p in maintenance.Where(p=>Neighbors(p).Any(entrances.Contains))){distances[p]=0;queue.Enqueue(p);}
        while(queue.TryDequeue(out var p))foreach(var q in Neighbors(p))if(maintenance.Contains(q)&&distances.TryAdd(q,distances[p]+1))queue.Enqueue(q);
        return distances;
    }

    private sealed record Frontage(Cell Center,Cell Normal,List<Cell> Barrier,Cell Approach);

    private static void PlaceExteriorWithinBounds(StationProject project,int solarTarget,int accessTarget,SeedRandom random,int attempt)
    {
        PlanningException? last=null;
        try{PlaceExterior(project,solarTarget,accessTarget,random);return;}
        catch(PlanningException e) when(e.Diagnostic.Code is "solar-fit" or "space-access-fit"){last=e;}
        var settings=project.Config.Spatial;
        for(var solars=solarTarget;solars>=settings.SolarMin;solars--)
        for(var accesses=accessTarget;accesses>=settings.SpaceAccessMin;accesses--)
        {
            if(solars==solarTarget&&accesses==accessTarget)continue;
            project.SpatialPlan!.SolarInstallations.Clear();project.SpatialPlan.MaintenanceSpaceAccess.Clear();
            try{PlaceExterior(project,solars,accesses,new SeedRandom(project.Config.Seed,1009+attempt*31+solars*7+accesses));return;}
            catch(PlanningException e) when(e.Diagnostic.Code is "solar-fit" or "space-access-fit"){last=e;if(e.Diagnostic.Code=="solar-fit")break;}
        }
        throw last??new PlanningException("maintenance","exterior-feature-fit","Exterior service installations cannot meet their configured minimum counts.");
    }

    private static void PlaceExterior(StationProject project,int solarCount,int accessCount,SeedRandom random)
    {
        var plan=project.SpatialPlan!;var c=project.Config;
        var envelope=plan.Envelope.ToHashSet();var hull=plan.Hull.ToHashSet();var maintenance=plan.Maintenance.ToHashSet();
        var outside=OuterSpace(c,envelope);var keepout=plan.ReservedExterior.ToHashSet();
        var frontages=new List<Frontage>();
        foreach(var p in Sorted(hull))foreach(var normal in Cell.Directions)
        {
            var tangent=new Cell(-normal.Y,normal.X);
            var outer=new[]{p,p+tangent};
            var barrier=outer.SelectMany(q=>Enumerable.Range(0,c.HullThickness).Select(depth=>q+normal*-depth)).ToList();
            if(barrier.All(hull.Contains)&&outer.All(q=>maintenance.Contains(q+normal*-c.HullThickness)&&outside.Contains(q+normal)))
                frontages.Add(new(p,normal,barrier,p+normal*-c.HullThickness));
        }
        random.Shuffle(frontages);
        var occupied=new HashSet<Cell>(keepout);var usedFrontages=new List<Cell>();
        foreach(var frontage in frontages)
        {
            if(plan.SolarInstallations.Count==solarCount)break;
            if(usedFrontages.Any(p=>p.Distance(frontage.Center)<8))continue;
            var normal=frontage.Normal;var tangent=new Cell(-normal.Y,normal.X);
            var catwalk=new HashSet<Cell>();var panels=new HashSet<Cell>();
            for(var d=1;d<=5;d++)for(var w=0;w<2;w++)catwalk.Add(frontage.Center+normal*d+tangent*w);
            for(var d=6;d<=9;d++)for(var w=-2;w<=3;w++)panels.Add(frontage.Center+normal*d+tangent*w);
            var footprint=catwalk.Concat(panels).ToHashSet();
            if(footprint.Any(p=>!outside.Contains(p)||!StationGenerator.Inside(c,p,1)||occupied.Contains(p)||Neighbors(p).Any(occupied.Contains)))continue;
            plan.SolarInstallations.Add(new SolarInstallation{Id=$"Solar{plan.SolarInstallations.Count+1}",Cells=Sorted(panels),Catwalk=Sorted(catwalk),ConnectionCells=frontage.Barrier,MaintenanceApproach=frontage.Approach});
            occupied.UnionWith(footprint);occupied.UnionWith(frontage.Barrier);usedFrontages.Add(frontage.Center);
        }
        if(plan.SolarInstallations.Count!=solarCount)
            throw new PlanningException("maintenance","solar-fit",$"Requested {solarCount} external solar installations, but only {plan.SolarInstallations.Count} fit with maintenance access and clear docking approaches. Enlarge exterior canvas/maintenance frontage or reduce count bounds.");
        foreach(var frontage in frontages)
        {
            if(plan.MaintenanceSpaceAccess.Count==accessCount)break;
            if(usedFrontages.Any(p=>p.Distance(frontage.Center)<5))continue;
            var exterior=frontage.Barrier.Where(p=>outside.Contains(p+frontage.Normal)).SelectMany(p=>new[]{p+frontage.Normal,p+frontage.Normal*2}).ToHashSet();
            if(exterior.Any(p=>!outside.Contains(p)||occupied.Contains(p))||frontage.Barrier.Any(occupied.Contains))continue;
            plan.MaintenanceSpaceAccess.Add(new MaintenanceSpaceAccess{Id=$"MaintenanceEVA{plan.MaintenanceSpaceAccess.Count+1}",MaintenanceApproach=frontage.Approach,BarrierCells=frontage.Barrier,ExteriorCells=Sorted(exterior),Barrier=plan.MaintenanceSpaceAccess.Count%2==0?"window":"wall"});
            occupied.UnionWith(exterior);occupied.UnionWith(frontage.Barrier);usedFrontages.Add(frontage.Center);
        }
        if(plan.MaintenanceSpaceAccess.Count!=accessCount)
            throw new PlanningException("maintenance","space-access-fit",$"Requested {accessCount} sealed exterior maintenance access areas, but only {plan.MaintenanceSpaceAccess.Count} fit. Add maintenance frontage on the outer hull or reduce count bounds.");
    }

    internal static HashSet<Cell> OuterSpace(StationGenerationConfig c,HashSet<Cell> envelope)
    {
        var result=new HashSet<Cell>();var queue=new Queue<Cell>();
        for(var x=0;x<c.Width;x++)foreach(var y in new[]{0,c.Height-1})if(result.Add(new(x,y)))queue.Enqueue(new(x,y));
        for(var y=0;y<c.Height;y++)foreach(var x in new[]{0,c.Width-1})if(result.Add(new(x,y)))queue.Enqueue(new(x,y));
        while(queue.TryDequeue(out var p))foreach(var q in Neighbors(p))if(StationGenerator.Inside(c,q)&&!envelope.Contains(q)&&result.Add(q))queue.Enqueue(q);
        return result;
    }

    public static void ApplyBoundaries(StationSpatialPlan plan,HashSet<Cell> walls,HashSet<Cell> maintenance)
    {
        foreach(var room in plan.DeepMaintenanceRooms)
        {
            maintenance.ExceptWith(room.Cells);maintenance.ExceptWith(room.Walls);maintenance.ExceptWith(room.PortCells);
            walls.UnionWith(room.Walls);
        }
        foreach(var room in plan.CommonMaintenanceRooms)
        {
            maintenance.ExceptWith(room.Cells);maintenance.ExceptWith(room.Walls);maintenance.ExceptWith(room.PublicDoorCells);maintenance.ExceptWith(room.MaintenanceDoorCells);
            walls.UnionWith(room.Walls);
        }
    }

    /// <summary>Read-only checks of authored masks. It never repairs or regenerates them.</summary>
    public static List<Diagnostic> Validate(StationProject project)
    {
        var errors=new List<Diagnostic>();var plan=project.SpatialPlan;if(plan==null)return errors;
        var s=project.Config.Spatial;var envelope=plan.Envelope.ToHashSet();var maintenance=plan.Maintenance.ToHashSet();var hull=plan.Hull.ToHashSet();var halls=plan.PublicCorridors.ToHashSet();
        var walls=plan.Walls.ToHashSet();var service=plan.ServiceReservations.ToHashSet();var outside=OuterSpace(project.Config,envelope);var docks=plan.ReservedExterior.ToHashSet();
        void Error(string code,string message)=>errors.Add(new("maintenance",code,message));
        errors.AddRange(ValidatePassageWidth(maintenance,project.Config.MaintenanceWidth,s.MaintenancePassageMaxWidth));
        errors.AddRange(ValidateCommonRooms(project));
        var ownership=plan.Regions.SelectMany(r=>r.Territory.Select(cell=>(cell,r.Id))).GroupBy(x=>x.cell).ToDictionary(g=>g.Key,g=>g.First().Id);
        var touching=new HashSet<string>(StringComparer.Ordinal);
        foreach(var (cell,id) in ownership)foreach(var q in Neighbors(cell))
            if(ownership.TryGetValue(q,out var other)&&other!=id&&!project.Topology!.Edges.Any(e=>e.Hard&&e.Relation=="adjacency"&&(e.From==id&&e.To==other||e.From==other&&e.To==id)))
            {touching.Add(id);touching.Add(other);}
        if(touching.Count>project.Config.Departments.Count/2)
            Error("department-separation", $"{touching.Count} departments share unrequested direct boundaries. Most departments need circulation or maintenance buffers; only Required adjacency permits intentional shared boundaries.");
        if(plan.DeepMaintenanceRooms.Count<s.DeepMaintenanceMin||plan.DeepMaintenanceRooms.Count>s.DeepMaintenanceMax)Error("deep-maintenance-count","Deep maintenance room count is outside its configured minimum/maximum.");
        if(plan.SolarInstallations.Count<s.SolarMin||plan.SolarInstallations.Count>s.SolarMax)Error("solar-count","Solar installation count is outside its configured minimum/maximum.");
        if(plan.MaintenanceSpaceAccess.Count<s.SpaceAccessMin||plan.MaintenanceSpaceAccess.Count>s.SpaceAccessMax)Error("space-access-count","Maintenance exterior access count is outside its configured minimum/maximum.");
        var occupiedRooms=new HashSet<Cell>();var distances=MaintenanceDistances(maintenance,halls,plan);
        var commonPublic=plan.CommonMaintenanceRooms.SelectMany(r=>r.Cells.Concat(r.PublicDoorCells)).ToHashSet();
        foreach(var room in plan.DeepMaintenanceRooms)
        {
            var floor=room.Cells.ToHashSet();var barrier=room.Walls.ToHashSet();var ports=room.PortCells.ToHashSet();var footprint=floor.Concat(barrier).Concat(ports).ToHashSet();
            if(!BoundaryWithinDepth(footprint,floor,hull,s.DepartmentBoundaryMaxDepth))Error("deep-maintenance-boundary",$"{room.Id}: room partitions must stay within the configured boundary depth of usable floor.");
            if(floor.Count<s.DeepMaintenanceRoomMinArea||floor.Count>s.DeepMaintenanceRoomMaxArea)Error("deep-maintenance-area",$"{room.Id}: usable floor area is outside its configured room limits.");
            if(floor.Count<4||StationValidator.Components(floor).Count!=1||floor.Overlaps(barrier)||floor.Overlaps(ports)||barrier.Overlaps(ports)||footprint.Any(p=>!service.Contains(p)||!envelope.Contains(p)||hull.Contains(p)||maintenance.Contains(p)||halls.Contains(p))||occupiedRooms.Overlaps(footprint))Error("deep-maintenance-footprint",$"{room.Id}: room floor, enclosing walls and maintenance door must be disjoint service-owned geometry.");
            if(!barrier.IsSubsetOf(walls)||floor.Any(p=>Neighbors(p).Any(q=>!floor.Contains(q)&&!walls.Contains(q)&&!hull.Contains(q)&&!ports.Contains(q))))Error("deep-maintenance-enclosure",$"{room.Id}: the maintenance room requires a complete planned wall enclosure.");
            if(ports.Count==0||StationValidator.Components(ports).Count!=1||!maintenance.Contains(room.MaintenanceApproach)||distances.GetValueOrDefault(room.MaintenanceApproach,-1)<2||ports.Any(p=>!Neighbors(p).Any(floor.Contains)||!Neighbors(p).Any(maintenance.Contains)||Neighbors(p).Any(halls.Contains)||Neighbors(p).Any(commonPublic.Contains)))Error("deep-maintenance-access",$"{room.Id}: its only planned door must require traversing maintenance, with no hall or common-room bypass.");
            occupiedRooms.UnionWith(footprint);
        }
        var occupiedExternal=new HashSet<Cell>();
        foreach(var solar in plan.SolarInstallations)
        {
            var cells=solar.Cells.ToHashSet();var path=solar.Catwalk.ToHashSet();var exterior=cells.Concat(path).ToHashSet();
            if(cells.Count==0||path.Count==0||cells.Overlaps(path)||StationValidator.Components(exterior).Count!=1||exterior.Any(p=>!outside.Contains(p)||docks.Contains(p))||occupiedExternal.Overlaps(exterior))Error("solar-footprint",$"{solar.Id}: panels and connected catwalk must be external and clear of docking approaches and other installations.");
            if(!maintenance.Contains(solar.MaintenanceApproach)||solar.ConnectionCells.Count==0||solar.ConnectionCells.Any(p=>!hull.Contains(p))||StationValidator.Components(solar.ConnectionCells.ToHashSet()).Count!=1||!solar.ConnectionCells.Any(p=>Neighbors(p).Any(path.Contains))||!solar.ConnectionCells.Any(p=>Neighbors(p).Contains(solar.MaintenanceApproach)))Error("solar-maintenance-access",$"{solar.Id}: its planned hull airlock must join an exterior catwalk to maintenance.");
            occupiedExternal.UnionWith(exterior);
        }
        foreach(var access in plan.MaintenanceSpaceAccess)
        {
            var exterior=access.ExteriorCells.ToHashSet();
            if(access.Barrier is not ("wall" or "window")||access.BarrierCells.Count==0||access.BarrierCells.Any(p=>!hull.Contains(p))||StationValidator.Components(access.BarrierCells.ToHashSet()).Count!=1||!maintenance.Contains(access.MaintenanceApproach)||!access.BarrierCells.Any(p=>Neighbors(p).Contains(access.MaintenanceApproach))||exterior.Count==0||StationValidator.Components(exterior).Count!=1||!access.BarrierCells.Any(p=>Neighbors(p).Any(exterior.Contains))||exterior.Any(p=>!outside.Contains(p)||docks.Contains(p)||occupiedExternal.Contains(p)))Error("maintenance-space-access",$"{access.Id}: sealed wall/window frontage must separate reachable maintenance from clear exterior space.");
            occupiedExternal.UnionWith(access.ExteriorCells);
        }
        return errors;
    }
}
