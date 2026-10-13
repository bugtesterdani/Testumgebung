"""Evidence-based dependency inventory. Does not select the project's own license."""
from pathlib import Path
from collections import defaultdict
import argparse, csv, hashlib, io, json, re, shutil, sys, tarfile, urllib.request, zipfile
import xml.etree.ElementTree as ET

ROOT=Path(__file__).resolve().parents[1]
REPORT=ROOT/'docs/licensing'
CACHE=ROOT/'artifacts/licensing/cache'
LICENSES=ROOT/'licenses/third-party'
for p in [REPORT,CACHE,LICENSES]:p.mkdir(parents=True,exist_ok=True)
sys.path.insert(0,str(ROOT/'artifacts/tools/license-audit'))
try:import pefile
except ImportError:pefile=None

def rel(p):
    p=Path(p).resolve()
    return p.relative_to(ROOT).as_posix() if p.is_relative_to(ROOT) else str(p)
def sha(data):return hashlib.sha256(data).hexdigest()
def csv_write(name,rows):
    if not rows:return
    with (REPORT/name).open('w',encoding='utf-8-sig',newline='') as f:
        writer=csv.DictWriter(f,fieldnames=list(rows[0]));writer.writeheader()
        for row in rows:writer.writerow({k:'; '.join(v) if isinstance(v,list) else v for k,v in row.items()})
def save_license(category,name,data):
    target=LICENSES/category/name
    target.parent.mkdir(parents=True,exist_ok=True);target.write_bytes(data)
    return {'path':rel(target),'sha256':sha(data)}
def metadata(folder):
    specs=list(folder.glob('*.nuspec'))
    if not specs:return {'license':'UNRESOLVED','evidence':'missing nuspec'}
    root=ET.parse(specs[0]).getroot()
    values={e.tag.split('}')[-1]:e.text or '' for e in root.iter() if e.tag.split('}')[-1] in {'id','version','authors','copyright','license','licenseUrl','projectUrl'}}
    license_element=next((e for e in root.iter() if e.tag.split('}')[-1]=='license'),None)
    values['license_type']=license_element.get('type','') if license_element is not None else ''
    repository=next((e for e in root.iter() if e.tag.split('}')[-1]=='repository'),None)
    values['repository']=repository.attrib if repository is not None else {}
    values['evidence']='nuget-cache/'+folder.parent.name+'/'+folder.name+'/'+specs[0].name;values['license_files']=[]
    category='nuget/'+values.get('id',folder.parent.name)+'/'+values.get('version',folder.name)
    found=set()
    for p in folder.rglob('*'):
        if p.is_file() and (p.name.lower().startswith(('license','notice','third-party','thirdparty','copying','copyright')) or
            values.get('license_type')=='file' and p.relative_to(folder).as_posix().lower()==values.get('license','').lower()):
            found.add(p)
    for p in sorted(found):values['license_files'].append(save_license(category,p.relative_to(folder).as_posix(),p.read_bytes()))
    if values.get('license_type')=='expression':
        for token in re.findall(r'[A-Za-z][A-Za-z0-9.+-]*',values.get('license','')):
            if token in {'AND','OR','WITH'}:continue
            cache=CACHE/('spdx-'+token+'.json')
            try:
                if not cache.exists():
                    url=f'https://raw.githubusercontent.com/spdx/license-list-data/v3.29.0/json/details/{token}.json'
                    cache.write_bytes(urllib.request.urlopen(url,timeout=25).read())
                content=json.loads(cache.read_text(encoding='utf-8'))
                values['license_files'].append(save_license(category,'SPDX-'+token+'.txt',content['licenseText'].encode()))
            except Exception as ex:values.setdefault('license_errors',[]).append(token+': '+str(ex))
    return values

def projects_and_packages():
    projects=[];packages={};edges=[]
    inherited=ET.parse(ROOT/'Directory.Build.props').getroot()
    framework=inherited.findtext('.//TargetFramework')
    for p in sorted(list((ROOT/'src').rglob('*.csproj'))+list((ROOT/'tests').rglob('*.csproj'))):
        if 'obj' in p.parts or 'bin' in p.parts:continue
        root=ET.parse(p).getroot();direct={};refs=[]
        for e in root.iter('PackageReference'):direct[e.get('Include')]=e.get('Version') or e.findtext('Version') or 'unspecified'
        for e in root.iter('ProjectReference'):
            include=e.get('Include','');refs.append(rel(p.parent/include.replace('\\','/')) if '$(' not in include else include)
        assets=p.parent/'obj/project.assets.json'
        entry={'project':rel(p),'framework':root.findtext('.//TargetFramework') or framework,'kind':'product' if 'src' in p.parts else 'test/fixture','references':refs,'direct_packages':[k+'/'+v for k,v in direct.items()],'assets_available':assets.exists()}
        projects.append(entry)
        if not assets.exists():continue
        data=json.loads(assets.read_text(encoding='utf-8-sig'))
        for key,lib in data.get('libraries',{}).items():
            if lib.get('type')!='package':continue
            name,version=key.rsplit('/',1)
            row=packages.setdefault(key,{'name':name,'version':version,'projects':[],'direct_in':[],'targets':[], 'package_sha512':lib.get('sha512',''),'assets_files':[]})
            row['projects'].append(rel(p))
            if name in direct:row['direct_in'].append(rel(p))
            row['assets_files'].append(rel(assets))
            if 'metadata' not in row:
                folder=next((Path(f)/lib.get('path',key.lower()) for f in data.get('packageFolders',{}) if (Path(f)/lib.get('path',key.lower())).exists()),None)
                row['metadata']=metadata(folder) if folder else {'license':'UNRESOLVED','evidence':'package cache missing'}
            for target,libraries in data.get('targets',{}).items():
                if key not in libraries:continue
                row['targets'].append(target)
                for dependency,constraint in libraries[key].get('dependencies',{}).items():
                    edges.append({'project':rel(p),'target':target,'parent':key,'dependency':dependency,'constraint':constraint})
    return projects,packages,edges

def pe_version(data):
    if pefile is None:return '', '', 'pefile unavailable'
    try:
        pe=pefile.PE(data=data,fast_load=True)
        pe.parse_data_directories(directories=[pefile.DIRECTORY_ENTRY['IMAGE_DIRECTORY_ENTRY_RESOURCE']])
        info={}
        for group in getattr(pe,'FileInfo',[]):
            for node in group:
                for table in getattr(node,'StringTable',[]):
                    info.update({k.decode(errors='replace'):v.decode(errors='replace') for k,v in table.entries.items()})
        pe.close()
        return info.get('FileVersion',''),info.get('ProductVersion',''),info.get('ProductName','')
    except Exception as ex:return '','',type(ex).__name__

def release_inventory(bundle):
    rows=[];deps={};texts=[]
    with zipfile.ZipFile(bundle) as z:
        package=next(n for n in z.namelist() if n.endswith('.msix'))
        cached=CACHE/Path(package).name
        if not cached.exists():
            with z.open(package) as src,cached.open('wb') as target:shutil.copyfileobj(src,target)
    with zipfile.ZipFile(cached) as z:
        for item in z.infolist():
            name=item.filename
            if name.endswith('.deps.json'):
                data=json.loads(z.read(item));deps[name]=data
            if re.search(r'(license|copying|third.party|notice)',Path(name).name,re.I):texts.append(name)
            if Path(name).suffix.lower() in {'.exe','.dll','.pyd','.whl','.a'}:
                data=z.read(item);version,product_version,product_name=pe_version(data) if name.lower().endswith(('.exe','.dll','.pyd')) else ('','','')
                rows.append({'release':Path(bundle).name,'path':name,'file_version':version,'product_version':product_version,'product_name':product_name,'size':len(data),'sha256':sha(data),'component':'','license':''})
    return rows,deps,texts

def toolchain_sources():
    source=ROOT/'toolchains/w64devkit/source/source.tar';rows=[]
    if not source.exists():return rows
    with tarfile.open(source) as outer:
        for member in outer.getmembers():
            if not member.isfile():continue
            basename=Path(member.name).name
            cache=CACHE/basename
            if not cache.exists():
                with outer.extractfile(member) as src,cache.open('wb') as target:shutil.copyfileobj(src,target)
            row={'archive':basename,'source_sha256':sha(cache.read_bytes()),'source_location':'toolchains/w64devkit/source/source.tar!/'+member.name,'license_files':[]}
            with tarfile.open(cache) as nested:
                for item in nested:
                    if not item.isfile():continue
                    filename=Path(item.name).name
                    if not re.match(r'^(COPYING(?:[.\-].*)?|LICENSE(?:[.\-].*)?|LICENCE(?:[.\-].*)?|NOTICE(?:[.\-].*)?|UNLICENSE|COPYRIGHT|LEGAL|README\.md|README\.adoc)$',filename,re.I):continue
                    if filename.lower().startswith('readme') and len(Path(item.name).parts)>2:continue
                    parts=Path(item.name).parts
                    if '..' in parts or Path(item.name).is_absolute():raise ValueError('Unsafe source archive path')
                    row['license_files'].append(save_license('w64devkit/'+basename,item.name,nested.extractfile(item).read()))
            rows.append(row)
    return rows

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--bundle',required=True);args=parser.parse_args()
    projects,packages,edges=projects_and_packages()
    binaries,deps,release_licenses=release_inventory(args.bundle)
    source_archives=toolchain_sources()
    used=defaultdict(list)
    for location,data in deps.items():
        for key,value in data.get('libraries',{}).items():used[key].append(location)
    missing=[]
    for key,locations in used.items():
        if key in packages:continue
        name,version=key.rsplit('/',1)
        lib=next(data['libraries'][key] for data in deps.values() if key in data.get('libraries',{}))
        if lib.get('type') not in {'package','runtimepack'}:continue
        folder=Path.home()/'.nuget/packages'/name.lower()/version.lower()
        # .NET deps prefix runtimepack. is not part of the NuGet ID.
        if name.startswith('runtimepack.'):folder=Path.home()/'.nuget/packages'/name[len('runtimepack.'):].lower()/version.lower()
        packages[key]={'name':name,'version':version,'projects':[],'direct_in':[],'targets':[],'package_sha512':'','assets_files':[], 'metadata':metadata(folder)}
        if packages[key]['metadata'].get('license')=='UNRESOLVED':missing.append(key)
    for key,row in packages.items():row['bundled_manifests']=used.get(key,[])
    for path,category in [(ROOT/'toolchains/python/LICENSE.txt','python'),(ROOT/'toolchains/w64devkit/COPYING.MinGW-w64-runtime.txt','w64devkit')]:
        if path.exists():save_license(category,path.name,path.read_bytes())
    wheel=ROOT/'toolchains/python/pip-bootstrap/pip.whl'
    if wheel.exists():
        with zipfile.ZipFile(wheel) as z:
            for name in z.namelist():
                if Path(name).name.lower().startswith(('license','copying','notice','authors','copyright')) and not name.endswith('/'):
                    save_license('pip-26.2.1',name,z.read(name))
    result={'date':'2026-10-13','projects':projects,'packages':packages,'dependency_edges':edges,'binaries':binaries,'release_manifests':deps,'release_license_files':release_licenses,'toolchain_sources':source_archives,'unresolved_bundled_packages':missing,'bundle':rel(args.bundle)}
    (REPORT/'inventory.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
    csv_write('projects.csv',projects)
    csv_write('nuget.csv',[{'name':r['name'],'version':r['version'],'license':r['metadata'].get('license','UNRESOLVED'),'license_type':r['metadata'].get('license_type',''),'direct_in':r['direct_in'],'resolved_in':r['projects'],'bundled_manifests':r['bundled_manifests'],'authors':r['metadata'].get('authors',''),'copyright':r['metadata'].get('copyright',''),'repository':r['metadata'].get('repository',{}).get('url',''),'license_files':[x['path'] for x in r['metadata'].get('license_files',[])]} for r in sorted(packages.values(),key=lambda r:(r['name'].lower(),r['version']))])
    csv_write('dependency-edges.csv',edges);csv_write('binaries.csv',binaries)
    (CACHE/'summary.json').write_text(json.dumps({'projects':len(projects),'nuget_versions':len(packages),'binaries':len(binaries),'toolchain_source_archives':len(source_archives),'unresolved':missing,'licenses':len(list(LICENSES.rglob('*.*')))},indent=2),encoding='utf-8')
    print((CACHE/'summary.json').read_text())
if __name__=='__main__':main()
