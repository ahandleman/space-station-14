"""Protect relationships discovered in the third direct station-map pass."""
import copy
import importlib.util
from pathlib import Path
import unittest

ROOT=Path(__file__).resolve().parents[1]
SPEC=importlib.util.spec_from_file_location('mapped_validator',ROOT/'validate.py')
V=importlib.util.module_from_spec(SPEC);SPEC.loader.exec_module(V)


def walk(ns,parent=''):
    for n in ns:
        path=parent+'/'+n['id'] if parent else n['id']
        yield path,n
        yield from walk(n.get('spaces',[]),path)


class MappedLocalDependencies(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.doc=V.load_program(ROOT/'catalog/standard-station.yaml')
        cls.nodes=dict(walk(cls.doc['spaces']))
        cls.assemblies={a['id']:a for a in cls.doc['assemblies']}
        cls.edges={e['id']:e for e in V.flatten_relationships(cls.doc)}

    def feature(self,owner,id):
        return next(f for f in self.nodes[owner]['features'] if f['id']==id)

    def rule(self,owner,type):
        return next(r for r in self.nodes[owner]['requirements'] if r['type']==type)

    def test_genpop_defaults_have_local_lockers_and_distinct_directed_gate_roles(self):
        owner='security/sentencing';node=self.nodes[owner]
        if 'security/cell' in self.nodes:
            self.assertEqual(0,self.nodes['security/cell']['count']['preferred'])
            self.assertEqual(0,self.nodes['security/cell']['count']['min'])
        self.assertEqual('mandatory',self.rule(owner,'x_genpop_custody')['priority'])
        profiles={profile['id']:profile for profile in self.doc['interfaces']}
        for profile_id,prototype,direction in [
                ('genpop_entry_turnstile','EnergyGateGenpopEnter','a_to_b'),
                ('genpop_exit_turnstile','EnergyGateGenpopLeave','b_to_a')]:
            features=[f for f in node['features'] if prototype in f['prototypes']]
            self.assertEqual(1,len(features))
            self.assertEqual(1,features[0]['count']['min'])
            self.assertIn(prototype,profiles[profile_id]['construction'])
            self.assertEqual(direction,profiles[profile_id]['passage']['direction'])
            self.assertEqual(1,profiles[profile_id]['passage']['maxWidth'])
            seams=[edge for key,edge in self.edges.items() if key.startswith(owner+'::')
                   and edge['type']=='shared_boundary' and edge['parameters']['interface']==profile_id]
            self.assertEqual(1,len(seams))
            self.assertEqual({'space':owner},seams[0]['from'])
            self.assertEqual({'space':'security/brig'},seams[0]['to'])
        locker_parts=[part for feature in node['features'] if feature.get('assembly')
                      for part in self.assemblies[feature['assembly']]['parts']
                      if any(proto.startswith('LockerPrisoner') for proto in part['prototypes'])]
        self.assertGreaterEqual(len(locker_parts),2,'Repeated lockers belong to the local sentencing inventory.')

    def test_body_storage_clearance_uses_rotated_native_offset_for_every_machine(self):
        for owner,fid in [('medical/morgue','morgue_trays'),('service/chapel/crematorium','crematorium')]:
            params=self.rule(owner,'x_native_loading_access')['parameters']
            self.assertIn(fid,params['featureIds'])
            self.assertEqual(params['nativeComponent'],'EntityStorage')
            self.assertEqual(params['offsetField'],'enteringOffset')
            self.assertTrue(params['allInstances'])
            self.assertTrue(params['useActualRotation'])
            self.assertTrue(params['requireUnobstructedOpenAndEject'])

    def test_pa_has_all_native_parts_and_separate_apc_mv_inputs(self):
        owner='engineering/particle_accelerator_control'
        params=self.rule(owner,'x_particle_accelerator_setup')['parameters']
        target=self.assemblies[params['targetAssembly']]
        self.assertEqual(len(target['parts']),7)
        self.assertEqual(set(params['partFeatures']),{p['id'] for p in target['parts']})
        self.assertEqual(len(set(params['partFeatures'].values())),7)
        for fid in params['partFeatures'].values():self.feature(owner,fid)
        self.assertEqual(params['initialState'],'player_setup')
        self.assertTrue(params['reserveCompleteFootprint'])
        self.assertNotIn('assembly',self.feature(owner,'pa_control'),'Target footprint must not duplicate the seven declared part roles.')
        for edge,medium in [('pa_control_supply','Apc'),('pa_drive_supply','MVPower')]:
            r=self.edges[owner+'::'+edge]
            self.assertEqual(r['parameters']['flow'],'reverse')
            self.assertEqual(r['parameters']['medium'],medium)

    def test_ame_target_contains_a_buildable_core_and_reuses_inventory(self):
        owner='engineering/ame';params=self.rule(owner,'x_ame_setup')['parameters']
        target=self.assemblies[params['targetAssembly']]
        shield={(p['x'],p['y']) for p in target['parts'] if 'AmeShielding' in p['prototypes']}
        self.assertEqual(len(shield),9)
        self.assertTrue(any(all((x+dx,y+dy) in shield for dx in [-1,0,1] for dy in [-1,0,1]) for x,y in shield))
        self.assertEqual(params['nativeHvNode'],'input')
        self.assertEqual(params['initialState'],'player_setup')
        self.feature(owner,params['shieldingKitFeature']);self.feature(owner,params['fuelKitFeature'])
        self.assertNotIn('assembly',self.feature(owner,params['controllerFeature']))

    def test_containment_collector_requirements_follow_generator_variant(self):
        owner='engineering/containment_engine';params=self.rule(owner,'x_containment_setup')['parameters']
        variants={v['generatorPrototype']:v for v in params['variants']}
        self.assertEqual(set(variants),set(self.feature(owner,params['generatorFeature'])['prototypes']))
        self.assertNotEqual(variants['TeslaGenerator']['collectorFeature'],variants['SingularityGenerator']['collectorFeature'])
        self.assertIsNone(variants['SingularityGenerator']['groundingFeature'])
        for variant in variants.values():
            collector=self.feature(owner,variant['collectorFeature'])
            self.assertEqual(collector['count']['min'],0,'Unused variant collectors must stay optional.')
            self.assertGreaterEqual(collector['count']['max'],variant['minimumCollectorsPerGenerator'])
        self.assertTrue(params['requireEmitterBoltLanes'])
        self.assertTrue(params['requireNativeFieldLinks'])
        self.assertEqual(self.edges[owner+'::field_emitter_supply']['parameters']['medium'],'MVPower')

    def test_upload_operator_has_native_credentials_and_a_local_approach(self):
        owner='silicon/ai_upload';params=self.rule(owner,'x_ai_upload_operation')['parameters']
        actor=next(a for a in self.doc['accessProfiles'] if a['id']==params['operatorAccessProfile'])
        self.assertIn('ResearchDirector',actor['levels'])
        edge=self.edges[owner+'::upload_operator_approach']
        self.assertEqual(edge['parameters']['accessProfile'],actor['id'])
        self.assertIn('silicon/ai_core',edge['parameters']['avoid'])
        self.assertEqual(params['targetComponent'],'StationAiHeld')

    def test_optional_telepad_owns_console_demand_without_exclusive_pairing(self):
        owner='cargo/loading_bay';edge=self.edges[owner+'::telepad_approving_console']
        self.assertEqual(edge['from'],{'feature':owner+'#receiving_pad'})
        self.assertEqual(edge['parameters']['consoleEndpoint'],'to')
        self.assertTrue(edge['parameters']['allowSharedPad'])
        self.assertNotEqual(edge['matching']['targets'],'distinct')
        doc=copy.deepcopy(self.doc);node=dict(walk(doc['spaces']))[owner]
        next(f for f in node['features'] if f['id']=='receiving_pad')['count']={'min':0,'preferred':0,'max':0}
        self.assertEqual(V.validate_document(doc),[],'Existing orders/shuttle facilities must remain valid without a telepad.')

    def test_disposal_intake_contains_a_same_tile_native_entry_and_named_terminal(self):
        owner='engineering/disposals';unit=self.feature(owner,'disposal_unit')
        assembly=self.assemblies[unit['assembly']]
        self.assertEqual({tuple([p['x'],p['y']]) for p in assembly['parts']},{(0,0)})
        self.assertEqual({x for p in assembly['parts'] for x in p['prototypes']},{'DisposalUnit','DisposalTrunk'})
        params=self.rule(owner,'x_disposal_local_transfer')['parameters']
        edge=self.edges[owner+'::'+params['routeRelationship']]
        self.assertEqual(edge['to'],{'feature':owner+'#'+params['terminalFeature']})
        self.assertTrue(params['rejectUnintendedTermination'])
        self.assertTrue(params['requireActualInitialPayloadTags'])

    def test_thermal_button_fans_out_within_its_suite_and_injector_has_a_feed(self):
        edge=self.edges['engineering/teg::ignite_burn_chamber']
        self.assertEqual(edge['matching']['targets'],'all')
        self.assertEqual(edge['matching']['scope'],'same_parent')
        self.assertEqual((edge['parameters']['sourcePort'],edge['parameters']['targetPort']),('Pressed','Trigger'))
        self.assertEqual(edge['to'],{'feature':'engineering/teg/burn_chamber#ignition'})
        params=self.rule('engineering/teg/generator_room','x_local_control_access')['parameters']
        self.assertIn('../burn_chamber',params['forbiddenApproachSpaces'])
        self.assertEqual(params['requiredEnvironment'],'habitable')
        supply=self.edges['engineering/teg/burn_chamber::injector_requires_fuel_header']
        self.assertEqual(supply['parameters']['flow'],'reverse')
        self.assertEqual(supply['parameters']['fromPort'],'pipe')


if __name__=='__main__':unittest.main()
