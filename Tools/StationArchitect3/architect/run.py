"""Generation orchestration, deterministic artifacts and independent replay."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

from . import VERSION
from .instances import resolve_instances
from .packing import generate_layout
from .repair import publish_contracts, repair_area
from .render import write_preview
from .settings import DEFAULTS, validate_settings
from .state import LayoutState, json_ready
from .verify import verify_layout


def _hash(value):
    return hashlib.sha256(json.dumps(json_ready(value), sort_keys=True,
                                     separators=(',', ':')).encode()).hexdigest()


def shape_signature(state):
    """Canonical whole-layout signature modulo translations and dihedral poses."""
    representations = []
    for flip in (1, -1):
        for rotation in range(4):
            rooms = {}
            for key, cells in sorted(state.floors.items()):
                transformed = []
                for x, y in cells:
                    x *= flip
                    for _ in range(rotation):
                        x, y = -y, x
                    transformed.append((x, y))
                rooms[key] = transformed
            cells = [c for cs in rooms.values() for c in cs]
            if not cells:
                return _hash([])
            minx, miny = min(c[0] for c in cells), min(c[1] for c in cells)
            representations.append(json.dumps({k: sorted((x-minx, y-miny) for x, y in cs)
                                               for k, cs in rooms.items()}, sort_keys=True))
    return hashlib.sha256(min(representations).encode()).hexdigest()


def _rank(report):
    failures = report.get('hard_failures', [])
    unknown = report.get('unknown_mandatory', [])
    costs = report.get('soft_costs', {})
    if isinstance(costs, dict):
        soft = tuple(costs[k] for k in sorted(costs, key=lambda x: int(x)))
    else:
        soft = (float(costs),)
    return len(failures), len(unknown), soft


def _policy_score(state, verification, settings):
    # Exact geometric comparisons use a fixed selected program; never omit rooms
    # to win compaction. Evaluator metrics remain visible separately in the report.
    cells = [c for key, floor in state.floors.items()
             if state.program.spaces[key]['node']['placement'].get('zone') != 'outboard'
             for c in floor]
    if not cells:
        # Feasibility ranks an empty candidate last already. Reports must remain
        # strict JSON, including intentional zero/tiny-budget runs.
        return 0.0
    cx = sum(x for x, y in cells)/len(cells)
    cy = sum(y for x, y in cells)/len(cells)
    reference = 0.0
    for n in state.program.spaces.values():
        if n['kind'] not in ('room', 'passage'):
            continue
        requirements = [r for r in n['requirements'] if r['type'] == 'metric'
                        and r['parameters']['metric'] == 'area.exclusive']
        reference += next((r['parameters'].get('preferred', r['parameters'].get('min', 1))
                           for r in requirements), 1)
    compactness = sum((x-cx)**2+(y-cy)**2 for x, y in cells)/len(cells)/max(1, reference)
    metrics = verification.get('metrics', {})
    wrap = metrics.get('mean_wrap', 0)
    if isinstance(wrap, dict):
        wrap = sum(wrap.values())/max(1, len(wrap))
    if not isinstance(wrap, (int, float)):
        wrap = 0
    split = 0
    for key, count in metrics.get('department_components', {}).items():
        node = state.program.spaces[key]['node']
        organization = node.get('organization', {})
        if organization.get('mode') == 'distributed':
            continue
        if organization.get('mode') == 'mixed':
            from .verify import Verifier
            count = Verifier(state).metric(key, {'id': 'policy:core_components', 'type': 'metric',
                'parameters': {'metric': 'core.components', 'min': 1, 'scale': 1}})['measured']
        tags = node.get('tags', [])
        weight = settings['maintenance_split_weight'] if 'maintenance_owner' in tags else settings['distributed_split_weight'] if 'distributed' in tags else settings['split_weight']
        split += weight * max(0, count - 1)
    score = settings.get('compactness_weight', 4)*compactness + settings.get('wrap_weight', 4)*(1-wrap) + split
    metrics['core_squared_radius_over_reference_area'] = compactness
    metrics['search_policy_cost'] = score
    return score


def _repair_area_deficits(state, settings):
    work = 0
    for key, n in state.program.spaces.items():
        if n['kind'] != 'room' or key not in state.floors:
            continue
        minimum = max([0] + [r['parameters'].get('min', 0) for r in n['requirements']
                             if r['type'] == 'metric' and r.get('priority', 'mandatory') == 'mandatory'
                             and r['parameters']['metric'] == 'area.exclusive'])
        if len(state.floors[key]) < minimum:
            remaining = max(0, settings['max_proposals'] - work)
            result = repair_area(state, key, int(minimum),
                                 {**settings, 'local_proposals': min(settings['local_proposals'], remaining)})
            work += result.attempts
            state = result.state
    return state, work


def run_generation(document, settings=None, seed=1, candidate_only=False):
    from .verify import capability_report
    settings = validate_settings({} if settings is None else settings)
    program = resolve_instances(document, seed=seed)
    unsupported = capability_report(program)
    selection_errors = [d for d in program.diagnostics if d.get('severity', 'error') == 'error']
    blockers = [d for d in unsupported if d.get('priority', 'mandatory') == 'mandatory']
    attempted = []
    used = 0
    best = None
    best_report = None
    best_rank = None
    termination = 'completed'
    if selection_errors or (blockers and not candidate_only):
        best = LayoutState(program)
        checks = [{'id': gap['id'], 'owner': gap.get('owner'), 'type': gap['type'],
                   'priority': gap.get('priority', 'mandatory'), 'status': 'unknown',
                   'message': gap['reason']} for gap in unsupported]
        best_report = {'checks': checks, 'hard_failures': [],
                       'unknown_mandatory': [c for c in checks if c['priority'] == 'mandatory'],
                       'soft_costs': {}, 'metrics': {}, 'verified': False,
                       'facets': {'layout': 'not_evaluated', 'native_realization': 'not_evaluated',
                                  'runtime_operation': 'not_evaluated'}}
        termination = 'selection_unresolved' if selection_errors else 'unsupported_required'
    else:
        for attempt in range(max(1, settings['root_restarts'])):
            remaining = settings['max_proposals'] - used
            if remaining <= 0:
                termination = 'budget_exhausted'
                break
            candidate_seed = seed if attempt == 0 else int(_hash([seed, 'packing', attempt])[:8], 16)
            state = generate_layout(program, {**settings, 'max_proposals': remaining}, seed=candidate_seed)
            used += int(state.metadata.get('work_used', 0))
            state, repair_work = _repair_area_deficits(state, {**settings, 'max_proposals': max(0, settings['max_proposals']-used)})
            used += repair_work
            verification = verify_layout(state)
            policy = _policy_score(state, verification, settings)
            # Additional search preferences share authored default tier zero.
            costs = dict(verification.get('soft_costs', {}))
            costs['0'] = costs.get('0', costs.pop(0, 0)) + policy
            verification['soft_costs'] = costs
            ranking = _rank(verification)
            attempted.append({'attempt': attempt, 'packingSeed': candidate_seed,
                              'workUsed': state.metadata.get('work_used', 0) + repair_work,
                              'hardFailures': len(verification['hard_failures']),
                              'unknownMandatory': len(verification['unknown_mandatory']),
                              'softCosts': costs})
            if best_rank is None or ranking < best_rank:
                best, best_report, best_rank = state, verification, ranking
            if state.metadata.get('termination') == 'budget_exhausted' or used >= settings['max_proposals']:
                termination = 'budget_exhausted'
                break
        if best is None:
            best = LayoutState(program)
            best_report = verify_layout(best)
        elif not best_report['verified'] and termination == 'completed':
            termination = 'search_exhausted'
    publish_contracts(best, best_report)
    status = 'verified_for_requested_scope' if best_report['verified'] else 'incomplete' if best.floors else 'none'
    report = {
        'format': 'ss14-architect-generation-report', 'schemaVersion': 1,
        'generatorVersion': VERSION, 'sourceHash': program.source_hash,
        'settingsHash': _hash(settings), 'seed': seed, 'settings': settings,
        'termination': termination, 'candidateStatus': status,
        'requestedScope': ['selection', 'layout_geometry', 'traversal_access'],
        'notEvaluated': ['native_map_realization', 'runtime_operation'],
        'capabilityGaps': unsupported, 'selectionDiagnostics': program.diagnostics,
        'workUsed': used, 'attempts': attempted, 'verification': best_report,
        'shapeSignature': shape_signature(best),
        'selectedInstances': len(program.spaces),
        'selectedFeatures': len(program.features),
    }
    return best, report


def save_run(state, report, directory):
    out = Path(directory)
    out.mkdir(parents=True, exist_ok=True)
    payload = {'format': 'ss14-architect-layout', 'schemaVersion': 1,
               'sourceDocument': state.program.source, 'instanceSeed': report['seed'],
               'sourceHash': state.program.source_hash,
               'geometry': {k: v for k, v in vars(state).items() if k != 'program'}}
    (out / 'layout.json').write_text(json.dumps(json_ready(payload), indent=2, allow_nan=False), encoding='utf-8')
    (out / 'report.json').write_text(json.dumps(json_ready(report), indent=2, allow_nan=False), encoding='utf-8')
    (out / 'contracts.json').write_text(json.dumps(json_ready(state.summaries), indent=2, allow_nan=False), encoding='utf-8')
    write_preview(state, report, out)


def load_layout(path):
    payload = json.loads(Path(path).read_text(encoding='utf-8'))
    if payload.get('format') != 'ss14-architect-layout' or payload.get('schemaVersion') != 1:
        raise ValueError('Unsupported saved layout format/version')
    if not {'sourceDocument', 'instanceSeed', 'sourceHash', 'geometry'} <= payload.keys():
        raise ValueError('Saved layout is missing its source, seed, hash or geometry')
    program = resolve_instances(payload['sourceDocument'], seed=payload['instanceSeed'])
    if program.source_hash != payload['sourceHash']:
        raise ValueError('Saved source hash does not match its document')
    geometry = payload['geometry']
    metadata = geometry.get('metadata', {})
    if metadata.get('departmentTemplate'):
        from .departments import isolate_department, _harness_node
        full = program
        if metadata.get('placementScenario'):
            from .scenarios import isolate_scope
            program, department, contracts, scope = isolate_scope(full, metadata['placementScenario'])
            if scope['ownerTemplate'] != metadata['departmentTemplate']:
                raise ValueError('Saved placement scope does not match its owner.')
            metadata['placementScope'] = scope
            metadata['deferredAncestorRequirements'] = scope['deferredAncestorRequirements']
        else:
            program, department, contracts, _ = isolate_department(
                payload['sourceDocument'], metadata['departmentTemplate'], payload['instanceSeed'], full)
        allowed_landings = {c['target'] for c in contracts
                            if c['type'] in ('route', 'shared_boundary') and c['target'] in full.spaces}
        if any('maintenance_circulation' in full.spaces[k]['node'].get('tags', []) for k in allowed_landings):
            allowed_landings.update(k for k, n in full.spaces.items()
                                    if 'public_circulation' in n['node'].get('tags', []))
        harness = []
        for key in geometry.get('floors', {}):
            if key not in program.spaces and key in allowed_landings:
                program.spaces[key] = _harness_node(full, key)
                harness.append(key)
        # Recompute the test scope and external declarations from authored input;
        # saved summaries cannot waive local rooms or invent external targets.
        metadata['departmentInstance'] = department
        metadata['externalContracts'] = contracts
        metadata['harnessSpaces'] = harness
    state = LayoutState(program)
    for name in ('floors', 'envelopes'):
        setattr(state, name, {k: {tuple(c) for c in cells} for k, cells in geometry.get(name, {}).items()})
    state.walls = {tuple(c) for c in geometry.get('walls', [])}
    for name in ('interfaces', 'reservations'):
        value = geometry.get(name, [])
        for item in value:
            item['cells'] = {tuple(c) for c in item['cells']}
            if 'normal' in item:
                item['normal'] = tuple(item['normal'])
        setattr(state, name, value)
    state.features = geometry.get('features', {})
    for pose in state.features.values():
        for key in ('cells', 'clearance', 'blocking_cells'):
            if key in pose or key != 'blocking_cells':
                pose[key] = {tuple(c) for c in pose.get(key, [])}
    state.strips = {k: [{tuple(c) for c in strip} for strip in strips]
                    for k, strips in geometry.get('strips', {}).items()}
    state.bounds = tuple(geometry.get('bounds', [0, 0, 0, 0]))
    state.revision = geometry.get('revision', 0)
    state.trace = geometry.get('trace', [])
    state.metadata = metadata
    return state
