#!/usr/bin/env python3
"""Generate and independently inspect a hierarchical Station Architect 3 layout."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys

from validate import load_program, ProgramError
from architect.run import load_layout, run_generation, save_run
from architect.settings import load_settings
from architect.state import json_ready
from architect.verify import verify_layout


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('program', nargs='?', default=str(Path(__file__).parent / 'examples/station-layout.yaml'))
    parser.add_argument('--settings', help='Runtime generation settings YAML')
    parser.add_argument('--seed', type=int, default=1)
    parser.add_argument('--output', default='.tmp/architect3-layout')
    parser.add_argument('--candidate-only', action='store_true', help='Allow incomplete previews with explicitly unsupported requirements')
    parser.add_argument('--verify', metavar='LAYOUT_JSON', help='Recompute checks from a saved layout and its original program')
    parser.add_argument('--json', action='store_true', help='Emit the full verification/run report')
    args = parser.parse_args(argv)
    try:
        if args.verify:
            state = load_layout(args.verify)
            if state.metadata.get('departmentInstance'):
                from architect.departments import verify_department
                report = verify_department(state, state.metadata['departmentInstance'])
            else:
                report = verify_layout(state)
            print(json.dumps(json_ready(report), indent=2) if args.json else
                  f"Verified: {report['verified']}; failures: {len(report['hard_failures'])}; unknown mandatory: {len(report['unknown_mandatory'])}")
            return 0 if report['verified'] else 2
        document = load_program(args.program)
        settings = load_settings(args.settings)
        state, report = run_generation(document, settings, args.seed, args.candidate_only)
        save_run(state, report, args.output)
        if args.json:
            print(json.dumps(json_ready(report), indent=2))
        else:
            print(f"{report['candidateStatus']} / {report['termination']}")
            print(f"Rooms/spaces: {report['selectedInstances']}; work: {report['workUsed']}; hard failures: {len(report['verification']['hard_failures'])}; unknown mandatory: {len(report['verification']['unknown_mandatory'])}")
            print(f"Preview: {(Path(args.output) / 'index.html').resolve()}")
            print('Native map realization and runtime operation have not been evaluated.')
        return 0 if report['candidateStatus'].startswith('verified') else 2
    except (ProgramError, ValueError, OSError) as error:
        print(f'Generation failed: {error}', file=sys.stderr)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
