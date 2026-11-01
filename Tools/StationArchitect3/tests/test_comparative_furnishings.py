"""Previously empty amenity rooms require real, source-backed local contents."""
import json
import importlib.util
import math
from pathlib import Path
import sys
import unittest

ROOT=Path(__file__).resolve().parents[1]
REPO=ROOT.parents[1]
sys.path.insert(0,str(ROOT))
from validate import load_program
from architect.fixtures import NativeLoader, describe_feature, place_features, assess_features, native_index, _pose, _aisle
from architect.geometry import rect, outline, components
from architect.instances import resolve_instances
from architect.state import LayoutState
import yaml


class ComparativeFurnishingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.document=load_program(ROOT/'catalog/standard-station.yaml')
        cls.program=resolve_instances(cls.document,counts={'commons/concourse':2})
        cls.baseline=json.loads((ROOT/'research/comparative-furnishing-baseline.json').read_text(encoding='utf-8'))

    def test_default_distributed_commons_does_not_force_optional_plazas(self):
        normal=resolve_instances(self.document)
        self.assertFalse(any(n['template']=='commons/concourse' for n in normal.spaces.values()))
        selected=0
        for feature in normal.features.values():
            template=normal.spaces[feature['room']]['template']
            declared={f['feature'] for f in self.baseline['localRooms'].get(template,[])}
            if feature['node']['id'] in declared:selected+=len(describe_feature(normal,feature['id'])['parts'])
        self.assertEqual(43,selected)

    def test_local_baselines_select_all_mandatory_physical_instances(self):
        selected=[]
        for template,declared in self.baseline['localRooms'].items():
            rooms=[key for key,n in self.program.spaces.items() if n['template']==template]
            self.assertTrue(rooms,template)
            for room in rooms:
                for item in declared:
                    features=[f for f in self.program.features.values() if f['room']==room and f['node']['id']==item['feature']]
                    self.assertEqual(item['minimum'],len(features),(room,item['feature']))
                    for f in features:
                        self.assertGreater(f['node']['count']['min'],0)
                        if item.get('assembly'):self.assertEqual(item['assembly'],f['node']['assembly'])
                        else:self.assertEqual(item['prototype'],f['prototype'])
                        parts=describe_feature(self.program,f['id'])['parts']
                        self.assertEqual(item.get('nativePartCount',1),len(parts))
                        selected.extend((f['id'],part['id']) for part in parts)
        self.assertEqual(51,len(selected))

    def test_required_furnishings_fit_at_authored_minimum_room_area(self):
        for room,n in self.program.spaces.items():
            if n['template'] not in self.baseline['localRooms']:continue
            area=max(r['parameters']['min'] for r in n['requirements']
                     if r['type']=='metric' and r['parameters']['metric']=='area.exclusive')
            width=max(3,int(math.sqrt(area)));height=math.ceil(area/width)
            floor=rect(0,0,width,height)
            for x in range(width*height-area):floor.remove((width-1-x,height-1))
            s=LayoutState(self.program,floors={room:floor},walls=outline(floor))
            result=place_features(s,room,seed=7)
            with self.subTest(room=room,minimumArea=area):
                self.assertTrue(all(c['status']=='satisfied' for c in result),result)
                blocked={c for p in s.features.values() for c in p['blocking_cells']}
                starts=_aisle(s,room)-blocked
                reached=set().union(*(c for c in components(floor-blocked) if c&starts))
                for key,pose in s.features.items():
                    if self.program.features[key]['node'].get('assembly') not in self.baseline['nativeAssemblyEvidence']:continue
                    for part in pose['prototype_parts']:
                        if part['id']!='table':self.assertIn((part['x'],part['y']),reached)
                baseline_ids={item['feature'] for item in self.baseline['localRooms'][n['template']]}
                key=next(k for k,f in self.program.features.items() if f['room']==room and f['node']['id'] in baseline_ids)
                s.features.pop(key)
                self.assertTrue(any(c['id']=='fixture:'+key and c['status']=='violated' for c in assess_features(s,{room})))

    def test_cited_furnishings_are_exact_native_map_entities(self):
        loaded={}
        for record in self.baseline['nativeEvidence']:
            if record['map'] not in loaded:
                document=yaml.load((REPO/record['map']).read_text(encoding='utf-8'),Loader=NativeLoader)
                loaded[record['map']]={e['uid']:(g.get('proto',''),e) for g in document['entities'] for e in g['entities']}
            proto,entity=loaded[record['map']][record['uid']]
            transform=next(c for c in entity['components'] if c['type']=='Transform')
            with self.subTest(source=record['map'],uid=record['uid']):
                self.assertEqual(record['prototype'],proto)
                self.assertEqual(record['grid'],transform['parent'])
                self.assertEqual(record['position'],[float(v) for v in transform['pos'].split(',')])

    def test_native_access_audit_resolves_the_contained_electronics_reader(self):
        spec=importlib.util.spec_from_file_location('comparison_audit',ROOT/'research/comparative-map-audit.py')
        module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
        index=native_index()
        public={c['type']:c for c in index.resolve('Airlock')['components']}
        locked={c['type']:c for c in index.resolve('AirlockEngineeringLocked')['components']}
        self.assertNotIn('access',locked['AccessReader'])
        self.assertTrue(module.public_access(public,{},index)['eligible'])
        self.assertFalse(module.public_access(locked,{},index)['eligible'])
        missing=dict(locked);missing.pop('ContainerFill')
        result=module.public_access(missing,{},index)
        self.assertFalse(result['eligible']);self.assertTrue(result['unknown'])

    def test_every_table_group_rotation_keeps_seats_adjacent_and_facing_the_table(self):
        checked=set()
        for key,f in self.program.features.items():
            assembly=f['node'].get('assembly')
            if assembly not in self.baseline['nativeAssemblyEvidence'] or assembly in checked:continue
            checked.add(assembly);d=describe_feature(self.program,key)
            for turn in (0,90,180,270):
                pose=_pose(d,(10,10),turn,set())
                table=next(p for p in pose['prototype_parts'] if p['id']=='table')
                for seat in (p for p in pose['prototype_parts'] if p['id']!='table'):
                    delta=(table['x']-seat['x'],table['y']-seat['y'])
                    angle=math.radians(seat['rotation'])
                    self.assertEqual((round(math.sin(angle)),-round(math.cos(angle))),delta)
                    self.assertEqual(1,abs(delta[0])+abs(delta[1]))
        self.assertEqual(set(self.baseline['nativeAssemblyEvidence']),checked)

    def test_group_offsets_and_facing_match_the_cited_native_map_pose(self):
        loaded={}
        for assembly,source in self.baseline['nativeAssemblyEvidence'].items():
            if source['map'] not in loaded:
                doc=yaml.load((REPO/source['map']).read_text(encoding='utf-8'),Loader=NativeLoader)
                loaded[source['map']]={e['uid']:(g.get('proto'),next(c for c in e.get('components',[]) if c['type']=='Transform'))
                                       for g in doc['entities'] for e in g['entities'] if any(c['type']=='Transform' for c in e.get('components',[]))}
            records=loaded[source['map']]
            origin=[float(v) for v in records[source['anchorUid']][1]['pos'].split(',')]
            for part in self.program.definitions['assemblies'][assembly]['parts']:
                proto,t=records[source['parts'][part['id']]];x,y=(float(v) for v in t['pos'].split(','))
                native_rotation=round(math.degrees(float(t.get('rot','0 rad').split()[0])))%360
                self.assertEqual(proto,part['prototypes'][0])
                self.assertEqual((part['x'],part['y']),(x-origin[0],y-origin[1]))
                self.assertEqual(part['rotation'],native_rotation)


if __name__=='__main__':unittest.main()
