"""Exterior contracts keep real free space and respect declared open surfaces."""
from pathlib import Path
import sys
import unittest
import copy

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from architect.state import InstanceProgram, LayoutState
from architect.geometry import rect
from architect.packing import rebuild_walls
from architect.departments import _landing, attach_boundary_harness
from architect.verify import Verifier


def scene():
    p = InstanceProgram(definitions={
        'environments': {'inside': {'enclosure': 'sealed'}, 'space': {'enclosure': 'open'}},
        'interfaces': {'door': {'capabilities': ['crew_passage']}}})
    for key in ('room', 'obstacle', 'hall', 'solar'):
        p.spaces[key] = {'id': key, 'kind': 'room', 'parent': None, 'owner': None,
                         'children': [], 'requirements': [],
                         'node': {'environment': 'space' if key == 'solar' else 'inside'}}
    s = LayoutState(p, floors={'room': rect(0, 0, 4, 4)})
    return s


class BoundaryTests(unittest.TestCase):
    def test_open_platform_has_no_invented_outer_wall(self):
        s = scene()
        s.floors['solar'] = rect(5, 0, 6, 6)
        rebuild_walls(s)
        self.assertIn((4, 2), s.walls)  # sealed station edge remains
        self.assertNotIn((11, 2), s.walls)
        self.assertNotIn((8, 6), s.walls)
        self.assertFalse(s.walls & s.floors['solar'])
        original = s.walls.copy()
        from architect.repair import rebuild_walls as repair_rebuild
        repair_rebuild(s)
        self.assertEqual(original, s.walls)

    def test_public_approach_can_bend_around_a_wing(self):
        s = scene()
        s.floors['obstacle'] = (rect(-4, -3, 3, 7) | rect(5, -3, 3, 7)
                                | rect(0, -4, 4, 3) | rect(-2, 10, 8, 2))
        rebuild_walls(s)
        port = _landing(s, 'room', 'hall', 'door', 2, s.program)
        self.assertIsNotNone(port)
        self.assertEqual((0, 1), port['normal'])
        self.assertFalse(s.floors['hall'] & s.floors['obstacle'])

    def test_enclosed_pocket_is_not_an_external_connection(self):
        s = scene()
        outer = rect(-4, -4, 12, 12) - rect(-3, -3, 10, 10)
        s.floors['obstacle'] = outer
        rebuild_walls(s)
        self.assertIsNone(_landing(s, 'room', 'hall', 'door', 2, s.program))
        self.assertNotIn('hall', s.floors)

    def test_shared_door_reserves_the_same_inside_width_as_its_crew_route(self):
        s = scene()
        p = s.program
        p.definitions['accessProfiles'] = {'staff': {'levels': ['Staff']}}
        p.definitions['interfaces']['staff_door'] = {'capabilities': ['crew_passage'],
            'access': {'anyOf': [['Staff']]}, 'airtight': True, 'construction': ['Airlock'],
            'normalState': 'controlled'}
        p.spaces['d'] = {'id': 'd', 'kind': 'department', 'children': ['room'],
                         'parent': None, 'owner': 'd', 'node': {}, 'requirements': []}
        p.spaces['room'].update(parent='d', owner='d')
        p.spaces['room']['node']['access'] = 'staff'
        # A two-tile doorway fits at y=0, but its lower tile has no inside
        # two-wide approach. The genuine two-wide port starts one row higher.
        s.floors['room'] = rect(0, 1, 4, 2) | {(0, 0)}
        full = copy.deepcopy(p)
        p.spaces.pop('hall')
        edges = [{'id': 'border', 'source': 'room', 'target': 'hall', 'type': 'shared_boundary',
                  'parameters': {'interface': 'staff_door', 'minLength': 1, 'maxSegments': 1}},
                 {'id': 'walk', 'source': 'room', 'target': 'hall', 'type': 'route',
                  'parameters': {'mode': 'crew', 'accessProfile': 'staff', 'minWidth': 2,
                                 'within': 'station', 'through': [], 'avoid': []}}]
        p.relationships = edges
        rebuild_walls(s)
        attach_boundary_harness(s, 'd', full, edges)
        self.assertIsNotNone(Verifier(s).route('room', 'hall', edges[1]['parameters']))
        ports = [i for i in s.interfaces if i['a'] == 'room' and i['b'] == 'hall']
        self.assertEqual(1, len(ports), 'One real doorway must satisfy both local obligations.')
        self.assertEqual({(-1, 1), (-1, 2)}, ports[0]['cells'])


if __name__ == '__main__':
    unittest.main()
