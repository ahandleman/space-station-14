"""Concrete selection tests: repetition, activation and truthful diagnostics."""
import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from architect.instances import resolve_instances
from architect.settings import load_settings, validate_settings
from validate import load_program


def fixture():
    return load_program(ROOT / 'examples/hierarchy-station.yaml')


def small():
    d = fixture()
    # Keep definitions but use a single, independent repeated department.
    a = copy.deepcopy(d['spaces'][1]['spaces'][0]['spaces'][0])
    a['id'] = 'consumer'; a.pop('relationships', None)
    b = copy.deepcopy(a); b['id'] = 'provider'
    b['count'] = dict(min=0, preferred=0, max=3)
    dep = copy.deepcopy(d['spaces'][1]); dep['id'] = 'division'
    dep['spaces'] = [a, b]; dep['requirements'] = []
    d['spaces'] = [dep]
    return d


def dependency(source='./consumer', target='./provider', mode='one'):
    return dict(id='supply', type='dependency', **{'from': {'space': source}, 'to': {'space': target}},
                matching=dict(targets=mode, scope='same_parent', presence='require_target'),
                parameters=dict(reason='Local supply.'))


class InstanceTests(unittest.TestCase):
    def test_repeated_suite_keeps_actual_ancestor_binding(self):
        p = resolve_instances(fixture())
        edges = [r for r in p.relationships if r['type'] == 'shared_boundary' and 'hospitality' in r['source']]
        self.assertEqual(4, len(edges))
        for r in edges:
            self.assertEqual(r['source'].rsplit('/', 1)[0], r['target'].rsplit('/', 1)[0])
            self.assertEqual(r['owner'], r['source'].rsplit('/', 1)[0])
        self.assertFalse(p.diagnostics)

    def test_resolver_does_not_change_authored_input(self):
        d = fixture(); before = copy.deepcopy(d)
        p = resolve_instances(d)
        self.assertEqual(d, before)
        self.assertEqual(d, p.source)
        p.source['name'] = 'edited snapshot'
        self.assertEqual(d, before)

    def test_contained_access_refs_are_concrete_and_local(self):
        p = resolve_instances(fixture())
        c = p.spaces['service[0]/chapel[0]/confessional[0]']
        r = next(r for r in c['requirements'] if r['type'] == 'access_policy')
        self.assertEqual(['service[0]/chapel[0]'], r['parameters']['mustTraverse'])
        self.assertEqual(['service/chapel'], r['authoredParameters']['mustTraverse'])

    def test_optional_target_activation_per_repeated_parent(self):
        d = small(); dep = d['spaces'][0]; dep['count'] = dict(min=2, preferred=2, max=2)
        dep['relationships'] = [dependency()]
        p = resolve_instances(d)
        self.assertEqual(2, len(p.relationships))
        for r in p.relationships:
            self.assertEqual(r['source'].split('/')[0], r['target'].split('/')[0])
        self.assertFalse(p.diagnostics)

    def test_absent_optional_source_does_not_activate_target(self):
        d = small(); dep = d['spaces'][0]
        dep['spaces'][0]['count'] = dict(min=0, preferred=0, max=1)
        dep['relationships'] = [dependency()]
        p = resolve_instances(d)
        self.assertEqual(1, len(p.spaces))
        self.assertEqual([], p.relationships)

    def test_if_both_present_does_not_force_provider(self):
        d = small(); dep = d['spaces'][0]; dep['relationships'] = [dependency()]
        dep['relationships'][0]['matching']['presence'] = 'if_both_present'
        p = resolve_instances(d)
        self.assertEqual(2, len(p.spaces))
        self.assertEqual([], p.relationships)

    def test_distinct_targets_activate_enough_capacity(self):
        d = small(); dep = d['spaces'][0]
        dep['spaces'][0]['count'] = dict(min=3, preferred=3, max=3)
        dep['relationships'] = [dependency(mode='distinct')]
        p = resolve_instances(d)
        self.assertEqual(3, len(p.relationships))
        self.assertEqual(3, len({r['target'] for r in p.relationships}))
        self.assertFalse(p.diagnostics)

    def test_distinct_capacity_can_expand_provider_parent(self):
        d = small(); dep = d['spaces'][0]
        dep['spaces'][0]['count'] = dict(min=2, preferred=2, max=2)
        provider = dep['spaces'].pop()
        provider['count'] = dict(min=1, preferred=1, max=1)
        dep['spaces'].append(dict(id='providers',name='Providers',kind='group',
            count=dict(min=0,preferred=0,max=2),placement=dict(nesting='owned',zone='any',grid='station'),
            requirements=[],features=[],spaces=[provider]))
        dep['relationships'] = [dependency(target='./providers/provider', mode='distinct')]
        p = resolve_instances(d)
        self.assertEqual(2, len(p.relationships))
        self.assertEqual(2, len({r['target'] for r in p.relationships}))
        self.assertFalse(p.diagnostics)

    def test_explicit_absent_provider_reports_unresolved_without_waiver(self):
        d = small(); d['spaces'][0]['relationships'] = [dependency()]
        p = resolve_instances(d, counts={'division/provider': 0})
        self.assertIn('required_target_missing', {x['code'] for x in p.diagnostics})
        self.assertFalse(any(x.get('code') == 'proven_infeasible' for x in p.diagnostics))

    def test_choice_counts_families_per_parent_not_instance_quantity(self):
        d = small(); dep = d['spaces'][0]
        dep['count'] = dict(min=2, preferred=2, max=2)
        for n in dep['spaces']:
            n['count'] = dict(min=0, preferred=2, max=3)
        dep['choices'] = [dict(id='one_family', members=['./consumer', './provider'],
                               minSelected=1, preferredSelected=1, maxSelected=1)]
        p = resolve_instances(d)
        for instance in ['division[0]', 'division[1]']:
            children = [p.spaces[k] for k in p.spaces[instance]['children']]
            self.assertEqual(2, len(children))
            self.assertEqual(1, len({c['template'] for c in children}))
        self.assertFalse(p.diagnostics)

    def test_count_override_respects_bounds_and_reports_unknown_keys(self):
        d = small()
        with self.assertRaises(ValueError):
            resolve_instances(d, counts={'division/provider': 9})
        p = resolve_instances(d, counts={'division/provider': 2})
        self.assertEqual(2, len([n for n in p.spaces.values() if n['template'] == 'division/provider']))
        p = resolve_instances(d, counts={'missing': 1})
        self.assertIn('unused_count_override', {x['code'] for x in p.diagnostics})

    def test_same_parent_grandchild_to_uncle(self):
        d = small(); dep = d['spaces'][0]
        leaf = dep['spaces'].pop(0)
        group = dict(id='suite',name='Suite',kind='group',count=dict(min=1,preferred=1,max=1),
                     placement=dict(nesting='owned',zone='any',grid='station'),requirements=[],features=[],spaces=[leaf])
        dep['spaces'].insert(0, group); dep['count'] = dict(min=2,preferred=2,max=2)
        dep['relationships'] = [dependency('./suite/consumer','./provider')]
        p = resolve_instances(d)
        for r in p.relationships:
            self.assertEqual(r['source'].split('/')[0], r['target'].split('/')[0])
        self.assertEqual(2, len(p.relationships))


class SettingsTests(unittest.TestCase):
    def test_supplied_profile_and_defaults(self):
        self.assertEqual(load_settings(), load_settings(ROOT / 'examples/generation-settings.yaml'))
        self.assertEqual(0, validate_settings({'max_proposals': 0})['max_proposals'])
        self.assertEqual(0, validate_settings({'local_proposals': 0})['local_proposals'])

    def test_unknown_invalid_and_boolean_settings_rejected(self):
        for value in ({'bogus': 2}, {'maintenance_width': 4}, {'wrap_weight': -1},
                      {'area_jitter': float('nan')}, {'local_proposals': True},
                      {'department_gap': 3}, {'area_jitter': 0.9}):
            with self.subTest(value=value), self.assertRaises(ValueError):
                validate_settings(value)


if __name__ == '__main__':
    unittest.main()
