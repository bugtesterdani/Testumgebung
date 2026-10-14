"""Version planning and release bodies stored directly in GitHub, never in a changelog."""
import argparse
from datetime import datetime, timezone
import json
import os
import re
import subprocess
from urllib.error import HTTPError
from urllib.parse import urlencode
from urllib.request import Request, urlopen

VERSION = re.compile(r"(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\Z")
PRIVATE = {"confidential", "vertraulich", "private", "security", "security-sensitive"}

def version(value):
    if not VERSION.fullmatch(value) or any(int(n) > 65535 for n in value.split('.')):
        raise ValueError("Release-Version muss drei Zahlen zwischen 0 und 65535 enthalten.")
    return value

def release_version(issue):
    title = re.fullmatch(r"Release\s+(\d+\.\d+\.\d+)", issue.get('title', ''), re.I)
    if not title: raise ValueError("Release-Issue benötigt den Titel Release X.Y.Z.")
    return version(title[1])

def labels(item):
    return {x['name'].casefold() for x in item.get('labels', [])}

def item_line(item, pull=False):
    number = int(item['number'])
    if labels(item) & PRIVATE:
        return f"- `{'PR' if pull else 'Issue'} #{number}`: Vertraulicher Eintrag (Inhalt ausgeblendet)"
    # Keep API text from injecting Markdown sections, links or mention notifications.
    title = re.sub(r'[\r\n\t]+', ' ', item.get('title', '')).replace('@', '@\u200b')
    title = re.sub(r'([\\`*_{}\[\]()<>#!|])', r'\\\1', title)
    return f"- {'PR' if pull else 'Issue'} #{number}: {title}"

def body(base, previous, issues, pulls):
    lines = [f"# Testumgebung {base}", '',
             f"Installer-Version: `{base}.65535`.", '',
             f"Zeitraum: seit `{previous}`." if previous else "Erstes Release: gesamte bisherige Git-Historie.", '',
             "## Pull Requests", '']
    lines += [item_line(p, True) for p in sorted(pulls, key=lambda x: x['number'])] or ['Keine Pull Requests in diesem Release.']
    lines += ['', '## Issues', '']
    lines += [item_line(i) + ('' if labels(i) & PRIVATE else f" ({i['state']})")
              for i in sorted(issues, key=lambda x: x['number'])] or ['Keine zugeordneten Issues.']
    lines += ['', 'Enthalten sind alle PRs der aufgenommenen Commits sowie Issues des Versions-Milestones, verknüpfte Abschluss-Issues und im Zeitraum erstellte oder geschlossene Issues ohne anderen Versions-Milestone.',
              'Vertraulich gekennzeichnete Einträge erscheinen ausschließlich mit ihrer ID.', '',
              'Installation, passende Quellen und Lizenzbedingungen liegen als Release-Anlagen bei. Die Signatur verwendet derzeit das Entwicklungssignierzertifikat.']
    return '\n'.join(lines) + '\n'

class GitHub:
    def __init__(self):
        self.repo = os.environ['GITHUB_REPOSITORY']
        self.token = os.environ.get('GH_TOKEN') or os.environ['GITHUB_TOKEN']
    def api(self, path, data=None, method=None):
        raw = None if data is None else json.dumps(data).encode()
        request = Request('https://api.github.com/' + path, data=raw,
                          method=method or ('POST' if raw is not None else 'GET'),
                          headers={'Authorization': 'Bearer ' + self.token, 'Accept': 'application/vnd.github+json',
                                   'Content-Type': 'application/json',
                                   'X-GitHub-Api-Version': '2022-11-28', 'User-Agent': 'Testumgebung-release'})
        try:
            with urlopen(request, timeout=60) as response:
                payload = response.read()
                return json.loads(payload) if payload else None
        except HTTPError as error:
            # Do not print response bodies, issue titles or tokens in build logs.
            raise RuntimeError(f'GitHub API fehlgeschlagen (HTTP {error.code}).') from None
    def pages(self, path, **query):
        page = 1; result = []
        while True:
            values = self.api(path + '?' + urlencode({**query, 'per_page': 100, 'page': page}))
            result.extend(values)
            if len(values) < 100: return result
            page += 1
    def issues(self):
        return [i for i in self.pages(f'repos/{self.repo}/issues', state='all') if 'pull_request' not in i]

def output(**values):
    with open(os.environ['GITHUB_OUTPUT'], 'a', encoding='utf-8') as target:
        for name, value in values.items(): target.write(f'{name}={value}\n')

def git(*args):
    return subprocess.check_output(['git', *args], text=True).strip()

def base_version(issues, fallback):
    releases = [i for i in issues if 'release' in labels(i)]
    active = [i for i in releases if i['state'] == 'open']
    if len(active) > 1: raise ValueError('Nur ein offenes Release-Issue ist als gemeinsame Versionsquelle erlaubt.')
    chosen = active or sorted(releases, key=lambda x: x['number'], reverse=True)[:1]
    return release_version(chosen[0]) if chosen else version(fallback)

def plan(api, args):
    if args.tag:
        if not args.tag.startswith('v'): raise ValueError('Tag muss vX.Y.Z heißen.')
        base = version(args.tag[1:]); build = base + '.65535'
    else:
        base = version(args.base) if args.base else base_version(api.issues(), args.fallback)
        index = int(os.environ['GITHUB_RUN_NUMBER'])
        if not 1 <= index < 65535: raise ValueError('Buildnummer überschreitet den MSIX-Bereich.')
        build = f'{base}.{index}'
    output(version=build, base=base)
    print('Build-Version: ' + build)

def trigger(api, args):
    pull_number = args.pull
    if getattr(args, 'sha', ''):
        if not re.fullmatch(r'[0-9a-f]{40}', args.sha): raise ValueError('Ungültiger Main-Commit.')
        associated = api.pages(f'repos/{api.repo}/commits/{args.sha}/pulls')
        merged = [p for p in associated if p.get('merged_at') and p.get('merge_commit_sha') == args.sha and p['base']['ref'] == 'main']
        if not merged: output(release='false'); return
        if len(merged) != 1: raise ValueError('Main-Commit kann nicht eindeutig einem Merge zugeordnet werden.')
        pull_number = merged[0]['number']
    pull = api.api(f'repos/{api.repo}/pulls/{pull_number}')
    if not pull.get('merged') or pull['base']['ref'] != 'main':
        output(release='false'); return
    # Match only explicit local closing keywords; no auto-release from ordinary merges.
    numbers = set(int(x) for x in re.findall(r'\b(?:close[sd]?|fix(?:es|ed)?|resolve[sd]?)\s+#(\d+)\b', pull.get('body') or '', re.I))
    issues = [api.api(f'repos/{api.repo}/issues/{n}') for n in numbers]
    releases = [i for i in issues if 'release' in labels(i)]
    if not releases: output(release='false'); return
    if len(releases) != 1: raise ValueError('Ein Release-PR muss genau ein Release-Issue abschließen.')
    issue = releases[0]; base = release_version(issue)
    if issue['state'] != 'closed': raise ValueError('Release-Issue ist noch nicht abgeschlossen; Tag wird nicht angelegt.')
    milestone = issue.get('milestone')
    if not milestone or milestone['title'] != base:
        raise ValueError('Release-Issue benötigt den gleichnamigen Versions-Milestone.')
    sha = pull['merge_commit_sha']
    if not re.fullmatch(r'[0-9a-f]{40}', sha): raise ValueError('Ungültiger Merge-Commit.')
    existing = api.pages(f'repos/{api.repo}/git/matching-refs/tags/v{base}')
    exact = [r for r in existing if r['ref'] == f'refs/tags/v{base}']
    if exact:
        if exact[0]['object']['sha'] != sha: raise ValueError('Release-Tag existiert bereits an einem anderen Commit.')
    else:
        api.api(f'repos/{api.repo}/git/refs', {'ref': f'refs/tags/v{base}', 'sha': sha})
    output(release='true', base=base, tag='v'+base, sha=sha, issue=issue['number'])
    print(f'Release-Issue #{issue["number"]}: Tag v{base} am geprüften Merge-Commit.')

def release_for_tag(api, tag):
    # Draft releases are not reliably returned by the tag lookup endpoint.
    # Authenticated release listing includes drafts; retain the returned ID.
    matches = [r for r in api.pages(f'repos/{api.repo}/releases') if r['tag_name'] == tag]
    if len(matches) != 1: raise ValueError('Release für den Tag fehlt oder ist nicht eindeutig.')
    return matches[0]

def publish(api, args):
    base = version(args.tag.removeprefix('v'))
    current = git('rev-parse', args.tag + '^{commit}')
    published = {r['tag_name'] for r in api.pages(f'repos/{api.repo}/releases') if not r.get('draft') and not r.get('prerelease')}
    candidates = [t for t in git('tag', '--merged', current).splitlines()
                  if t in published and t != args.tag and re.fullmatch(r'v\d+\.\d+\.\d+', t)]
    previous = min(candidates, key=lambda t: int(git('rev-list', '--count', f'{t}..{current}'))) if candidates else None
    commits = git('rev-list', f'{previous}..{current}' if previous else current).splitlines()
    pulls = {}
    for sha in commits:
        for p in api.pages(f'repos/{api.repo}/commits/{sha}/pulls'):
            if p.get('merged_at') and p['base']['ref'] == 'main': pulls[p['number']] = p
    # Linked closing issues cover older issues resolved by the included PRs.
    linked = set()
    for n in pulls:
        owner, name = api.repo.split('/')
        cursor = None
        while True:
            query = '''query($owner:String!,$name:String!,$n:Int!,$cursor:String){repository(owner:$owner,name:$name){pullRequest(number:$n){closingIssuesReferences(first:100,after:$cursor){nodes{number repository{nameWithOwner}} pageInfo{hasNextPage endCursor}}}}}'''
            result = api.api('graphql', {'query': query, 'variables': {'owner': owner, 'name': name, 'n': n, 'cursor': cursor}})
            if result.get('errors'): raise RuntimeError('Verknüpfte Issues konnten nicht vollständig gelesen werden.')
            refs = result['data']['repository']['pullRequest']['closingIssuesReferences']
            linked.update(i['number'] for i in refs['nodes'] if i['repository']['nameWithOwner'] == api.repo)
            if not refs['pageInfo']['hasNextPage']: break
            cursor = refs['pageInfo']['endCursor']
    start = datetime.fromisoformat(git('show', '-s', '--format=%cI', previous)) if previous else datetime.min.replace(tzinfo=timezone.utc)
    end = datetime.now(timezone.utc)
    selected = []
    for issue in api.issues():
        times = [datetime.fromisoformat(issue[k].replace('Z', '+00:00')) for k in ('created_at', 'closed_at') if issue.get(k)]
        milestone = (issue.get('milestone') or {}).get('title')
        if milestone == base or issue['number'] in linked or (not milestone and any(start < t <= end for t in times)):
            selected.append(issue)
    release = release_for_tag(api, args.tag)
    if not release.get('draft'): raise ValueError('Release Notes eines veröffentlichten Releases werden nicht überschrieben.')
    api.api(f'repos/{api.repo}/releases/{release["id"]}', {
        'body': body(base, previous, selected, list(pulls.values())),
        'tag_name': args.tag, 'target_commitish': current, 'draft': True,
    }, method='PATCH')
    print(f'Release Notes direkt auf GitHub gespeichert: {len(pulls)} PRs, {len(selected)} Issues.')

def milestone_for(api, base):
    matches = [m for m in api.pages(f'repos/{api.repo}/milestones', state='all') if m['title'] == base]
    if len(matches) > 1: raise ValueError('Versions-Milestone ist nicht eindeutig.')
    if matches:
        if matches[0].get('state') == 'closed': raise ValueError('Der gewünschte Versions-Milestone ist bereits geschlossen.')
        return matches[0]['number']
    return api.api(f'repos/{api.repo}/milestones', {'title': base, 'description': 'Geplante Änderungen dieser Version.'})['number']

def next_version(base):
    parts = [int(p) for p in version(base).split('.')]
    for index in (2, 1, 0):
        if parts[index] < 65535:
            parts[index] += 1
            return '.'.join(map(str, parts))
        parts[index] = 0
    raise ValueError('Keine höhere MSIX-Basisversion verfügbar.')

def next_release(api, args):
    if not args.tag.startswith('v'): raise ValueError('Tag muss vX.Y.Z heißen.')
    base = version(args.tag[1:])
    release = release_for_tag(api, args.tag)
    if not any(a['name'].endswith('.msixbundle') for a in release.get('assets', [])):
        raise ValueError('Release enthält noch kein erfolgreich gebautes MSIX-Bundle.')
    issues = api.issues()
    marker = f'<!-- next-release-for:{args.tag} -->'
    if any(marker in (i.get('body') or '') and i['state'] == 'closed' for i in issues):
        print('Folgeplanung dieses Tags ist bereits abgeschlossen; kein neues Issue.')
        return
    active = [i for i in issues if i['state'] == 'open' and 'release' in labels(i)]
    if len(active) > 1: raise ValueError('Mehrere offene Release-Issues; Planung nicht eindeutig.')
    if active:
        planned = release_version(active[0])
        if tuple(map(int, planned.split('.'))) <= tuple(map(int, base.split('.'))):
            raise ValueError('Offenes Release-Issue ist keine spätere Version.')
        print(f'Bestehendes Release-Issue #{active[0]["number"]} bleibt unverändert.')
        return
    proposed = next_version(base)
    # A closed planning issue must not be recreated, even on an old build rerun.
    if any('release' in labels(i) and i.get('title', '').casefold() == f'Release {proposed}'.casefold() for i in issues):
        raise ValueError('Planung dieser Folgeversion existiert bereits und ist geschlossen.')
    milestone = milestone_for(api, proposed)
    issue = api.api(f'repos/{api.repo}/issues', {
        'title': f'Release {proposed}', 'labels': ['release'], 'milestone': milestone,
        'body': marker + f'\nNächste Planung nach erfolgreichem Build von **{args.tag}**.\n\n'
                'Der Titel **Release X.Y.Z** ist die gemeinsame Versionsquelle für main und PR-Builds. '
                'Du kannst den Titel später ändern; die Automatik ordnet diesem Issue den passenden Milestone zu. '
                'Bereits zugeordnete Feature-Issues/PRs bei einer Versionsänderung selbst umordnen.\n\n'
                'Mehrere normale PRs sammeln. Erst ein abschließender Release-PR mit `Closes #<ID dieses Issues>` '
                'erzeugt nach Merge Tag und Release-Entwurf.\n\n'
                '- [ ] Geplante Änderungen abgeschlossen\n- [ ] Release-PR geprüft und gemerged\n'
                '- [ ] Paket und Lizenznachweise geprüft\n- [ ] Release-Entwurf veröffentlicht\n\n'
                'Anleitung: docs/release-ablauf.md.'})
    print(f'Nächstes bearbeitbares Release-Issue #{issue["number"]}: {proposed}.')

def sync_release(api, args):
    issue = api.api(f'repos/{api.repo}/issues/{args.issue}')
    if issue['state'] != 'open' or 'release' not in labels(issue): return
    base = release_version(issue)
    active = [i for i in api.issues() if i['state'] == 'open' and 'release' in labels(i)]
    if len(active) != 1: raise ValueError('Genau ein offenes Release-Issue erforderlich.')
    if (issue.get('milestone') or {}).get('title') == base: return
    number = milestone_for(api, base)
    api.api(f'repos/{api.repo}/issues/{issue["number"]}', {'milestone': number}, method='PATCH')
    print(f'Release-Issue #{issue["number"]}: Milestone {base} zugeordnet.')

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('command', choices=['plan', 'trigger', 'publish', 'next', 'sync'])
    parser.add_argument('--tag', default=''); parser.add_argument('--base', default='')
    parser.add_argument('--fallback', default='1.0.14'); parser.add_argument('--pull', type=int)
    parser.add_argument('--sha', default='')
    parser.add_argument('--issue', type=int)
    args = parser.parse_args()
    {'plan': plan, 'trigger': trigger, 'publish': publish, 'next': next_release, 'sync': sync_release}[args.command](GitHub(), args)

if __name__ == '__main__': main()
