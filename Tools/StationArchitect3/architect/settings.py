"""Strict runtime search policy, deliberately separate from station YAML."""
from pathlib import Path
import math
import yaml

DEFAULTS = {
    'suite_alternatives': 3, 'parent_alternatives': 4, 'local_proposals': 200,
    'root_restarts': 4, 'max_proposals': 100000, 'maintenance_width': 2,
    'department_gap': 5, 'compactness_weight': 4.0, 'wrap_weight': 4.0,
    'split_weight': 12.0, 'distributed_split_weight': 2.0,
    'maintenance_split_weight': 1.0, 'area_jitter': 0.12, 'room_growth_steps': 200,
}


def validate_settings(values):
    if not isinstance(values, dict):
        raise ValueError('Generation settings must be a mapping.')
    unknown = set(values) - set(DEFAULTS)
    if unknown:
        raise ValueError('Unknown generation settings: ' + ', '.join(sorted(unknown)))
    result = {**DEFAULTS, **values}
    for key, value in result.items():
        if type(value) not in (int, float) or not math.isfinite(value):
            raise ValueError(f'{key} must be a finite number.')
        if key in ('compactness_weight', 'wrap_weight', 'split_weight',
                   'distributed_split_weight', 'maintenance_split_weight', 'area_jitter'):
            if value < 0:
                raise ValueError(f'{key} must be nonnegative.')
        elif type(value) is not int or value < (0 if key in ('room_growth_steps', 'local_proposals', 'max_proposals') else 1):
            raise ValueError(f'{key} must be an integer within its nonnegative/positive work limit.')
    if result['maintenance_width'] not in (2, 3):
        raise ValueError('maintenance_width must be 2 or 3 clear tiles.')
    if result['department_gap'] < result['maintenance_width'] + 2:
        raise ValueError('department_gap must reserve maintenance width plus two structural tiles.')
    if result['area_jitter'] > 0.5:
        raise ValueError('area_jitter must be between zero and 0.5; hard bounds are never randomized.')
    return result


def load_settings(path=None):
    if path is None:
        return dict(DEFAULTS)
    # Use the same duplicate-key rejection as authored programs.
    from validate import StrictLoader
    value = yaml.load(Path(path).read_text(encoding='utf-8'), Loader=StrictLoader)
    return validate_settings(value)
