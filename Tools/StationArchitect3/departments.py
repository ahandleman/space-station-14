#!/usr/bin/env python3
"""Generate and resize real catalog departments before station integration."""
from __future__ import annotations

import argparse
import html
import json
from pathlib import Path

from validate import load_program
from architect.department_packing import department_area_bounds
from architect.departments import department_ids, generate_department, isolate_department
from architect.instances import resolve_instances
from architect.run import save_run
from architect.settings import load_settings
from architect.state import json_ready
from architect.scenarios import placement_scopes, isolate_scope, scope_bounds


def write_gallery(directory, cases):
    out = Path(directory)
    rows = []
    for case in cases:
        report = case['report']
        check = report['verification']
        inventory = check['inventory']
        status = 'Local constraints passed' if check['localLayoutVerified'] else 'Needs repair'
        scope = report.get('scope', {})
        quality = check.get('layoutQuality', {})
        rows.append(f'''<a class="card" href="{html.escape(case['path'])}/index.html"><img src="{html.escape(case['path'])}/layout.svg" alt="{html.escape(scope.get('name', case['department']))} layout"><div class="body"><h2>{html.escape(scope.get('name', case['department']))} · {html.escape(case['size'])}</h2><b class="{'pass' if check['localLayoutVerified'] else 'fail'}">{status}</b><p>{html.escape(scope.get('kind', 'department'))} · quality: {html.escape(quality.get('status', 'not_evaluated'))}</p><p>{check['metrics']['department_floor']} floor tiles · {inventory['realizedRooms']}/{inventory['selectedRooms']} rooms · {inventory['realizedFeatures']}/{inventory['selectedFeatures']} equipment features; {inventory['abstractServices']} service demands</p><p>{len(check['hard_failures'])} local failures · {len(check['pendingRequirements'])} deferred checks · {len(check['externalContracts'])} external contracts</p></div></a>''')
    document = '''<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Local placement laboratory — Station Architect 3</title><style>
body{margin:0;padding:28px;background:#101723;color:#dce7f3;font:15px system-ui}h1{font-size:26px;margin-bottom:8px}header p{max-width:950px;color:#adbed1;line-height:1.5}main{display:grid;grid-template-columns:repeat(auto-fit,minmax(310px,1fr));gap:18px}.card{background:#192638;border:1px solid #35465e;border-radius:8px;overflow:hidden;text-decoration:none;color:inherit}.card:hover{border-color:#8cc8e7}.card img{width:100%;height:230px;object-fit:contain;background:#0c131d}.body{padding:16px}h2{font-size:17px;margin:0 0 10px}.body p{font-size:13px;color:#acbfd3}.pass{color:#9bdfad}.fail{color:#ffc495}a{color:#9bd2fa}</style><header><h1>Local placement laboratory</h1><p>Rooms, functional suites, installations and department cores tested at the sizes shown. Distributed ownership is tested through individual placement units. Local constraints, advisory quality and station integration have separate results. Outside patches represent pending connections.</p><p><a href="matrix.json">Detailed matrix report</a></p></header><main>__CARDS__</main></html>'''
    (out / 'index.html').write_text(document.replace('__CARDS__', ''.join(rows)), encoding='utf-8')
    matrix = {'format': 'ss14-architect-department-matrix', 'schemaVersion': 1,
              'scope': 'Local placement inventory, fixture geometry and boundary approaches; station distribution and native operation await integration.',
              'passed': sum(c['report']['verification']['localLayoutVerified'] for c in cases), 'total': len(cases),
              'qualityProfilesPassed': sum(c['report']['verification'].get('layoutQuality', {}).get('status') == 'passes_profile' for c in cases),
              'stationDistributionVerified': False,
              'cases': [{'department': c['department'], 'size': c['size'], 'path': c['path'], 'report': c['report']} for c in cases]}
    (out / 'matrix.json').write_text(json.dumps(json_ready(matrix), indent=2, allow_nan=False), encoding='utf-8')
    return matrix


def run_matrix(document, departments, scales, output, seed=1, settings=None, aspect=1.0, area=None, scope=None):
    out = Path(output)
    out.mkdir(parents=True, exist_ok=True)
    full = resolve_instances(document, seed)
    cases = []
    for department in departments:
        _, key, _, _ = isolate_department(document, department, seed, full)
        choices = [s for s in placement_scopes(full, key) if scope is None or s['id'] == scope]
        if not choices:
            raise ValueError('The chosen placement scope does not belong to this catalog group.')
        for specimen in choices:
            isolated, owner, _, _ = isolate_scope(full, specimen['id'])
            preferred = scope_bounds(isolated, owner)['preferred']
            for scale in scales:
                target = area if area is not None else round(preferred * scale)
                size = f'{target} tiles' if area is not None else f'{scale:g}x'
                slug = f'{specimen["id"].replace("/", "-")}-{target}-seed{seed}'
                state, report = generate_department(document, department, target, aspect, seed, settings, full,
                                                    scenario=specimen['id'])
                report['testScale'] = scale
                save_run(state, report, out / slug)
                cases.append({'department': department, 'size': size, 'path': slug, 'report': report})
                v = report['verification']
                print(f"{specimen['id']} {target}: {'PASS' if v['localLayoutVerified'] else 'INCOMPLETE'}; {v['inventory']['realizedRooms']}/{v['inventory']['selectedRooms']} rooms, {v['inventory']['realizedFeatures']}/{v['inventory']['selectedFeatures']} features; failures={len(v['hard_failures'])}; quality={v['layoutQuality']['status']}", flush=True)
    return write_gallery(out, cases)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('department', nargs='?', help='Department template ID, e.g. medical, command or service')
    parser.add_argument('--all', action='store_true', help='Test every catalog department')
    parser.add_argument('--program', default=str(Path(__file__).parent / 'catalog/standard-station.yaml'))
    parser.add_argument('--scales', default='0.85,1,1.15', help='Comma-separated fractions of the selected department target')
    parser.add_argument('--area', type=int, help='One explicit requested usable-floor area')
    parser.add_argument('--scope', help='Choose one authored local placement scope from this catalog group')
    parser.add_argument('--aspect', type=float, default=1.0)
    parser.add_argument('--seed', type=int, default=1)
    parser.add_argument('--settings')
    parser.add_argument('--output', default='.tmp/architect3-departments')
    args = parser.parse_args(argv)
    if bool(args.department) == args.all:
        parser.error('Choose one department or --all.')
    document = load_program(args.program)
    targets = department_ids(document) if args.all else [args.department]
    try:
        scales = [1.0] if args.area is not None else [float(v) for v in args.scales.split(',')]
        if not scales or any(not 0 < s <= 3 for s in scales):
            raise ValueError('scales must be between zero and three')
        result = run_matrix(document, targets, scales, args.output, args.seed,
                            load_settings(args.settings), args.aspect, args.area, args.scope)
    except (ValueError, OSError) as error:
        parser.error(str(error))
    print(f"Department gallery: {(Path(args.output)/'index.html').resolve()}")
    return 0 if result['passed'] == result['total'] else 2


if __name__ == '__main__':
    raise SystemExit(main())
