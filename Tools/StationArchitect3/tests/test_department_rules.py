"""Local spatial adapters certify their exact facet, never machine operation."""
from pathlib import Path
import copy
import sys
import unittest

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT))
from architect.department_rules import spatial_rule_checks
from architect.fixtures import describe_feature, _pose
from architect.geometry import rect, outline
from architect.state import InstanceProgram, LayoutState


def room(program,key,environment='normal'):
    program.spaces[key]={'id':key,'template':key,'parent':None,'owner':None,
                        'kind':'room','children':[],'requirements':[],
                        'node':{'environment':environment,'features':[],
                                'placement':{'grid':'station','nesting':'owned'}}}


def scene():
    p=InstanceProgram()
    p.definitions={'assemblies':{},'accessProfiles':{'staff':{'levels':['Medical']},'public':{'levels':[]}},
                   'environments':{'normal':{'pressure':'standard'},'space':{'pressure':'vacuum'}},
                   'interfaces':{
                       'morgue_door':{'capabilities':['crew_passage'],'access':{'anyOf':[['Medical']]},'airtight':True},
                       'public_door':{'capabilities':['crew_passage'],'access':{'anyOf':[]},'airtight':True}}}
    room(p,'room');room(p,'hall')
    floors={'room':rect(0,0,6,6),'hall':rect(0,-4,6,3)}
    walls=outline(set().union(*floors.values()))
    return LayoutState(p,floors=floors,walls=walls,bounds=(-3,-6,10,10))


def door(state,key='entry',profile='morgue_door',a='room',b='hall',cells=None,normal=(0,-1)):
    cells={(1,-1)} if cells is None else cells
    state.walls-=cells
    state.interfaces.append({'id':key,'a':a,'b':b,'profile':profile,'cells':cells,'normal':normal})


def feature(state,local='machine',prototype='Morgue',origin=(3,3),rotation=0,clearance=None,owner='room'):
    key=owner+'#'+local
    node={'id':local,'prototypes':[prototype],'clearance':{'workingTiles':1,'approachWidth':1}}
    state.program.spaces[owner]['node']['features'].append(node)
    state.program.features[key]={'id':key,'room':owner,'prototype':prototype,'node':node}
    d=describe_feature(state.program,key)
    state.features[key]=_pose(d,origin,rotation,{(origin[0],origin[1]-1)} if clearance is None else clearance)
    return key


def rule(state,kind,parameters,owner='room',key='local',**extra):
    value={'id':owner+'@'+key,'authoredId':key,'owner':owner,'type':kind,'priority':'mandatory',
           'parameters':parameters,**extra}
    state.program.spaces[owner]['requirements'].append(value)
    return value


def checks(state):
    return spatial_rule_checks(state)['checks']


def eva_scene(width=1):
    s=scene()
    s.program.definitions['accessProfiles']['staff']['canTraverseVacuum']=True
    s.program.definitions['domains']={'exterior_space':{'kind':'vacuum','connectedToExterior':True}}
    s.floors={'room':rect(0,0,3,3),'hall':rect(9,0,3,3)}
    s.walls=outline(set().union(*s.floors.values()))
    s.interfaces=[];s.bounds=(-4,-4,16,7)
    door(s,'outside_a',a='room',b='@domain/exterior_space',cells={(3,1)},normal=(1,0))
    door(s,'outside_b',a='hall',b='@domain/exterior_space',cells={(8,1)},normal=(-1,0))
    r={'id':'room@eva','owner':'room','type':'route','priority':'mandatory','source':'room','target':'hall',
       'parameters':{'mode':'eva','accessProfile':'staff','minWidth':width,'within':'station','through':[],'avoid':[]}}
    s.program.relationships=[r]
    return s,r


class LocalSpatialRuleTests(unittest.TestCase):
    def test_eva_partial_route_uses_exterior_airlocks_and_preserves_runtime_unknown(self):
        s,r=eva_scene();result=spatial_rule_checks(s)
        self.assertEqual([],result['completed_rule_ids'])
        c=result['partial_checks'][0]
        self.assertEqual(r['id']+'::spatial',c['id'])
        self.assertEqual('satisfied',c['status'])
        self.assertGreater(c['measured']['exteriorTiles'],0)
        self.assertEqual(['outside_a','outside_b'],c['measured']['authorizedExteriorInterfaces'])
        self.assertEqual('not_evaluated',c['measured']['runtimeSurvival'])

    def test_eva_requires_vacuum_capability_and_actual_door_credentials(self):
        s,r=eva_scene()
        s.program.definitions['accessProfiles']['staff']['canTraverseVacuum']=False
        self.assertEqual('violated',spatial_rule_checks(s)['partial_checks'][0]['status'])
        s.program.definitions['accessProfiles']['staff']['canTraverseVacuum']=True
        s.program.definitions['accessProfiles']['staff']['levels']=[]
        self.assertEqual('violated',spatial_rule_checks(s)['partial_checks'][0]['status'])

    def test_eva_declared_maximum_length_is_binding(self):
        s,r=eva_scene();c=spatial_rule_checks(s)['partial_checks'][0]
        length=c['measured']['length']
        r['parameters']['maxLength']=length
        self.assertEqual('satisfied',spatial_rule_checks(s)['partial_checks'][0]['status'])
        r['parameters']['maxLength']=length-1
        self.assertEqual('violated',spatial_rule_checks(s)['partial_checks'][0]['status'])
        self.assertEqual([],spatial_rule_checks(s)['completed_rule_ids'])

    def test_eva_direction_is_preserved_with_and_without_waypoints(self):
        s,r=eva_scene()
        outward=copy.deepcopy(s.program.definitions['interfaces']['morgue_door'])
        inward=copy.deepcopy(outward)
        outward['passage']={'direction':'a_to_b'}
        inward['passage']={'direction':'b_to_a'}
        s.program.definitions['interfaces'].update(outward=outward,inward=inward)
        s.interfaces[0]['profile']='outward';s.interfaces[1]['profile']='inward'
        self.assertEqual('satisfied',spatial_rule_checks(s)['partial_checks'][0]['status'])
        r['source'],r['target']='hall','room'
        for through in ([],['room']):
            r['parameters']['through']=through
            self.assertEqual('violated',spatial_rule_checks(s)['partial_checks'][0]['status'])

    def test_eva_cannot_replace_a_two_wide_route_with_single_tile_airlocks(self):
        s,r=eva_scene(width=2)
        self.assertEqual('violated',spatial_rule_checks(s)['partial_checks'][0]['status'])
        for i in s.interfaces:
            x=next(iter(i['cells']))[0];i['cells']|={(x,0)};s.walls-={(x,0)}
        self.assertEqual('satisfied',spatial_rule_checks(s)['partial_checks'][0]['status'])

    def test_eva_sealed_courtyard_is_not_exterior_connected_space(self):
        s,r=eva_scene();s.walls|=outline(rect(-2,-2,16,7))
        self.assertEqual('violated',spatial_rule_checks(s)['partial_checks'][0]['status'])

    def test_eva_open_vacuum_lattice_endpoint_needs_no_fake_airlock(self):
        s,r=eva_scene();s.program.spaces['room']['node']['environment']='space'
        s.interfaces=[i for i in s.interfaces if i['a']!='room']
        s.walls-=outline(s.floors['room'])
        self.assertEqual('satisfied',spatial_rule_checks(s)['partial_checks'][0]['status'])

    def test_eva_missing_hull_or_an_unrealized_door_is_not_authorized_entry(self):
        s,r=eva_scene();s.interfaces=[i for i in s.interfaces if i['a']!='room']
        # Removing an interface leaves a geometric hole, but no declared entry.
        self.assertEqual('violated',spatial_rule_checks(s)['partial_checks'][0]['status'])
        s,r=eva_scene();s.walls.add((3,1))
        self.assertEqual('violated',spatial_rule_checks(s)['partial_checks'][0]['status'])

    def test_eva_hazardous_endpoints_are_conditional_but_other_process_spaces_are_excluded(self):
        s,r=eva_scene();s.program.definitions['environments']['process']={'pressure':'process','temperature':'hot'}
        s.program.spaces['room']['node']['environment']='process'
        c=spatial_rule_checks(s)['partial_checks'][0]
        self.assertEqual('satisfied',c['status'])
        self.assertEqual(['room'],c['measured']['conditionalProcessEndpoints'])
        room(s.program,'burn','process');s.floors['burn']=rect(4,4,3,3);s.walls|=outline(s.floors['burn'])
        r['parameters']['through']=['burn']
        c=spatial_rule_checks(s)['partial_checks'][0]
        self.assertEqual('violated',c['status'])
        self.assertEqual(['burn'],c['measured']['excludedProcessRooms'])
        s.program.definitions['environments']['process']['pressure']='standard'
        self.assertEqual('violated',spatial_rule_checks(s)['partial_checks'][0]['status'])

    def test_eva_respects_avoid_and_through_contracts(self):
        s,r=eva_scene();room(s.program,'waypoint','space');s.floors['waypoint']=rect(4,4,2,2)
        r['parameters']['through']=['waypoint']
        self.assertEqual('satisfied',spatial_rule_checks(s)['partial_checks'][0]['status'])
        r['parameters']['avoid']=['waypoint']
        self.assertEqual('violated',spatial_rule_checks(s)['partial_checks'][0]['status'])

    def test_every_entrance_must_obey_local_profile(self):
        s=scene();door(s)
        r=rule(s,'x_entry_interface_policy',{'interface':'morgue_door','allRoutineCrewEntrances':True,
                                            'preserveUpstreamCredentialChecks':True})
        self.assertEqual('satisfied',checks(s)[0]['status'])
        door(s,'bypass','public_door',cells={(4,-1)})
        result=spatial_rule_checks(s)
        self.assertEqual('violated',result['checks'][0]['status'])
        self.assertEqual(['bypass'],result['checks'][0]['measured']['wrongPolicy'])
        self.assertIn(r['id'],result['completed_rule_ids'])

    def test_native_storage_uses_actual_rotation_and_authorized_approach(self):
        s=scene();door(s)
        key=feature(s,rotation=90,clearance={(4,3)})
        r=rule(s,'x_native_loading_access',{'featureIds':['machine'],'nativeComponent':'EntityStorage',
                'offsetField':'enteringOffset','accessProfile':'staff'})
        self.assertEqual('satisfied',checks(s)[0]['status'])
        self.assertEqual([(4,3)],checks(s)[0]['measured'][key])
        s.features[key]['clearance']={(2,3)}
        self.assertEqual('violated',checks(s)[0]['status'])
        s.features[key]['clearance']={(4,3)}
        r['parameters']['accessProfile']='public'
        self.assertEqual('violated',checks(s)[0]['status'])

    def test_selected_storage_instances_cannot_be_omitted(self):
        s=scene();door(s)
        key=feature(s)
        rule(s,'x_native_loading_access',{'featureIds':['machine'],'nativeComponent':'EntityStorage',
                'offsetField':'enteringOffset','accessProfile':'staff'})
        s.features.pop(key)
        self.assertEqual('violated',checks(s)[0]['status'])

    def test_loading_cannot_seed_ingress_behind_an_outward_only_gate(self):
        s=scene();door(s);feature(s)
        rule(s,'x_native_loading_access',{'featureIds':['machine'],'nativeComponent':'EntityStorage',
                'offsetField':'enteringOffset','accessProfile':'staff'})
        s.program.definitions['interfaces']['morgue_door']['passage']={'direction':'a_to_b'}
        self.assertEqual('violated',checks(s)[0]['status'])
        s.program.definitions['interfaces']['morgue_door']['passage']['direction']='b_to_a'
        self.assertEqual('satisfied',checks(s)[0]['status'])

    def test_loading_position_cannot_overlap_other_native_collision(self):
        s=scene();door(s)
        feature(s)
        feature(s,local='obstruction',prototype='SMESBasic',origin=(3,2),clearance={(4,2)})
        rule(s,'x_native_loading_access',{'featureIds':['machine'],'nativeComponent':'EntityStorage',
                'offsetField':'enteringOffset','accessProfile':'staff'})
        self.assertEqual('violated',checks(s)[0]['status'])

    def test_control_cannot_use_only_an_entrance_from_forbidden_process_room(self):
        s=scene();door(s)
        feature(s,prototype='ComputerPowerMonitoring')
        r=rule(s,'x_local_control_access',{'controlFeatureIds':['machine'],'accessProfile':'staff',
                'requiredEnvironment':'normal','operatingSpace':'.','forbiddenApproachSpaces':['/hall']})
        self.assertEqual('violated',checks(s)[0]['status'])
        room(s.program,'process');s.floors['process']=rect(20,20,3,3)
        r['parameters']['forbiddenApproachSpaces']=['/process']
        self.assertEqual('satisfied',checks(s)[0]['status'])
        r['parameters']['requiredEnvironment']='space'
        self.assertEqual('violated',checks(s)[0]['status'])

    def test_vacuum_lattice_support_is_exposed_but_sealed_courtyard_is_not(self):
        s=scene();s.program.spaces['room']['node']['environment']='space'
        feature(s,prototype='HeatExchanger',origin=(3,3))
        s.walls=set()
        rule(s,'x_feature_exposure',{'featureIds':['machine'],'medium':'vacuum','allInstances':True,
                'sampleDirections':['north','east','south','west'],'rangeTiles':8,'minimumClearDirections':2})
        self.assertEqual('satisfied',checks(s)[0]['status'])
        s.walls=outline(s.floors['room'])
        self.assertEqual('violated',checks(s)[0]['status'])

    def test_solar_panels_do_not_falsely_shadow_each_other(self):
        s=scene();s.program.spaces['room']['node']['environment']='space';s.walls=set()
        feature(s,prototype='SolarPanel',origin=(2,2))
        feature(s,local='neighbor',prototype='SolarPanel',origin=(2,3))
        rule(s,'x_feature_exposure',{'featureIds':['machine'],'medium':'sunlight','allInstances':True,
                'sampleDirections':['north'],'rangeTiles':20,'minimumClearDirections':1})
        self.assertEqual('satisfied',checks(s)[0]['status'])
        s.walls={(2,4)}
        self.assertEqual('violated',checks(s)[0]['status'])

    def test_vacuum_gap_is_partial_and_does_not_certify_environment(self):
        s=scene();s.floors={'room':rect(0,0,3,3),'hall':rect(7,0,3,3)}
        s.walls=outline(s.floors['room'])|outline(s.floors['hall'])
        r=rule(s,'environment',{'profile':'normal','minVacuumGap':2})
        result=spatial_rule_checks(s)
        self.assertNotIn(r['id'],result['completed_rule_ids'])
        self.assertEqual(r['id']+'::spatial',result['partial_checks'][0]['id'])
        self.assertEqual('satisfied',result['partial_checks'][0]['status'])
        s.floors['hall']=rect(4,0,3,3)
        s.walls=outline(s.floors['room'])|outline(s.floors['hall'])
        self.assertEqual('violated',spatial_rule_checks(s)['partial_checks'][0]['status'])

    def test_reach_uses_bound_pair_and_native_default(self):
        s=scene();source=feature(s,prototype='BrigTimer',origin=(0,0))
        target=feature(s,local='target',prototype='AirlockBrigGlassLocked',origin=(31,0))
        nearby=feature(s,local='nearby',prototype='AirlockBrigGlassLocked',origin=(1,0))
        binding={'id':'room@binding','authoredId':'binding','owner':'room','type':'x_signal_binding',
                 'source':source,'target':target,'parameters':{}}
        reach={'id':'room@reach','owner':'room','type':'x_device_link_reach','source':source,'target':target,
               'priority':'desired','parameters':{'bindingRelationship':'binding','rangeSourceComponent':'DeviceLinkSource',
               'rangeSourceField':'range','distanceMetric':'native_map_distance'}}
        s.program.relationships=[binding,reach]
        result=spatial_rule_checks(s)
        self.assertEqual('violated',result['checks'][0]['status'])
        self.assertEqual(30,result['checks'][0]['measured']['nativeRange'])
        self.assertEqual('desired',result['checks'][0]['priority'])
        reach['target']=nearby
        self.assertEqual('violated',checks(s)[0]['status'])
        reach['target']=target;s.features[target]['origin']=(5,0)
        self.assertEqual('satisfied',checks(s)[0]['status'])
        self.assertNotIn(binding['id'],spatial_rule_checks(s)['completed_rule_ids'])
        room(s.program,'other_grid')
        s.program.spaces['other_grid']['node']['placement']['grid']='other'
        s.program.features[target]['room']='other_grid'
        self.assertEqual('unknown',checks(s)[0]['status'])

    def test_local_feature_id_does_not_consume_other_room_instances(self):
        s=scene();door(s)
        feature(s)
        feature(s,owner='hall',origin=(3,-3))
        del s.features['hall#machine']
        rule(s,'x_native_loading_access',{'featureIds':['machine'],'nativeComponent':'EntityStorage',
                'offsetField':'enteringOffset','accessProfile':'staff'})
        self.assertEqual('satisfied',checks(s)[0]['status'])

    def test_mixed_runtime_contract_is_not_reported_complete(self):
        s=scene();rule(s,'x_teg_operating_loop',{})
        result=spatial_rule_checks(s)
        self.assertEqual([],result['checks'])
        self.assertEqual([],result['completed_rule_ids'])


if __name__=='__main__':unittest.main()
