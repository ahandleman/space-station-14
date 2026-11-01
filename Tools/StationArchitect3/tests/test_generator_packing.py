"""End-to-end constructive geometry and deterministic alternative checks."""
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from validate import load_program
from architect.instances import resolve_instances
from architect.packing import generate_layout
from architect.settings import DEFAULTS
from architect.state import json_ready
from architect.verify import verify_layout


def normalized_shapes(state):
    shapes = []
    for key, cells in sorted(state.floors.items()):
        if state.program.spaces[key]['kind'] != 'room':
            continue
        variants = []
        for reflect in (1, -1):
            for turns in range(4):
                points = []
                for x, y in cells:
                    x *= reflect
                    for _ in range(turns):
                        x, y = -y, x
                    points.append((x, y))
                x0, y0 = min(x for x, y in points), min(y for x, y in points)
                variants.append(tuple(sorted((x - x0, y - y0) for x, y in points)))
        shapes.append((key, min(variants)))
    return tuple(shapes)


class HierarchyPackingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.program = resolve_instances(load_program(ROOT / 'examples/hierarchy-station.yaml'))

    def test_representative_seeds_independently_verify(self):
        for seed in (1, 3, 8, 11, 15, 20):
            with self.subTest(seed=seed):
                state = generate_layout(self.program, DEFAULTS, seed)
                report = verify_layout(state)
                self.assertTrue(report['verified'], report['hard_failures'])
                self.assertFalse(report['unknown_mandatory'])
                self.assertGreater(report['metrics']['mean_wrap'], 0.35)

    def test_seed_is_reproducible_and_variety_survives_symmetry(self):
        first = generate_layout(self.program, DEFAULTS, 1)
        repeated = generate_layout(self.program, DEFAULTS, 1)
        self.assertEqual(json_ready(first.floors), json_ready(repeated.floors))
        self.assertEqual(json_ready(first.interfaces), json_ready(repeated.interfaces))
        self.assertEqual(first.trace, repeated.trace)
        signatures = {normalized_shapes(generate_layout(self.program, DEFAULTS, seed)) for seed in (1, 2, 3, 4)}
        self.assertGreater(len(signatures), 1)

    def test_room_growth_really_adds_coherent_free_patches(self):
        state = generate_layout(self.program, DEFAULTS, 1)
        changes = [t for t in state.trace if t.get('operation') == 'free_boundary_patch_growth']
        self.assertTrue(changes)
        for change in changes:
            self.assertGreater(change['areaAfter'], change['areaBefore'])
            self.assertEqual(change['areaAfter'] - change['areaBefore'], len(change['cells']))
            self.assertIn(change['scope'], change['pressures'])
            self.assertTrue(set(map(tuple, change['cells'])) <= state.floors[change['scope']])

    def test_alternative_offers_remain_correlated_and_unverified(self):
        state = generate_layout(self.program, DEFAULTS, 2)
        offers = state.metadata['construction_alternatives']
        self.assertTrue(offers)
        for offer in offers:
            self.assertEqual(offer['frame'], 'suite_local')
            self.assertEqual(offer['verification'], 'unverified_proposal')
            self.assertEqual(offer['sourceHash'], self.program.source_hash)
            self.assertLessEqual(len(offer['alternatives']), DEFAULTS['parent_alternatives'])
            for alternative in offer['alternatives']:
                for room, floor in alternative['floors'].items():
                    self.assertEqual(len(floor), alternative['floorAreas'][room])

    def test_budget_exhaustion_is_explicit_and_bounded(self):
        state = generate_layout(self.program, {**DEFAULTS, 'max_proposals': 0}, 1)
        self.assertEqual(state.metadata['termination'], 'budget_exhausted')
        self.assertEqual(state.metadata['work_used'], 0)
        self.assertFalse(verify_layout(state)['verified'])
        self.assertNotIn('proven_infeasible', str(state.metadata))

    def test_large_gaps_still_use_narrow_connected_passages(self):
        for width, gap in ((2, 8), (3, 5), (3, 8)):
            with self.subTest(width=width, gap=gap):
                state = generate_layout(self.program, {**DEFAULTS, 'maintenance_width': width,
                                                       'department_gap': gap}, 1)
                report = verify_layout(state)
                self.assertTrue(report['verified'], report['hard_failures'])


if __name__ == '__main__':
    unittest.main()
