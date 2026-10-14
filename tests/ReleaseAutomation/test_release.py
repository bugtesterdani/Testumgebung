import importlib.util
from pathlib import Path
import tempfile
import os
import unittest
from unittest.mock import patch
from types import SimpleNamespace

spec = importlib.util.spec_from_file_location('release', Path(__file__).resolve().parents[2] / 'build/github-release.py')
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)

def issue(n, v, state='open'):
    return {'number': n, 'title': 'Release '+v, 'state': state, 'labels': [{'name': 'release'}]}

class ReleaseTests(unittest.TestCase):
    def test_three_part_version_and_msix_boundaries(self):
        self.assertEqual(release.version('1.0.14'), '1.0.14')
        for bad in ('v1.0.14', '1.0.14.0', '01.0.14', '1.0.-1', '65536.0.1', '1.0.14\n'):
            with self.subTest(bad=bad), self.assertRaises(ValueError): release.version(bad)

    def test_one_open_release_controls_all_builds(self):
        self.assertEqual(release.base_version([issue(1, '1.0.13', 'closed'), issue(4, '1.0.14')], '1.0.12'), '1.0.14')
        with self.assertRaises(ValueError): release.base_version([issue(1, '1.0.14'), issue(2, '1.0.15')], '1.0.12')

    def test_latest_release_remains_the_base_until_next_issue(self):
        self.assertEqual(release.base_version([issue(1, '1.0.13', 'closed'), issue(4, '1.0.14', 'closed')], '1.0.12'), '1.0.14')
        self.assertEqual(release.base_version([], '1.0.14'), '1.0.14')

    def test_private_titles_do_not_leak_in_notes(self):
        for label in ('confidential', 'VERTRAULICH', 'security', 'private'):
            item = {'number': 42, 'title': 'SECRET customer and vulnerability', 'state': 'closed', 'labels': [{'name': label}]}
            notes = release.body('1.0.14', 'v1.0.13', [item], [item])
            self.assertNotIn('SECRET', notes)
            self.assertNotIn('customer', notes)
            self.assertIn('Issue #42', notes)
            self.assertIn('PR #42', notes)

    def test_public_titles_cannot_inject_sections_or_mentions(self):
        line = release.item_line({'number': 7, 'title': '# fake\n[click](url) @all', 'labels': []})
        self.assertNotIn('\n', line)
        self.assertNotIn('@all', line)
        self.assertNotIn('[click](url)', line)

    def test_official_release_installs_above_development_builds(self):
        with tempfile.NamedTemporaryFile(delete=False) as f: path = f.name
        try:
            with patch.dict(os.environ, {'GITHUB_OUTPUT': path, 'GITHUB_RUN_NUMBER': '31'}):
                release.plan(None, SimpleNamespace(tag='v1.0.14', base='', fallback='1.0.13'))
            self.assertIn('version=1.0.14.65535', Path(path).read_text())
        finally: Path(path).unlink()

    def test_no_release_for_an_ordinary_merge(self):
        class API:
            repo = 'owner/repo'
            def api(self, path): return {'merged': True, 'base': {'ref': 'main'}, 'body': 'normal change'}
        with patch.object(release, 'output') as out:
            release.trigger(API(), SimpleNamespace(pull=2))
            out.assert_called_once_with(release='false')

    def test_release_trigger_pins_tag_to_merged_commit(self):
        sha = 'a'*40
        class API:
            repo = 'owner/repo'
            calls = []
            def api(self, path, data=None):
                if data:
                    self.calls.append(data); return {}
                if '/pulls/' in path: return {'merged': True, 'base': {'ref': 'main'}, 'body': 'Closes #1', 'merge_commit_sha': sha}
                return {**issue(1, '1.0.14', 'closed'), 'milestone': {'title': '1.0.14'}}
            def pages(self, path): return []
        api = API()
        with patch.object(release, 'output') as out:
            release.trigger(api, SimpleNamespace(pull=2))
            self.assertEqual(api.calls, [{'ref': 'refs/tags/v1.0.14', 'sha': sha}])
            self.assertEqual(out.call_args.kwargs['sha'], sha)

    def test_unmerged_pr_cannot_create_tag(self):
        class API:
            repo = 'owner/repo'
            def api(self, path): return {'merged': False}
        with patch.object(release, 'output') as out:
            release.trigger(API(), SimpleNamespace(pull=2))
            out.assert_called_once_with(release='false')

    def test_release_title_cannot_smuggle_an_extra_version(self):
        with self.assertRaises(ValueError): release.release_version({**issue(1, '1.0.14'), 'title': 'Release 1.0.14 1.0.15'})

    def test_direct_push_is_not_a_release_trigger(self):
        class API:
            repo = 'owner/repo'
            def pages(self, path): return []
        with patch.object(release, 'output') as out:
            release.trigger(API(), SimpleNamespace(pull=None, sha='a'*40))
            out.assert_called_once_with(release='false')

    def test_notes_are_written_to_github_and_redact_linked_private_issue(self):
        class API:
            repo = 'owner/repo'
            written = None
            def pages(self, path, **kwargs):
                if path.endswith('/releases'): return [{'tag_name': 'v1.0.13', 'draft': False}, {'id': 42, 'tag_name': 'v1.0.14', 'draft': True}]
                if '/commits/' in path:
                    return [{'number': 5, 'title': 'Included change', 'merged_at': '2026-10-13T00:00:00Z', 'base': {'ref': 'main'}, 'labels': []}]
                raise AssertionError(path)
            def issues(self):
                return [{'number': 3, 'title': 'SECRET internal defect', 'state': 'closed', 'labels': [{'name': 'confidential'}],
                         'created_at': '2025-01-01T00:00:00Z', 'closed_at': '2025-02-01T00:00:00Z', 'milestone': None}]
            def api(self, path, data=None, method=None):
                if path == 'graphql':
                    return {'data': {'repository': {'pullRequest': {'closingIssuesReferences': {
                        'nodes': [{'number': 3, 'repository': {'nameWithOwner': self.repo}}],
                        'pageInfo': {'hasNextPage': False, 'endCursor': None}}}}}}
                if '/releases/tags/' in path: return {'id': 42}
                if path.endswith('/releases/42'):
                    self.written = data['body']; self.method = method; self.updated = data; return {}
                raise AssertionError(path)
        def fake_git(*args):
            if args[0] == 'rev-parse': return 'a'*40
            if args[0] == 'tag': return 'v1.0.13\nv1.0.14'
            if args[:2] == ('rev-list', '--count'): return '1'
            if args[0] == 'rev-list': return 'a'*40
            if args[0] == 'show': return '2026-10-01T00:00:00+00:00'
            raise AssertionError(args)
        api = API()
        with patch.object(release, 'git', fake_git): release.publish(api, SimpleNamespace(tag='v1.0.14'))
        self.assertEqual(api.method, 'PATCH')
        self.assertEqual('v1.0.14', api.updated['tag_name'])
        self.assertEqual('a'*40, api.updated['target_commitish'])
        self.assertTrue(api.updated['draft'])
        self.assertIn('PR #5', api.written)
        self.assertIn('Issue #3', api.written)
        self.assertNotIn('SECRET', api.written)
        self.assertIn('1.0.14.65535', api.written)

if __name__ == '__main__': unittest.main()
