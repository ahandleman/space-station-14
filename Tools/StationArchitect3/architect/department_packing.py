"""Department-scale floor allocation, contact embedding and explicit resize work.

The selected program is immutable. Every selected physical room receives floor;
external relationships remain boundary contracts for the station integrator.
"""
from __future__ import annotations

import copy
import math
import random
import heapq
from collections import defaultdict

from .geometry import bbox, components, neighbors, outline, rect, seam_runs
from .packing import (Budget, SearchBudget, _seam, _seams, _plan_seams,
                      interface_minimum, area_bounds, rng_for, rebuild_walls)
from .settings import DEFAULTS
from .state import LayoutState
from .policies import internal_profile


def _metric(space, name):
    return [r['parameters'] for r in space.get('requirements', [])
            if r['type'] == 'metric' and r['parameters']['metric'] == name
            and r.get('priority', 'mandatory') == 'mandatory']


def _limits(space, metric, low=0, high=10**8):
    for p in _metric(space, metric):
        low, high = max(low, p.get('min', low)), min(high, p.get('max', high))
    return low, high


def _physical(program, department):
    return [k for k in program.descendants(department, True)
            if program.spaces[k]['kind'] in ('room', 'passage')]


def _root(program, key, owned):
    if key in program.features:
        key = program.features[key]['room']
    if key not in owned:
        return None
    while program.spaces[key]['node']['placement'].get('nesting') == 'contained':
        parent = program.spaces[key]['parent']
        if parent not in owned:
            break
        key = parent
    return key


def _room_floor_min(program, room):
    low, target, high = area_bounds(program.spaces[room])
    width = max([1] + [r['parameters'].get('minWidth', 1) for r in program.relationships
                      if r['type'] == 'route' and room in (r['source'], r['target'])
                      and r['parameters'].get('mode') == 'crew'])
    low = max(low, width * width)
    try:
        from .fixtures import estimate_room
        estimate = estimate_room(program, room)
        low = max(low, estimate.get('minimum_area', 0), estimate.get('blocked_area', 0) + width * width)
    except ImportError:
        pass
    return int(low), float(target), int(high)


def department_area_bounds(program, department):
    rooms = _physical(program, department)
    limits = {r: _room_floor_min(program, r) for r in rooms}
    low = sum(x[0] for x in limits.values())
    high = sum(x[2] for x in limits.values())
    low, high = _limits(program.spaces[department], 'area.aggregate', low, high)
    preferred = next((r['parameters']['preferred'] for r in program.spaces[department]['requirements']
                      if r['type'] == 'metric' and r['parameters']['metric'] == 'area.aggregate'
                      and 'preferred' in r['parameters']), sum(x[1] for x in limits.values()))
    return {'min': int(low), 'preferred': int(max(low, min(high, preferred))), 'max': int(high),
            'rooms': {r: {'min': a, 'preferred': p, 'max': b} for r, (a, p, b) in limits.items()}}


def _allocation(program, department, target, seed, circulation_boost=1.0, circulation_floor=0, circulation_ceiling=None):
    bounds = department_area_bounds(program, department)
    if target < bounds['min'] or target > bounds['max']:
        return None, bounds, 'Requested area contradicts selected room/department hard bounds.'
    rooms = bounds['rooms']
    allocation = {r: v['min'] for r, v in rooms.items()}
    # Empirical equipment packing claims can exceed an authored soft area target.
    # They guide this candidate, never rewrite YAML or establish infeasibility.
    try:
        from .fixtures import estimate_room
        claims={}
        for r in rooms:
            recommendation=estimate_room(program,r).get('recommended_area',0)
            if recommendation>rooms[r]['preferred']:
                claim=min(rooms[r]['max'],math.ceil(math.sqrt(recommendation))**2)
                allocation[r]=max(allocation[r],claim)
                claims[r]=max(rooms[r]['min'],min(rooms[r]['max'],recommendation))
        # A square is a convenient equipment proposal, not a hard area bound.
        # Donate its rounding margin before rejecting a smaller requested size.
        while sum(allocation.values())>target:
            slack=[r for r in claims if allocation[r]>claims[r]]
            if not slack:
                break
            chosen=max(slack,key=lambda r:(allocation[r]-claims[r],r))
            allocation[chosen]-=1
        if sum(allocation.values())>target:
            return None,bounds,'Current equipment packing claims exceed requested usable-floor capacity.'
    except ImportError:
        pass
    preferred = {r: min(v['max'], v['preferred'] * circulation_boost)
                 if 'department_circulation' in program.spaces[r]['node'].get('tags', [])
                 else v['preferred'] for r, v in rooms.items()}
    rng = rng_for(seed, department + ':allocation')
    ties = {r: rng.random() for r in rooms}
    circles=[r for r in rooms if 'department_circulation' in program.spaces[r]['node'].get('tags',[])]
    while sum(allocation[r] for r in circles)<circulation_floor:
        options=[r for r in circles if allocation[r]<rooms[r]['max']]
        if not options or sum(allocation.values())>=target:
            return None,bounds,'Current circulation reservation exceeds this allocation capacity.'
        chosen=max(options,key=lambda r:((preferred[r]-allocation[r])/max(1,preferred[r]),ties[r],r))
        allocation[chosen]+=1
    while sum(allocation.values()) < target:
        candidates = [r for r in rooms if allocation[r] < rooms[r]['max']]
        if circulation_ceiling is not None and sum(allocation[r] for r in circles)>=max(circulation_ceiling,circulation_floor):
            alternatives=[r for r in candidates if r not in circles]
            if alternatives:
                candidates=alternatives
        if not candidates:
            return None, bounds, 'Room maximums leave no space for requested aggregate area.'
        # Normalized marginal pressure keeps small rooms relevant; explicit
        # target is conserved while local preferred values remain unmodified.
        chosen = max(candidates, key=lambda r: ((preferred[r] - allocation[r]) / max(1, preferred[r]), ties[r], r))
        allocation[chosen] += 1
    for node in program.descendants(department):
        descendants = set(program.descendants(node, True))
        total = sum(v for r, v in allocation.items() if r in descendants)
        low, high = _limits(program.spaces[node], 'area.aggregate')
        if not low <= total <= high:
            return None, bounds, 'Selected allocation violates a nested aggregate interval.'
    return allocation, bounds, None


def _exact_shape(area, width):
    height = math.ceil(area / width)
    cells = rect(0, 0, width, height)
    # The opposite frontier remains a continuous service/door run. A partial
    # last row is an intentional coherent notch, not arbitrary pixel noise.
    for x in range(width - (width * height - area), width):
        cells.discard((x, 0))
    return cells


def _shape_offers(program, room, target, seed, maximum=8):
    space = program.spaces[room]
    short_low, short_high = _limits(space, 'shape.short_side', 1, 10**8)
    ratio_low, ratio_high = _limits(space, 'shape.aspect_ratio', 1, 10**8)
    if not _metric(space, 'shape.aspect_ratio'):
        ratio_high = 3
    fill_low, _ = _limits(space, 'shape.fill_ratio', 0, 1)
    width_min = max([1] + [r['parameters'].get('minWidth', 1) for r in program.relationships
                          if r['type'] == 'route' and room in (r['source'], r['target'])
                          and r['parameters'].get('mode') == 'crew'])
    shapes = []
    rng = rng_for(seed, room + ':shape')
    preferred_ratio = rng.uniform(2.4, min(3, ratio_high)) if 'department_circulation' in space['node'].get('tags', []) else rng.uniform(ratio_low, math.sqrt(ratio_low*ratio_high))
    machine_dimensions=(1,1)
    machine_run=0
    try:
        from .fixtures import estimate_room
        estimate=estimate_room(program,room)
        machine_dimensions=estimate.get('minimum_dimensions',(1,1))
        if estimate.get('recommended_area',0)>area_bounds(space)[1]:
            preferred_ratio=1
        elif min(machine_dimensions)>=3 and estimate.get('blocked_area',0)>=9:
            # Reserve a working approach beside a native multi-tile machine.
            # This is a candidate shape claim, not a stronger authored bound.
            machine_run=max(machine_dimensions)+width_min
            preferred_ratio=max(1,machine_run/min(machine_dimensions))
    except ImportError:
        pass
    for width in range(width_min, target + 1):
        height = math.ceil(target / width)
        if height < width_min:
            continue
        short, long = min(width, height), max(width, height)
        if short<min(machine_dimensions) or long<max(max(machine_dimensions),machine_run):
            continue
        if not short_low <= short <= short_high or not ratio_low <= long / short <= ratio_high:
            continue
        if target / (width * height) < fill_low:
            continue
        mask = _exact_shape(target, width)
        if len(components(mask)) != 1:
            continue
        score = abs(math.log((long / short) / preferred_ratio)) + (1-target/(width*height))
        shapes.append((score, width, mask))
    if not shapes:
        return []
    shapes.sort(key=lambda x: (x[0], x[1]))
    return [mask for _, _, mask in shapes[:maximum]]


def _shift(cells, dx, dy):
    return {(x + dx, y + dy) for x, y in cells}


def _block_shift(block, dx, dy):
    return {'floors': {k: _shift(v, dx, dy) for k, v in block['floors'].items()},
            'envelopes': {k: _shift(v, dx, dy) for k, v in block['envelopes'].items()}}


def _block_cells(block):
    return set().union(*block['floors'].values()) if block['floors'] else set()


def _contained_width(program, child, parent):
    return max([1]+[edge['parameters'].get('minWidth',1) for edge in program.relationships
                    if edge['type']=='route' and edge.get('priority')=='mandatory'
                    and edge['parameters'].get('mode')=='crew'
                    and {edge['source'],edge['target']}=={child,parent}])


def _blocks(program, room, allocation, seed, maximum=8):
    children = [c for c in program.spaces[room]['children'] if c in allocation
                and program.spaces[c]['node']['placement'].get('nesting') == 'contained']
    own_shapes = _shape_offers(program, room, allocation[room], seed, maximum * 3)
    if not children:
        return [{'floors': {room: mask}, 'envelopes': {room: mask | outline(mask)}} for mask in own_shapes[:maximum]]
    child_offers = {c: _blocks(program, c, allocation, seed, maximum) for c in children}
    if any(not offers for offers in child_offers.values()):
        return []
    results = []
    for variant in range(maximum):
        children_blocks = [child_offers[c][variant % len(child_offers[c])] for c in children]
        total_width = sum(bbox(_block_cells(b))[2] - bbox(_block_cells(b))[0] for b in children_blocks) + len(children) - 1
        compatible = [m for m in own_shapes if bbox(m)[2] >= total_width]
        if not compatible:
            continue
        own = compatible[variant % len(compatible)]
        parent_top = bbox(own)[3]
        # Keep the complete two-row working frontier beside nested rooms.
        # A partial last row otherwise leaves a child with no physical door.
        own={(x,parent_top-y-1) for x,y in own}
        floors, envelopes = {room: set(own)}, {}
        x = 0
        for b in children_blocks:
            x0, y0, x1, y1 = bbox(_block_cells(b))
            moved = _block_shift(b, x - x0, parent_top + 1 - y0)
            floors.update(moved['floors'])
            envelopes.update(moved['envelopes'])
            x += x1 - x0 + 1
        if any(not any(len(run)>=_contained_width(program,c,room) for _,run in seam_runs(floors[c],floors[room])) for c in children):
            continue
        union = set().union(*floors.values())
        envelopes[room] = union | outline(union)
        results.append({'floors': floors, 'envelopes': envelopes})
    return results


def _crew(program, profile):
    return 'crew_passage' in program.definitions.get('interfaces', {}).get(profile, {}).get('capabilities', [])


def _contact_graph(program, department, roots):
    owned = set(_physical(program, department))
    physical, walking = [], defaultdict(set)
    for edge in program.relationships:
        if edge['type'] != 'shared_boundary' or edge.get('priority') != 'mandatory':
            continue
        a, b = _root(program, edge['source'], owned), _root(program, edge['target'], owned)
        if a not in roots or b not in roots or a == b:
            continue
        physical.append({'a': a, 'b': b, 'profile': edge['parameters']['interface'],
                         'length': interface_minimum(program, edge['parameters']['interface'],
                                                     edge['parameters'].get('minLength', 1)),
                         'rule': edge['id'], 'authored': True})
        if _crew(program, edge['parameters']['interface']):
            walking[a].add(b); walking[b].add(a)
    circulation = sorted(r for r in roots if 'department_circulation' in program.spaces[r]['node'].get('tags', []))
    # Only an explicitly declared circulation room can own shared hall floor.
    # A room receiving many routes (for example a brig) remains a real suite.
    circulation.sort()
    for a, b in zip(circulation, circulation[1:]):
        profile = internal_profile(program, program.spaces[a]['node'].get('access'), False, room=a)
        physical.append({'a': a, 'b': b, 'profile': profile, 'length': 2, 'authored': False})
        walking[a].add(b); walking[b].add(a)
    components_list, remaining = [], set(roots) - set(circulation)
    while remaining:
        first = min(remaining); connected, todo = set(), [first]
        while todo:
            node = todo.pop()
            if node in connected:
                continue
            connected.add(node); todo.extend(walking[node] - connected - set(circulation))
        remaining -= connected
        components_list.append(connected)
    loads = {r: 0 for r in circulation}
    for component in sorted(components_list, key=lambda c: (-len(c), min(c))):
        if not circulation:
            break
        if any(walking[r] & set(circulation) for r in component):
            continue
        candidates = []
        for room in component:
            space = program.spaces[room]
            if space['node']['placement'].get('zone') == 'outboard':
                continue
            allowed = None
            for rule in space['requirements']:
                if rule['type'] == 'access_policy' and rule['parameters'].get('entryFrom'):
                    allowed = set(rule['parameters']['entryFrom']) if allowed is None else allowed & set(rule['parameters']['entryFrom'])
            for hall in circulation:
                if allowed is None or hall in allowed:
                    degree = sum(room in (e['a'], e['b']) for e in physical)
                    candidates.append((loads[hall], -degree, room, hall))
        if not candidates:
            continue
        _, _, room, hall = min(candidates)
        profile = internal_profile(program, program.spaces[room]['node'].get('access'), False, room=room)
        physical.append({'a': room, 'b': hall, 'profile': profile, 'length': 2, 'authored': False})
        loads[hall] += max(1, len(component))
    return physical, circulation


def _graph(roots, contacts):
    graph = {r: set() for r in roots}
    for edge in contacts:
        graph[edge['a']].add(edge['b']); graph[edge['b']].add(edge['a'])
    return graph


def _fits_contact(program, first, second, contact):
    if program.definitions.get('interfaces', {}).get(contact['profile'], {}).get('geometry') == 'open_edge':
        from .geometry import open_edge_runs
        return any(len(run) >= contact['length'] for _, run in open_edge_runs(first, second))
    return any(len(run) >= contact['length'] for _, run in seam_runs(first, second))


def _embed(program, department, allocation, roots, contacts, circulation, aspect, seed, budget, keep_front=False):
    graph = _graph(roots, contacts)
    rng = rng_for(seed, department + ':embedding')
    offers = {r: _blocks(program, r, allocation, seed, 8) for r in roots}
    if any(not value for value in offers.values()):
        return None, {'reason': 'No exact shape fits requested room area/contained children.',
                      'rooms': [r for r, value in offers.items() if not value]}
    first = circulation[seed % len(circulation)] if circulation else max(roots, key=lambda r: (len(graph[r]), allocation[r], r))
    # The seed already varies shape preference and module order. Starting from
    # a low-ranked mask as well needlessly creates thin, poorly filled rooms.
    initial_index=0
    try:
        from .fixtures import estimate_room
        if estimate_room(program,first).get('recommended_area',0)>area_bounds(program.spaces[first])[1]:
            initial_index=0
    except ImportError:
        pass
    initial = offers[first][initial_index]
    result = copy.deepcopy(initial)
    placed, pending = {first}, set(roots) - {first}
    trace = []
    while pending:
        room = max(pending, key=lambda r: (len(graph[r] & placed), len(graph[r]), allocation[r], r))
        anchors = sorted(graph[room] & placed) or sorted(placed)
        candidates = []
        occupied = _block_cells(result)
        for offer_index, block in enumerate(offers[room]):
            x0, y0, x1, y1 = bbox(_block_cells(block)); width, height = x1 - x0, y1 - y0
            positions = set()
            for neighbor in anchors:
                ax0, ay0, ax1, ay1 = bbox(result['floors'][neighbor])
                for nx, ny in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    if nx:
                        xx = ax1 + 1 if nx > 0 else ax0 - width - 1
                        for yy in range(ay0 - height + 2, ay1 - 1):
                            positions.add((xx - x0, yy - y0))
                    else:
                        yy = ay1 + 1 if ny > 0 else ay0 - height - 1
                        for xx in range(ax0 - width + 2, ax1 - 1):
                            positions.add((xx - x0, yy - y0))
            ordered = sorted(positions)
            rng.shuffle(ordered)
            for dx, dy in ordered:
                budget.take()
                moved = _block_shift(block, dx, dy)
                cells = _block_cells(moved)
                if keep_front and any(y < 0 for _, y in cells):
                    continue
                if cells & occupied or outline(cells) & occupied:
                    continue
                active = [e for e in contacts if room in (e['a'], e['b']) and (e['b'] if e['a'] == room else e['a']) in placed]
                if _plan_seams(program, {**result['floors'], **moved['floors']}, active) is None:
                    continue
                x, y, xx, yy = bbox(occupied | cells)
                ratio = (xx - x) / max(1, yy - y)
                score = (xx-x)*(yy-y) + len(occupied|cells) * .6 * abs(math.log(ratio/aspect))
                # Do not spend an entire hub wall on a two-wide door. A long
                # overhang can preserve frontage for several other local rooms.
                for e in active:
                    other = e['b'] if e['a'] == room else e['a']
                    if room in circulation or other in circulation:
                        length = max([0] + [len(run) for _, run in seam_runs(moved['floors'][room], result['floors'][other])])
                        score += 60 * max(0, length - e['length'])
                # Leave accessible wall capacity around high-degree hubs.
                score += .05 * (abs(dx) + abs(dy))
                if keep_front:
                    score += 80 * max(0, bbox(moved['floors'][room])[1])
                candidates.append((score, offer_index, dx, dy, moved))
        if not candidates:
            return None, {'reason': 'Contact embedding exhausted available local frontiers.', 'room': room,
                          'placed': sorted(placed), 'requiredNeighbors': sorted(graph[room] & placed)}
        candidates.sort(key=lambda c: (c[0], c[1], c[2], c[3]))
        choose = min(len(candidates)-1, seed % 3)
        _, _, dx, dy, moved = candidates[choose]
        result['floors'].update(moved['floors']); result['envelopes'].update(moved['envelopes'])
        placed.add(room); pending.remove(room)
        trace.append({'phase': 'department_contact_placement', 'scope': department,
                      'room': room, 'area': allocation[room], 'neighbors': sorted(graph[room] & placed),
                      'offset': [dx, dy]})
    return result, trace


def _walk_components(roots, contacts):
    graph = _graph(roots, contacts)
    remaining, result = set(roots), []
    while remaining:
        first = min(remaining); group, todo = set(), [first]
        while todo:
            r = todo.pop()
            if r in group:
                continue
            group.add(r); todo.extend(graph[r] - group)
        remaining -= group; result.append(group)
    return result


def _route_square(start, network, blocked, bounds, budget):
    """Two-wide private circulation routing; walls already have reserved cells."""
    from .geometry import square_anchors
    goals = square_anchors(network, 2)
    if not goals:
        return None
    x0,y0,x1,y1 = bounds
    cache = {}
    def valid(p):
        if p not in cache:
            x,y=p
            cache[p] = x0 <= x < x1-1 and y0 <= y < y1-1 and not (rect(x,y,2,2) & blocked)
        return cache[p]
    if not valid(start):
        return None
    queue = [(0, 0, start)]; scores={start:0}; parents={start:None}; serial=0
    while queue:
        _, _, p = heapq.heappop(queue)
        if p in goals:
            floor=set()
            while p is not None:
                floor.update(rect(p[0],p[1],2,2));p=parents[p]
            return floor
        for n in neighbors(p):
            if not valid(n):
                continue
            score=scores[p] + .1 + len(rect(n[0],n[1],2,2) - network)
            if score >= scores.get(n, math.inf):
                continue
            budget.take();scores[n]=score;parents[n]=p;serial+=1
            h=min(abs(n[0]-g[0])+abs(n[1]-g[1]) for g in goals)*.1
            heapq.heappush(queue,(score+h,serial,n))
    return None


def _gateway_candidates(floor, occupied, network, length=2):
    from .geometry import square_anchors
    candidates=[]
    anchors=square_anchors(network,2)
    for nx,ny in ((1,0),(-1,0),(0,1),(0,-1)):
        tx,ty=-ny,nx
        for x,y in floor:
            approach={(x+t*tx-d*nx,y+t*ty-d*ny) for t in range(length) for d in range(2)}
            if not approach <= floor:
                continue
            seam={(x+t*tx+nx,y+t*ty+ny) for t in range(length)}
            landing={(x+t*tx+d*nx,y+t*ty+d*ny) for t in range(max(2,length)) for d in (2,3)}
            if seam & occupied or landing & occupied:
                continue
            anchor=(min(p[0] for p in landing),min(p[1] for p in landing))
            distance=min(abs(anchor[0]-g[0])+abs(anchor[1]-g[1]) for g in anchors)
            candidates.append((distance,anchor,(nx,ny),seam,landing))
    return sorted(candidates,key=lambda c:(c[0],c[1],c[2]))


def _partition_circulation(program, network, circulation, allocation, anchors, seed):
    """Connected exact allocations, searching both sides of each potential cut."""
    def fits(key,cells):
        x0,y0,x1,y1=bbox(cells)
        lo,hi=_limits(program.spaces[key],'shape.aspect_ratio',1,10**8)
        return bool(cells) and len(components(cells))==1 and lo<=max(x1-x0,y1-y0)/max(1,min(x1-x0,y1-y0))<=hi
    def solve(keys,remaining):
        key=keys[0];target=allocation[key]
        required=set().union(*(a for room,a in anchors if room==key))
        forbidden=set().union(*(a for room,a in anchors if room in keys[1:]))
        if len(keys)==1:
            return {key:remaining} if len(remaining)==target and required<=remaining and fits(key,remaining) else None
        # A fixed doorway in the middle of a thin hall is a claim on ownership,
        # not necessarily a good growth seed. Growing outward from it can split
        # the unallocated remainder before reaching the owner's area budget.
        # Also grow from both ends, keeping that same doorway claim mandatory.
        seeds=list(dict.fromkeys(sorted(required)[:2]+[
            min(remaining,key=lambda p:(p[0],p[1])),max(remaining,key=lambda p:(p[0],p[1])),
            min(remaining,key=lambda p:(p[1],p[0])),max(remaining,key=lambda p:(p[1],p[0]))]))
        seeds=[p for p in seeds if p not in forbidden]
        for first in seeds:
            for order in range(3):
                selected={first};rest=remaining-selected
                while len(selected)<target:
                    frontier=({n for c in selected for n in neighbors(c)} & rest)-forbidden
                    def score(c):
                        required_distance=min([abs(c[0]-r[0])+abs(c[1]-r[1]) for r in required-selected] or [0])
                        if order==0:distance=(c[0]-first[0])**2+(c[1]-first[1])**2
                        elif order==1:distance=abs(c[0]-first[0])*3+abs(c[1]-first[1])
                        else:distance=abs(c[1]-first[1])*3+abs(c[0]-first[0])
                        return required_distance,distance,-sum(n in selected for n in neighbors(c)),c
                    chosen=next((c for c in sorted(frontier,key=score) if len(components(rest-{c}))<=1),None)
                    if chosen is None:break
                    selected.add(chosen);rest.remove(chosen)
                if len(selected)==target and required<=selected and fits(key,selected):
                    tail=solve(keys[1:],rest)
                    if tail is not None:return {key:selected,**tail}
        return None
    return solve(circulation,set(network))


def _permits_actor(program, profile, actor):
    definition=program.definitions.get('interfaces',{}).get(profile,{})
    clauses=definition.get('access',{}).get('anyOf',[])
    levels=set(program.definitions.get('accessProfiles',{}).get(actor,{}).get('levels',[]))
    return bool(definition) and (not clauses or any(set(c)<=levels for c in clauses))


def _entry_profile(program, room, component=None):
    """A gateway carries the actors required by its local route contracts.

    Choose only declared actor/door policies; shared Maintenance credentials
    must never accidentally replace the department credential boundary.
    """
    preferred=internal_profile(program,program.spaces[room]['node'].get('access'),room=room)
    if any(r['type']=='x_entry_interface_policy' for r in program.spaces[room]['requirements']):
        return preferred
    members=set(component or [room])
    actors={program.spaces[room]['node'].get('access')}
    for edge in program.relationships:
        if edge['type']=='route' and edge.get('priority')=='mandatory' and edge['parameters'].get('mode')=='crew':
            if (edge['source'] in members)!=(edge['target'] in members):
                actors.add(edge['parameters'].get('accessProfile','public'))
    actors.discard(None)
    def permits(profile, actor):
        return _permits_actor(program,profile,actor)
    if all(permits(preferred,a) for a in actors):return preferred
    candidates={internal_profile(program,a,public=a=='public') for a in actors}
    compatible=[p for p in candidates if p is not None
                and program.definitions['interfaces'][p].get('passage',{}).get('direction','bidirectional')=='bidirectional'
                and all(permits(p,a) for a in actors)]
    if not compatible:return preferred
    return min(compatible,key=lambda p:(sum(permits(p,a) for a in program.definitions.get('accessProfiles',{})),p))


def _module_proposal(program, department, allocation, core, circulation, aspect, seed, budget, family="split_bays", corridor_width=None, suite_alternatives=3):
    """Place real suites first, then fit authored circulation into reserved gaps."""
    room_roots=[r for r in core if r not in circulation]
    if not room_roots:
        contacts,_=_contact_graph(program,department,core)
        return _distributed_proposal(program,department,allocation,core,contacts,aspect,seed,budget)
    authored=[]
    for e in program.relationships:
        if e['type']=='shared_boundary' and e.get('priority')=='mandatory' and e['source'] in room_roots and e['target'] in room_roots:
            authored.append({'a':e['source'],'b':e['target'],'profile':e['parameters']['interface'],
                             'length':interface_minimum(program,e['parameters']['interface'],e['parameters'].get('minLength',1)),
                             'rule':e['id'],'authored':True})
    groups=_walk_components(room_roots,authored)
    modules=[];trace=[]
    for group in groups:
        edges=[e for e in authored if e['a'] in group and e['b'] in group]
        for local_attempt in range(suite_alternatives):
            block,detail=_embed(program,department,allocation,sorted(group),edges,[],1,
                                seed+local_attempt*104729,budget,True)
            if block is not None:
                break
        if block is None:
            return None,{'reason':'Mandatory suite could not preserve a staff frontier.','detail':detail}
        x0,y0,x1,y1=bbox(_block_cells(block));block=_block_shift(block,-x0,-y0)
        # A suite can rotate as one rigid local solution. Prefer the shorter
        # frontage so many independent rooms do not consume an enormous
        # circulation ribbon; contained relationships rotate with their owner.
        _,_,w,h=bbox(_block_cells(block))
        if w>h:
            block={'floors':{k:{(y,x) for x,y in v} for k,v in block['floors'].items()},
                   'envelopes':{k:{(y,x) for x,y in v} for k,v in block['envelopes'].items()}}
        nested_public=[r for r in group if (program.spaces[r]['node'].get('access')=='public'
                       and any(program.spaces[c]['node']['placement'].get('nesting')=='contained'
                               for c in program.spaces[r]['children']))
                       or any(e['type']=='shared_boundary' and e.get('priority')=='mandatory'
                              and e['source']==r and e['target'] not in program.spaces for e in program.relationships)]
        if nested_public:
            # Put the demanded endpoint on the outside of its suite. Nested
            # children or a staff office must not hide the public frontage.
            variants=[];variant=block
            for turn in range(4):
                xx,yy,ww,hh=bbox(_block_cells(variant))
                host=set().union(*(variant['floors'][r] for r in nested_public))
                score=sum(y for x,y in host)/len(host)/max(1,hh)
                variants.append((score,-ww,turn,variant))
                variant={'floors':{k:{(hh-y-1,x) for x,y in v} for k,v in variant['floors'].items()},
                         'envelopes':{k:{(hh-y-1,x) for x,y in v} for k,v in variant['envelopes'].items()}}
            block=max(variants,key=lambda v:v[:3])[3]
        block['roots']=sorted(group);modules.append(block);trace.extend(detail)
    rng=rng_for(seed,department+':module_order');rng.shuffle(modules)
    modules.sort(key=lambda b:-max(bbox(_block_cells(b))[2:]))
    terminal=None
    if family=='central_cap' and len(modules)>2:
        terminal=max(modules,key=lambda b:len(_block_cells(b)))
        modules.remove(terminal)
    bay_count=2 if family=='split_bays' and len(circulation)>1 and len(modules)>=7 else 1
    private_width=corridor_width or (3 if bay_count==1 else 2)
    rows=[[] for _ in range(bay_count*2)];loads=[0 for _ in rows]
    def needs_direct_external(roots):
        for edge in program.relationships:
            if edge['source'] not in roots or edge['target'] in program.spaces:
                continue
            if edge['type']=='shared_boundary' and edge.get('priority')=='mandatory':
                return True
            if edge['type']!='route' or edge['parameters'].get('mode')!='crew':
                continue
            actor=edge['parameters'].get('accessProfile','public')
            levels=set(program.definitions.get('accessProfiles',{}).get(actor,{}).get('levels',[]))
            for hall in circulation:
                profile=internal_profile(program,room=hall)
                clauses=program.definitions.get('interfaces',{}).get(profile,{}).get('access',{}).get('anyOf',[])
                if clauses and not any(set(c)<=levels for c in clauses):return True
        return False
    for b in modules:
        exposed=needs_direct_external(b['roots']) or any(program.spaces[r]['node'].get('access')=='public'
                    or program.spaces[r]['node']['placement'].get('zone')=='hull'
                    or any(q['type'] in ('docking','perimeter') for q in program.spaces[r]['requirements'])
                    for r in b['roots'])
        choices=[1,2*(bay_count-1)] if exposed else list(range(len(rows)))
        row=min(choices,key=lambda i:loads[i]);rows[row].append(b);loads[row]+=bbox(_block_cells(b))[2]+1
    floors={};envelopes={};extent=[];network=set();bay_origins=[];previous_top=None
    for bay in range(bay_count):
        below_height=max([0]+[bbox(_block_cells(b))[3] for b in rows[2*bay+1]])
        base=0 if previous_top is None else previous_top+below_height+3
        bay_origins.append(base);local_extents=[]
        for side in range(2):
            cursor=0
            # Nested public destinations need an exterior side of their host,
            # not merely an exposed child. An end position supplies that side.
            rows[2*bay+side].sort(key=lambda b:not (needs_direct_external(b['roots']) or any(program.spaces[r]['node'].get('access')=='public'
                and any(program.spaces[c]['node']['placement'].get('nesting')=='contained' for c in program.spaces[r]['children'])
                for r in b['roots'])))
            for b in rows[2*bay+side]:
                x0,y0,x1,y1=bbox(_block_cells(b))
                if side:
                    b={'floors':{k:{(x,-y-1) for x,y in v} for k,v in b['floors'].items()},
                       'envelopes':{k:{(x,-y-1) for x,y in v} for k,v in b['envelopes'].items()}}
                    moved=_block_shift(b,cursor,base-1)
                else:
                    moved=_block_shift(b,cursor,base+private_width+1)
                # Pack the actual suite outline, not its empty bounding box.
                # Unequal rooms inside a functional suite leave useful recesses
                # that a later module can occupy while retaining a wall gap.
                occupied_before=set().union(*floors.values()) if floors else set()
                while bbox(_block_cells(moved))[0]>0:
                    trial=_block_shift(moved,-1,0)
                    trial_cells=_block_cells(trial)
                    if (trial_cells|outline(trial_cells)) & occupied_before:
                        break
                    moved=trial
                floors.update(moved['floors']);envelopes.update(moved['envelopes']);local_extents.append(bbox(_block_cells(moved)))
                cursor=max(cursor,bbox(_block_cells(moved))[2]+1)
        extent.extend(local_extents)
        length=max(x1 for x0,y0,x1,y1 in local_extents)
        # The outer connecting trunk belongs only to a split-bay proposal.
        # A single shared hall needs no empty three-tile tail beside every room.
        external_targets={e['target'] for e in program.relationships
                          if e['type']=='route' and e['source'] in circulation
                          and e['target'] not in program.spaces
                          and e['parameters'].get('mode')=='crew'}
        # Public and maintenance approaches need separate exposed capacity even
        # when a hall has multiple ownership allocations. Splitting its floor
        # does not create another physical frontier through the room banks.
        port_tail=len(external_targets)>1
        start=-3 if bay_count>1 or port_tail else 0
        network|=rect(start,base,length-start,private_width)
        previous_top=max(y1 for x0,y0,x1,y1 in local_extents)
    if len(bay_origins)>1:
        network|=rect(-3,min(bay_origins),2,max(bay_origins)-min(bay_origins)+private_width)
    if terminal is not None:
        # A large functional suite can close the end of a short shared hall.
        # Its depth then overlaps both room banks instead of extending a wing.
        root=max(terminal['roots'],key=lambda r:(sum(r in (e['a'],e['b']) for e in authored),len(terminal['floors'][r])))
        variants=[];variant=terminal
        for turn in range(4):
            _,_,width,height=bbox(_block_cells(variant))
            ax,ay,bx,by=bbox(variant['floors'][root])
            variants.append(((ax,width,abs((ay+by)/2-height/2)),turn,variant))
            variant={'floors':{k:{(height-y-1,x) for x,y in v} for k,v in variant['floors'].items()},
                     'envelopes':{k:{(height-y-1,x) for x,y in v} for k,v in variant['envelopes'].items()}}
        terminal=min(variants,key=lambda v:v[:2])[2]
        _,ry,_,ry1=bbox(terminal['floors'][root])
        right=max(x1 for x0,y0,x1,y1 in extent)
        dy=bay_origins[0]+(private_width-(ry1-ry))//2-ry
        moved=_block_shift(terminal,right+1,dy)
        floors.update(moved['floors']);envelopes.update(moved['envelopes'])
        extent.append(bbox(_block_cells(moved)))
    occupied=set().union(*floors.values());blocked=occupied|outline(occupied)
    x0,y0,x1,y1=bbox(occupied);routing_bounds=(x0-5,y0-5,x1+5,y1+5)
    contacts=list(authored);required_anchors=[];gateways=[]
    # A narrow or directed gate satisfies its own local transition, but cannot
    # stand in for the ordinary two-wide staff connection to owned circulation.
    # Keep the physical suite intact while giving these staff components their
    # own policy-compatible circulation gateways.
    walking=[e for e in authored if _crew(program,e['profile']) and e['length']>=2
             and program.definitions['interfaces'][e['profile']].get('passage',{}).get('direction','bidirectional')=='bidirectional']
    jobs=[]
    for group in _walk_components(room_roots,walking):
        explicit=[e for e in program.relationships if e['type']=='shared_boundary'
                  and e.get('priority')=='mandatory' and e['source'] in group and e['target'] in circulation]
        jobs.extend((group,e,None) for e in explicit)
        obligations=[e for e in program.relationships if e['type']=='route' and e.get('priority')=='mandatory'
                     and e['parameters'].get('mode')=='crew' and e['source'] in group and e['target'] in circulation]
        for obligation in obligations:
            actor=obligation['parameters'].get('accessProfile','public')
            width=max(2,obligation['parameters'].get('minWidth',1))
            permitted=[e for e in walking if e['a'] in group and e['b'] in group
                       and e['length']>=width and _permits_actor(program,e['profile'],actor)]
            component=next(c for c in _walk_components(group,permitted) if obligation['source'] in c)
            covered=False
            for members,edge,job_actor in jobs:
                if edge:
                    definition=program.definitions['interfaces'][edge['parameters']['interface']]
                    length=interface_minimum(program,edge['parameters']['interface'],edge['parameters'].get('minLength',1))
                    covered |= (edge['source'] in component and length>=width
                                and definition.get('passage',{}).get('direction','bidirectional')=='bidirectional'
                                and _permits_actor(program,edge['parameters']['interface'],actor))
                elif members==component:
                    candidates=[r for r in members if len(members)==1 or not any(
                        q['type']=='access_policy' and not q['parameters'].get('throughTraffic',True)
                        for q in program.spaces[r]['requirements'])]
                    covered |= (job_actor==actor or bool(candidates) and all(
                        _permits_actor(program,_entry_profile(program,r,members),actor) for r in candidates))
            if not covered:
                jobs.append((component,None,actor))
        if not explicit and not obligations:
            jobs.append((group,None,None))
    gateway_cells=set()
    for group,required_edge,actor in jobs:
        eligible=[]
        demanded={r['source'] for r in program.relationships if r['type']=='route' and r['source'] in group
                  and r['target'] in circulation}
        if not required_edge and not demanded and all(program.spaces[r]['node'].get('access')=='public' for r in group):
            continue
        rooms=[required_edge['source']] if required_edge else group
        length=interface_minimum(program,required_edge['parameters']['interface'],required_edge['parameters'].get('minLength',1)) if required_edge else 2
        for room in rooms:
            policies=[r['parameters'] for r in program.spaces[room]['requirements'] if r['type']=='access_policy']
            allowed={required_edge['target']} if required_edge else set(circulation)
            for p in policies:
                if p.get('entryFrom'):
                    allowed&=set(p['entryFrom'])
            if not allowed:
                continue
            if len(group)>1 and any(not p.get('throughTraffic',True) for p in policies):
                continue
            if actor is not None and not _permits_actor(program,_entry_profile(program,room,group),actor):
                continue
            for candidate in [c for c in _gateway_candidates(floors[room],occupied,network,length)
                              if not c[3]&gateway_cells][:12]:
                eligible.append((candidate[0] + (0 if not demanded or room in demanded else 100),room,allowed,candidate))
        if not eligible:
            # Public/exterior-only destinations may have no owned-circulation
            # entry. An internal route obligation still requires a real gateway.
            needs=any(r['type']=='route' and r['source'] in group and r['target'] in program.spaces
                      and r['target'] in circulation for r in program.relationships)
            if needs or required_edge:
                return None,{'reason':'No policy-compatible owned-circulation gateway.','rooms':sorted(group)}
            continue
        found=None
        for _,room,allowed,candidate in sorted(eligible,key=lambda c:(c[0],c[1],c[3][1])):
            _,anchor,normal,seam,landing=candidate
            path=_route_square(anchor,network,blocked,routing_bounds,budget)
            if path is not None:
                found=(room,allowed,normal,seam,landing,path);break
        if found is None:
            return None,{'reason':'No clear two-wide circulation branch reaches a suite.','rooms':sorted(group)}
        room,allowed,normal,seam,landing,path=found;network|=path|landing
        gateway_cells.update(seam)
        gateways.append((room,allowed,normal,seam,landing,required_edge,group))
        if required_edge:
            required_anchors.append((required_edge['target'],landing))
    capacity=sum(allocation[r] for r in circulation)
    if len(network)>capacity:
        return None,{'reason':'Required two-wide departmental circulation exceeds allocated usable area.',
                     'needed':len(network),'allocated':capacity}
    while len(network)<capacity:
        candidates={n for c in network for n in neighbors(c)}-network-blocked
        if not candidates:
            return None,{'reason':'No compatible filler floor for circulation target.'}
        cx=sum(x for x,y in network)/len(network);cy=sum(y for x,y in network)/len(network)
        chosen=min(candidates,key=lambda c:(-sum(n in network for n in neighbors(c)),(c[0]-cx)**2+(c[1]-cy)**2,c))
        network.add(chosen)
    split=_partition_circulation(program,network,circulation,allocation,required_anchors,seed)
    if split is None:
        return None,{'reason':'Circulation cannot be partitioned into connected exact local budgets.'}
    floors.update(split)
    for r,c in split.items():envelopes[r]=c|outline(c)
    interfaces=[]
    open_profile=next((key for key,p in program.definitions['interfaces'].items()
                       if p.get('geometry')=='open_edge' and 'crew_passage' in p.get('capabilities',[])),None)
    if len(circulation)>1 and open_profile is None:
        return None,{'reason':'A declared open-edge interface is needed between circulation allocations.'}
    from .geometry import open_edge_runs
    for i,a in enumerate(circulation):
        for b in circulation[i+1:]:
            for normal,cells in open_edge_runs(split[a],split[b]):
                interfaces.append({'id':f'department_open:{len(interfaces)}','a':a,'b':b,'profile':open_profile,'cells':cells,'normal':normal})
    for room,allowed,normal,seam,landing,edge,group in gateways:
        nx,ny=normal
        owners={r for r in allowed if all((x+nx,y+ny) in split[r] for x,y in seam)}
        if not owners:
            return None,{'reason':'A circulation ownership cut crosses a required gateway.','room':room}
        owner=min(owners);profile=_entry_profile(program,room,group)
        if edge:profile=edge['parameters']['interface']
        interface={'id':f'department_door:{len(interfaces)}','a':room,'b':owner,'profile':profile,'cells':seam,'normal':normal}
        if edge:interface['rule']=edge['id']
        interfaces.append(interface)
    trace.append({'phase':'department_circulation','floorArea':len(network),'allocation':{r:allocation[r] for r in circulation},
                  'branches':len(gateways),'proposalFamily':family,'status':'realized'})
    return {'floors':floors,'envelopes':envelopes,'interfaces':interfaces,'contacts':contacts},trace


def _distributed_proposal(program, department, allocation, roots, contacts, aspect, seed, budget):
    """Public/distributed facilities retain real exposed approach lanes.

    Lanes are unassigned boundary capacity, not invented owned circulation floor.
    The department harness or later station coordinator supplies their owner.
    """
    if not roots:
        return {'floors':{},'envelopes':{}},[]
    groups=_walk_components(roots,contacts)
    blocks=[];trace=[]
    for group in groups:
        edges=[e for e in contacts if e['a'] in group and e['b'] in group]
        block,detail=_embed(program,department,allocation,sorted(group),edges,[],1,seed,budget,True)
        if block is None:return None,detail
        x,y,xx,yy=bbox(_block_cells(block));block=_block_shift(block,-x,-y)
        block['roots']=list(group);blocks.append(block);trace.extend(detail)
    rng=rng_for(seed,department+':distributed');rng.shuffle(blocks)
    blocks.sort(key=lambda b:any(q['type']=='docking' for r in b['roots'] for q in program.spaces[r]['requirements']))
    total=sum(len(_block_cells(b)) for b in blocks)
    row_width=max(max(bbox(_block_cells(b))[2] for b in blocks),math.ceil(math.sqrt(total*aspect)*1.25))
    result={'floors':{},'envelopes':{}};x=y=row_height=0
    for block in blocks:
        _,_,width,height=bbox(_block_cells(block))
        if x and x+width>row_width:
            x=0;y+=row_height+5;row_height=0
        # Docking modules are ends, keeping a real runway outside the footprint.
        docked=any(q['type']=='docking' for r in block['roots'] for q in program.spaces[r]['requirements'])
        if docked:
            x=max(x,bbox(_block_cells(result))[2]+5)
        moved=_block_shift(block,x,y)
        result['floors'].update(moved['floors']);result['envelopes'].update(moved['envelopes'])
        x+=width+1;row_height=max(row_height,height)
    trace.append({'phase':'distributed_department','laneClearance':4,'status':'reserved_unassigned_approach_capacity'})
    return result,trace


def _external_ports(state, department):
    floor = set().union(*state.floors.values()) if state.floors else set()
    result = []
    owned = set(state.program.descendants(department))
    for room, cells in state.floors.items():
        demands = [r['id'] for r in state.program.relationships
                   if (r['source'] == room or r['source'] in state.program.features and state.program.features[r['source']]['room'] == room)
                   and r['target'] not in owned and r['target'] not in state.program.features]
        if not demands:
            continue
        for nx, ny in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            frontier = {(x+nx, y+ny) for x, y in cells
                        if all((x+nx*t,y+ny*t) not in floor for t in (1, 2, 3, 4))}
            for run in components(frontier):
                if len(run) >= 1:
                    result.append({'room': room, 'normal': [nx, ny], 'cells': run,
                                   'requirementIds': demands, 'status': 'available_boundary_offer'})
    return result


def _proposal_frontage_failures(program, department, block, contacts):
    """Test actual authored hull interfaces before preferring a compact shape."""
    from .exterior import realize_exterior
    from .verify import Verifier
    temporary=LayoutState(program)
    temporary.floors={k:set(v) for k,v in block['floors'].items()}
    temporary.interfaces=copy.deepcopy(block.get('interfaces',[]))
    _seams(temporary,block.get('contacts',contacts))
    for room in temporary.floors:
        node=program.spaces[room]
        if node['node']['placement'].get('nesting')=='contained':
            _seam(temporary,room,node['parent'],internal_profile(program,room=room),
                  _contained_width(program,room,node['parent']))
    rebuild_walls(temporary)
    realize_exterior(temporary,department)
    all_cells=set().union(*temporary.floors.values(),temporary.walls,
                         *(r['cells'] for r in temporary.reservations))
    x,y,xx,yy=bbox(all_cells);temporary.bounds=(x-4,y-4,xx+4,yy+4)
    checker=Verifier(temporary)
    for room in temporary.floors:
        for rule in program.spaces[room]['requirements']:
            if rule['type'] in ('perimeter','docking') and rule.get('priority')=='mandatory':
                checker.node_requirement(room,rule)
    return [check['id'] for check in checker.checks if check['status']=='violated']


def pack_department(program, department_id, target_area=None, aspect=1, seed=1, settings=None):
    """Realize one selected department, retaining its complete local program.

    Requested aggregate floor is an exact allocation, not a percentage applied to
    existing geometry. Hard bounds are never scaled. Failed search returns a
    diagnostic state with a truthful status; no rooms are silently dropped.
    """
    settings = {**DEFAULTS, **(settings or {})}
    if department_id not in program.spaces or program.spaces[department_id]['kind'] != 'department':
        raise ValueError('department_id must name a selected department instance')
    if not isinstance(aspect, (int, float)) or isinstance(aspect, bool) or not math.isfinite(aspect) or aspect <= 0:
        raise ValueError('aspect must be a positive finite number')
    bounds = department_area_bounds(program, department_id)
    target = bounds['preferred'] if target_area is None else target_area
    if type(target) is not int or target < 1:
        raise ValueError('target_area must be a positive integer')
    state = LayoutState(program)
    if hasattr(program,'placement_scope'):
        state.metadata['placementScope']=copy.deepcopy(program.placement_scope)
    state.metadata.update({'department': department_id, 'requested_area': target, 'requested_aspect': aspect,
                           'area_bounds': bounds, 'algorithm': 'department_contact_growth_v1', 'seed': seed})
    physical = _physical(program, department_id)
    owned = set(physical)
    roots = [r for r in physical if _root(program, r, owned) == r]
    core = [r for r in roots if program.spaces[r]['node']['placement'].get('zone') != 'outboard']
    outboard = [r for r in roots if r not in core]
    contacts, circulation = _contact_graph(program, department_id, core)
    budget = Budget(settings['max_proposals'])
    chosen, allocation = None, None
    circulation_requirements=defaultdict(int)
    candidates=[]
    try:
        per_family=max(4,settings.get('parent_alternatives',4)*2)
        families=('central_cap','central_corridor','split_bays')
        attempts=per_family*len(families)
        for attempt in range(attempts):
            family=families[attempt//per_family]
            private_width=(2 if attempt%2==0 else 3) if family!='split_bays' else 2
            family_key=(family,private_width)
            circulation_required=circulation_requirements[family_key]
            boost=1 if family!='split_bays' else 1+(attempt%per_family)*.5
            from .quality import quality_policy
            kind=getattr(program,'placement_scope',{}).get('kind','department')
            fractional_targets=[r['max'] for r in quality_policy()['profiles'][kind]
                                if r['metric']=='circulation.fraction' and 'max' in r]
            ceiling=math.floor(target*min(fractional_targets)) if fractional_targets and family!='split_bays' else None
            allocation, _, error = _allocation(program, department_id, target, seed,
                                                circulation_boost=boost,
                                                circulation_floor=circulation_required,
                                                circulation_ceiling=ceiling)
            if error:
                if target<bounds['min'] or target>bounds['max']:
                    state.metadata.update({'termination': 'infeasible_bounds', 'reason': error, 'work_used': budget.used})
                    return state
                circulation_requirements[family_key]=0
                state.trace.append({'phase':'department_reallocation','attempt':attempt,'reason':error})
                continue
            try:
                if circulation:
                    block, trace = _module_proposal(program, department_id, allocation, core, circulation,
                                                   aspect, seed + attempt, budget, family=family, corridor_width=private_width,
                                                   suite_alternatives=settings['suite_alternatives'])
                else:
                    block, trace = _distributed_proposal(program, department_id, allocation, core, contacts,
                                                        aspect, seed + attempt, budget)
            except SearchBudget:
                if not candidates:
                    raise
                state.trace.append({'phase':'department_candidate_search','status':'budget_exhausted',
                                    'retainedCompleteCandidates':len(candidates)})
                break
            if block is not None:
                cells=_block_cells(block);x,y,xx,yy=bbox(cells)
                width,height=xx-x,yy-y
                orientation_cost=min(abs(math.log((width/height)/aspect)),abs(math.log((height/width)/aspect))) if width and height else 0
                hall_fraction=sum(allocation[r] for r in circulation)/max(1,len(cells))
                from .quality import layout_quality
                specimen=LayoutState(program,floors=block['floors'])
                specimen.metadata['placementScope']=copy.deepcopy(getattr(program,'placement_scope',{}))
                quality=layout_quality(specimen,department_id)
                # Configured quality penalties lead; geometric tie breakers do
                # not turn a relaxed profile into an additional hard constraint.
                score=(quality['score'],orientation_cost,width*height/max(1,len(cells)),hall_fraction)
                frontage_failures=_proposal_frontage_failures(program,department_id,block,contacts)
                candidates.append(((len(frontage_failures),score),attempt,block,allocation.copy(),trace))
                state.trace.append({'phase':'department_candidate_assessment','attempt':attempt,'family':family,
                    'qualityScore':quality['score'],'qualityMetrics':quality['metrics'],'geometryScore':score,'frontageFailures':frontage_failures,'bbox':[width,height],
                    'circulationFraction':hall_fraction})
                if not circulation or (quality['score']==0 and not frontage_failures):break
                continue
            if 'needed' in trace:
                circulation_requirements[family_key]=max(circulation_required,trace['needed'])
            state.trace.append({'phase': 'department_repack', 'attempt': attempt, 'scope': department_id, **trace})
        if candidates:
            quality,attempt,chosen,allocation,trace=min(candidates,key=lambda c:c[:2])
            state.trace.extend(trace)
            state.trace.append({'phase':'department_candidate_quality','candidates':len(candidates),'selectedAttempt':attempt,'frontageFailures':quality[0],'score':quality[1]})
        if chosen is None:
            state.metadata.update({'termination': 'search_exhausted', 'reason': 'No bounded contact arrangement was found.', 'work_used': budget.used})
            return state
        state.floors.update(chosen['floors']); state.envelopes.update(chosen['envelopes'])
        state.interfaces.extend(chosen.get('interfaces',[]))
        contacts=chosen.get('contacts',contacts)
        # Detached exterior machinery is explicit; it is never painted into an
        # occupied department interior merely to reach an aggregate-area target.
        extent = bbox(_block_cells(chosen)); cursor = extent[1]
        for room in outboard:
            offers = _blocks(program, room, allocation, seed, 4)
            if not offers:
                state.metadata.update({'termination':'search_exhausted','work_used':budget.used,
                    'reason':'No exact outboard room shape preserves its selected requirements.','failed_room':room})
                return state
            block = offers[0]; x0, y0, x1, y1 = bbox(_block_cells(block))
            moved = _block_shift(block, extent[2] + 5 - x0, cursor - y0)
            state.floors.update(moved['floors']); state.envelopes.update(moved['envelopes'])
            cursor += y1 - y0 + 4
        _seams(state, contacts)
        for room in physical:
            space = program.spaces[room]
            if space['node']['placement'].get('nesting') == 'contained':
                profile = internal_profile(program, space['node'].get('access'), space['node'].get('access') == 'public',room=room)
                _seam(state, room, space['parent'], profile, _contained_width(program,room,space['parent']))
        # Aspect is a parent-level orientation pressure. Rotate the complete
        # local solution, preserving every owned mask, seam and nested envelope.
        x0,y0,x1,y1=bbox(state.aggregate(department_id))
        ratio=(x1-x0)/max(1,y1-y0)
        if abs(math.log((1/ratio)/aspect))<abs(math.log(ratio/aspect))-1e-9:
            state.floors={k:{(y,x) for x,y in cells} for k,cells in state.floors.items()}
            state.envelopes={k:{(y,x) for x,y in cells} for k,cells in state.envelopes.items()}
            for interface in state.interfaces:
                interface['cells']={(y,x) for x,y in interface['cells']}
                nx,ny=interface['normal'];interface['normal']=(ny,nx)
            state.trace.append({'phase':'department_orientation','aspect':aspect,'transposed':True})
        rebuild_walls(state)
        envelope = state.aggregate(department_id)
        state.envelopes[department_id] = envelope | outline(envelope)
        state.metadata['external_ports'] = _external_ports(state, department_id)
        everything = envelope | state.walls | {c for r in state.reservations for c in r['cells']}
        x, y, xx, yy = bbox(everything)
        state.bounds = (x-4,y-4,xx+4,yy+4)
        state.metadata.update({'termination': 'completed', 'allocated_areas': allocation,
                                'actual_area': len(envelope), 'work_used': budget.used,
                               'actual_aspect': (bbox(envelope)[2]-bbox(envelope)[0])/max(1,bbox(envelope)[3]-bbox(envelope)[1]),
                               'aspect_policy': 'soft_shape_and_orientation_preference',
                               'aspect_cost': abs(math.log(((bbox(envelope)[2]-bbox(envelope)[0])/max(1,bbox(envelope)[3]-bbox(envelope)[1]))/aspect)),
                               'internal_contacts': contacts})
    except SearchBudget:
        state.metadata.update({'termination': 'budget_exhausted', 'work_used': budget.used,
                               'reason': 'Department search budget exhausted; not an infeasibility proof.'})
    return state
