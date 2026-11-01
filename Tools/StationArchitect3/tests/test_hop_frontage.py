"""Public service frontages are narrow open floor, not generic waiting rooms."""
import copy
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from validate import load_program, validate_document
from architect.geometry import bbox, rect
from architect.instances import resolve_instances
from architect.run import run_generation
from architect.verify import verify_layout


def program(name='station-layout.yaml'):
    return load_program(ROOT / 'examples' / name)


def find_space(document, path):
    nodes = document['spaces']
    for part in path.split('/'):
        node = next(n for n in nodes if n['id'] == part)
        nodes = node['spaces']
    return node


class FrontageTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.state, cls.report = run_generation(program(), {'root_restarts': 1}, seed=11)

    def test_normal_station_has_one_hospitality_and_repetition_is_explicit(self):
        normal = resolve_instances(program())
        repeated = resolve_instances(program('hierarchy-station.yaml'))
        for suffix in ('/bar', '/bar_commons', '/kitchen'):
            self.assertEqual(1, sum(n['template'].endswith(suffix) for n in normal.spaces.values()))
            self.assertEqual(2, sum(n['template'].endswith(suffix) for n in repeated.spaces.values()))
        catalog = load_program(ROOT / 'catalog/standard-station.yaml')
        self.assertEqual({'min': 1, 'preferred': 1, 'max': 1}, find_space(catalog, 'service/hospitality')['count'])

    def test_queue_is_one_deep_and_directly_abuts_hall(self):
        self.assertTrue(self.report['verification']['verified'])
        key = 'command[0]/personnel[0]/hop_line[0]'
        floor = self.state.floors[key]
        x0, y0, x1, y1 = bbox(floor)
        self.assertEqual(1, min(x1-x0, y1-y0))
        self.assertGreaterEqual(len(floor), 3)
        interface = next(i for i in self.state.interfaces if i['a'] == key and i['profile'] == 'public_open_edge')
        self.assertEqual(floor, interface['cells'])
        dx, dy = interface['normal']
        hall = self.state.floors[interface['b']]
        self.assertTrue(all((x+dx, y+dy) in hall for x, y in floor))
        self.assertFalse(floor & self.state.walls)
        checks = self.report['verification']['checks']
        self.assertTrue(any('hop_counter' in c['id'] and c['status'] == 'satisfied' for c in checks))
        self.assertTrue(any('staff_access' in c['id'] and 'hop_office' in c['id'] and c['status'] == 'satisfied' for c in checks))

    def test_square_queue_cannot_pass_depth_requirement(self):
        state = self.state.clone()
        key = 'command[0]/personnel[0]/hop_line[0]'
        x, y, _, _ = bbox(state.floors[key])
        state.floors[key] = rect(x, y, 2, 2)
        check = next(c for c in verify_layout(state)['checks'] if c['id'] == key+'::queue_depth')
        self.assertEqual('violated', check['status'])
        self.assertEqual(2, check['measured'])

    def test_undeclared_open_floor_contact_is_rejected(self):
        state = self.state.clone()
        state.interfaces = [i for i in state.interfaces if i['profile'] != 'public_open_edge']
        report = verify_layout(state)
        self.assertFalse(report['verified'])
        self.assertTrue(any(c['id'].startswith('boundary:') for c in report['hard_failures']))

    def test_open_edge_cannot_claim_a_lock_or_structural_door(self):
        for edit in ({'normalState': 'controlled'}, {'construction': ['Airlock']},
                     {'airtight': True}, {'access': {'anyOf': [['Staff']]}}):
            with self.subTest(edit=edit):
                doc = program()
                profile = next(i for i in doc['interfaces'] if i['id'] == 'public_open_edge')
                profile.update(edit)
                self.assertTrue(any(d['code'] == 'open-edge-interface' for d in validate_document(doc)))

    def test_resized_frontage_stays_one_deep_without_losing_counter(self):
        for length in (3, 5, 8):
            with self.subTest(length=length):
                doc = program()
                queue = find_space(doc, 'command/personnel/hop_line')
                area = next(r['parameters'] for r in queue['requirements'] if r['id'] == 'floor_area')
                area.update(min=length, preferred=length, max=length)
                state, report = run_generation(doc, {'root_restarts': 2}, seed=length)
                self.assertTrue(report['verification']['verified'], report['verification']['hard_failures'])
                cells = state.floors['command[0]/personnel[0]/hop_line[0]']
                x, y, xx, yy = bbox(cells)
                self.assertEqual(length, len(cells))
                self.assertEqual(1, min(xx-x, yy-y))


if __name__ == '__main__':
    unittest.main()
