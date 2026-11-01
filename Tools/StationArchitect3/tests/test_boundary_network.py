"""Boundary harness connectivity uses physical corridor floor and clear width."""
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT))
from architect.boundary_network import join_boundary_patches, _halo
from architect.geometry import rect, outline, components, square_anchors
from architect.packing import rebuild_walls
from architect.state import InstanceProgram, LayoutState
from architect.verify import Verifier


def scene(with_doors=True):
    p=InstanceProgram()
    p.definitions={'environments':{'normal':{'pressure':'standard','enclosure':'sealed'}},
                   'accessProfiles':{'worker':{'levels':['Maintenance']}},
                   'interfaces':{'door':{'capabilities':['crew_passage'],'access':{'anyOf':[['Maintenance']]}}}}
    for key in ('owned','network'):
        p.spaces[key]={'id':key,'parent':None,'owner':None,'kind':'room','children':[],
                       'node':{'environment':'normal'},'requirements':[],'harness':key=='network'}
    s=LayoutState(p,floors={'owned':rect(0,0,4,5),'network':rect(-4,1,3,2)|rect(5,1,3,2)})
    if with_doors:
        s.interfaces=[{'id':'west','a':'owned','b':'network','profile':'door','cells':{(-1,1),(-1,2)},'normal':(-1,0)},
                      {'id':'east','a':'owned','b':'network','profile':'door','cells':{(4,1),(4,2)},'normal':(1,0)}]
    rebuild_walls(s)
    return s


def hull_scene():
    s=scene()
    s.program.definitions['domains']={'exterior_space':{'pressure':'vacuum'}}
    s.floors['owned']=rect(0,0,4,13)
    s.interfaces.append({'id':'space-exit','a':'owned','b':'@domain/exterior_space',
                         'profile':'door','cells':{(1,-1)},'normal':(0,-1)})
    s.reservations.append({'id':'short-approach','owner':'owned','kind':'exterior',
                           'cells':{(1,-2),(1,-3),(1,-4)}})
    s.program.spaces['owned']['requirements']=[{'id':'eva-exit','type':'perimeter',
        'parameters':{'interface':'door','domain':'exterior_space','minLength':1}}]
    rebuild_walls(s)
    return s


class BoundaryNetworkTests(unittest.TestCase):
    def test_join_is_real_two_wide_floor_around_foreign_room_and_preserves_doors(self):
        s=scene();owned=set(s.floors['owned']);original=set(s.floors['network'])
        self.assertTrue(all(Verifier(s).interface_valid(i) for i in s.interfaces))
        result=join_boundary_patches(s,'network',2)
        self.assertEqual('connected',result['status'],result)
        self.assertGreater(result['addedTiles'],0)
        self.assertEqual(owned,s.floors['owned'])
        self.assertFalse((s.floors['network']-original)&_halo(owned))
        self.assertEqual(1,len(components(square_anchors(s.floors['network'],2))))
        self.assertTrue(all(Verifier(s).interface_valid(i) for i in s.interfaces))
        self.assertEqual(2,len(s.interfaces))

    def test_reservation_and_structural_halo_remain_clear(self):
        s=scene();reservation=rect(-5,7,14,3)
        s.reservations=[{'id':'shuttle','owner':'owned','kind':'docking','cells':reservation}]
        original=set(s.floors['network'])
        result=join_boundary_patches(s,'network',2)
        self.assertEqual('connected',result['status'],result)
        self.assertFalse((s.floors['network']-original)&_halo(reservation))
        self.assertFalse(s.walls&reservation)

    def test_landing_can_turn_sideways_beside_its_existing_clear_approach(self):
        s=scene(False)
        s.program.spaces['other']={**s.program.spaces['owned'],'id':'other'}
        s.floors={'owned':rect(0,0,3,4),'other':rect(0,9,3,4),
                  'network':rect(0,5,2,3)|rect(8,5,2,3)}
        s.interfaces=[{'id':'entry','a':'owned','b':'network','profile':'door',
                       'cells':{(0,4),(1,4)},'normal':(0,1)}]
        rebuild_walls(s);original=set(s.floors['network']);owned=set(s.floors['owned'])
        result=join_boundary_patches(s,'network')
        self.assertEqual('connected',result['status'],result)
        self.assertTrue(original<=s.floors['network'])
        self.assertEqual(owned,s.floors['owned'])
        self.assertTrue(Verifier(s).interface_valid(s.interfaces[0]))

    def test_join_preserves_true_exterior_instead_of_enclosing_finite_airlock_patch(self):
        s=hull_scene();owned=set(s.floors['owned'])
        rule=s.program.spaces['owned']['requirements'][0]
        self.assertEqual('satisfied',Verifier(s).node_requirement('owned',rule)['status'])
        result=join_boundary_patches(s,'network')
        self.assertEqual('connected',result['status'],result)
        checker=Verifier(s)
        self.assertEqual('satisfied',checker.node_requirement('owned',rule)['status'])
        self.assertIn((1,-2),checker.exterior)
        self.assertEqual(owned,s.floors['owned'])
        witness=next(r for r in s.reservations if r['id']=='vacuum-witness:space-exit')
        self.assertGreater(len(witness['cells']),3)
        self.assertFalse(witness['cells']&(checker.all_floor|s.walls))
        self.assertEqual(1,len(components(square_anchors(s.floors['network'],2))))

    def test_enclosed_hull_candidate_is_rejected_atomically_by_independent_postcondition(self):
        s=hull_scene();before=s.clone()
        # Simulate an optimizer which protects only the finite landing patch.
        # Independent hull verification must still prevent a false commit.
        with patch('architect.boundary_network._exterior_witnesses',return_value=(set(),[])):
            result=join_boundary_patches(s,'network')
        self.assertEqual('failed',result['status'],result)
        self.assertIn('enclose',result['reason'])
        self.assertEqual(before.floors,s.floors);self.assertEqual(before.walls,s.walls)
        self.assertEqual(before.reservations,s.reservations)

    def test_unreachable_patch_failure_is_atomic_and_explicit(self):
        s=scene(False);s.floors['owned']=set();s.floors['network']=rect(0,0,2,2)|rect(8,0,2,2)
        rebuild_walls(s);s.walls|=outline(rect(-1,-1,4,4))
        before=s.clone();result=join_boundary_patches(s,'network',2)
        self.assertEqual('failed',result['status'],result)
        self.assertEqual(before.floors,s.floors);self.assertEqual(before.walls,s.walls)
        self.assertEqual('failed',s.metadata['boundary_networks']['network']['status'])

    def test_one_tile_opening_does_not_satisfy_two_wide_network(self):
        s=scene(False);s.floors['owned']=set();s.floors['network']=rect(0,0,2,2)|rect(8,0,2,2)
        rebuild_walls(s);s.walls|=outline(rect(-1,-1,4,4));s.walls.discard((3,0))
        self.assertEqual('failed',join_boundary_patches(s,'network',2)['status'])
        self.assertEqual('connected',join_boundary_patches(s,'network',1)['status'])

    def test_zero_budget_preserves_geometry(self):
        s=scene();before=s.clone()
        result=join_boundary_patches(s,'network',maximum_work=0)
        self.assertEqual('budget_exhausted',result['status'])
        self.assertEqual(before.floors,s.floors);self.assertEqual(before.walls,s.walls)
        self.assertEqual('budget_exhausted',join_boundary_patches(s,'network',maximum_cells=1)['status'])

    def test_owned_departments_cannot_be_expanded_as_harness_floor(self):
        s=scene();before=s.clone()
        self.assertEqual('failed',join_boundary_patches(s,'owned')['status'])
        self.assertEqual(before.floors,s.floors)

    def test_connected_network_is_idempotent(self):
        s=scene();join_boundary_patches(s,'network')
        before=s.clone();result=join_boundary_patches(s,'network')
        self.assertEqual('connected',result['status'])
        self.assertEqual(0,result['addedTiles'])
        self.assertEqual(before.floors,s.floors);self.assertEqual(before.walls,s.walls)


if __name__=='__main__':unittest.main()
