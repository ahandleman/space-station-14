"""Constructive hierarchical tile packing with bounded local alternatives.

Rooms are real floor masks; logical owners only contribute grouping and budgets.
Mandatory seams produce small deformable suites. Circulation is reserved while
those suites are packed, rather than painted into every leftover pocket.
"""
from __future__ import annotations

from collections import defaultdict
import hashlib
import math
import random

from .geometry import bbox, components, docking_cells, neighbors, outline, rect, seam_runs, open_edge_runs
from .state import LayoutState


class SearchBudget(Exception):
    pass


class Budget:
    def __init__(self, limit):
        self.limit = limit
        self.used = 0

    def take(self):
        if self.used >= self.limit:
            raise SearchBudget()
        self.used += 1


def rng_for(seed, key):
    digest = hashlib.sha256(f'{seed}:{key}'.encode()).digest()
    return random.Random(int.from_bytes(digest[:8], 'little'))


def area_bounds(space):
    low, preferred, high = 16, 36, 100000
    found = False
    for rule in space.get('requirements', []):
        p = rule['parameters']
        if rule['type'] == 'metric' and p.get('metric') == 'area.exclusive':
            if not found:
                low, preferred, high, found = 0, p.get('preferred', p.get('min', 36)), 100000, True
            if rule.get('priority', 'mandatory') == 'mandatory':
                low = max(low, p.get('min', 0))
                high = min(high, p.get('max', 100000))
            if 'preferred' in p:
                preferred = p['preferred']
    return int(low), float(max(low, min(high, preferred))), int(high)


def _public(program, room):
    profile = program.definitions.get('accessProfiles', {}).get(
        program.spaces[room]['node'].get('access'), {})
    return set(profile.get('levels', [])) <= {'public'}


def _contained(program, room):
    return [c for c in program.spaces[room]['children']
            if program.spaces[c]['kind'] == 'room'
            and program.spaces[c]['node']['placement'].get('nesting') == 'contained']


def _root_room(program, room):
    if room in program.features:
        room = program.features[room]['room']
    if room not in program.spaces or program.spaces[room]['kind'] not in ('room', 'passage'):
        return None
    while program.spaces[room]['node']['placement'].get('nesting') == 'contained':
        parent = program.spaces[room]['parent']
        if parent not in program.spaces or program.spaces[parent]['kind'] != 'room':
            break
        room = parent
    return room


def _dimensions(space, rng, jitter=0.12, extra=0):
    low, preferred, high = area_bounds(space)
    target = min(high, max(low, preferred * (1 + rng.uniform(-jitter, jitter))))
    target += extra
    short_rules = [r['parameters'] for r in space['requirements']
                   if r['type'] == 'metric' and r.get('priority', 'mandatory') == 'mandatory'
                   and r['parameters']['metric'] == 'shape.short_side']
    if short_rules:
        short_low = max(p.get('min', 1) for p in short_rules)
        short_high = min(p.get('max', float('inf')) for p in short_rules)
        if short_low == short_high:
            depth = int(short_low)
            length = max(depth, min(int(high // depth), int(math.ceil(target / depth))))
            return length, depth
    minimum_height = 4
    for rule in space['requirements']:
        if rule['type'] == 'docking':
            p = rule['parameters']
            minimum_height = max(minimum_height, p['approachWidth'] + 2 * p.get('clearance', 0))
    aspect = rng.uniform(0.8, 1.4)
    candidates = []
    for width in range(4, max(7, int(math.sqrt(max(16, target))) * 2 + 4)):
        height = max(minimum_height, int(math.ceil(target / width)))
        area = width * height
        if area < low + extra or area > high + extra:
            continue
        score = abs(area - target) / max(1, target) + 0.4 * abs(math.log(width / height / aspect))
        candidates.append((score, width, height))
    if not candidates:
        width = max(4, int(math.sqrt(max(16, target))))
        return width, max(minimum_height, math.ceil(target / width))
    _, width, height = min(candidates)
    return width, height


def _shift(cells, dx, dy):
    return {(x + dx, y + dy) for x, y in cells}


def _translate(block, dx, dy):
    return {
        'floors': {k: _shift(v, dx, dy) for k, v in block['floors'].items()},
        'envelopes': {k: _shift(v, dx, dy) for k, v in block['envelopes'].items()},
        'roots': list(block.get('roots', [])),
    }


def _cells(block):
    return set().union(*block['floors'].values()) if block['floors'] else set()


def _room_block(program, room, settings, seed, budget):
    """Place contained complexes first, preserving exclusive host area."""
    budget.take()
    rng = rng_for(seed, 'room:' + room)
    children = [_room_block(program, c, settings, seed, budget) for c in _contained(program, room)]
    if not children:
        width, height = _dimensions(program.spaces[room], rng, settings['area_jitter'])
        floor = rect(0, 0, width, height)
        return {'floors': {room: floor}, 'envelopes': {room: set(floor)}, 'roots': [room]}
    # Children are separated from host floor by an actual structural ring and a
    # two-wide usable host approach, not overlapping nested room rectangles.
    child_width = sum(bbox(_cells(c))[2] - bbox(_cells(c))[0] + 1 for c in children) - 1
    child_height = max(bbox(_cells(c))[3] - bbox(_cells(c))[1] for c in children)
    width, height = child_width + 6, child_height + 6
    child_holes = sum(len(_cells(c) | outline(_cells(c))) for c in children)
    low, preferred, high = area_bounds(program.spaces[room])
    while width * height - child_holes < preferred:
        if width <= height:
            width += 1
        else:
            height += 1
    parent_floor = rect(0, 0, width, height)
    floors, envelopes = {}, {room: set(parent_floor)}
    x = 3
    for child in children:
        x0, y0, x1, y1 = bbox(_cells(child))
        placed = _translate(child, x - x0, 3 - y0)
        child_cells = _cells(placed)
        parent_floor -= child_cells | outline(child_cells)
        floors.update(placed['floors'])
        envelopes.update(placed['envelopes'])
        x += x1 - x0 + 1
    floors[room] = parent_floor
    # Coherent exterior trimming is permitted later if the parent is oversized.
    return {'floors': floors, 'envelopes': envelopes, 'roots': [room]}


def _mandatory_edges(program):
    return [r for r in program.relationships if r['type'] == 'shared_boundary'
            and r.get('priority', 'mandatory') == 'mandatory'
            and r['source'] in program.spaces and r['target'] in program.spaces]


def _components(program, roots):
    graph = {r: set() for r in roots}
    for edge in _mandatory_edges(program):
        a, b = _root_room(program, edge['source']), _root_room(program, edge['target'])
        if a in graph and b in graph and a != b:
            graph[a].add(b)
            graph[b].add(a)
    pending, result = set(roots), []
    while pending:
        first = min(pending)
        members, todo = set(), [first]
        while todo:
            node = todo.pop()
            if node in members:
                continue
            members.add(node)
            pending.discard(node)
            todo.extend(sorted(graph[node] - members))
        result.append((members, graph))
    return result


def _collides(a, occupied):
    # At least one full structural cell between every pair of floor masks.
    return bool(a & occupied or any(n in occupied for p in a for n in neighbors(p)))


def _edge_runs(program, edge, a, b):
    profile = program.definitions.get('interfaces', {}).get(edge['parameters']['interface'], {})
    return open_edge_runs(a, b) if profile.get('geometry') == 'open_edge' else seam_runs(a, b)


def _blocks_collide(program, moved, existing):
    """Only explicit open-edge relationships permit abutting floor labels."""
    permitted = {frozenset((r['source'], r['target'])) for r in _mandatory_edges(program)
                 if program.definitions['interfaces'][r['parameters']['interface']].get('geometry') == 'open_edge'}
    for a, floor in moved['floors'].items():
        for b, other in existing['floors'].items():
            if floor & other:
                return True
            if frozenset((a, b)) not in permitted and any(n in other for p in floor for n in neighbors(p)):
                return True
    return False


def _suite(program, roots, graph, settings, seed, budget, trace):
    """Embed mandatory adjacency components using several local shape offers."""
    blocks = {r: _room_block(program, r, settings, seed, budget) for r in sorted(roots)}
    docked = {r for r in roots if any(q['type'] == 'docking' for q in program.spaces[r]['requirements'])}
    # Public customer rooms get frontage; docking frontage is reserved on east.
    anchor = min(roots, key=lambda r: (r not in docked, not _public(program, r), -len(graph[r]), r))
    result = blocks[anchor]
    placed = {anchor}
    todo = set(roots) - placed
    rng = rng_for(seed, 'suite:' + '|'.join(sorted(roots)))
    while todo:
        candidates = [r for r in sorted(todo) if graph[r] & placed]
        room = candidates[0] if candidates else min(todo)
        block = blocks[room]
        x0, y0, x1, y1 = bbox(_cells(block))
        width, height = x1 - x0, y1 - y0
        occupied = _cells(result)
        positions = []
        anchors = sorted(graph[room] & placed) or [anchor]
        normals = [(1, 0), (-1, 0), (0, 1), (0, -1)]
        rng.shuffle(normals)
        for neighbor in anchors:
            ax0, ay0, ax1, ay1 = bbox(result['floors'][neighbor])
            open_pair = any({edge['source'], edge['target']} == {room, neighbor}
                            and program.definitions['interfaces'][edge['parameters']['interface']].get('geometry') == 'open_edge'
                            for edge in _mandatory_edges(program))
            gap = 0 if open_pair else 1
            for nx, ny in normals:
                # Preserve the public root's entire south frontier, and the dock
                # root's east frontier. No room-name-specific topology rules.
                if neighbor == anchor and ((_public(program, anchor) and ny == -1)
                                            or (anchor in docked and nx == 1)):
                    continue
                if nx:
                    xx = ax1 + gap if nx == 1 else ax0 - width - gap
                    offsets = [ay0, ay1 - height, (ay0 + ay1 - height) // 2]
                    trial = [(xx, yy) for yy in offsets]
                else:
                    yy = ay1 + gap if ny == 1 else ay0 - height - gap
                    offsets = [ax0, ax1 - width, (ax0 + ax1 - width) // 2]
                    trial = [(xx, yy) for xx in offsets]
                for xx, yy in trial:
                    budget.take()
                    if _public(program, anchor) and yy < 0:
                        continue
                    moved = _translate(block, xx - x0, yy - y0)
                    if _blocks_collide(program, moved, result):
                        continue
                    supported = True
                    for edge in _mandatory_edges(program):
                        if {edge['source'], edge['target']} <= (placed | {room}) and room in (edge['source'], edge['target']):
                            aa = moved['floors'].get(edge['source'], result['floors'].get(edge['source'], set()))
                            bb = moved['floors'].get(edge['target'], result['floors'].get(edge['target'], set()))
                            if not any(len(run) >= edge['parameters'].get('minLength', 1) for _, run in _edge_runs(program, edge, aa, bb)):
                                supported = False
                                break
                    if not supported:
                        continue
                    extent = bbox(occupied | _cells(moved))
                    area = (extent[2] - extent[0]) * (extent[3] - extent[1])
                    positions.append((area + rng.random() * 0.1, xx, yy, moved))
        if positions:
            _, xx, yy, moved = min(positions, key=lambda p: (p[0], p[1], p[2]))
        else:
            xx = bbox(occupied)[2] + 1
            moved = _translate(block, xx - x0, -y0)
            trace.append({'phase': 'suite', 'status': 'unresolved', 'scope': program.common_ancestor(sorted(roots)),
                          'room': room, 'reason': 'No local placement realizes every mandatory seam; kept diagnostic candidate.'})
        result['floors'].update(moved['floors'])
        result['envelopes'].update(moved['envelopes'])
        placed.add(room)
        todo.remove(room)
    result['roots'] = sorted(roots)
    x0, y0, _, _ = bbox(_cells(result))
    result = _translate(result, -x0, -y0)
    trace.append({'phase': 'suite', 'status': 'placed', 'scope': program.common_ancestor(sorted(roots)),
                  'rooms': sorted(roots), 'candidate': seed, 'footprint': list(bbox(_cells(result)))})
    return result


def _suite_score(program, block, settings):
    """Local proposal score; it never substitutes for final exact verification."""
    cells = _cells(block)
    x0, y0, x1, y1 = bbox(cells)
    failures = 0
    for edge in _mandatory_edges(program):
        if edge['source'] in block['floors'] and edge['target'] in block['floors']:
            if not any(len(run) >= edge['parameters'].get('minLength', 1)
                       for _, run in _edge_runs(program, edge, block['floors'][edge['source']], block['floors'][edge['target']])):
                failures += 1
    preference = 0.0
    for room, floor in block['floors'].items():
        low, target, high = area_bounds(program.spaces[room])
        failures += not (low <= len(floor) <= high)
        preference += abs(len(floor) - target) / max(1, target)
    packing_cost = (x1 - x0) * (y1 - y0) / max(1, len(cells))
    return failures, preference + settings.get('compactness_weight', 4) * packing_cost


def _profile_for(program, actor=None, public=False):
    profiles = program.definitions.get('interfaces', {})
    levels = set(program.definitions.get('accessProfiles', {}).get(actor, {}).get('levels', ['public']))
    candidates = []
    for key, profile in profiles.items():
        if profile.get('geometry') == 'open_edge':
            continue
        if profile.get('passage', {}).get('direction', 'bidirectional') != 'bidirectional':
            # Automatic access doors have no authored travel orientation.
            # Directional gates are realized only by their local contracts.
            continue
        if 'crew_passage' not in profile.get('capabilities', []) or 'docking' in profile.get('capabilities', []):
            continue
        clauses = profile.get('access', {}).get('anyOf', [])
        if clauses and not any(set(clause) <= levels for clause in clauses):
            continue
        # Public approach remains public; staff ingress prefers an actual lock.
        if public and clauses:
            continue
        candidates.append((bool(clauses) != (not public), key))
    return min(candidates)[1] if candidates else None


def rebuild_walls(state):
    """Rebuild one-tile structures around actual floors, retaining door holes."""
    floor = set().union(*state.floors.values()) if state.floors else set()
    enclosed = set().union(*(cells for room, cells in state.floors.items()
                            if state.program.definitions.get('environments', {}).get(
                                state.program.spaces[room]['node'].get('environment'), {}
                            ).get('enclosure') != 'open')) if state.floors else set()
    doors = set().union(*(set(i['cells']) for i in state.interfaces)) if state.interfaces else set()
    state.walls = outline(enclosed) - floor - doors
    # Internal floor owners do not touch: their shared structural strip already
    # belongs to outline(floor). Corners are structural when diagonal enclosure
    # needs them, without claiming any floor area.
    for x, y in enclosed:
        for dx, dy in ((1, 1), (1, -1), (-1, 1), (-1, -1)):
            q = (x + dx, y + dy)
            if q not in floor and q not in doors:
                if (x + dx, y) not in floor and (x, y + dy) not in floor:
                    state.walls.add(q)


def interface_minimum(program, profile, requested=1):
    definition = program.definitions.get('interfaces', {}).get(profile, {})
    default = 2 if ('crew_passage' in definition.get('capabilities', [])
                    and definition.get('geometry') != 'open_edge') else 1
    return max(requested, definition.get('passage', {}).get('minWidth', default))


def _plan_seams(program, floors, demands, occupied=()):
    """Allocate disjoint interface runs together instead of reusing a border.

    Centering one door greedily can split the only frontier needed by a second
    doorway. Backtracking within each room pair retains that existing centered
    preference while proving enough distinct physical capacity for every edge.
    This is also used while embedding rooms, before committing any interfaces.
    """
    groups = {}
    for demand in demands:
        a, b, profile = demand['a'], demand['b'], demand['profile']
        if profile is None or a not in floors or b not in floors:
            return None
        groups.setdefault(frozenset((a, b)), []).append(demand)
    occupied = set(occupied)
    result = []
    for pair in groups.values():
        capacity = set()
        for demand in pair:
            definition = program.definitions['interfaces'][demand['profile']]
            runs = open_edge_runs if definition.get('geometry') == 'open_edge' else seam_runs
            capacity.update(c for _, run in runs(floors[demand['a']], floors[demand['b']]) for c in run)
        if sum(d.get('length', 2) for d in pair) > len(capacity-occupied):
            return None
        def solve(index, used):
            if index == len(pair):
                return []
            demand = pair[index]
            a, b, profile = demand['a'], demand['b'], demand['profile']
            length = demand.get('length', 2)
            definition = program.definitions['interfaces'][profile]
            passage = definition.get('passage', {})
            if length < passage.get('minWidth', 1) or length > passage.get('maxWidth', math.inf):
                return None
            is_open = definition.get('geometry') == 'open_edge'
            runs = open_edge_runs(floors[a], floors[b]) if is_open else seam_runs(floors[a], floors[b])
            for normal, run in runs:
                for free in sorted(components(run-used), key=lambda c: (-len(c), min(c))):
                    if len(free) < length:
                        continue
                    nx, ny = normal
                    ordered = sorted(free, key=lambda p: p[0]*-ny+p[1]*nx)
                    center = (len(ordered)-length)//2
                    starts = [0] if is_open else sorted(range(len(ordered)-length+1), key=lambda n: (abs(n-center), n))
                    for start in starts:
                        cells = set(ordered) if is_open else set(ordered[start:start+length])
                        if len(cells) > passage.get('maxWidth', math.inf):
                            continue
                        tail = solve(index+1, used|cells)
                        if tail is not None:
                            interface = {'a': a, 'b': b, 'profile': profile,
                                         'cells': cells, 'normal': normal}
                            if demand.get('rule'):
                                interface['rule'] = demand['rule']
                            return [interface, *tail]
            return None
        allocated = solve(0, occupied)
        if allocated is None:
            return None
        result.extend(allocated)
        occupied.update(c for interface in allocated for c in interface['cells'])
    return result


def _seams(state, demands):
    occupied = set().union(*(i['cells'] for i in state.interfaces)) if state.interfaces else set()
    plan = _plan_seams(state.program, state.floors, demands, occupied)
    if plan is None:
        return False
    for interface in plan:
        interface['id'] = f'interface:{len(state.interfaces)}'
        state.interfaces.append(interface)
    return True


def _seam(state, a, b, profile, length=2, rule=None):
    return _seams(state, [{'a': a, 'b': b, 'profile': profile, 'length': length, 'rule': rule}])


def _allowed_neighbors(space):
    allowed = None
    for rule in space['requirements']:
        if rule['type'] == 'access_policy' and rule['parameters'].get('entryFrom'):
            choices = set(rule['parameters']['entryFrom'])
            allowed = choices if allowed is None else allowed & choices
    return allowed


def add_interfaces(state):
    """Build exact authored borders, then credential-compatible access doors."""
    program = state.program
    _seams(state, [{'a': edge['source'], 'b': edge['target'],
                    'profile': edge['parameters']['interface'],
                    'length': interface_minimum(program, edge['parameters']['interface'],
                                                edge['parameters'].get('minLength', 1)),
                    'rule': edge['id']} for edge in _mandatory_edges(program)])
    passages = [i for i, n in program.spaces.items() if n['kind'] == 'passage']
    for room, space in sorted(program.spaces.items()):
        if space['kind'] != 'room' or room not in state.floors:
            continue
        allowed = _allowed_neighbors(space)
        parent = space['parent']
        actor = space['node'].get('access')
        is_public = _public(program, room)
        profile = _profile_for(program, actor, is_public)
        if space['node']['placement'].get('nesting') == 'contained' and parent in state.floors:
            _seam(state, room, parent, profile, 2)
            continue
        # An entryFrom room (e.g. armory) is reached through that host and gets
        # no automatic hall/maintenance bypass.
        if allowed is not None:
            destinations = [p for p in sorted(allowed) if p in state.floors]
        else:
            destinations = sorted(passages, key=lambda p: (
                ('public_circulation' in program.spaces[p]['node'].get('tags', [])) != is_public, p))
        for destination in destinations:
            if any(i['a'] == room and i['b'] == destination or i['b'] == room and i['a'] == destination for i in state.interfaces
                   if 'crew_passage' in program.definitions.get('interfaces', {}).get(i['profile'], {}).get('capabilities', [])):
                break
            if _seam(state, room, destination, profile, 2):
                break
    # Explicit passages are shared resources; connect them through controlled
    # transitions, not by giving adjacent differently owned floor a free edge.
    for idx, a in enumerate(passages):
        for b in passages[idx + 1:]:
            restrictive = b if 'maintenance_circulation' in program.spaces[b]['node'].get('tags', []) else a
            profile = _profile_for(program, program.spaces[restrictive]['node'].get('access'), False)
            _seam(state, a, b, profile, 2)


def _notches(state, settings, seed, budget):
    """Coherent corner changes retain every required interface approach."""
    protected = set()
    for interface in state.interfaces:
        nx, ny = interface['normal']
        for x, y in interface['cells']:
            protected.update({(x - nx, y - ny), (x + nx, y + ny)})
    for room, floor in sorted(state.floors.items()):
        if state.program.spaces[room]['kind'] != 'room' or _contained(state.program, room):
            continue
        rng = rng_for(seed, 'outline:' + room)
        if rng.random() > 0.7 or not settings.get('room_growth_steps', 0):
            continue
        x0, y0, x1, y1 = bbox(floor)
        if x1 - x0 < 6 or y1 - y0 < 6:
            continue
        low, preferred, high = area_bounds(state.program.spaces[room])
        corners = [(x0, y0), (x1 - 2, y0), (x0, y1 - 2), (x1 - 2, y1 - 2)]
        rng.shuffle(corners)
        for xx, yy in corners:
            budget.take()
            patch = rect(xx, yy, 2, 2)
            if not patch <= floor or patch & protected or len(floor) - 4 < low:
                continue
            result = floor - patch
            if len(components(result)) != 1:
                continue
            state.floors[room] = result
            state.envelopes[room] = result
            state.trace.append({'phase': 'room_growth', 'status': 'committed', 'scope': room,
                                'operation': 'coherent_corner_retract', 'cells': sorted(patch),
                                'areaBefore': len(floor), 'areaAfter': len(result)})
            break


def _dock(state, room, rule):
    floor = state.floors[room]
    x0, y0, x1, y1 = bbox(floor)
    # East frontage is reserved by component placement and by end-of-row packing.
    ys = [y for y in range(y0, y1) if (x1 - 1, y) in floor]
    pairs = [(y, y + 1) for y in ys if y + 1 in ys]
    if not pairs:
        return
    yy, _ = min(pairs, key=lambda pair: abs(pair[0] - (y0 + y1 - 2) / 2))
    domains = state.program.definitions.get('domains', {})
    domain = next((k for k, d in sorted(domains.items()) if d.get('connectedToExterior')), None)
    if not domain:
        return
    interface = {'id': f'interface:{len(state.interfaces)}', 'a': room,
                 'b': '@domain/' + domain, 'profile': rule['parameters']['interface'],
                 'cells': {(x1, yy), (x1, yy + 1)}, 'normal': (1, 0), 'rule': rule['id']}
    state.interfaces.append(interface)
    cells = docking_cells(interface, rule['parameters'])
    state.reservations.append({'id': f'dock:{room}:{rule["id"]}', 'owner': room,
                               'kind': 'docking', 'rule': rule['id'], 'cells': cells})


def _grow_rooms(state, settings, seed, budget):
    """Bid for useful free patches using local and ancestor area pressures.

    Initial rectangles are capacity proposals. This pass can leave those parcels
    and grow into genuinely unused neighboring space, while preserving real
    circulation, structural separation, contained envelopes and exterior reserves.
    Cross-owner transfers are left to the transactional repair coordinator.
    """
    program = state.program
    reserved = set().union(*(r['cells'] for r in state.reservations)) if state.reservations else set()
    reserved_structure = {(x + dx, y + dy) for x, y in reserved
                          for dx in (-1, 0, 1) for dy in (-1, 0, 1)}
    interface_cells = set().union(*(i['cells'] for i in state.interfaces)) if state.interfaces else set()
    room_ids = [k for k in sorted(state.floors) if program.spaces[k]['kind'] == 'room'
                and program.spaces[k]['node']['placement'].get('nesting') != 'contained']
    rng = rng_for(seed, 'growth_order')
    rng.shuffle(room_ids)
    steps = 0
    for room in room_ids:
        floor = state.floors[room]
        low, target, high = area_bounds(program.spaces[room])
        local_rng = rng_for(seed, 'growth_patch:' + room)
        while len(floor) < target and steps < settings.get('room_growth_steps', 0):
            occupied_other = set().union(*(v for k, v in state.floors.items() if k != room))
            forbidden = occupied_other | outline(occupied_other) | reserved_structure | interface_cells
            # A two-by-two growth patch retains usable room depth; tentacles and
            # isolated leftover tiles cannot earn area credit through this move.
            frontier = outline(floor)
            patches = {tuple(sorted(rect(x + dx, y + dy, 2, 2) - floor))
                       for x, y in frontier for dx, dy in ((0, 0), (-1, 0), (0, -1), (-1, -1))}
            proposals = []
            for packed in sorted(patches):
                if not packed:
                    continue
                patch = set(packed)
                if len(patch) < 2 or patch & forbidden or len(floor) + len(patch) > high:
                    continue
                budget.take()
                candidate = floor | patch
                if len(components(candidate)) != 1:
                    continue
                x0, y0, x1, y1 = bbox(candidate)
                aspect = max(x1 - x0, y1 - y0) / max(1, min(x1 - x0, y1 - y0))
                bounds_ok = True
                cost_change = 0.0
                for ancestor in program.ancestors(room):
                    before = len(state.aggregate(ancestor))
                    for rule in program.spaces[ancestor]['requirements']:
                        if rule['type'] != 'metric':
                            continue
                        p = rule['parameters']
                        metric = p.get('metric')
                        if metric == 'shape.aspect_ratio' and ancestor == room:
                            if rule.get('priority', 'mandatory') == 'mandatory' and not p.get('min', 0) <= aspect <= p.get('max', float('inf')):
                                bounds_ok = False
                        if metric == 'shape.short_side' and ancestor == room:
                            short_side = min(x1 - x0, y1 - y0)
                            if rule.get('priority', 'mandatory') == 'mandatory' and not p.get('min', 1) <= short_side <= p.get('max', float('inf')):
                                bounds_ok = False
                        if metric not in ('area.aggregate', 'area.exclusive'):
                            continue
                        if metric == 'area.exclusive' and ancestor != room:
                            continue
                        before_area = len(floor) if metric == 'area.exclusive' else before
                        after_area = before_area + len(patch)
                        if rule.get('priority', 'mandatory') == 'mandatory' and after_area > p.get('max', float('inf')):
                            bounds_ok = False
                        if 'preferred' in p:
                            scale = p.get('scale', max(1, p['preferred']))
                            cost_change += rule.get('weight', 100) * (abs(after_area - p['preferred']) - abs(before_area - p['preferred'])) / scale
                if not bounds_ok or cost_change >= 0:
                    continue
                # Preference cost decides usefulness; a small deterministic shape
                # bias chooses among equally useful coherent frontier patches.
                perimeter_delta = len(outline(candidate)) - len(outline(floor))
                proposals.append((cost_change + 0.02 * perimeter_delta + local_rng.random() * 0.01,
                                  min(patch), patch))
            if not proposals:
                state.trace.append({'phase': 'room_growth', 'status': 'locally_unresolved', 'scope': room,
                                    'requestedScope': program.spaces[room]['parent'], 'required': len(floor) < low,
                                    'reason': 'No free local patch improves area pressures without consuming a protected neighbor or reservation.'})
                break
            _, _, patch = min(proposals, key=lambda item: (item[0], item[1]))
            before_area = len(floor)
            floor |= patch
            state.envelopes.setdefault(room, set()).update(patch)
            steps += 1
            state.trace.append({'phase': 'room_growth', 'status': 'committed', 'scope': room,
                                'operation': 'free_boundary_patch_growth', 'cells': sorted(patch),
                                'areaBefore': before_area, 'areaAfter': len(floor),
                                'pressures': program.ancestors(room)})
    state.metadata['growth_steps'] = steps


def _features(state, seed):
    # Geometry is explicit even when prototype collision/operation semantics are
    # unsupported. Those semantics remain unknown in independent verification.
    occupied = set()
    for fid, feature in sorted(state.program.features.items()):
        room = feature['room']
        if feature['node'].get('prototypes') == [] and not feature['node'].get('assembly'):
            continue
        if room not in state.floors:
            continue
        node = feature['node']
        assembly = state.program.definitions.get('assemblies', {}).get(node.get('assembly'), {})
        offsets = {(0, 0)}
        if assembly:
            offsets = {tuple(part.get('position', part.get('offset', [0, 0]))) for part in assembly.get('parts', [])} or {(0, 0)}
        floor = state.floors[room]
        x0, y0, x1, y1 = bbox(floor)
        candidates = sorted(floor, key=lambda p: (abs(p[0] - (x0 + x1) / 2) + abs(p[1] - (y0 + y1) / 2), p))
        for x, y in candidates:
            cells = _shift(offsets, x, y)
            if cells <= floor and not cells & occupied:
                state.features[fid] = {'room': room, 'cells': cells, 'clearance': set(), 'rotation': 0,
                                       'prototype': feature.get('prototype'), 'assembly': node.get('assembly')}
                occupied.update(cells)
                break


def generate_layout(program, settings, seed=1):
    """Produce a bounded candidate; only the independent verifier can accept it."""
    state = LayoutState(program)
    budget = Budget(settings.get('max_proposals', 100000))
    try:
        roots = [i for i, n in program.spaces.items() if n['kind'] == 'room' and _root_room(program, i) == i]
        blocks = []
        for members, graph in _components(program, roots):
            alternatives = []
            for variant in range(settings.get('suite_alternatives', 3)):
                local_trace = []
                option = _suite(program, members, graph, settings, seed + variant * 104729, budget, local_trace)
                alternatives.append((_suite_score(program, option, settings), variant, option, local_trace))
            alternatives.sort(key=lambda entry: (entry[0], entry[1]))
            _, variant, block, chosen_trace = alternatives[0]
            state.trace.extend(chosen_trace)
            scope = program.common_ancestor(sorted(members)) or '@station'
            state.summaries[scope + ':' + min(members)] = {
                'scope': scope, 'chosenAlternative': variant, 'frame': 'suite_local',
                'verification': 'unverified_proposal', 'sourceHash': program.source_hash,
                'alternatives': [{'id': n, 'score': list(score),
                                  'footprint': list(bbox(_cells(candidate))),
                                  'floorAreas': {k: len(v) for k, v in candidate['floors'].items()},
                                  'floors': {k: sorted(v) for k, v in candidate['floors'].items()},
                                  'unresolvedExternalNeeds': [r['id'] for r in program.relationships
                                      if (r['source'] in candidate['floors']) != (r['target'] in candidate['floors'])]}
                                 for score, n, candidate, _ in alternatives[:settings.get('parent_alternatives', 4)]]}
            state.metadata.setdefault('construction_alternatives', []).append(state.summaries[scope + ':' + min(members)])
            block['owner'] = program.spaces[min(members)].get('owner')
            block['docking'] = any(r['type'] == 'docking' for k in members for r in program.spaces[k]['requirements'])
            block['deep'] = all(any(r['type'] == 'access_policy' and r['parameters'].get('mustTraverse')
                                    and any('maintenance_circulation' in program.spaces.get(p, {}).get('node', {}).get('tags', [])
                                            for p in r['parameters']['mustTraverse'])
                                    for r in program.spaces[k]['requirements']) for k in members)
            blocks.append(block)
        passage_ids = [i for i, n in program.spaces.items() if n['kind'] == 'passage']
        public = next((i for i in passage_ids if 'public_circulation' in program.spaces[i]['node'].get('tags', [])), None)
        maintenance = next((i for i in passage_ids if 'maintenance_circulation' in program.spaces[i]['node'].get('tags', [])), None)
        primary = public or maintenance
        secondary = maintenance or public
        width = settings.get('maintenance_width', 2)
        gap = max(width + 2, settings.get('department_gap', width + 2))
        # Overlapping two-wide side reservations can form a legitimate three-wide
        # strip. Any wider gap instead gets two distinct narrow strips and a
        # short cross-link; never widen the entire maintenance passage to fill it.
        if gap > width + 2 and not (width == 2 and gap == 5):
            gap = max(gap, 2 * width + 3)
        public_width = 3
        if primary:
            for rule in program.spaces[primary]['requirements']:
                if rule['type'] == 'metric' and rule['parameters'].get('metric') == 'corridor.clear_width':
                    p = rule['parameters']
                    public_width = max(p.get('min', 2), min(p.get('max', 5), p.get('preferred', 3)))
            state.floors[primary] = set()
            state.strips[primary] = []
        if secondary:
            state.floors.setdefault(secondary, set())
            state.strips.setdefault(secondary, [])
        def strip(owner, cells):
            if owner and cells:
                state.floors[owner].update(cells)
                state.strips[owner].append(cells)
        rng = rng_for(seed, 'department_order')
        groups = defaultdict(list)
        for block in blocks:
            groups[block['owner']].append(block)
        owners = sorted(groups, key=lambda v: v or '')
        rng.shuffle(owners)
        rows = [[], []]
        loads = [0, 0]
        # Keep ordinary owners together, while independent distributed suites
        # may occupy opposite station neighborhoods without changing ownership.
        for owner in owners:
            values = groups[owner]
            rng.shuffle(values)
            tags = program.spaces.get(owner, {}).get('node', {}).get('tags', [])
            distributed = bool({'distributed', 'maintenance_owner'} & set(tags))
            chosen = min(range(2), key=lambda r: loads[r])
            for block in values:
                row = min(range(2), key=lambda r: loads[r]) if distributed else chosen
                rows[row].append(block)
                loads[row] += bbox(_cells(block))[2] + gap
        # End-mounted docking avoids pushing a berth through another department.
        for row in rows:
            row.sort(key=lambda b: b['docking'])
        maximum_x = 0
        extents = []
        for row_index, row in enumerate(rows):
            cursor = 0
            previous_right = None
            for block in row:
                budget.take()
                x0, y0, x1, y1 = bbox(_cells(block))
                h = y1 - y0
                open_front = any(r['type'] == 'shared_boundary'
                                 and r['source'] in block['floors'] and r['target'] == primary
                                 and program.definitions['interfaces'][r['parameters']['interface']].get('geometry') == 'open_edge'
                                 for r in program.relationships)
                # Reflection keeps the public frontier on the central spine.
                if row_index:
                    reflected = {'floors': {k: {(x, -y - 1) for x, y in f} for k, f in block['floors'].items()},
                                 'envelopes': {k: {(x, -y - 1) for x, y in f} for k, f in block['envelopes'].items()},
                                 'roots': block['roots']}
                    placed = _translate(reflected, cursor - x0, 0 if open_front else -1)
                else:
                    placed = _translate(block, cursor - x0, public_width + (0 if open_front else 1) - y0)
                state.floors.update(placed['floors'])
                state.envelopes.update(placed['envelopes'])
                bx0, by0, bx1, by1 = bbox(_cells(placed))
                # Three-sided narrow maintenance wraps are shared by adjacent
                # modules. Broad unused regions remain explicit unbuilt space.
                if row_index:
                    rear_y = by0 - width - 1
                    side_y, side_h = rear_y, by1 - rear_y - (1 if open_front else 0)
                else:
                    rear_y = by1 + 1
                    side_y = by0 + (1 if open_front else 0)
                    side_h = rear_y + width - side_y
                strip(secondary, rect(bx0 - width - 1, side_y, width, side_h))
                if not block['docking']:
                    strip(secondary, rect(bx1 + 1, side_y, width, side_h))
                strip(secondary, rect(bx0 - width - 1, rear_y,
                                      bx1 - bx0 + width + 1 + (0 if block['docking'] else width + 1), width))
                if previous_right is not None and gap > width + 2:
                    # A local cross-link keeps independent side reservations in
                    # one maintenance component without a broad leftover chamber.
                    link_y = -1 - width if row_index else public_width + 1
                    strip(secondary, rect(previous_right + 1, link_y,
                                          bx0 - previous_right - 2, width))
                extents.append((bx0, by0, bx1, by1))
                state.trace.append({'phase': 'department_packing', 'status': 'committed',
                                    'scope': block['owner'], 'rooms': block['roots'],
                                    'bounds': [bx0, by0, bx1, by1], 'row': row_index})
                previous_right = bx1
                cursor = bx1 + gap
            maximum_x = max(maximum_x, cursor - gap if row else 0)
        if primary and maximum_x:
            strip(primary, rect(0, 0, maximum_x, public_width))
        if secondary and extents:
            # One exterior side trunk joins top/bottom maintenance without
            # claiming a crossing through public floor as maintenance coverage.
            bottom = min(e[1] for e in extents) - width - 1
            top = max(e[3] for e in extents) + width + 1
            strip(secondary, rect(-width - 1, bottom, width, top - bottom))
        add_interfaces(state)
        _notches(state, settings, seed, budget)
        for room, space in program.spaces.items():
            if room in state.floors and space['kind'] == 'room':
                for rule in space['requirements']:
                    if rule['type'] == 'docking':
                        _dock(state, room, rule)
        _grow_rooms(state, settings, seed, budget)
        _features(state, seed)
        rebuild_walls(state)
        occupied = set().union(*state.floors.values(), state.walls) if state.floors else set()
        reserved = set().union(*(r['cells'] for r in state.reservations)) if state.reservations else set()
        x0, y0, x1, y1 = bbox(occupied | reserved)
        state.bounds = (x0 - 2, y0 - 2, x1 + 2, y1 + 2)
        state.metadata['termination'] = 'completed'
    except SearchBudget:
        state.trace.append({'phase': 'search', 'status': 'budget_exhausted', 'scope': None,
                            'reason': 'Configured placement proposal budget exhausted; no infeasibility proof.'})
        state.metadata['termination'] = 'budget_exhausted'
        rebuild_walls(state)
    state.metadata.update({'work_used': budget.used, 'seed': seed, 'algorithm': 'hierarchical_tile_growth_v1',
                           'native_realization': 'not_evaluated', 'runtime_operation': 'not_evaluated'})
    return state
