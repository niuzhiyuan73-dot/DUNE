import json
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[3]


class CatalogEvidenceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.catalog = json.loads((ROOT / 'mods/ai/current_mode_catalog.json').read_text(encoding='utf-8'))
        cls.records = {r['id']: r for r in cls.catalog['records']}
        cls.locale = json.loads((ROOT / 'dump/current_mode/localization.json').read_text(encoding='utf-8'))

    def test_fixed_configuration_is_covered(self):
        s = self.catalog['scenario']
        ids = set(s['options']['ConflictDeck'])
        for p in s['players']:
            for k in ('starting_deck', 'starting_intrigue', 'starting_tech'):
                ids.update(p[k])
        self.assertFalse(ids - self.records.keys())

    def test_unique_archetypes_and_attribute_keys(self):
        self.assertEqual(len(self.records), len(self.catalog['records']))
        defs = json.loads((ROOT / 'dump/current_mode/attribute_definitions.json').read_text())
        self.assertEqual(len(defs), len({d['key'] for d in defs}))

    def test_diplomacy_does_not_inherit_wrong_legacy_income(self):
        r = self.records['8cdce934-fa2e-4d87-9e5e-c59eb5ed8b5e']
        self.assertEqual(r['attributes']['Persuasion'], 1)
        self.assertNotIn('Solari', r['attributes'])
        self.assertTrue(any('太阳币' in x['text'] for x in r['legacy_evidence_unverified']))
        self.assertFalse(any('太阳币' in x for x in r['structured_summary']))

    def test_conflict_rewards_keep_all_three_original_texts(self):
        r = self.records['fa49748c-0b90-4f5a-821a-7bf96d241250']
        text = {t['key']: t['zh_raw'] for t in r['localized_fields']}
        for n in (1, 2, 3):
            key = f'card.conflict.propagandaup.reward.{n}'
            self.assertEqual(text[key], self.locale['zh_CN'][key])
        self.assertEqual(len(r['attributes']['ConflictRewardArchetypes']), 3)

    def test_original_long_rule_text_is_not_truncated(self):
        long_fields = [t for r in self.records.values() for t in r['localized_fields']
                       if t['zh_raw'] and len(t['zh_raw']) > 70]
        self.assertTrue(long_fields)
        for field in long_fields:
            self.assertEqual(field['zh_raw'], self.locale['zh_CN'][field['key']])


if __name__ == '__main__':
    unittest.main()
