"""Inventory is actual native-derived geometry, not decorative one-tile icons."""
import copy
import math
from pathlib import Path
import sys
import unittest

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT))
from validate import load_program
from architect.fixtures import (describe_feature, estimate_room, place_features,
                                assess_features, prototype_descriptor, _pose, _interface_approaches)
from architect.geometry import rect, outline
from architect.instances import resolve_instances
from architect.state import InstanceProgram, LayoutState


def example(prototypes=('GravityGenerator',), working=2, size=(6,6)):
    p=InstanceProgram()
    p.spaces['room']={'id':'room','parent':None,'owner':None,'kind':'room','children':[],
                      'node':{'placement':{'grid':'station','nesting':'owned'}},'requirements':[]}
    p.definitions={'assemblies':{},'interfaces':{}}
    for i,proto in enumerate(prototypes):
        key=f'room#feature[{i}]'
        p.features[key]={'id':key,'room':'room','prototype':proto,
                         'node':{'prototypes':[proto] if proto else [],
                                 'clearance':{'workingTiles':working,'approachWidth':1}}}
    floor=rect(0,0,*size)
    return LayoutState(p,floors={'room':floor},walls=outline(floor),bounds=(-3,-3,size[0]+3,size[1]+3))


class NativeFixtureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.program=resolve_instances(load_program(ROOT/'catalog/standard-station.yaml'))

    def test_real_large_machine_and_circle_inheritance(self):
        gravity=prototype_descriptor('GravityGenerator')
        self.assertEqual(rect(-1,-1,3,3),gravity['blocking_cells'])
        self.assertEqual({(0,0)},prototype_descriptor('BorgCharger')['cells'])
        self.assertEqual({(0,0)},prototype_descriptor('SolarTracker')['cells'])

    def test_collision_layers_distinguish_floor_and_underfloor(self):
        self.assertFalse(prototype_descriptor('HydroponicsTray')['blocking_cells'])
        self.assertTrue(prototype_descriptor('SolarPanel')['blocking_cells'])
        self.assertEqual('underfloor',prototype_descriptor('GasVolumePump')['layer'])
        self.assertFalse(prototype_descriptor('GasVolumePump')['blocking_cells'])
        self.assertEqual('wall',prototype_descriptor('BrigTimer')['layer'])
        self.assertEqual('portable',prototype_descriptor('CryoxadoneBeakerSmall')['layer'])

    def test_every_selected_catalog_feature_has_a_spatial_descriptor(self):
        for key in self.program.features:
            with self.subTest(feature=key):
                d=describe_feature(self.program,key)
                self.assertTrue(d['known'])
                self.assertTrue(d['cells'] or d['layer']=='abstract')
                self.assertTrue(d['fingerprint'])

    def test_catalog_alternative_prototypes_and_assemblies_are_indexed(self):
        alternatives={proto for f in self.program.features.values() for proto in f['node']['prototypes']}
        alternatives|={proto for assembly in self.program.definitions['assemblies'].values()
                       for part in assembly['parts'] for proto in part['prototypes']}
        for proto in sorted(alternatives):
            with self.subTest(prototype=proto):
                self.assertTrue(prototype_descriptor(proto)['source'])

    def test_selected_unplaced_machine_fails(self):
        state=example()
        check=assess_features(state)[0]
        self.assertEqual('violated',check['status'])
        self.assertIn('not placed',check['message'])

    def test_large_machine_placement_has_real_size_and_access(self):
        state=example(size=(5,5))
        checks=place_features(state,'room',seed=3)
        self.assertTrue(all(c['status']=='satisfied' for c in checks),checks)
        pose=state.features['room#feature[0]']
        self.assertEqual(9,len(pose['blocking_cells']))
        self.assertEqual(2,len(pose['clearance']))
        self.assertFalse(pose['clearance'] & pose['blocking_cells'])

    def test_saved_collision_mask_cannot_hide_machine(self):
        state=example()
        place_features(state,'room')
        state.features['room#feature[0]']['blocking_cells']=set()
        check=assess_features(state)[0]
        self.assertEqual('violated',check['status'])
        self.assertIn('descriptor',check['message'])

    def test_authored_approach_width_cannot_shrink_to_working_tile_count(self):
        state=example(('SMESBasic',),working=1)
        state.program.features['room#feature[0]']['node']['clearance']['approachWidth']=2
        checks=place_features(state,'room')
        self.assertTrue(all(c['status']=='satisfied' for c in checks),checks)
        self.assertEqual(2,len(state.features['room#feature[0]']['clearance']))

    def test_native_loading_offset_rotates_with_storage(self):
        state=example(('Morgue',),working=1)
        d=describe_feature(state.program,'room#feature[0]')
        # South-facing native loading offset is (0,-1); at 90 degrees it is east.
        pose=_pose(d,(2,2),90,{(3,2)})
        state.features['room#feature[0]']=pose
        self.assertEqual('satisfied',assess_features(state)[0]['status'])
        pose['clearance']={(1,2)}
        self.assertEqual('violated',assess_features(state)[0]['status'])

    def test_exact_assembly_parts_rotate_together(self):
        p=copy.deepcopy(self.program)
        key=next(k for k,f in p.features.items() if f['node'].get('assembly')=='teg_native')
        room=p.features[key]['room'];p.features={key:p.features[key]}
        d=describe_feature(p,key)
        state=LayoutState(p,floors={room:rect(0,0,8,8)})
        pose=_pose(d,(3,3),90,{(2,3)})
        state.features[key]=pose
        self.assertEqual({(3,2),(3,3),(3,4)},pose['cells'])
        self.assertEqual('satisfied',assess_features(state,rooms={room})[0]['status'])
        pose['prototype_parts'][1]['x']+=1
        self.assertEqual('violated',assess_features(state,rooms={room})[0]['status'])

    def test_subfloor_and_surface_can_share_a_tile(self):
        state=example(('SMESBasic','GasPassiveVent'),working=0)
        for key in state.program.features:
            d=describe_feature(state.program,key)
            state.features[key]=_pose(d,(2,2),0,set())
        self.assertTrue(all(c['status']=='satisfied' for c in assess_features(state)))
        # Another surface machine cannot use that same floor cell.
        state.program.features['room#feature[1]']['prototype']='SMESBasic'
        state.program.features['room#feature[1]']['node']['prototypes']=['SMESBasic']
        d=describe_feature(state.program,'room#feature[1]')
        state.features['room#feature[1]']=_pose(d,(2,2),0,set())
        self.assertTrue(any(c['status']=='violated' for c in assess_features(state)))

    def test_wallmount_uses_structure_and_owner_working_floor(self):
        state=example(('BrigTimer',),working=1)
        checks=place_features(state,'room')
        self.assertTrue(all(c['status']=='satisfied' for c in checks),checks)
        pose=state.features['room#feature[0]']
        self.assertTrue(pose['cells']<=state.walls)
        self.assertFalse(pose['blocking_cells'])
        self.assertTrue(pose['clearance']<=state.floors['room'])

    def test_door_binds_interface_instead_of_blocking_room_floor(self):
        state=example(('AirlockBrigGlassLocked',),working=1)
        state.program.definitions['interfaces']['door']={'capabilities':['crew_passage']}
        state.interfaces=[{'id':'door1','a':'room','b':'hall','profile':'door',
                           'cells':{(2,-1),(3,-1)},'normal':(0,-1)}]
        state.walls-={(2,-1),(3,-1)}
        checks=place_features(state,'room')
        self.assertTrue(all(c['status']=='satisfied' for c in checks),checks)
        pose=state.features['room#feature[0]']
        self.assertEqual('door1',pose['interface'])
        self.assertFalse(pose['blocking_cells'])

    def test_abstract_contract_cannot_spawn_hidden_equipment(self):
        state=example((None,),working=0)
        self.assertEqual('satisfied',assess_features(state)[0]['status'])
        state.features['room#feature[0]']={'room':'room','cells':{(0,0)}}
        self.assertEqual('violated',assess_features(state)[0]['status'])

    def test_dense_solar_inventory_is_not_omitted(self):
        p=copy.deepcopy(self.program)
        room=next(i for i,n in p.spaces.items() if n['template']=='maintenance_rooms/solar_array')
        p.features={k:f for k,f in p.features.items() if f['room']==room}
        plan=estimate_room(p,room)
        self.assertGreaterEqual(plan['minimum_area'],47)
        side=math.ceil(math.sqrt(plan['recommended_area']))
        floor=rect(0,0,side,side)
        state=LayoutState(p,floors={room:floor},walls=outline(floor))
        checks=place_features(state,room,seed=11)
        self.assertTrue(all(c['status']=='satisfied' for c in checks),checks)
        self.assertEqual(plan['concrete_count'],len(state.features))

    def test_exposed_lattice_edges_do_not_become_eight_crew_doors(self):
        p=copy.deepcopy(self.program)
        room=next(i for i,n in p.spaces.items() if n['template']=='maintenance_rooms/solar_array')
        p.features={k:f for k,f in p.features.items() if f['room']==room}
        floor=rect(0,0,9,9)
        state=LayoutState(p,floors={room:floor},walls=outline(floor))
        state.interfaces=[{'id':f'edge{i}','a':room,'b':'@domain/exterior_space','profile':'exposed_lattice',
                           'cells':{(9,i)},'normal':(1,0)} for i in range(8)]
        state.walls-={(9,i) for i in range(8)}
        self.assertFalse(_interface_approaches(state,room))
        checks=place_features(state,room,seed=1)
        self.assertTrue(all(c['status']=='satisfied' for c in checks),checks)
        self.assertEqual(46,len(state.features))

    def test_interaction_counter_keeps_actual_operating_approach(self):
        state=example(('SMESBasic',),working=1)
        state.program.definitions['interfaces']['counter']={'capabilities':['item_transfer','speech']}
        state.interfaces=[{'id':'counter','a':'room','b':'hall','profile':'counter',
                           'cells':{(2,-1),(3,-1)},'normal':(0,-1)}]
        required=_interface_approaches(state,'room')
        self.assertTrue({(2,0),(3,0)}<=required)
        checks=place_features(state,'room')
        self.assertTrue(all(c['status']=='satisfied' for c in checks),checks)
        self.assertFalse(required & state.features['room#feature[0]']['blocking_cells'])


if __name__=='__main__':
    unittest.main()
