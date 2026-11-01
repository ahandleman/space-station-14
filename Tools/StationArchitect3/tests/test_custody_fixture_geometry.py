"""Native turnstile placement binds both prototype and cardinal travel direction."""
import copy
from pathlib import Path
import sys
import unittest

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT))
from architect.fixtures import assess_features,describe_feature,place_features,prototype_descriptor,_pose,rotate
from architect.department_rules import spatial_rule_checks
from architect.geometry import rect
from architect.packing import rebuild_walls
from architect.state import InstanceProgram,LayoutState


def gates_scene(turn=0):
    program=InstanceProgram()
    program.definitions={'assemblies':{},'interfaces':{},'environments':{'normal':{'pressure':'standard','enclosure':'sealed'}}}
    for room in ('sentencing','brig'):
        program.spaces[room]={'id':room,'kind':'room','parent':None,'owner':None,'children':[],
                              'requirements':[],'node':{'environment':'normal'}}
    state=LayoutState(program,floors={'sentencing':rect(0,0,5,5),'brig':rect(6,0,5,5)})
    for role,proto,direction,y in [('entry','EnergyGateGenpopEnter','a_to_b',1),
                                   ('exit','EnergyGateGenpopLeave','b_to_a',3)]:
        program.definitions['interfaces'][role]={'capabilities':['crew_passage'],'construction':[proto],
            'access':{'anyOf':[['Security']]},'passage':{'direction':direction,'minWidth':1,'maxWidth':1}}
        state.interfaces.append({'id':role,'a':'sentencing','b':'brig','cells':{(5,y)},'normal':(1,0),'profile':role})
        key='sentencing#'+role
        program.features[key]={'id':key,'room':'sentencing','prototype':proto,
            'node':{'prototypes':[proto],'clearance':{'workingTiles':0,'approachWidth':1}}}
    state.floors={key:{rotate(c,turn) for c in cells} for key,cells in state.floors.items()}
    for interface in state.interfaces:
        interface['cells']={rotate(c,turn) for c in interface['cells']}
        interface['normal']=rotate(interface['normal'],turn)
    rebuild_walls(state)
    return state


def custody_scene():
    state=gates_scene();p=state.program
    p.definitions['accessProfiles']={'staff':{'levels':['Security']},
        'admission':{'levels':['GenpopEnter']},'release':{'levels':['GenpopLeave']}}
    p.definitions['interfaces']['entry']['access']['anyOf']=[['GenpopEnter'],['Security']]
    p.definitions['interfaces']['exit']['access']['anyOf']=[['GenpopLeave'],['Security']]
    p.definitions['assemblies']['lockers']={'parts':[{'id':'locker'+str(i),
        'prototypes':['LockerPrisoner'+(str(i+1) if i else '')],'x':i,'y':0,'rotation':0}
        for i in range(4)]}
    p.features['sentencing#lockers']={'id':'sentencing#lockers','room':'sentencing','prototype':None,
        'node':{'id':'lockers','prototypes':[],'assembly':'lockers','clearance':{'workingTiles':4,'approachWidth':4}}}
    for key,feature in p.features.items():feature['node']['id']=key.partition('#')[2]
    p.spaces['sentencing']['node']['features']=[f['node'] for f in p.features.values()]
    p.spaces['sentencing']['requirements']=[{'id':'custody','type':'x_genpop_custody','owner':'sentencing',
        'priority':'mandatory','parameters':{'lockerFeatureIds':['lockers'],'entryGateFeature':'entry',
            'exitGateFeature':'exit','detentionRoom':'brig','operatorAccessProfile':'staff',
            'admissionAccessProfile':'admission','releaseAccessProfile':'release','cardPrototype':'PrisonerIDCard'}}]
    place_features(state,'sentencing')
    return state


class CustodyFixtureGeometry(unittest.TestCase):
    def test_native_turnstiles_are_interface_geometry_with_real_one_tile_footprint(self):
        for prototype in ('EnergyGateGenpopEnter','EnergyGateGenpopLeave'):
            descriptor=prototype_descriptor(prototype)
            self.assertEqual('interface',descriptor['layer'])
            self.assertEqual({(0,0)},descriptor['cells'])
            self.assertEqual(set(),descriptor['blocking_cells'])
            self.assertTrue(descriptor['interface_binding']['requireConstructionPrototype'])
            self.assertTrue(descriptor['interface_binding']['requireDirectedPassage'])

    def test_entry_and_exit_bind_their_own_lane_at_every_quarter_turn(self):
        for turn in (0,90,180,270):
            with self.subTest(turn=turn):
                state=gates_scene(turn)
                self.assertTrue(all(c['status']=='satisfied' for c in place_features(state,'sentencing')))
                for role,expected in [('entry',90),('exit',270)]:
                    pose=state.features['sentencing#'+role]
                    self.assertEqual(role,pose['interface'])
                    self.assertEqual((expected+turn)%360,pose['rotation'])
                    self.assertEqual(set(),pose['blocking_cells'])

    def test_wrong_construction_or_undirected_profile_cannot_accept_a_gate(self):
        for change in ('construction','direction'):
            state=gates_scene()
            if change=='construction':state.program.definitions['interfaces']['entry']['construction']=['AirlockSecurityLocked']
            else:state.program.definitions['interfaces']['entry']['passage']['direction']='bidirectional'
            checks=place_features(state,'sentencing')
            self.assertNotIn('sentencing#entry',state.features)
            self.assertTrue(any(c['status']=='violated' and c['id']=='fixture:sentencing#entry' for c in checks))

    def test_self_consistent_saved_pose_cannot_reverse_the_actual_native_gate(self):
        state=gates_scene();place_features(state,'sentencing')
        key='sentencing#entry';pose=copy.deepcopy(state.features[key])
        state.features[key]=_pose(describe_feature(state.program,key),pose['origin'],
                                   (pose['rotation']+180)%360,pose['clearance'],pose['interface'])
        result=next(c for c in assess_features(state) if c['id']=='fixture:'+key)
        self.assertEqual('violated',result['status'])
        self.assertIn('directional passage',result['message'])

    def test_custody_spatial_facet_checks_each_locker_but_does_not_certify_native_timing(self):
        state=custody_scene();result=spatial_rule_checks(state)
        self.assertEqual([],result['completed_rule_ids'])
        check=result['partial_checks'][0]
        self.assertEqual('custody::spatial',check['id'])
        self.assertEqual('satisfied',check['status'],check)
        self.assertEqual(4,check['measured']['lockerParts'])
        self.assertEqual('not_evaluated',check['measured']['nativeIdPairingExpiryAndPropertyRetrieval'])
        self.assertIsNone(check['measured']['phaseRouteLengths']['unserved_escape'])
        self.assertIsNone(check['measured']['phaseRouteLengths']['released_reentry'])

    def test_custody_cannot_pass_with_omitted_lockers_or_unserved_gate_access(self):
        state=custody_scene();state.features.pop('sentencing#lockers')
        self.assertEqual('violated',spatial_rule_checks(state)['partial_checks'][0]['status'])
        state=custody_scene()
        state.program.definitions['interfaces']['exit']['access']['anyOf'].append(['GenpopEnter'])
        check=spatial_rule_checks(state)['partial_checks'][0]
        self.assertEqual('violated',check['status'])
        self.assertIn('unserved_escape',check['message'])

    def test_custody_fixture_rebinding_and_per_part_approaches_are_independently_checked(self):
        state=custody_scene();pose=state.features['sentencing#entry']
        pose['interface']='exit'
        self.assertEqual('violated',spatial_rule_checks(state)['partial_checks'][0]['status'])
        state=custody_scene();state.features['sentencing#lockers']['clearance']=set()
        check=spatial_rule_checks(state)['partial_checks'][0]
        self.assertEqual('violated',check['status'])
        self.assertIn('locker part lacks',check['message'])


if __name__=='__main__':unittest.main()
