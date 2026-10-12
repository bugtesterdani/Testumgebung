"""Generate GitHub wiki pages from docs; Markdown remains the editable source."""
from pathlib import Path
from urllib.parse import quote, unquote, urlsplit
import argparse, re, shutil, json

ROOT=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser()
parser.add_argument('--repository',default='bugtesterdani/Testumgebung')
parser.add_argument('--ref',default='main')
parser.add_argument('--output',default='artifacts/wiki')
args=parser.parse_args()
out=Path(args.output).resolve()
if not out.is_relative_to(ROOT) or out==ROOT:raise ValueError('Output must be inside the project')
out.mkdir(parents=True,exist_ok=True)
sources=sorted((ROOT/'docs').rglob('*.md'))
names={p.resolve():p.relative_to(ROOT/'docs').with_suffix('').as_posix().replace('/','--') for p in sources}
assert len(set(names.values()))==len(names)
base='https://github.com/'+args.repository
entries=[]
for p in sources:
    text=p.read_text(encoding='utf-8-sig')
    def replace(m):
        label,url=m.groups()
        if urlsplit(url).scheme or url.startswith('//'):return m.group(0)
        path,sep,anchor=url.partition('#')
        if not path:return m.group(0)
        target=(p.parent/unquote(path)).resolve()
        if target in names:
            return f']({base}/wiki/{quote(names[target])}'+('#'+anchor if sep else '')+')'
        if not target.is_relative_to(ROOT):return m.group(0)
        relative=target.relative_to(ROOT).as_posix()
        prefix='raw' if target.suffix.lower() in {'.png','.jpg','.jpeg','.gif','.svg'} else 'blob'
        return f']({base}/{prefix}/{quote(args.ref,safe="")}/{quote(relative,safe="/")})'
    # Capture only the closing label bracket and target; preserve image/link labels.
    text=re.sub(r'(\])\(([^)]+)\)',replace,text)
    title=re.search(r'^#\s+(.+)$',text,re.M)
    title=title.group(1) if title else p.stem
    name=names[p.resolve()]
    (out/(name+'.md')).write_text(text,encoding='utf-8')
    entries.append((name,title,p.relative_to(ROOT).as_posix()))
links=['# Testumgebung - Dokumentation','','Diese Seiten werden automatisch aus `docs/` im Hauptrepository erzeugt. Änderungen bitte dort vornehmen; die nächste Synchronisierung übernimmt sie.','','## Anleitungen']
sidebar=['# Dokumentation','',f'[Startseite]({base}/wiki/Home)','']
for name,title,source in entries:
    line=f'- [{title}]({base}/wiki/{quote(name)})'
    links.append(line);sidebar.append(line)
links+=['','## Downloads',f'- [MSIX und Gesamthandbuch als PDF]({base}/releases)',f'- [Aktuelle Build-Artefakte]({base}/actions)','', 'Privates Wiki: GitHub Pro/Team erforderlich. Das Hauptrepository enthält alle Anleitungen unabhängig vom Wiki.']
(out/'Home.md').write_text('\n'.join(links)+'\n',encoding='utf-8')
(out/'_Sidebar.md').write_text('\n'.join(sidebar)+'\n',encoding='utf-8')
(out/'_Footer.md').write_text(f'Automatisch erzeugt aus [{args.repository}]({base}).\n',encoding='utf-8')
(out/'wiki-manifest.json').write_text(json.dumps(entries,ensure_ascii=False,indent=2),encoding='utf-8')
print(f'Generated {len(entries)} wiki pages in {out}')
