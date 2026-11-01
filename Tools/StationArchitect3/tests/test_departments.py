"""Complete catalog departments retain their program through resize and reload."""
import json
from pathlib import Path
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from validate import load_program
from architect.department_packing import department_area_bounds
from architect.departments import generate_department, isolate_department, resize_department, verify_department
from architect.instances import resolve_instances
from architect.run import load_layout, save_run
from architect.state import LayoutState, json_ready
from architect.verify import Verifier


def selection(state):
    return ({k for k, n in state.program.spaces.items() if not n.get('harness')},
            set(state.program.features),
            {(r['id'], r['source'], r['target']) for r in state.program.relationships})


class DepartmentIntegrationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.document = load_program(ROOT / 'catalog/standard-station.yaml')
        cls.full = resolve_instances(cls.document, seed=1)
        cls.cases = {}
        cls.bounds = {}
        for department in ('command', 'arrivals'):
            local, key, _, _ = isolate_department(cls.document, department, resolved=cls.full)
            cls.bounds[department] = department_area_bounds(local, key)
            cls.cases[department] = generate_department(cls.document, department,
                cls.bounds[department]['preferred'], seed=1, resolved=cls.full)

    def test_full_selected_catalog_rooms_and_physical_equipment_are_present(self):
        for department, (state, report) in self.cases.items():
            with self.subTest(department=department):
                verification = report['verification']
                self.assertTrue(verification['localLayoutVerified'], verification['hard_failures'])
                local, owner, _, _ = isolate_department(self.document, department, resolved=self.full)
                rooms = set(local.descendants(owner, True))
                self.assertEqual(rooms, {r for r in state.floors if not state.program.spaces[r].get('harness')})
                self.assertEqual(set(local.features), set(state.features))
                inventory = verification['inventory']
                self.assertEqual(len(rooms), inventory['selectedRooms'])
                self.assertEqual(inventory['selectedRooms'], inventory['realizedRooms'])
                self.assertEqual(inventory['selectedFeatures'], inventory['realizedFeatures'])
                self.assertEqual([], inventory['missingRooms'])
                self.assertEqual([], inventory['missingFeatures'])
                self.assertTrue(any(n.get('harness') for n in state.program.spaces.values()))
                self.assertLess(inventory['selectedRooms'], sum(n['kind'] in ('room', 'passage') for n in state.program.spaces.values()))

    def test_abstract_service_demands_do_not_count_as_unplaced_equipment(self):
        local, owner, contracts, _ = isolate_department(self.document, 'medical', resolved=self.full)
        abstract = {key for key, feature in local.features.items()
                    if not feature['node'].get('prototypes') and not feature['node'].get('assembly')}
        physical = set(local.features) - abstract
        self.assertTrue(abstract, 'The real cryogenic circuit is a declared service, not an entity.')
        # Inspect the incomplete program before any pose exists. The report must
        # request every physical machine without inventing a missing circuit icon.
        verification = verify_department(LayoutState(local), owner, contracts)
        inventory = verification['inventory']
        self.assertEqual(len(physical), inventory['selectedFeatures'])
        self.assertEqual(len(abstract), inventory['abstractServices'])
        self.assertEqual(len(local.features), inventory['totalDeclaredFeatures'])
        self.assertEqual(physical, set(inventory['missingFeatures']))
        self.assertFalse(abstract & set(inventory['missingFeatures']))
        self.assertFalse(verification['localLayoutVerified'])

    def test_smaller_and_larger_departments_have_exact_area_and_unchanged_bindings(self):
        for department, (original, _) in self.cases.items():
            before = selection(original)
            for scale in (0.85, 1.15):
                target = round(self.bounds[department]['preferred'] * scale)
                with self.subTest(department=department, target=target):
                    resized, report = resize_department(original, target)
                    self.assertTrue(report['resizeAccepted'], report['verification']['hard_failures'])
                    self.assertTrue(report['selectionPreserved'])
                    self.assertEqual(before, selection(resized))
                    self.assertEqual(target, len(resized.aggregate(resized.metadata['departmentInstance'])))
                    self.assertEqual(target, report['verification']['metrics']['department_floor'])
                    self.assertEqual([], report['verification']['inventory']['missingFeatures'])
                    self.assertNotEqual(original.floors, resized.floors)
            self.assertEqual(self.bounds[department]['preferred'], len(original.aggregate(original.metadata['departmentInstance'])))

    def test_rejected_resize_preserves_original_geometry_and_program(self):
        original, _ = self.cases['arrivals']
        snapshot = json_ready({k: v for k, v in vars(original).items() if k != 'program'})
        result, report = resize_department(original, self.bounds['arrivals']['min']-1)
        self.assertIs(result, original)
        self.assertFalse(report['resizeAccepted'])
        self.assertFalse(report['verification']['localLayoutVerified'])
        self.assertTrue(report['selectionPreserved'])
        self.assertEqual('infeasible_bounds', report['termination'])
        self.assertEqual(snapshot, json_ready({k: v for k, v in vars(original).items() if k != 'program'}))

    def test_saved_department_rebuilds_selection_and_independently_verifies(self):
        state, report = self.cases['command']
        with tempfile.TemporaryDirectory() as directory:
            save_run(state, report, directory)
            path = Path(directory) / 'layout.json'
            restored = load_layout(path)
            checked = verify_department(restored, restored.metadata['departmentInstance'])
            self.assertTrue(checked['localLayoutVerified'], checked['hard_failures'])
            self.assertEqual(selection(state), selection(restored))
            self.assertEqual(report['verification']['inventory'], checked['inventory'])
            self.assertEqual(report['verification']['metrics']['department_floor'], checked['metrics']['department_floor'])
            self.assertEqual(report['verification']['externalContracts'], checked['externalContracts'])
            self.assertTrue((Path(directory) / 'layout.svg').is_file())

    def test_saved_success_flags_cannot_hide_removed_equipment_or_external_contracts(self):
        state, report = self.cases['arrivals']
        with tempfile.TemporaryDirectory() as directory:
            save_run(state, report, directory)
            path = Path(directory) / 'layout.json'
            payload = json.loads(path.read_text(encoding='utf-8'))
            removed = next(iter(payload['geometry']['features']))
            del payload['geometry']['features'][removed]
            payload['geometry']['metadata'].update(verified=True, externalContracts=[])
            payload['geometry']['summaries'] = {'claimed': {'verified': True}}
            path.write_text(json.dumps(payload), encoding='utf-8')
            restored = load_layout(path)
            checked = verify_department(restored, restored.metadata['departmentInstance'])
            self.assertFalse(checked['localLayoutVerified'])
            self.assertIn(removed, checked['inventory']['missingFeatures'])
            self.assertEqual(report['verification']['externalContracts'], checked['externalContracts'])
            self.assertTrue(any(removed in c['id'] for c in checked['hard_failures']))

    def test_layout_success_keeps_station_and_native_obligations_explicit(self):
        for state, report in self.cases.values():
            checked = report['verification']
            self.assertTrue(checked['localLayoutVerified'])
            self.assertFalse(checked['fullDepartmentVerified'])
            self.assertTrue(checked['externalContracts'])
            self.assertIn('station_integration', report['notEvaluated'])
            self.assertIn('native_runtime_operation', report['notEvaluated'])
            self.assertEqual('not_evaluated', checked['facets']['native_operation'])

    def test_unknown_exterior_geometry_cannot_be_reclassified_as_a_local_pass(self):
        state = self.cases['arrivals'][0].clone()
        # Saved canvas bounds can exhaust the independent exterior work limit.
        # This is a geometric unknown, not permission to defer a required dock.
        state.bounds = (-100000, -100000, 100000, 100000)
        checked = verify_department(state, state.metadata['departmentInstance'])
        exterior = [c for c in checked['checks'] if c['type'] in ('docking', 'perimeter')]
        self.assertTrue(exterior)
        self.assertTrue(any(c['status'] == 'unknown' for c in exterior))
        self.assertFalse(checked['localLayoutVerified'])

    def test_unknown_native_footprint_blocks_local_inventory_acceptance(self):
        state = self.cases['arrivals'][0].clone()
        feature = next(key for key, value in state.program.features.items() if value.get('prototype') == 'ComputerRadar')
        state.program.features[feature]['prototype'] = 'ArchitectTestMissingNativePrototype'
        checked = verify_department(state, state.metadata['departmentInstance'])
        self.assertFalse(checked['localLayoutVerified'])
        self.assertTrue(any(feature in c['id'] and c['status'] == 'unknown' for c in checked['hard_failures']))

    def test_public_boundary_access_cannot_unlock_a_staff_room_or_waive_its_policy(self):
        original, report = self.cases['arrivals']
        control = next(k for k, n in original.program.spaces.items() if n['template'] == 'arrivals/docking_control')
        hall = next(k for k, n in original.program.spaces.items() if 'public_circulation' in n['node'].get('tags', []))
        route = {'accessProfile': 'public', 'mode': 'crew', 'minWidth': 1, 'within': 'station'}
        self.assertIsNone(Verifier(original).route(hall, control, route))
        state = original.clone()
        door = next(i for i in state.interfaces if control in (i['a'], i['b']))
        door['profile'] = 'public_door'
        self.assertIsNotNone(Verifier(state).route(hall, control, route))
        access_rule = next(r for r in state.program.spaces[control]['requirements'] if r['type'] == 'access_policy')
        with tempfile.TemporaryDirectory() as directory:
            save_run(state, report, directory)
            path = Path(directory) / 'layout.json'
            payload = json.loads(path.read_text(encoding='utf-8'))
            metadata = payload['geometry']['metadata']
            metadata['harnessSpaces'].append(control)
            metadata['externalContracts'].append({'id': access_rule['id'], 'type': 'access_policy',
                                                  'status': 'pending_station_integration'})
            path.write_text(json.dumps(payload), encoding='utf-8')
            restored = load_layout(path)
            self.assertFalse(restored.program.spaces[control].get('harness', False))
            self.assertNotIn(control, restored.metadata['harnessSpaces'])
            checked = verify_department(restored, restored.metadata['departmentInstance'])
            self.assertFalse(checked['localLayoutVerified'])
            failure = next(c for c in checked['hard_failures'] if c['id'] == access_rule['id'])
            self.assertIn('public entry', failure['message'])
            self.assertNotIn(access_rule['id'], {c['id'] for c in checked['externalContracts']})

    def test_normal_hospitality_is_single_and_repeated_fixture_is_explicit(self):
        normal = resolve_instances(load_program(ROOT / 'examples/station-layout.yaml'))
        repeated = resolve_instances(load_program(ROOT / 'examples/hierarchy-station.yaml'))
        for program, count in ((self.full, 1), (normal, 1), (repeated, 2)):
            for room in ('bar', 'bar_commons', 'kitchen'):
                with self.subTest(count=count, room=room):
                    self.assertEqual(count, sum(n['template'] == 'service/hospitality/'+room for n in program.spaces.values()))


if __name__ == '__main__':
    unittest.main()
