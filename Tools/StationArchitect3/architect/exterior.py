"""Realize declared hull frontages and protect full shuttle approach geometry."""
from .geometry import DIRECTIONS, bbox, components, docking_cells, neighbors
from .packing import rebuild_walls


def _runs(state, room, length):
    floor = state.floors.get(room, set())
    occupied = set().union(*state.floors.values()) if state.floors else set()
    openings = {c for i in state.interfaces for c in i['cells']}
    forbidden = occupied | openings
    for nx, ny in DIRECTIONS:
        lines = {}
        for x, y in floor:
            seam, outer = (x+nx, y+ny), (x+2*nx, y+2*ny)
            if seam in forbidden or outer in forbidden or outer in state.walls:
                continue
            lines.setdefault(seam[0]*nx+seam[1]*ny, set()).add(seam)
        for line in lines.values():
            for run in components(line):
                ordered = sorted(run)
                for start in range(len(ordered)-length+1):
                    yield {'a': room, 'b': '@domain/exterior_space',
                           'cells': set(ordered[start:start+length]), 'normal': (nx, ny)}


def realize_exterior(state, department):
    rooms = state.program.descendants(department, True)
    # Packer proposals may have chosen an obstructed hull side. Rebuild from the
    # actual geometry; this pass never edits any room or waives its requirement.
    exterior_ids = {i['id'] for i in state.interfaces if i['a'] in rooms and str(i['b']).startswith('@domain/')}
    state.interfaces = [i for i in state.interfaces if i['id'] not in exterior_ids]
    state.reservations = [r for r in state.reservations if not (r['owner'] in rooms and r['kind'] in ('docking', 'exterior'))]
    rebuild_walls(state)
    all_floor = state.aggregate(department)
    if not all_floor:
        return
    ax, ay, bx, by = bbox(all_floor | state.walls)
    extent = max(bx-ax, by-ay) + 8
    for room in rooms:
        requirements = state.program.spaces[room]['requirements']
        by_profile = {}
        for rule in requirements:
            if rule['type'] in ('perimeter', 'docking'):
                by_profile.setdefault(rule['parameters']['interface'], []).append(rule)
        for profile, rules in by_profile.items():
            docking = next((r for r in rules if r['type'] == 'docking'), None)
            perimeter = [r for r in rules if r['type'] == 'perimeter']
            length = max([1] + [r['parameters']['minLength'] for r in perimeter])
            continuous = any(r['parameters'].get('continuous') for r in perimeter)
            candidates = list(_runs(state, room, length if continuous or docking else 1))
            candidates.sort(key=lambda i: (-sum(x*i['normal'][0]+y*i['normal'][1] for x,y in i['cells'])/len(i['cells']), sorted(i['cells'])))
            accepted = 0
            for candidate in candidates:
                if accepted >= length:
                    break
                candidate['id'] = f'hull:{room}:{profile}:{accepted}'
                candidate['profile'] = profile
                candidate['b'] = '@domain/' + next((r['parameters']['domain'] for r in perimeter), 'exterior_space')
                nx, ny = candidate['normal']
                occupied = set().union(*state.floors.values(), state.walls)
                occupied -= candidate['cells']
                occupied |= {c for i in state.interfaces for c in i['cells']}
                reserved = {c for r in state.reservations for c in r['cells']}
                if candidate['cells'] & reserved:
                    continue
                if docking:
                    clearance = docking_cells(candidate, docking['parameters'])
                    # Continue the full-width outward lane beyond its nominal
                    # finite reservation to the outside of every built obstacle.
                    ray = {(x+step*nx,y+step*ny) for x,y in clearance for step in range(extent)}
                    if ray & occupied or clearance & reserved:
                        continue
                else:
                    clearance = set()
                    ray = {(x+step*nx,y+step*ny) for x,y in candidate['cells'] for step in range(1,extent)}
                    if ray & occupied:
                        continue
                if any(candidate['cells'] & i['cells'] for i in state.interfaces):
                    continue
                state.interfaces.append(candidate)
                if docking:
                    state.reservations.append({'id': 'dock:'+room+':'+docking['id'],
                                               'owner': room, 'kind': 'docking', 'cells': clearance,
                                               'rule': docking['id']})
                else:
                    # Subsequent boundary ports may not build a hallway over a
                    # window's vacuum frontage or an EVA exit's outside approach.
                    approach = {(x+step*nx, y+step*ny) for x, y in candidate['cells']
                                for step in range(1, 4)}
                    state.reservations.append({'id': 'outside:'+candidate['id'],
                                               'owner': room, 'kind': 'exterior', 'cells': approach})
                accepted += len(candidate['cells'])
                rebuild_walls(state)
            state.trace.append({'event': 'hull_frontage', 'room': room, 'profile': profile,
                                'requiredLength': length, 'realizedLength': accepted})
