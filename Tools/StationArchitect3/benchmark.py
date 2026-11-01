"""Repeatable supported-layout benchmark; never a native-map certification."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import time

from validate import load_program
from architect.run import run_generation
from architect.settings import load_settings


def partition_signature(state):
    """Ignore labels and canonicalize translation, rotation and reflection.

    This distinguishes actual room-mask partitions, not just renamed rooms.
    Different signatures are geometric variation, not proof of new gameplay loops.
    """
    if not any(state.floors.values()):
        return None
    variants = []
    for flip in (1, -1):
        for rotation in range(4):
            rooms = []
            for cells in state.floors.values():
                transformed = []
                for x, y in cells:
                    x *= flip
                    for _ in range(rotation):
                        x, y = -y, x
                    transformed.append((x, y))
                rooms.append(transformed)
            minx = min(x for cells in rooms for x, y in cells)
            miny = min(y for cells in rooms for x, y in cells)
            normalized = sorted(sorted((x - minx, y - miny) for x, y in cells) for cells in rooms)
            variants.append(json.dumps(normalized, separators=(',', ':')))
    return hashlib.sha256(min(variants).encode()).hexdigest()


def benchmark(document, settings, seeds=20, variants=False):
    jobs = [('default', seed, {}) for seed in range(1, seeds + 1)]
    if variants:
        jobs += [('width3', seed, {'maintenance_width': 3}) for seed in range(1, min(seeds, 3) + 1)]
        jobs += [('gap8', seed, {'department_gap': 8}) for seed in range(1, min(seeds, 3) + 1)]
    started = time.monotonic()
    rows = []
    implementation_hashes = {p.name: hashlib.sha256(p.read_bytes()).hexdigest()
                             for p in sorted((Path(__file__).parent / 'architect').glob('*.py'))}
    for group, seed, extra in jobs:
        tick = time.monotonic()
        state, report = run_generation(document, {**settings, **extra}, seed)
        verified = report['verification']
        metrics = verified['metrics']
        row = {'group': group, 'seed': seed, 'settingsOverrides': extra,
               'verified': verified['verified'], 'termination': report['termination'],
               'candidateStatus': report['candidateStatus'], 'shapeSignature': report['shapeSignature'],
               'unlabeledPartitionSignature': partition_signature(state),
               'selectionSignature': hashlib.sha256(json.dumps(sorted(state.program.spaces)).encode()).hexdigest(),
               'selectedInstances': len(state.program.spaces), 'workUsed': report['workUsed'],
               'elapsedSeconds': round(time.monotonic() - tick, 3),
               'failureIds': [c['id'] for c in verified['hard_failures']],
               'unknownMandatory': [c['id'] for c in verified['unknown_mandatory']]}
        row.update({key: metrics.get(key) for key in ('core_density', 'mean_wrap', 'worst_wrap')})
        rows.append(row)
        print(f"{group} seed={seed}: verified={row['verified']} work={row['workUsed']}", flush=True)
    summary = {}
    for group in sorted({r['group'] for r in rows}):
        batch = [r for r in rows if r['group'] == group]
        accepted = [r for r in batch if r['verified']]
        ranges = {}
        for key in ('core_density', 'mean_wrap', 'worst_wrap', 'workUsed', 'elapsedSeconds'):
            values = [r[key] for r in accepted if r[key] is not None]
            ranges[key] = [min(values), max(values)] if values else None
        summary[group] = {'runs': len(batch), 'verified': len(accepted),
                          'fixedSelection': len({r['selectionSignature'] for r in batch}) == 1,
                          'uniqueCanonicalShapeSignatures': len({r['shapeSignature'] for r in accepted}),
                          'uniqueUnlabeledPartitionSignatures': len({r['unlabeledPartitionSignature'] for r in accepted}),
                          'ranges': ranges}
    return {'format': 'ss14-architect-layout-benchmark', 'schemaVersion': 1,
            'sourceHash': hashlib.sha256(json.dumps(document, sort_keys=True, separators=(',', ':')).encode()).hexdigest(),
            'implementationHashes': implementation_hashes, 'settings': settings,
            'elapsedSeconds': round(time.monotonic() - started, 3), 'summary': summary, 'runs': rows,
            'scope': 'Selected-program layout geometry and traversal only; not native SS14 runtime or full-catalog acceptance.',
            'interpretation': 'Partition signatures ignore room IDs and rigid transforms. They measure geometric variation, not automatically distinct gameplay topologies. Compare packing only within fixed-selection batches. Density includes maintenance floor, so increased corridor area is not by itself improved room packing.'}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--program', type=Path, default=Path(__file__).parent / 'examples/hierarchy-station.yaml')
    parser.add_argument('--settings', type=Path)
    parser.add_argument('--seeds', type=int, default=20)
    parser.add_argument('--variants', action='store_true', help='Also exercise width 3 and department gap 8 for up to three seeds each.')
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args(argv)
    if args.seeds < 1:
        parser.error('--seeds must be positive')
    result = benchmark(load_program(args.program), load_settings(args.settings), args.seeds, args.variants)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps(result['summary'], indent=2))
    return 0 if all(r['verified'] for r in result['runs']) else 2


if __name__ == '__main__':
    raise SystemExit(main())
