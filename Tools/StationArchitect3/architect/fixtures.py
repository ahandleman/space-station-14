"""Native-derived spatial inventory, placement and independent pose checks.

Only spatial realization is certified here. Native saved entities, access settings,
device links, atmos, power and machine operation remain separate obligations.
"""
from __future__ import annotations

import copy
from functools import lru_cache
import hashlib
import json
import math
from pathlib import Path
import random
import re

import yaml

from .geometry import bbox, components, neighbors, shortest_path, square_anchors, rect
from .state import json_ready

REPO = Path(__file__).resolve().parents[3]
POLICY_PATH = Path(__file__).resolve().parents[1] / 'data/fixture-footprints.yaml'


class SpatialUnknown(ValueError):
    pass


class NativeLoader(getattr(yaml, 'CSafeLoader', yaml.SafeLoader)):
    pass


def _tag(loader, suffix, node):
    if isinstance(node, yaml.MappingNode):
        return {'_nativeType': suffix.removeprefix('type:'), **loader.construct_mapping(node, deep=True)}
    if isinstance(node, yaml.SequenceNode):
        return loader.construct_sequence(node, deep=True)
    return loader.construct_scalar(node)


NativeLoader.add_multi_constructor('!', _tag)


def _variant(value, index):
    if isinstance(value, dict):
        if value.get('_nativeType') == 'CreateVariants':
            choices = next((value[k] for k in ('values', 'sequences', 'mappings') if k in value), None)
            if not isinstance(choices, list) or not choices:
                raise SpatialUnknown('Unsupported native variant declaration')
            return _variant(choices[index if len(choices) > 1 else 0], index)
        return {k: _variant(v, index) for k, v in value.items()}
    if isinstance(value, list):
        return [_variant(v, index) for v in value]
    return value


def _merge(parent, child):
    if parent.get('_nativeType') != child.get('_nativeType') and '_nativeType' in child:
        return copy.deepcopy(child)
    out = copy.deepcopy(parent)
    for key, value in child.items():
        if key == 'components' and isinstance(value, list) and all(isinstance(c, dict) and 'type' in c for c in value):
            cs = {c['type']: c for c in out.get(key, []) if isinstance(c, dict) and 'type' in c}
            for c in value:
                if c.get('remove'):
                    cs.pop(c['type'], None)
                else:
                    cs[c['type']] = _merge(cs.get(c['type'], {}), c)
            out[key] = list(cs.values())
        elif isinstance(value, dict) and isinstance(out.get(key), dict):
            out[key] = _merge(out[key], value)
        else:
            out[key] = copy.deepcopy(value)
    return out


class PrototypeIndex:
    def __init__(self, repository=REPO):
        self.repository = Path(repository)
        self.raw, self.sources, self.cache, self.errors = {}, {}, {}, {}
        for path in sorted(set((self.repository / 'Resources/Prototypes').rglob('*.yml')) |
                           set((self.repository / 'Resources/Prototypes').rglob('*.yaml'))):
            data = yaml.load(path.read_text(encoding='utf-8-sig'), Loader=NativeLoader)
            if not isinstance(data, list):
                continue
            for node in data:
                if not isinstance(node, dict) or node.get('type') != 'entity':
                    continue
                ident = node.get('id')
                ids = [ident] if isinstance(ident, str) else ident.get('values', []) if isinstance(ident, dict) else []
                for i, key in enumerate(ids):
                    try:
                        self.raw[key] = _variant(node, i)
                        self.sources[key] = path.relative_to(self.repository).as_posix()
                    except (SpatialUnknown, IndexError) as error:
                        self.errors[key] = str(error)
        self.groups = self._collision_groups()

    def _collision_groups(self):
        path = self.repository / 'Content.Shared/Physics/CollisionGroup.cs'
        result = {}
        for name, expr in re.findall(r'^\s*(\w+)\s*=\s*([^,\n]+)', path.read_text(encoding='utf-8'), re.M):
            expr = expr.strip()
            shift = re.fullmatch(r'1\s*<<\s*(\d+)', expr)
            if shift:
                result[name] = 1 << int(shift[1])
            elif re.fullmatch(r'-?\d+', expr):
                result[name] = int(expr)
            else:
                tokens = [s.strip() for s in expr.split('|')]
                if all(s in result for s in tokens):
                    result[name] = 0
                    for token in tokens:
                        result[name] |= result[token]
        return result

    def resolve(self, key, active=()):
        if key in self.cache:
            return self.cache[key]
        if key in active or key not in self.raw:
            raise SpatialUnknown(self.errors.get(key, f'Unknown or cyclic native prototype: {key}'))
        node = self.raw[key]
        parents = node.get('parent', [])
        parents = [parents] if isinstance(parents, str) else parents
        if not isinstance(parents, list) or not all(isinstance(x, str) for x in parents):
            raise SpatialUnknown(f'Unsupported parent declaration for {key}')
        base = {}
        # SS14 multiple inheritance gives the earlier listed parent precedence.
        for parent in reversed(parents):
            base = _merge(base, self.resolve(parent, (*active, key)))
        self.cache[key] = _merge(base, node)
        return self.cache[key]

    def bits(self, value):
        if isinstance(value, int):
            return value
        values = value if isinstance(value, list) else [value] if value else []
        result = 0
        for entry in values:
            for name in re.split(r'[,|]', str(entry)):
                name = name.strip()
                if name not in self.groups:
                    raise SpatialUnknown(f'Unknown collision layer/mask {name}')
                result |= self.groups[name]
        return result


@lru_cache(maxsize=1)
def native_index():
    return PrototypeIndex()


@lru_cache(maxsize=1)
def policy():
    value = yaml.safe_load(POLICY_PATH.read_text(encoding='utf-8'))
    if value.get('format') != 'ss14-spatial-fixture-policy' or value.get('version') != 1:
        raise ValueError('Unsupported fixture footprint policy')
    return value


def _point(value):
    if isinstance(value, str):
        return tuple(float(v.strip()) for v in value.split(','))
    return tuple(float(v) for v in value)


def rotate(point, rotation):
    x, y = point
    for _ in range((rotation % 360) // 90):
        x, y = -y, x
    return x, y


def _shape_cells(shape):
    kind = shape.get('_nativeType')
    if kind in ('PhysShapeAabb', None) and ('bounds' in shape or kind == 'PhysShapeAabb'):
        bounds = _point(shape.get('bounds', policy()['nativeDefaults']['aabbBounds']))
    elif kind == 'PhysShapeCircle' or 'radius' in shape:
        r = float(shape.get('radius', policy()['nativeDefaults']['circleRadius']))
        x, y = _point(shape.get('position', (0, 0)))
        bounds = (x-r, y-r, x+r, y+r)
    elif kind in ('PolygonShape', 'Polygon', None) and 'vertices' in shape:
        points = [_point(v) for v in shape['vertices']]
        bounds = min(x for x,y in points), min(y for x,y in points), max(x for x,y in points), max(y for x,y in points)
    else:
        raise SpatialUnknown(f'Unsupported native collision shape {kind or shape}')
    x0, y0, x1, y1 = bounds
    # Entity origins are at tile centers. Touching the exact adjacent tile edge
    # does not occupy it. Bounding cells are conservative for circles/polygons.
    return {(x, y) for x in range(math.floor(x0+.5+1e-7), math.ceil(x1+.5-1e-7))
            for y in range(math.floor(y0+.5+1e-7), math.ceil(y1+.5-1e-7))}


@lru_cache(maxsize=512)
def prototype_descriptor(prototype):
    index = native_index()
    native = index.resolve(prototype)
    cs = {c['type']: c for c in native.get('components', [])}
    layer = policy()['defaultLayer']
    interface_binding = None
    for rule in policy()['classification']:
        if set(rule.get('componentsAll', [])) <= cs.keys():
            layer = rule['layer']
            interface_binding = rule.get('interfaceBinding')
            break
    cells = {tuple(c) for c in policy()['anchorCells']}
    blocked = set()
    opaque = set()
    physics = cs.get('Physics', {})
    for fixture in cs.get('Fixtures', {}).get('fixtures', {}).values():
        footprint = _shape_cells(fixture.get('shape', {}))
        # Sensor extents do not consume construction floor (e.g. recycler intake).
        if fixture.get('hard', True):
            cells |= footprint
        if index.bits(fixture.get('layer', [])) & index.groups['Opaque']:
            opaque |= footprint
        if (physics.get('canCollide', True) and fixture.get('hard', True)
                and (index.bits(fixture.get('layer', [])) & index.groups['MobMask']
                     or index.bits(fixture.get('mask', [])) & index.groups['MobLayer'])):
            blocked |= footprint
    if layer == 'interface':
        # Interface collision is represented by the access-controlled door edge.
        blocked = set()
    loading = set()
    if 'enteringOffset' in cs.get('EntityStorage', {}):
        x, y = _point(cs['EntityStorage']['enteringOffset'])
        if int(x)!=x or int(y)!=y:
            raise SpatialUnknown(f'{prototype} has a subtile native loading offset')
        loading.add((int(x), int(y)))
    result = {'prototype': prototype, 'layer': layer, 'cells': cells,
              'blocking_cells': blocked, 'loading': loading,
              'opaque_cells': opaque,
              'components': sorted(cs), 'source': index.sources[prototype],
              'native_geometry': 'conservative_tile_bounds',
              'operation': 'not_evaluated'}
    if interface_binding:
        result['interface_binding'] = copy.deepcopy(interface_binding)
    result['fingerprint'] = hashlib.sha256(json.dumps(json_ready(result), sort_keys=True).encode()).hexdigest()
    return result


def describe_feature(program, key):
    feature = program.features[key]
    authored = feature['node']
    assembly = authored.get('assembly')
    if not assembly and not feature.get('prototype'):
        return {'id': key, 'room': feature['room'], 'layer': 'abstract', 'cells': set(),
                'blocking_cells': set(), 'loading': set(), 'parts': [], 'workingTiles': 0,
                'approachWidth': 0, 'known': True, 'fingerprint': 'abstract_service_no_entity'}
    parts = program.definitions.get('assemblies', {}).get(assembly, {}).get('parts', []) if assembly else [
        {'id': 'device', 'prototypes': [feature['prototype']], 'x': 0, 'y': 0, 'rotation': 0}]
    if not parts:
        raise SpatialUnknown(f'Unknown assembly {assembly}')
    cells, blocked, loading, opaque, realized = set(), set(), set(), set(), []
    for part in parts:
        prototype = part['prototypes'][0]
        d = prototype_descriptor(prototype)
        dx, dy, rotation = part.get('x', 0), part.get('y', 0), part.get('rotation', 0)
        transform = lambda mask: {(rotate(c, rotation)[0]+dx, rotate(c, rotation)[1]+dy) for c in mask}
        cells |= transform(d['cells'])
        blocked |= transform(d['blocking_cells'])
        opaque |= transform(d['opaque_cells'])
        loading |= transform(d['loading'])
        realized.append({'id': part['id'], 'prototype': prototype, 'x': dx, 'y': dy,
                         'rotation': rotation, 'layer': d['layer'], 'source': d['source'],
                         'cells': transform(d['cells']), 'fingerprint': d['fingerprint']})
        if d.get('interface_binding'):
            realized[-1]['interface_binding'] = copy.deepcopy(d['interface_binding'])
    layers = {p['layer'] for p in realized}
    layer = next(iter(layers)) if len(layers) == 1 else 'floor'
    clearance = authored.get('clearance', {})
    result = {'id': key, 'room': feature['room'], 'layer': layer, 'cells': cells,
              'blocking_cells': blocked, 'loading': loading, 'parts': realized,
              'opaque_cells': opaque,
              'workingTiles': clearance.get('workingTiles', 0),
              'approachWidth': clearance.get('approachWidth', 1), 'known': True}
    result['fingerprint'] = hashlib.sha256(json.dumps(json_ready(result), sort_keys=True).encode()).hexdigest()
    return result


def estimate_room(program, room_id):
    descriptors, unknown = [], []
    for key, f in program.features.items():
        if f['room'] != room_id:
            continue
        try:
            descriptors.append(describe_feature(program, key))
        except SpatialUnknown as error:
            unknown.append({'feature': key, 'reason': str(error)})
    floor = [d for d in descriptors if d['layer'] in ('floor', 'portable')]
    occupied = max(sum(len(d['cells']) for d in floor),
                   sum(len(d['cells']) for d in descriptors if d['layer']=='underfloor'))
    blocked = sum(len(d['blocking_cells']) for d in floor)
    working = max([0] + [max(d['workingTiles'],d['approachWidth']) if d['workingTiles'] else len(d['loading']) for d in descriptors])
    width = height = 1
    for d in floor:
        x0,y0,x1,y1 = bbox(d['cells'])
        width = max(width, min(x1-x0, y1-y0))
        height = max(height, max(x1-x0, y1-y0))
    return {'minimum_area': occupied + working,
            'recommended_area': occupied + max(working, math.ceil(blocked*.65), 2*math.ceil(math.sqrt(blocked))) if occupied else working,
            'minimum_dimensions': (width, height), 'feature_count': len(descriptors)+len(unknown),
            'concrete_count': sum(d['layer'] != 'abstract' for d in descriptors),
            'native_part_count': sum(len(d['parts']) for d in descriptors),
            'blocked_area': blocked, 'working_area': working,
            'wall_slots': sum(len(d['cells']) for d in descriptors if d['layer']=='wall'),
            'interface_slots': sum(len(d['cells']) for d in descriptors if d['layer']=='interface'),
            'descriptors': descriptors, 'unknown': unknown}


def room_minimum_area(program, room_id):
    return estimate_room(program, room_id)['minimum_area']


def _move(mask, origin, rotation):
    return {(rotate(c, rotation)[0]+origin[0], rotate(c, rotation)[1]+origin[1]) for c in mask}


def _working_candidates(d, origin, rotation, floor):
    loading = _move(d['loading'], origin, rotation)
    cells = _move(d['cells'], origin, rotation)
    working = d['workingTiles']
    if not loading <= floor:
        return []
    if not working:
        return [loading]
    # Work regions are actual adjacent floor, shared by different machines when
    # unobstructed. A side can vary independently of sprite pose, except native
    # loading offsets which rotate with the installation.
    results = []
    for turn in range(4):
        oriented = {rotate((x-origin[0], y-origin[1]), -90*turn) for x,y in cells}
        x0,y0,x1,y1 = bbox(oriented)
        width = max(d['approachWidth'], min(max(1,x1-x0), math.ceil(math.sqrt(working))))
        depth = math.ceil(max(working,width) / width)
        xstart = x0 + (x1-x0-width)//2
        region = {(xstart + n % width, y0-1-n//width) for n in range(width*depth)}
        region = {(rotate(c,90*turn)[0]+origin[0], rotate(c,90*turn)[1]+origin[1]) for c in region} | loading
        if region <= floor and not region & cells:
            results.append(region)
    return results


def _interface_approaches(state, room):
    floor = state.floors.get(room,set())
    result = set()
    for i in state.interfaces:
        if room not in (i.get('a'),i.get('b')):
            continue
        profile=state.program.definitions.get('interfaces',{}).get(i.get('profile'),{})
        if not set(profile.get('capabilities',[])) & set(policy()['workingClearance']['preserveInterfaceCapabilities']):
            continue
        near = {n for c in i['cells'] for n in neighbors(c)} & floor
        if profile.get('geometry') == 'open_edge':
            near |= set(i['cells']) & floor
        result |= near
        for _ in range(policy()['workingClearance']['preserveInterfaceApproachDepth']-1):
            near = {n for c in near for n in neighbors(c)} & floor
            result |= near
    return result


def _aisle(state, room):
    floor = state.floors.get(room,set())
    approaches = _interface_approaches(state,room)
    width = policy()['placement']['preserveRouteWidth']
    anchors = square_anchors(floor,width)
    if not anchors:
        return approaches
    targets = [min(anchors,key=lambda a:(min(abs(a[0]-p[0])+abs(a[1]-p[1]) for p in part),a))
               for part in components(approaches)]
    if not targets:
        # A free entrance offer at a boundary is less restrictive than a rigid
        # center aisle when a small room contains one large installation.
        targets = [min(anchors)]
    tree = {targets[0]}
    for target in targets[1:]:
        path = shortest_path(anchors,tree,{target})
        if path:
            tree.update(path)
    return approaches | {c for x,y in tree for c in rect(x,y,width,width)}


def _compatible_layers(first, second):
    return second in policy()['compatibleLayers'].get(first, []) and first in policy()['compatibleLayers'].get(second, [])


def _interface_rotations(program, descriptor, interface):
    """Bind native directional parts to their actual profile and seam direction.

    The native hard fixture is represented by an access/direction-controlled
    interface. Its pose cannot quietly substitute for another door, or reverse
    the permitted travel through a correctly named profile.
    """
    profile=program.definitions.get('interfaces',{}).get(interface.get('profile'),{})
    rotations=set(policy()['rotations'])
    for part in descriptor['parts']:
        binding=part.get('interface_binding')
        if not binding:
            continue
        if binding.get('requireConstructionPrototype') and part['prototype'] not in profile.get('construction',[]):
            return []
        direction=profile.get('passage',{}).get('direction','bidirectional')
        if direction not in ('a_to_b','b_to_a'):
            if binding.get('requireDirectedPassage'):
                return []
            continue
        normal=tuple(interface.get('normal',()))
        if normal not in ((1,0),(0,1),(-1,0),(0,-1)):
            return []
        forward=normal if direction=='a_to_b' else (-normal[0],-normal[1])
        native=tuple(binding['nativeForward'])
        rotations={turn for turn in rotations if rotate(native,turn+part['rotation'])==forward}
    return sorted(rotations)


def _pose(d, origin, rotation, clearance, interface=None):
    return {'room': d['room'], 'cells': _move(d['cells'],origin,rotation),
            'blocking_cells': _move(d['blocking_cells'],origin,rotation),
            'clearance': set(clearance), 'origin': tuple(origin), 'rotation': rotation,
            'layer': d['layer'], 'descriptor_hash': d['fingerprint'],
            'prototype_parts': [{**{k:v for k,v in part.items() if k != 'cells'},
                                 'x': rotate((part['x'],part['y']),rotation)[0]+origin[0],
                                 'y': rotate((part['x'],part['y']),rotation)[1]+origin[1],
                                 'rotation': (part['rotation']+rotation)%360}
                                for part in d['parts']],
            'interface': interface, 'spatial_only': True}


def _check(key, room, status, message, measured=None):
    result = {'id':'fixture:'+key,'type':'fixture_geometry','owner':room,
              'priority':'mandatory','status':status,'message':message,
              'facet':'native_spatial_inventory'}
    if measured is not None:
        result['measured'] = measured
    return result


def place_features(state, room_id, seed=1):
    """Place all selected room inventory, retaining explicit failures.

    An unsuccessful room arrangement is retried locally; no authored feature is
    dropped or a minimum room bound silently enlarged.
    """
    plan = estimate_room(state.program,room_id)
    descriptors = [d for d in plan['descriptors'] if d['layer'] != 'abstract']
    floor = state.floors.get(room_id,set())
    foreign = {k:v for k,v in state.features.items() if v.get('room') != room_id}
    foreign_blocked = set().union(*(p.get('blocking_cells',p.get('cells',set())) for p in foreign.values())) if foreign else set()
    foreign_at = {}
    for p in foreign.values():
        for cell in p.get('cells',set()):
            foreign_at.setdefault(cell,[]).append(p)
    best = {}
    permanent = _aisle(state,room_id)
    priorities = {'interface':0,'wall':1,'floor':2,'portable':3,'underfloor':4}
    descriptors.sort(key=lambda d:(priorities[d['layer']],-len(d['cells']),-len(d['blocking_cells']),d['id']))
    for attempt in range(policy()['placement']['attemptsPerRoom']):
        placed = {}
        protected = set(permanent)
        rng = random.Random(int.from_bytes(hashlib.sha256(f'{seed}:{room_id}:fixtures:{attempt}'.encode()).digest()[:8],'big'))
        for d in descriptors:
            possible = []
            if d['layer'] == 'interface':
                for interface in state.interfaces:
                    if room_id not in (interface.get('a'),interface.get('b')):
                        continue
                    profile = state.program.definitions.get('interfaces',{}).get(interface.get('profile'),{})
                    if 'crew_passage' not in profile.get('capabilities',[]) and 'docking' not in profile.get('capabilities',[]):
                        continue
                    if profile.get('geometry') == 'open_edge':
                        continue
                    for c in sorted(interface['cells']):
                        for rotation in _interface_rotations(state.program,d,interface):
                            possible.append((c,rotation,interface['id']))
            else:
                anchors = ({n for c in floor for n in neighbors(c)} & state.walls) if d['layer'] == 'wall' else floor
                if attempt == 0:
                    ranked = [(c[1],c[0],c) for c in sorted(anchors)]
                elif attempt == 1:
                    ranked = [(c[0],c[1],c) for c in sorted(anchors)]
                elif attempt == 2:
                    ranked = [(-c[1],-c[0],c) for c in sorted(anchors)]
                else:
                    ranked = [(min(sum(n in floor for n in neighbors(c)),3),rng.random(),c) for c in sorted(anchors)]
                for _,_,c in sorted(ranked):
                    turns = list(policy()['rotations']);rng.shuffle(turns)
                    possible.extend((c,rotation,None) for rotation in turns)
            for origin,rotation,interface in possible[:policy()['placement']['candidateLimit']]:
                pose_cells = _move(d['cells'],origin,rotation)
                blocked = _move(d['blocking_cells'],origin,rotation)
                support = floor if d['layer'] not in ('wall','interface') else state.walls if d['layer']=='wall' else set().union(*(set(i['cells']) for i in state.interfaces if i['id']==interface))
                if not pose_cells <= support or blocked & protected:
                    continue
                conflicts = False
                if any(not _compatible_layers(d['layer'],other.get('layer','floor'))
                       for cell in pose_cells for other in foreign_at.get(cell,[])):
                    continue
                for other in placed.values():
                    if pose_cells & other['cells'] and not _compatible_layers(d['layer'],other['layer']):
                        conflicts=True;break
                if conflicts:
                    continue
                already_blocked = foreign_blocked | (set().union(*(p['blocking_cells'] for p in placed.values())) if placed else set())
                regions = _working_candidates(d,origin,rotation,floor)
                regions.sort(key=lambda region: (len(region-protected), sorted(region)))
                for clearance in regions:
                    if clearance & (already_blocked|blocked):
                        continue
                    free = floor - already_blocked - blocked
                    # Protect the approaches already promised to other devices,
                    # not only this device's adjacent work cells. A new machine
                    # must not cut an earlier loading aisle into a sealed pocket.
                    required = (protected | clearance) & floor
                    if required and not any(required <= component for component in components(free)):
                        continue
                    placed[d['id']] = _pose(d,origin,rotation,clearance,interface)
                    protected |= clearance
                    break
                if d['id'] in placed:
                    break
        if len(placed)>len(best):
            best=placed
        if len(placed)==len(descriptors):
            break
    state.features = {**foreign,**best}
    state.metadata.setdefault('fixture_inventory',{})[room_id] = {
        'selected':plan['feature_count'],'placed':len(best),'abstract':sum(d['layer']=='abstract' for d in plan['descriptors']),
        'native_parts_selected':plan['native_part_count'],
        'native_parts_placed':sum(len(p['prototype_parts']) for p in best.values()),
        'unknown':plan['unknown'],'required_floor_estimate':plan['minimum_area'],
        'native_operation':'not_evaluated'}
    return assess_features(state,rooms={room_id})


def assess_features(state, rooms=None):
    """Reconstruct every footprint from the source; never trust saved cell sets."""
    checks=[]
    room_ids = set(state.program.spaces) if rooms is None else set(rooms)
    all_blocked = set().union(*(p.get('blocking_cells',p.get('cells',set())) for p in state.features.values())) if state.features else set()
    for key,pose in state.features.items():
        if key not in state.program.features and pose.get('room') in room_ids:
            checks.append(_check(key,pose.get('room'),'violated','Undeclared feature cannot be introduced by a saved pose.'))
    for key,feature in state.program.features.items():
        room = feature['room']
        if room not in room_ids:
            continue
        try:
            d=describe_feature(state.program,key)
        except SpatialUnknown as error:
            checks.append(_check(key,room,'unknown',str(error)));continue
        if d['layer']=='abstract':
            pose=state.features.get(key,{})
            hidden=bool(pose.get('cells') or pose.get('blocking_cells') or pose.get('prototype_parts'))
            checks.append(_check(key,room,'violated' if hidden else 'satisfied',
                                 'Abstract service demand must not spawn a hidden physical entity.'));continue
        pose=state.features.get(key)
        if pose is None:
            checks.append(_check(key,room,'violated','Selected concrete feature is not placed.'));continue
        errors=[]
        if pose.get('room')!=room:
            errors.append('feature pose changes authored room ownership')
        origin=tuple(pose.get('origin',()))
        rotation=pose.get('rotation')
        if len(origin)!=2 or rotation not in policy()['rotations']:
            checks.append(_check(key,room,'violated','Missing or invalid native anchor/quarter-turn pose.'));continue
        cells=_move(d['cells'],origin,rotation)
        blocked=_move(d['blocking_cells'],origin,rotation)
        if pose.get('descriptor_hash')!=d['fingerprint'] or set(pose.get('cells',()))!=cells or set(pose.get('blocking_cells',()))!=blocked or pose.get('layer')!=d['layer']:
            errors.append('saved footprint/layer differs from native-derived descriptor')
        floor=state.floors.get(room,set())
        if d['layer']=='wall':
            if not cells<=state.walls or not any(n in floor for c in cells for n in neighbors(c)):
                errors.append('wall mount has no owning-room structural support')
        elif d['layer']=='interface':
            mount=next((i for i in state.interfaces if i['id']==pose.get('interface')),None)
            if not mount or room not in (mount.get('a'),mount.get('b')) or not cells<=set(mount['cells']):
                errors.append('door feature is not mounted to its owning room interface')
            elif not {'crew_passage','docking'} & set(state.program.definitions.get('interfaces',{}).get(mount.get('profile'),{}).get('capabilities',[])):
                errors.append('door feature cannot substitute for an interaction counter')
            elif rotation not in _interface_rotations(state.program,d,mount):
                errors.append('native interface prototype or facing differs from its declared directional passage')
        elif not cells<=floor:
            errors.append('fixture footprint exceeds owning room surface')
        clearance=set(pose.get('clearance',()))
        allowed_clearances=_working_candidates(d,origin,rotation,floor)
        if clearance not in allowed_clearances or clearance & all_blocked:
            errors.append('working/loading clearance is invalid or blocked')
        entrances=_interface_approaches(state,room) - all_blocked
        if entrances and clearance:
            reachable=set().union(*(c for c in components(floor-all_blocked) if c & entrances))
            if not clearance<=reachable:
                errors.append('working floor is unreachable from room entrances')
        for other_key,other in state.features.items():
            if other_key==key:
                continue
            if cells & set(other.get('cells',())) and not _compatible_layers(d['layer'],other.get('layer','floor')):
                errors.append('incompatible fixture-layer overlap');break
        expected=_pose(d,origin,rotation,clearance,pose.get('interface'))['prototype_parts']
        if pose.get('prototype_parts')!=expected:
            errors.append('assembly part identities/poses do not match authored realization')
        checks.append(_check(key,room,'violated' if errors else 'satisfied',
                             '; '.join(errors) if errors else 'All selected parts, collision footprint, mount and working clearance fit; operation is not evaluated.',
                             {'parts':len(d['parts']),'occupiedTiles':len(cells),'blockedTiles':len(blocked),'workingTiles':len(clearance)}))
    return checks
