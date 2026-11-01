"""Repair scope, atomic rollback and obligation protection on actual tile masks."""
from pathlib import Path
import copy
import sys
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from architect.geometry import rect
from architect.repair import RepairTransaction, rebuild_walls, repair_area, repair_feature, publish_contracts
from architect.settings import DEFAULTS
from architect.state import InstanceProgram, LayoutState
from architect.verify import verify_layout


def room(key, minimum=1, maximum=100, parent='d'):
    return {'id': key, 'template': key, 'kind': 'room', 'parent': parent, 'owner': parent,
            'children': [], 'node': {'placement': {'nesting': 'owned', 'zone': 'interior', 'grid': 'station'}, 'tags': []},
            'requirements': [{'id': key+'::area', 'type': 'metric', 'owner': key, 'priority': 'mandatory',
                              'weight': 100, 'tier': 0,
                              'parameters': {'metric': 'area.exclusive', 'min': minimum, 'max': maximum, 'scale': 1}}]}


def example(a_min=1, b=True):
    p = InstanceProgram()
    p.spaces['d'] = {'id': 'd', 'kind': 'department', 'parent': None, 'owner': 'd',
                     'children': ['a', 'b'] if b else ['a'], 'node': {'tags': []}, 'requirements': []}
    p.spaces['a'] = room('a', a_min)
    if b:
        p.spaces['b'] = room('b', 20)
    state = LayoutState(p, floors={'a': rect(0, 0, 4, 5)}, bounds=(-5, -5, 20, 20))
    if b:
        state.floors['b'] = rect(5, 0, 6, 5)
    rebuild_walls(state)
    return state


class RepairTests(unittest.TestCase):
    def test_room_local_fill_never_changes_sibling(self):
        state = example(20)
        state.floors['a'].remove((2, 2))
        state.envelopes['a'] = rect(0, 0, 4, 5)
        original_b = set(state.floors['b'])
        result = repair_area(state, 'a', 20, DEFAULTS)
        self.assertEqual('repaired', result.status)
        self.assertEqual('a', result.scope)
        self.assertEqual(original_b, result.state.floors['b'])
        self.assertEqual(19, len(state.floors['a']))
        self.assertTrue(verify_layout(result.state)['verified'])
        self.assertFalse(any(e['event'] == 'repair_escalated' for e in result.state.trace))

    def test_sibling_exchange_preserves_department_total(self):
        state = example(22)
        result = repair_area(state, 'a', 22, DEFAULTS)
        self.assertEqual('repaired', result.status)
        self.assertEqual('d', result.scope)
        self.assertEqual(22, len(result.state.floors['a']))
        self.assertEqual(28, len(result.state.floors['b']))
        self.assertEqual(50, len(result.state.aggregate('d')))
        self.assertTrue(verify_layout(result.state)['verified'])

    def test_root_requested_when_parent_has_no_capacity(self):
        state = example(22, b=False)
        state.envelopes['d'] = set(state.floors['a']) | state.walls
        result = repair_area(state, 'a', 22, DEFAULTS)
        self.assertEqual('repaired', result.status)
        self.assertIsNone(result.scope)
        self.assertTrue(any(e['event'] == 'repair_escalated' and e['from'] == 'd' for e in result.state.trace))
        self.assertTrue(verify_layout(result.state)['verified'])

    def test_transaction_restores_all_original_geometry(self):
        state = example()
        before = copy.deepcopy(state)
        tx = RepairTransaction(state, 'd', 'deliberately overlap')
        tx.candidate.floors['a'].add((6, 1))
        tx.candidate.summaries.clear()
        result, failure = tx.commit()
        self.assertIsNone(result)
        self.assertTrue(failure['lostProtected'])
        self.assertEqual(before, state)

    def test_transaction_cannot_change_an_unrelated_room(self):
        state = example()
        tx = RepairTransaction(state, 'a', 'wrong authority')
        tx.candidate.floors['b'].remove((6, 1))
        result, failure = tx.commit()
        self.assertIsNone(result)
        self.assertIn('b', failure['outsideRepairAuthority'])

    def test_transaction_cannot_move_remote_structure_or_envelope(self):
        state = example()
        tx = RepairTransaction(state, 'a', 'remote wall')
        tx.candidate.walls.add((18, 18))
        result, failure = tx.commit()
        self.assertIsNone(result)
        self.assertTrue(failure['outsideRepairAuthority'])
        tx = RepairTransaction(state, 'a', 'remote lease')
        tx.candidate.envelopes['b'] = rect(5, 0, 8, 5)
        result, failure = tx.commit()
        self.assertIsNone(result)
        self.assertIn('b', failure['outsideRepairAuthority'])

    def test_transaction_cannot_rewrite_requirement_to_pass(self):
        state = example(22)
        tx = RepairTransaction(state, 'a', 'waive the minimum')
        tx.candidate.program.spaces['a']['requirements'][0]['parameters']['min'] = 1
        result, failure = tx.commit()
        self.assertIsNone(result)
        self.assertTrue(failure['authoredProgramChanged'])
        self.assertEqual(22, state.program.spaces['a']['requirements'][0]['parameters']['min'])

    def test_missing_department_lease_escalates_to_station(self):
        state = example(22, b=False)
        result = repair_area(state, 'a', 22, DEFAULTS)
        self.assertEqual('repaired', result.status)
        self.assertIsNone(result.scope)

    def test_invalidation_cannot_erase_a_protected_rule(self):
        state = example()
        tx = RepairTransaction(state, 'd', 'remove floor and invalidate summaries')
        tx.candidate.floors.pop('a')
        tx.candidate.summaries.clear()
        result, failure = tx.commit()
        self.assertIsNone(result)
        self.assertIn('floor:a', failure['lostProtected'])

    def test_large_area_request_cannot_bypass_maximum(self):
        state = example()
        state.program.spaces['a']['requirements'][0]['parameters']['max'] = 20
        result = repair_area(state, 'a', 21, {**DEFAULTS, 'local_proposals': 30})
        self.assertNotEqual('repaired', result.status)
        self.assertEqual(20, len(state.floors['a']))

    def test_zero_budget_is_not_infeasibility(self):
        state = example(22)
        result = repair_area(state, 'a', 22, {**DEFAULTS, 'local_proposals': 0})
        self.assertEqual('budget_exhausted', result.status)
        self.assertEqual(0, result.attempts)

    def test_feature_relocation_only_changes_local_pose(self):
        state = example()
        state.program.features['a#box'] = {'room': 'a', 'node': {}}
        state.features['a#box'] = {'room': 'a', 'cells': {(4, 2)}, 'clearance': {(4, 3)}}
        original = copy.deepcopy(state.floors)
        result = repair_feature(state, 'a#box', DEFAULTS)
        self.assertEqual('repaired', result.status)
        self.assertEqual('a', result.scope)
        self.assertEqual(original, result.state.floors)
        self.assertTrue(verify_layout(result.state)['verified'])

    def test_contracts_keep_external_rule_local(self):
        state = example()
        state.program.relationships.append({'id': 'a::needs_b', 'owner': 'a', 'source': 'a', 'target': 'b',
                                            'type': 'dependency', 'priority': 'mandatory', 'parameters': {}})
        summaries = publish_contracts(state, verify_layout(state))
        self.assertEqual('a', summaries['a']['externalNeeds'][0]['declaredAt'])
        self.assertFalse(summaries['d']['externalNeeds'])
        self.assertEqual(50, summaries['d']['aggregateArea'])


if __name__ == '__main__':
    unittest.main()
