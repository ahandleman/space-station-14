"""Reproducible native genpop custody evidence; no inferred room ownership."""
import hashlib
import json
import math
from pathlib import Path
import sys

import yaml

TOOL=Path(__file__).resolve().parents[1]
REPO=TOOL.parents[1]
sys.path.insert(0,str(TOOL))
from architect.fixtures import NativeLoader,native_index,_merge
from architect.state import json_ready

SOURCES=[
    'Resources/Prototypes/Entities/Structures/Doors/EnergyGates/energy-gate.yml',
    'Resources/Prototypes/Entities/Structures/Storage/Closets/Lockers/lockers.yml',
    'Resources/Prototypes/Entities/Structures/Storage/Closets/wall_lockers.yml',
    'Resources/Prototypes/Entities/Objects/Misc/identification_cards.yml',
    'Content.Shared/Security/Systems/SharedGenpopSystem.cs',
    'Content.Shared/Security/Components/GenpopLockerComponent.cs',
    'Content.Server/Security/GenpopSystem.cs',
    'Content.Shared/Access/Components/ExpireIdCardComponent.cs',
    'Content.Shared/Access/Systems/SharedIdCardSystem.cs',
    'Content.Server/Access/Systems/IdCardSystem.cs',
    'Content.Shared/Doors/Systems/SharedTurnstileSystem.cs',
    'RobustToolbox/Robust.Shared.Maths/Angle.cs',
]


def sha(path):return hashlib.sha256(path.read_bytes()).hexdigest()


def components(prototype):
    return {c['type']:c for c in native_index().resolve(prototype).get('components',[])}


def angle(value):
    text=str(value)
    return float(text.split()[0]) if text.endswith('rad') else math.radians(float(text))


def audit():
    index=native_index()
    prototype_ids=['EnergyGateGenpopEnter','EnergyGateGenpopLeave','LockerPrisoner',
                   'LockerPrisoner2','LockerWallBasePrisoner','PrisonerIDCard']
    prototypes={p:{'source':index.sources[p],'components':components(p)} for p in prototype_ids}
    maps=[]
    for name in ('box','packed','bagel','plasma','saltern'):
        path=REPO/f'Resources/Maps/{name}.yml'
        document=yaml.load(path.read_text(encoding='utf-8'),Loader=NativeLoader)
        entries=[]
        for group in document['entities']:
            prototype=group.get('proto','')
            if not (prototype.startswith(('EnergyGateGenpop','LockerPrisoner','LockerWallPrisoner'))
                    or prototype=='LockerWallBasePrisoner'):
                continue
            base=components(prototype)
            for entity in group['entities']:
                mapped={c['type']:c for c in entity.get('components',[])}
                effective={k:dict(v) for k,v in base.items()}
                for key,value in mapped.items():effective[key]=_merge(effective.get(key,{}),value)
                transform=effective.get('Transform',{})
                rotation=angle(transform.get('rot','0 rad'))
                position=[float(x) for x in transform.get('pos','0,0').split(',')]
                entry={'uid':entity['uid'],'prototype':prototype,'grid':transform.get('parent'),
                       'position':position,'rotationDegrees':round(math.degrees(rotation)%360,6),
                       'source':f'Resources/Maps/{name}.yml#uid={entity["uid"]}',
                       'mappedComponents':mapped,
                       'nativeAccess':effective.get('AccessReader',{}).get('access'),
                       'hasNativeDoorComponent':'Door' in effective,
                       'hasNativeAirtightComponent':'Airtight' in effective,
                       'hasDeviceLinkSource':'DeviceLinkSource' in effective,
                       'hasDeviceLinkSink':'DeviceLinkSink' in effective}
                if 'Turnstile' in effective:
                    entry['travelDirection']=[round(math.sin(rotation),6),round(-math.cos(rotation),6)]
                if 'GenpopLocker' in effective:
                    entry['initialLinkedId']=effective['GenpopLocker'].get('linkedId')
                entries.append(entry)
        gates=[e for e in entries if 'travelDirection' in e]
        lockers=[e for e in entries if 'initialLinkedId' in e]
        for gate in gates:
            gate['nearbyLockers']=[{'uid':e['uid'],'distance':round(math.dist(e['position'],gate['position']),3)}
                for e in lockers if e['grid']==gate['grid'] and math.dist(e['position'],gate['position'])<=15]
        maps.append({'map':name,'source':str(path.relative_to(REPO)).replace('\\','/'),
                     'sha256':sha(path),'gates':gates,'lockers':lockers,
                     'gateCount':len(gates),'lockerCount':len(lockers)})
    result={'format':'ss14-native-genpop-custody-evidence','version':1,
            'scope':'Source-backed prototype/mechanic and mapped pose facts; no native execution or room ownership certification.',
            'userDesign':'Sentencing holds timed prisoner lockers; separate entry/exit turnstiles connect Brig and Sentencing; Sentencing also connects Perma; individual timed cells are absent by default.',
            'prototypeFacts':prototypes,'maps':maps,
            'sourceHashes':{s:sha(REPO/s) for s in SOURCES},
            'runtimePairing':'Each configured GenpopLocker spawns one PrisonerIDCard and stores that entity as LinkedId. Gates read access tags, with no per-locker gate wiring.',
            'directionConvention':{'0':[0,-1],'90':[1,0],'180':[0,1],'270':[-1,0]},
            'limits':['No simulated sentence expiration, AI/mob movement, pulling, item retrieval, or native map export.',
                      'Nearby locker coordinates do not prove a named room ownership boundary.',
                      'Map variation is evidence; the current user-requested custody design is authoritative.']}
    out=TOOL/'research/custody-flow-correction.json'
    out.write_text(json.dumps(json_ready(result),indent=2,allow_nan=False),encoding='utf-8')
    for m in maps:print(m['map'],m['gateCount'],'gates',m['lockerCount'],'lockers',[(e['uid'],e['position'],e['rotationDegrees']) for e in m['gates']])
    print(out)
    return result


if __name__=='__main__':audit()
