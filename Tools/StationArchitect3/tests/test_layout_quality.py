"""A geometric pass must not conceal a long, sparse or corridor-heavy layout."""
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from architect.state import InstanceProgram, LayoutState
from architect.geometry import rect
from architect.quality import layout_quality
from architect.verify import Verifier


def scene():
    p = InstanceProgram()
    p.spaces['d'] = {'id': 'd', 'kind': 'department', 'children': ['r', 'satellite'],
                     'parent': None, 'owner': 'd', 'template': 'd', 'requirements': [],
                     'node': {'organization': {'mode': 'mixed', 'independent': ['d/satellite']}}}
    for key in ('r', 'satellite'):
        p.spaces[key] = {'id': key, 'kind': 'room', 'parent': 'd', 'owner': 'd',
                        'template': 'd/'+key, 'children': [], 'requirements': [],
                        'node': {'tags': [], 'placement': {'zone': 'interior'}}}
    return LayoutState(p, floors={'r': rect(0, 0, 30, 3), 'satellite': rect(100, 100, 3, 3)})


class LayoutQualityTests(unittest.TestCase):
    def test_elongated_feasible_mask_still_needs_review(self):
        s = scene()
        s.program.spaces['d']['children'] = ['r']
        s.floors.pop('satellite')
        q = layout_quality(s, 'd')
        self.assertEqual('needs_review', q['status'])
        aspect = next(c for c in q['checks'] if c['metric'] == 'core.aspect_ratio')
        self.assertEqual(10, aspect['measured'])
        self.assertGreater(aspect['cost'], 0)
        self.assertIn('furnishing_program_completeness', q['notEvaluated'])

    def test_boundary_padding_and_outboard_room_do_not_change_core_quality(self):
        s = scene()
        s.program.spaces['d']['node']['organization'] = {'mode': 'clustered'}
        original = layout_quality(s, 'd')
        s.program.spaces['satellite']['node']['placement']['zone'] = 'outboard'
        s.program.spaces['satellite']['harness'] = True
        s.floors['satellite'] = rect(-1000, -1000, 10, 10)
        s.program.spaces['d']['children'] = ['r']
        expected = layout_quality(s, 'd')
        s.program.spaces['d']['children'].append('satellite')
        actual = layout_quality(s, 'd')
        self.assertEqual(expected, actual)
        self.assertNotEqual(original['metrics']['core.fill_ratio'], actual['metrics']['core.fill_ratio'])

    def test_core_cohesion_exempts_independent_facility_but_full_shape_does_not(self):
        s = scene()
        rule = {'id': 'cohesion', 'type': 'metric', 'owner': 'd', 'priority': 'mandatory',
                'parameters': {'metric': 'core.components', 'min': 1, 'max': 1, 'scale': 1}}
        result = Verifier(s).metric('d', rule)
        self.assertEqual('satisfied', result['status'])
        self.assertEqual(1, result['measured'])
        rule['parameters']['metric'] = 'shape.components'
        self.assertEqual('violated', Verifier(s).metric('d', rule)['status'])

    def test_exterior_installation_quality_does_not_demand_clustering_across_vacuum(self):
        s = scene()
        s.floors['r'] = rect(0, 0, 8, 8)
        s.floors['satellite'] = rect(100, 100, 9, 9)
        s.program.spaces['satellite']['node']['placement']['zone'] = 'outboard'
        s.metadata['placementScope'] = {'kind': 'installation'}
        q = layout_quality(s, 'd')
        self.assertEqual('passes_profile', q['status'])
        self.assertEqual(1, q['metrics']['rooms.mean_fill_ratio'])


if __name__ == '__main__':
    unittest.main()
