"""Check the signed payload against the inventoried binaries and license texts."""
from pathlib import Path
import argparse, csv, hashlib, json, shutil, zipfile
from urllib.parse import unquote
ROOT=Path(__file__).resolve().parents[1]
p=argparse.ArgumentParser();p.add_argument('--bundle',required=True);args=p.parse_args()
bundle=Path(args.bundle)
inventory=json.loads((ROOT/'docs/licensing/inventory.json').read_text(encoding='utf-8'))
cache=ROOT/'artifacts/licensing/cache/verification.msix'
with zipfile.ZipFile(bundle) as outer:
    name=next(n for n in outer.namelist() if n.endswith('.msix'))
    with outer.open(name) as source,cache.open('wb') as target:shutil.copyfileobj(source,target)
with zipfile.ZipFile(cache) as z:
    names={unquote(n):n for n in z.namelist()}
    if len(names)!=len(z.namelist()):raise RuntimeError('Duplicate decoded MSIX paths')
    actual={n for n in names if Path(n).suffix.lower() in {'.exe','.dll','.pyd','.whl','.a'}}
    expected={r['path'] for r in inventory['binaries']}
    if actual!=expected:raise RuntimeError(f'Binary file list differs: {actual^expected}')
    for row in inventory['binaries']:
        if hashlib.sha256(z.read(names[row['path']])).hexdigest()!=row['sha256']:raise RuntimeError('Binary hash differs: '+row['path'])
    with (ROOT/'docs/licensing/license-files.csv').open(encoding='utf-8-sig',newline='') as f:licenses=list(csv.DictReader(f))
    for row in licenses:
        if hashlib.sha256(z.read(names[row['path']])).hexdigest()!=row['sha256']:raise RuntimeError('License hash differs: '+row['path'])
    source_files=[ROOT/'toolchains/w64devkit/source/source.tar',ROOT/'toolchains/w64devkit/Dockerfile']
    source_files += [p for p in (ROOT/'toolchains/w64devkit/src').rglob('*') if p.is_file()]
    for path in source_files:
        name=path.relative_to(ROOT).as_posix()
        with path.open('rb') as original,z.open(names[name]) as packaged:
            if hashlib.file_digest(original,'sha256').digest()!=hashlib.file_digest(packaged,'sha256').digest():
                raise RuntimeError('Corresponding source differs: '+name)
    for name in ['LICENSE' ,'LICENSE-STATUS.md','THIRD-PARTY-NOTICES.md','THIRD-PARTY-TERMS.md','docs/licensing/inventory.json','docs/licensing/massnahmen.md']:
        if z.read(names[name])!=(ROOT/name).read_bytes():raise RuntimeError('Embedded document differs: '+name)
report={'bundle':bundle.name,'sha256':hashlib.file_digest(bundle.open('rb'),'sha256').hexdigest(),'binary_files_verified':len(expected),'license_files_verified':len(licenses),'corresponding_source_files_verified':len(source_files),'result':'passed'}
(bundle.parent/'LICENSE-VERIFICATION.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
print(json.dumps(report,indent=2))
