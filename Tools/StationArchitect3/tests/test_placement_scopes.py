"""Logical owner collections become explicit, independently checked local scopes."""
import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from validate import load_program
from architect.departments import generate_department, resize_department, verify_department
from architect.instances import resolve_instances
from architect.run import load_layout, save_run
from architect.scenarios import all_placement_scopes, isolate_scope, placement_scopes, scope_bounds


class PlacementScopeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.document = load_program(ROOT / 'catalog/standard-station.yaml')
        cls.full = resolve_instances(cls.document, seed=1)
        cls.scopes = all_placement_scopes(cls.full)
        cls.local, cls.owner, cls.contracts, cls.scope = isolate_scope(cls.full, 'commons/lounge[0]')
        cls.bounds = scope_bounds(cls.local, cls.owner)
        cls.state, cls.report = generate_department(cls.document, 'commons',
            target_area=cls.bounds['preferred'], seed=1, resolved=cls.full, scenario=cls.scope['id'])

    def test_scope_partition_preserves_all_selected_physical_inventory_once(self):
        for owner, node in self.full.spaces.items():
            if node['kind'] != 'department':
                continue
            with self.subTest(owner=owner):
                selected = set(self.full.descendants(owner, True))
                seen_rooms, seen_features = set(), set()
                for scope in placement_scopes(self.full, owner):
                    local, key, _, _ = isolate_scope(self.full, scope['id'])
                    rooms = set(local.descendants(key, True))
                    features = set(local.features)
                    self.assertFalse(rooms & seen_rooms)
                    self.assertFalse(features & seen_features)
                    seen_rooms |= rooms
                    seen_features |= features
                self.assertEqual(selected, seen_rooms)
                self.assertEqual({k for k, f in self.full.features.items() if f['room'] in selected}, seen_features)

    def test_distributed_owners_have_local_units_but_no_packed_owner_scope(self):
        for template in ('commons', 'service', 'maintenance_rooms'):
            scopes = [s for s in self.scopes if s['ownerTemplate'] == template]
            with self.subTest(owner=template):
                self.assertGreater(len(scopes), 1)
                self.assertTrue(all(s['organizationMode'] == 'distributed' for s in scopes))
                self.assertTrue(all(not s['wholeOwner'] for s in scopes))
                self.assertFalse(any(s['kind'] == 'department' for s in scopes))
                with self.assertRaises(ValueError):
                    isolate_scope(self.full, template)

    def test_hospitality_contacts_and_nested_chapel_rooms_remain_local(self):
        hospitality, _, _, descriptor = isolate_scope(self.full, 'service/hospitality[0]')
        self.assertEqual('suite', descriptor['kind'])
        expected = {'service/hospitality/'+name for name in ('kitchen','bar','cold_store','bar_commons')}
        self.assertEqual(expected, {n['template'] for n in hospitality.spaces.values() if n['kind'] == 'room'})
        borders = [r for r in hospitality.relationships if r['type'] == 'shared_boundary']
        self.assertGreaterEqual(len(borders), 3)
        self.assertTrue(all(r['source'] in hospitality.spaces and r['target'] in hospitality.spaces for r in borders))
        serving = [r for r in borders if r['authoredId'] in ('kitchen_serves_shared_commons', 'bar_serves_shared_commons')]
        self.assertEqual(2, len(serving))
        self.assertEqual(1, len({r['target'] for r in serving}))
        chapel, _, _, descriptor = isolate_scope(self.full, 'service/chapel[0]')
        self.assertEqual('suite', descriptor['kind'])
        for child in ('confessional', 'crematorium'):
            node = next(n for n in chapel.spaces.values() if n['template'] == 'service/chapel/'+child)
            self.assertIn(node['parent'], chapel.spaces)
            self.assertEqual('contained', node['node']['placement']['nesting'])
            self.assertTrue(any(r['type'] == 'access_policy' for r in node['requirements']))

    def test_solar_installation_keeps_its_bound_controllers_and_access_rooms(self):
        local, _, _, scope = isolate_scope(self.full, 'maintenance_rooms/solar_installations')
        self.assertEqual('installation', scope['kind'])
        expected = {'maintenance_rooms/'+name for name in ('solar_control','solar_array','external_access')}
        physical = {n['template'] for n in local.spaces.values() if n['kind'] in ('room','passage')}
        self.assertEqual(expected, physical)
        for template in expected:
            self.assertEqual(sum(n['template'] == template for n in self.full.spaces.values()),
                             sum(n['template'] == template for n in local.spaces.values()))

    def test_mixed_cores_exclude_independent_facilities_and_do_not_claim_whole_owner(self):
        detached = {'command':'personnel', 'security':'checkpoint', 'engineering':'substation'}
        for owner, child in detached.items():
            with self.subTest(owner=owner):
                local, _, _, scope = isolate_scope(self.full, owner+'/core')
                self.assertEqual('core', scope['kind'])
                self.assertFalse(scope['wholeOwner'])
                self.assertFalse(any(n['template'] == owner+'/'+child or n['template'].startswith(owner+'/'+child+'/')
                                     for n in local.spaces.values()))
                independents = [s for s in self.scopes if s['ownerTemplate'] == owner and s['kind'] != 'core']
                self.assertTrue(independents)
                self.assertTrue(all(s['deferredAncestorRequirements'] for s in
                    (isolate_scope(self.full, i['id'])[3] for i in independents)))

    def test_courtroom_and_law_office_are_separate_rooms_outside_security_core(self):
        core, _, contracts, _ = isolate_scope(self.full, 'security/core')
        detached = {'security/courtroom', 'security/law_office'}
        self.assertFalse(detached & {n['template'] for n in core.spaces.values()})
        for template in detached:
            with self.subTest(room=template):
                local, owner, _, scope = isolate_scope(self.full, template+'[0]')
                self.assertEqual('room', scope['kind'])
                self.assertFalse(scope['wholeOwner'])
                self.assertEqual([template], scope['memberTemplates'])
                self.assertEqual({template}, {local.spaces[k]['template']
                                             for k in local.descendants(owner, True)})
        court_routes = [r for r in core.relationships
                        if self.full.spaces.get(r['source'], {}).get('template') == 'security/sentencing'
                        and self.full.spaces.get(r['target'], {}).get('template') == 'security/courtroom'
                        and r['type'] == 'route' and r['priority'] == 'mandatory']
        self.assertTrue(court_routes, 'Courtroom access remains a required external station route.')
        self.assertTrue(all(r['parameters']['within'] == 'station' for r in court_routes),
                        'Independent Courtroom access may use station halls outside Security ownership.')
        self.assertTrue({r['id'] for r in court_routes} <= {r['id'] for r in contracts})
        for relation in self.full.relationships:
            if relation['type'] == 'shared_boundary' and relation['priority'] == 'mandatory':
                endpoints = {self.full.spaces.get(relation[key], {}).get('template')
                             for key in ('source', 'target')}
                self.assertFalse(endpoints & detached,
                                 'Independent court/legal rooms must not regain forced adjacency through a border.')

    def test_lounge_has_exact_local_area_and_reports_parent_and_station_limits(self):
        checked = self.report['verification']
        self.assertTrue(checked['localLayoutVerified'], checked['hard_failures'])
        self.assertFalse(checked['fullDepartmentVerified'])
        self.assertFalse(self.report['scope']['wholeOwner'])
        self.assertEqual('room', self.report['scope']['kind'])
        self.assertEqual(self.bounds['preferred'], checked['metrics']['department_floor'])
        self.assertEqual(1, checked['inventory']['selectedRooms'])
        self.assertEqual(set(self.local.features), set(self.state.features))
        self.assertIn('station_distribution', self.report['notEvaluated'])
        self.assertIn('owner_aggregate_constraints', self.report['notEvaluated'])
        pending = {c['id'] for c in checked['pendingRequirements'] if c.get('facet') == 'owner_organization'}
        self.assertEqual({r['id'] for r in self.scope['deferredAncestorRequirements']}, pending)
        self.assertIn(checked['layoutQuality']['status'], ('passes_profile','needs_review'))
        self.assertIn('station_distribution', checked['layoutQuality']['notEvaluated'])

    def test_resize_keeps_the_same_local_scope_without_adding_other_amenities(self):
        target = min(self.bounds['max'], self.bounds['preferred'] + max(4, round(self.bounds['preferred'] * .15)))
        self.assertGreater(target, self.bounds['preferred'])
        resized, report = resize_department(self.state, target)
        self.assertTrue(report['resizeAccepted'], report['verification']['hard_failures'])
        self.assertTrue(report['selectionPreserved'])
        self.assertEqual(self.scope['id'], report['scope']['id'])
        self.assertEqual(self.scope['id'], resized.metadata['placementScenario'])
        self.assertEqual(target, report['verification']['metrics']['department_floor'])
        self.assertEqual(1, report['verification']['inventory']['selectedRooms'])
        self.assertEqual(set(self.state.program.features), set(resized.program.features))

    def test_saved_scope_and_deferred_ancestors_are_rebuilt_after_metadata_forgery(self):
        with tempfile.TemporaryDirectory() as directory:
            save_run(self.state, self.report, directory)
            path = Path(directory) / 'layout.json'
            payload = json.loads(path.read_text(encoding='utf-8'))
            metadata = payload['geometry']['metadata']
            metadata['deferredAncestorRequirements'] = []
            metadata['placementScope'] = {'id':'commons', 'wholeOwner':True, 'deferredAncestorRequirements':[]}
            metadata['externalContracts'] = []
            metadata['verified'] = True
            payload['geometry']['summaries'] = {'claimed':{'verified':True}}
            path.write_text(json.dumps(payload), encoding='utf-8')
            restored = load_layout(path)
            checked = verify_department(restored, restored.metadata['departmentInstance'])
            self.assertTrue(checked['localLayoutVerified'], checked['hard_failures'])
            self.assertFalse(checked['fullDepartmentVerified'])
            self.assertEqual(self.scope['id'], restored.metadata['placementScope']['id'])
            self.assertFalse(restored.metadata['placementScope']['wholeOwner'])
            self.assertEqual(self.scope['deferredAncestorRequirements'], restored.metadata['deferredAncestorRequirements'])
            self.assertEqual(self.report['verification']['inventory'], checked['inventory'])
            self.assertEqual(self.report['verification']['externalContracts'], checked['externalContracts'])
            self.assertEqual({r['id'] for r in self.scope['deferredAncestorRequirements']},
                             {c['id'] for c in checked['pendingRequirements'] if c.get('facet') == 'owner_organization'})

    def test_new_owner_name_and_authored_mandatory_border_form_a_generic_suite(self):
        document = copy.deepcopy(self.document)
        owner = next(n for n in document['spaces'] if n['id'] == 'commons')
        owner['id'] = 'visitor_amenities'
        owner['name'] = 'Visitor amenities'
        def rename(value):
            if isinstance(value, dict):
                return {k:rename(v) for k,v in value.items()}
            if isinstance(value, list):
                return [rename(v) for v in value]
            if isinstance(value, str) and (value == '/commons' or value.startswith('/commons/')):
                return '/visitor_amenities'+value[len('/commons'):]
            return value
        document = rename(document)
        owner = next(n for n in document['spaces'] if n['id'] == 'visitor_amenities')
        hospitality = next(n for n in next(n for n in document['spaces'] if n['id'] == 'service')['spaces'] if n['id'] == 'hospitality')
        border = copy.deepcopy(next(r for r in hospitality['relationships'] if r['id'] == 'food_cold_store'))
        border.update(id='lounge_tool_connection', **{'from':{'space':'./lounge'}, 'to':{'space':'./tool_room'}})
        border['parameters']['interface'] = 'public_door'
        owner['relationships'] = [border]
        full = resolve_instances(document, seed=1)
        scopes = placement_scopes(full, 'visitor_amenities[0]')
        joined = [s for s in scopes if {'visitor_amenities/lounge','visitor_amenities/tool_room'} <= set(s['memberTemplates'])]
        self.assertEqual(1, len(joined))
        self.assertEqual('suite', joined[0]['kind'])
        local, _, _, _ = isolate_scope(full, joined[0]['id'])
        relation = next(r for r in local.relationships if r['authoredId'] == 'lounge_tool_connection')
        self.assertIn(relation['source'], local.spaces)
        self.assertIn(relation['target'], local.spaces)
        self.assertTrue(all(s['id'].startswith('visitor_amenities/') for s in scopes))


if __name__ == '__main__':
    unittest.main()
