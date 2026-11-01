"""Requested custody flow and process-room borders on complete generated cores."""
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from validate import load_program
from architect.instances import resolve_instances
from architect.departments import generate_department, verify_department
from architect.verify import Verifier


class CustodyConnectionIntegrationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.document = load_program(ROOT/'catalog/standard-station.yaml')
        cls.full = resolve_instances(cls.document)
        cls.security, cls.security_report = generate_department(
            cls.document, 'security', scenario='security/core', resolved=cls.full)
        cls.engineering, cls.engineering_report = generate_department(
            cls.document, 'engineering', scenario='engineering/core', resolved=cls.full)

    def test_security_core_has_shared_custody_and_independent_legal_rooms(self):
        state, result = self.security, self.security_report['verification']
        self.assertTrue(result['localLayoutVerified'], result['hard_failures'])
        templates = {n['template'] for n in state.program.spaces.values() if not n.get('harness')}
        self.assertNotIn('security/cell', templates)
        self.assertNotIn('security/courtroom', templates)
        self.assertNotIn('security/law_office', templates)
        self.assertFalse(any(f.get('prototype') == 'BrigTimer' for f in state.program.features.values()))
        bank = state.features['security[0]/sentencing[0]#sentence_lockers[0]']
        self.assertEqual(4, len(bank['prototype_parts']))
        self.assertEqual(4, len({part['prototype'] for part in bank['prototype_parts']}))

    def test_actual_gate_lanes_implement_admission_and_release_without_reentry(self):
        v = Verifier(self.security)
        sentencing, brig = 'security[0]/sentencing[0]', 'security[0]/brig[0]'
        for actor, a, b, reachable in (
                ('genpop_admission', sentencing, brig, True),
                ('genpop_admission', brig, sentencing, False),
                ('genpop_release', brig, sentencing, True),
                ('genpop_release', sentencing, brig, False),
                ('public', sentencing, brig, False)):
            with self.subTest(actor=actor, source=a, target=b):
                path = v.route(a, b, {'accessProfile': actor, 'minWidth': 1, 'within': 'owner'})
                self.assertEqual(reachable, path is not None)
        self.assertIsNotNone(v.route(brig, 'public_hall[0]', {
            'accessProfile': 'genpop_release', 'minWidth': 1, 'within': 'station',
            'through': [sentencing]}), 'Released prisoners must reach public circulation after their property lockers.')
        entry = self.security.features[sentencing+'#brig_entry_gate[0]']
        leave = self.security.features[sentencing+'#brig_exit_gate[0]']
        self.assertNotEqual(entry['interface'], leave['interface'])
        self.assertFalse(entry['cells'] & leave['cells'])

    def test_perma_has_its_controlled_entry_on_sentencing(self):
        v = Verifier(self.security)
        perma, sentencing = 'security[0]/perma[0]', 'security[0]/sentencing[0]'
        matches = v.matching_interfaces(perma, sentencing, 'detention_door')
        self.assertTrue(matches)
        self.assertGreaterEqual(v.seam_measure(matches)[0], 2)
        self.assertIsNotNone(v.route(perma, sentencing, {
            'accessProfile': 'detention', 'minWidth': 2, 'within': 'owner'}))

    def test_atmos_teg_requires_a_real_border_not_just_a_corridor_route(self):
        state, result = self.engineering, self.engineering_report['verification']
        self.assertTrue(result['localLayoutVerified'], result['hard_failures'])
        rule = next(r for r in state.program.relationships if r.get('authoredId') == 'atmos_generator_connection')
        self.assertEqual('satisfied', Verifier(state).relationship(rule)['status'])
        damaged = state.clone()
        matches = Verifier(damaged).matching_interfaces(rule['source'], rule['target'], rule['parameters']['interface'])
        ids = {i['id'] for i in matches}
        damaged.interfaces = [i for i in damaged.interfaces if i['id'] not in ids]
        self.assertEqual('violated', Verifier(damaged).relationship(rule)['status'])
        self.assertFalse(verify_department(damaged, damaged.metadata['departmentInstance'])['localLayoutVerified'])


if __name__ == '__main__':
    unittest.main()
