namespace Content.StationGeneration;

public static partial class MaintenancePlanner
{
    public static List<Diagnostic> ValidatePassageWidth(IEnumerable<Cell> cells,int minimum,int maximum)
    {
        var diagnostics=new List<Diagnostic>();
        if(minimum<2||maximum<minimum||maximum>8)
            return [new("maintenance","maintenance-width-setting","Maintenance needs a minimum clear width of at least two, and a maximum no smaller than that minimum.")];
        var floor=cells.ToHashSet();
        if(floor.Any(p=>!InSquare(p,floor,minimum)))
            diagnostics.Add(new("maintenance","maintenance-width",$"Every maintenance passage tile must belong to a {minimum}-by-{minimum} clear floor patch."));
        if(floor.Any(p=>Square(p,maximum+1).All(floor.Contains)))
            diagnostics.Add(new("maintenance","maintenance-width-max",$"Maintenance contains an open {maximum+1}-by-{maximum+1} pocket. Passages may be at most {maximum} wide; give larger pockets to departments or enclosed rooms."));
        return diagnostics;
    }

    private static IEnumerable<Cell> Square(Cell origin,int side) => new Rect(origin.X,origin.Y,side,side).Cells();

    private static bool InSquare(Cell cell,HashSet<Cell> floor,int side)
    {
        for(var y=cell.Y-side+1;y<=cell.Y;y++)
        for(var x=cell.X-side+1;x<=cell.X;x++)
            if(Square(new(x,y),side).All(floor.Contains))return true;
        return false;
    }

    /// <summary>
    /// Reclaim oversized service pockets through connected department frontiers.
    /// Required approach patches and the remaining service network are retained;
    /// allocations never become anonymous structural reserve.
    /// </summary>
    private static void NarrowAndReclaim(StationProject project,int attempt)
    {
        var plan=project.SpatialPlan!;var c=project.Config;var minimum=c.MaintenanceWidth;var maximum=c.Spatial.MaintenancePassageMaxWidth;
        var floor=plan.Maintenance.ToHashSet();var halls=plan.PublicCorridors.ToHashSet();var hull=plan.Hull.ToHashSet();
        var serviceOwnership=plan.ServiceReservations.ToHashSet();
        var territories=plan.Regions.ToDictionary(r=>r.Id,r=>r.Territory.ToHashSet(),StringComparer.Ordinal);
        var originalTerritories=territories.ToDictionary(kv=>kv.Key,kv=>new HashSet<Cell>(kv.Value),StringComparer.Ordinal);
        var definitions=c.Departments.ToDictionary(d=>d.Id,StringComparer.Ordinal);
        var departmentFloors=territories.ToDictionary(kv=>kv.Key,kv=>kv.Value.Where(p=>!hull.Contains(p)&&Neighbors(p).All(kv.Value.Contains)).ToHashSet(),StringComparer.Ordinal);
        if(territories.Any(kv=>!BoundaryWithinDepth(kv.Value,departmentFloors[kv.Key],hull,c.Spatial.DepartmentBoundaryMaxDepth)))
            throw new PlanningException("growth","department-boundary-depth","An existing department contains boundary ownership beyond its configured depth from usable floor. Regrow its territory before allocating service pockets.");
        var owners=territories.SelectMany(kv=>kv.Value.Select(p=>(p,kv.Key))).ToDictionary(x=>x.p,x=>x.Key);
        var protectedFloor=new HashSet<Cell>();
        var terminals=new List<HashSet<Cell>>();
        // Department doors are derived again after reclamation. Preserve a
        // legal frontage, rather than freezing an obsolete automatic doorway.
        terminals.AddRange(plan.DeepMaintenanceRooms.Select(r=>ApproachSpan(r.PortCells,floor)));
        terminals.AddRange(plan.CommonMaintenanceRooms.Select(r=>ApproachSpan(r.MaintenanceDoorCells,floor)));
        terminals.AddRange(plan.SolarInstallations.Select(r=>r.ConnectionCells.SelectMany(Neighbors).Where(floor.Contains).ToHashSet()));
        terminals.AddRange(plan.MaintenanceSpaceAccess.Select(r=>r.BarrierCells.SelectMany(Neighbors).Where(floor.Contains).ToHashSet()));
        foreach(var terminal in terminals.Where(t=>t.Count>0))
            ProtectTerminal(terminal);
        void ProtectTerminal(HashSet<Cell> terminal)
        {
            var patches=terminal.SelectMany(p=>Enumerable.Range(p.X-minimum+1,minimum).SelectMany(x=>Enumerable.Range(p.Y-minimum+1,minimum).Select(y=>new Cell(x,y))))
                .Distinct().Select(p=>Square(p,minimum).ToHashSet()).Where(p=>terminal.IsSubsetOf(p)&&p.IsSubsetOf(floor))
                .OrderByDescending(p=>p.Count(protectedFloor.Contains)).ThenBy(p=>p.Min(x=>x.Y)).ThenBy(p=>p.Min(x=>x.X)).ToList();
            if(patches.Count==0)throw new PlanningException("maintenance","maintenance-approach-width","A maintenance, deep-room or exterior doorway cannot retain the configured clear passage width.");
            protectedFloor.UnionWith(patches[0]);
        }
        var reclaimed=new HashSet<Cell>();var iterations=0;var expandedSearch=false;var rollbacks=0;
        var blockedClaims=new HashSet<(Cell,string)>();
        while(true)
        {
            var wide=WideCells(floor,maximum+1);
            if(wide.Count==0)break;
            if(++iterations>plan.Envelope.Count)
                throw new PlanningException("maintenance","maintenance-reclaim-budget","Maintenance pocket allocation exceeded its bounded tile budget.");
            if(c.Spatial.MaintenancePocketPreference=="rooms"&&ExpandDeepRoom(plan,floor,wide,protectedFloor,halls,minimum,c.Spatial.DeepMaintenanceRoomMaxArea))continue;
            var options=new List<(string Id,HashSet<Cell> Patch,int Cost)>();
            var seenOptions=new HashSet<(string,Cell,int,int)>();
            foreach(var p in Sorted(wide.Where(p=>!protectedFloor.Contains(p))))
            foreach(var id in Neighbors(p).Where(owners.ContainsKey).Select(q=>owners[q]).Distinct(StringComparer.Ordinal))
            foreach(var (patchWidth,patchHeight) in expandedSearch
                ?Enumerable.Range(1,Math.Max(plan.CellSize+3,maximum+3)).SelectMany(w=>Enumerable.Range(1,Math.Max(plan.CellSize+3,maximum+3)).Select(h=>(w,h))).ToArray()
                :new[]{(2,2),(2,3),(3,2),(2,4),(4,2),(3,3),(1,1),(1,2),(2,1),(1,3),(3,1)})
            for(var oy=-patchHeight+1;oy<=0;oy++)for(var ox=-patchWidth+1;ox<=0;ox++)
            {
                var origin=new Cell(p.X+ox,p.Y+oy);if(!seenOptions.Add((id,origin,patchWidth,patchHeight)))continue;
                var patch=new Rect(origin.X,origin.Y,patchWidth,patchHeight).Cells().ToHashSet();
                if(patch.Any(q=>!floor.Contains(q)&&(!owners.TryGetValue(q,out var owner)||owner!=id)))continue;
                patch.IntersectWith(floor);
                if(patch.Count==0||patch.Overlaps(protectedFloor))continue;
                if(patch.SelectMany(Neighbors).Any(q=>owners.TryGetValue(q,out var owner)&&owner!=id))continue;
                var cost=territories[id].Count*10/Math.Max(1,definitions[id].MaxArea)-patch.Count*4+patch.Count(q=>blockedClaims.Contains((q,id)))*10;
                options.Add((id,patch,cost));
            }
            var progressed=false;var widthRejected=0;var reachRejected=0;var departmentRejected=0;
            foreach(var option in options.OrderBy(o=>o.Cost).ThenBy(o=>attempt==0?0:unchecked((uint)(o.Patch.Min(p=>p.X)*73856093^o.Patch.Min(p=>p.Y)*19349663^attempt*83492791)))
                         .ThenBy(o=>o.Id,StringComparer.Ordinal).ThenBy(o=>o.Patch.Min(p=>p.Y)).ThenBy(o=>o.Patch.Min(p=>p.X)))
            {
                if(!option.Patch.IsSubsetOf(floor)||option.Patch.SelectMany(Neighbors).Any(q=>owners.TryGetValue(q,out var owner)&&owner!=option.Id)||!option.Patch.Any(p=>InSquare(p,floor,maximum+1)))continue;
                var remaining=new HashSet<Cell>(floor);remaining.ExceptWith(option.Patch);
                if(!RetainsWidth(remaining,option.Patch,minimum)){widthRejected++;continue;}
                if(!PublicReachable(remaining,halls,plan)){reachRejected++;continue;}
                var future=new HashSet<Cell>(territories[option.Id]);future.UnionWith(option.Patch);
                var futureFloor=future.Where(p=>!hull.Contains(p)&&Neighbors(p).All(future.Contains)).ToHashSet();
                var newFloor=futureFloor.Except(departmentFloors[option.Id]).ToHashSet();
                // Expansion cannot remove existing floor or increase an old
                // wall's distance from it; only newly allocated cells need the
                // boundary test. New floor components must join known floor.
                if(futureFloor.Count>definitions[option.Id].MaxArea||newFloor.Any(p=>!InSquare(p,futureFloor,2))||StationValidator.Components(newFloor).Any(comp=>!comp.Any(p=>Neighbors(p).Any(departmentFloors[option.Id].Contains)))||!BoundaryWithinDepth(option.Patch,futureFloor,hull,c.Spatial.DepartmentBoundaryMaxDepth)){departmentRejected++;continue;}
                if(plan.Seeds.Single(s=>s.Id==option.Id).AllowMaintenanceAccess&&!HasMaintenanceFrontage(future,futureFloor,remaining,hull,minimum)){departmentRejected++;continue;}
                floor=remaining;territories[option.Id]=future;reclaimed.UnionWith(option.Patch);
                departmentFloors[option.Id]=futureFloor;
                foreach(var p in option.Patch)owners[p]=option.Id;
                progressed=true;
            }
            if(!progressed&&!expandedSearch){expandedSearch=true;continue;}
            if(!progressed&&ExpandDeepRoom(plan,floor,wide,protectedFloor,halls,minimum,c.Spatial.DeepMaintenanceRoomMaxArea))progressed=true;
            if(!progressed&&plan.CommonMaintenanceRooms.Count<c.Spatial.CommonMaintenanceMax&&AddCommonRoom(wide))progressed=true;
            if(!progressed&&rollbacks<c.CandidateBudget&&Rollback(wide))
            {rollbacks++;expandedSearch=false;continue;}
            if(progressed)expandedSearch=false;
            if(!progressed)
            {
                Publish();StationSpatialPlanner.RebuildBoundaries(project,false);
                throw new PlanningException("maintenance","maintenance-pocket-fit",$"An oversized maintenance pocket cannot be reclaimed while retaining clear {minimum}-{maximum} tile passages, department limits and every planned access ({wide.Count} wide tiles, {wide.Count(protectedFloor.Contains)} protected after {iterations} allocations; {options.Count} frontier options: {widthRejected} narrow, {reachRejected} disconnected, {departmentRejected} department-limited). Increase nearby department area limits, revise placement or permit larger bounded maintenance rooms.");
            }
        }
        Publish();

        void Publish()
        {
        foreach(var region in plan.Regions)region.Territory=Sorted(territories[region.Id]);
        plan.ServiceReservations=Sorted(serviceOwnership.Where(p=>!reclaimed.Contains(p)));
        plan.Maintenance=Sorted(floor);
        }

        bool AddCommonRoom(HashSet<Cell> preferred)
        {
            Publish();StationSpatialPlanner.RebuildBoundaries(project,false);
            var before=plan.CommonMaintenanceRooms.Count;
            try{PlaceCommonRooms(project,plan.CommonMaintenanceRooms.Count+1,new SeedRandom(c.Seed,1709+attempt*31+plan.CommonMaintenanceRooms.Count),preferred);}
            catch(PlanningException e) when(e.Diagnostic.Code=="common-maintenance-fit"){return false;}
            floor=plan.Maintenance.ToHashSet();
            foreach(var room in plan.CommonMaintenanceRooms.Skip(before))ProtectTerminal(ApproachSpan(room.MaintenanceDoorCells,floor));
            return true;
        }

        bool Rollback(HashSet<Cell> wide)
        {
            var nearby=reclaimed.Where(p=>!blockedClaims.Contains((p,owners[p]))).OrderBy(p=>wideDistance(p)).ThenBy(p=>p.Y).ThenBy(p=>p.X).Take(1).ToList();
            if(nearby.Count==0)return false;
            var nearest=nearby[0];var id=owners[nearest];blockedClaims.Add((nearest,id));
            // Reconsider the whole local branch, not a wall tip that could leave
            // an isolated usable floor island in the reclaimed department.
            var branch=StationValidator.Components(territories[id].Except(originalTerritories[id]).ToHashSet()).First(comp=>comp.Contains(nearest));
            var restore=new HashSet<Cell>(branch);
            var future=new HashSet<Cell>(territories[id]);future.ExceptWith(restore);
            var futureFloor=future.Where(p=>!hull.Contains(p)&&Neighbors(p).All(future.Contains)).ToHashSet();
            var restoredFloor=new HashSet<Cell>(floor);restoredFloor.UnionWith(restore);
            if(StationValidator.Components(futureFloor).Count!=1||futureFloor.Any(p=>!InSquare(p,futureFloor,2))||!BoundaryWithinDepth(future,futureFloor,hull,c.Spatial.DepartmentBoundaryMaxDepth)||restore.Any(p=>!InSquare(p,restoredFloor,minimum)))
            {restore=new HashSet<Cell>(reclaimed);restoredFloor=new HashSet<Cell>(floor);restoredFloor.UnionWith(restore);}
            if(restore.Count==0||restore.Any(p=>!InSquare(p,restoredFloor,minimum)))return false;
            foreach(var p in restore){territories[owners[p]].Remove(p);owners.Remove(p);}
            foreach(var (department,territory) in territories)departmentFloors[department]=territory.Where(p=>!hull.Contains(p)&&Neighbors(p).All(territory.Contains)).ToHashSet();
            floor=restoredFloor;reclaimed.ExceptWith(restore);return true;

            int wideDistance(Cell p)=>wide.Select(q=>p.Distance(q)).DefaultIfEmpty(int.MaxValue).Min();
        }
    }

    private static HashSet<Cell> WideCells(HashSet<Cell> floor,int side) => floor.Where(p=>Square(p,side).All(floor.Contains)).SelectMany(p=>Square(p,side)).ToHashSet();

    private static HashSet<Cell> ApproachSpan(List<Cell> doorway,HashSet<Cell> floor)
    {
        foreach(var normal in Cell.Directions)
        {
            var span=doorway.Select(p=>p+normal).ToHashSet();
            if(span.IsSubsetOf(floor))return span;
        }
        return [];
    }

    private static bool HasMaintenanceFrontage(HashSet<Cell> territory,HashSet<Cell> floor,HashSet<Cell> network,HashSet<Cell> hull,int width)
    {
        foreach(var p in floor)
        foreach(var normal in Cell.Directions)
        {
            var wall=p+normal;if(!territory.Contains(wall)||hull.Contains(wall)||!network.Contains(wall+normal))continue;
            var tangent=new Cell(-normal.Y,normal.X);
            if(Enumerable.Range(-(width-1)/2,width).Select(i=>wall+tangent*i).All(q=>territory.Contains(q)&&!hull.Contains(q)&&floor.Contains(q+normal*-1)&&network.Contains(q+normal)))return true;
        }
        return false;
    }

    private static bool BoundaryWithinDepth(HashSet<Cell> territory,HashSet<Cell> floor,HashSet<Cell> hull,int depth) =>
        territory.Where(p=>!floor.Contains(p)&&!hull.Contains(p)).All(p=>new Rect(p.X-depth,p.Y-depth,depth*2+1,depth*2+1).Cells().Any(floor.Contains));

    private static bool ExpandDeepRoom(StationSpatialPlan plan,HashSet<Cell> maintenance,HashSet<Cell> wide,HashSet<Cell> protectedFloor,HashSet<Cell> halls,int minimum,int maximumArea)
    {
        foreach(var room in plan.DeepMaintenanceRooms)
        {
            var old=room.Cells.Concat(room.Walls).Concat(room.PortCells).ToHashSet();
            var oldFloor=room.Cells.ToHashSet();
            var otherFloors=plan.DeepMaintenanceRooms.Where(r=>r!=room).SelectMany(r=>r.Cells).Concat(plan.CommonMaintenanceRooms.SelectMany(r=>r.Cells)).ToHashSet();
            var borrowedWalls=room.Walls.Where(p=>Neighbors(p).Any(otherFloors.Contains)).ToHashSet();
            var shared=plan.Hull.Concat(plan.Walls).Concat(plan.DeepMaintenanceRooms.Where(r=>r!=room).SelectMany(r=>r.Walls)).Concat(plan.CommonMaintenanceRooms.SelectMany(r=>r.Walls)).ToHashSet();
            var minX=old.Min(p=>p.X);var minY=old.Min(p=>p.Y);var maxX=old.Max(p=>p.X);var maxY=old.Max(p=>p.Y);
            foreach(var direction in Cell.Directions)
            {
                var x=minX+(direction.X<0?-1:0);var y=minY+(direction.Y<0?-1:0);
                var width=maxX-minX+1+Math.Abs(direction.X);var height=maxY-minY+1+Math.Abs(direction.Y);
                var footprint=new Rect(x,y,width,height).Cells().ToHashSet();var added=footprint.Except(old).ToHashSet();
                if(!added.Overlaps(wide)||!added.IsSubsetOf(maintenance)||added.Overlaps(protectedFloor))continue;
                var inner=new Rect(x+1,y+1,width-2,height-2).Cells().ToHashSet();
                if(inner.Count>maximumArea||!oldFloor.IsSubsetOf(inner)||inner.Overlaps(borrowedWalls)||room.PortCells.Any(inner.Contains))continue;
                var walls=footprint.Except(inner).Except(room.PortCells).ToHashSet();
                if(room.PortCells.Any(p=>!Neighbors(p).Any(inner.Contains)))continue;
                var remaining=new HashSet<Cell>(maintenance);remaining.ExceptWith(added);
                if(!RetainsWidth(remaining,added,minimum)||!PublicReachable(remaining,halls,plan))continue;
                room.Cells=Sorted(inner);room.Walls=Sorted(walls);maintenance.ExceptWith(added);return true;
            }
            // A bounded room may follow an irregular service pocket. Retain a
            // real connected interior and full enclosure rather than requiring
            // every enlarged room to remain a rectangle.
            foreach(var p in Sorted(wide.Where(p=>Neighbors(p).Any(old.Contains))))
            foreach(var (w,h) in new[]{(2,2),(2,3),(3,2),(3,3),(4,3),(3,4),(4,4),(5,4),(4,5)})
            for(var oy=-h+1;oy<=0;oy++)for(var ox=-w+1;ox<=0;ox++)
            {
                var patch=new Rect(p.X+ox,p.Y+oy,w,h).Cells().ToHashSet();
                if(patch.Any(q=>!old.Contains(q)&&!maintenance.Contains(q)))continue;
                var added=patch.Except(old).ToHashSet();if(added.Count==0||added.Overlaps(protectedFloor))continue;
                var footprint=new HashSet<Cell>(old);footprint.UnionWith(added);
                var inner=footprint.Where(q=>Neighbors(q).All(n=>footprint.Contains(n)||shared.Contains(n))).ToHashSet();
                if(inner.Count>maximumArea||!oldFloor.IsSubsetOf(inner)||inner.Overlaps(borrowedWalls)||StationValidator.Components(inner).Count!=1||inner.Any(q=>!InSquare(q,inner,2))||!BoundaryWithinDepth(footprint,inner,[],1)||room.PortCells.Any(inner.Contains)||room.PortCells.Any(q=>!Neighbors(q).Any(inner.Contains)))continue;
                var remaining=new HashSet<Cell>(maintenance);remaining.ExceptWith(added);
                if(!RetainsWidth(remaining,added,minimum)||!PublicReachable(remaining,halls,plan))continue;
                room.Cells=Sorted(inner);room.Walls=Sorted(footprint.Except(inner).Except(room.PortCells));maintenance.ExceptWith(added);return true;
            }
        }
        return false;
    }

    private static bool RetainsWidth(HashSet<Cell> remaining,HashSet<Cell> removed,int minimum)
    {
        var affected=removed.SelectMany(p=>new Rect(p.X-minimum,p.Y-minimum,minimum*2+1,minimum*2+1).Cells()).Where(remaining.Contains).Distinct();
        return affected.All(p=>InSquare(p,remaining,minimum));
    }

    private static bool PublicReachable(HashSet<Cell> floor,HashSet<Cell> halls,StationSpatialPlan? plan=null,CommonMaintenanceRoom? additional=null)
    {
        if(floor.Count==0)return false;
        var walk=new HashSet<Cell>(floor);
        foreach(var room in (plan?.CommonMaintenanceRooms??[]).Concat(additional==null?[]:[additional]))
        {walk.UnionWith(room.Cells);walk.UnionWith(room.PublicDoorCells);walk.UnionWith(room.MaintenanceDoorCells);}
        var reached=new HashSet<Cell>();var queue=new Queue<Cell>();
        foreach(var p in walk.Where(p=>Neighbors(p).Any(halls.Contains)))if(reached.Add(p))queue.Enqueue(p);
        while(queue.TryDequeue(out var p))foreach(var q in Neighbors(p))if(walk.Contains(q)&&reached.Add(q))queue.Enqueue(q);
        return floor.IsSubsetOf(reached);
    }
}
