"""Current native genpop facts; these do not simulate a prison sentence."""
import json
import math
from pathlib import Path
import re
import sys
import unittest

import yaml

ROOT=Path(__file__).resolve().parents[1]
REPO=ROOT.parents[1]
sys.path.insert(0,str(ROOT))
from architect.fixtures import NativeLoader,native_index


def components(proto):
    return {c['type']:c for c in native_index().resolve(proto)['components']}


class GenpopNativeFacts(unittest.TestCase):
    def test_prisoner_card_replaces_entry_credentials_on_expiration(self):
        card=components('PrisonerIDCard')
        self.assertEqual(['GenpopEnter'],card['Access']['tags'])
        self.assertEqual(['GenpopLeave'],card['ExpireIdCard']['expiredAccess'])
        self.assertIn('GenpopIdCard',card)
        code=(REPO/'Content.Shared/Access/Systems/SharedIdCardSystem.cs').read_text()
        self.assertIn('_access.TrySetTags(ent, ent.Comp.ExpiredAccess)',code)

    def test_every_numbered_locker_has_independent_runtime_pairing_not_gate_signals(self):
        ids=['LockerPrisoner']+[f'LockerPrisoner{i}' for i in range(2,9)]
        ids+=['LockerWallBasePrisoner']+[f'LockerWallPrisoner{i}' for i in range(2,9)]
        for proto in ids:
            with self.subTest(prototype=proto):
                cs=components(proto)
                self.assertIn('GenpopLocker',cs)
                self.assertFalse(cs['Lock']['useAccess'])
                self.assertEqual([['Security']],cs['AccessReader']['access'])
                self.assertNotIn('DeviceLinkSource',cs)
                self.assertNotIn('DeviceLinkSink',cs)
                self.assertIsNone(cs['GenpopLocker'].get('linkedId'))
        code=(REPO/'Content.Server/Security/GenpopSystem.cs').read_text()
        self.assertIn('var uid = Spawn(ent.Comp.IdCardProto, spawnCoordinates)',code)
        self.assertIn('ent.Comp.LinkedId = uid',code)
        self.assertRegex(code,r'if \(sentence > 0\)\s+IdCard.SetExpireTime')
        shared=(REPO/'Content.Shared/Security/Systems/SharedGenpopSystem.cs').read_text()
        self.assertIn('FindPotentialAccessItems(args.User).Contains(ent.Comp.LinkedId.Value)',shared)
        self.assertIn('!expireIdCard.Expired',shared)

    def test_native_turnstiles_are_directional_nonairtight_interfaces(self):
        for proto,tag in [('EnergyGateGenpopEnter','GenpopEnter'),('EnergyGateGenpopLeave','GenpopLeave')]:
            cs=components(proto)
            self.assertIn('Turnstile',cs)
            self.assertNotIn('Door',cs)
            self.assertNotIn('Airtight',cs)
            self.assertEqual([[tag],['Security']],cs['AccessReader']['access'])
            self.assertEqual(['MobState','Pullable'],cs['Turnstile']['processWhitelist']['components'])
        angle=(REPO/'RobustToolbox/Robust.Shared.Maths/Angle.cs').read_text()
        self.assertIn('return (this - MathHelper.PiOver2).ToVec()',angle)
        turnstile=(REPO/'Content.Shared/Doors/Systems/SharedTurnstileSystem.cs').read_text()
        self.assertIn('return diff < Math.PI / 4',turnstile)
        self.assertIn('rot.ToWorldVec().ToAngle()',turnstile)

    def test_cited_gate_and_locker_poses_exist_in_current_maps(self):
        evidence=json.loads((ROOT/'research/custody-flow-correction.json').read_text())
        for map_record in evidence['maps']:
            text=(REPO/map_record['source']).read_text(encoding='utf-8')
            groups={}
            for record in map_record['gates']+map_record['lockers']:
                proto=record['prototype']
                if proto not in groups:
                    match=re.search(r'^- proto: '+re.escape(proto)+r'\r?\n.*?(?=^- proto:|\Z)',text,re.M|re.S)
                    self.assertIsNotNone(match,proto)
                    groups[proto]=yaml.load(match.group(),Loader=NativeLoader)[0]['entities']
                entity=next(e for e in groups[proto] if e['uid']==record['uid'])
                transform=next(c for c in entity['components'] if c['type']=='Transform')
                self.assertEqual(record['grid'],transform['parent'])
                self.assertEqual(record['position'],[float(v) for v in transform['pos'].split(',')])
                radians=float(str(transform.get('rot','0 rad')).split()[0])
                self.assertAlmostEqual(record['rotationDegrees'],math.degrees(radians)%360,places=5)
                if 'travelDirection' in record:
                    self.assertEqual(record['travelDirection'],[round(math.sin(radians),6),round(-math.cos(radians),6)])


if __name__=='__main__':unittest.main()
