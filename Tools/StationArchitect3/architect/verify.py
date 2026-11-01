"""Independent checks against realized tiles; solver scores are never evidence.

This verifier certifies the supported layout predicates only. Native prototype
geometry, utility operation and extension semantics remain explicit unknowns.
"""
from __future__ import annotations

from collections import deque
import math

from .geometry import (DIRECTIONS, bbox, components, convex_hull_area,
                       covered_by_squares, docking_cells, docking_exterior_lane, flood_exterior,
                       neighbors, rect, shortest_path, square_anchors)

METRICS = {'area.exclusive', 'area.aggregate', 'shape.aspect_ratio', 'core.components',
           'shape.short_side',
           'shape.fill_ratio', 'shape.components', 'corridor.clear_width',
           'maintenance.wrap_fraction'}
NODE_TYPES = {'metric', 'perimeter', 'access_policy', 'docking'}
EDGE_TYPES = {'shared_boundary', 'route', 'separation', 'grid_relation', 'dependency'}


def _unsupported(rule):
    kind = rule['type']
    p = rule.get('parameters', {})
    if kind == 'metric' and p.get('metric') not in METRICS:
        return 'No evaluator for this metric.'
    if kind == 'route' and p.get('mode') == 'eva':
        return 'EVA routing and hazard survival are not implemented.'
    if kind == 'grid_relation' and p.get('bridgeAssembly'):
        return 'Native grid bridge realization is not implemented.'
    if kind not in NODE_TYPES | EDGE_TYPES:
        return 'No implemented evaluator for this requirement semantics.'
    return None


def capability_report(program):
    result = []
    for instance, node in program.spaces.items():
        for rule in node.get('requirements', []):
            reason = _unsupported(rule)
            if reason:
                result.append({'id': rule['id'], 'owner': instance, 'type': rule['type'],
                               'priority': rule.get('priority', 'mandatory'), 'reason': reason})
    for rule in program.relationships:
        reason = _unsupported(rule)
        if reason:
            result.append({'id': rule['id'], 'owner': rule.get('owner'), 'type': rule['type'],
                           'priority': rule.get('priority', 'mandatory'), 'reason': reason})
    for key, feature in program.features.items():
        node = feature.get('node', {})
        if node.get('prototypes') or node.get('assembly'):
            result.append({'id': 'feature:' + key, 'owner': feature['room'],
                           'type': 'native_feature', 'priority': 'mandatory',
                           'reason': 'Native collision and operating footprints require an adapter.'})
    return result


class Verifier:
    def __init__(self, state):
        self.state, self.program = state, state.program
        self.checks = []
        self.all_floor = set().union(*state.floors.values()) if state.floors else set()
        self.interface_cells = set().union(*(set(i.get('cells', ())) for i in state.interfaces)) if state.interfaces else set()
        self.fixture_cells = set().union(*(set(f.get('blocking_cells', f.get('cells', ()))) for f in state.features.values())) if state.features else set()
        self.obstacles = self.all_floor | state.walls | self.interface_cells | self.fixture_cells
        reserved = set().union(*(set(r.get('cells', ())) for r in state.reservations)) if state.reservations else set()
        x0, y0, x1, y1 = bbox(self.obstacles | reserved)
        if state.bounds[2] > state.bounds[0]:
            x0, y0 = min(x0, state.bounds[0]), min(y0, state.bounds[1])
            x1, y1 = max(x1, state.bounds[2]), max(y1, state.bounds[3])
        self.exterior = flood_exterior(self.obstacles, (x0, y0, x1, y1))
        self.walk_cache = {}
        self.passage_cache = {}
        self.wrap = {}
        self.wrap_details = {}
        self._interfaces_valid = {}
        self._blocked_moves = None

    def check(self, rule, status, message, measured=None, cost=0.0, owner=None):
        result = {'id': rule['id'], 'type': rule.get('type', 'structure'),
                  'owner': owner if owner is not None else rule.get('owner'),
                  'priority': rule.get('priority', 'mandatory'), 'weight': rule.get('weight', 100),
                  'tier': rule.get('tier', 0), 'status': status, 'message': message}
        if measured is not None:
            result['measured'] = measured
        if status != 'unknown':
            result['cost'] = cost
        self.checks.append(result)
        return result

    def structural(self, key, owner, valid, message, measured=None):
        return self.check({'id': key, 'type': 'structure', 'owner': owner},
                          'satisfied' if valid else 'violated', message, measured)

    def room(self, endpoint):
        if endpoint in self.program.features:
            return self.program.features[endpoint]['room']
        return endpoint

    def endpoint_rooms(self, endpoint):
        endpoint = self.room(endpoint)
        if endpoint in self.program.spaces:
            return set(self.program.descendants(endpoint, physical_only=True))
        return {endpoint}

    def floor(self, endpoint):
        return set().union(*(self.state.floors.get(i, set()) for i in self.endpoint_rooms(endpoint)))

    def profile(self, interface):
        return self.program.definitions.get('interfaces', {}).get(interface.get('profile'))

    def permitted(self, interface, actor):
        profile = self.profile(interface)
        if not profile or 'crew_passage' not in profile.get('capabilities', []):
            return False
        credentials = self.program.definitions.get('accessProfiles', {}).get(actor)
        if credentials is None:
            return False
        clauses = profile.get('access', {}).get('anyOf', [])
        levels = set(credentials.get('levels', []))
        return not clauses or any(set(c) <= levels for c in clauses)

    def passage_allows(self, interface, source, target):
        direction = self.profile(interface).get('passage', {}).get('direction', 'bidirectional')
        if direction == 'bidirectional':
            return True
        return ((source, target) == (interface['a'], interface['b'])) == (direction == 'a_to_b')

    def interface_valid(self, interface):
        key = id(interface)
        if key in self._interfaces_valid:
            return self._interfaces_valid[key]
        a, b = interface.get('a'), interface.get('b')
        normal = tuple(interface.get('normal', (0, 0)))
        cells = set(interface.get('cells', ()))
        valid = bool(cells) and normal in DIRECTIONS and self.profile(interface) is not None and a != b
        if valid:
            nx, ny = normal
            first, second = self.floor(a), self.floor(b)
            domain = isinstance(b, str) and b.startswith('@domain/')
            valid = len({x * nx + y * ny for x, y in cells}) == 1
            passage = self.profile(interface).get('passage', {})
            if passage:
                valid &= passage.get('direction', 'bidirectional') in ('a_to_b', 'b_to_a', 'bidirectional')
                valid &= len(components(cells)) == 1
                valid &= passage.get('minWidth', 1) <= len(cells) <= passage.get('maxWidth', math.inf)
            if self.profile(interface).get('geometry') == 'open_edge':
                valid &= not domain and cells <= first
                valid &= all((x + nx, y + ny) in second for x, y in cells)
                valid &= not bool(cells & (self.state.walls | self.fixture_cells))
                valid &= self.profile(interface).get('normalState') == 'open'
                valid &= not self.profile(interface).get('access', {}).get('anyOf')
                self._interfaces_valid[key] = valid
                return valid
            valid &= all((x - nx, y - ny) in first for x, y in cells)
            if not domain:
                valid &= all((x + nx, y + ny) in second for x, y in cells)
            else:
                valid &= b[8:] in self.program.definitions.get('domains', {})
            valid &= not bool(cells & (self.all_floor | self.state.walls | self.fixture_cells))
        self._interfaces_valid[key] = valid
        return valid

    def blocked_moves(self):
        """Directed tile transitions forbidden by realized passage geometry."""
        if self._blocked_moves is not None:
            return self._blocked_moves
        blocked = set()
        for interface in self.state.interfaces:
            if not self.interface_valid(interface):
                continue
            profile = self.profile(interface)
            direction = profile.get('passage', {}).get('direction', 'bidirectional')
            if direction == 'bidirectional' or 'crew_passage' not in profile.get('capabilities', []):
                continue
            nx, ny = interface['normal']
            fx, fy = (nx, ny) if direction == 'a_to_b' else (-nx, -ny)
            for x, y in interface['cells']:
                cell = (x, y)
                if profile.get('geometry') == 'open_edge':
                    other = (x+nx, y+ny)
                    blocked.add((other, cell) if direction == 'a_to_b' else (cell, other))
                    continue
                # Native directional gates admit an axial crossing. A footprint
                # may not slide sideways between opposed adjacent gate lanes.
                for dx, dy in DIRECTIONS:
                    other = (x+dx, y+dy)
                    if (dx, dy) != (fx, fy):
                        blocked.add((cell, other))
                    if (-dx, -dy) != (fx, fy):
                        blocked.add((other, cell))
        self._blocked_moves = blocked
        return blocked

    def step_allowed(self, first, second, width=1):
        blocked = self.blocked_moves()
        if not blocked:
            return True
        return all(((first[0]+x, first[1]+y), (second[0]+x, second[1]+y)) not in blocked
                   for x in range(width) for y in range(width))

    def path(self, cells, starts, goals, width=1):
        """Shortest path on cells/footprint anchors, honoring directed seams."""
        if not self.blocked_moves():
            return shortest_path(cells, starts, goals)
        starts, goals = set(starts) & cells, set(goals) & cells
        queue = deque(sorted(starts))
        previous = {p: None for p in queue}
        while queue:
            point = queue.popleft()
            if point in goals:
                result = []
                while point is not None:
                    result.append(point)
                    point = previous[point]
                return list(reversed(result))
            for neighbor in neighbors(point):
                if neighbor in cells and neighbor not in previous and self.step_allowed(point, neighbor, width):
                    previous[neighbor] = point
                    queue.append(neighbor)
        return None

    def walkable(self, actor, allowed=None, removed=()):
        # Room access is an intended actor, not a hidden permission on its floor.
        allowed = set(self.state.floors) if allowed is None else set(allowed)
        removed = set(removed)
        key = (actor, tuple(sorted(allowed)), tuple(sorted(removed)))
        if key in self.walk_cache:
            return self.walk_cache[key]
        allowed -= removed
        cells = set()
        for owner in allowed:
            node = self.program.spaces.get(owner, {}).get('node', {})
            environment = self.program.definitions.get('environments', {}).get(node.get('environment'), {})
            if environment.get('pressure') == 'vacuum':
                continue
            cells.update(self.state.floors.get(owner, ()))
        for interface in self.state.interfaces:
            if not self.interface_valid(interface) or not self.permitted(interface, actor):
                continue
            a, b = interface['a'], interface['b']
            if a in allowed and b in allowed:
                cells.update(interface['cells'])
        cells -= self.fixture_cells
        self.walk_cache[key] = cells
        return cells

    def route(self, source, target, p):
        actor = p.get('accessProfile', 'public')
        if actor not in self.program.definitions.get('accessProfiles', {}):
            return None
        within = p.get('within', 'station')
        source_room = self.room(source)
        node = self.program.spaces.get(source_room, {})
        owner = node.get('owner') if within == 'owner' else node.get('parent')
        allowed = None if within == 'station' else self.endpoint_rooms(owner)
        if allowed is not None:
            # In a local specimen the remaining owner/parent is represented by
            # explicitly reconstructed boundary ports. This proves an approach
            # conditional on composition, without including proxy floor in area.
            boundary_key = 'harnessOwner' if within == 'owner' else 'harnessParent'
            allowed |= {k for k, n in self.program.spaces.items()
                        if n.get('harness') and n.get(boundary_key) == owner}
        removed = {i for reference in p.get('avoid', []) for i in self.endpoint_rooms(reference)}
        cells = self.walkable(actor, allowed, removed)
        width = p.get('minWidth', 1)
        anchors = square_anchors(cells, width)
        starts = square_anchors(self.floor(source) & cells, width)
        goals = square_anchors(self.floor(target) & cells, width)
        waypoints = [square_anchors(self.floor(i) & cells, width) for i in p.get('through', [])]
        if not starts or not goals or any(not w for w in waypoints):
            return None
        if not waypoints:
            return self.path(anchors, starts, goals, width)
        full = (1 << len(waypoints)) - 1
        def bits(point):
            return sum(1 << i for i, group in enumerate(waypoints) if point in group)
        queue = deque((p, bits(p)) for p in sorted(starts))
        previous = {s: None for s in queue}
        while queue:
            current = queue.popleft()
            point, visited = current
            if point in goals and visited == full:
                path = []
                while current is not None:
                    path.append(current[0])
                    current = previous[current]
                return list(reversed(path))
            for n in neighbors(point):
                nxt = (n, visited | bits(n))
                if n in anchors and nxt not in previous and self.step_allowed(point, n, width):
                    previous[nxt] = current
                    queue.append(nxt)
        return None

    def structure_checks(self):
        open_edges = set()
        for interface in self.state.interfaces:
            if self.profile(interface) and self.profile(interface).get('geometry') == 'open_edge' and self.interface_valid(interface):
                dx, dy = interface['normal']
                open_edges.update(frozenset((c, (c[0] + dx, c[1] + dy))) for c in interface['cells'])
        seen = set()
        for owner, cells in sorted(self.state.floors.items()):
            valid_owner = owner in self.program.spaces and self.program.spaces[owner]['kind'] in ('room', 'passage')
            self.structural('floor:' + owner, owner, valid_owner and bool(cells) and not bool(cells & seen),
                            'Exclusive physical floor must be nonempty and disjoint.', len(cells))
            self.structural('walls:' + owner, owner, not bool(cells & self.state.walls), 'Usable floor cannot overlap structural walls.')
            adjacent_foreign = {n for c in cells for n in neighbors(c) if n in self.all_floor and n not in cells
                                and frozenset((c, n)) not in open_edges}
            self.structural('boundary:' + owner, owner, not adjacent_foreign,
                            'Different floor owners need a realized structural/interface seam; touching labels are not a doorway.')
            node = self.program.spaces.get(owner, {}).get('node', {})
            environment = self.program.definitions.get('environments', {}).get(node.get('environment'), {})
            if environment.get('enclosure') == 'sealed':
                uncovered = {n for c in cells for n in neighbors(c) if n not in self.all_floor | self.state.walls | self.interface_cells}
                self.structural('enclosure:' + owner, owner, not uncovered, 'Sealed floor needs enclosing structure at its exposed perimeter.')
            seen.update(cells)
        for owner, node in self.program.spaces.items():
            if node['kind'] in ('room', 'passage') and owner not in self.state.floors:
                self.structural('floor:' + owner, owner, False, 'Selected physical space has no realized floor.')
            if node['kind'] not in ('room', 'passage'):
                continue
            if node.get('node', {}).get('placement', {}).get('nesting') == 'contained':
                parent = node['parent']
                envelope = self.state.envelopes.get(parent, set())
                self.structural('containment:' + owner, owner, bool(envelope) and self.floor(owner) <= envelope,
                                'Contained descendants must lie inside the host complex envelope.')
                # A claimed envelope cannot simply include arbitrary remote cells.
                host_floor = self.state.floors.get(parent, set())
                child_floor = set().union(*(self.state.floors.get(c, set()) for c in self.program.descendants(parent, True)))
                support = child_floor | self.state.walls | self.interface_cells
                self.structural('envelope:' + owner, owner, bool(host_floor) and len(components(envelope)) == 1 and envelope <= support,
                                'Host envelope is one actual built complex, not an arbitrary bounding claim.')
        for interface in self.state.interfaces:
            self.structural('interface:' + interface.get('id', '?'), interface.get('a'), self.interface_valid(interface),
                            'Interface needs one real structural row with floor approaches and a known profile.')
        interface_seen = set()
        open_seen = set()
        duplicate = False
        for interface in self.state.interfaces:
            cells = set(interface.get('cells', ()))
            if self.profile(interface) and self.profile(interface).get('geometry') == 'open_edge':
                dx, dy = interface['normal']
                edges = {frozenset((c, (c[0] + dx, c[1] + dy))) for c in cells}
                duplicate |= bool(edges & open_seen)
                open_seen.update(edges)
                continue
            duplicate |= bool(cells & interface_seen)
            interface_seen |= cells
        self.structural('interfaces:exclusive', None, not duplicate, 'Two interfaces cannot spend the same structural cells.')
        occupied_features = set()
        for key, pose in sorted(self.state.features.items()):
            cells, clearance = set(pose.get('cells', ())), set(pose.get('clearance', ()))
            room = pose.get('room')
            specialized = pose.get('layer') in ('underfloor', 'wall', 'interface', 'abstract', 'portable')
            support = self.state.floors.get(room, set()) | (self.state.walls | self.interface_cells if specialized else set())
            other_feature_cells = set().union(*(set(other.get('blocking_cells', other.get('cells', ()))) for other_key, other in self.state.features.items() if other_key != key))
            blocking = set(pose.get('blocking_cells', cells))
            self.structural('feature_pose:' + key, room, key in self.program.features and (bool(cells) or pose.get('layer') == 'abstract') and cells <= support and clearance <= self.state.floors.get(room, set()) and not (blocking & occupied_features) and not (clearance & other_feature_cells),
                            'Feature and operation clearance must fit their owning room.')
            occupied_features.update(blocking)
        for reservation in self.state.reservations:
            if reservation.get('kind') in ('docking', 'exterior', 'vacuum', 'deployment', 'beam'):
                self.structural('reservation:' + reservation['id'], reservation.get('owner'),
                                not bool(set(reservation.get('cells', ())) & self.obstacles),
                                'Protected empty volume cannot contain floor, walls, interfaces or fixtures.')
        for diagnostic in self.program.diagnostics:
            if diagnostic.get('severity', 'error') == 'error':
                self.check({'id': 'selection:' + str(len(self.checks)), 'type': 'selection'}, 'violated', diagnostic.get('message', str(diagnostic)))

    def cohesion_cells(self, owner, cells=None):
        cells = self.floor(owner) if cells is None else cells
        structure = self.state.walls | self.interface_cells
        return cells | {p for p in structure if any((p[0] - dx, p[1] - dy) in cells and
                                                    (p[0] + dx, p[1] + dy) in cells
                                                    for dx, dy in ((1, 0), (0, 1)))}

    def maintenance_wrap(self, owner):
        if owner in self.wrap:
            return self.wrap[owner]
        floor = self.floor(owner)
        maintenance = set().union(*(self.state.floors.get(k, set()) for k, n in self.program.spaces.items()
                                    if 'maintenance_circulation' in n.get('node', {}).get('tags', []) and self.passage_geometry(k, 2, 3)[0]))
        reachable = max(components(maintenance), key=len, default=set())
        structure = self.state.walls | self.interface_cells
        excluded = set()
        # Only required frontage may leave the denominator. An added decorative
        # exterior interface must not manufacture a perfect wrapping score.
        allowances = {}
        for room in self.endpoint_rooms(owner):
            for rule in self.program.spaces.get(room, {}).get('requirements', []):
                if rule.get('priority', 'mandatory') != 'mandatory':
                    continue
                p = rule.get('parameters', {})
                if rule['type'] == 'perimeter':
                    key = (room, p['interface'], '@domain/' + p['domain'])
                    allowances[key] = max(allowances.get(key, 0), p['minLength'])
        for rule in self.program.relationships:
            if rule['type'] == 'shared_boundary' and rule.get('priority', 'mandatory') == 'mandatory' and str(rule.get('target', '')).startswith('@domain/'):
                key = (rule['source'], rule['parameters']['interface'], rule['target'])
                allowances[key] = max(allowances.get(key, 0), rule['parameters']['minLength'])
        for interface in sorted(self.state.interfaces, key=lambda i: i.get('id', '')):
            key = (interface.get('a'), interface.get('profile'), interface.get('b'))
            remaining = allowances.get(key, 0)
            if remaining and self.interface_valid(interface):
                chosen = sorted(interface.get('cells', ()))[:remaining]
                excluded.update(chosen)
                allowances[key] -= len(chosen)
        owned_rooms = self.endpoint_rooms(owner)
        for rule in self.program.relationships:
            if rule['type'] != 'shared_boundary' or rule.get('priority', 'mandatory') != 'mandatory':
                continue
            sources, targets = self.endpoint_rooms(rule.get('source')), self.endpoint_rooms(rule.get('target'))
            if bool(sources & owned_rooms) == bool(targets & owned_rooms):
                continue
            matches = self.matching_interfaces(rule['source'], rule['target'], rule['parameters']['interface'])
            matching_cells = set().union(*(set(i['cells']) for i in matches)) if matches else set()
            remaining = max(0, rule['parameters']['minLength'] - len(matching_cells & excluded))
            for interface in sorted(matches, key=lambda i: i.get('id', '')):
                chosen = sorted(set(interface['cells']) - excluded)[:remaining]
                excluded.update(chosen)
                remaining -= len(chosen)
        eligible, covered, excluded_length = 0, 0, 0
        for x, y in floor:
            for dx, dy in DIRECTIONS:
                near, far = (x + dx, y + dy), (x + 2 * dx, y + 2 * dy)
                if near in floor or (near in structure and far in floor):
                    continue
                if near in excluded:
                    excluded_length += 1
                    continue
                eligible += 1
                covered += near in reachable or (near in structure and far in reachable)
        result = covered / eligible if eligible else 0.0
        self.wrap[owner] = result
        self.wrap_details[owner] = {'eligibleBoundary': eligible, 'coveredBoundary': covered,
                                    'excludedRequiredFrontage': excluded_length}
        return result

    def passage_geometry(self, owner, minimum, maximum):
        cache_key = (owner, minimum, maximum)
        if cache_key in self.passage_cache:
            return self.passage_cache[cache_key]
        cells = self.state.floors.get(owner, set())
        clear = cells - self.fixture_cells
        anchors = square_anchors(clear, minimum)
        covered = covered_by_squares(clear, minimum)
        no_wide = not square_anchors(clear, maximum + 1) if maximum < 100 else True
        strips = self.state.strips.get(owner, [])
        witnessed = set()
        strips_ok = bool(strips)
        for strip in strips:
            x, y, xx, yy = bbox(strip)
            strips_ok &= bool(strip) and strip == rect(x, y, xx - x, yy - y) and minimum <= min(xx - x, yy - y) <= maximum
            witnessed.update(strip)
        valid = bool(clear) and covered == clear and len(components(anchors)) == 1 and no_wide and strips_ok and witnessed == cells
        widths = {c: minimum for c in covered}
        # Preferred width is a real soft target, even when 2 and 3 are both legal.
        # Classify each cell by the largest permitted clear square covering it.
        x0, y0, x1, y1 = bbox(clear)
        for width in range(minimum + 1, min(maximum, x1 - x0, y1 - y0) + 1):
            for c in covered_by_squares(clear, width):
                widths[c] = width
        mean_width = sum(widths.values()) / len(widths) if widths else 0
        result = valid, {'minimum': minimum, 'maximum': maximum, 'uncovered': len(clear - covered),
                         'stripWitness': bool(strips_ok), 'noOversizedSquare': no_wide, 'meanWidth': mean_width}
        self.passage_cache[cache_key] = result
        return result

    def metric(self, owner, rule):
        p = rule['parameters']
        metric = p['metric']
        cells = self.floor(owner)
        x0, y0, x1, y1 = bbox(cells)
        width, height = x1 - x0, y1 - y0
        if metric == 'area.exclusive':
            value = len(self.state.floors.get(owner, set()))
        elif metric == 'area.aggregate':
            value = len(cells)
        elif metric == 'shape.aspect_ratio':
            value = max(width, height) / max(1, min(width, height))
        elif metric == 'shape.short_side':
            value = min(width, height)
        elif metric == 'shape.fill_ratio':
            value = len(cells) / max(1, width * height)
        elif metric == 'shape.components':
            logical = self.program.spaces[owner]['kind'] in ('department', 'group')
            value = len(components(self.cohesion_cells(owner) if logical else cells))
        elif metric == 'core.components':
            config = self.program.spaces[owner]['node'].get('organization', {})
            detached = [*config.get('independent', []),
                        *(p for suite in config.get('suites', []) for p in suite['members'])]
            keys = [k for k in self.endpoint_rooms(owner)
                    if not self.program.spaces[k].get('harness')
                    and self.program.spaces[k]['node']['placement'].get('zone') != 'outboard'
                    and not any(self.program.spaces[k]['template'] == p or
                                self.program.spaces[k]['template'].startswith(p+'/') for p in detached)]
            core = set().union(*(self.state.floors.get(k, set()) for k in keys)) if keys else set()
            value = len(components(self.cohesion_cells(owner, core)))
        elif metric == 'maintenance.wrap_fraction':
            value = self.maintenance_wrap(owner)
        elif metric == 'corridor.clear_width':
            minimum, maximum = int(p.get('min', 1)), int(p.get('max', 1000000))
            valid, measurement = self.passage_geometry(owner, minimum, maximum)
            value = minimum if valid else 0
            return self.check(rule, 'satisfied' if valid else 'violated',
                              'Clear passage must be explained by connected allowed-width strips; no narrow choke or oversized open pocket.',
                              measurement,
                              abs(measurement['meanWidth'] - p.get('preferred', measurement['meanWidth'])) / p.get('scale', 1) + (0 if valid else 1), owner)
        else:
            return self.check(rule, 'unknown', 'No metric evaluator.', owner=owner)
        low, high = p.get('min', -math.inf), p.get('max', math.inf)
        outside = max(low - value, value - high, 0)
        deviation = abs(value - p['preferred']) if 'preferred' in p else 0
        cost = (outside + deviation) / p.get('scale', 1)
        return self.check(rule, 'satisfied' if outside == 0 else 'violated', 'Measured on exact realized geometry.', value, cost, owner)

    def matching_interfaces(self, source, target, profile, rule=None):
        sources, targets = self.endpoint_rooms(source), self.endpoint_rooms(target)
        directional = self.program.definitions.get('interfaces', {}).get(profile, {}).get('passage', {}).get('direction', 'bidirectional') != 'bidirectional'
        matches = [i for i in self.state.interfaces if i.get('profile') == profile and self.interface_valid(i) and
                   ((i.get('a') in sources and i.get('b') in targets) or
                    (not directional and i.get('b') in sources and i.get('a') in targets))]
        # Distinct bindings of the same profile to the same pair may not borrow
        # one another's physical run to satisfy their individual contracts.
        if rule is not None and any(i.get('rule') for i in matches):
            matches = [i for i in matches if not i.get('rule') or i.get('rule') == rule]
        return matches

    def seam_measure(self, interfaces):
        # Only collinear seams combine; a bent service frontier is not one run.
        lines = {}
        for interface in interfaces:
            nx, ny = interface['normal']
            for x, y in interface['cells']:
                key = ('x', x) if nx else ('y', y)
                lines.setdefault(key, set()).add((x, y))
        segments = [part for cells in lines.values() for part in components(cells)]
        return sum(map(len, segments)), len(segments), max(map(len, segments), default=0)

    def relationship(self, rule):
        reason = _unsupported(rule)
        if reason:
            return self.check(rule, 'unknown', reason)
        p, kind = rule.get('parameters', {}), rule['type']
        source, target = rule.get('source'), rule.get('target')
        if source is None or target is None:
            return self.check(rule, 'violated', 'Active relationship has no concrete endpoint binding.')
        if kind == 'dependency':
            exists = target in self.program.spaces or target in self.program.features or str(target).startswith('@domain/')
            return self.check(rule, 'satisfied' if exists else 'violated', 'Concrete target presence.', exists, 0 if exists else 1)
        if kind == 'shared_boundary':
            matches = self.matching_interfaces(source, target, p['interface'], rule['id'])
            total, count, longest = self.seam_measure(matches)
            length = longest if p.get('continuous') else total
            valid = length >= p['minLength'] and total <= p.get('maxLength', math.inf) and count >= p.get('minSegments', 1) and count <= p.get('maxSegments', math.inf)
            cost = abs(length - p.get('preferredLength', length)) + (0 if valid else 1)
            return self.check(rule, 'satisfied' if valid else 'violated', 'Physical interface runs and approaches.',
                              {'length': length, 'segments': count, 'longest': longest}, cost)
        if kind == 'route':
            path = self.route(source, target, p)
            valid = path is not None and len(path) - 1 <= p.get('maxLength', math.inf)
            return self.check(rule, 'satisfied' if valid else 'violated', 'Credential-aware clear-footprint route, including waypoints and exclusions.',
                              {'length': len(path) - 1 if path else None, 'width': p.get('minWidth', 1)}, 0 if valid else 1)
        if kind == 'grid_relation':
            def grid(endpoint):
                room = self.room(endpoint)
                if room in self.program.spaces:
                    return self.program.spaces[room]['node'].get('placement', {}).get('grid')
                if str(room).startswith('@domain/'):
                    return self.program.definitions.get('domains', {}).get(room[8:], {}).get('grid')
                return None
            a, b = grid(source), grid(target)
            if a is None or b is None:
                return self.check(rule, 'unknown', 'Endpoint grid is not established.')
            valid = (a == b) == (p['relation'] == 'same')
            return self.check(rule, 'satisfied' if valid else 'violated', 'Explicit engine-grid identities.', [a, b], 0 if valid else 1)
        if kind == 'separation':
            a, b = self.floor(source), self.floor(target)
            if p.get('metric') == 'walking':
                path = self.route(source, target, {'accessProfile': p.get('accessProfile', 'public'), 'minWidth': 1})
                value = len(path) - 1 if path else None
            else:
                # Distance between closed unit-tile boundaries, not tile centers.
                value = min((max(abs(x - xx) - 1, 0) + max(abs(y - yy) - 1, 0)
                             for x, y in a for xx, yy in b), default=None)
            valid = value is not None and p.get('minDistance', 0) <= value <= p.get('maxDistance', math.inf)
            cost = abs(value - p.get('preferredDistance', value)) if value is not None else 1
            return self.check(rule, 'satisfied' if valid else 'violated', 'Measured separation under the declared metric.', value, cost + (not valid))

    def access_policy(self, owner, rule):
        p = rule['parameters']
        owned = self.endpoint_rooms(owner)
        entries = [i for i in self.state.interfaces if self.interface_valid(i) and
                   'crew_passage' in self.profile(i).get('capabilities', []) and
                   ((i['a'] in owned) != (i['b'] in owned))]
        allowed = {i for reference in p.get('entryFrom', []) for i in self.endpoint_rooms(reference)}
        failures = []
        for interface in entries:
            other = interface['b'] if interface['a'] in owned else interface['a']
            inside = interface['a'] if interface['a'] in owned else interface['b']
            if not self.passage_allows(interface, other, inside):
                continue
            exterior = str(other).startswith('@domain/')
            tags = self.program.spaces.get(other, {}).get('node', {}).get('tags', [])
            if exterior and not p.get('allowExteriorEntry', False):
                failures.append('exterior entry')
            if not exterior and allowed and other not in allowed:
                failures.append('unlisted entrance neighbor')
            if 'maintenance_circulation' in tags and not p.get('allowMaintenanceEntry', True):
                failures.append('maintenance entry')
        public_sources = [i for i, n in self.program.spaces.items() if 'public_circulation' in n.get('node', {}).get('tags', [])]
        public_floor = set().union(*(self.floor(i) for i in public_sources))
        if not p.get('allowPublicEntry', True) and public_floor:
            cells = self.walkable('public')
            if self.path(cells, public_floor, self.floor(owner)) is not None:
                failures.append('public entry')
        for reference in p.get('mustTraverse', []):
            removed = self.endpoint_rooms(reference)
            for actor in self.program.definitions.get('accessProfiles', {}):
                cells = self.walkable(actor, removed=removed)
                if public_floor and self.path(cells, public_floor, self.floor(owner)) is not None:
                    failures.append('bypass of ' + reference + ' by ' + actor)
        if not p.get('throughTraffic', True):
            for actor in self.program.definitions.get('accessProfiles', {}):
                usable = [i for i in entries if self.permitted(i, actor)]
                # External-to-external passage through this complex, not access to
                # its own contained children, is through traffic.
                if len(usable) > 1:
                    inner = self.walkable(actor, allowed=owned)
                    for index, first in enumerate(usable):
                        a = {n for c in first['cells'] for n in neighbors(c)} & inner
                        for second in usable[index + 1:]:
                            b = {n for c in second['cells'] for n in neighbors(c)} & inner
                            def permits_boundary(interface, entering):
                                inside = interface['a'] if interface['a'] in owned else interface['b']
                                outside = interface['b'] if interface['a'] in owned else interface['a']
                                return self.passage_allows(interface, outside, inside) if entering else self.passage_allows(interface, inside, outside)
                            forward = permits_boundary(first, True) and permits_boundary(second, False)
                            reverse = permits_boundary(second, True) and permits_boundary(first, False)
                            if ((forward and self.path(inner, a, b) is not None) or
                                    (reverse and self.path(inner, b, a) is not None)):
                                failures.append('through traffic')
        return self.check(rule, 'violated' if failures else 'satisfied',
                          '; '.join(sorted(set(failures))) or 'All entry and no-bypass policies hold.',
                          {'entries': len(entries)}, len(set(failures)), owner)

    def exterior_interfaces(self, owner, profile, domain=None):
        owned = self.endpoint_rooms(owner)
        return [i for i in self.state.interfaces if i.get('a') in owned and i.get('profile') == profile and
                str(i.get('b', '')).startswith('@domain/') and (domain is None or i['b'] == '@domain/' + domain) and self.interface_valid(i)]

    def node_requirement(self, owner, rule):
        reason = _unsupported(rule)
        if reason:
            return self.check(rule, 'unknown', reason, owner=owner)
        kind, p = rule['type'], rule.get('parameters', {})
        if kind == 'metric':
            return self.metric(owner, rule)
        if kind == 'access_policy':
            return self.access_policy(owner, rule)
        if self.exterior is None:
            return self.check(rule, 'unknown', 'Exterior flood-fill canvas exceeds the declared verification work limit.', owner=owner)
        if kind == 'perimeter':
            interfaces = self.exterior_interfaces(owner, p['interface'], p['domain'])
            exposed = [i for i in interfaces if all((x + i['normal'][0], y + i['normal'][1]) in self.exterior for x, y in i['cells'])]
            total, count, longest = self.seam_measure(exposed)
            value = longest if p.get('continuous') else total
            valid = value >= p['minLength']
            return self.check(rule, 'satisfied' if valid else 'violated', 'Real interface to exterior-connected empty space.',
                              value, max(0, p['minLength'] - value) + abs(value - p.get('preferredLength', value)), owner)
        if kind == 'docking':
            interfaces = self.exterior_interfaces(owner, p['interface'])
            witnesses = []
            reserved = set().union(*(set(r.get('cells', ())) for r in self.state.reservations
                                     if r.get('owner') == owner and r.get('kind') == 'docking'))
            for interface in interfaces:
                required = docking_cells(interface, p)
                valid = bool(required) and required <= reserved and not bool(required & self.obstacles)
                if p.get('approachToExterior'):
                    valid &= required <= self.exterior
                    valid &= not bool(docking_exterior_lane(interface, p, self.obstacles) & self.obstacles)
                if valid:
                    witnesses.append(required)
            return self.check(rule, 'satisfied' if witnesses else 'violated',
                              'Berth and approach reconstructed from dock normal, dimensions, offset and clearance.',
                              {'validDocks': len(witnesses)}, 0 if witnesses else 1, owner)

    def run(self):
        self.structure_checks()
        for owner, node in self.program.spaces.items():
            for rule in node.get('requirements', []):
                self.node_requirement(owner, rule)
        for rule in self.program.relationships:
            self.relationship(rule)
        for capability in capability_report(self.program):
            if capability['type'] == 'native_feature':
                self.check(capability, 'unknown', capability['reason'])
        hard = [c for c in self.checks if c['priority'] == 'mandatory' and c['status'] == 'violated']
        unknown = [c for c in self.checks if c['priority'] == 'mandatory' and c['status'] == 'unknown']
        costs = {}
        for check in self.checks:
            if check['status'] != 'unknown':
                tier = str(check['tier'])
                costs[tier] = costs.get(tier, 0.0) + check.get('cost', 0.0) * check['weight']
        departments = [i for i, n in self.program.spaces.items() if n['kind'] == 'department']
        core = set().union(*(cells for i, cells in self.state.floors.items()
                             if self.program.spaces.get(i, {}).get('node', {}).get('placement', {}).get('zone') != 'outboard'))
        hull_area = convex_hull_area(core | self.state.walls)
        wrap = {i: self.maintenance_wrap(i) for i in departments}
        maintenance = [i for i, n in self.program.spaces.items() if 'maintenance_circulation' in n.get('node', {}).get('tags', [])]
        x = sum(c[0] for c in core) / len(core) if core else 0
        y = sum(c[1] for c in core) / len(core) if core else 0
        rg2 = sum((cx - x) ** 2 + (cy - y) ** 2 for cx, cy in core) / len(core) if core else 0
        return {'checks': self.checks, 'hard_failures': hard, 'unknown_mandatory': unknown,
                'soft_costs': costs, 'verified': not hard and not unknown,
                'facets': {'layout': 'verified' if not hard and not unknown else 'incomplete',
                           'native_realization': 'not_evaluated', 'runtime_operation': 'not_evaluated'},
                'metrics': {'usable_floor': len(self.all_floor), 'core_floor': len(core),
                            'core_hull_area': hull_area, 'core_density': len(core) / hull_area if hull_area else 0,
                            'core_squared_radius_of_gyration': rg2,
                            'maintenance_area': sum(len(self.state.floors.get(i, ())) for i in maintenance),
                            'department_wrap': wrap, 'mean_wrap': sum(wrap.values()) / len(wrap) if wrap else 0,
                            'department_wrap_details': {i: self.wrap_details[i] for i in departments},
                            'worst_wrap': min(wrap.values(), default=0),
                            'department_components': {i: len(components(self.cohesion_cells(i))) for i in departments}}}


def verify_layout(state):
    return Verifier(state).run()
