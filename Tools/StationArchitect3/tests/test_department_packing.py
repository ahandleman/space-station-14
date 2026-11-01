"""Full selected departments retain their local program at different floor budgets."""
import copy
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from validate import load_program
from architect.department_packing import pack_department, department_area_bounds, _contact_graph
from architect.departments import department_ids, isolate_department, generate_department
from architect.geometry import bbox, components
from architect.instances import resolve_instances
from architect.verify import Verifier
from architect.quality import layout_quality
from architect.scenarios import all_placement_scopes, isolate_scope, scope_bounds


class DepartmentPackingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.document = load_program(ROOT / 'catalog/standard-station.yaml')
        cls.full = resolve_instances(cls.document, seed=1)

    def local(self, department):
        program, key, _, _ = isolate_department(self.document, department, resolved=self.full)
        return program, key

    def assert_owned_geometry(self, state, key, target):
        rooms = {k for k in state.program.descendants(key, True)
                 if state.program.spaces[k]['kind'] in ('room', 'passage')}
        self.assertEqual('completed', state.metadata['termination'], state.trace)
        self.assertEqual(rooms, set(state.floors))
        self.assertEqual(target, len(state.aggregate(key)))
        self.assertEqual(target, sum(map(len, state.floors.values())), 'Owned floor may not overlap.')
        bounds = department_area_bounds(state.program, key)['rooms']
        for room, floor in state.floors.items():
            self.assertEqual(1, len(components(floor)), room)
            self.assertGreaterEqual(len(floor), bounds[room]['min'], room)
            self.assertLessEqual(len(floor), bounds[room]['max'], room)
        verifier = Verifier(state)
        for interface in state.interfaces:
            self.assertTrue(verifier.interface_valid(interface), interface)
        for edge in state.program.relationships:
            if (edge['type']=='route' and edge.get('priority')=='mandatory'
                    and edge['parameters'].get('mode')=='crew'
                    and edge['source'] in rooms and edge['target'] in rooms):
                self.assertIsNotNone(verifier.route(edge['source'],edge['target'],edge['parameters']),edge['id'])

    def test_authored_placement_scopes_preserve_exact_budgets_and_inventory(self):
        """Ownership groups do not imply that every member must share one site.

        Exercise every actual placement unit, including the Chapel suite and
        solar installations. Cohesive departments and mixed-owner cores also
        exercise compact and expanded budgets here; the integration matrix
        exercises all three budgets for every room and suite as well.
        """
        for scope in all_placement_scopes(self.full):
            program, key, _, _ = isolate_scope(self.full, scope['id'])
            program.placement_scope = scope
            before = copy.deepcopy(program)
            preferred = scope_bounds(program, key)['preferred']
            factors = (.85, 1, 1.15) if scope['kind'] in ('department', 'core') else (1,)
            masks = []
            for factor in factors:
                target = round(preferred * factor)
                with self.subTest(scope=scope['id'], target=target):
                    state = pack_department(program, key, target, seed=1,
                                            settings={'max_proposals': 100000})
                    self.assert_owned_geometry(state, key, target)
                    self.assertEqual(set(before.features), set(state.program.features))
                    self.assertEqual(before.spaces, state.program.spaces)
                    self.assertEqual(before.relationships, state.program.relationships)
                    masks.append(state.aggregate(key))
            self.assertEqual(len(factors), len({frozenset(m) for m in masks}))

    def test_outside_hard_bounds_is_distinct_from_search_failure(self):
        program, key = self.local('command')
        bounds = department_area_bounds(program, key)
        for target in (bounds['min'] - 1, bounds['max'] + 1):
            state = pack_department(program, key, target)
            self.assertEqual('infeasible_bounds', state.metadata['termination'])
            self.assertFalse(state.floors)
        exhausted = pack_department(program, key, settings={'max_proposals': 0})
        self.assertEqual('budget_exhausted', exhausted.metadata['termination'])
        self.assertIn('not an infeasibility proof', exhausted.metadata['reason'])

    def test_busy_real_room_is_not_inferred_to_be_a_corridor(self):
        program, key = self.local('security')
        roots = [k for k in program.descendants(key, True)
                 if program.spaces[k]['kind'] in ('room', 'passage')]
        _, circulation = _contact_graph(program, key, roots)
        self.assertEqual(2, len(circulation))
        self.assertTrue(all('department_circulation' in program.spaces[k]['node']['tags']
                            for k in circulation))
        self.assertFalse(any('/brig[' in k for k in circulation))

    def test_dense_native_inventory_receives_space_without_changing_source_bounds(self):
        program, key = self.local('maintenance_rooms')
        before = copy.deepcopy(program.spaces)
        state = pack_department(program, key, 287)
        arrays = [k for k, n in program.spaces.items() if n['template'] == 'maintenance_rooms/solar_array']
        self.assertEqual(2, len(arrays))
        for room in arrays:
            self.assertGreaterEqual(len(state.floors[room]), 81)
        self.assertEqual(287, len(state.aggregate(key)))
        self.assertEqual(before, program.spaces)

    def test_aspect_orients_a_complete_solution_and_preserves_interfaces(self):
        program, key = self.local('command')
        wide = pack_department(program, key, aspect=4)
        tall = pack_department(program, key, aspect=.25)
        self.assert_owned_geometry(wide, key, wide.metadata['requested_area'])
        self.assert_owned_geometry(tall, key, tall.metadata['requested_area'])
        x, y, xx, yy = bbox(wide.aggregate(key))
        self.assertGreater(xx-x, yy-y)
        x, y, xx, yy = bbox(tall.aggregate(key))
        self.assertGreater(yy-y, xx-x)
        self.assertEqual({k: len(v) for k, v in wide.floors.items()},
                         {k: len(v) for k, v in tall.floors.items()})

    def test_seed_changes_geometry_without_changing_selected_inventory(self):
        program, key = self.local('medical')
        first = pack_department(program, key, seed=1)
        second = pack_department(program, key, seed=19)
        self.assert_owned_geometry(first, key, first.metadata['requested_area'])
        self.assert_owned_geometry(second, key, second.metadata['requested_area'])
        self.assertNotEqual(first.floors, second.floors)
        self.assertEqual(set(first.program.features), set(second.program.features))

    def test_compact_cores_replace_long_edge_spines_without_losing_local_routes(self):
        # The old feasible proposals had aspect ratios 3.4-3.6 and spent up to
        # 30% of their floor on a long external spine. These are real full
        # programs, not reduced geometric demonstration fixtures.
        for scenario in ('medical', 'science', 'cargo', 'command/core', 'engineering/core'):
            with self.subTest(scenario=scenario):
                program, key, _, scope = isolate_scope(self.full, scenario)
                program.placement_scope = scope
                target = scope_bounds(program, key)['preferred']
                state, report = generate_department(self.document, scope['ownerTemplate'], target,
                                                    scenario=scenario, resolved=self.full)
                self.assertTrue(report['verification']['verified'], report['verification']['hard_failures'])
                self.assertEqual(target, len(state.aggregate(key)))
                self.assertEqual(set(program.features), set(state.program.features))
                quality = layout_quality(state, key)
                self.assertLessEqual(quality['metrics']['core.aspect_ratio'], 2.4)
                self.assertLessEqual(quality['metrics']['circulation.fraction'], .25)
                self.assertTrue(any(t.get('phase') == 'department_candidate_quality'
                                    for t in state.trace))

    def test_distributed_single_corridor_scope_has_no_invented_neighbor_rooms(self):
        scenario = 'service/service_circulation[0]'
        selected = resolve_instances(self.document, seed=1,
                                     counts={'service/service_circulation': 1})
        program, key, _, scope = isolate_scope(selected, scenario)
        program.placement_scope = scope
        target = scope_bounds(program, key)['preferred']
        state = pack_department(program, key, target)
        self.assert_owned_geometry(state, key, target)
        self.assertEqual(1, len(state.floors))
        self.assertEqual('room', layout_quality(state, key)['profile'])


if __name__ == '__main__':
    unittest.main()
