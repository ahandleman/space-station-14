"""Specific functional connectors remain usable without opening protected leaves."""
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from validate import load_program, validate_document


class DepartmentPolicyTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.document = load_program(ROOT / 'catalog/standard-station.yaml')
        cls.nodes = {}
        def walk(nodes, prefix=''):
            for node in nodes:
                path = (prefix + '/' + node['id']).strip('/')
                cls.nodes[path] = node
                walk(node['spaces'], path)
        walk(cls.document['spaces'])

    def policy(self, path):
        return next(r for r in self.nodes[path]['requirements'] if r['type'] == 'access_policy')

    def test_actual_connectors_permit_controlled_transit(self):
        connectors = ['medical/treatment', 'medical/morgue', 'security/brig',
                      'service/hospitality/kitchen', 'command/bridge', 'cargo/warehouse',
                      'cargo/loading_bay', 'cargo/salvage', 'cargo/salvage_airlock',
                      'maintenance_rooms/external_access']
        for path in connectors:
            with self.subTest(path=path):
                rule = self.policy(path)
                self.assertTrue(rule['parameters']['throughTraffic'])
                self.assertFalse(rule['parameters']['allowPublicEntry'])
                evidence = ('requested_genpop_and_local_connections' if path == 'security/brig'
                            else 'department_connector_policy_review')
                self.assertIn(evidence, rule['evidence'])
                self.assertGreater(len(rule['description']), 70)

    def test_protected_leaves_did_not_become_corridors(self):
        self.assertNotIn('security/cell', self.nodes, 'Shared custody no longer selects individual timed cells.')
        for path in ['medical/surgery', 'medical/cryogenics', 'service/hospitality/cold_store',
                     'security/wardens_office', 'security/armory',
                     'security/perma', 'command/captains_room', 'command/vault',
                     'maintenance_rooms/deep_store']:
            with self.subTest(path=path):
                self.assertFalse(self.policy(path)['parameters']['throughTraffic'])

    def test_space_vestibules_keep_their_interior_neighbor_restrictions(self):
        salvage = self.policy('cargo/salvage_airlock')['parameters']
        self.assertEqual(['../salvage'], salvage['entryFrom'])
        self.assertFalse(salvage['allowMaintenanceEntry'])
        self.assertTrue(salvage['allowExteriorEntry'])
        exterior = self.policy('maintenance_rooms/external_access')['parameters']
        self.assertEqual(['/maintenance'], exterior['entryFrom'])
        self.assertTrue(exterior['allowExteriorEntry'])
        deep = self.policy('maintenance_rooms/deep_store')['parameters']
        self.assertIn('/maintenance', deep['mustTraverse'])
        self.assertFalse(deep['allowExteriorEntry'])

    def test_public_sentencing_does_not_open_restricted_custody_or_staff_floor(self):
        sentencing = self.policy('security/sentencing')['parameters']
        self.assertTrue(sentencing['allowPublicEntry'])
        self.assertTrue(sentencing['throughTraffic'])
        brig = self.policy('security/brig')['parameters']
        self.assertFalse(brig['allowPublicEntry'])
        self.assertEqual({'../sentencing', '../wardens_office', '../secure_circulation'},
                         set(brig['entryFrom']))
        perma = self.policy('security/perma')['parameters']
        self.assertFalse(perma['allowPublicEntry'])
        self.assertEqual(['../sentencing'], perma['entryFrom'])
        edges = self.nodes['security/sentencing']['relationships']
        public = [r for r in edges if r['type'] == 'route' and r['priority'] == 'mandatory'
                  and r['to'].get('space') == '/public_hall'
                  and r['parameters']['accessProfile'] == 'public']
        self.assertTrue(public, 'Released prisoners must have an uncredentialed outward route.')
        self.assertTrue(all(r['parameters']['minWidth'] >= 2 for r in public))
        staff = [r for r in edges if r['type'] == 'shared_boundary'
                 and r['to'].get('space') == '../secure_circulation']
        self.assertTrue(staff)
        self.assertTrue(all(r['parameters']['interface'] == 'security_door' for r in staff))

    def test_loading_bay_can_reach_its_actual_required_dock(self):
        node = self.nodes['cargo/loading_bay']
        self.assertTrue(self.policy('cargo/loading_bay')['parameters']['allowExteriorEntry'])
        self.assertTrue(any(r['type'] == 'docking' for r in node['requirements']))
        self.assertTrue(any(r['id'] == 'loading_warehouse' and r['type'] == 'shared_boundary'
                            for r in node['relationships']))

    def test_morgue_remains_role_controlled_and_has_nonclinical_approaches(self):
        profile = next(p for p in self.document['interfaces'] if p['id'] == 'morgue_door')
        self.assertEqual([['Medical'], ['Detective'], ['Chapel']], profile['access']['anyOf'])
        morgue = self.nodes['medical/morgue']
        rule = next(r for r in morgue['requirements'] if r['type'] == 'x_entry_interface_policy')
        self.assertEqual('morgue_door', rule['parameters']['interface'])
        self.assertTrue(rule['parameters']['allRoutineCrewEntrances'])
        self.assertTrue(rule['parameters']['preserveUpstreamCredentialChecks'])
        for actor in ('detective', 'chaplain'):
            route = next(r for r in morgue['relationships'] if r['id'] == 'morgue_'+actor+'_approach')
            self.assertEqual(actor, route['parameters']['accessProfile'])
            for forbidden in ('../medical_circulation', '../treatment', '../surgery', '../chemistry'):
                self.assertIn(forbidden, route['parameters']['avoid'])
            self.assertNotIn('../triage', route['parameters']['avoid'])

    def test_confessional_is_a_single_person_destination_not_a_corridor(self):
        chapel = self.nodes['service/chapel']
        widths = {r['id']: r['parameters']['minWidth'] for r in chapel['relationships'] if r['type'] == 'route'}
        self.assertEqual(1, widths['nested_access_service_chapel_confessional'])
        self.assertEqual(2, widths['nested_access_service_chapel_crematorium'])
        self.assertEqual(2, widths['access_service_chapel'])
        policy = self.policy('service/chapel/confessional')['parameters']
        self.assertFalse(policy['throughTraffic'])
        self.assertEqual(['./..'], policy['entryFrom'])

    def test_catalog_counts_and_thin_queue_leave_valid_aggregate_bounds(self):
        self.assertEqual({'min': 1, 'preferred': 1, 'max': 1}, self.nodes['service/hospitality']['count'])
        queue = self.nodes['command/personnel/hop_line']
        short = next(r['parameters'] for r in queue['requirements']
                     if r['type'] == 'metric' and r['parameters']['metric'] == 'shape.short_side')
        self.assertEqual((1, 1), (short['min'], short['max']))
        self.assertFalse([d for d in validate_document(self.document) if d['severity'] == 'error'])


if __name__ == '__main__':
    unittest.main()
