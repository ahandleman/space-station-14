"""Mutation tests for the independent geometric verifier."""
import sys
from pathlib import Path
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from architect.geometry import bbox, docking_cells, outline, rect, square_anchors
from architect.state import InstanceProgram, LayoutState
from architect.verify import capability_report, verify_layout
from benchmark import partition_signature


def program(nodes):
    p = InstanceProgram()
    p.definitions = {
        'accessProfiles': {'public': {'levels': ['public']}, 'staff': {'levels': ['public', 'Staff']}},
        'interfaces': {
            'door': {'capabilities': ['crew_passage'], 'access': {'anyOf': []}},
            'staff_door': {'capabilities': ['crew_passage'], 'access': {'anyOf': [['Staff']]}},
            'counter': {'capabilities': ['item_transfer'], 'access': {'anyOf': []}},
            'dock': {'capabilities': ['docking'], 'access': {'anyOf': []}},
        },
        'environments': {'habitable': {'pressure': 'standard', 'enclosure': 'sealed'}},
        'domains': {'exterior': {'kind': 'exterior_space'}},
    }
    for key, kind, tags in nodes:
        p.spaces[key] = {'id': key, 'template': key, 'kind': kind, 'parent': None,
                         'owner': key, 'children': [], 'requirements': [],
                         'node': {'tags': tags, 'environment': 'habitable', 'access': 'public',
                                  'placement': {'grid': 'station', 'nesting': 'owned', 'zone': 'interior'}}}
    return p


def rule(owner, id, type, parameters, **extra):
    return {'id': owner + '::' + id, 'owner': owner, 'type': type, 'parameters': parameters,
            'priority': 'mandatory', 'weight': 100, 'tier': 0, **extra}


def enclose(state):
    floor = set().union(*state.floors.values())
    opening = set().union(*(set(i['cells']) for i in state.interfaces)) if state.interfaces else set()
    state.walls = outline(floor) - opening
    state.bounds = bbox(floor | state.walls | opening)
    return state


def check(report, id):
    return next(c for c in report['checks'] if c['id'] == id)


class GeometryTests(unittest.TestCase):
    def test_half_open_bbox_and_square_width(self):
        cells = rect(-2, 3, 5, 2)
        self.assertEqual(bbox(cells), (-2, 3, 3, 5))
        self.assertEqual(len(square_anchors(cells, 2)), 4)
        self.assertFalse(square_anchors(cells, 3))

    def test_partition_signature_ignores_labels_and_rigid_transforms(self):
        state = LayoutState(InstanceProgram(), floors={'a': rect(0, 0, 3, 4), 'b': rect(5, 0, 2, 3)})
        signature = partition_signature(state)
        transformed = {key: {(-y + 17, x - 12) for x, y in cells} for key, cells in state.floors.items()}
        state.floors = {'a': transformed['b'], 'b': transformed['a']}
        self.assertEqual(partition_signature(state), signature)
        state.floors['a'].add((30, 30))
        self.assertNotEqual(partition_signature(state), signature)


class VerifierTests(unittest.TestCase):
    def pair(self, interface='counter'):
        p = program([('a', 'room', []), ('b', 'room', [])])
        p.relationships = [rule('a', 'seam', 'shared_boundary',
                                {'interface': interface, 'minLength': 2, 'continuous': True,
                                 'minSegments': 1, 'maxSegments': 1}, source='a', target='b')]
        s = LayoutState(p, floors={'a': rect(0, 0, 4, 4), 'b': rect(5, 0, 4, 4)},
                        interfaces=[{'id': 'ab', 'a': 'a', 'b': 'b', 'profile': interface,
                                     'cells': {(4, 1), (4, 2)}, 'normal': (1, 0)}])
        return enclose(s)

    def test_actual_counter_seam_validates(self):
        self.assertTrue(verify_layout(self.pair())['verified'])

    def test_corner_or_fragmented_contact_fails(self):
        state = self.pair()
        state.interfaces[0]['cells'] = {(4, 0), (4, 3)}
        enclose(state)
        self.assertEqual(check(verify_layout(state), 'a::seam')['status'], 'violated')

    def test_counter_cannot_supply_crew_route(self):
        state = self.pair()
        state.program.relationships.append(rule('a', 'route', 'route', {'accessProfile': 'public', 'mode': 'crew', 'minWidth': 2}, source='a', target='b'))
        self.assertEqual(check(verify_layout(state), 'a::route')['status'], 'violated')
        state.interfaces[0]['profile'] = 'door'
        self.assertEqual(check(verify_layout(state), 'a::route')['status'], 'satisfied')

    def test_actor_credentials_and_width_are_physical(self):
        state = self.pair('staff_door')
        state.program.relationships = [rule('a', 'route', 'route', {'accessProfile': 'public', 'mode': 'crew', 'minWidth': 2}, source='a', target='b')]
        self.assertEqual(check(verify_layout(state), 'a::route')['status'], 'violated')
        state.program.relationships[0]['parameters']['accessProfile'] = 'staff'
        self.assertEqual(check(verify_layout(state), 'a::route')['status'], 'satisfied')
        state.interfaces[0]['cells'] = {(4, 1)}
        enclose(state)
        self.assertEqual(check(verify_layout(state), 'a::route')['status'], 'violated')

    def test_overlap_and_undeclared_floor_transition_fail(self):
        state = self.pair()
        state.floors['b'].add((0, 0))
        self.assertEqual(check(verify_layout(state), 'floor:b')['status'], 'violated')
        state = self.pair()
        state.floors['b'].add((4, 3))
        self.assertEqual(check(verify_layout(state), 'boundary:a')['status'], 'violated')

    def test_feature_collision_and_operation_space_are_checked(self):
        state = self.pair()
        for key in ('first', 'second'):
            state.program.features[key] = {'room': 'a', 'node': {'prototypes': []}}
            state.features[key] = {'room': 'a', 'cells': {(1, 1)}, 'clearance': {(1, 2)}}
        self.assertEqual(check(verify_layout(state), 'feature_pose:second')['status'], 'violated')
        state.features['second']['cells'] = {(1, 2)}
        self.assertEqual(check(verify_layout(state), 'feature_pose:first')['status'], 'violated')

    def test_separation_uses_floor_boundaries_not_centers(self):
        state = self.pair()
        state.program.relationships = [rule('a', 'separation', 'separation',
            {'metric': 'boundary_manhattan', 'minDistance': 1, 'maxDistance': 1}, source='a', target='b')]
        self.assertEqual(check(verify_layout(state), 'a::separation')['measured'], 1)

    def test_width_witness_cannot_hide_choke_or_chamber(self):
        p = program([('maints', 'passage', ['maintenance_circulation'])])
        p.spaces['maints']['requirements'] = [rule('maints', 'width', 'metric', {'metric': 'corridor.clear_width', 'min': 2, 'max': 3, 'scale': 1})]
        cells = rect(0, 0, 10, 2)
        state = enclose(LayoutState(p, floors={'maints': cells}, strips={'maints': [set(cells)]}))
        self.assertTrue(verify_layout(state)['verified'])
        state.floors['maints'].remove((5, 1))
        enclose(state)
        self.assertEqual(check(verify_layout(state), 'maints::width')['status'], 'violated')
        state.floors['maints'] = rect(0, 0, 10, 4)
        state.strips['maints'] = [rect(0, 0, 10, 2), rect(0, 2, 10, 2)]
        enclose(state)
        self.assertEqual(check(verify_layout(state), 'maints::width')['status'], 'violated')

    def deep_room(self):
        p = program([('hall', 'passage', ['public_circulation']), ('maints', 'passage', ['maintenance_circulation']), ('deep', 'room', [])])
        p.spaces['deep']['requirements'] = [rule('deep', 'entry', 'access_policy', {
            'entryFrom': ['maints'], 'allowPublicEntry': False, 'allowMaintenanceEntry': True,
            'allowExteriorEntry': False, 'throughTraffic': False, 'mustTraverse': ['maints']})]
        state = LayoutState(p, floors={'hall': rect(0, 0, 9, 3), 'maints': rect(0, 4, 5, 3), 'deep': rect(6, 4, 3, 3)},
                            interfaces=[{'id': 'hall_maint', 'a': 'hall', 'b': 'maints', 'profile': 'staff_door', 'normal': (0, 1), 'cells': {(1, 3), (2, 3)}},
                                        {'id': 'maint_deep', 'a': 'maints', 'b': 'deep', 'profile': 'staff_door', 'normal': (1, 0), 'cells': {(5, 4), (5, 5)}}])
        return enclose(state)

    def test_new_bypass_invalidates_existing_good_route(self):
        state = self.deep_room()
        self.assertEqual(check(verify_layout(state), 'deep::entry')['status'], 'satisfied')
        state.interfaces.append({'id': 'bypass', 'a': 'hall', 'b': 'deep', 'profile': 'staff_door', 'normal': (0, 1), 'cells': {(6, 3), (7, 3)}})
        enclose(state)
        result = check(verify_layout(state), 'deep::entry')
        self.assertEqual(result['status'], 'violated')
        self.assertIn('bypass', result['message'])

    def test_aggregate_counts_floor_once_and_containment_is_real(self):
        p = program([('host', 'room', []), ('child', 'room', [])])
        p.spaces['child']['parent'] = 'host'
        p.spaces['child']['node']['placement']['nesting'] = 'contained'
        p.spaces['host']['children'] = ['child']
        child = rect(3, 3, 3, 3)
        host = rect(0, 0, 9, 9) - child - outline(child)
        state = enclose(LayoutState(p, floors={'host': host, 'child': child}, envelopes={'host': rect(0, 0, 9, 9)}))
        p.spaces['host']['requirements'] = [rule('host', 'area', 'metric', {'metric': 'area.aggregate', 'min': len(host) + len(child), 'max': len(host) + len(child), 'scale': 1})]
        self.assertTrue(verify_layout(state)['verified'])
        state.floors['child'].add((20, 20))
        self.assertEqual(check(verify_layout(state), 'containment:child')['status'], 'violated')

    def dock(self):
        p = program([('room', 'room', [])])
        parameters = {'interface': 'dock', 'berthWidth': 4, 'berthDepth': 4, 'portOffset': 0,
                      'clearance': 1, 'approachWidth': 4, 'approachLength': 6, 'approachToExterior': True, 'source': 'fixture'}
        p.spaces['room']['requirements'] = [rule('room', 'dock', 'docking', parameters)]
        interface = {'id': 'dock', 'a': 'room', 'b': '@domain/exterior', 'profile': 'dock', 'normal': (0, 1), 'cells': {(2, 6), (3, 6)}}
        reserved = docking_cells(interface, parameters)
        return enclose(LayoutState(p, floors={'room': rect(0, 0, 6, 6)}, interfaces=[interface],
                                   reservations=[{'id': 'berth', 'owner': 'room', 'kind': 'docking', 'cells': reserved}]))

    def test_dock_geometry_recomputed_and_blocking_growth_rejected(self):
        state = self.dock()
        self.assertTrue(verify_layout(state)['verified'])
        state.floors['room'].add(min(state.reservations[0]['cells']))
        result = verify_layout(state)
        self.assertEqual(check(result, 'room::dock')['status'], 'violated')
        self.assertEqual(check(result, 'reservation:berth')['status'], 'violated')

    def test_courtyard_is_not_exterior(self):
        state = self.dock()
        region = state.reservations[0]['cells']
        x0, y0, x1, y1 = bbox(region)
        state.walls.update(outline(rect(x0 - 1, y0 - 1, x1 - x0 + 2, y1 - y0 + 2)))
        self.assertEqual(check(verify_layout(state), 'room::dock')['status'], 'violated')

    def test_narrow_escape_does_not_supply_shuttle_approach(self):
        state = self.dock()
        # Leave every reserved cell free and a flood-fill path around this wall,
        # but block the straight padded lane beyond the short reserved approach.
        _, _, _, y1 = bbox(state.reservations[0]['cells'])
        state.walls.update(rect(0, y1 + 1, 6, 1))
        self.assertEqual(check(verify_layout(state), 'room::dock')['status'], 'violated')

    def test_unknown_mandatory_semantics_never_pass(self):
        state = self.pair()
        state.program.spaces['a']['requirements'] = [rule('a', 'native', 'x_unimplemented', {})]
        self.assertEqual(len(capability_report(state.program)), 1)
        result = verify_layout(state)
        self.assertFalse(result['verified'])
        self.assertEqual(result['unknown_mandatory'][0]['id'], 'a::native')
        state.program.spaces['a']['requirements'][0]['priority'] = 'desired'
        result = verify_layout(state)
        self.assertTrue(result['verified'])
        self.assertNotIn('cost', check(result, 'a::native'))


if __name__ == '__main__':
    unittest.main()
