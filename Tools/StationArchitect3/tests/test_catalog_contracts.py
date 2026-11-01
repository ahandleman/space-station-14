"""Protect the requested functional relationships in the authored deliverable."""
import copy
import importlib.util
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('catalog_validator', ROOT / 'validate.py')
V = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(V)


def spaces(items, parent=''):
    for node in items:
        path = '/'.join(filter(None, [parent, node['id']]))
        yield path, node
        yield from spaces(node.get('spaces', []), path)


class CatalogContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.program = V.load_program(ROOT / 'catalog/standard-station.yaml')
        cls.nodes = dict(spaces(cls.program['spaces']))
        cls.edges = {r['authoredId']: r for r in V.flatten_relationships(cls.program)}

    def test_full_catalog_and_small_example_are_valid_authoring(self):
        for name in ['catalog/standard-station.yaml', 'examples/serving-clusters.yaml']:
            with self.subTest(name=name):
                self.assertEqual(V.validate_document(V.load_program(ROOT / name)), [])

    def test_both_food_fronts_require_the_same_parent_commons(self):
        kitchen = self.edges['kitchen_serves_shared_commons']
        bar = self.edges['bar_serves_shared_commons']
        self.assertEqual(kitchen['to'], bar['to'])
        commons = self.nodes[kitchen['to']['space']]
        self.assertEqual(commons['count'], {'min': 1, 'preferred': 1, 'max': 1})
        for edge in [kitchen, bar]:
            self.assertEqual(edge['type'], 'shared_boundary')
            self.assertEqual(edge['priority'], 'mandatory')
            self.assertEqual(edge['matching']['scope'], 'same_parent')
            self.assertEqual(edge['parameters']['interface'], 'service_counter')

    def test_service_interfaces_do_not_replace_staff_routes(self):
        for stem, public in [('service_hospitality_kitchen', 'service/hospitality/bar_commons'),
                             ('service_hospitality_bar', 'service/hospitality/bar_commons'),
                             ('command_personnel_hop_office', 'command/personnel/hop_line')]:
            edge = self.edges['access_' + stem]
            self.assertEqual(edge['priority'], 'mandatory')
            self.assertIn(public, edge['parameters']['avoid'])
        hop = self.edges['hop_serves_queue']
        self.assertEqual(hop['priority'], 'mandatory')
        interface = next(x for x in self.program['interfaces'] if x['id'] == hop['parameters']['interface'])
        self.assertNotIn('crew_passage', interface['capabilities'])
        self.assertEqual(interface['access']['anyOf'], [['HeadOfPersonnel']])

    def test_conditional_support_is_required_from_the_consumer(self):
        edge = self.edges['atmos_requires_reserve']
        self.assertEqual(edge['from'], {'space': 'engineering/atmos'})
        self.assertEqual(edge['matching']['presence'], 'require_target')
        bank = next(f for f in self.nodes['engineering/gas_storage']['features'] if f['id'] == 'air_bank')
        self.assertEqual(bank['prototypes'], [], 'A collector must not duplicate the real canister requirement.')

    def test_teg_has_distinct_driven_supply_and_complete_return_contracts(self):
        room = self.nodes['engineering/teg/generator_room']
        features = {f['id']: f for f in room['features']}
        for side in ['hot', 'cold']:
            pump = side + '_pump'
            self.assertIn(pump, features)
            supply = self.edges[side + '_supply']
            self.assertTrue(supply['to']['feature'].endswith('#' + pump))
            delivery = self.edges[side + '_pump_delivery']
            self.assertEqual(delivery['parameters']['toPort'], side + '_in')
            returned = self.edges[side + '_return']
            self.assertEqual(returned['matching']['targets'], 'all')
            self.assertTrue(returned['parameters']['distinctCircuit'])
        self.assertNotEqual(self.edges['hot_supply']['parameters']['medium'],
                            self.edges['cold_supply']['parameters']['medium'])

    def test_vacuum_is_supported_same_grid_and_distinct_from_hot_enclosure(self):
        generator = self.nodes['engineering/teg/generator_room']
        hot = self.nodes['engineering/teg/burn_chamber']
        cold = self.nodes['engineering/teg/cold_radiator']
        self.assertEqual({n['placement']['grid'] for n in [generator, hot, cold]}, {'station'})
        self.assertEqual(hot['environment'], 'hot_process')
        self.assertEqual(hot['placement']['surface'], 'floor')
        self.assertEqual(cold['environment'], 'vacuum')
        self.assertEqual(cold['placement']['surface'], 'lattice')

    def test_declared_extension_local_references_resolve_without_claiming_physics(self):
        for _, node in self.nodes.items():
            features = {f['id']: f for f in node['features']}
            for requirement in node['requirements']:
                params = requirement['parameters']
                if requirement['type'] == 'x_feature_exposure':
                    self.assertTrue(set(params['featureIds']) <= features.keys())
                    self.assertTrue(params['allInstances'])
                    self.assertLessEqual(params['minimumClearDirections'], len(params['sampleDirections']))
                if requirement['type'] == 'x_teg_operating_conditions':
                    self.assertIn(params['assemblyFeature'], features)
                    self.assertTrue(set(params['flowDrivers']) <= features.keys())
                    self.assertIn(params['startupPowerPort'],
                                  {p['id'] for p in features[params['assemblyFeature']]['ports']})
        compiled = V.compile_document(self.program)
        self.assertEqual(compiled['certification']['runtimeOperation'], 'not_evaluated')
        thermal = [e for e in compiled['externalEvaluatorsRequired']
                   if e['type'] in ['x_teg_operating_conditions', 'x_feature_exposure']]
        self.assertEqual(len(thermal), 3)
        self.assertTrue(all(e['priority'] == 'mandatory' for e in thermal))

    def test_undefined_extension_or_weakened_operating_parameters_fail(self):
        doc = copy.deepcopy(self.program)
        del doc['extensions'][0]
        self.assertTrue(V.validate_document(doc))
        doc = copy.deepcopy(self.program)
        node = dict(spaces(doc['spaces']))['engineering/teg/generator_room']
        next(r for r in node['requirements'] if r['type'] == 'x_teg_operating_conditions')['parameters']['isolateCircuits'] = False
        self.assertTrue(V.validate_document(doc))

    def test_secure_and_head_rooms_do_not_use_generic_public_staff_access(self):
        expected = {'medical/surgery': 'medical', 'medical/chemistry': 'chemistry',
                    'medical/medical_office': 'cmo', 'engineering/engineering_office': 'ce',
                    'science/science_office': 'rd', 'security/security_office': 'hos',
                    'cargo/cargo_office': 'qm'}
        for path, actor in expected.items():
            self.assertEqual(self.nodes[path]['access'], actor)
        self.assertNotIn('security/cell', self.nodes)
        self.assertEqual(self.edges['access_security_perma']['to'], {'space': 'security/sentencing'})


if __name__ == '__main__':
    unittest.main()
