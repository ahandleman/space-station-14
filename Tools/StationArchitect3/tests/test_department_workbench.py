"""The browser API keeps rejected proposals separate from accepted departments."""
import copy
from http.client import HTTPConnection
from http.server import ThreadingHTTPServer
import json
from pathlib import Path
import sys
import tempfile
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
import workbench as W
from validate import load_program


def candidate(area=16, accepted=True, extra=False):
    room = 'command[0]/office[0]'
    spaces = {room: {'kind': 'room', 'template': 'command/office',
                     'node': {'name': 'Office'}}}
    features = {room+'#desk[0]': {'room': room, 'prototype': 'Table', 'node': {'name': 'Desk', 'prototypes': ['Table']}}}
    if extra:
        features[room+'#extra[0]'] = {'room': room, 'prototype': 'Chair', 'node': {'name': 'Chair', 'prototypes': ['Chair']}}
    state = SimpleNamespace(program=SimpleNamespace(spaces=spaces, features=features),
                            floors={room: {(x, 0) for x in range(area)}},
                            features={key: {'layer': 'floor'} for key in features})
    failures = [] if accepted else [{'id': 'floor_area', 'owner': room, 'type': 'metric',
                                    'priority': 'mandatory', 'status': 'violated',
                                    'message': 'The requested floor cannot fit the selected rooms.'}]
    report = {'department': 'command[0]', 'requestedArea': area,
              'candidateStatus': 'verified_local_department_layout' if accepted else 'incomplete_department',
              'verification': {'localLayoutVerified': accepted, 'verified': accepted,
                               'checks': list(failures), 'hard_failures': list(failures),
                               'metrics': {'department_floor': area},
                               'inventory': {'selectedRooms': 1, 'realizedRooms': 1,
                                             'selectedFeatures': len(features), 'realizedFeatures': len(features)},
                               'pendingRequirements': [], 'externalContracts': []}}
    return state, report


def save_candidate(state, report, directory):
    """Record the actual pair handed to the existing export function."""
    directory.mkdir(parents=True)
    (directory / 'layout.json').write_text(json.dumps({'area': sum(map(len, state.floors.values()))}), encoding='utf-8')
    (directory / 'report.json').write_text(json.dumps(report), encoding='utf-8')
    (directory / 'layout.svg').write_text('<svg xmlns="http://www.w3.org/2000/svg"/>', encoding='utf-8')


class WorkbenchTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.document = load_program(ROOT / 'catalog/standard-station.yaml')
        with tempfile.TemporaryDirectory() as directory:
            cls.catalog_data = W.DepartmentWorkbench(cls.document, directory).catalog()

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.workbench = W.DepartmentWorkbench(self.document, self.directory.name)
        # Build the actual full catalog once, retaining its real scope registry
        # in API tests while generation itself is replaced by controlled proposals.
        self.workbench._catalog = copy.deepcopy(self.catalog_data)
        self.request = {'department': 'command', 'scope': 'command/core', 'seed': 17, 'area': 16, 'aspect': 1}

    def generate(self, values, payload=None, resize=False):
        with patch.object(self.workbench, '_resolved', return_value=None), \
                patch.object(W, 'generate_department', return_value=values), \
                patch.object(W, 'save_run', side_effect=save_candidate):
            return self.workbench.generate(payload or self.request, resize=resize)

    def test_catalog_has_every_department_with_complete_selected_inventory(self):
        result = self.workbench.catalog()
        self.assertEqual(11, len(result['departments']))
        self.assertEqual(set(W.department_ids(self.document)), {d['id'] for d in result['departments']})
        self.assertTrue(all(d['bounds']['min'] <= d['bounds']['preferred'] <= d['bounds']['max']
                            for d in result['departments']))
        service = next(d for d in result['departments'] if d['id'] == 'service')
        self.assertEqual(1, sum(r['id'].endswith('/bar[0]') for r in service['rooms']))
        self.assertTrue(any('/chapel[0]/crematorium[0]' in r['id'] for r in service['rooms']))
        self.assertGreater(service['selectedFeatures'], 15)
        medical = next(d for d in result['departments'] if d['id'] == 'medical')
        self.assertEqual(1, medical['abstractServices'])
        self.assertEqual(medical['totalDeclaredFeatures'], medical['selectedFeatures'] + medical['abstractServices'])
        result['departments'].clear()
        self.assertEqual(11, len(self.workbench.catalog()['departments']), 'Callers cannot mutate cached metadata.')

    def test_scopes_partition_the_complete_owner_inventory_without_duplicates(self):
        for owner in self.workbench.catalog()['departments']:
            with self.subTest(owner=owner['id']):
                scoped_rooms = [r['id'] for scope in owner['scopes'] for r in scope['rooms']]
                self.assertEqual({r['id'] for r in owner['rooms']}, set(scoped_rooms))
                self.assertEqual(len(scoped_rooms), len(set(scoped_rooms)))
                self.assertEqual(owner['selectedFeatures'], sum(s['selectedFeatures'] for s in owner['scopes']))
                self.assertEqual(owner['abstractServices'], sum(s['abstractServices'] for s in owner['scopes']))
                self.assertTrue(all(s['bounds']['min'] <= s['bounds']['preferred'] <= s['bounds']['max'] for s in owner['scopes']))
                if owner['organization']['mode'] == 'distributed':
                    self.assertNotIn(owner['id'], {s['id'] for s in owner['scopes']})
                    self.assertFalse(any(s['kind'] == 'department' for s in owner['scopes']))

    def test_distributed_collections_require_a_scope_and_mixed_owners_default_to_the_core(self):
        self.assertEqual('command/core', self.workbench._scope_id('command', None))
        self.assertEqual('arrivals', self.workbench._scope_id('arrivals', None))
        for owner in ('commons', 'service', 'maintenance_rooms'):
            with self.subTest(owner=owner), self.assertRaises(W.APIError):
                self.workbench.validate_request({**self.request, 'department': owner, 'scope': None})
        for scope in ('commons', 'commons/restrooms[0]', '../command/core', 4, False, {}):
            with self.subTest(scope=scope), self.assertRaises(W.APIError):
                self.workbench.validate_request({**self.request, 'scope': scope})
        actual = self.workbench.validate_request({**self.request, 'department':'commons', 'scope':'commons/restrooms[0]'})
        self.assertEqual(('commons', 'commons/restrooms[0]'), actual[:2])

    def test_only_configured_departments_and_bounded_numeric_inputs_are_accepted(self):
        bad = [None, [], {'path': 'outside.yaml'},
               {**self.request, 'department': '../catalog'}, {**self.request, 'seed': True},
               {**self.request, 'seed': -1}, {**self.request, 'seed': 2**32},
               {**self.request, 'area': 0}, {**self.request, 'area': 1.5},
               {**self.request, 'aspect': float('nan')}, {**self.request, 'aspect': float('inf')},
               {**self.request, 'aspect': False}, {**self.request, 'aspect': 4.01},
               {**self.request, 'output': 'elsewhere'}]
        for payload in bad:
            with self.subTest(payload=payload), self.assertRaises(W.APIError):
                self.workbench.validate_request(payload)
        self.assertEqual(('command', 'command/core', 17, 16, 1.0), self.workbench.validate_request(self.request))

    def test_resize_requires_a_previous_passing_selection(self):
        with self.assertRaises(W.APIError) as raised:
            self.workbench.generate(self.request, resize=True)
        self.assertEqual(409, raised.exception.status)

    def test_workbench_port_cannot_be_shared_by_an_older_server(self):
        first = W.WorkbenchServer(('127.0.0.1', 0), W.handler_for(self.workbench))
        self.addCleanup(first.server_close)
        with self.assertRaises(OSError):
            second = W.WorkbenchServer(first.server_address, W.handler_for(self.workbench))
            self.addCleanup(second.server_close)

    def test_service_demands_are_shown_without_a_false_unplaced_machine(self):
        state, report = candidate()
        room = next(iter(state.floors))
        state.program.features[room+'#power'] = {'room': room, 'node': {'name': 'Power supply demand'}}
        result = self.generate((state, report))
        row = result['visible']['rooms'][0]
        self.assertEqual(1, row['physicalFeatures'])
        self.assertEqual(1, row['abstractServices'])
        demand = next(f for f in row['features'] if f['abstract'])
        self.assertFalse(demand['placed'])
        self.assertIsNone(demand['prototype'])

    def test_failed_resize_keeps_prior_preview_and_exports_real_failed_geometry(self):
        first = self.generate(candidate())
        failed = self.generate(candidate(area=5, accepted=False), {**self.request, 'area': 5}, resize=True)
        self.assertTrue(first['accepted'])
        self.assertFalse(failed['accepted'])
        self.assertTrue(failed['retainedPrevious'])
        self.assertTrue(failed['canResize'])
        self.assertEqual(first['visible']['svgUrl'], failed['visible']['svgUrl'])
        self.assertEqual(16, failed['visible']['report']['requestedArea'])
        self.assertEqual(5, failed['proposal']['report']['requestedArea'])
        proposal_path = self.workbench.static_path(failed['proposal']['layoutUrl'])
        self.assertEqual(5, json.loads(proposal_path.read_text())['area'])
        self.assertEqual(16, json.loads(self.workbench.static_path(first['visible']['layoutUrl']).read_text())['area'])
        self.assertFalse(failed['proposal']['report']['resizeAccepted'])
        self.assertTrue(failed['proposal']['report']['selectionPreserved'])

    def test_successful_resize_replaces_visible_layout_without_changing_selection(self):
        first = self.generate(candidate())
        resized = self.generate(candidate(area=24), {**self.request, 'area': 24}, resize=True)
        self.assertTrue(resized['accepted'])
        self.assertFalse(resized['retainedPrevious'])
        self.assertNotEqual(first['visible']['id'], resized['visible']['id'])
        self.assertEqual(resized['proposal']['id'], resized['visible']['id'])
        self.assertTrue(resized['proposal']['report']['selectionPreserved'])

    def test_even_passing_geometry_cannot_change_selected_equipment_during_resize(self):
        first = self.generate(candidate())
        changed = self.generate(candidate(area=20, extra=True), resize=True)
        self.assertFalse(changed['accepted'])
        self.assertTrue(changed['retainedPrevious'])
        self.assertEqual(first['visible']['id'], changed['visible']['id'])
        check = changed['proposal']['report']['verification']['hard_failures'][-1]
        self.assertEqual('workbench::resize_selection', check['id'])

    def test_an_unaccepted_new_seed_does_not_reuse_another_seeds_layout(self):
        self.generate(candidate())
        failed = self.generate(candidate(area=5, accepted=False), {**self.request, 'seed': 18})
        self.assertFalse(failed['retainedPrevious'])
        self.assertFalse(failed['canResize'])
        self.assertEqual(failed['proposal']['id'], failed['visible']['id'])

    def test_acceptance_cache_cannot_leak_between_rooms_of_the_same_owner_and_seed(self):
        one = {**self.request, 'department':'commons', 'scope':'commons/restrooms[0]'}
        two = {**one, 'scope':'commons/restrooms[1]'}
        accepted_one = self.generate(candidate(), one)
        failed_two = self.generate(candidate(area=5, accepted=False), two)
        self.assertFalse(failed_two['accepted'])
        self.assertFalse(failed_two['retainedPrevious'])
        self.assertFalse(failed_two['canResize'])
        with self.assertRaises(W.APIError) as raised:
            self.workbench.generate(two, resize=True)
        self.assertEqual(409, raised.exception.status)
        accepted_two = self.generate(candidate(area=24), two)
        failed_one = self.generate(candidate(area=5, accepted=False), one, resize=True)
        self.assertTrue(failed_one['retainedPrevious'])
        self.assertEqual(accepted_one['visible']['id'], failed_one['visible']['id'])
        self.assertNotEqual(accepted_two['visible']['id'], failed_one['visible']['id'])
        self.assertEqual(one['scope'], failed_one['scope'])

    def test_static_routes_only_serve_known_assets_inside_output(self):
        run_id = 'run-' + 'a'*32
        self.assertEqual(Path(self.directory.name).resolve() / run_id / 'report.json',
                         self.workbench.static_path('/runs/'+run_id+'/report.json'))
        for request in ('/workbench.py', '/api/catalog/../../catalog/standard-station.yaml',
                        '/runs/'+run_id+'/../../secrets.txt', '/runs/'+run_id+'/%2e%2e%2fsecrets.txt',
                        '/runs/'+run_id+'/%5c..%5csecrets.txt', '/runs/'+run_id+'/program.yaml',
                        '/runs/not-a-generated-run/report.json'):
            with self.subTest(request=request):
                self.assertIsNone(self.workbench.static_path(request))
        self.assertTrue(self.workbench.static_path('/').is_file())
        self.assertTrue(self.workbench.static_path('/departments.js').is_file())

    def test_http_json_contract_rejects_foreign_origins_and_preserves_resize_status(self):
        server = ThreadingHTTPServer(('127.0.0.1', 0), W.handler_for(self.workbench))
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        self.addCleanup(server.server_close)
        self.addCleanup(lambda: (server.shutdown(), thread.join(timeout=2)))

        def request(path, payload, headers=None):
            connection = HTTPConnection('127.0.0.1', server.server_port, timeout=5)
            try:
                connection.request('POST', path, json.dumps(payload), headers or {'Content-Type': 'application/json'})
                response = connection.getresponse()
                return response.status, json.loads(response.read())
            finally:
                connection.close()

        status, body = request('/api/resize', self.request)
        self.assertEqual(409, status)
        self.assertIn('Generate', body['error'])
        status, _ = request('/api/generate', self.request,
                            {'Content-Type': 'application/json', 'Origin': 'https://outside.example'})
        self.assertEqual(403, status)
        self.assertEqual(415, request('/api/generate', self.request, {'Content-Type': 'text/plain'})[0])
        self.assertEqual(404, request('/api/files', self.request)[0])
        self.assertEqual(400, request('/api/generate', {**self.request, 'source': 'other.yaml'})[0])
        with patch.object(self.workbench, '_resolved', return_value=None), \
                patch.object(W, 'generate_department', return_value=candidate()), \
                patch.object(W, 'save_run', side_effect=save_candidate):
            status, body = request('/api/generate', self.request,
                                   {'Content-Type': 'application/json; charset=utf-8',
                                    'Origin': f'http://127.0.0.1:{server.server_port}'})
        self.assertEqual(200, status)
        self.assertTrue(body['accepted'])
        self.assertEqual('command/core', body['scope'])
        self.assertEqual(1, len(body['visible']['rooms'][0]['features']))


if __name__ == '__main__':
    unittest.main()
