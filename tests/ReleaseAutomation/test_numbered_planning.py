from types import SimpleNamespace
from unittest.mock import patch
import os
import unittest
from test_next_release import API, release


def numbered(number=1, selected='', state='open'):
    return {'number': number, 'title': f'Release R{number}', 'state': state,
            'labels': [{'name': 'release'}], 'milestone': {'title': f'Release R{number}'},
            'body': f'<!-- release-version:{selected} -->'}


class NumberedPlanningTests(unittest.TestCase):
    def test_new_plan_has_no_automatic_target_and_uses_previous_build_base(self):
        api = API()
        release.next_release(api, SimpleNamespace(tag='v1.0.14'))
        self.assertIsNone(release.release_version(api.items[0], required=False))
        self.assertEqual('1.0.14', release.base_version(api.items, '1.0.0'))
        self.assertEqual(1, len(api.items))
        with self.assertRaises(ValueError): release.release_version(api.items[0])

    def test_setting_or_changing_target_preserves_planning_milestone_and_shared_version(self):
        api = API()
        release.next_release(api, SimpleNamespace(tag='v1.0.14'))
        for target in ('1.0.15', '1.1.0'):
            api.items[0]['body'] = api.items[0]['body'].replace('<!-- release-version:' + ('' if target == '1.0.15' else '1.0.15') + ' -->', '<!-- release-version:' + target + ' -->')
            release.sync_release(api, SimpleNamespace(issue=1))
            self.assertEqual('Release R1', api.items[0]['milestone']['title'])
            self.assertEqual(target, release.base_version(api.items, '1.0.0'))
            with patch.dict(os.environ, {'GITHUB_RUN_NUMBER': '42'}), patch.object(release, 'output') as out:
                release.plan(api, SimpleNamespace(tag='', base='', fallback='1.0.0'))
            out.assert_called_once_with(version=target + '.42', base=target)
        self.assertEqual(1, len(api.milestones))

    def test_clearing_target_restores_previous_base_and_sync_works_before_selection(self):
        api = API(); release.next_release(api, SimpleNamespace(tag='v1.0.14'))
        release.sync_release(api, SimpleNamespace(issue=1))
        api.items[0]['body'] = api.items[0]['body'].replace('<!-- release-version: -->', '<!-- release-version:1.1.0 -->')
        self.assertEqual('1.1.0', release.base_version(api.items, '1.0.0'))
        api.items[0]['body'] = api.items[0]['body'].replace('<!-- release-version:1.1.0 -->', '<!-- release-version: -->')
        self.assertEqual('1.0.14', release.base_version(api.items, '1.0.0'))

    def test_next_release_increments_planning_number_not_software_version(self):
        api = API(); release.next_release(api, SimpleNamespace(tag='v1.0.14'))
        api.items[0]['state'] = 'closed'
        api.items[0]['body'] = api.items[0]['body'].replace('<!-- release-version: -->', '<!-- release-version:1.1.0 -->')
        api.tag = 'v1.1.0'
        release.next_release(api, SimpleNamespace(tag='v1.1.0'))
        self.assertEqual('Release R2', api.items[-1]['title'])
        self.assertIsNone(release.release_version(api.items[-1], required=False))
        self.assertEqual('1.1.0', release.base_version(api.items, '1.0.0'))
        release.next_release(api, SimpleNamespace(tag='v1.1.0'))
        self.assertEqual(2, len(api.items))

    def test_cancelled_unversioned_closed_plan_does_not_hide_last_completed_version(self):
        self.assertEqual('1.2.3', release.base_version([numbered(1, '1.2.3', 'closed'), numbered(2, '', 'closed')], '1.0.0'))

    def test_invalid_or_duplicate_versions_are_rejected_and_legacy_titles_stay_supported(self):
        for text in ('<!-- release-version:1.0 -->', '<!-- release-version:v1.0.0 -->',
                     '<!-- release-version:1.0.0 --><!-- release-version:1.1.0 -->'):
            with self.subTest(text=text), self.assertRaises(ValueError):
                release.release_version({**numbered(), 'body': text}, required=False)
        self.assertEqual('1.2.3', release.release_version({'title': 'Release 1.2.3'}))
        with self.assertRaises(ValueError): release.release_version({'title': 'Release 1.2.3', 'body': '<!-- release-version:1.3.0 -->'})

    def test_selected_target_must_exceed_the_completed_build_base(self):
        for target in ('1.2.3', '1.2.2'):
            with self.subTest(target=target), self.assertRaises(ValueError):
                release.release_version({**numbered(1, target), 'body': f'<!-- release-build-base:1.2.3 -->\n<!-- release-version:{target} -->'})

    def test_next_planning_number_includes_closed_cancelled_plans(self):
        api = API(); api.items = [numbered(9, '', 'closed')]
        release.next_release(api, SimpleNamespace(tag='v1.0.14'))
        self.assertEqual('Release R10', api.items[-1]['title'])
        self.assertIsNone(release.release_version(api.items[-1], required=False))

    def test_official_trigger_requires_selected_target_and_matching_pr_before_writes(self):
        for target, requested, expected in (('', '', False), ('1.3.0', '1.4.0', False), ('1.3.0', '1.3.0', True)):
            class TriggerAPI:
                repo = 'owner/repo'
                writes = []
                def api(self, path, data=None):
                    if data: self.writes.append(data); return {}
                    if '/pulls/' in path:
                        return {'merged': True, 'base': {'ref': 'main'}, 'body': 'Closes #1' + (f'\n<!-- release-version:{requested} -->' if requested else ''), 'merge_commit_sha': 'a'*40}
                    return numbered(1, target, 'closed')
                def pages(self, path): return []
            api = TriggerAPI()
            with self.subTest(target=target, requested=requested), patch.object(release, 'output'):
                if expected:
                    release.trigger(api, SimpleNamespace(pull=2))
                    self.assertEqual([{'ref': 'refs/tags/v1.3.0', 'sha': 'a'*40}], api.writes)
                else:
                    with self.assertRaises(ValueError): release.trigger(api, SimpleNamespace(pull=2))
                    self.assertFalse(api.writes)


if __name__ == '__main__': unittest.main()
