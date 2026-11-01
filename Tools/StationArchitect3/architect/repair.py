"""Bounded, local-first geometry repair with atomic independent verification.

The authored instance tree is immutable. Repairs change allocations, never rules.
No failed heuristic search is treated as a proof of infeasibility.
"""
from __future__ import annotations

from dataclasses import dataclass
import hashlib
import json
from .state import LayoutState, json_ready

DIRECTIONS = ((1, 0), (0, 1), (-1, 0), (0, -1))


def adjacent(cell):
    x, y = cell
    return [(x + dx, y + dy) for dx, dy in DIRECTIONS]


def occupied(state):
    return set().union(*state.floors.values()) if state.floors else set()


def rebuild_walls(state):
    """Regenerate the full-tile structural skin without changing interfaces."""
    from .packing import rebuild_walls as rebuild
    rebuild(state)


def _protected(report):
    return {c['id'] for c in report['checks']
            if c.get('priority') == 'mandatory' and c['status'] == 'satisfied'}


@dataclass
class RepairResult:
    state: LayoutState
    status: str
    scope: str | None
    attempts: int
    reason: str = ''


class RepairTransaction:
    """A private candidate plus a pre-edit set of protected obligations.

    The protected set does not disappear when cached witnesses are invalidated.
    The verifier recomputes its checks from geometry at every attempted commit.
    """

    def __init__(self, state: LayoutState, scope: str | None, reason: str):
        from .verify import verify_layout
        self.original = state
        self.candidate = state.clone()
        self.scope = scope
        self.reason = reason
        self.before = verify_layout(state)
        self.protected = _protected(self.before)
        self.report = None

    def commit(self, predicate=lambda state: True):
        from .verify import verify_layout
        if self.candidate.program != self.original.program:
            return None, {'authoredProgramChanged': True}
        if self.scope is not None:
            allowed = set(self.original.program.descendants(self.scope))
            changed_rooms = {i for i in self.original.floors.keys() | self.candidate.floors.keys()
                             if self.original.floors.get(i) != self.candidate.floors.get(i)}
            changed_features = {i for i in self.original.features.keys() | self.candidate.features.keys()
                                if self.original.features.get(i) != self.candidate.features.get(i)}
            forbidden = changed_rooms - allowed
            for field in ('envelopes', 'strips'):
                before = getattr(self.original, field)
                after = getattr(self.candidate, field)
                forbidden.update(k for k in before.keys() | after.keys()
                                 if before.get(k) != after.get(k) and k not in allowed)
            if self.original.bounds != self.candidate.bounds:
                forbidden.add('@station/canvas')
            changed_walls = self.original.walls ^ self.candidate.walls
            if changed_walls:
                floor_owners = {}
                for layout in (self.original, self.candidate):
                    for key, floor in layout.floors.items():
                        for c in floor:
                            floor_owners.setdefault(c, set()).add(key)
                for c in changed_walls:
                    touching = set().union(*(floor_owners.get((c[0]+dx, c[1]+dy), set())
                                             for dx in (-1, 0, 1) for dy in (-1, 0, 1)))
                    if not touching or not touching <= allowed:
                        forbidden.add('@wall/' + str(c))
            for key in changed_features:
                pose = self.candidate.features.get(key, self.original.features.get(key, {}))
                if pose.get('room') not in allowed:
                    forbidden.add(key)
            for field in ('interfaces', 'reservations'):
                before = {i['id']: i for i in getattr(self.original, field)}
                after = {i['id']: i for i in getattr(self.candidate, field)}
                for key in before.keys() | after.keys():
                    if before.get(key) == after.get(key):
                        continue
                    resource = after.get(key, before.get(key))
                    owners = {resource['a'], resource['b']} if field == 'interfaces' else {resource.get('owner')}
                    if not owners <= allowed:
                        forbidden.add(key)
            if forbidden:
                return None, {'outsideRepairAuthority': sorted(forbidden)}
        self.report = verify_layout(self.candidate)
        after = {c['id']: c for c in self.report['checks']}
        lost = sorted(i for i in self.protected
                      if i not in after or after[i]['status'] != 'satisfied')
        # New structural or requirement failures are also forbidden. Existing
        # construction deficits can remain explicit while another one is repaired.
        previous_failures = {c['id'] for c in self.before['checks']
                             if c.get('priority') == 'mandatory'
                             and c['status'] != 'satisfied'}
        introduced = sorted(c['id'] for c in self.report['checks']
                            if c.get('priority') == 'mandatory'
                            and c['status'] in ('violated', 'unknown')
                            and c['id'] not in previous_failures)
        if lost or introduced or not predicate(self.candidate):
            return None, {'lostProtected': lost, 'introducedFailures': introduced}
        changed = sorted(i for i in self.candidate.floors
                         if self.candidate.floors[i] != self.original.floors.get(i, set()))
        self.candidate.revision = self.original.revision + 1
        self.candidate.summaries.clear()
        self.candidate.trace.append({'event': 'repair_committed', 'scope': self.scope,
                                     'reason': self.reason, 'changedRooms': changed,
                                     'revision': self.candidate.revision,
                                     'protectedChecksRecomputed': len(self.protected)})
        return self.candidate, None


def _area_min(program, room):
    return max([0] + [r['parameters'].get('min', 0)
                      for r in program.spaces[room]['requirements']
                      if r['type'] == 'metric' and r.get('priority', 'mandatory') == 'mandatory'
                      and r['parameters']['metric'] == 'area.exclusive'])


def _blocks(state):
    return ({c for r in state.reservations for c in r['cells']}
            | {c for i in state.interfaces for c in i['cells']}
            | {c for p in state.features.values()
               for c in p.get('cells', set()) | p.get('clearance', set())})


def _local_additions(state, room):
    cells = state.floors.get(room, set())
    # Only genuine room-local free floor inside an existing lease can be used.
    envelope = state.envelopes.get(room, set())
    candidates = {n for c in cells for n in adjacent(c)} & envelope
    blocked = occupied(state) | state.walls | _blocks(state)
    return sorted(candidates - blocked,
                  key=lambda c: (-sum(n in cells for n in adjacent(c)), c))


def _sibling_exchanges(state, room, scope):
    """Slide a wall tile toward a donor, preserving its full-tile thickness."""
    cells = state.floors.get(room, set())
    allowed = set(state.program.descendants(scope, True)) - {room}
    owner_at = {c: i for i in allowed for c in state.floors.get(i, set())}
    blocked = _blocks(state)
    # Keep door/counter approaches untouched by this operator; other operators
    # can deliberately move entire interfaces in a larger transaction.
    blocked |= {n for i in state.interfaces for c in i['cells'] for n in adjacent(c)}
    candidates = []
    for p in sorted(cells):
        for dx, dy in DIRECTIONS:
            wall = (p[0] + dx, p[1] + dy)
            donor_cell = (p[0] + 2 * dx, p[1] + 2 * dy)
            donor = owner_at.get(donor_cell)
            if (wall in state.walls and donor and wall not in blocked
                    and donor_cell not in blocked
                    and state.program.spaces[donor]['kind'] == 'room'
                    and len(state.floors[donor]) > _area_min(state.program, donor)):
                candidates.append((wall, donor_cell, donor))
    return sorted(set(candidates))


def _external_additions(state, room, scope):
    floor = occupied(state)
    blocks = _blocks(state)
    cells = state.floors.get(room, set())
    lease = state.envelopes.get(scope) if scope is not None else None
    if scope is not None and lease is None:
        # Logical ownership is not a grant to consume arbitrary station space.
        # Without an explicit parent allocation, the station must coordinate it.
        return []
    # Physical contained rooms never expand beyond the host complex.
    node = state.program.spaces[room]
    if node['node']['placement'].get('nesting') == 'contained':
        host = state.envelopes.get(node['parent'], set())
        lease = host if lease is None else lease & host
    result = []
    minx, miny, maxx, maxy = state.bounds
    for p in sorted(cells):
        for dx, dy in DIRECTIONS:
            wall = (p[0] + dx, p[1] + dy)
            outer = (p[0] + 2 * dx, p[1] + 2 * dy)
            if wall not in state.walls or outer in floor or wall in blocks or outer in blocks:
                continue
            if not (minx < outer[0] < maxx and miny < outer[1] < maxy):
                continue
            if lease is not None and (wall not in lease or outer not in lease):
                continue
            # Do not turn another room's structural skin into an unauthorized seam.
            if any(n in floor - cells for n in adjacent(wall)):
                continue
            result.append(wall)
    return sorted(set(result), key=lambda c: (-sum(n in cells for n in adjacent(c)), c))


def repair_area(state: LayoutState, room: str, target: int, settings: dict) -> RepairResult:
    """Try own free floor, sibling slack, then ancestor/exterior capacity.

    Area is a request, not a modification of the authored preferred/min/max value.
    Each candidate is checked against unchanged requirements before commit.
    """
    if room not in state.floors:
        return RepairResult(state, 'unresolved', room, 0, 'No realized room floor')
    if len(state.floors[room]) >= target:
        return RepairResult(state, 'satisfied', room, 0)
    limit = max(0, settings.get('local_proposals', 200))
    attempts = 0
    scopes = [('room', room)]
    scopes.extend(('siblings', p) for p in state.program.ancestors(room)[1:])
    scopes.append(('station', None))
    trace = []
    for level, scope in scopes:
        if attempts >= limit:
            break
        # Try distinct first moves so one rejected donor cannot permanently hide
        # a valid alternative. Each full candidate remains bounded by target size.
        for alternative in range(max(1, settings.get('parent_alternatives', 4))):
            if attempts >= limit:
                break
            transaction = RepairTransaction(state, scope, f'area request for {room}: {target}')
            trial = transaction.candidate
            steps = 0
            while len(trial.floors[room]) < target and attempts < limit:
                attempts += 1
                steps += 1
                if level == 'room':
                    choices = _local_additions(trial, room)
                    if not choices:
                        break
                    trial.floors[room].add(choices[alternative % len(choices)])
                else:
                    choices = _sibling_exchanges(trial, room, scope) if scope else []
                    if choices:
                        wall, donor_cell, donor = choices[alternative % len(choices)]
                        trial.floors[room].add(wall)
                        trial.floors[donor].remove(donor_cell)
                    else:
                        choices = _external_additions(trial, room, scope)
                        if not choices:
                            break
                        trial.floors[room].add(choices[alternative % len(choices)])
                    rebuild_walls(trial)
                if steps > target - len(state.floors[room]) + 1:
                    break
            trial.trace.extend(trace)
            result, failure = transaction.commit(lambda s: len(s.floors[room]) >= target)
            if result is not None:
                result.trace.append({'event': 'repair_scope_result', 'level': level,
                                     'scope': scope, 'attempts': attempts, 'status': 'repaired'})
                return RepairResult(result, 'repaired', scope, attempts)
            trace.append({'event': 'repair_attempt', 'level': level, 'scope': scope,
                          'instance': room, 'targetArea': target, 'alternative': alternative,
                          'failure': failure})
        trace.append({'event': 'repair_escalated', 'from': scope, 'instance': room,
                      'reason': 'local candidates exhausted or resource outside allocation',
                      'requestedArea': target, 'attempts': attempts})
    # Traces are diagnostic metadata, never geometry commits.
    state.trace.extend(trace)
    return RepairResult(state, 'budget_exhausted' if attempts >= limit else 'unresolved',
                        None, attempts, 'No verified repair within the searched alternatives')


def repair_feature(state: LayoutState, feature: str, settings: dict) -> RepairResult:
    if feature not in state.features:
        return RepairResult(state, 'unresolved', None, 0, 'Feature is not realized')
    pose = state.features[feature]
    room = pose['room']
    limit = settings.get('local_proposals', 200)
    attempts = 0
    for distance in range(1, 1 + max(1, int(limit ** 0.5))):
        offsets = [(x, y) for x in range(-distance, distance + 1)
                   for y in range(-distance, distance + 1) if abs(x) + abs(y) == distance]
        for dx, dy in offsets:
            if attempts >= limit:
                return RepairResult(state, 'budget_exhausted', room, attempts)
            attempts += 1
            tx = RepairTransaction(state, room, f'feature placement {feature}')
            new_pose = tx.candidate.features[feature]
            for key in ('cells', 'clearance'):
                new_pose[key] = {(x + dx, y + dy) for x, y in pose.get(key, set())}
            if not (new_pose['cells'] | new_pose.get('clearance', set())) <= state.floors[room]:
                continue
            # All preexisting feature failures must now be discharged, as well
            # as preserving previously satisfied obligations.
            result, _ = tx.commit()
            if result and not any(feature in str(c) for c in tx.report['hard_failures']):
                return RepairResult(result, 'repaired', room, attempts)
    return RepairResult(state, 'unresolved', room, attempts)


def publish_contracts(state: LayoutState, report: dict) -> dict:
    """Current, correlated contracts. No scalar offer is labelled a fit proof."""
    checks = report['checks']
    summaries = {}
    for key in reversed(list(state.program.spaces)):
        members = set(state.program.descendants(key))
        endpoints = members | {i for i, f in state.program.features.items() if f['room'] in members}
        footprint = state.aggregate(key)
        external = [r for r in state.program.relationships
                    if (r['source'] in endpoints) != (r['target'] in endpoints)]
        internal = [c for c in checks if c.get('owner') in members]
        digest = hashlib.sha256(json.dumps(json_ready(sorted(footprint))).encode()).hexdigest()[:16]
        summaries[key] = {
            'instance': key, 'revision': state.revision, 'alternativeId': digest,
            'exclusiveArea': len(state.floors.get(key, set())), 'aggregateArea': len(footprint),
            'footprint': footprint,
            'ports': [i for i in state.interfaces if (i['a'] in members) != (i['b'] in members)],
            'externalNeeds': [{'id': r['id'], 'declaredAt': r.get('owner'),
                               'type': r['type'], 'source': r['source'], 'target': r['target'],
                               'parameters': r['parameters']} for r in external],
            'verifiedLocalChecks': [c['id'] for c in internal if c['status'] == 'satisfied'],
            'unresolvedLocalChecks': [c['id'] for c in internal
                                      if c['status'] not in ('satisfied', 'inactive')],
            'assumptions': {'stateRevision': state.revision,
                            'externalGeometryMustBeRecheckedAfterMovement': True},
            'constructionAlternatives': [offer for offer in state.metadata.get('construction_alternatives', [])
                                         if offer.get('scope') == key],
        }
    state.summaries = summaries
    return summaries
