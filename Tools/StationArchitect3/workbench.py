#!/usr/bin/env python3
"""Local browser workbench for complete catalog departments and resizing."""
from __future__ import annotations

import argparse
import copy
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import math
import mimetypes
from pathlib import Path
import re
import socket
import threading
from urllib.parse import unquote, urlsplit
import uuid

from validate import load_program
from architect.departments import department_ids, generate_department, isolate_department
from architect.department_packing import department_area_bounds
from architect.instances import resolve_instances
from architect.run import save_run
from architect.settings import load_settings
from architect.state import json_ready
from architect.scenarios import placement_scopes, isolate_scope, scope_bounds, organization

ROOT = Path(__file__).resolve().parent
WEB = ROOT / 'web'
ARTIFACTS = {'index.html', 'layout.svg', 'layout.json', 'report.json', 'contracts.json'}


class WorkbenchServer(ThreadingHTTPServer):
    # HTTPServer enables SO_REUSEADDR, which on Windows lets older workbench
    # processes share a port and randomly serve incompatible API versions.
    allow_reuse_address = False

    def server_bind(self):
        if hasattr(socket, 'SO_EXCLUSIVEADDRUSE'):
            self.socket.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        super().server_bind()


class APIError(ValueError):
    def __init__(self, message, status=400):
        super().__init__(message)
        self.status = status


def _selection(state):
    return ({k for k, n in state.program.spaces.items() if not n.get('harness')},
            set(state.program.features))


def _abstract(feature):
    return not feature['node'].get('prototypes') and not feature['node'].get('assembly')


def _inventory(state):
    result = []
    for key, node in state.program.spaces.items():
        if node.get('harness') or node['kind'] not in ('room', 'passage'):
            continue
        features = []
        for fid, feature in state.program.features.items():
            if feature['room'] == key:
                features.append({'id': fid, 'name': feature.get('node', {}).get('name', fid.rsplit('#', 1)[-1]),
                                 'prototype': feature.get('prototype'),
                                 'assembly': feature.get('node', {}).get('assembly'),
                                 'abstract': _abstract(feature),
                                 'placed': fid in state.features,
                                 'nativeParts': [{k: part[k] for k in ('id', 'prototype') if k in part}
                                                 for part in state.features.get(fid, {}).get('prototype_parts', [])],
                                 'layer': state.features.get(fid, {}).get('layer')})
        result.append({'id': key, 'name': node['node'].get('name', key),
                       'template': node['template'], 'area': len(state.floors.get(key, set())),
                       'physicalFeatures': sum(not f['abstract'] for f in features),
                       'abstractServices': sum(f['abstract'] for f in features),
                       'features': features})
    return result


class DepartmentWorkbench:
    """One source document, explicit output root, and transactional visible state."""

    def __init__(self, document, output, settings=None):
        self.document = copy.deepcopy(document)
        self.output = Path(output).resolve()
        self.output.mkdir(parents=True, exist_ok=True)
        self.settings = load_settings() if settings is None else dict(settings)
        self.ids = department_ids(self.document)
        self.lock = threading.RLock()
        self.accepted = {}
        self.resolved = {}
        self._catalog = None

    def _resolved(self, seed):
        if seed not in self.resolved:
            self.resolved[seed] = resolve_instances(self.document, seed=seed)
            # Keep the development UI from accumulating a full catalog forever
            # when repeatedly trying new seeds. Accepted states remain separate.
            while len(self.resolved) > 8:
                self.resolved.pop(next(iter(self.resolved)))
        return self.resolved[seed]

    def catalog(self):
        with self.lock:
            if self._catalog is None:
                full = self._resolved(1)
                rows = []
                for department in self.ids:
                    local, key, contracts, _ = isolate_department(self.document, department, resolved=full)
                    bounds = department_area_bounds(local, key)
                    rooms = [n for n in local.spaces.values() if n['kind'] in ('room', 'passage')]
                    abstract = sum(_abstract(f) for f in local.features.values())
                    scopes = []
                    for scope in placement_scopes(full, key):
                        specimen, specimen_owner, ports, _ = isolate_scope(full, scope['id'])
                        sb = scope_bounds(specimen, specimen_owner)
                        scope_rooms = [n for n in specimen.spaces.values() if n['kind'] in ('room', 'passage')]
                        demands = sum(_abstract(f) for f in specimen.features.values())
                        scopes.append({**{k: v for k, v in scope.items() if k != 'roots'},
                                       'bounds': {k: sb[k] for k in ('min', 'preferred', 'max')},
                                       'selectedRooms': len(scope_rooms),
                                       'selectedFeatures': len(specimen.features)-demands,
                                       'abstractServices': demands, 'externalConnections': len(ports),
                                       'rooms': [{'id': n['id'], 'name': n['node'].get('name', n['id']),
                                                  'kind': n['kind']} for n in scope_rooms]})
                    rows.append({'id': department, 'name': local.spaces[key]['node'].get('name', department),
                                 'organization': organization(local.spaces[key]['node']), 'scopes': scopes,
                                 'bounds': {k: bounds[k] for k in ('min', 'preferred', 'max')},
                                 'selectedRooms': len(rooms), 'selectedFeatures': len(local.features) - abstract,
                                 'abstractServices': abstract, 'totalDeclaredFeatures': len(local.features),
                                 'externalConnections': len(contracts),
                                 'rooms': [{'id': n['id'], 'name': n['node'].get('name', n['id']),
                                            'kind': n['kind']} for n in rooms]})
                self._catalog = {'name': self.document.get('name', 'Station catalog'),
                                 'sourceHash': full.source_hash, 'departments': rows,
                                 'scope': 'Department layout and inventory. Station connections and native operation remain separate checks.'}
            return copy.deepcopy(self._catalog)

    def validate_request(self, payload):
        if not isinstance(payload, dict):
            raise APIError('Request must be a JSON object.')
        unknown = set(payload) - {'department', 'scope', 'seed', 'area', 'aspect'}
        if unknown:
            raise APIError('Unknown request fields: ' + ', '.join(sorted(unknown)))
        department = payload.get('department')
        if department not in self.ids:
            raise APIError('Choose a department from the configured catalog.')
        scope = self._scope_id(department, payload.get('scope'))
        seed = payload.get('seed', 1)
        if type(seed) is not int or not 0 <= seed <= 2**32-1:
            raise APIError('Seed must be an integer from 0 to 4294967295.')
        area = payload.get('area')
        if area is not None and (type(area) is not int or not 1 <= area <= 1_000_000):
            raise APIError('Usable floor area must be a positive integer at most 1000000.')
        aspect = payload.get('aspect', 1.0)
        if type(aspect) not in (int, float) or not math.isfinite(aspect) or not 0.25 <= aspect <= 4:
            raise APIError('Aspect must be a finite number between 0.25 and 4.')
        return department, scope, seed, area, float(aspect)

    def _scope_id(self, department, scope):
        owner = next(d for d in self.catalog()['departments'] if d['id'] == department)
        if scope is None and owner['organization']['mode'] != 'distributed':
            scope = owner['scopes'][0]['id'] if owner['scopes'] else None
        if not isinstance(scope, str) or scope not in {s['id'] for s in owner['scopes']}:
            raise APIError('Choose a local placement test from the selected catalog group.')
        return scope

    def _save(self, state, report):
        run_id = 'run-' + uuid.uuid4().hex
        directory = self.output / run_id
        save_run(state, report, directory)
        return {'id': run_id, 'svgUrl': '/runs/' + run_id + '/layout.svg',
                'previewUrl': '/runs/' + run_id + '/index.html',
                'reportUrl': '/runs/' + run_id + '/report.json',
                'layoutUrl': '/runs/' + run_id + '/layout.json',
                'report': copy.deepcopy(report), 'rooms': _inventory(state)}

    def generate(self, payload, resize=False):
        department, scope, seed, area, aspect = self.validate_request(payload)
        with self.lock:
            cache_key = (department, scope, seed)
            previous = self.accepted.get(cache_key)
            if resize and previous is None:
                raise APIError('Generate a department that passes its layout checks before resizing it.', 409)
            # Generate a real proposal so failed geometry and its report remain
            # paired. resize_department returns the prior state on failure, which
            # must not be saved beside the rejected proposal's checks.
            state, report = generate_department(self.document, department, area, aspect, seed,
                                                self.settings, resolved=self._resolved(seed), scenario=scope)
            report = copy.deepcopy(report)
            preserved = not resize or _selection(state) == _selection(previous['state'])
            verified = bool(report['verification'].get('localLayoutVerified', report['verification'].get('verified')))
            accepted = verified and preserved
            if resize:
                report['resizeAccepted'] = accepted
                report['selectionPreserved'] = preserved
                if not preserved:
                    failure = {'id': 'workbench::resize_selection', 'type': 'selection', 'priority': 'mandatory',
                               'status': 'violated', 'owner': report.get('department'),
                               'message': 'Resizing changed the selected rooms or features; the previous layout is retained.'}
                    report['verification']['checks'].append(failure)
                    report['verification']['hard_failures'].append(failure)
                    report['verification']['verified'] = False
                    report['verification']['localLayoutVerified'] = False
                    report['candidateStatus'] = 'incomplete_department'
            proposal = self._save(state, report)
            if accepted:
                self.accepted[cache_key] = {'state': state, 'public': proposal}
            retained = not accepted and previous is not None
            visible = previous['public'] if retained else proposal
            return {'action': 'resize' if resize else 'generate', 'accepted': accepted,
                    'retainedPrevious': retained, 'canResize': accepted or retained,
                    'department': department, 'scope': scope, 'seed': seed,
                    'proposal': proposal, 'visible': visible}

    def static_path(self, request_path):
        path = unquote(urlsplit(request_path).path)
        names = {'/': 'departments.html', '/departments.css': 'departments.css', '/departments.js': 'departments.js'}
        if path in names:
            return WEB / names[path]
        match = re.fullmatch(r'/runs/(run-[0-9a-f]{32})/([a-z.]+)', path)
        if not match or match[2] not in ARTIFACTS:
            return None
        target = (self.output / match[1] / match[2]).resolve()
        return target if target.is_relative_to(self.output) else None


def handler_for(workbench):
    class Handler(BaseHTTPRequestHandler):
        server_version = 'StationArchitectDepartmentWorkbench/1'

        def send_json(self, value, status=200):
            body = json.dumps(json_ready(value), allow_nan=False).encode('utf-8')
            self.send_response(status)
            self.send_header('Content-Type', 'application/json; charset=utf-8')
            self.send_header('Cache-Control', 'no-store')
            self.send_header('X-Content-Type-Options', 'nosniff')
            self.send_header('Content-Length', str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            if urlsplit(self.path).path == '/api/catalog':
                try:
                    return self.send_json(workbench.catalog())
                except (ValueError, OSError) as error:
                    return self.send_json({'error': str(error)}, 500)
            path = workbench.static_path(self.path)
            if path is None or not path.is_file():
                return self.send_json({'error': 'Not found.'}, 404)
            body = path.read_bytes()
            self.send_response(200)
            content_type = mimetypes.guess_type(path.name)[0] or 'application/octet-stream'
            if content_type.startswith('text/') or content_type in ('application/javascript', 'application/json', 'image/svg+xml'):
                content_type += '; charset=utf-8'
            self.send_header('Content-Type', content_type)
            self.send_header('X-Content-Type-Options', 'nosniff')
            self.send_header('Cache-Control', 'no-store')
            self.send_header('Content-Length', str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_POST(self):
            # Drain bounded bodies even for rejected requests. Closing a Windows
            # socket with unread JSON can reset the connection before the client
            # receives the useful 4xx response.
            try:
                length = int(self.headers.get('Content-Length', '0'))
            except ValueError:
                length = -1
            body = self.rfile.read(length) if 0 < length <= 16384 else b''
            path = urlsplit(self.path).path
            if path not in ('/api/generate', '/api/resize'):
                return self.send_json({'error': 'Not found.'}, 404)
            port = self.server.server_address[1]
            origin = self.headers.get('Origin')
            if origin and origin not in (f'http://127.0.0.1:{port}', f'http://localhost:{port}'):
                return self.send_json({'error': 'Requests must come from this local workbench.'}, 403)
            if self.headers.get('Content-Type', '').split(';', 1)[0].strip().lower() != 'application/json':
                return self.send_json({'error': 'Use application/json.'}, 415)
            try:
                if not 0 < length <= 16384:
                    raise APIError('Request body must contain at most 16384 bytes of JSON.')
                payload = json.loads(body.decode('utf-8'))
                self.send_json(workbench.generate(payload, resize=path == '/api/resize'))
            except APIError as error:
                self.send_json({'error': str(error)}, error.status)
            except (ValueError, UnicodeError) as error:
                self.send_json({'error': str(error)}, 400)
            except (OSError, RuntimeError) as error:
                self.send_json({'error': 'Generation could not complete: ' + str(error)}, 500)

        def log_message(self, pattern, *args):
            # The CLI stays useful while repeated generation writes large reports.
            if args and str(args[1] if len(args) > 1 else '').startswith(('4', '5')):
                super().log_message(pattern, *args)
    return Handler


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--program', type=Path, default=ROOT / 'catalog/standard-station.yaml')
    parser.add_argument('--settings', type=Path)
    parser.add_argument('--output', type=Path, default=Path('.tmp/architect3-workbench'))
    parser.add_argument('--port', type=int, default=8435)
    args = parser.parse_args(argv)
    if not 0 <= args.port <= 65535:
        parser.error('--port must be between 0 and 65535')
    workbench = DepartmentWorkbench(load_program(args.program), args.output, load_settings(args.settings))
    server = WorkbenchServer(('127.0.0.1', args.port), handler_for(workbench))
    print(f'Department workbench: http://127.0.0.1:{server.server_address[1]}', flush=True)
    print(f'Catalog: {args.program.resolve()}', flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
