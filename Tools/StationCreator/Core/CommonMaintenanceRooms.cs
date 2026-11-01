namespace Content.StationGeneration;

/// <summary>A service room with a real public-hall doorway as well as a maintenance doorway.</summary>
public sealed class CommonMaintenanceRoom
{
    public string Id { get; set; } = "";
    public List<Cell> Cells { get; set; } = [];
    public List<Cell> Walls { get; set; } = [];
    public List<Cell> PublicDoorCells { get; set; } = [];
    public List<Cell> MaintenanceDoorCells { get; set; } = [];
    public Cell PublicApproach { get; set; }
    public Cell MaintenanceApproach { get; set; }
}

public static partial class MaintenancePlanner
{
    private static void PlaceCommonRooms(StationProject project,int count,SeedRandom random,HashSet<Cell>? preferred=null)
    {
        var plan=project.SpatialPlan!;var c=project.Config;var s=c.Spatial;
        if(count<=plan.CommonMaintenanceRooms.Count)return;
        var target=Math.Clamp(s.CommonMaintenanceRoomTargetArea,s.CommonMaintenanceRoomMinArea,s.CommonMaintenanceRoomMaxArea);
        var shapes=new[]{target,s.CommonMaintenanceRoomMinArea}.Distinct().SelectMany(area=>Enumerable.Range(2,Math.Max(2,area))
            .Select(w=>(Width:w,Height:Math.Max(2,(area+w-1)/w))).Where(d=>(long)d.Width*d.Height<=s.CommonMaintenanceRoomMaxArea)
            .OrderBy(d=>Math.Abs(d.Width-d.Height)).ThenBy(d=>d.Width*d.Height).Take(1)).Distinct().ToList();
        var maintenance=plan.Maintenance.ToHashSet();var halls=plan.PublicCorridors.ToHashSet();
        var structures=plan.Walls.Concat(plan.Hull).Concat(plan.DeepMaintenanceRooms.SelectMany(r=>r.Walls)).Concat(plan.CommonMaintenanceRooms.SelectMany(r=>r.Walls)).ToHashSet();
        var deepKeepout=plan.DeepMaintenanceRooms.SelectMany(r=>r.PortCells).SelectMany(p=>new Rect(p.X-c.MaintenanceWidth,p.Y-c.MaintenanceWidth,c.MaintenanceWidth*2+1,c.MaintenanceWidth*2+1).Cells()).ToHashSet();
        foreach(var shape in shapes)
        {
            var origins=new HashSet<Cell>();
            foreach(var p in maintenance.Where(p=>Neighbors(p).Any(halls.Contains)))
            foreach(var normal in Cell.Directions.Where(d=>halls.Contains(p+d)))
            {
                if(normal.X!=0)for(var offset=1;offset<=shape.Height;offset++)origins.Add(new(normal.X>0?p.X-shape.Width-1:p.X,p.Y-offset));
                else for(var offset=1;offset<=shape.Width;offset++)origins.Add(new(p.X-offset,normal.Y>0?p.Y-shape.Height-1:p.Y));
            }
            var candidates=Sorted(origins);random.Shuffle(candidates);
            foreach(var origin in candidates)
            {
                if(plan.CommonMaintenanceRooms.Count>=count)break;
                var body=new Rect(origin.X+1,origin.Y+1,shape.Width,shape.Height).Cells().ToHashSet();
                if(!body.IsSubsetOf(maintenance))continue;
                var ring=new Rect(origin.X,origin.Y,shape.Width+2,shape.Height+2).Cells().Except(body).ToHashSet();
                if(ring.Any(p=>!maintenance.Contains(p)&&!structures.Contains(p)))continue;
                var walls=ring.Where(maintenance.Contains).ToHashSet();var footprint=body.Concat(walls).ToHashSet();
                if(footprint.Overlaps(deepKeepout)||preferred!=null&&!footprint.Overlaps(preferred))continue;
                var remaining=new HashSet<Cell>(maintenance);remaining.ExceptWith(footprint);
                if(!RetainsWidth(remaining,footprint,c.MaintenanceWidth))continue;
                if(plan.Regions.Where(r=>plan.Seeds.Single(seed=>seed.Id==r.Id).AllowMaintenanceAccess).Any(r=>!HasMaintenanceFrontage(r.Territory.ToHashSet(),r.Cells.ToHashSet(),remaining,plan.Hull.ToHashSet(),c.MaintenanceWidth)))continue;
                var publicDoors=Doorways(body,walls,halls,c.CorridorWidth);
                var serviceDoors=Doorways(body,walls,remaining,c.MaintenanceWidth);
                CommonMaintenanceRoom? selected=null;
                foreach(var publicDoor in publicDoors)
                foreach(var serviceDoor in serviceDoors)
                {
                    if(publicDoor.Cells.Intersect(serviceDoor.Cells).Any())continue;
                    if(Enumerable.Range(1,c.MaintenanceWidth).SelectMany(depth=>serviceDoor.Cells.Select(p=>p+serviceDoor.Normal*depth)).Any(p=>!remaining.Contains(p)))continue;
                    var candidate=new CommonMaintenanceRoom{Id=$"CommonMaintenance{plan.CommonMaintenanceRooms.Count+1}",Cells=Sorted(body),
                        Walls=Sorted(walls.Except(publicDoor.Cells).Except(serviceDoor.Cells)),PublicDoorCells=publicDoor.Cells,MaintenanceDoorCells=serviceDoor.Cells,
                        PublicApproach=publicDoor.Approach,MaintenanceApproach=serviceDoor.Approach};
                    if(!PublicReachable(remaining,halls,plan,candidate))continue;
                    var distances=MaintenanceDistances(remaining,halls,plan,candidate);
                    if(plan.DeepMaintenanceRooms.Any(r=>distances.GetValueOrDefault(r.MaintenanceApproach,-1)<2))continue;
                    selected=candidate;break;
                }
                if(selected==null)continue;
                plan.CommonMaintenanceRooms.Add(selected);maintenance=remaining;structures.UnionWith(selected.Walls);
            }
        }
        if(plan.CommonMaintenanceRooms.Count<count)
            throw new PlanningException("maintenance","common-maintenance-fit",$"Cannot fit {count} enclosed maintenance rooms with both public and service doorways inside their configured area limits.");
        plan.Maintenance=Sorted(maintenance);
    }

    private sealed record RoomDoorway(List<Cell> Cells,Cell Approach,Cell Normal);
    private static List<RoomDoorway> Doorways(HashSet<Cell> room,HashSet<Cell> walls,HashSet<Cell> network,int width)
    {
        var result=new List<RoomDoorway>();
        foreach(var center in Sorted(walls))foreach(var normal in Cell.Directions)
        {
            var tangent=new Cell(-normal.Y,normal.X);var cells=Enumerable.Range(-(width-1)/2,width).Select(i=>center+tangent*i).ToList();
            if(cells.All(p=>walls.Contains(p)&&room.Contains(p+normal*-1)&&network.Contains(p+normal)))result.Add(new(cells,center+normal,normal));
        }
        return result;
    }

    public static List<Diagnostic> ValidateCommonRooms(StationProject project)
    {
        var errors=new List<Diagnostic>();var plan=project.SpatialPlan;if(plan==null)return errors;
        if(plan.CommonMaintenanceRooms==null||plan.CommonMaintenanceRooms.Any(r=>r==null||r.Cells==null||r.Walls==null||r.PublicDoorCells==null||r.MaintenanceDoorCells==null))
            return [new("maintenance","common-maintenance-data","Hall-accessible maintenance rooms require floor, wall and both doorway lists.")];
        var c=project.Config;var s=c.Spatial;var maintenance=plan.Maintenance.ToHashSet();var halls=plan.PublicCorridors.ToHashSet();
        var walls=plan.Walls.ToHashSet();var hull=plan.Hull.ToHashSet();var service=plan.ServiceReservations.ToHashSet();var occupied=new HashSet<Cell>();
        void Error(string code,string message)=>errors.Add(new("maintenance",code,message));
        if(plan.CommonMaintenanceRooms.Count<s.CommonMaintenanceMin||plan.CommonMaintenanceRooms.Count>s.CommonMaintenanceMax)Error("common-maintenance-count","Hall-accessible maintenance room count is outside its configured bounds.");
        if(plan.CommonMaintenanceRooms.Select(r=>r.Id).Distinct(StringComparer.Ordinal).Count()!=plan.CommonMaintenanceRooms.Count)Error("common-maintenance-ids","Hall-accessible maintenance rooms need unique identifiers.");
        foreach(var room in plan.CommonMaintenanceRooms)
        {
            var floor=room.Cells.ToHashSet();var partitions=room.Walls.ToHashSet();var publicDoor=room.PublicDoorCells.ToHashSet();var serviceDoor=room.MaintenanceDoorCells.ToHashSet();
            var footprint=floor.Concat(partitions).Concat(publicDoor).Concat(serviceDoor).ToHashSet();
            if(floor.Count<s.CommonMaintenanceRoomMinArea||floor.Count>s.CommonMaintenanceRoomMaxArea||StationValidator.Components(floor).Count!=1||floor.Any(p=>!InSquare(p,floor,2)))Error("common-maintenance-area",$"{room.Id}: room floor must be usable, connected and within the configured area bounds.");
            if(footprint.Count!=room.Cells.Count+room.Walls.Count+room.PublicDoorCells.Count+room.MaintenanceDoorCells.Count||!footprint.IsSubsetOf(service)||footprint.Overlaps(occupied)||footprint.Overlaps(maintenance)||footprint.Overlaps(halls)||footprint.Overlaps(hull)||floor.Overlaps(walls)||publicDoor.Overlaps(walls)||serviceDoor.Overlaps(walls)||!partitions.IsSubsetOf(walls))Error("common-maintenance-footprint",$"{room.Id}: room floor, new walls and doorways require disjoint service ownership.");
            if(floor.Any(p=>Neighbors(p).Any(q=>!floor.Contains(q)&&!walls.Contains(q)&&!hull.Contains(q)&&!publicDoor.Contains(q)&&!serviceDoor.Contains(q))))Error("common-maintenance-enclosure",$"{room.Id}: the room requires complete physical enclosure except its declared doorways.");
            if(!BoundaryWithinDepth(footprint,floor,hull,s.DepartmentBoundaryMaxDepth))Error("common-maintenance-boundary",$"{room.Id}: room partitions must stay within the configured boundary depth of usable floor.");
            if(!ValidDoor(publicDoor,floor,halls,c.CorridorWidth,room.PublicApproach)||!ValidDoor(serviceDoor,floor,maintenance,c.MaintenanceWidth,room.MaintenanceApproach))Error("common-maintenance-access",$"{room.Id}: a full-width public doorway and a separate maintenance doorway must physically join its floor to both networks.");
            occupied.UnionWith(footprint);
        }
        return errors;
    }

    private static bool ValidDoor(HashSet<Cell> door,HashSet<Cell> room,HashSet<Cell> network,int width,Cell approach)
    {
        if(door.Count!=width||!network.Contains(approach)||!door.Any(p=>Neighbors(p).Contains(approach)))return false;
        foreach(var normal in Cell.Directions)
        {
            var tangent=new Cell(-normal.Y,normal.X);var start=door.OrderBy(p=>p.X*tangent.X+p.Y*tangent.Y).First();
            if(Enumerable.Range(0,width).All(i=>door.Contains(start+tangent*i))&&door.All(p=>room.Contains(p+normal*-1)&&network.Contains(p+normal)))return true;
        }
        return false;
    }
}
