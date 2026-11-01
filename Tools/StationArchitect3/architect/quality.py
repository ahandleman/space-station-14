"""Local layout quality is distinct from hard geometry and station acceptance."""
from functools import lru_cache
from pathlib import Path
from .geometry import bbox


@lru_cache(maxsize=1)
def quality_policy():
    import yaml
    from validate import StrictLoader
    path = Path(__file__).resolve().parents[1] / 'data/layout-quality.yaml'
    value = yaml.load(path.read_text(encoding='utf-8'), Loader=StrictLoader)
    if value.get('format') != 'ss14-layout-quality' or value.get('version') != 1:
        raise ValueError('Unsupported layout quality policy.')
    for profile in value['profiles'].values():
        for rule in profile:
            if rule['metric'] not in {'core.aspect_ratio', 'core.fill_ratio', 'circulation.fraction', 'rooms.mean_fill_ratio'}:
                raise ValueError('Unsupported layout quality metric: '+rule['metric'])
            if rule.get('weight', 1) < 0 or not ({'min', 'max'} & rule.keys()):
                raise ValueError('Quality pressures need nonnegative weights and a bound.')
    return value


def layout_quality(state, owner, kind=None):
    scope = state.metadata.get('placementScope', {})
    kind = kind or scope.get('kind', 'department')
    config = state.program.spaces[owner]['node'].get('organization', {})
    detached = [*config.get('independent', []),
                *(p for suite in config.get('suites', []) for p in suite['members'])] if kind in ('department', 'core') else []
    rooms = [k for k in state.program.descendants(owner, True)
             if not state.program.spaces[k].get('harness')
             and state.program.spaces[k]['node']['placement'].get('zone') != 'outboard'
             and not any(state.program.spaces[k]['template'] == p or
                         state.program.spaces[k]['template'].startswith(p+'/') for p in detached)]
    floor = set().union(*(state.floors.get(k, set()) for k in rooms)) if rooms else set()
    x0, y0, x1, y1 = bbox(floor)
    width, height = x1-x0, y1-y0
    circulation = sum(len(state.floors.get(k, set())) for k in rooms
                      if 'department_circulation' in state.program.spaces[k]['node'].get('tags', []))
    metrics = {'core.aspect_ratio': max(width, height)/max(1, min(width, height)),
               'core.fill_ratio': len(floor)/max(1, width*height),
               'circulation.fraction': circulation/max(1, len(floor))}
    room_fills = []
    for key in state.program.descendants(owner, True):
        if state.program.spaces[key].get('harness'):
            continue
        cells = state.floors.get(key, set())
        ax, ay, bx, by = bbox(cells)
        room_fills.append(len(cells)/max(1, (bx-ax)*(by-ay)))
    metrics['rooms.mean_fill_ratio'] = sum(room_fills)/max(1, len(room_fills))
    checks = []
    for rule in quality_policy()['profiles'][kind]:
        value = metrics[rule['metric']]
        distance = max(rule.get('min', value)-value, value-rule.get('max', value), 0)
        reference = rule.get('max', rule.get('min', 1))
        cost = rule.get('weight', 1)*distance/max(0.01, reference)
        checks.append({**rule, 'measured': value, 'cost': cost,
                       'status': 'satisfied' if distance == 0 and floor else 'needs_review'})
    passed = bool(floor) and all(c['status'] == 'satisfied' for c in checks)
    return {'profile': kind, 'status': 'passes_profile' if passed else 'needs_review',
            'score': sum(c['cost'] for c in checks), 'checks': checks, 'metrics': metrics,
            'notEvaluated': ['station_distribution', 'furnishing_program_completeness', 'runtime_gameflow']}
