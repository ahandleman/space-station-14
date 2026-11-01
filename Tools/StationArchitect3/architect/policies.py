"""Explicit YAML construction policy; local interface declarations take priority."""
from functools import lru_cache
from pathlib import Path
import yaml


@lru_cache(maxsize=1)
def layout_policy():
    from validate import StrictLoader
    path = Path(__file__).resolve().parents[1] / 'data/layout-policy.yaml'
    result = yaml.load(path.read_text(encoding='utf-8'), Loader=StrictLoader)
    if not isinstance(result, dict) or set(result) != {'internalInterfaces'} or not isinstance(result['internalInterfaces'], dict):
        raise ValueError('Layout policy must define internalInterfaces as an actor/interface mapping.')
    return result


def internal_profile(program, actor=None, public=False, room=None):
    if room is not None:
        node = program.spaces[room]
        actor = actor or node['node'].get('access')
        for rule in node['requirements']:
            if rule['type'] == 'x_entry_interface_policy':
                return rule['parameters']['interface']
    actor = actor or ('public' if public else None)
    preferred = layout_policy()['internalInterfaces'].get(actor)
    definition = program.definitions.get('interfaces', {}).get(preferred)
    if definition:
        if 'crew_passage' not in definition.get('capabilities', []):
            raise ValueError(f'Configured internal interface {preferred} cannot carry crew.')
        levels = set(program.definitions['accessProfiles'].get(actor, {}).get('levels', []))
        clauses = definition['access']['anyOf']
        if clauses and not any(set(c) <= levels for c in clauses):
            return None
        return preferred
    # Small self-contained fixtures may declare their own generic door family.
    # They still need an authored compatible profile; no new profile is invented.
    from .packing import _profile_for
    return _profile_for(program, actor, public=public or actor == 'public')
