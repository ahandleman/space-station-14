"""End-to-end generation, saved-artifact verification and bounded failure tests."""
import copy
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from contextlib import redirect_stdout
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from architect.run import load_layout, run_generation, save_run, shape_signature
from architect.state import json_ready
from architect.verify import verify_layout
from generate import main
from validate import load_program


class GenerationRunTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.document = load_program(ROOT / 'examples/hierarchy-station.yaml')
        cls.state, cls.report = run_generation(cls.document, seed=1)

    def test_default_fixture_generates_and_saved_geometry_reverifies(self):
        self.assertEqual('completed', self.report['termination'])
        self.assertEqual('verified_for_requested_scope', self.report['candidateStatus'])
        self.assertTrue(self.report['verification']['verified'])
        self.assertEqual([], self.report['verification']['hard_failures'])
        self.assertEqual([], self.report['verification']['unknown_mandatory'])
        self.assertIn('runtime_operation', self.report['notEvaluated'])
        with tempfile.TemporaryDirectory() as directory:
            save_run(self.state, self.report, directory)
            out = Path(directory)
            for name in ('layout.json', 'report.json', 'contracts.json', 'index.html'):
                self.assertTrue((out / name).is_file(), name)
            restored = load_layout(out / 'layout.json')
            self.assertTrue(verify_layout(restored)['verified'])
            self.assertEqual(self.report['shapeSignature'], shape_signature(restored))
            with redirect_stdout(io.StringIO()):
                self.assertEqual(0, main(['--verify', str(out / 'layout.json')]))

    def test_same_seed_reproduces_geometry_bindings_and_signature(self):
        state, report = run_generation(self.document, seed=1)
        self.assertEqual(self.report['shapeSignature'], report['shapeSignature'])
        self.assertEqual(self.report['attempts'], report['attempts'])
        self.assertEqual(json_ready(self.state.floors), json_ready(state.floors))
        self.assertEqual(json_ready(self.state.interfaces), json_ready(state.interfaces))
        self.assertEqual(self.state.program.relationships, state.program.relationships)

    def test_zero_budget_returns_no_layout_without_false_infeasibility(self):
        with patch('architect.run.generate_layout', side_effect=AssertionError('Packing must not run')):
            state, report = run_generation(self.document, {'max_proposals': 0})
        self.assertEqual('budget_exhausted', report['termination'])
        self.assertEqual('none', report['candidateStatus'])
        self.assertEqual(0, report['workUsed'])
        self.assertEqual({}, state.floors)
        self.assertFalse(report['verification']['verified'])

    def test_full_catalog_capability_rejection_precedes_packing(self):
        document = load_program(ROOT / 'catalog/standard-station.yaml')
        with patch('architect.run.generate_layout', side_effect=AssertionError('Unsupported full program must not pack')):
            state, report = run_generation(document)
        self.assertEqual('unsupported_required', report['termination'])
        self.assertEqual('none', report['candidateStatus'])
        self.assertEqual(0, report['workUsed'])
        self.assertTrue(report['capabilityGaps'])
        self.assertTrue(report['verification']['unknown_mandatory'])
        self.assertEqual({}, state.floors)

    def test_candidate_only_preserves_unknown_local_extension(self):
        document = copy.deepcopy(self.document)
        document['extensions'].append(dict(id='x_fixture_operation', appliesTo='node',
            description='An intentionally unsupported operation.', evaluator='fixture.operation',
            parametersSchema=dict(type='object', properties={}, additionalProperties=False)))
        document['spaces'][1]['spaces'][0]['spaces'][0]['requirements'].append(
            dict(id='operation', type='x_fixture_operation', parameters={}))
        state, report = run_generation(document, {'root_restarts': 1}, candidate_only=True)
        self.assertTrue(state.floors)
        self.assertEqual('incomplete', report['candidateStatus'])
        unknown = report['verification']['unknown_mandatory']
        self.assertTrue(any(c['type'] == 'x_fixture_operation' for c in unknown))
        self.assertFalse(report['verification']['verified'])
        self.assertTrue(any('hop_office' in c['id'] for c in unknown))

    def test_saved_geometry_mutation_cannot_reuse_success_flags(self):
        with tempfile.TemporaryDirectory() as directory:
            save_run(self.state, self.report, directory)
            path = Path(directory) / 'layout.json'
            payload = json.loads(path.read_text(encoding='utf-8'))
            room = next(key for key, n in self.state.program.spaces.items() if n['kind'] == 'room')
            payload['geometry']['floors'][room] = []
            payload['geometry']['metadata']['verified'] = True
            payload['geometry']['summaries'] = {'claimed': {'verified': True}}
            path.write_text(json.dumps(payload), encoding='utf-8')
            restored = load_layout(path)
            verification = verify_layout(restored)
            self.assertFalse(verification['verified'])
            self.assertTrue(verification['hard_failures'])
            with redirect_stdout(io.StringIO()):
                self.assertEqual(2, main(['--verify', str(path)]))

    def test_saved_source_hash_is_checked_before_verification(self):
        with tempfile.TemporaryDirectory() as directory:
            save_run(self.state, self.report, directory)
            path = Path(directory) / 'layout.json'
            payload = json.loads(path.read_text(encoding='utf-8'))
            payload['sourceDocument']['name'] = 'Changed program'
            path.write_text(json.dumps(payload), encoding='utf-8')
            with self.assertRaisesRegex(ValueError, 'hash'):
                load_layout(path)

    def test_api_rejects_unknown_settings(self):
        with patch('architect.run.generate_layout', side_effect=AssertionError('Invalid settings must not pack')):
            with self.assertRaisesRegex(ValueError, 'Unknown'):
                run_generation(self.document, {'unimplemented_setting': 3})
            with self.assertRaisesRegex(ValueError, 'mapping'):
                run_generation(self.document, [])

    def test_small_budget_is_respected_and_does_not_claim_success(self):
        for budget in (1, 5, 20):
            with self.subTest(budget=budget):
                state, report = run_generation(self.document, {'max_proposals': budget})
                self.assertLessEqual(report['workUsed'], budget)
                self.assertEqual('budget_exhausted', report['termination'])
                self.assertNotEqual('verified_for_requested_scope', report['candidateStatus'])
                self.assertFalse(report['verification']['verified'])
                # JSON consumers, including the browser preview, cannot accept
                # Infinity as a placeholder score for a partial empty candidate.
                json.dumps(report, allow_nan=False)
                with tempfile.TemporaryDirectory() as directory:
                    save_run(state, report, directory)
                    saved = json.loads((Path(directory) / 'report.json').read_text(encoding='utf-8'))
                    self.assertEqual('budget_exhausted', saved['termination'])


if __name__ == '__main__':
    unittest.main()
