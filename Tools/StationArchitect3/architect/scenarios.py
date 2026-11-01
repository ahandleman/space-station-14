"""Placement scopes distinguish logical ownership from spatial organization.

A distributed collection is tested as local rooms/installations, never packed
into a pretend department. The source program and omitted ancestor obligations
remain available for later station integration.
"""
from __future__ import annotations

import copy
from .state import InstanceProgram


def organization(node):
    return node.get('organization', {'mode': 'clustered'})


def placement_scopes(full, owner):
    node = full.spaces[owner]
    config = organization(node['node'])
    mode = config['mode']
    children = list(node['children'])
    if mode == 'clustered':
        groups = [(node['template'], node['node']['name'], 'department', children)]
    else:
        grouped = set()
        groups = []
        for suite in config.get('suites', []):
            roots = [k for k in children if full.spaces[k]['template'] in suite['members']]
            if roots:
                groups.append((node['template']+'/'+suite['id'], suite['name'], 'installation', roots))
                grouped.update(roots)
        independent = set(config.get('independent', []))
        detached = [k for k in children if k not in grouped and
                    (mode == 'distributed' or full.spaces[k]['template'] in independent)]
        # A mandatory physical border keeps its endpoints in the same local unit.
        parent = {k: k for k in detached}
        def root(k):
            while parent[k] != k:
                parent[k] = parent[parent[k]]
                k = parent[k]
            return k
        membership = {d: k for k in detached for d in full.descendants(k)}
        for edge in full.relationships:
            if edge['type'] != 'shared_boundary' or edge.get('priority', 'mandatory') != 'mandatory':
                continue
            endpoints = [full.features.get(edge[s], {}).get('room', edge[s]) for s in ('source', 'target')]
            a, b = (membership.get(e) for e in endpoints)
            if a and b and root(a) != root(b):
                parent[root(b)] = root(a)
        units = {}
        for key in detached:
            units.setdefault(root(key), []).append(key)
        for roots in units.values():
            first = full.spaces[roots[0]]
            local = roots[0].split('/', 1)[1]
            key = node['template']+'/'+local
            name = ' + '.join(full.spaces[k]['node']['name'] for k in roots)
            repeated = sum(full.spaces[k]['template'] == first['template'] for k in children) > 1
            if len(roots) == 1 and repeated:
                name += f" {first['index']+1}"
            kind = 'room' if len(roots) == 1 and first['kind'] in ('room', 'passage') and not first['children'] else 'suite'
            groups.append((key, name, kind, roots))
        if mode == 'mixed':
            core = [k for k in children if k not in grouped and k not in detached]
            if core:
                groups.insert(0, (node['template']+'/core', node['node']['name']+' core', 'core', core))
    result = []
    all_rooms = set(full.descendants(owner, True))
    for key, name, kind, roots in groups:
        rooms = {r for k in roots for r in full.descendants(k, True)}
        # Physical owner nodes are unusual, but never silently lose their floor.
        if node['kind'] in ('room', 'passage'):
            rooms.add(owner)
        if not rooms:
            continue
        result.append({'id': key, 'name': name, 'kind': kind, 'organizationMode': mode,
                       'owner': owner, 'ownerTemplate': node['template'], 'roots': list(roots),
                       'memberTemplates': sorted({full.spaces[k]['template'] for k in roots}),
                       'wholeOwner': rooms == all_rooms})
    return result


def all_placement_scopes(full):
    return [scope for key, node in full.spaces.items() if node['kind'] == 'department'
            for scope in placement_scopes(full, key)]


def isolate_scope(full, scope_id):
    choices = [s for s in all_placement_scopes(full) if s['id'] == scope_id]
    if len(choices) != 1:
        raise ValueError(f'Choose an explicit placement scope; {scope_id!r} matches {len(choices)}.')
    scope = copy.deepcopy(choices[0])
    owner = scope['owner']
    members = {r for root in scope['roots'] for r in full.descendants(root)} | {owner}
    for key in list(members):
        members.update(k for k in full.ancestors(key) if k in full.descendants(owner))
    feature_ids = {k for k, f in full.features.items() if f['room'] in members}
    endpoints = members | feature_ids
    deferred = []
    spaces = {k: copy.deepcopy(n) for k, n in full.spaces.items() if k in members}
    for key, node in spaces.items():
        complete = set(full.descendants(key)) <= members
        node['children'] = [c for c in node['children'] if c in members]
        if not complete:
            active = [r for r in node['requirements'] if scope['kind'] == 'core'
                      and r['type'] == 'metric' and r['parameters']['metric'] == 'core.components']
            deferred.extend(copy.deepcopy([r for r in node['requirements'] if r not in active]))
            node['requirements'] = active
    edges = []
    for rule in full.relationships:
        if rule['source'] not in endpoints:
            continue
        if rule['source'] in spaces and not set(full.descendants(rule['source'])) <= members:
            deferred.append(copy.deepcopy(rule))
        else:
            edges.append(copy.deepcopy(rule))
    local = InstanceProgram(spaces=spaces,
                            features={k: copy.deepcopy(full.features[k]) for k in feature_ids},
                            relationships=edges, definitions=copy.deepcopy(full.definitions),
                            diagnostics=copy.deepcopy(full.diagnostics), source=copy.deepcopy(full.source),
                            source_hash=full.source_hash)
    contracts = [{'id': r['id'], 'declaredAt': r.get('owner'), 'type': r['type'],
                  'source': r['source'], 'target': r['target'], 'parameters': copy.deepcopy(r['parameters']),
                  'priority': r.get('priority', 'mandatory'), 'status': 'pending_station_integration'}
                 for r in edges if r['target'] not in endpoints and not str(r['target']).startswith('@domain/')]
    scope['deferredAncestorRequirements'] = deferred
    local.placement_scope = copy.deepcopy(scope)
    return local, owner, contracts, scope


def scope_bounds(program, owner):
    """Suggest a usable specimen size without changing authored area intervals."""
    from .department_packing import department_area_bounds
    from .fixtures import estimate_room
    import math
    bounds = department_area_bounds(program, owner)
    if any(r['type'] == 'metric' and r['parameters']['metric'] == 'area.aggregate'
           and 'preferred' in r['parameters'] for r in program.spaces[owner]['requirements']):
        return bounds
    recommended = sum(max(v['preferred'], min(v['max'], math.ceil(math.sqrt(
        estimate_room(program, key).get('recommended_area', 0)))**2))
                      for key, v in bounds['rooms'].items())
    bounds['preferred'] = int(min(bounds['max'], max(bounds['preferred'], recommended)))
    return bounds
