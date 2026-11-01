"""Real, bounded test-harness corridors joining exterior landing patches.

This does not assign station maintenance topology. It connects the explicitly
named harness floor without changing any owned department floor or doorway.
"""
from collections import deque
from functools import lru_cache
from pathlib import Path

import yaml

from .geometry import bbox, components, flood_exterior, neighbors, rect, square_anchors
from .packing import rebuild_walls
from .verify import Verifier


@lru_cache(maxsize=1)
def network_policy():
    path=Path(__file__).resolve().parents[1]/'data/boundary-network.yaml'
    value=yaml.safe_load(path.read_text(encoding='utf-8'))
    if value.get('format')!='ss14-boundary-network-policy' or value.get('version')!=1:
        raise ValueError('Unsupported boundary network policy')
    for key in ('canvasPadding','maximumCanvasCells','maximumSearchStates','foreignStructureClearance',
                'reservationClearance','interfaceApproachDepth'):
        if type(value.get(key)) is not int or value[key]<1:raise ValueError(f'{key} must be a positive integer')
    return value


def _halo(cells,depth=1):
    return {(x+dx,y+dy) for x,y in cells for dx in range(-depth,depth+1) for dy in range(-depth,depth+1)}


def _exterior_witnesses(checker,canvas):
    """Keep actual vacuum paths from exposed hull ports to the routing boundary.

    A finite clear patch in front of an airlock can still become an enclosed
    courtyard. A witness extends through the entire construction canvas; its
    halo keeps newly built corridor walls from sealing it. Prefer the authored
    normal, with a real exterior path as a fallback for an already bent lane.
    """
    flooded=flood_exterior(checker.obstacles,bbox(canvas))
    if flooded is None:
        return None, []
    exterior=flooded & canvas
    x0,y0,x1,y1=bbox(canvas)
    edge={p for p in exterior if p[0] in (x0,x1-1) or p[1] in (y0,y1-1)}
    witnesses=[]
    previous=None
    for interface in checker.state.interfaces:
        if not str(interface.get('b','')).startswith('@domain/') or not checker.interface_valid(interface):
            continue
        nx,ny=interface['normal']
        starts={(x+nx,y+ny) for x,y in interface['cells']}
        if not starts or not starts<=exterior:
            continue
        paths=set()
        for start in sorted(starts):
            ray=set();point=start
            while point in canvas:
                ray.add(point);point=(point[0]+nx,point[1]+ny)
            if ray<=exterior:
                paths|=ray
                continue
            if previous is None:
                # One reverse flood serves every bent witness on this canvas.
                queue=deque(sorted(edge));previous={p:None for p in queue}
                while queue:
                    parent=queue.popleft()
                    for point in neighbors(parent):
                        if point in exterior and point not in previous:
                            previous[point]=parent;queue.append(point)
            if start not in previous:
                return None, []
            point=start
            while point is not None:
                paths.add(point);point=previous[point]
        witnesses.append({'id':'vacuum-witness:'+interface['id'],'owner':interface['a'],
                          'kind':'exterior','cells':paths,'interface':interface['id']})
    return set().union(*(w['cells'] for w in witnesses)) if witnesses else set(),witnesses


def join_boundary_patches(state,target,width=2,*,maximum_cells=None,maximum_work=None):
    """Join a harness via swept width-by-width routes, or leave geometry intact.

    Reports explicit bounded-search failures in metadata. Necessary geometry
    cannot be waived by these diagnostics or by a single-cell flood fill.
    """
    if type(width) is not int or width<1:raise ValueError('Boundary route width must be a positive integer')
    policy=network_policy()
    maximum_cells=policy['maximumCanvasCells'] if maximum_cells is None else maximum_cells
    maximum_work=policy['maximumSearchStates'] if maximum_work is None else maximum_work
    if type(maximum_cells) is not int or maximum_cells<0 or type(maximum_work) is not int or maximum_work<0:
        raise ValueError('Boundary work/canvas limits must be nonnegative integers')
    original=set(state.floors.get(target,set()))
    report={'target':target,'width':width,'status':'failed','componentsBefore':len(components(original)),
            'componentsAfter':len(components(original)),'addedTiles':0,'searchStates':0,'paths':[]}
    def finish(status,reason):
        report.update(status=status,reason=reason)
        state.metadata.setdefault('boundary_networks',{})[target]=report
        state.trace.append({'event':'boundary_network',**{k:v for k,v in report.items() if k!='paths'}})
        return report
    if not state.program.spaces.get(target,{}).get('harness'):
        return finish('failed','Only an explicit external harness may receive boundary corridor floor.')
    if not original:return finish('failed','The target harness has no realized landing floor.')
    initial_anchors=square_anchors(original,width)
    groups=components(initial_anchors)
    if any(not square_anchors(part,width) for part in components(original)):
        return finish('failed','A landing patch has no complete requested-width footprint.')
    if len(groups)==1:return finish('connected','Existing landing patches already share a full-width route.')
    foreign={c for key,cells in state.floors.items() if key!=target for c in cells}
    foreign_shell=_halo(foreign,policy['foreignStructureClearance'])
    reclaimable=state.walls&(_halo(original)-original)-foreign_shell
    foreign_walls=state.walls-reclaimable
    reserved={c for reservation in state.reservations for c in reservation['cells']}
    interfaces={c for interface in state.interfaces for c in interface['cells']}
    approaches=set()
    for interface in state.interfaces:
        nx,ny=interface.get('normal',(0,0))
        for x,y in interface['cells']:
            for step in range(1,policy['interfaceApproachDepth']+1):
                approaches.update(((x+step*nx,y+step*ny),(x-step*nx,y-step*ny)))
    fixtures={c for pose in state.features.values() for c in pose.get('cells',())}
    # A landing's own approach is already protected traversable floor. Its
    # sides must remain available for a corridor turn; reserving an additional
    # empty halo there can seal a valid landing between two department wings.
    # Foreign/empty approaches still need a halo so new walls cannot consume them.
    blocked=(foreign_shell|foreign_walls|_halo(reserved,policy['reservationClearance'])|
             interfaces|_halo(approaches-original)|_halo(fixtures))-original
    # Existing target approach floor remains traversable; only new corridor
    # construction is excluded from protected ports and operating positions.
    x0,y0,x1,y1=bbox(blocked|original)
    pad=max(policy['canvasPadding'],2*width+1)
    x0,y0,x1,y1=x0-pad,y0-pad,x1+pad,y1+pad
    canvas_cells=(x1-x0)*(y1-y0);report['canvasCells']=canvas_cells
    if canvas_cells>maximum_cells:return finish('budget_exhausted','Boundary routing exceeds the configured canvas limit.')
    canvas=rect(x0,y0,x1-x0,y1-y0)
    previous_checker=Verifier(state)
    vacuum,witnesses=_exterior_witnesses(previous_checker,canvas)
    if vacuum is None:return finish('failed','Cannot retain an exterior-connected vacuum witness within the bounded canvas.')
    blocked|=_halo(vacuum,policy['reservationClearance'])-original
    free=canvas-blocked
    anchors=square_anchors(free,width)
    working=set(original)
    paths=[]
    while len(groups)>1:
        groups.sort(key=lambda group:(-len(group),min(group)))
        starts=groups[0]&anchors;goals=set().union(*groups[1:])&anchors
        if not starts or not goals:return finish('failed','Protected landing patches have no full-width escape.')
        queue=deque(sorted(starts));previous={p:None for p in queue};found=None
        while queue:
            if report['searchStates']>=maximum_work:
                return finish('budget_exhausted','Boundary routing exhausted the configured search-state budget.')
            point=queue.popleft();report['searchStates']+=1
            if point in goals:found=point;break
            for n in neighbors(point):
                if n in anchors and n not in previous:previous[n]=point;queue.append(n)
        if found is None:return finish('failed','No full-width route avoids protected department geometry and reservations.')
        path=[]
        while found is not None:path.append(found);found=previous[found]
        path.reverse();paths.append(path)
        working|={c for x,y in path for c in rect(x,y,width,width)}
        groups=components(square_anchors(working,width))
    # Commit only after all full-width components connect; failed candidates
    # never leave partial corridors or changed walls in the shared layout.
    previous_walls=set(state.walls);previous_bounds=state.bounds
    valid_interfaces={i['id'] for i in state.interfaces if previous_checker.interface_valid(i)}
    hull_rules=[(owner,rule) for owner,node in state.program.spaces.items() for rule in node.get('requirements',[])
                if rule['type'] in ('perimeter','docking') and previous_checker.node_requirement(owner,rule)['status']=='satisfied']
    state.floors[target]=working
    rebuild_walls(state)
    state.walls|=foreign_walls
    checker=Verifier(state)
    if any(not checker.interface_valid(i) for i in state.interfaces if i['id'] in valid_interfaces):
        state.floors[target]=original;state.walls=previous_walls;state.bounds=previous_bounds
        return finish('failed','Boundary construction would invalidate an existing doorway or approach.')
    extended_exterior=flood_exterior(checker.obstacles,(x0,y0,x1,y1))
    if (extended_exterior is None or not vacuum<=extended_exterior or
            any(checker.node_requirement(owner,rule)['status']!='satisfied' for owner,rule in hull_rules)):
        state.floors[target]=original;state.walls=previous_walls;state.bounds=previous_bounds
        return finish('failed','Boundary construction would enclose a hull frontage or obstruct its exterior approach.')
    # Later public-origin landings must respect the same vacuum witnesses.
    existing={r['id'] for r in state.reservations}
    state.reservations.extend(w for w in witnesses if w['id'] not in existing)
    cells=set().union(*state.floors.values(),state.walls)
    bx0,by0,bx1,by1=bbox(cells)
    state.bounds=(min(previous_bounds[0],bx0-3),min(previous_bounds[1],by0-3),
                  max(previous_bounds[2],bx1+3),max(previous_bounds[3],by1+3))
    report.update(componentsAfter=1,addedTiles=len(working-original),paths=paths,
                  preservedExteriorInterfaces=len(witnesses),preservedVacuumTiles=len(vacuum))
    return finish('connected','Landing patches are joined by actual unobstructed full-width harness floor.')
