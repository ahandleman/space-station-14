"""Local spatial adapters for department acceptance, separate from operation.

Only fully spatial contracts enter completed_rule_ids. Thermal state, operating
pressure, startup, device programming and power supply are never inferred here.
"""
from __future__ import annotations

import math
import posixpath
from collections import deque

from .fixtures import (SpatialUnknown, assess_features, describe_feature, native_index, policy,
                       prototype_descriptor, rotate)
from .geometry import bbox, flood_exterior, neighbors, outline, square_anchors, rect
from .verify import Verifier

DIRECTIONS={'north':(0,1),'east':(1,0),'south':(0,-1),'west':(-1,0)}


def _check(rule, status, message, measured=None, partial=False, cost=0):
    result={'id':rule['id']+('::spatial' if partial else ''),
            'sourceRule':rule['id'],'owner':rule.get('owner'),'type':rule['type'],
            'priority':rule.get('priority','mandatory'),'weight':rule.get('weight',100),
            'tier':rule.get('tier',0),'status':status,'message':message,
            'facet':'layout_geometry','cost':cost}
    if measured is not None:result['measured']=measured
    return result


def _rooms(program, owner, reference):
    if reference in program.spaces:return [reference]
    node=program.spaces[owner]
    template=reference[1:] if reference.startswith('/') else posixpath.normpath(node['template']+'/'+reference)
    return [key for key,n in program.spaces.items() if n['template']==template
            and all(node.get('bindings',{}).get(k)==n.get('bindings',{}).get(k)
                    for k in node.get('bindings',{}).keys() & n.get('bindings',{}).keys())]


def _features(program, owner, names):
    selected=[]
    for name in names:
        if name in program.features:
            selected.append(name);continue
        local=name[1:] if name.startswith('#') else name
        if '/' not in local and '#' not in local:
            declared=[f for f in program.spaces[owner]['node'].get('features',[]) if f['id']==local]
            if not declared:
                raise SpatialUnknown(f'Undeclared local feature {local} in {owner}')
            selected.extend(key for key,f in program.features.items() if f['room']==owner and f['node'].get('id')==local)
        else:
            path,sep,fid=local.partition('#')
            if not sep:raise SpatialUnknown(f'Unsupported feature reference {name}')
            owners=_rooms(program,owner,path)
            if not owners:raise SpatialUnknown(f'Unresolved local feature owner {path}')
            selected.extend(key for key,f in program.features.items() if f['room'] in owners and f['node'].get('id')==fid)
    return sorted(set(selected))


def _access_starts(verifier,owner,actor,removed=()):
    result=set()
    for interface in verifier.state.interfaces:
        if interface.get('a') in removed or interface.get('b') in removed:continue
        if owner not in (interface.get('a'),interface.get('b')) or not verifier.interface_valid(interface) or not verifier.permitted(interface,actor):
            continue
        nx,ny=interface['normal']
        sign=-1 if interface['a']==owner else 1
        open_edge=verifier.profile(interface).get('geometry')=='open_edge'
        floor=verifier.state.floors.get(owner,set())
        for x,y in interface['cells']:
            inner=(x,y) if open_edge and sign==-1 else (x+sign*nx,y+sign*ny)
            outer=(x+nx,y+ny) if open_edge and sign==-1 else (x,y) if open_edge else (x-sign*nx,y-sign*ny)
            if inner not in floor:continue
            # A release-only gate is not an imaginary ingress seed behind it.
            if open_edge:
                admitted=verifier.step_allowed(outer,inner)
            else:
                admitted=verifier.step_allowed(outer,(x,y)) and verifier.step_allowed((x,y),inner)
            if admitted:result.add(inner)
    return result


def _entry_policy(state,verifier,rule):
    owner=rule['owner'];p=rule['parameters']
    expected=state.program.definitions.get('interfaces',{}).get(p['interface'])
    if expected is None:return _check(rule,'unknown','Named interface profile is not defined.')
    entries=[];bad=[]
    for interface in state.interfaces:
        if owner not in (interface.get('a'),interface.get('b')):continue
        profile=state.program.definitions.get('interfaces',{}).get(interface.get('profile'),{})
        if 'crew_passage' not in profile.get('capabilities',[]):continue
        entries.append(interface['id'])
        if interface.get('profile')!=p['interface'] or not verifier.interface_valid(interface):bad.append(interface['id'])
    if not entries:return _check(rule,'violated','No realized routine entrance supplies the local entry policy.')
    return _check(rule,'violated' if bad else 'satisfied',
                  'Every routine crew entrance retains the declared interface profile; upstream routes remain independently checked.',
                  {'entries':entries,'wrongPolicy':bad},cost=len(bad))


def _loading(state,verifier,rule):
    p=rule['parameters'];owner=rule['owner'];actor=p['accessProfile']
    selected=_features(state.program,owner,p['featureIds'])
    if not selected:return _check(rule,'inactive','No selected instances in the declared feature families.')
    if actor not in state.program.definitions.get('accessProfiles',{}):return _check(rule,'unknown','Unknown loading actor.')
    starts=_access_starts(verifier,owner,actor)
    walk=verifier.walkable(actor)
    bad=[];positions={}
    for key in selected:
        pose=state.features.get(key)
        if not pose:bad.append(key+': unplaced');continue
        d=describe_feature(state.program,key)
        loads=[]
        for part in d['parts']:
            native=native_index().resolve(part['prototype'])
            cs={c['type']:c for c in native.get('components',[])}
            raw=cs.get(p['nativeComponent'],{}).get(p['offsetField'])
            if raw is None:raise SpatialUnknown(f'{part["prototype"]} has no declared native loading offset')
            values=[float(x.strip()) for x in raw.split(',')] if isinstance(raw,str) else list(raw)
            if len(values)!=2 or any(int(x)!=x for x in values):
                raise SpatialUnknown('Subtile loading offsets require a continuous-position adapter.')
            offset=rotate(tuple(values),part['rotation'])
            relative=(part['x']+offset[0],part['y']+offset[1])
            dx,dy=rotate(relative,pose['rotation'])
            loads.append((int(pose['origin'][0]+dx),int(pose['origin'][1]+dy)))
        positions[key]=loads
        floor=state.floors.get(owner,set())
        for point in loads:
            if point not in floor or point not in walk or point not in set(pose.get('clearance',())):
                bad.append(key+': loading/ejection support blocked')
            elif not starts or verifier.path(walk,starts,{point}) is None:
                bad.append(key+': loading approach is not reachable under actor credentials')
    return _check(rule,'violated' if bad else 'satisfied',
                  '; '.join(bad) if bad else 'Every selected native rotated opening/ejection position has clear support and an authorized approach.',
                  positions,cost=len(bad))


def _controls(state,verifier,rule):
    p=rule['parameters'];owner=rule['owner'];actor=p['accessProfile']
    if state.program.spaces[owner]['node'].get('environment')!=p['requiredEnvironment']:
        return _check(rule,'violated','Operating room does not declare the required environment profile.')
    forbidden=set()
    for ref in p['forbiddenApproachSpaces']:
        found=_rooms(state.program,owner,ref)
        if not found:raise SpatialUnknown(f'Unresolved forbidden process-space reference {ref}')
        forbidden.update(found)
    selected=_features(state.program,owner,p['controlFeatureIds'])
    if not selected:return _check(rule,'inactive','No selected local control instances.')
    if actor not in state.program.definitions.get('accessProfiles',{}):raise SpatialUnknown('Unknown control actor')
    walk=verifier.walkable(actor,removed=forbidden)
    starts=_access_starts(verifier,owner,actor,removed=forbidden)&walk
    failures=[]
    for key in selected:
        pose=state.features.get(key)
        approaches=set(pose.get('clearance',())) if pose else set()
        if not approaches or not approaches<=state.floors.get(owner,set()) or not starts or verifier.path(walk,starts,approaches) is None:
            failures.append(key)
    return _check(rule,'violated' if failures else 'satisfied',
                  'Operating positions stay in the declared host environment and are reached without entering excluded process spaces.',
                  {'unreachableControls':failures,'forbidden':sorted(forbidden)},cost=len(failures))


def _genpop(state,verifier,rule):
    """Prove custody geometry and authored credential phases, not ID execution."""
    p=rule['parameters'];owner=rule['owner']
    detention=_rooms(state.program,owner,p['detentionRoom'])
    if len(detention)!=1:raise SpatialUnknown('Custody geometry needs one concrete local detention room.')
    brig=detention[0]
    lockers=_features(state.program,owner,p['lockerFeatureIds'])
    entry=_features(state.program,owner,[p['entryGateFeature']])
    leave=_features(state.program,owner,[p['exitGateFeature']])
    selected=set(lockers+entry+leave)
    if not lockers or not entry or not leave:
        return _check(rule,'violated','A selected locker bank, entry lane and release lane must all be realized.',partial=True)
    card={c['type']:c for c in native_index().resolve(p['cardPrototype']).get('components',[])}
    if 'GenpopIdCard' not in card or 'ExpireIdCard' not in card:
        raise SpatialUnknown('Configured card does not declare native genpop expiration semantics.')
    admission=set(card.get('Access',{}).get('tags',[]))
    release=set(card['ExpireIdCard'].get('expiredAccess',[]))
    actors={phase:p[field] for phase,field in [('admission','admissionAccessProfile'),
               ('release','releaseAccessProfile'),('operator','operatorAccessProfile')]}
    profiles=state.program.definitions.get('accessProfiles',{})
    if any(actor not in profiles for actor in actors.values()):raise SpatialUnknown('Custody actor profile is missing.')
    credentials={phase:set(profiles[actor].get('levels',[])) for phase,actor in actors.items()}
    failures=[];bindings={}
    if not admission or not release or not admission<=credentials['admission'] or not release<=credentials['release']:
        failures.append('custody actor credentials do not include the native card phase')
    fixture_checks={c['id'].removeprefix('fixture:'):c for c in assess_features(state,rooms={owner})}
    for key in selected:
        if fixture_checks.get(key,{}).get('status')!='satisfied':failures.append(key+': native spatial fixture is invalid')
    def permitted(tags,cs):
        clauses=cs.get('AccessReader',{}).get('access',[])
        return not clauses or any(set(clause)<=tags for clause in clauses)
    occupied_roles=[]
    for role,keys,allowed,denied in [('entry',entry,'admission','release'),('release',leave,'release','admission')]:
        cells=set()
        for key in keys:
            pose=state.features.get(key,{})
            mount=next((i for i in state.interfaces if i['id']==pose.get('interface')),None)
            if not mount or {mount['a'],mount['b']}!={owner,brig} or not verifier.interface_valid(mount):
                failures.append(key+': gate is not on the declared sentencing/detention boundary')
            else:
                cells|=set(mount['cells']);bindings[key]=mount['id']
            for part in describe_feature(state.program,key)['parts']:
                cs={c['type']:c for c in native_index().resolve(part['prototype']).get('components',[])}
                if ('Turnstile' not in cs or not permitted(credentials[allowed],cs)
                        or permitted(credentials[denied],cs) or not permitted(credentials['operator'],cs)):
                    failures.append(key+': native gate access does not implement the named custody role')
        occupied_roles.append(cells)
    if occupied_roles[0]&occupied_roles[1]:failures.append('entry and release must occupy distinct physical gate tiles')
    phase_routes={}
    for label,source,target,actor,required in [
            ('admission',owner,brig,actors['admission'],True),
            ('release',brig,owner,actors['release'],True),
            ('unserved_escape',brig,owner,actors['admission'],False),
            ('released_reentry',owner,brig,actors['release'],False)]:
        path=verifier.route(source,target,{'accessProfile':actor,'minWidth':1,'within':'station'})
        phase_routes[label]=None if path is None else len(path)-1
        if (path is not None)!=required:failures.append(label+': actual directed credential route has the wrong reachability')
    operator_starts=_access_starts(verifier,owner,actors['operator'])
    walks={phase:verifier.walkable(actors[phase]) for phase in ('operator','release')}
    locker_parts=0
    for key in lockers:
        pose=state.features.get(key)
        if not pose:continue
        for part in describe_feature(state.program,key)['parts']:
            locker_parts+=1
            cs={c['type']:c for c in native_index().resolve(part['prototype']).get('components',[])}
            if 'GenpopLocker' not in cs or not permitted(credentials['operator'],cs):
                failures.append(key+': locker part lacks native genpop configuration access');continue
            cells={(pose['origin'][0]+rotate(c,pose['rotation'])[0],
                    pose['origin'][1]+rotate(c,pose['rotation'])[1]) for c in part['cells']}
            approaches={n for c in cells for n in neighbors(c)}&set(pose.get('clearance',()))&state.floors.get(owner,set())
            if (not approaches or verifier.path(walks['operator'],operator_starts,approaches) is None or
                    verifier.path(walks['release'],verifier.floor(brig),approaches) is None):
                failures.append(key+': a native locker part lacks staff/released-prisoner operating access')
    measured={'gateInterfaces':bindings,'lockerParts':locker_parts,'nativeAdmissionTags':sorted(admission),
              'nativeReleaseTags':sorted(release),'phaseRouteLengths':phase_routes,
              'nativeIdPairingExpiryAndPropertyRetrieval':'not_evaluated'}
    return _check(rule,'violated' if failures else 'satisfied',
                  '; '.join(failures) if failures else 'Actual gate poses, credential phases, directed custody routes and every locker approach fit; native ID pairing, timing and retrieval remain unverified.',
                  measured,partial=True,cost=len(failures))


def _vacuum(state,padding=0):
    inhabited=set()
    envs=state.program.definitions.get('environments',{})
    for key,cells in state.floors.items():
        env=envs.get(state.program.spaces.get(key,{}).get('node',{}).get('environment'),{})
        if env.get('pressure')!='vacuum':inhabited|=cells
    barriers=set(state.walls)|inhabited
    for interface in state.interfaces:
        profile=state.program.definitions.get('interfaces',{}).get(interface.get('profile'),{})
        if profile.get('airtight',True):barriers.update(interface['cells'])
    bounds=bbox(barriers|set().union(*state.floors.values())) if state.floors else bbox(barriers)
    x0,y0,x1,y1=bounds
    if state.bounds[2]>state.bounds[0]:
        x0,y0=min(x0,state.bounds[0]),min(y0,state.bounds[1])
        x1,y1=max(x1,state.bounds[2]),max(y1,state.bounds[3])
    return flood_exterior(barriers,(x0-padding,y0-padding,x1+padding,y1+padding))


def _opacity(state):
    result=set(state.walls)
    for interface in state.interfaces:
        profile=state.program.definitions.get('interfaces',{}).get(interface.get('profile'),{})
        if profile.get('geometry')=='open_edge':continue
        opaque=False
        for proto in profile.get('construction',[]):
            opaque|=bool(prototype_descriptor(proto).get('opaque_cells'))
        if opaque or not profile.get('construction') and 'visibility' not in profile.get('capabilities',[]):result.update(interface['cells'])
    for key,pose in state.features.items():
        if key not in state.program.features:continue
        d=describe_feature(state.program,key)
        for cell in d.get('opaque_cells',set()):
            x,y=rotate(cell,pose['rotation']);result.add((x+pose['origin'][0],y+pose['origin'][1]))
    return result


def _exposure(state,rule):
    p=rule['parameters'];owner=rule['owner']
    selected=_features(state.program,owner,p['featureIds'])
    if not selected:return _check(rule,'inactive','No selected exposure feature instances.')
    vacuum=_vacuum(state,p['rangeTiles'])
    if vacuum is None:return _check(rule,'unknown','Exterior calculation exceeds its bounded canvas.')
    opaque=_opacity(state) if p['medium']=='sunlight' else set(state.walls)
    failures=[];directions={}
    for key in selected:
        pose=state.features.get(key)
        if not pose:failures.append(key+': unplaced');continue
        origin=tuple(pose['origin']);clear=[]
        if p['medium']=='vacuum' and not set(pose['cells'])<=vacuum:
            failures.append(key+': exchanger is not in exterior-connected vacuum')
        for name in p['sampleDirections']:
            dx,dy=DIRECTIONS[name]
            ray={(origin[0]+dx*n,origin[1]+dy*n) for n in range(1,p['rangeTiles']+1)}
            if p['medium']=='vacuum':
                okay=ray<=vacuum and not ray&opaque
            else:
                okay=not ray&opaque
            if okay:clear.append(name)
        directions[key]=clear
        if len(clear)<p['minimumClearDirections']:failures.append(key+': insufficient unblocked sample directions')
    return _check(rule,'violated' if failures else 'satisfied',
                  '; '.join(failures) if failures else 'Every selected installation has the declared geometric exposure; operating output is not inferred.',
                  directions,cost=len(failures))


def _vacuum_gap(state,rule):
    owner=rule['owner'];minimum=rule['parameters'].get('minVacuumGap',0)
    own=state.aggregate(owner)
    own|=outline(own)&state.walls
    envs=state.program.definitions.get('environments',{})
    occupied=set()
    for key,cells in state.floors.items():
        if key in state.program.descendants(owner):continue
        env=envs.get(state.program.spaces[key]['node'].get('environment'),{})
        if env.get('pressure')=='standard':occupied|=cells
    occupied|=outline(occupied)&state.walls
    if not own or not occupied:return _check(rule,'unknown','Vacuum separation needs realized process and inhabited boundaries.',partial=True)
    gap=min(max(0,abs(x-xx)+abs(y-yy)-1) for x,y in own for xx,yy in occupied)
    vacuum=_vacuum(state)
    if vacuum is None:
        return _check(rule,'unknown','Exterior calculation exceeds its bounded canvas.',partial=True)
    # Geometry proves open external separation, not actual pressure or temperature.
    boundary=outline(own)-own
    open_boundary=boundary & (vacuum or set())
    good=gap>=minimum and bool(open_boundary)
    return _check(rule,'satisfied' if good else 'violated',
                  'Process/habitable built boundaries require the authored vacuum gap; full operating environment remains unevaluated.',
                  {'gap':gap,'minimum':minimum,'exteriorBoundaryTiles':len(open_boundary)},partial=True,cost=max(0,minimum-gap))


def _link_reach(state,rule):
    p=rule['parameters'];owner=rule.get('owner')
    bindings=[r for r in state.program.relationships if r.get('owner')==owner and r.get('authoredId')==p['bindingRelationship']
              and r.get('source')==rule['source']]
    if not bindings:return _check(rule,'unknown','The referenced concrete device binding is unavailable in this scope.')
    binding=next((r for r in bindings if r['target']==rule['target']),None)
    if binding is None:return _check(rule,'violated','Reach target differs from the actual bound device pair.')
    source=state.program.features[rule['source']]
    native=native_index().resolve(source['prototype'])
    cs={c['type']:c for c in native.get('components',[])}
    if p['rangeSourceComponent'] not in cs:raise SpatialUnknown('Selected native source lacks its range component')
    limit=cs[p['rangeSourceComponent']].get(p['rangeSourceField'])
    if limit is None:
        default=policy()['nativeDefaults']['componentFields'].get(p['rangeSourceComponent'],{}).get(p['rangeSourceField'])
        if not default:raise SpatialUnknown('Native range default has no supported source-backed adapter')
        limit=default['value']
    a,b=state.features.get(rule['source']),state.features.get(rule['target'])
    if not a or not b:return _check(rule,'violated','Bound device pair has missing poses.')
    grids={state.program.spaces[state.program.features[key]['room']]['node'].get('placement',{}).get('grid')
           for key in (rule['source'],rule['target'])}
    if len(grids)>1:return _check(rule,'unknown','Cross-grid native transforms are required before checking map distance.')
    distance=math.dist(a['origin'],b['origin'])
    return _check(rule,'satisfied' if distance<=limit else 'violated',
                  'Repairability range is measured against the actual selected device binding, not a convenient nearby substitute.',
                  {'distance':distance,'nativeRange':limit,'binding':binding['id']},cost=max(0,distance-limit)/max(1,limit))


def _via_path(verifier,anchors,starts,goals,waypoints,width=1):
    """Full-width footprint routing while visiting every declared waypoint."""
    if not starts or not goals or any(not group for group in waypoints):return None
    if not waypoints:return verifier.path(anchors,starts,goals,width)
    if len(waypoints)>12:raise SpatialUnknown('EVA waypoint search exceeds its bounded state budget')
    def bits(point):return sum(1<<i for i,group in enumerate(waypoints) if point in group)
    complete=(1<<len(waypoints))-1
    queue=deque((p,bits(p)) for p in sorted(starts))
    previous={entry:None for entry in queue}
    while queue:
        current=queue.popleft();point,visited=current
        if point in goals and visited==complete:
            path=[]
            while current is not None:
                path.append(current[0]);current=previous[current]
            return list(reversed(path))
        for neighbor in neighbors(point):
            nxt=(neighbor,visited|bits(neighbor))
            if neighbor in anchors and nxt not in previous and verifier.step_allowed(point,neighbor,width):
                previous[nxt]=current;queue.append(nxt)
    return None


def _eva_route(state,verifier,rule):
    """Geometric exterior access; suit/thermal/process safety remains pending."""
    p=rule['parameters'];actor=p['accessProfile'];width=p.get('minWidth',1)
    credentials=state.program.definitions.get('accessProfiles',{}).get(actor)
    if credentials is None:return _check(rule,'unknown','Unknown EVA actor profile.',partial=True)
    if not credentials.get('canTraverseVacuum',False):
        return _check(rule,'violated','Actor does not declare vacuum traversal capability.',partial=True)
    if p.get('within','station')!='station':
        return _check(rule,'unknown','Owner/parent exterior-domain bounds are not yet defined for EVA routing.',partial=True)
    removed={room for endpoint in p.get('avoid',[]) for room in verifier.endpoint_rooms(endpoint)}
    endpoints=verifier.endpoint_rooms(rule['source'])|verifier.endpoint_rooms(rule['target'])
    envs=state.program.definitions.get('environments',{})
    allowed=set();hazards=set();vacuum_rooms=set()
    for room in state.floors:
        env=envs.get(state.program.spaces.get(room,{}).get('node',{}).get('environment'),{})
        pressure=env.get('pressure')
        if pressure not in ('standard','vacuum','process'):
            if room in endpoints:raise SpatialUnknown('Endpoint environment has no supported pressure classification')
            continue
        hazardous=pressure=='process' or env.get('temperature') in ('hot','custom')
        if hazardous:hazards.add(room)
        if pressure=='vacuum':vacuum_rooms.add(room)
        # Entering a process endpoint is conditional on shutdown/cooling. Other
        # process rooms must never become convenient EVA traversal shortcuts.
        if room not in removed and (not hazardous or room in endpoints):allowed.add(room)
    vacuum=_vacuum(state,width+1)
    if vacuum is None:return _check(rule,'unknown','Exterior routing exceeds its bounded canvas.',partial=True)
    excluded_floor={c for room,cells in state.floors.items() if room not in allowed for c in cells}
    vacuum-=excluded_floor|verifier.fixture_cells|state.walls
    # A missing hull wall is not an invented permission to enter a sealed room.
    # Only a real authorized exterior interface may bridge that boundary.
    indoor={c for room,cells in state.floors.items() if room not in vacuum_rooms for c in cells}
    vacuum-=outline(indoor)
    cells=verifier.walkable(actor,allowed=allowed,removed=removed)|vacuum
    # Non-airtight counters may pass gas while still blocking crew movement.
    interface_cells={c for interface in state.interfaces for c in interface['cells']}
    cells-=interface_cells
    ports=[]
    for interface in state.interfaces:
        a,b=interface.get('a'),interface.get('b')
        if not verifier.interface_valid(interface):continue
        profile=verifier.profile(interface)
        if str(b).startswith('@domain/'):
            domain=state.program.definitions.get('domains',{}).get(b[8:],{})
            if a not in allowed or domain.get('kind')!='vacuum' or not domain.get('connectedToExterior'):continue
            nx,ny=interface['normal']
            outer={(x+nx,y+ny) for x,y in interface['cells']}
            if not outer<=vacuum:continue
            if verifier.permitted(interface,actor):
                cells.update(interface['cells']);ports.append(interface['id'])
            elif not profile.get('construction') and not profile.get('airtight',True) and profile.get('normalState')=='open':
                # A visibility-only lattice boundary is actual empty exterior,
                # not a controlled crew door. Native support is already floor.
                cells.update(set(interface['cells'])&vacuum)
        elif a in allowed and b in allowed and verifier.permitted(interface,actor):
            cells.update(interface['cells'])
    cells-=state.walls|verifier.fixture_cells|excluded_floor
    anchors=square_anchors(cells,width)
    starts=square_anchors(verifier.floor(rule['source'])&cells,width)&anchors
    goals=square_anchors(verifier.floor(rule['target'])&cells,width)&anchors
    waypoints=[square_anchors(verifier.floor(i)&cells,width)&anchors for i in p.get('through',[])]
    path=_via_path(verifier,anchors,starts,goals,waypoints,width)
    measured={'width':width,'authorizedExteriorInterfaces':ports,
              'conditionalProcessEndpoints':sorted(hazards&endpoints),
              'excludedProcessRooms':sorted(hazards-endpoints),'runtimeSurvival':'not_evaluated'}
    if path:
        traversed={c for x,y in path for c in rect(x,y,width,width)}
        measured.update(path=path,exteriorTiles=len(traversed&vacuum),length=len(path)-1)
    good=path is not None and len(path)-1<=p.get('maxLength',math.inf)
    if 'maxLength' in p:measured['maximumLength']=p['maxLength']
    return _check(rule,'satisfied' if good else 'violated',
                  'A continuous authorized full-width geometric EVA route exists; suit state, pressure cycling and process shutdown/cooling remain unverified.' if good else
                  'The shortest qualifying EVA route exceeds the declared maximum length.' if path else
                  'No authorized full-width EVA route connects the actual endpoint floors through exterior-connected vacuum and declared interfaces.',
                  measured,partial=True,cost=0 if good else 1)


def spatial_rule_checks(state):
    verifier=Verifier(state)
    checks=[];partial=[];completed=[]
    evaluators={'x_entry_interface_policy':lambda r:_entry_policy(state,verifier,r),
                'x_native_loading_access':lambda r:_loading(state,verifier,r),
                'x_local_control_access':lambda r:_controls(state,verifier,r),
                'x_feature_exposure':lambda r:_exposure(state,r),
                'x_device_link_reach':lambda r:_link_reach(state,r)}
    rules=[r for n in state.program.spaces.values() for r in n.get('requirements',[])]+list(state.program.relationships)
    for rule in rules:
        if rule['type']=='x_genpop_custody':
            try:partial.append(_genpop(state,verifier,rule))
            except (SpatialUnknown,KeyError,ValueError,TypeError) as error:
                partial.append(_check(rule,'unknown','Custody spatial adapter cannot resolve this declaration: '+str(error),partial=True))
            continue
        if rule['type']=='route' and rule['parameters'].get('mode')=='eva':
            try:partial.append(_eva_route(state,verifier,rule))
            except (SpatialUnknown,KeyError,ValueError,TypeError) as error:
                partial.append(_check(rule,'unknown','EVA spatial adapter cannot resolve this declaration: '+str(error),partial=True))
            continue
        if rule['type']=='environment' and rule['parameters'].get('minVacuumGap',0)>0:
            partial.append(_vacuum_gap(state,rule));continue
        if rule['type'] not in evaluators:continue
        try:check=evaluators[rule['type']](rule)
        except (SpatialUnknown,KeyError,ValueError,TypeError) as error:
            check=_check(rule,'unknown','Spatial adapter cannot resolve this declaration: '+str(error))
        checks.append(check)
        if check['status']!='unknown':completed.append(rule['id'])
    return {'checks':checks,'completed_rule_ids':completed,'partial_checks':partial}
