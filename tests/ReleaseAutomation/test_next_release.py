import importlib.util
from pathlib import Path
from types import SimpleNamespace
import unittest

spec = importlib.util.spec_from_file_location('next_release', Path(__file__).resolve().parents[2] / 'build/github-release.py')
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


class API:
    repo = 'owner/repo'

    def __init__(self):
        self.items = []
        self.milestones = []
        self.assets = [{'name': 'Testumgebung.msixbundle'}]
        self.writes = []

    def issues(self):
        return self.items

    def pages(self, path, **query):
        if path.endswith('/milestones'): return self.milestones
        raise AssertionError(path)

    def api(self, path, data=None, method=None):
        if '/releases/tags/' in path: return {'assets': self.assets}
        if data is None: return next(i for i in self.items if path.endswith('/' + str(i['number'])))
        self.writes.append((path, data, method))
        if path.endswith('/milestones'):
            item = dict(data, number=len(self.milestones) + 1, state='open')
            self.milestones.append(item)
            return item
        if method == 'PATCH':
            item = next(i for i in self.items if path.endswith('/' + str(i['number'])))
            item['milestone'] = next(m for m in self.milestones if m['number'] == data['milestone'])
            return item
        item = dict(data, number=len(self.items) + 1, state='open')
        item['labels'] = [{'name': label} for label in item['labels']]
        item['milestone'] = next(m for m in self.milestones if m['number'] == data['milestone'])
        self.items.append(item)
        return item


class NextReleaseTests(unittest.TestCase):
    def test_success_then_rerun_creates_only_one_issue(self):
        api = API()
        release.next_release(api, SimpleNamespace(tag='v1.0.14'))
        release.next_release(api, SimpleNamespace(tag='v1.0.14'))
        self.assertEqual(1, len(api.items))
        self.assertEqual('Release 1.0.15', api.items[0]['title'])
        self.assertEqual('1.0.15', api.items[0]['milestone']['title'])

    def test_edited_title_syncs_milestone_and_controls_builds(self):
        api = API()
        release.next_release(api, SimpleNamespace(tag='v1.0.14'))
        api.items[0]['title'] = 'Release 1.1.0'
        release.sync_release(api, SimpleNamespace(issue=1))
        self.assertEqual('1.1.0', api.items[0]['milestone']['title'])
        self.assertEqual('1.1.0', release.base_version(api.items, '1.0.14'))
        before = len(api.writes)
        release.next_release(api, SimpleNamespace(tag='v1.0.14'))
        self.assertEqual(before, len(api.writes))

    def test_closed_renamed_successor_is_not_recreated_on_old_rerun(self):
        api = API()
        release.next_release(api, SimpleNamespace(tag='v1.0.14'))
        api.items[0].update(title='Release 1.1.0', state='closed')
        release.next_release(api, SimpleNamespace(tag='v1.0.14'))
        self.assertEqual(1, len(api.items))

    def test_missing_package_does_not_start_next_version(self):
        api = API()
        api.assets = []
        with self.assertRaises(ValueError): release.next_release(api, SimpleNamespace(tag='v1.0.14'))
        self.assertFalse(api.writes)

    def test_invalid_version_blocks_changes(self):
        api = API()
        release.next_release(api, SimpleNamespace(tag='v1.0.14'))
        api.items[0]['title'] = 'Release 1.1'
        count = len(api.writes)
        with self.assertRaises(ValueError): release.sync_release(api, SimpleNamespace(issue=1))
        self.assertEqual(count, len(api.writes))

    def test_multiple_plans_and_non_future_plan_fail(self):
        api = API()
        release.next_release(api, SimpleNamespace(tag='v1.0.14'))
        api.items.append(dict(api.items[0], number=2))
        with self.assertRaises(ValueError): release.next_release(api, SimpleNamespace(tag='v1.0.14'))
        api.items.pop()
        api.items[0]['title'] = 'Release 1.0.14'
        with self.assertRaises(ValueError): release.next_release(api, SimpleNamespace(tag='v1.0.14'))

    def test_version_rollover_and_exhaustion(self):
        self.assertEqual('1.1.0', release.next_version('1.0.65535'))
        self.assertEqual('2.0.0', release.next_version('1.65535.65535'))
        with self.assertRaises(ValueError): release.next_version('65535.65535.65535')


if __name__ == '__main__': unittest.main()
