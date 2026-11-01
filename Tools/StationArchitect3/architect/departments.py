"""Full-catalog department experiments with explicit external boundary contracts.

Isolation never removes a selected local room, feature or authored requirement.
External landing geometry is a test harness, not a substitute for another department.
"""
from __future__ import annotations

import copy
from collections import deque
from . import VERSION

from .geometry import DIRECTIONS, bbox, components, neighbors, rect, square_anchors
from .instances import resolve_instances
from .packing import _profile_for, rebuild_walls
from .policies import internal_profile
from .settings import validate_settings
from .state import InstanceProgram
from .verify import Verifier, verify_layout


def department_ids(document):
    return [n['id'] for n in document['spaces'] if n['kind'] == 'department']


def isolate_department(document, department, seed=1, resolved=None):
    full = resolved if resolved is not None else resolve_instances(document, seed=seed)
    choices = [k for k, n in full.spaces.items() if n['kind'] == 'department'
               and department in (k, n['template'])]
    if len(choices) != 1:
        raise ValueError(f'Choose one department instance; {department!r} matches {len(choices)}.')
    key = choices[0]
    members = set(full.descendants(key))
    feature_ids = {k for k, f in full.features.items() if f['room'] in members}
    endpoints = members | feature_ids
    local = InstanceProgram(
        spaces={k: copy.deepcopy(v) for k, v in full.spaces.items() if k in members},
        features={k: copy.deepcopy(v) for k, v in full.features.items() if k in feature_ids},
        relationships=[copy.deepcopy(r) for r in full.relationships if r['source'] in endpoints],
        definitions=copy.deepcopy(full.definitions), diagnostics=copy.deepcopy(full.diagnostics),
        source=copy.deepcopy(full.source), source_hash=full.source_hash)
    contracts = []
    for rule in local.relationships:
        if rule['target'] not in endpoints and not str(rule['target']).startswith('@domain/'):
            contracts.append({'id': rule['id'], 'declaredAt': rule.get('owner'), 'type': rule['type'],
                              'source': rule['source'], 'target': rule['target'],
                              'parameters': copy.deepcopy(rule['parameters']),
                              'priority': rule.get('priority', 'mandatory'),
                              'status': 'pending_station_integration'})
    return local, key, contracts, full


def _endpoint_room(program, endpoint):
    return program.features.get(endpoint, {}).get('room', endpoint)


def _harness_node(full, target):
    original = full.spaces.get(target)
    if not original:
        return None
    n = copy.deepcopy(original)
    n.update(parent=None, owner=None, children=[], requirements=[], harness=True)
    n['harnessOwner'] = original.get('owner')
    n['harnessParent'] = original.get('parent')
    n['node']['features'] = []
    n['node']['spaces'] = []
    n['node']['requirements'] = []
    # This is a local landing for an exported port. It is deliberately not a
    # realization of the target room's full inventory, area or station network.
    n['node']['name'] = 'Boundary: ' + n['node'].get('name', target)
    return n


def _can_enter(state, room, target, full):
    program = state.program
    target_tags = full.spaces.get(target, {}).get('node', {}).get('tags', [])
    for r in program.spaces[room]['requirements']:
        if r['type'] != 'access_policy':
            continue
        p = r['parameters']
        if p.get('entryFrom') and target not in p['entryFrom']:
            return False
        if 'maintenance_circulation' in target_tags and not p.get('allowMaintenanceEntry', True):
            return False
        if not p.get('throughTraffic', True):
            owned = set(program.descendants(room, True))
            existing = [i for i in state.interfaces
                        if 'crew_passage' in program.definitions['interfaces'].get(i['profile'], {}).get('capabilities', [])
                        and ((i['a'] in owned) != (i['b'] in owned))]
            if existing:
                return False
    return True


def _landing(state, room, target, profile, width, full, source_cells=None, approach_width=None):
    """Reserve one exposed physical port plus a modest outside approach patch."""
    spec = state.program.definitions['interfaces'].get(profile, {})
    open_edge = spec.get('geometry') == 'open_edge'
    offset = 1 if open_edge else 2
    floor = state.floors.get(room, set()) if source_cells is None else source_cells
    used = set().union(*state.floors.values()) if state.floors else set()
    exclusions = {c for r in state.reservations for c in r['cells']}
    exclusions |= {n for c in list(exclusions) for n in neighbors(c)}
    openings = {c for i in state.interfaces for c in i['cells']}
    approaches = {c for i in state.interfaces for p in i['cells'] for c in neighbors(p)}
    blocked = {c for pose in state.features.values() for c in pose.get('blocking_cells', pose.get('cells', set()))}
    approach_width = width if approach_width is None else approach_width
    # An ordinary hall can bend around a wing. Check swept clear-width squares
    # against the outside component, rather than requiring a shuttle-like ray.
    obstacles = used | state.walls | exclusions
    minx, miny, maxx, maxy = bbox(obstacles)
    pad = max(4, width + 1)
    free = rect(minx-pad, miny-pad, maxx-minx+2*pad+1, maxy-miny+2*pad+1) - obstacles
    anchors = square_anchors(free, width)
    outside = {p for p in anchors if p[0] == minx-pad or p[1] == miny-pad
               or p[0] == maxx+pad-width+1 or p[1] == maxy+pad-width+1}
    queue = deque(outside)
    while queue:
        for n in neighbors(queue.popleft()):
            if n in anchors and n not in outside:
                outside.add(n)
                queue.append(n)
    candidates = []
    for nx, ny in DIRECTIONS:
        tx, ty = -ny, nx
        for x, y in sorted(floor):
            source_run = {(x + j*tx, y + j*ty) for j in range(width)}
            if not source_run <= floor or source_run & blocked:
                continue
            approach = {(px - step*nx, py - step*ny) for px, py in source_run for step in range(approach_width)}
            if not approach <= floor or approach & blocked:
                continue
            seam = source_run if open_edge else {(px+nx, py+ny) for px, py in source_run}
            if seam & openings or (not open_edge and seam & used):
                continue
            depth = max(3, width + (1 if open_edge else 0))
            landing = {(px + step*nx, py + step*ny)
                       for px, py in source_run for step in range(offset, offset+depth)}
            if landing & (used | exclusions | openings | approaches) or seam & exclusions:
                continue
            # Maintain a full structural separation from unrelated room floor.
            allowed_touch = source_run if open_edge else set()
            if any(n in used - allowed_touch for c in landing for n in neighbors(c)):
                continue
            # Only expose ports to unbounded outside, never drill into a closed
            # interior room pocket and pretend it is a future station neighbor.
            if not square_anchors(landing, width) & outside:
                continue
            candidates.append(((x, y, nx, ny), seam, landing, (nx, ny)))
    if not candidates:
        return None
    _, seam, patch, normal = min(candidates)
    if target not in state.program.spaces:
        n = _harness_node(full, target)
        if n is None:
            return None
        state.program.spaces[target] = n
    state.floors.setdefault(target, set()).update(patch)
    interface = {'id': f'boundary:{len(state.interfaces)}:{room}->{target}',
                 'a': room, 'b': target, 'profile': profile, 'cells': seam, 'normal': normal,
                 'testHarness': True}
    state.interfaces.append(interface)
    rebuild_walls(state)
    state.metadata.setdefault('boundary_ports', []).append(
        {'room': room, 'target': target, 'interface': interface['id'], 'width': width,
         'landing': patch, 'status': 'conditional_on_external_composition'})
    return interface


def attach_boundary_harness(state, department, full, contracts):
    members = set(state.program.descendants(department))
    for contract in sorted(contracts, key=lambda c: (c['type'] != 'shared_boundary', c['id'])):
        rule = next(r for r in state.program.relationships if r['id'] == contract['id'])
        if rule['type'] not in ('route', 'shared_boundary') or rule['target'] not in full.spaces:
            continue
        source = _endpoint_room(state.program, rule['source'])
        if source not in members:
            continue
        target, p = rule['target'], rule['parameters']
        if rule['type'] == 'route' and p.get('mode') == 'eva':
            _eva_landing(state, target, p, full)
            continue
        checker = Verifier(state)
        if rule['type'] == 'route' and target in state.floors and checker.route(source, target, p) is not None:
            continue
        if rule['type'] == 'shared_boundary':
            profile, width, gateways = p['interface'], p['minLength'], [source]
            approach_width = 1
            if 'crew_passage' in state.program.definitions['interfaces'][profile].get('capabilities', []):
                # One doorway must serve both its shared-border obligation and
                # the widest local route using that same boundary contract.
                route_widths = [c['parameters'].get('minWidth', 1) for c in contracts
                                if c['type'] == 'route' and c['source'] == rule['source']
                                and c['target'] == target]
                width = max([width] + route_widths)
                approach_width = max([1] + route_widths)
        else:
            width = p.get('minWidth', 1)
            approach_width = width
            actor = p.get('accessProfile', 'public')
            # Prefer existing internal circulation gateways so branch rooms do
            # not gain an unnecessary second entrance and prohibited through route.
            gateways = sorted((i for i in members if i in state.floors),
                              key=lambda i: ('department_circulation' not in state.program.spaces[i]['node'].get('tags', []), i != source, i))
            gateways = [i for i in gateways if i == source or checker.route(source, i, p) is not None]
            profile = _profile_for(state.program, actor, public=actor == 'public')
        if not profile:
            continue
        for room in gateways:
            walking = 'crew_passage' in state.program.definitions['interfaces'].get(profile, {}).get('capabilities', [])
            if walking and not _can_enter(state, room, target, full):
                continue
            if rule['type'] == 'route':
                profile = internal_profile(state.program, p.get('accessProfile', 'public'), room=room)
                if not profile:
                    continue
            if _landing(state, room, target, profile, width, full, approach_width=approach_width):
                break
    # The public test origin provides a real approach to maintenance-only rooms.
    # This makes their no-bypass tests non-vacuous without claiming a full network.
    public = next((k for k, n in full.spaces.items() if 'public_circulation' in n['node'].get('tags', [])), None)
    maintenance = [k for k, n in state.program.spaces.items() if n.get('harness') and 'maintenance_circulation' in n['node'].get('tags', [])]
    from .boundary_network import join_boundary_patches
    for key in maintenance:
        join_boundary_patches(state, key, width=2)
    if public:
        profile = internal_profile(state.program, 'maintenance')
        if profile:
            for key in maintenance:
                for component in components(state.floors.get(key, set())):
                    _landing(state, key, public, profile, 2, full, component)
    state.metadata['harnessSpaces'] = [k for k, n in state.program.spaces.items() if n.get('harness')]
    if state.floors:
        all_cells = set().union(*state.floors.values(), state.walls)
        for r in state.reservations:
            all_cells.update(r['cells'])
        a, b, c, d = bbox(all_cells)
        state.bounds = (a-3, b-3, c+3, d+3)


def _eva_landing(state, target, parameters, full):
    """A remote airlock test port, reached through actual exterior vacuum."""
    if target in state.floors:
        return
    original = full.spaces[target]
    profiles = [r['parameters']['interface'] for r in original['requirements']
                if r['type'] == 'perimeter' and 'crew_passage' in
                full.definitions['interfaces'][r['parameters']['interface']].get('capabilities', [])]
    if not profiles:
        return
    actor = full.definitions['accessProfiles'][parameters['accessProfile']]
    profile = next((key for key in profiles if not full.definitions['interfaces'][key]['access']['anyOf']
                    or any(set(clause) <= set(actor['levels']) for clause in
                           full.definitions['interfaces'][key]['access']['anyOf'])), None)
    if profile is None:
        return
    width = parameters.get('minWidth', 1)
    occupied = set().union(*state.floors.values(), state.walls)
    occupied |= {c for r in state.reservations for c in r['cells']}
    ax, ay, bx, by = bbox(occupied)
    size = max(3, width)
    x, y = bx+4, ay
    patch = rect(x, y, size, size)
    state.program.spaces[target] = _harness_node(full, target)
    state.floors[target] = patch
    seam = {(x+size, y+j) for j in range(width)}
    interface = {'id': 'eva-boundary:'+target, 'a': target, 'b': '@domain/exterior_space',
                 'profile': profile, 'cells': seam, 'normal': (1, 0), 'testHarness': True}
    state.interfaces.append(interface)
    state.reservations.append({'id': 'eva-approach:'+target, 'owner': target, 'kind': 'exterior',
                               'cells': {(xx+step, yy) for xx, yy in seam for step in range(1, 4)}})
    rebuild_walls(state)
    state.metadata.setdefault('boundary_ports', []).append(
        {'room': target, 'target': '@domain/exterior_space', 'interface': interface['id'],
         'width': width, 'landing': patch, 'status': 'conditional_on_external_composition'})


def _native_classification(rule):
    # Keep the complete native obligation visible. A layout test is not a test
    # that machinery has gas, power, inventory, persistent links or runtime state.
    return ('service_topology' if rule['type'] == 'service_link' else
            'native_operation' if rule['type'].startswith('x_') or rule['type'] == 'environment'
            else 'unsupported_geometry')


def verify_department(state, department, contracts=None, include_equipment=True):
    from .fixtures import assess_features
    from .department_rules import spatial_rule_checks
    contracts = contracts if contracts is not None else state.metadata.get('externalContracts', [])
    external_ids = {c['id'] for c in contracts}
    base = verify_layout(state)
    adapted = spatial_rule_checks(state)
    completed = set(adapted['completed_rule_ids'])
    partial_ids = {c.get('sourceRule') for c in adapted.get('partial_checks', [])}
    eva_ids = {r['id'] for r in state.program.relationships
               if r['type'] == 'route' and r['parameters'].get('mode') == 'eva'}
    spatial_checks, boundary_checks, pending = [], [], []
    for check in base['checks']:
        if check['id'] in eva_ids & partial_ids:
            pending.append({**check, 'facet': 'native_operation'})
            continue
        if check['id'] in external_ids:
            boundary_checks.append(check)
            continue
        if check['type'] == 'native_feature':
            continue
        if check['id'] in completed:
            continue
        if check['status'] == 'unknown':
            facet = _native_classification(check)
            if facet == 'unsupported_geometry':
                spatial_checks.append({**check, 'facet': facet})
            else:
                pending.append({**check, 'facet': facet})
        else:
            spatial_checks.append(check)
    spatial_checks.extend(adapted['checks'])
    for check in adapted.get('partial_checks', []):
        if check.get('sourceRule') in external_ids:
            boundary_checks.append(check)
        else:
            spatial_checks.append(check)
    if include_equipment:
        equipment = assess_features(state)
        if isinstance(equipment, dict):
            equipment = equipment.get('checks', [])
        spatial_checks.extend(equipment)
    mandatory_bad = [c for c in spatial_checks if c.get('priority', 'mandatory') == 'mandatory'
                     and c['status'] not in ('satisfied', 'inactive')]
    # Crossing-room physical seams and the local part of walking obligations
    # must actually work in their harness. External machinery links stay pending.
    boundary_bad = [c for c in boundary_checks if c['type'] in ('route', 'shared_boundary')
                    and c.get('priority', 'mandatory') == 'mandatory' and c['status'] != 'satisfied']
    owned_rooms = state.program.descendants(department, True)
    expected_features = [k for k, f in state.program.features.items() if f['room'] in owned_rooms]
    abstract = [k for k in expected_features if not state.program.features[k]['node'].get('prototypes')
                and not state.program.features[k]['node'].get('assembly')]
    physical_features = [k for k in expected_features if k not in abstract]
    native_parts = sum(len(state.program.definitions.get('assemblies', {}).get(
        state.program.features[k]['node'].get('assembly'), {}).get('parts', []))
        if state.program.features[k]['node'].get('assembly') else 1 for k in physical_features)
    inventory = {'selectedRooms': len(owned_rooms), 'realizedRooms': sum(bool(state.floors.get(i)) for i in owned_rooms),
                 'selectedFeatures': len(physical_features), 'realizedFeatures': sum(i in state.features for i in physical_features),
                 'abstractServices': len(abstract), 'totalDeclaredFeatures': len(expected_features),
                 'selectedNativeParts': native_parts,
                 'realizedNativeParts': sum(len(state.features.get(k, {}).get('prototype_parts', [])) for k in physical_features),
                 'missingRooms': [i for i in owned_rooms if not state.floors.get(i)],
                 'missingFeatures': [i for i in physical_features if i not in state.features]}
    for rule in state.metadata.get('deferredAncestorRequirements', []):
        pending.append({**rule, 'status': 'unknown', 'facet': 'owner_organization',
                        'message': 'This owner requirement applies to all its placements together; a local specimen cannot certify it.'})
    requested = state.metadata.get('requestedDepartmentArea')
    actual = len(state.aggregate(department))
    if requested is not None:
        check = {'id': department+'::requested_size', 'owner': department, 'type': 'requested_size',
                 'priority': 'mandatory', 'status': 'satisfied' if actual == requested else 'violated',
                 'message': 'Requested department usable floor without boundary harness or structural cells.',
                 'measured': {'requested': requested, 'actual': actual}}
        spatial_checks.append(check)
        if check['status'] != 'satisfied':
            mandatory_bad.append(check)
    ready = not mandatory_bad and not boundary_bad and not inventory['missingRooms'] and (not include_equipment or not inventory['missingFeatures'])
    x0, y0, x1, y1 = bbox(state.aggregate(department))
    from .quality import layout_quality
    quality = layout_quality(state, department)
    return {'checks': spatial_checks + boundary_checks + pending,
            'hard_failures': mandatory_bad + boundary_bad,
            'unknown_mandatory': [c for c in pending if c.get('priority', 'mandatory') == 'mandatory'],
            'verified': ready, 'localLayoutVerified': ready, 'fullDepartmentVerified': ready and not pending and not contracts,
            'soft_costs': base['soft_costs'], 'metrics': {**base['metrics'], 'department_floor': actual,
                'department_width': x1-x0, 'department_height': y1-y0,
                'department_aspect': (x1-x0)/max(1, y1-y0)},
            'inventory': inventory, 'boundaryChecks': boundary_checks, 'pendingRequirements': pending,
            'layoutQuality': quality,
            'externalContracts': contracts,
            'facets': {'local_layout_and_inventory': 'verified' if ready else 'incomplete',
                       'station_integration': 'pending', 'station_distribution': 'not_evaluated',
                       'native_operation': 'not_evaluated'}}


def generate_department(document, department, target_area=None, aspect=1.0, seed=1, settings=None, resolved=None, scenario=None):
    from .department_packing import pack_department
    from .exterior import realize_exterior
    from .fixtures import place_features
    settings = validate_settings({} if settings is None else settings)
    scope = None
    if scenario is None:
        local, key, contracts, full = isolate_department(document, department, seed, resolved)
    else:
        from .scenarios import isolate_scope, scope_bounds
        full = resolved if resolved is not None else resolve_instances(document, seed)
        local, key, contracts, scope = isolate_scope(full, scenario)
        if department not in (key, local.spaces[key]['template']):
            raise ValueError('The placement scope does not belong to the chosen owner.')
        if target_area is None:
            target_area = scope_bounds(local, key)['preferred']
    if target_area is not None and (type(target_area) is not int or target_area < 1):
        raise ValueError('Department area must be a positive integer.')
    if not isinstance(aspect, (int, float)) or not 0.25 <= aspect <= 4:
        raise ValueError('Department aspect must be between 0.25 and 4.')
    best, best_rank, attempts, used = None, None, [], 0
    for attempt in range(settings['parent_alternatives']):
        remaining = max(0, settings['max_proposals'] - used)
        state = pack_department(copy.deepcopy(local), key, target_area, aspect=aspect,
                                seed=seed + attempt * 104729, settings={**settings, 'max_proposals': remaining})
        used += state.metadata.get('work_used', 0)
        state.metadata.update(departmentInstance=key, departmentTemplate=local.spaces[key]['template'],
                              requestedDepartmentArea=state.metadata.get('requested_area', target_area),
                              externalContracts=contracts, instanceSeed=seed)
        if scope is not None:
            state.metadata.update(placementScenario=scenario, placementScope=scope,
                                  deferredAncestorRequirements=scope['deferredAncestorRequirements'])
        realize_exterior(state, key)
        attach_boundary_harness(state, key, full, contracts)
        for room in list(state.program.descendants(key, True)):
            if room in state.floors:
                place_features(state, room, seed + attempt)
        verification = verify_department(state, key, contracts)
        inventory = verification['inventory']
        rank = (len(verification['hard_failures']), len(inventory['missingRooms']),
                len(inventory['missingFeatures']), verification['layoutQuality']['score'])
        attempts.append({'attempt': attempt, 'localFailures': rank[0], 'missingRooms': rank[1],
                         'missingFeatures': rank[2], 'termination': state.metadata.get('termination')})
        if best is None or rank < best_rank:
            best, best_rank = (state, verification), rank
        if (verification['verified'] and verification['layoutQuality']['status'] == 'passes_profile') or state.metadata.get('termination') == 'infeasible_bounds' or used >= settings['max_proposals']:
            break
    state, verification = best
    state.metadata['work_used'] = used
    state.metadata['departmentAttempts'] = attempts
    state.trace.append({'event': 'department_candidate_selection', 'attempts': attempts,
                        'selectedFailures': best_rank[0], 'selectionChanged': False})
    from .repair import publish_contracts
    publish_contracts(state, verification)
    report = {'format': 'ss14-architect-department-report', 'schemaVersion': 1,
              'generatorVersion': VERSION, 'seed': seed, 'sourceHash': local.source_hash,
              'settings': settings, 'department': key, 'requestedArea': target_area, 'aspect': aspect,
              'termination': state.metadata.get('termination', 'completed'),
              'candidateStatus': 'verified_local_department_layout' if verification['verified'] else 'incomplete_department',
              'requestedScope': ['department_inventory', 'local_layout', 'boundary_approaches'],
              'notEvaluated': ['station_integration', 'native_runtime_operation'],
              'verification': verification, 'workUsed': state.metadata.get('work_used', 0), 'attempts': attempts}
    if scope is not None:
        report['scope'] = {k: v for k, v in scope.items() if k not in ('roots', 'deferredAncestorRequirements')}
        if not scope['wholeOwner']:
            report['candidateStatus'] = 'verified_local_placement' if verification['verified'] else 'incomplete_placement'
            report['notEvaluated'].extend(['station_distribution', 'owner_aggregate_constraints'])
            report['requestedScope'] = ['placement_inventory', 'local_layout', 'boundary_approaches']
    else:
        mode = local.spaces[key]['node'].get('organization', {}).get('mode', 'clustered')
        if mode != 'clustered':
            # Keep the low-level whole-owner experiment available to research
            # callers without presenting it as an authored placement scope.
            report['scope'] = {'id': local.spaces[key]['template'], 'owner': key,
                               'kind': 'collection_experiment', 'organizationMode': mode,
                               'name': local.spaces[key]['node']['name'], 'wholeOwner': True}
            report['candidateStatus'] = 'collection_constraint_experiment'
            report['requestedScope'] = ['selected_owner_inventory', 'geometry_experiment']
            report['notEvaluated'].extend(['owner_spatial_organization', 'station_distribution'])
    return state, report


def resize_department(state, target_area, aspect=1.0, settings=None):
    """Re-solve one owner with unchanged source/selection; refuse a failed edit."""
    seed = state.metadata.get('instanceSeed', 1)
    candidate, report = generate_department(state.program.source, state.metadata['departmentTemplate'],
                                             target_area, aspect, seed, settings,
                                             scenario=state.metadata.get('placementScenario'))
    before = {i for i, n in state.program.spaces.items() if not n.get('harness')}
    after = {i for i, n in candidate.program.spaces.items() if not n.get('harness')}
    unchanged = before == after and set(state.program.features) == set(candidate.program.features)
    accepted = report['verification']['verified'] and unchanged
    report['resizeAccepted'] = accepted
    report['selectionPreserved'] = unchanged
    candidate.trace.append({'event': 'department_resize', 'requestedArea': target_area,
                            'previousArea': len(state.aggregate(state.metadata['departmentInstance'])),
                            'accepted': accepted, 'selectionPreserved': unchanged})
    return (candidate if accepted else state), report
