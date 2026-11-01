"""Select concrete repeated instances and bind local V3 relationships.

The compiler remains the authoring authority. This adapter selects a finite
configuration; unresolved activation is reported rather than called impossible.
"""
from __future__ import annotations

import copy
import hashlib
import json

import validate as V
from .state import InstanceProgram


def resolve_instances(document: dict, seed: int = 1, counts: dict | None = None) -> InstanceProgram:
    compiled = V.compile_document(document)
    resolved = V.resolve_document(document)
    index = V.Index(resolved)
    templates = {}
    def collect(nodes):
        for n in nodes:
            templates[n['path']] = n
            collect(n['children'])
    collect(compiled['tree'])
    overrides = {} if counts is None else counts
    if not isinstance(overrides, dict) or any(type(v) is not int or v < 0 for v in overrides.values()):
        raise ValueError('Count overrides must map template paths or parent-instance/local-name paths to nonnegative integers.')
    program = InstanceProgram(source=copy.deepcopy(document))
    program.source_hash = hashlib.sha256(json.dumps(document, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
    program.definitions = {name: {v['id']: copy.deepcopy(v) for v in values}
                           for name, values in compiled['definitions'].items()}
    by_template = {}
    selected_counts = {}
    forced = {}
    used_overrides = set()

    def report(code, message, subject=None, severity='error'):
        d = dict(code=code, message=message, severity=severity, subject=subject)
        if d not in program.diagnostics:
            program.diagnostics.append(d)

    def count_for(path, parent):
        node = index.features.get(path) or index.nodes[path]
        bounds = node['count']
        local = path.split('#')[-1] if '#' in path else path.rsplit('/', 1)[-1]
        instance_key = (parent + ('#' if '#' in path else '/') + local) if parent else local
        override = next((k for k in (instance_key, path) if k in overrides), None)
        if override is not None:
            used_overrides.add(override)
            n = overrides[override]
        else:
            n = selected_counts.get((path, parent), bounds['preferred'])
            if path in templates and templates[path]['presence'] == 'unavailable':
                n = 0
            elif path in templates and templates[path]['presence'] == 'required_when_parent_present':
                n = max(1, n)
        n = max(n, forced.get((path, parent), 0))
        if not bounds['min'] <= n <= bounds['max']:
            raise ValueError(f'Count {n} for {instance_key} is outside {bounds["min"]}..{bounds["max"]}.')
        selected_counts[(path, parent)] = n
        return n

    choices_by_parent = {}
    for choice in compiled['choices']:
        parent = index.parents[choice['members'][0]]
        choices_by_parent.setdefault(parent, []).append(choice)

    def choose_children(parent_template, parent_instance):
        children = index.children.get(parent_template, [])
        values = {p: count_for(p, parent_instance) for p in children}
        choices = choices_by_parent.get(parent_template, [])
        if not choices:
            return values
        candidates = sorted({p for c in choices for p in c['members']})
        variable = [p for p in candidates if index.count(p)['min'] == 0
                    and not forced.get((p, parent_instance), 0)
                    and templates[p]['presence'] != 'required_when_parent_present']
        if len(variable) > 18:
            report('selection_search_exhausted', 'Too many overlapping optional families for bounded choice selection.', parent_instance)
            return values
        best = None
        for mask in range(1 << len(variable)):
            trial = dict(values)
            for bit, p in enumerate(variable):
                active = bool(mask & (1 << bit))
                if active and (index.count(p)['max'] == 0 or templates[p]['presence'] == 'unavailable'):
                    break
                local = p.rsplit('/', 1)[-1]
                key = f'{parent_instance}/{local}' if parent_instance else local
                if (key in overrides or p in overrides) and active != bool(values[p]):
                    break
                trial[p] = max(1, values[p]) if active else 0
            else:
                cost = sum(abs(trial[p] - values[p]) for p in candidates)
                valid = True
                for c in choices:
                    n = sum(trial[p] > 0 for p in c['members'])
                    rule = index.rule(c)
                    if rule['priority'] == 'mandatory' and not c['minSelected'] <= n <= c['maxSelected']:
                        valid = False
                        break
                    cost += 5 * abs(n - c.get('preferredSelected', c['minSelected']))
                if valid and (best is None or cost < best[0]):
                    best = (cost, trial)
        if best is None:
            report('selection_unresolved', 'No allowed family choice under current activation and count overrides.', parent_instance)
        else:
            values = best[1]
        selected_counts.update({(p, parent_instance): n for p, n in values.items()})
        return values

    def rebuild():
        # A different activated branch can repair an earlier provisional choice;
        # diagnostics describe the final selected state, not abandoned attempts.
        program.diagnostics[:] = [d for d in program.diagnostics
                                  if d['code'] not in ('selection_unresolved', 'selection_search_exhausted')]
        program.spaces.clear()
        program.features.clear()
        by_template.clear()
        def visit(parent_template, parent_instance, ancestors, owner):
            for path, amount in choose_children(parent_template, parent_instance).items():
                node = index.nodes[path]
                for i in range(amount):
                    instance = (parent_instance + '/' if parent_instance else '') + f'{node["id"]}[{i}]'
                    actual_owner = instance if node['kind'] == 'department' else owner
                    bindings = {**ancestors, path: instance}
                    n = dict(id=instance, template=path, parent=parent_instance, owner=actual_owner,
                             kind=node['kind'], children=[], node=copy.deepcopy(node), requirements=[],
                             bindings=bindings, index=i)
                    program.spaces[instance] = n
                    by_template.setdefault(path, []).append(instance)
                    if parent_instance:
                        program.spaces[parent_instance]['children'].append(instance)
                    for f in node.get('features', []):
                        fp = path + '#' + f['id']
                        for j in range(count_for(fp, instance)):
                            fid = instance + '#' + f'{f["id"]}[{j}]'
                            prototypes = f['prototypes']
                            # Per-instance stream prevents unrelated selection changing an existing feature.
                            h = hashlib.sha256(f'{seed}:{fid}:prototype'.encode()).digest()
                            prototype = prototypes[int.from_bytes(h[:8], 'big') % len(prototypes)] if prototypes else None
                            program.features[fid] = dict(id=fid, template=fp, room=instance,
                                owner=actual_owner, node=copy.deepcopy(f), prototype=prototype,
                                bindings=bindings, index=j)
                            by_template.setdefault(fp, []).append(fid)
                    visit(path, instance, bindings, actual_owner)
        visit(None, None, {}, None)

    def concrete(endpoint):
        return program.features.get(endpoint) or program.spaces.get(endpoint)

    def compatible(source, target, edge, common=None):
        if source.startswith('@domain/') or target.startswith('@domain/'):
            return edge['matching']['scope'] == 'station'
        a, b = concrete(source), concrete(target)
        if edge['matching']['scope'] == 'same_owner':
            return a['owner'] is not None and a['owner'] == b['owner']
        if edge['matching']['scope'] == 'same_parent':
            common = common or index.common(a['template'], b['template'])
            return common is not None and a['bindings'].get(common) == b['bindings'].get(common)
        # Declaration locality applies to the source; station-scoped targets may
        # legitimately live in another repetition or department.
        return True

    def candidates_for(source, binding, edge):
        result = []
        for candidate in binding['candidates']:
            if not candidate['available']:
                continue
            p = candidate['endpoint']
            possible = [p] if p.startswith('@domain/') else by_template.get(p, [])
            result.extend(t for t in possible if compatible(source, t, edge, candidate['sharedAncestorInstance']))
        return sorted(set(result))

    def activate(path, source, edge, increase=False):
        if path.startswith('@domain/'):
            return False
        bounds = index.count(path)
        if bounds['max'] == 0:
            return False
        a = concrete(source)
        anchor = None
        if edge['matching']['scope'] == 'same_parent':
            anchor = index.common(a['template'], path)
        elif edge['matching']['scope'] == 'same_owner':
            anchor = index.owners[path]
        parent = None
        chain = index.ancestors(path)
        if '#' in path:
            chain.append(path)
        for p in chain:
            if anchor and p in a['bindings'] and (p == anchor or anchor.startswith(p + '/')):
                parent = a['bindings'][p]
                continue
            b = index.count(p)
            current = selected_counts.get((p, parent), 0)
            wanted = max(1, current + (1 if increase and p == path else 0))
            if wanted > b['max']:
                return False
            local = p.split('#')[-1] if '#' in p else p.rsplit('/', 1)[-1]
            key = (parent + ('#' if '#' in p else '/') + local) if parent else local
            override = next((k for k in (key, p) if k in overrides), None)
            if override and overrides[override] < wanted:
                return False
            if current < wanted:
                forced[(p, parent)] = wanted
                selected_counts[(p, parent)] = wanted
                return True
            matches = [i for i in by_template.get(p, [])
                       if (program.features[i]['room'] if i in program.features else program.spaces[i]['parent']) == parent]
            if not matches:
                return False
            parent = matches[0]
        return False

    # Monotone activation is bounded by finite template capacity; branch conflicts
    # remain explicit rather than manufacturing a successful matching.
    for attempt in range(128):
        rebuild()
        changed = False
        for edge in compiled['relationships']:
            if edge['priority'] != 'mandatory' or edge['matching']['presence'] != 'require_target':
                continue
            for binding in edge['sourceBindings']:
                sources = [binding['source']] if binding['source'].startswith('@domain/') else by_template.get(binding['source'], [])
                for source in sources:
                    if candidates_for(source, binding, edge):
                        continue
                    ordered = [c for c in binding['candidates'] if c['available']]
                    for candidate in ordered:
                        if activate(candidate['endpoint'], source, edge):
                            changed = True
                            break
                    if changed:
                        break
                if changed:
                    break
            if changed:
                break
        if not changed:
            # Distinct links may require a larger optional target count even
            # though each source individually already has a candidate.
            for edge in compiled['relationships']:
                if edge['priority'] != 'mandatory' or edge['matching']['targets'] != 'distinct' or edge['matching']['presence'] != 'require_target':
                    continue
                graph = {}
                for binding in edge['sourceBindings']:
                    for source in by_template.get(binding['source'], []):
                        graph[source] = (candidates_for(source, binding, edge), binding)
                matched = {}
                def augment(source, seen):
                    for target in graph[source][0]:
                        if target in seen:
                            continue
                        seen.add(target)
                        if target not in matched or augment(matched[target], seen):
                            matched[target] = source
                            return True
                    return False
                for source in sorted(graph, key=lambda x: (len(graph[x][0]), x)):
                    if augment(source, set()):
                        continue
                    for candidate in graph[source][1]['candidates']:
                        if not candidate['available']:
                            continue
                        endpoint = candidate['endpoint']
                        expansion_paths = [endpoint]
                        # A provider can have count one inside a repeatable suite.
                        # Expand that suite, but never duplicate the shared scope
                        # merely to manufacture a foreign-parent target.
                        a = concrete(source)
                        boundary = (index.common(a['template'], endpoint)
                                    if edge['matching']['scope'] == 'same_parent'
                                    else index.owners.get(endpoint) if edge['matching']['scope'] == 'same_owner' else None)
                        for ancestor in reversed(index.ancestors(endpoint)):
                            if ancestor == boundary:
                                break
                            if ancestor not in expansion_paths:
                                expansion_paths.append(ancestor)
                        for expansion in expansion_paths:
                            if activate(expansion, source, edge, increase=True):
                                changed = True
                                break
                        if changed:
                            break
                    if changed:
                        break
                if changed:
                    break
        if not changed:
            break
    else:
        report('selection_search_exhausted', 'Activation work budget exhausted; selection is incomplete.')

    for key in overrides.keys() - used_overrides:
        report('unused_count_override', f'Count override {key!r} did not match an instantiated parent/template.', key)

    def mapped_refs(paths, context):
        a = concrete(context)
        result = []
        for p in paths:
            common = index.common(a['template'], p)
            values = by_template.get(p, [])
            values = [v for v in values if not common or a['bindings'].get(common) == concrete(v)['bindings'].get(common)]
            # Preserve an unresolved reference as a non-existent endpoint, so no
            # evaluator can silently erase a required waypoint or entry restriction.
            result.extend(values or [f'@unselected/{p}'])
        return result

    for instance, node in program.spaces.items():
        for raw in index.nodes[node['template']].get('requirements', []):
            rule = {**copy.deepcopy(raw), **index.rule(raw), 'authoredId': raw['id'],
                    'id': instance + '::' + raw['id'], 'owner': instance,
                    'declarationScope': node['template'], 'authoredParameters': copy.deepcopy(raw['parameters'])}
            if rule['type'] == 'access_policy':
                for key in ('entryFrom', 'mustTraverse'):
                    rule['parameters'][key] = mapped_refs(rule['parameters'][key], instance)
            node['requirements'].append(rule)

    for edge in compiled['relationships']:
        graph = {}
        binding_lookup = {}
        for binding in edge['sourceBindings']:
            sources = [binding['source']] if binding['source'].startswith('@domain/') else by_template.get(binding['source'], [])
            for source in sources:
                targets = candidates_for(source, binding, edge)
                if not targets and edge['matching']['presence'] == 'if_both_present':
                    continue
                graph[source] = targets
                binding_lookup[source] = binding
        assignments = {}
        if edge['matching']['targets'] == 'distinct':
            occupied = {}
            def augment(source, seen):
                for target in graph[source]:
                    if target in seen:
                        continue
                    seen.add(target)
                    if target not in occupied or augment(occupied[target], seen):
                        occupied[target] = source
                        return True
                return False
            for source in sorted(graph, key=lambda x: (len(graph[x]), x)):
                if not augment(source, set()):
                    report('instance_matching_unresolved', 'Selected instances lack a complete injective matching; another selection may work.', edge['id'])
            assignments = {s: [t] for t, s in occupied.items()}
        else:
            for source, targets in graph.items():
                assignments[source] = targets if edge['matching']['targets'] == 'all' else targets[:1]
        for source in graph:
            targets = assignments.get(source, [])
            if not targets:
                if edge['priority'] == 'mandatory':
                    report('required_target_missing', 'Selected source has no selected compatible target.', source + '::' + edge['id'])
                else:
                    report('desired_target_missing', 'Desired edge has no selected compatible target and is unscored.',
                           source + '::' + edge['id'], severity='warning')
                continue
            a = concrete(source)
            declaration = edge['declarationScope']
            owner = a['bindings'].get(declaration) if a and declaration else None
            for n, target in enumerate(targets):
                rule = {k: copy.deepcopy(v) for k, v in edge.items() if k != 'sourceBindings'}
                rule.update(id=f'{edge["id"]}@{source}->{target}', owner=owner,
                            source=source, target=target,
                            targetCandidates=list(graph[source]),
                            source_template=binding_lookup[source]['source'],
                            target_template=concrete(target)['template'] if concrete(target) else target,
                            authoredParameters=copy.deepcopy(edge['parameters']))
                if rule['type'] == 'route':
                    for key in ('through', 'avoid'):
                        rule['parameters'][key] = mapped_refs(rule['parameters'][key], source)
                program.relationships.append(rule)
    return program
