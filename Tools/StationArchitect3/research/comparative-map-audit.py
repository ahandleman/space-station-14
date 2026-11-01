"""Read-only native map / generated-layout comparison; ownership is not inferred.

Run from the repository root. Native chunks follow MapChunkSerializer.cs; inert
YAML and inherited native prototypes come from the independent v3 source adapter.
"""
from __future__ import annotations

import argparse
import base64
from collections import Counter
import hashlib
import itertools
import json
import math
from pathlib import Path
import re
import statistics
import struct
import sys

import yaml

TOOL=Path(__file__).resolve().parents[1]
REPO=TOOL.parents[1]
sys.path.insert(0,str(TOOL))
from architect.fixtures import NativeLoader, native_index, _merge
from architect.geometry import bbox, components, neighbors, outline, rect
from architect.run import load_layout
from architect.state import json_ready

AMENITY=re.compile(r'\b(dorms?|dormitory|bedroom|bathroom|restroom|toilet|arcade|library|chapel|bar|kitchen|botany|theat(?:er|re)|janitor|gym|lounge|tools|cryosleep|cryogenic|garden|observatory|cafeteria|restaurant)\b',re.I)
SOCIAL=re.compile(r'\b(dorms?|dormitory|bedroom|bathroom|restroom|toilet|arcade|library|chapel|bar|theat(?:er|re)|janitor|gym|lounge|tools|cryosleep|garden|observatory|cafeteria|restaurant)\b',re.I)
FURNITURE=re.compile(r'^(?:Chair|Stool|Bench|Sofa|Table|Bed|Bookshelf|Toilet|Sink|Shower|PottedPlant|Locker|Rack|VendingMachine)')
SEATING=re.compile(r'^(?:Chair|Stool|Bench|Sofa|Table)')
DEPARTMENT=re.compile(r'\b(engineering|security|medical|science|cargo|bridge|kitchen|botany|bar|library|chapel|dorm|arcade)\b',re.I)


def load(path):return yaml.load(Path(path).read_text(encoding='utf-8-sig'),Loader=NativeLoader)
def digest(path):return hashlib.sha256(Path(path).read_bytes()).hexdigest()
def pair(value):return tuple(float(v) for v in str(value).split(','))


def shapes(cells):
    x0,y0,x1,y1=bbox(cells);w,h=x1-x0,y1-y0
    return {'area':len(cells),'bounds':[x0,y0,x1,y1],
            'fillRatio':len(cells)/max(1,w*h),'aspect':max(w,h)/max(1,min(w,h))}


def dispersion(points,extent):
    if not points:return {'count':0}
    x0,y0,x1,y1=extent;diag=math.hypot(x1-x0,y1-y0)
    xs=[p[0] for p in points];ys=[p[1] for p in points]
    mean=(statistics.mean(xs),statistics.mean(ys))
    return {'count':len(points),'bounds':[min(xs),min(ys),max(xs),max(ys)],
            'normalizedRmsRadius':math.sqrt(statistics.mean((x-mean[0])**2+(y-mean[1])**2 for x,y in points))/max(1,diag),
            'stationWidthFraction':(max(xs)-min(xs))/max(1,x1-x0),
            'stationHeightFraction':(max(ys)-min(ys))/max(1,y1-y0),
            'maximumSeparation':max((math.dist(a,b) for a,b in itertools.combinations(points,2)),default=0)}


def grid_tiles(document,entities):
    grids={}
    for uid,e in entities.items():
        if 'MapGrid' not in e['components']:continue
        tiles={}
        for chunk in e['components']['MapGrid'].get('chunks',{}).values():
            size=chunk.get('size',16);cx,cy=map(int,str(chunk['ind']).split(','));version=chunk.get('version',1)
            format_='<iBBB' if version>=7 else '<iBB' if version>=6 else '<HBB'
            packed=base64.b64decode(chunk['tiles'])
            if len(packed)!=size*size*struct.calcsize(format_):raise ValueError('Unexpected native tile chunk size')
            for i,(tile,*_) in enumerate(struct.iter_unpack(format_,packed)):
                name=document['tilemap'][tile]
                if name!='Space':tiles[(cx*size+i%size,cy*size+i//size)]=name
        grids[uid]=tiles
    return grids


def graph_stats(regions,edges):
    vertices=set(range(len(regions)));adj={v:set() for v in vertices}
    for a,b in edges:adj[a].add(b);adj[b].add(a)
    unseen=set(vertices);count=0
    while unseen:
        count+=1;pending=[unseen.pop()]
        while pending:
            for n in adj[pending.pop()]&unseen:unseen.remove(n);pending.append(n)
    return {'vertices':len(vertices),'distinctRegionEdges':len(edges),'connectedComponents':count,
            'cycleRank':len(edges)-len(vertices)+count}


def cycle_evidence(edges,doors):
    tree={};cycles=[]
    doors_by_edge={}
    for door in doors:
        if not door['publicCredentialEligible']:continue
        for edge in itertools.combinations(door['adjacentClosedComponents'],2):doors_by_edge.setdefault(edge,[]).append(door['uid'])
    for a,b in sorted(edges):
        queue=[a];previous={a:None}
        for current in queue:
            if current==b:break
            for n in sorted(tree.get(current,set())):
                if n not in previous:previous[n]=current;queue.append(n)
        if b in previous:
            path=[];current=b
            while current is not None:path.append(current);current=previous[current]
            path.reverse();path.append(a)
            ids=[min(doors_by_edge[tuple(sorted((u,v)))]) for u,v in zip(path,path[1:])]
            cycles.append({'closedComponents':path,'doorUids':ids,
                           'scope':'public credential-eligible component graph; furniture collision and runtime states excluded'})
        else:
            tree.setdefault(a,set()).add(b);tree.setdefault(b,set()).add(a)
    return cycles


def public_holes(walk):
    if not walk:return []
    public=max(components(walk),key=len)
    x0,y0,x1,y1=bbox(public);canvas=rect(x0-1,y0-1,x1-x0+2,y1-y0+2)
    result=[]
    for hole in components(canvas-public):
        if any(x in(x0-1,x1) or y in(y0-1,y1) for x,y in hole):continue
        s=shapes(hole)
        if s['area']>=9 and min(s['bounds'][2]-s['bounds'][0],s['bounds'][3]-s['bounds'][1])>=3:result.append(s)
    return result


def public_access(cs,entities,index,seen=()):
    """Resolve native reader delegation, including authored map-init boards.

    AccessReaderSystem.IsAllowed ignores the outer reader's clauses when its
    containerAccessProvider is set. Missing/unknown providers are never public.
    """
    if len(seen)>16:return {'eligible':False,'unknown':True,'source':'delegation_limit','clauses':[]}
    reader=cs.get('AccessReader')
    if reader is None:return {'eligible':True,'source':'no_reader','clauses':[]}
    if reader.get('enabled',True) is False:return {'eligible':True,'source':'disabled_reader','clauses':[]}
    provider=reader.get('containerAccessProvider')
    if provider:
        container=cs.get('ContainerContainer',{}).get('containers',{}).get(provider,{})
        child_ids=container.get('entities',[]) if isinstance(container,dict) else []
        children=[(f'uid:{uid}',entities[uid]['components']) for uid in child_ids if uid in entities]
        if not children:
            for proto in cs.get('ContainerFill',{}).get('containers',{}).get(provider,[]):
                if proto in seen:continue
                native=index.resolve(proto);children.append((proto,{c['type']:c for c in native.get('components',[])}))
        checks=[{'provider':name,**public_access(child,entities,index,(*seen,name))} for name,child in children if 'AccessReader' in child]
        return {'eligible':any(c['eligible'] for c in checks),'source':'container:'+provider,'providers':checks,
                'unknown':not bool(checks),'clauses':[clause for c in checks for clause in c.get('clauses',[])]}
    access=reader.get('access',[])
    return {'eligible':not access or any(not clause for clause in access),'source':'direct_reader','clauses':access}


def inspect_map(name,localization,migrations):
    path=REPO/f'Resources/Maps/{name}.yml';doc=load(path);index=native_index();entities={};unresolved=Counter()
    for group in doc['entities']:
        raw=group.get('proto','');proto=migrations.get(raw,raw)
        if proto is None:continue
        try:base=index.resolve(proto) if proto else {}
        except ValueError:base={};unresolved[proto]+=len(group['entities'])
        defaults={c['type']:c for c in base.get('components',[])}
        for node in group['entities']:
            cs=dict(defaults)
            for c in node.get('components',[]):cs[c['type']]=_merge(cs.get(c['type'],{}),c)
            entities[node['uid']]={'uid':node['uid'],'prototype':proto,'rawPrototype':raw,'components':cs}
    grids=grid_tiles(doc,entities);main=max(grids,key=lambda k:len(grids[k]));tiles=grids[main]
    floor={c for c,tile in tiles.items() if tile!='Lattice'}
    blocked=set();doors=[];beacons=[];furniture=[];equipment=[];records=[]
    for uid,e in entities.items():
        cs=e['components'];t=cs.get('Transform',{})
        # Direct grid children only: carried/container inventory is not furniture.
        if t.get('parent')!=main:continue
        x,y=pair(t.get('pos','0,0'));cell=(math.floor(x),math.floor(y))
        rec={'uid':uid,'prototype':e['prototype'],'rawPrototype':e['rawPrototype'],'grid':main,'position':[x,y],
             'rotation':t.get('rot','0 rad'),'source':f'Resources/Maps/{name}.yml#uid={uid}',
             'prototypeSource':index.sources.get(e['prototype'])}
        tags=cs.get('Tag',{}).get('tags',[])
        if 'Wall' in cs or 'Window' in tags or 'Grille' in cs:blocked.add(cell)
        if 'Door' in cs:
            access=public_access(cs,entities,index)
            doors.append({**rec,'cell':cell,'access':access,'publicCredentialEligible':access['eligible']})
        if 'NavMapBeacon' in cs:
            b=cs['NavMapBeacon'];label=b.get('text') or localization.get(b.get('defaultText'),b.get('defaultText',''))
            beacons.append({**rec,'label':label,'amenity':bool(AMENITY.search(str(label))),
                            'socialAmenity':bool(SOCIAL.search(str(label)))})
        if FURNITURE.match(e['prototype']):furniture.append({**rec,'seatingOrTable':bool(SEATING.match(e['prototype']))})
        if 'ApcPowerReceiver' in cs and 'Item' not in cs:equipment.append(rec)
        if AMENITY.search(e['prototype']):records.append(rec)
    closed=floor-blocked-{d['cell'] for d in doors};regions=components(closed)
    region_by_cell={c:i for i,group in enumerate(regions) for c in group}
    rooms=[]
    for i,cells in enumerate(regions):
        labels=[b for b in beacons if region_by_cell.get((math.floor(b['position'][0]),math.floor(b['position'][1])))==i]
        if not labels:continue
        f=[r for r in furniture if (math.floor(r['position'][0]),math.floor(r['position'][1])) in cells]
        e=[r for r in equipment if (math.floor(r['position'][0]),math.floor(r['position'][1])) in cells]
        rooms.append({'component':i,**shapes(cells),'beaconLabels':[b['label'] for b in labels],
                      'beaconUids':[b['uid'] for b in labels],'furnitureCount':len(f),'furnitureUids':[r['uid'] for r in f],
                      'seatingTableCount':sum(r['seatingOrTable'] for r in f),'poweredInstallationCount':len(e),
                      'ownership':'unknown; closed-door floor component proxy'})
        for b in labels:b['closedDoorComponent']=i
    edges=set();public_edges=set()
    for d in doors:
        regions_at={region_by_cell[c] for c in neighbors(d['cell']) if c in region_by_cell}
        d['adjacentClosedComponents']=sorted(regions_at)
        for edge in itertools.combinations(sorted(regions_at),2):
            edges.add(edge)
            if d['publicCredentialEligible']:public_edges.add(edge)
    public_walk=closed|{d['cell'] for d in doors if d['publicCredentialEligible']}
    holes=public_holes(public_walk)
    stats={'map':name,'path':path.relative_to(REPO).as_posix(),'sha256':digest(path),'mapFormat':doc['meta']['format'],
           'mainGrid':main,'gridCount':len(grids),'nonSpaceTiles':len(tiles),'nonLatticeFloorTiles':len(floor),
           'bounds':list(bbox(floor)),'beacons':beacons,'doors':doors,'furniture':furniture,'poweredInstallations':equipment,
           'beaconComponents':rooms,'amenityDispersion':dispersion([b['position'] for b in beacons if b['amenity']],bbox(floor)),
           'socialDispersion':dispersion([b['position'] for b in beacons if b['socialAmenity']],bbox(floor)),
           'closedDoorGraph':graph_stats(regions,edges),'publicDoorGraph':graph_stats(regions,public_edges),
           'publicGraphCycles':cycle_evidence(public_edges,doors),
           'largePublicWalkHoles':holes,'unresolvedPrototypes':dict(unresolved)}
    plotting={'floor':floor,'walls':blocked,'doors':{d['cell'] for d in doors},'beacons':beacons,
              'furniture':furniture,'public':max(components(public_walk),key=len),'regions':regions}
    return stats,plotting


def inspect_generated(path):
    path=Path(path).resolve()
    s=load_layout(path);department=s.metadata['departmentInstance'];owned=s.program.descendants(department,True)
    floor={c for room in owned for c in s.floors.get(room,set())};markers=[];rooms=[];furniture=[]
    for room in owned:
        cells=s.floors.get(room,set());n=s.program.spaces[room];xs=[x+.5 for x,y in cells];ys=[y+.5 for x,y in cells]
        if not cells:continue
        prototypes=[p['prototype'] for key,pose in s.features.items() if pose['room']==room for p in pose.get('prototype_parts',[])]
        f=[p for p in prototypes if FURNITURE.match(p)]
        rooms.append({'id':room,'name':n['node'].get('name',room),**shapes(cells),'prototypes':prototypes,
                      'physicalParts':len(prototypes),'furnitureCount':len(f),'seatingTableCount':sum(bool(SEATING.match(p)) for p in f)})
        markers.append({'label':n['node'].get('name',n['template'].split('/')[-1]),'position':[statistics.mean(xs),statistics.mean(ys)],
                        'amenity':True,'socialAmenity':True})
    for key,pose in s.features.items():
        if pose['room'] not in owned:continue
        for p in pose.get('prototype_parts',[]):
            if FURNITURE.match(p['prototype']):furniture.append({'prototype':p['prototype'],'position':[p['x']+.5,p['y']+.5],'rotation':p['rotation'],'id':key})
    stats={'path':str(Path(path).relative_to(REPO)),'sha256':digest(path),'department':department,
           'sourceHash':s.program.source_hash,
           'placementScenario':s.metadata.get('placementScenario'),
           'floorArea':len(floor),'bounds':list(bbox(floor)),'rooms':rooms,'furnitureCount':len(furniture),
           'seatingTableCount':sum(bool(SEATING.match(r['prototype'])) for r in furniture),
           'physicalParts':sum(r['physicalParts'] for r in rooms),
           'scope':'isolated department experiment; no whole-station ownership comparison claimed'}
    harness={c for room,cells in s.floors.items() if room not in owned for c in cells}
    plotting={'floor':floor|harness,'walls':s.walls,'doors':{c for i in s.interfaces for c in i['cells']},
              'beacons':markers,'furniture':furniture,'public':harness,'roomFloors':s.floors}
    return stats,plotting


def plot_maps(items,path):
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    fig,axes=plt.subplots(2,3,figsize=(20,12),facecolor='#101722')
    for ax,(title,data) in zip(axes.flat,items):
        ax.set_facecolor('#101722')
        def scatter(cells,color,size=3):
            if cells:ax.scatter([x+.5 for x,y in cells],[y+.5 for x,y in cells],s=size,c=color,marker='s',linewidths=0)
        scatter(data['floor'],'#38485b',4);scatter(data['public'],'#597b8a',4);scatter(data['walls'],'#8795a1',2)
        scatter(data['doors'],'#e6bd67',3)
        public=[b for b in data['beacons'] if b['amenity']]
        if data['furniture']:ax.scatter([r['position'][0] for r in data['furniture']],[r['position'][1] for r in data['furniture']],s=7,c='#b2cf87',alpha=.5)
        for b in public:
            x,y=b['position'];ax.scatter([x],[y],s=24,c='#ff8b7b',edgecolors='#101722',linewidths=.5)
            ax.annotate(b['label'],(x,y),xytext=(3,3),textcoords='offset points',color='#ffe0d5',fontsize=6)
        ax.set_aspect('equal');ax.set_title(title,color='white',fontsize=12)
        ax.tick_params(colors='#b5c5d4',labelsize=7)
    fig.suptitle('Native station amenities versus isolated generated suites',color='white',fontsize=19)
    fig.text(.5,.02,'Red: named amenity beacons / authored generated rooms. Green: furniture-family entities. Blue: native public credential-eligible floor approximation.\nEach panel has its own extent; native beacon regions are not inferred department ownership. Generated panels are isolated tests, not full stations.',ha='center',color='#c3d1df',fontsize=10)
    fig.tight_layout(rect=(0,.05,1,.96));fig.savefig(path,dpi=180,facecolor=fig.get_facecolor());plt.close(fig)


def plot_room_examples(maps,generated,plots,path,generated_label='isolated baseline'):
    import matplotlib.pyplot as plt
    selected=[]
    for label,names in (('Bar',('box','packed','bagel')),('Library',('packed','saltern','bagel'))):
        for name in names:
            stats=next(m for m in maps if m['map']==name);plot=plots[name]
            room=next(r for r in stats['beaconComponents'] if label in r['beaconLabels'])
            cells=plot['regions'][room['component']]
            furnishings=[f for f in stats['furniture'] if f['uid'] in room['furnitureUids']]
            selected.append((f'{name.title()} {label} — beacon UID {room["beaconUids"][0]}',cells,furnishings,room['fillRatio']))
        g=next(g for g in generated if g['department'].startswith('service['));plot=plots['service']
        tag='/bar_commons[' if label=='Bar' else '/library['
        room=next(r for r in g['rooms'] if tag in r['id']);cells=plot['roomFloors'][room['id']]
        furnishings=[f for f in plot['furniture'] if f['id'].startswith(room['id']+'#')]
        selected.append((f'Generated {label.lower()} — {generated_label}',cells,furnishings,room['fillRatio']))
    fig,axes=plt.subplots(2,4,figsize=(17,11),facecolor='#101722')
    extent=max(max(bbox(c)[2]-bbox(c)[0],bbox(c)[3]-bbox(c)[1]) for _,c,_,_ in selected)+2
    for ax,(title,cells,furniture,fill) in zip(axes.flat,selected):
        x0,y0,x1,y1=bbox(cells);ax.set_facecolor('#101722')
        ax.scatter([x-x0+.5 for x,y in cells],[y-y0+.5 for x,y in cells],marker='s',s=150,c='#3c5d70',linewidths=0)
        wall=outline(cells);ax.scatter([x-x0+.5 for x,y in wall],[y-y0+.5 for x,y in wall],marker='s',s=120,c='#8394a1',linewidths=0)
        for f in furniture:
            kind='seating' if re.match(r'^(Chair|Stool|Bench|Sofa)',f['prototype']) else 'table' if f['prototype'].startswith('Table') else 'other'
            ax.scatter([f['position'][0]-x0],[f['position'][1]-y0],s=95,marker={'seating':'o','table':'s','other':'D'}[kind],
                       c={'seating':'#ffb25b','table':'#a7d589','other':'#c4a8ff'}[kind],edgecolors='#101722',linewidths=.7)
            if kind=='seating':
                raw=f.get('rotation',0)
                angle=float(raw.split()[0]) if isinstance(raw,str) and raw.endswith('rad') else math.radians(float(str(raw).split()[0]))
                ax.arrow(f['position'][0]-x0,f['position'][1]-y0,.3*math.sin(angle),-.3*math.cos(angle),
                         color='#1a2530',width=.025,head_width=.13,length_includes_head=True,zorder=4)
        ax.set_xlim(-1,extent);ax.set_ylim(-1,extent);ax.set_aspect('equal');ax.tick_params(colors='#abc0d2',labelsize=7)
        ax.set_title(f'{title}\n{len(cells)} floor tiles · {len(furniture)} furnishings · fill {fill:.2f}',color='white',fontsize=10)
    fig.suptitle('Room-scale comparison at equal tile scale',color='white',fontsize=18)
    fig.text(.5,.025,'Orange circles/arrows: seats and native facing · Green squares: tables · Purple diamonds: shelves/vendors/other furniture\nNative floor components are beacon-local geometry proxies, not traced ownership. Exact entity UIDs are in audit.json.',ha='center',color='#c3d1df',fontsize=10)
    fig.subplots_adjust(left=.04,right=.99,bottom=.1,top=.9,hspace=.36,wspace=.2)
    fig.savefig(path,dpi=180,facecolor=fig.get_facecolor());plt.close(fig)


def markdown(report):
    lines=['# Native map comparison — fresh spatial audit','',
           'This audit reads current map tile chunks and mapped entity UIDs directly. Native room ownership is **not** inferred from beacons. Generated views are isolated department experiments, not completed station layouts.','',
           '| Native map / grid | Floor tiles | Shared amenity markers | Width / height span | Furniture-family entities | Large public-floor holes |',
           '| --- | ---: | ---: | --- | ---: | ---: |']
    for m in report['maps']:
        d=m['socialDispersion'];lines.append(f"| {m['map']} / {m['mainGrid']} | {m['nonLatticeFloorTiles']} | {d['count']} | {d.get('stationWidthFraction',0):.0%} / {d.get('stationHeightFraction',0):.0%} | {len(m['furniture'])} | {len(m['largePublicWalkHoles'])} |")
    lines+=['','Shared markers screen dorms, libraries, chapels, bars, janitor closets, tools, cryosleep, observatories and other social/crew amenities. This is a declared screening vocabulary, not a complete commons ontology or a public-access classification. Furniture counts match the explicit prototype-family regex in the script; carried/container contents are excluded. Native whole-grid floor totals are all mapped non-space/non-lattice tiles, not exclusive usable room floors. Native furniture includes matching interface furnishings and spawners, while generated totals count selected feature parts; these are coverage indicators rather than identical inventories.','',
            '## Direct comparison to generated experiments','']
    for g in report['generated']:
        lines.append(f"- `{g['department']}`: {len(g['rooms'])} rooms, {g['floorArea']} usable tiles, {g['physicalParts']} native entity parts, {g['furnitureCount']} furniture-family entities, including {g['seatingTableCount']} seating/table entities. Source: `{g['path']}`.")
    if report.get('updatedPlacements'):
        lines+=['','### Current local placement snapshots','',
                'The following are the distribution-aware local tests after furnishing changes. They do not claim that separated amenities have already been positioned into a whole station.','']
        for g in report['updatedPlacements']:
            lines.append(f"- `{g['placementScenario']}`: {g['floorArea']} floor tiles, {g['physicalParts']} native entity parts, {g['furnitureCount']} furniture-family entities; `{g['path']}`.")
    lines+=['','These generated snapshots are explicitly hashed comparison inputs. The initial snapshots predate the targeted furnishing correction: nine local room templates received local furnishings, adding 43 native entity parts through 30 feature instances by default, or 51 native parts through 34 feature instances when two optional concourse plazas are explicitly selected. Table/seat assemblies preserve all parts and their facing; grouping does not remove chairs. The plazas are not forced into distributed commons. See `../comparative-furnishing-baseline.json`. This correction does not itself complete station utilities or furnish every other department.','',
            '| Native component example | Beacon UID / grid | Floor area | Bounding fill | Seating/table entities |',
            '| --- | --- | ---: | ---: | ---: |']
    for m in report['maps']:
        for room in m['beaconComponents']:
            if any(label in ('Bar','Library','Dorms','Security','Cargo') for label in room['beaconLabels']):
                lines.append(f"| {m['map']}: {', '.join(room['beaconLabels'])} | {','.join(map(str,room['beaconUids']))} / {m['mainGrid']} | {room['area']} | {room['fillRatio']:.2f} | {room['seatingTableCount']} |")
    lines+=['','## Source-level examples','']
    for m in report['maps']:
        examples=[b for b in m['beacons'] if b['socialAmenity']]
        lines.append(f"### {m['map']} — grid {m['mainGrid']}")
        lines.append('')
        for b in examples[:20]:lines.append(f"- {b['label']}: UID **{b['uid']}**, `{b['prototype']}`, grid position `{tuple(b['position'])}`; `{b['source']}`.")
        if m['publicGraphCycles']:
            lines.append(f"- One public-credential graph cycle uses door UIDs **{', '.join(map(str,m['publicGraphCycles'][0]['doorUids']))}** on this grid. This is a closed-floor-component graph witness, not a simulated chase route.")
        lines.append('')
    lines+=['## Interpretation and implementation direction','',
            '- Treat commons as station-owned amenity instances placed into multiple host neighborhoods. Keep local requirements and identifiers, but remove a global commons circulation room or shared envelope as a default placement prerequisite.',
            '- Test amenity placement in a station context with multiple separated public circulation segments and department anchors. An isolated commons suite passing its own geometry does not test whether libraries, dormitories and arcades are distributed usefully.',
            '- Add authored furnishing/operational kits: seating, tables, beds, lockers, counters, lights, APCs, air alarms, disposal intakes and complete machine dependencies. Preserve circulation clearance independently of furnishing coverage. Current inventory completeness means completeness of the selected YAML, which is much smaller than native map contents.',
            '- The current correction adds exact local table/facing-seat assemblies for libraries, lounges, dining commons and optional plazas. Stage/audience orientation, counter customer positions and broader decoration still need authored arrangements; baseline counts alone do not recreate every social layout.',
            '- Measure corridor alternatives using actual access-qualified graph paths and geometry. The public-floor-hole statistic below is only a topology screening proxy; it is not a sec-versus-antag chase simulation.',
            '- Compare shapes at matching scales: native beacon floor components can join several functions, while generated rooms have explicit ownership. Use native examples to motivate nested suites, bent shared circulation and nonuniform room outlines rather than copying a single bounding rectangle.',
            '', '## Method and limits','',
            '- Native tile chunks use `RobustToolbox/Robust.Shared/EntitySerialization/MapChunkSerializer.cs` (versions 6/7 integer tile IDs, flags, variant and optional rotation/mirror byte). Current prototype inheritance and migration aliases are resolved from this checkout.',
            '- Geometry treats mapped wall/window/grille entities as full blocked tiles and closes doors for component measurements. Directional walls/windows therefore overpartition some geometry. Equipment collision and runtime door states are not simulated.',
            '- Public floor opens only doors whose effective authored reader permits an empty credential set. Container-delegated readers resolve the mapped board or native `ContainerFill` board prototype according to `AccessReaderSystem.IsAllowed`; missing providers are never treated as public. Bolts, power, runtime board construction, emagging, atmospheric conditions and intermediate furniture collision remain unverified.',
            '- Large public-floor holes are enclosed nonwalkable regions of at least nine tiles and at least three tiles on each bounding side, inside the largest eligible public component. They screen for potential circulation around substantial obstacles; they are not a count of designed chase loops.',
            '- Shape/equipment examples in the JSON include exact beacon UIDs and furniture UIDs, but ownership remains unknown. Empty or duplicated labels do not become inferred rooms.',
            '- PNG positions are true grid-local tile coordinates. The station overview panels have different extents; the room comparison uses equal tile scales. None is a traced native ownership map.', '']
    return '\n'.join(lines)


def main():
    ap=argparse.ArgumentParser();ap.add_argument('--output',default=str(TOOL/'research/comparative-map-audit'))
    ap.add_argument('--commons-layout');ap.add_argument('--service-layout')
    ap.add_argument('--updated-scenarios',help='Optional current placement matrix directory; compare preferred library/hospitality examples.')
    args=ap.parse_args();out=Path(args.output);out.mkdir(parents=True,exist_ok=True)
    localization=dict(re.findall(r'^([\w-]+)\s*=\s*(.*?)\s*$',(REPO/'Resources/Locale/en-US/navmap-beacons/station-beacons.ftl').read_text(encoding='utf-8'),re.M))
    migrations=load(REPO/'Resources/migration.yml');maps=[];plots=[];named_plots={}
    for name in ('box','packed','saltern','bagel'):
        data,plot=inspect_map(name,localization,migrations);maps.append(data);plots.append((f'{name.title()} — native grid {data["mainGrid"]}',plot));named_plots[name]=plot
        print(name,'grid',data['mainGrid'],'social markers',data['socialDispersion'],'furniture',len(data['furniture']),'public holes',len(data['largePublicWalkHoles']),flush=True)
    generated=[]
    for department,area in (('commons',581),('service',568)):
        supplied=getattr(args,department+'_layout')
        path=Path(supplied).resolve() if supplied else REPO/f'.tmp/architect3-departments-variety/{department}-{area}-seed7/layout.json'
        data,plot=inspect_generated(path);generated.append(data);plots.append((f'{department.title()} — generated isolated suite',plot));named_plots[department]=plot
        print(department,{k:data[k] for k in ('floorArea','physicalParts','furnitureCount','seatingTableCount')},flush=True)
    updated=[];current_rooms=[];current_furniture=[];current_floor={}
    if args.updated_scenarios:
        for path in sorted(Path(args.updated_scenarios).glob('*/report.json')):
            candidate=json.loads(path.read_text(encoding='utf-8'))
            if candidate.get('testScale')!=1 or candidate.get('scope',{}).get('id') not in ('service/library[0]','service/hospitality[0]','commons/lounge[0]','commons/concourse[0]','commons/arcade[0]','commons/reporter_office[0]','commons/emergency_storage[0]','service/chapel[0]','service/theatre[0]'):continue
            g,p=inspect_generated(path.parent/'layout.json');updated.append(g)
            if g['department'].startswith('service['):
                current_rooms.extend(g['rooms']);current_furniture.extend(p['furniture']);current_floor.update(p['roomFloors'])
    report={'format':'ss14-native-generated-comparison','version':1,'maps':maps,'generated':generated,'updatedPlacements':updated,
            'scope':'Source-backed screening; no inferred native ownership or runtime certification.',
            'classification':{'amenityRegex':AMENITY.pattern,'socialRegex':SOCIAL.pattern,'furnitureRegex':FURNITURE.pattern},
            'sourceFiles':{str(p.relative_to(REPO)):digest(p) for p in [REPO/'RobustToolbox/Robust.Shared/EntitySerialization/MapChunkSerializer.cs',REPO/'Resources/migration.yml',REPO/'Content.Shared/Access/Systems/AccessReaderSystem.cs',REPO/'Content.Shared/Containers/ContainerFillSystem.cs',TOOL/'architect/fixtures.py']}}
    (out/'audit.json').write_text(json.dumps(json_ready(report),indent=2,allow_nan=False),encoding='utf-8')
    (out/'audit.md').write_text(markdown(report),encoding='utf-8')
    plot_maps(plots,out/'native-vs-generated.png')
    plot_room_examples(maps,generated,named_plots,out/'room-scale-comparison.png')
    if any('/library[' in r['id'] for r in current_rooms) and any('/bar_commons[' in r['id'] for r in current_rooms):
        current_plots={**named_plots,'service':{'roomFloors':current_floor,'furniture':current_furniture}}
        plot_room_examples(maps,[{'department':'service[0]','rooms':current_rooms}],current_plots,
                           out/'room-scale-current.png',generated_label='current local placement')
    print(out.resolve(),flush=True)


if __name__=='__main__':main()
