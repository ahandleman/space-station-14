"""Regression contracts for map-informed local dependencies, without simulating physics."""
import importlib.util
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('additional_validator', ROOT / 'validate.py')
V = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(V)


def walk(items, parent=''):
    for node in items:
        path = '/'.join(filter(None, [parent, node['id']]))
        yield path, node
        yield from walk(node.get('spaces', []), path)


class AdditionalDependencies(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.doc = V.load_program(ROOT / 'catalog/standard-station.yaml')
        cls.nodes = dict(walk(cls.doc['spaces']))
        cls.edges = {e['authoredId']: e for e in V.flatten_relationships(cls.doc)}

    def feature(self, path, id):
        return next(f for f in self.nodes[path]['features'] if f['id'] == id)

    def requirement(self, path, kind):
        return next(r for r in self.nodes[path]['requirements'] if r['type'] == kind)

    def test_shipped_definitions_only_declare_local_relationships_and_choices(self):
        for file in ['catalog/standard-station.yaml', 'examples/serving-clusters.yaml']:
            doc = V.load_program(ROOT / file)
            self.assertNotIn('relationships', doc)
            self.assertNotIn('choices', doc)
            for edge in V.flatten_relationships(doc):
                self.assertTrue(edge['declarationScope'])
            for choice in V.resolve_document(doc)['choices']:
                self.assertTrue(choice['declarationScope'])

    def test_named_pair_constraints_belong_to_their_smallest_functional_suite(self):
        for name, owner in [('hop_serves_queue', 'command/personnel'),
                            ('kitchen_serves_shared_commons', 'service/hospitality'),
                            ('bar_serves_shared_commons', 'service/hospitality'),
                            ('analyzer_binding', 'science/artifact_lab')]:
            self.assertEqual(self.edges[name]['declarationScope'], owner)
        lab = self.nodes['science/artifact_lab']
        binding = next(e for e in lab['relationships'] if e['id'] == 'analyzer_binding')
        self.assertEqual(binding['from'], {'feature': '#analysis_console'})
        self.assertEqual(binding['to'], {'feature': '#artifact_analyzer'})

    def test_artifact_coverage_and_range_preference_use_the_real_pairing(self):
        binding = self.edges['analyzer_binding']
        self.assertEqual(binding['matching']['targets'], 'distinct')
        self.assertTrue(binding['parameters']['oneToOne'])
        self.assertTrue(binding['parameters']['persistBinding'])
        self.assertEqual(self.edges['analyzer_requires_console']['matching']['targets'], 'distinct')
        reach = self.edges['analyzer_link_reach']
        self.assertEqual(reach['priority'], 'desired')
        self.assertEqual(reach['parameters']['bindingRelationship'], binding['authoredId'])
        self.assertEqual(reach['declarationScope'], binding['declarationScope'])

    def test_research_consumers_own_their_service_needs(self):
        edges = [e for e in self.edges.values() if e['type'] == 'x_research_association']
        self.assertEqual(len(edges), 6)
        for edge in edges:
            self.assertEqual(edge['from']['feature'].split('#')[0], edge['declarationScope'])
            self.assertEqual(edge['to'], {'feature': 'science/server_room#research_server'})
            self.assertTrue(edge['parameters']['sameGrid'])
            self.assertTrue(edge['parameters']['requireRegistration'])
            self.assertEqual(edge['matching']['presence'], 'require_target')

    def test_incoming_power_is_a_consumer_owned_obligation(self):
        for name, owner in [('substation_hv_input', 'engineering/substation'),
                            ('power_plant_mv_input', 'engineering/power'),
                            ('teg_startup', 'engineering/teg/generator_room'),
                            ('burn_fuel', 'engineering/teg/burn_chamber'),
                            ('breathing_air_reserve', 'engineering/atmos')]:
            edge = self.edges[name]
            self.assertEqual(edge['declarationScope'], owner)
            self.assertEqual(edge['from']['feature'].split('#')[0], owner)
            self.assertEqual(edge['parameters']['flow'], 'reverse')
            self.assertEqual(edge['matching']['targets'], 'one')
        self.assertEqual(self.feature('engineering/substation', 'converter')['prototypes'], ['SubstationBasic'])
        self.assertEqual(self.feature('engineering/substation', 'smes')['count']['min'], 0)

    def test_cryo_support_covers_every_pod_and_cooler(self):
        for name in ['cryo_all_pods_connected', 'cryo_all_coolers_connected']:
            edge = self.edges[name]
            self.assertEqual(edge['declarationScope'], 'medical/cryogenics')
            self.assertEqual(edge['matching']['targets'], 'all')
        params = self.requirement('medical/cryogenics', 'x_cryotherapy_support')['parameters']
        self.assertTrue(params['allPodInstances'])
        self.assertTrue(params['keepRoomHabitable'])
        self.assertTrue(params['requireAttachedCanisters'])
        self.assertLessEqual(params['patientTemperatureMaximumK'], 273.15)
        self.assertEqual(self.nodes['medical/cryogenics']['environment'], 'habitable')
        media = {p['medium'] for f in self.nodes['medical/cryogenics']['features'] for p in f['ports']}
        self.assertTrue({'cryo_supply', 'cryo_treatment', 'cryo_waste'} <= media)

    def test_cold_store_is_cooled_and_belongs_to_its_own_kitchen(self):
        self.assertEqual(self.nodes['service/hospitality/cold_store']['access'], 'kitchen')
        self.assertEqual(self.nodes['service/hospitality/cold_store']['environment'], 'cold_storage')
        self.assertEqual(self.edges['food_cold_store']['matching']['scope'], 'same_parent')
        params = self.requirement('service/hospitality/cold_store', 'x_thermal_service')['parameters']
        self.assertTrue(params['requireRoomGasExchange'])
        self.assertTrue(params['requireMaintainedTemperature'])
        self.assertLessEqual(params['preferredTemperatureK'], params['maximumTemperatureK'])
        self.assertIn('WaterTankHighCapacity', self.feature('service/botany', 'water_supply')['prototypes'])

    def test_morgue_keeps_cross_role_admission_and_nonclinical_approaches(self):
        interface = next(i for i in self.doc['interfaces'] if i['id'] == 'morgue_door')
        self.assertEqual(interface['access']['anyOf'], [['Medical'], ['Detective'], ['Chapel']])
        self.requirement('medical/morgue', 'x_entry_interface_policy')
        for actor in ['detective', 'chaplain']:
            edge = self.edges['morgue_' + actor + '_approach']
            self.assertEqual(edge['declarationScope'], 'medical/morgue')
            self.assertEqual(edge['parameters']['accessProfile'], actor)
            self.assertIn('medical/medical_circulation', edge['parameters']['avoid'])

    def test_salvage_external_access_and_processing_are_local(self):
        approach = self.edges['salvage_eva_approach']
        self.assertIn('cargo/loading_bay', approach['parameters']['avoid'])
        self.requirement('cargo/salvage_airlock', 'x_airlock_cycle')
        self.requirement('cargo/salvage', 'x_salvage_deployment')
        self.assertEqual(self.nodes['cargo/material_processing']['count']['min'], 0)
        for name in ['materials_to_salvage', 'materials_to_warehouse']:
            self.assertEqual(self.edges[name]['declarationScope'], 'cargo/material_processing')
        self.assertTrue(self.requirement('cargo/material_processing', 'x_material_handling')['parameters']['excludeMachineFromCrewRoute'])

    def test_utility_rules_resolve_local_hardware_without_global_enumeration(self):
        assemblies = {a['id'] for a in self.doc['assemblies']}
        for path, node in self.nodes.items():
            features = {f['id']: f for f in node['features']}
            for rule in node['requirements']:
                params = rule['parameters']
                if rule['type'] == 'x_power_distribution':
                    self.assertEqual(params['scope'], 'local_space')
                    self.assertTrue(set(params['featureIds']) <= features.keys())
                    self.assertTrue(set(params['realizationAssemblies']) <= assemblies)
                    for port in params['incomingPorts'] + params['outgoingPorts']:
                        self.assertIn(port['port'], {p['id'] for p in features[port['feature']]['ports']})
                if rule['type'] == 'x_radio_channel_coverage':
                    self.assertEqual(params['scope'], 'local_installation')
                    self.assertEqual(params['radioReach'], 'same_map')
                    self.assertEqual({c['channel'] for c in params['channels']},
                                     {'Common', 'Supply', 'Engineering', 'Medical', 'Science', 'Security', 'Service', 'Command'})
                    for channel in params['channels']:
                        self.assertTrue(set(channel['providerFeatures']) <= features.keys())
                if rule['type'] == 'x_atmos_plant_support':
                    self.assertEqual(params['scope'], 'local_space')
                    self.assertIn(params['headerFeature'], features)
                    self.assertTrue(set(params['consumerRealizationTemplates'].values()) <= assemblies)
                if rule['type'] == 'x_grid_gravity':
                    self.assertEqual(params['affectedGrid'], 'host')
                    self.assertTrue(set(params['generatorFeatureIds']) <= features.keys())


if __name__ == '__main__':
    unittest.main()
