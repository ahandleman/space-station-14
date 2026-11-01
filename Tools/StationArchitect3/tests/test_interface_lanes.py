"""Distinct local connections need distinct, correctly oriented geometry."""
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from architect.geometry import rect
from architect.department_packing import pack_department
from architect.packing import _plan_seams, _seams, interface_minimum, rebuild_walls
from architect.state import InstanceProgram, LayoutState
from architect.verify import Verifier


def pair(height=4):
    program = InstanceProgram()
    program.definitions = {
        'interfaces': {'door': {'capabilities': ['crew_passage'], 'access': {'anyOf': []}}},
        'accessProfiles': {'public': {'levels': ['public']}},
        'environments': {'inside': {'pressure': 'standard', 'enclosure': 'sealed'}},
        'domains': {},
    }
    for key in ('first', 'second'):
        program.spaces[key] = {
            'id': key, 'template': key, 'kind': 'room', 'parent': None,
            'owner': key, 'children': [], 'requirements': [],
            'node': {'tags': [], 'environment': 'inside', 'access': 'public',
                     'placement': {'grid': 'station', 'nesting': 'owned', 'zone': 'interior'}},
        }
    return LayoutState(program, floors={'first': rect(0, 0, 4, height),
                                       'second': rect(5, 0, 4, height)})


def demands():
    return [{'a': 'first', 'b': 'second', 'profile': 'door', 'length': 2, 'rule': rule}
            for rule in ('entry', 'exit')]


class InterfaceLaneTests(unittest.TestCase):
    def test_two_connections_share_a_border_without_reusing_or_fragmenting_it(self):
        state = pair()
        self.assertTrue(_seams(state, demands()))
        self.assertEqual(2, len(state.interfaces))
        self.assertEqual({'entry', 'exit'}, {i['rule'] for i in state.interfaces})
        first, second = (i['cells'] for i in state.interfaces)
        self.assertFalse(first & second)
        self.assertEqual(4, len(first | second))
        rebuild_walls(state)
        verifier = Verifier(state)
        self.assertTrue(all(verifier.interface_valid(i) for i in state.interfaces))

    def test_individually_large_enough_border_cannot_satisfy_two_connections(self):
        state = pair(3)
        self.assertIsNone(_plan_seams(state.program, state.floors, demands()))
        self.assertFalse(_seams(state, demands()))
        self.assertFalse(state.interfaces, 'Failed batch allocation must be atomic.')

    def test_existing_interface_tiles_remain_reserved(self):
        state = pair(5)
        reserved = {(4, 2)}
        plan = _plan_seams(state.program, state.floors, demands(), reserved)
        self.assertIsNotNone(plan)
        self.assertTrue(all(not i['cells'] & reserved for i in plan))

    def directed_pair(self, both=True):
        state = pair(2)
        state.program.definitions['accessProfiles'].update({
            'incoming': {'levels': ['Enter']}, 'released': {'levels': ['Leave']},
            'staff': {'levels': ['Staff']},
        })
        for name, direction, credential in (('entry', 'a_to_b', 'Enter'), ('exit', 'b_to_a', 'Leave')):
            state.program.definitions['interfaces'][name] = {
                'capabilities': ['crew_passage'], 'access': {'anyOf': [[credential], ['Staff']]},
                'passage': {'direction': direction, 'minWidth': 1, 'maxWidth': 1},
            }
        edges = [{'a': 'first', 'b': 'second', 'profile': name, 'length': 1, 'rule': name}
                 for name in (('entry', 'exit') if both else ('entry',))]
        self.assertTrue(_seams(state, edges))
        rebuild_walls(state)
        return state

    def test_explicit_profile_width_replaces_default_two_tile_door(self):
        state = self.directed_pair()
        self.assertEqual(1, interface_minimum(state.program, 'entry'))
        self.assertEqual(2, interface_minimum(state.program, 'door'))
        self.assertTrue(all(len(i['cells']) == 1 for i in state.interfaces))
        state.interfaces[0]['cells'] |= state.interfaces[1]['cells']
        self.assertFalse(Verifier(state).interface_valid(state.interfaces[0]))

    def test_credentials_and_direction_are_both_required(self):
        state = self.directed_pair()
        verifier = Verifier(state)
        def route(actor, a, b, width=1):
            return verifier.route(a, b, {'mode': 'crew', 'accessProfile': actor, 'minWidth': width})
        self.assertIsNotNone(route('incoming', 'first', 'second'))
        self.assertIsNone(route('incoming', 'second', 'first'))
        self.assertIsNotNone(route('released', 'second', 'first'))
        self.assertIsNone(route('released', 'first', 'second'))
        self.assertIsNone(route('public', 'first', 'second'))
        self.assertIsNotNone(route('staff', 'first', 'second'))
        self.assertIsNotNone(route('staff', 'second', 'first'))
        self.assertIsNone(route('staff', 'first', 'second', 2),
                          'Opposed one-wide lanes are not a two-wide crossing.')
        self.assertIsNone(route('staff', 'second', 'first', 2))

    def test_privileged_access_does_not_reverse_an_individual_gate(self):
        verifier = Verifier(self.directed_pair(both=False))
        rule = {'mode': 'crew', 'accessProfile': 'staff', 'minWidth': 1}
        self.assertIsNotNone(verifier.route('first', 'second', rule))
        self.assertIsNone(verifier.route('second', 'first', rule))
        # Waypoint routing uses the same directed footprint transitions.
        self.assertIsNone(verifier.route('second', 'first', {**rule, 'through': ['first']}))

    def test_gate_cannot_be_entered_or_left_sideways(self):
        state = self.directed_pair(both=False)
        interface = state.interfaces[0]
        x, y = next(iter(interface['cells']))
        nx, ny = interface['normal']
        verifier = Verifier(state)
        self.assertTrue(verifier.step_allowed((x-nx,y-ny),(x,y)))
        for tangent in ((-ny,nx),(ny,-nx)):
            side=(x+tangent[0],y+tangent[1])
            self.assertFalse(verifier.step_allowed(side,(x,y)))
            self.assertFalse(verifier.step_allowed((x,y),side))

    def test_directed_requirement_cannot_use_reversed_endpoints(self):
        state = self.directed_pair()
        verifier = Verifier(state)
        self.assertEqual(1, len(verifier.matching_interfaces('first', 'second', 'entry')))
        self.assertFalse(verifier.matching_interfaces('second', 'first', 'entry'))

    def test_duplicate_profile_contracts_cannot_borrow_each_others_length(self):
        state = pair()
        self.assertTrue(_seams(state, demands()))
        rebuild_walls(state)
        verifier = Verifier(state)
        self.assertEqual(2, len(verifier.matching_interfaces('first', 'second', 'door')))
        self.assertEqual(1, len(verifier.matching_interfaces('first', 'second', 'door', 'entry')))
        self.assertEqual(1, len(verifier.matching_interfaces('first', 'second', 'door', 'exit')))

    def test_department_embedding_reserves_both_authored_directional_lanes(self):
        program = self.directed_pair().program
        program.spaces['owner'] = {
            'id': 'owner', 'template': 'owner', 'kind': 'department', 'parent': None,
            'owner': 'owner', 'children': ['first', 'second'], 'requirements': [],
            'node': {'tags': [], 'placement': {}},
        }
        for room in ('first', 'second'):
            program.spaces[room].update(parent='owner', owner='owner')
            program.spaces[room]['requirements'] = [{
                'id': room+'::area', 'type': 'metric', 'priority': 'mandatory',
                'parameters': {'metric': 'area.exclusive', 'min': 16, 'preferred': 16, 'max': 16},
            }]
        program.relationships = [{
            'id': name, 'source': 'first', 'target': 'second', 'owner': 'owner',
            'type': 'shared_boundary', 'priority': 'mandatory',
            'parameters': {'interface': name, 'minLength': 1, 'maxLength': 1,
                           'minSegments': 1, 'maxSegments': 1},
        } for name in ('entry', 'exit')]
        state = pack_department(program, 'owner', 32)
        self.assertEqual('completed', state.metadata['termination'], state.trace)
        self.assertEqual(32, len(state.aggregate('owner')))
        self.assertEqual({'first', 'second'}, set(state.floors))
        self.assertEqual(2, len(state.interfaces))
        self.assertTrue(all(len(i['cells']) == 1 for i in state.interfaces))
        verifier = Verifier(state)
        for rule in program.relationships:
            self.assertEqual('satisfied', verifier.relationship(rule)['status'])

    def test_shared_suite_keeps_named_gateway_and_other_actors_staff_access(self):
        program = pair().program
        program.definitions['accessProfiles'].update({
            'engineering': {'levels': ['Engineering']},
            'atmospherics': {'levels': ['Atmos']},
        })
        for profile, level in (('engineering_door', 'Engineering'), ('atmospherics_door', 'Atmos')):
            program.definitions['interfaces'][profile] = {
                'capabilities': ['crew_passage'], 'access': {'anyOf': [[level]]},
            }
        program.spaces['owner'] = {
            'id': 'owner', 'template': 'owner', 'kind': 'department', 'parent': None,
            'owner': 'owner', 'children': ['first', 'second', 'hall'], 'requirements': [],
            'node': {'tags': [], 'placement': {}},
        }
        import copy
        program.spaces['hall'] = copy.deepcopy(program.spaces['first'])
        program.spaces['hall'].update(id='hall', template='hall', kind='passage')
        program.spaces['hall']['node']['tags'] = ['department_circulation']
        for room, actor in (('first','engineering'), ('second','atmospherics'), ('hall','engineering')):
            program.spaces[room].update(parent='owner', owner='owner')
            program.spaces[room]['node']['access'] = actor
            area = 32 if room == 'hall' else 16
            program.spaces[room]['requirements'] = [{
                'id': room+'::area', 'type': 'metric', 'priority': 'mandatory',
                'parameters': {'metric': 'area.exclusive', 'min': area, 'preferred': area, 'max': area},
            }]
        program.relationships = [{
            'id': name, 'source': source, 'target': target, 'owner': 'owner',
            'type': 'shared_boundary', 'priority': 'mandatory',
            'parameters': {'interface': 'atmospherics_door', 'minLength': 2},
        } for name,source,target in (('suite','first','second'), ('named_gateway','second','hall'))]
        program.relationships.append({
            'id': 'other_actor_route', 'source': 'first', 'target': 'hall', 'owner': 'owner',
            'type': 'route', 'priority': 'mandatory',
            'parameters': {'mode': 'crew', 'accessProfile': 'engineering', 'minWidth': 2},
        })
        state = pack_department(program, 'owner', 64)
        self.assertEqual('completed', state.metadata['termination'], state.trace)
        verifier = Verifier(state)
        for rule in program.relationships:
            self.assertEqual('satisfied', verifier.relationship(rule)['status'], rule['id'])
        self.assertTrue(any(i['a']=='first' and i['b']=='hall' and i['profile']=='engineering_door'
                            for i in state.interfaces))


if __name__ == '__main__':
    unittest.main()
