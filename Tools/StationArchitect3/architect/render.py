"""Dependency-free SVG and browser inspection of actual generated tile masks."""
from __future__ import annotations

import hashlib
import html
import json
from pathlib import Path

from .state import LayoutState, json_ready


def color(key):
    hue = int(hashlib.sha256(key.encode()).hexdigest()[:8], 16) % 360
    return f'hsl({hue} 42% 46%)'


def _runs(cells):
    rows = {}
    for x, y in cells:
        rows.setdefault(y, []).append(x)
    for y, xs in sorted(rows.items()):
        xs.sort()
        first = prev = xs[0]
        for x in xs[1:]:
            if x != prev + 1:
                yield first, y, prev - first + 1
                first = x
            prev = x
        yield first, y, prev - first + 1


def svg_layout(state: LayoutState, report: dict | None = None) -> str:
    allcells = set().union(*state.floors.values()) if state.floors else {(0, 0)}
    allcells |= state.walls
    for reservation in state.reservations:
        allcells.update(reservation['cells'])
    if not allcells:
        allcells = {(0, 0)}
    x0, y0 = min(x for x, y in allcells) - 3, min(y for x, y in allcells) - 3
    w = max(x for x, y in allcells) - x0 + 4
    h = max(y for x, y in allcells) - y0 + 4
    parts = [f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="{x0} {y0} {w} {h}" role="img" aria-label="Generated station tile layout">',
             '<style>.room{stroke:none}.labels text{font:1px sans-serif;fill:#fff;paint-order:stroke;stroke:#101723;stroke-width:.15px}.reservation{stroke:#65c8d0;stroke-width:.08;fill:#65c8d020}.room:hover{filter:brightness(1.4)}.highlight{filter:brightness(1.65)}.dim{opacity:.18}</style>',
             f'<rect x="{x0}" y="{y0}" width="{w}" height="{h}" fill="#101723"/>']
    for reservation in state.reservations:
        parts.append('<g class="reservations">')
        for x, y, length in _runs(reservation['cells']):
            parts.append(f'<rect class="reservation" x="{x}" y="{y}" width="{length}" height="1"><title>{html.escape(reservation.get("kind", "reservation"))}</title></rect>')
        parts.append('</g>')
    parts.append('<g class="walls" fill="#a2aebc">')
    for x, y, length in _runs(state.walls):
        parts.append(f'<rect x="{x}" y="{y}" width="{length}" height="1"/>')
    parts.append('</g>')
    for key, cells in sorted(state.floors.items()):
        if not cells:
            continue
        instance = state.program.spaces[key]
        tags = instance['node'].get('tags', [])
        fill = '#394c61' if 'maintenance_circulation' in tags else '#60758c' if 'public_circulation' in tags else color(instance.get('owner') or key)
        parts.append(f'<g class="room" data-instance="{html.escape(key, quote=True)}" fill="{fill}"><title>{html.escape(key)} — {len(cells)} tiles</title>')
        for x, y, length in _runs(cells):
            parts.append(f'<rect x="{x}" y="{y}" width="{length}" height="1"/>')
        parts.append('</g>')
    for interface in state.interfaces:
        profile = state.program.definitions.get('interfaces', {}).get(interface['profile'], {})
        fill = '#a7efae' if 'crew_passage' in profile.get('capabilities', []) else '#efbe6a'
        for x, y, length in _runs(interface['cells']):
            parts.append(f'<rect x="{x}" y="{y}" width="{length}" height="1" fill="{fill}"><title>{html.escape(interface["profile"])}</title></rect>')
    parts.append('<g class="features" fill="#de9fcb">')
    for key, pose in state.features.items():
        parts_text = ', '.join(str(p.get('prototype', p.get('id', 'part'))) for p in pose.get('prototype_parts', []))
        title = key + (': ' + parts_text if parts_text else '')
        for x, y, length in _runs(pose.get('cells', set())):
            parts.append(f'<rect x="{x+.1}" y="{y+.1}" width="{length-.2}" height=".8"><title>{html.escape(title)}</title></rect>')
    parts.append('</g>')
    for interface in state.interfaces:
        profile = state.program.definitions.get('interfaces', {}).get(interface['profile'], {})
        direction = profile.get('passage', {}).get('direction', 'bidirectional')
        if direction == 'bidirectional':
            continue
        sign = 1 if direction == 'a_to_b' else -1
        dx, dy = (sign*n for n in interface['normal'])
        source, target = (interface['a'], interface['b']) if sign == 1 else (interface['b'], interface['a'])
        name = lambda key: state.program.spaces.get(key, {}).get('node', {}).get('name', key)
        title = interface['profile'] + ': ' + name(source) + ' -> ' + name(target)
        for x, y in sorted(interface['cells']):
            cx, cy = x+.5, y+.5
            tx, ty = cx+.32*dx, cy+.32*dy
            path = (f'M {cx-.3*dx} {cy-.3*dy} L {tx} {ty} '
                    f'M {tx-.22*dx-.17*dy} {ty-.22*dy+.17*dx} L {tx} {ty} '
                    f'L {tx-.22*dx+.17*dy} {ty-.22*dy-.17*dx}')
            parts.append(f'<path class="passage-direction" d="{path}" stroke="#152d25" stroke-width=".11" fill="none"><title>{html.escape(title)}</title></path>')
    parts.append('<g class="labels">')
    for key, cells in sorted(state.floors.items()):
        if not cells or state.program.spaces[key]['kind'] == 'passage':
            continue
        cx, cy = sum(x for x, y in cells) / len(cells), sum(y for x, y in cells) / len(cells)
        x, y = min(cells, key=lambda p: ((p[0]-cx)**2+(p[1]-cy)**2, p))
        name = state.program.spaces[key]['node'].get('name', key.rsplit('/', 1)[-1])
        parts.append(f'<text x="{x+.5}" y="{y+.5}" text-anchor="middle">{html.escape(name)}</text>')
    parts.append('</g></svg>')
    return ''.join(parts)


def write_preview(state, report, directory):
    out = Path(directory)
    out.mkdir(parents=True, exist_ok=True)
    svg = svg_layout(state, report)
    (out / 'layout.svg').write_text(svg, encoding='utf-8')
    # The viewer only inspects saved artifacts; it does not imply native-map validity.
    payload = json.dumps(json_ready({'report': report, 'trace': state.trace,
                                    'spaces': state.program.spaces}), ensure_ascii=True, allow_nan=False).replace('<', '\\u003c')
    page = r'''<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Station Architect 3 — layout inspection</title><style>
*{box-sizing:border-box}body{margin:0;background:#101723;color:#d9e4ef;font:14px system-ui}header{padding:18px 24px;background:#192435;border-bottom:1px solid #34455a}h1{font-size:20px;margin:0 0 8px}header p{margin:6px 0;color:#aebfd1}main{display:grid;grid-template-columns:320px 1fr;height:calc(100vh - 150px)}aside{overflow:auto;padding:16px;background:#152030}article{overflow:auto;position:relative}article svg{width:100%;min-height:100%;display:block}button,input{background:#25374b;color:#e9f3ff;border:1px solid #48627d;padding:7px;border-radius:4px}button{cursor:pointer}button:hover{background:#36516c}.controls{display:flex;gap:6px;flex-wrap:wrap;margin:8px 0}.row{display:block;width:100%;text-align:left;margin:3px 0;overflow-wrap:anywhere;font-size:12px}.bad{color:#ffc39e}.good{color:#a8e6ad}pre{white-space:pre-wrap;font-size:11px;overflow-wrap:anywhere}details{margin:12px 0}#status{font-weight:600}a{color:#a8d7ff}@media(max-width:800px){main{grid-template-columns:1fr;height:auto}aside{max-height:40vh}article{height:70vh}}</style>
<header><h1>Station Architect 3</h1><div id="status"></div><p>Tile layout candidate · green: walking interfaces · amber: service interfaces · blue outline: reserved exterior</p><a href="report.json">Requirement report</a> · <a href="layout.json">Saved layout</a> · <a href="layout.svg">Export SVG</a></header>
<main><aside><div class="controls"><button id="minus">−</button><button id="plus">+</button><button id="reset">Fit</button><button id="labels">Labels</button></div><input id="search" placeholder="Find a room" aria-label="Find a room" style="width:100%"><div id="rooms"></div><details open><summary>Checks</summary><pre id="checks"></pre></details><details><summary>Metrics</summary><pre id="metrics"></pre></details><details><summary>Repair trace</summary><pre id="trace"></pre></details></aside><article id="map">__SVG__</article></main>
<script id="data" type="application/json">__DATA__</script><script>
const d=JSON.parse(document.getElementById('data').textContent),r=d.report;const svg=document.querySelector('svg');let zoom=1;
const el=id=>document.getElementById(id);el('status').textContent=r.candidateStatus+' · '+r.termination;el('status').className=r.candidateStatus.startsWith('verified')?'good':'bad';
el('metrics').textContent=JSON.stringify(r.verification.metrics,null,2);el('trace').textContent=JSON.stringify(d.trace,null,2);
const issues=r.verification.checks.filter(c=>!['satisfied','inactive'].includes(c.status));el('checks').textContent=issues.length?issues.map(c=>c.status+' '+c.id+'\n'+c.message).join('\n\n'):'All requested fixture checks passed. Native realization and runtime have not been evaluated.';
function rooms(){el('rooms').replaceChildren();const q=el('search').value.toLowerCase();for(const [id,n] of Object.entries(d.spaces)){if(n.kind!=='room'||!id.toLowerCase().includes(q))continue;const b=document.createElement('button');b.className='row';b.textContent=n.node.name||id;b.title=id;b.onclick=()=>{for(const g of svg.querySelectorAll('.room')){g.classList.toggle('highlight',g.dataset.instance===id);g.classList.toggle('dim',g.dataset.instance!==id)}const own=r.verification.checks.filter(c=>c.owner===id);el('checks').textContent=own.map(c=>c.status+' '+c.id+'\n'+c.message).join('\n\n')||id};el('rooms').append(b)}}rooms();el('search').oninput=rooms;
function size(){svg.style.width=100*zoom+'%'}el('plus').onclick=()=>{zoom=Math.min(5,zoom*1.25);size()};el('minus').onclick=()=>{zoom=Math.max(.5,zoom/1.25);size()};el('reset').onclick=()=>{zoom=1;size();for(const g of svg.querySelectorAll('.room'))g.classList.remove('dim','highlight')};el('labels').onclick=()=>{const g=svg.querySelector('.labels');g.style.display=g.style.display==='none'?'':'none'};
</script></html>'''
    (out / 'index.html').write_text(page.replace('__SVG__', svg).replace('__DATA__', payload), encoding='utf-8')
