"""Add usage and component attribution to the evidence inventory; preserve uncertainties."""
from pathlib import Path
from collections import defaultdict,Counter
import csv, hashlib, html, json, re, sys, tarfile, urllib.request, zipfile
ROOT=Path(__file__).resolve().parents[1]
REPORT=ROOT/'docs/licensing';CACHE=ROOT/'artifacts/licensing/cache';LICENSES=ROOT/'licenses/third-party'
sys.path.insert(0,str(ROOT/'artifacts/tools/license-audit'))
import importlib.util
spec=importlib.util.spec_from_file_location('audit',ROOT/'build/audit-dependencies.py')
audit=importlib.util.module_from_spec(spec);spec.loader.exec_module(audit)

PURPOSES={
'Contracts':'Gemeinsame Verträge und Grundtypen','Domain':'Prüfprojekt-, Variablen- und Ergebnismodell',
'Engine':'Ablaufausführung, Bausteine und Ressourcen-Sperren','Hardware':'Hardware-Abstraktionen und Simulation',
'Identity':'Anmeldung und Berechtigungsverträge','Instruments':'Gerätetreiber, SSH/SFTP, Busse und Netzwerk',
'Persistence':'Projekt-, Ergebnis-, Einstellungs- und Release-Speicherung','DatabaseAdapters':'Datenbank-Verbindungen',
'Notifications':'Meldewege und Benachrichtigungen','Presentation':'Ansichtsmodelle, Editor und Werkersteuerung',
'Workers':'C#/Python/C++-Prozesssteuerung, Laufzeitwahl und GDB','Workers.CSharp':'Isolierter Roslyn-C#-Compiler/Worker',
'Workers.Native':'C++-Compileraufruf und native Ausführung','Desktop':'WPF-Oberfläche, Entra/MSAL, Datenbankprovider und OPC UA'}
SOURCE_PATTERNS={
'SSH.NET':r'Renci\.SshNet','Microsoft.CodeAnalysis':r'Microsoft\.CodeAnalysis',
'Microsoft.Identity.Client':r'Microsoft\.Identity\.Client','OPCFoundation':r'\bOpc\.Ua\b',
'Microsoft.Data.SqlClient':r'Microsoft\.Data\.SqlClient|Microsoft\.Data\.SqlClient\.SqlClientFactory',
'Npgsql':r'\bNpgsql\b','MySqlConnector':r'\bMySqlConnector\b','Microsoft.Data.Sqlite':r'Microsoft\.Data\.Sqlite',
'System.Security.Cryptography.ProtectedData':r'\bProtectedData\b|\bDataProtectionScope\b'}
TOOLS=[
('w64devkit','2.8.0','Unlicense for kit glue; component licenses separately','toolchains/w64devkit','build/acquire-toolchain.ps1; build/CodeWorkers.targets'),
('GCC','16.1.0','GPL-3.0-or-later; runtime exception for eligible runtime use','toolchains/w64devkit/bin/g++.exe','src/Testumgebung.Workers.Native/Toolchain.cs'),
('GDB','17.1','GPL-3.0-or-later','toolchains/w64devkit/bin/gdb.exe','src/Testumgebung.Workers/GdbInspector.cs'),
('GNU Binutils','2.46.0','GPL family, subcomponent licenses in source','toolchains/w64devkit/bin','GCC linker/assembler/toolchain'),
('GNU Make','4.4.1','GPL-3.0-or-later','toolchains/w64devkit/bin/make.exe','Shipped as part of full kit; not directly called by application'),
('BusyBox-w32','FRP-5857-g3681e397f','GPL-2.0-only with component exceptions','toolchains/w64devkit/bin/busybox.exe','Full-kit utilities and launcher aliases'),
('CMake','4.3.2','BSD-3-Clause plus bundled-library licenses','toolchains/w64devkit/bin/cmake.exe','Shipped as part of full kit'),
('Ninja','1.13.2','Apache-2.0','toolchains/w64devkit/bin/ninja.exe','Shipped as part of full kit'),
('CCache','4.13.4','GPL-3.0-or-later','toolchains/w64devkit/bin/ccache.exe','Full-kit compiler cache'),
('Universal Ctags','6.2.1','GPL-2.0 family; see source notices','toolchains/w64devkit/bin/ctags.exe','Shipped as part of full kit'),
('Vim','9.0','Vim license','toolchains/w64devkit/bin/vim.exe','Full-kit editor and alias launchers'),
('MinGW-w64','14.0.0','Mixed permissive/public-domain/LGPL parts; see COPYING','toolchains/w64devkit/include; toolchains/w64devkit/lib','Native compiler headers, import/static libraries, gendef/widl'),
('GMP','6.3.0','LGPL-3.0-or-later OR GPL-2.0-or-later','toolchains/w64devkit','Linked into GNU tools'),
('MPFR','4.2.2','LGPL-3.0-or-later','toolchains/w64devkit','Linked into GNU tools'),
('MPC','1.4.1','LGPL-3.0-or-later','toolchains/w64devkit','Linked into GNU tools'),
('Expat (kit)','2.8.0','MIT','toolchains/w64devkit','Linked into GDB'),
('libiconv','1.19','LGPL-2.1-or-later library; GPL utility','toolchains/w64devkit/bin/iconv.exe','Kit utility and GDB dependency'),
('PDCurses','3.9','Public domain core; port-specific licenses','toolchains/w64devkit','Windows-console support in Vim'),
('xxHash','0.8.3','BSD-2-Clause library; GPL utility portions','toolchains/w64devkit','Dependency of kit tools'),
('Zstandard','1.5.7','BSD-3-Clause OR GPL-2.0-only','toolchains/w64devkit/bin/zstd.exe','Kit tools and BusyBox dependency'),
('dcmake','1.7.1','Unlicense plus bundled MIT/Zlib library notices','toolchains/w64devkit/bin/dcmake.exe','Shipped as part of full kit'),
('7-Zip SFX','23.01','LGPL and unRAR restriction; see source DOC/License.txt','toolchains/.stage-*/w64devkit-x64-2.8.0.7z.exe','Build-only archive extractor; not shipped as an EXE in MSIX'),
('CPython','3.14.8','PSF and included third-party notices','workers/python/runtime/python.exe','src/Testumgebung.Workers/CodeRuntime.cs; PythonEnvironmentResolver.cs'),
('OpenSSL','3.5.9','Apache-2.0','workers/python/runtime/libssl-3.dll; libcrypto-3.dll','Python ssl/hashlib; PE version verified'),
('SQLite (Python)','3.50.4','Public domain','workers/python/runtime/sqlite3.dll','Python sqlite3; PE version verified'),
('libffi','3.4.4','MIT','workers/python/runtime/libffi-8.dll','Python ctypes; version from CPython v3.14.8 build specification'),
('Expat (Python)','2.8.5','MIT','workers/python/runtime/pyexpat.pyd','Version reported by bundled pyexpat.EXPAT_VERSION'),
('libmpdec','4.0.0','BSD-2-Clause','workers/python/runtime/_decimal.pyd','Version reported by bundled _decimal.__libmpdec_version__'),

('bzip2','1.0.8','bzip2 license','workers/python/runtime/_bz2.pyd','CPython build specification; notices in Python LICENSE.txt'),
('XZ/liblzma','5.2.5','Public-domain core plus component GPL/LGPL notices','workers/python/runtime/_lzma.pyd','CPython build specification; determine linked portions before assuming every source-license applies'),
('zlib-ng','2.2.4','Zlib','workers/python/runtime/python314.dll','CPython build specification'),
('zstd (Python)','1.5.7','BSD-3-Clause OR GPL-2.0-only','workers/python/runtime/_zstd.pyd','CPython build specification'),
('MSVC runtime','14.51.36247.0','Microsoft redistributable terms, not MIT','workers/python/runtime/vcruntime140.dll; vcruntime140_1.dll','Bundled by official Python ZIP; PE version verified'),
('pip','26.2.1','MIT plus vendor licenses','workers/python/runtime/pip-bootstrap/pip.whl','build/acquire-pip.ps1; PythonEnvironments.cs'),
('ReportGenerator','5.5.11','Apache-2.0','artifacts/tools/reportgenerator','build/coverage.ps1; development only'),
('ReportLab','4.4.9','BSD-3-Clause','Documentation workflow Python environment','build/create-complete-manual.py; documentation only'),
('Pillow (CI documentation)','12.3.0 in audited Actions run','HPND/Pillow license plus codec notices','GitHub runner Python environment','ReportLab dependency; version from run 38297143493'),
('charset-normalizer (CI documentation)','3.5.2 in audited Actions run','MIT','GitHub runner Python environment','ReportLab dependency; version from run 38297143493'),
('Python (CI documentation)','3.12.x','PSF','GitHub runner','actions/setup-python; exact patch depends on runner'),
('.NET SDK (local)','10.0.401','MIT and third-party/MS build tool terms','External installed SDK','dotnet build/test/publish; not itself shipped'),
('.NET SDK (CI)','10.0.401','MIT and third-party/MS build tool terms','GitHub runner','actions/setup-dotnet; SDK pinned via global.json and workflow'),
('Windows SDK MakeAppx/SignTool','Externally installed; chosen SDK directory','Microsoft SDK terms','Windows Kits/10/bin/<SDK>/x64','build/msix.ps1; build only'),
('PowerShell','7.x for builds; Windows PowerShell external for console','MIT for PowerShell 7; OS terms for Windows PowerShell','External installed runtime','build scripts; PythonEnvironments.cs'),
('Inno Setup','6.x, exact version not pinned','Inno Setup license','External ISCC.exe','build/publish.ps1 -Installer; not used by MSIX workflow'),
('GitHub CLI','2.102.0 local; runner version external','MIT and Go dependency notices','artifacts/tools/github-cli/bin/gh.exe','setup-github.ps1 and GitHub release job; not shipped'),
('Git','External version','GPL-2.0','External git.exe','Source/wikis upload; not shipped'),
('Paramiko','Optional, unpinned','LGPL-2.1-or-later; dependencies separate','External test Python','tests/Fixtures/ssh_server.py; not shipped'),
('Segoe UI / Consolas','Windows-provided, exact installed font version','Microsoft document embedding permissions','C:/Windows/Fonts/segoeui.ttf; segoeuib.ttf; consola.ttf','PDF font subsets; no standalone TTF shipped'),
('pefile','2024.8.26','MIT','artifacts/tools/license-audit','Audit-only PE parser; not shipped'),
('PyYAML','6.0.3','MIT','artifacts/tools/python-docs','Audit/setup-only workflow syntax checker; not shipped')]
TOOLS.extend([
('actions/checkout','v4 / 11d5960a326750d5838078e36cf38b85af677262','MIT','GitHub runner action','Both GitHub workflows; not redistributed'),
('actions/setup-dotnet','v4 / 67a3573c9a986a3f9c594539f4ab511d57bb3ce9','MIT','GitHub runner action','MSIX workflow; not redistributed'),
('actions/setup-python','v5 / a26af69be951a213d495a4c3e4e4022e16d87065','MIT','GitHub runner action','Both GitHub workflows; not redistributed'),
('actions/upload-artifact','v4 / ea165f8d65b6e75b540449e92b4886f43607fa02','MIT','GitHub runner action','Both GitHub workflows; not redistributed')])

EXTERNAL=[
('NI-DAQmx','nicaiu.dll','src/Testumgebung.Instruments/Daqmx.cs','NI license; driver independently installed'),
('VISA','visa64.dll / nivisa64 / ktvisa64','src/Testumgebung.Instruments/Io.cs','NI/Keysight implementation-specific license'),
('PEAK PCAN','PCANBasic.dll','src/Testumgebung.Instruments/Communication/Can.cs','PEAK SDK/driver license'),
('Kvaser CAN','canlib32.dll','src/Testumgebung.Instruments/Communication/Can.cs','Kvaser CANlib license'),
('Vector CAN','vxlapi64.dll','src/Testumgebung.Instruments/Communication/Can.cs','Vector XL Driver Library license'),
('PEAK LIN','PLinApi.dll','src/Testumgebung.Instruments/Communication/Lin.cs','PEAK PLIN API license'),
('LabVIEW VI DLL','User-selected .dll','src/Testumgebung.Workers/LabViewGlue.cs','User/vendor DLL and required LabVIEW runtime license'),
('User .NET DLLs','User-imported .dll','src/Testumgebung.Workers.CSharp/Program.cs','License checked by project owner for each imported DLL'),
('System Win32','kernel32.dll and OS libraries','src/Testumgebung.Instruments/Communication/Transports.cs','Windows OS component; not separately redistributed')]

def normalize_license(row):
    name=row['name'];value=row['metadata'].get('license','UNRESOLVED')
    expected=None
    if name.startswith('OPCFoundation.NetStandard.Opc.Ua.') and row['version']=='1.5.378.182':
        expected='3927c73bc0abbd9327364bcfdcbb7d8a7be50c05c2948fcfb49a45b165bda737'
    elif name=='BitFaster.Caching' and row['version']=='2.6.0':
        expected='f3bf474c62109fbe7b81345419eff56bfd6e5b539accc877a9b840e2e7a5176b'
    if expected:
        if not any(record['sha256']==expected and hashlib.sha256((ROOT/record['path']).read_bytes()).hexdigest()==expected
                   for record in row['metadata'].get('license_files',[])):
            raise ValueError('Reviewed MIT license text changed: '+name)
        return 'MIT'
    if name=='OPCFoundation.NetStandard.Opc.Ua.Core' and row['version']=='1.5.374.126':return 'GPL-2.0-only for nonmembers / RCL for corporate members; mixed source headers'
    if name=='Microsoft.Data.SqlClient.SNI.runtime':return 'Microsoft proprietary redistributable terms (LICENSE.txt)'
    if name=='xunit.abstractions':return 'Apache-2.0 (explicit frozen-abstractions upstream declaration; see UPSTREAM-LICENSE-DECLARATION.md)'
    return value or 'UNRESOLVED (license URL only)'

def attach_usage(data):
    files=[p for p in (ROOT/'src').rglob('*.cs') if 'obj' not in p.parts and 'bin' not in p.parts]
    for row in data['packages'].values():
        row['license_effective']=normalize_license(row)
        row['code_references']=[]
        for package,pattern in SOURCE_PATTERNS.items():
            if row['name']==package or row['name'].startswith(package+'.'):
                for p in files:
                    for i,line in enumerate(p.read_text(encoding='utf-8-sig').splitlines(),1):
                        if re.search(pattern,line):row['code_references'].append(p.relative_to(ROOT).as_posix()+':'+str(i))
    for project in data['projects']:
        project['references']=list(dict.fromkeys(project['references']))
        name=Path(project['project']).stem;short=name.removeprefix('Testumgebung.')
        project['purpose']=PURPOSES.get(short,'Automated tests' if 'Tests' in name else 'Test-only fixture')
        project['version']=data['release_version']+' in audited MSIX' if project['kind']=='product' else 'Default assembly 1.0.0; not shipped'
        project['used_by']=[r['project'] for r in data['projects'] if project['project'] in r['references']]
        if 'CodeWorkers.targets' in (ROOT/project['project']).read_text(encoding='utf-8-sig'):
            for reference in ['src/Testumgebung.Workers.CSharp/Testumgebung.Workers.CSharp.csproj (imported build-only reference)','src/Testumgebung.Workers.Native/Testumgebung.Workers.Native.csproj (imported build-only reference)']:
                if reference not in project['references']:project['references'].append(reference)

def map_manifests(data):
    paths={row['path']:row for row in data['binaries']}
    for manifest,doc in data['release_manifests'].items():
        prefix=Path(manifest).parent.as_posix();prefix='' if prefix=='.' else prefix+'/'
        for target,libs in doc.get('targets',{}).items():
            for key,library in libs.items():
                license=data['packages'].get(key,{}).get('license_effective','Project license pending' if key.startswith('Testumgebung.') else 'Unresolved')
                for field in ['runtime','native','resources','runtimeTargets']:
                    for asset,props in library.get(field,{}).items():
                        candidates=[prefix+asset,prefix+Path(asset).name]
                        if props.get('locale'):candidates.insert(0,prefix+props['locale']+'/'+Path(asset).name)
                        for path in candidates:
                            if path in paths:
                                row=paths[path];row['component']=key;row['license']=license;row['attribution_evidence']=manifest+' :: '+field+' :: '+asset
                                break
    for row in data['binaries']:
        path=row['path'];name=Path(path).name
        if path.startswith('toolchains/w64devkit/'):
            component,license=kit_component(path)
            row['component']=component;row['license']=license;row['attribution_evidence']='w64devkit Dockerfile/source archives; file role inferred from path'
        elif path.startswith('workers/python/runtime/'):
            component='CPython/3.14.8';license='PSF plus relevant native component notices'
            if name.startswith('libssl') or name.startswith('libcrypto'):component='OpenSSL/3.5.9';license='Apache-2.0'
            elif name=='sqlite3.dll':component='SQLite/3.50.4';license='Public domain'
            elif name=='libffi-8.dll':component='libffi/3.4.4';license='MIT'
            elif name=='libtommath.dll':component='libtommath/UNRESOLVED';license='Exact attribution unresolved; upstream Unlicense not treated as proof'
            elif name.startswith('vcruntime'):component='Microsoft VC runtime/14.51.36247.0';license='Microsoft redistributable terms'
            elif name=='pip.whl':component='pip/26.2.1 and vendored packages';license='MIT plus vendor notices'
            row['component']=component;row['license']=license;row['attribution_evidence']='Official CPython 3.14.8 ZIP + PE versions + CPython build specification'
        elif name.startswith('Testumgebung.'):
            row['component']=name.split('.exe')[0].split('.dll')[0]+'/'+data['release_version'];row['license']='MIT (own project; LICENSE)';row['attribution_evidence']='Own project + MSIX PE version'
        row.setdefault('attribution_evidence','UNRESOLVED')
        row['component_version']=row['component'].split('/',1)[-1] if '/' in row['component'] else 'See component metadata'
    # Runtime-pack satellite resources can be absent from .deps.json. Match their
    # actual bytes against the exact NuGet runtime pack, rather than guessing.
    for key,package in data['packages'].items():
        if not key.startswith('runtimepack.'):continue
        folder=Path.home()/'.nuget/packages'/package['name'].removeprefix('runtimepack.').lower()/package['version']
        candidates=defaultdict(list)
        for p in folder.rglob('*.resources.dll'):candidates[p.name].append(p)
        for row in data['binaries']:
            if row['component']:continue
            for candidate in candidates.get(Path(row['path']).name,[]):
                if hashlib.sha256(candidate.read_bytes()).hexdigest()==row['sha256']:
                    row['component']=key;row['component_version']=package['version'];row['license']=package['license_effective']
                    row['attribution_evidence']='SHA256 matches exact runtime-pack resource: '+candidate.relative_to(folder).as_posix()
                    break

def kit_component(path):
    name=Path(path).name.lower()
    for pattern,component,license in [
      ('gdb','GDB/17.1','GPL-3.0-or-later'),('binutils','Binutils/2.46.0','GNU component licenses'),
      ('ccache','CCache/4.13.4','GPL-3.0-or-later'),('cmake','CMake/4.3.2','BSD-3-Clause plus dependencies'),
      ('ctest','CMake/4.3.2','BSD-3-Clause plus dependencies'),('cpack','CMake/4.3.2','BSD-3-Clause plus dependencies'),
      ('ninja','Ninja/1.13.2','Apache-2.0'),('dcmake','dcmake/1.7.1','Unlicense plus bundled library notices'),
      ('ctags','Universal Ctags/6.2.1','GPL-2.0 family'),('vim','Vim/9.0','Vim license'),
      ('zstd','Zstandard/1.5.7','BSD-3-Clause OR GPL-2.0-only')]:
        if pattern in name:return component,license
    if '/gcc/' in path or any(x in name for x in ['gcc','g++','c++','gfortran','gcov','cc1','f951','collect2','lto-','libstdc++','libgomp','libquadmath','libatomic','libssp']):
        return 'GCC/16.1.0','GPL-3.0-or-later for tools; runtime exceptions/component licenses for libraries'
    stem=name.removesuffix('.exe').removeprefix('x86_64-w64-mingw32-')
    if stem in {'as','ld','ld.bfd','ar','nm','ranlib','strings','strip','size','objdump','objcopy','addr2line','dlltool','dllwrap','elfedit','windres','windmc','gprof'}:
        return 'Binutils/2.46.0','GPL/GNU component licenses'
    if stem in {'make','mingw32-make'}:return 'GNU Make/4.4.1','GPL-3.0-or-later'
    if stem in {'gendef','widl'} or path.endswith('.a') or '/include/' in path:
        return 'MinGW-w64/14.0.0','Mixed permissive/public-domain/LGPL parts; COPYING.MinGW-w64-runtime.txt'
    if stem=='iconv':return 'libiconv/1.19','GPL utility / LGPL library'
    if stem in {'w64devkit','peports','vc++filt','pkg-config','debugbreak','uuidgen','recycle','quilt'}:
        return 'w64devkit-glue/2.8.0','Unlicense / embedded-source notices (verify individual file)'
    return 'w64devkit/2.8.0 (BusyBox or launcher/utility role)','Mixed licenses; detailed source and aliases apply, not a single kit-wide Unlicense'

def pip_vendors():
    licenses={'CacheControl':'Apache-2.0','distlib':'Python-2.0','distro':'Apache-2.0','msgpack':'Apache-2.0','packaging':'Apache-2.0 OR BSD-2-Clause','platformdirs':'MIT','pyproject-hooks':'MIT','requests':'Apache-2.0','certifi':'MPL-2.0','idna':'BSD-3-Clause','urllib3':'MIT','rich':'MIT','pygments':'BSD-2-Clause','resolvelib':'ISC','setuptools':'MIT','tomli':'MIT','tomli-w':'MIT','truststore':'MIT'}
    rows=[]
    with zipfile.ZipFile(ROOT/'toolchains/python/pip-bootstrap/pip.whl') as z:
        for name,version in re.findall(r'^\s*([\w-]+)==([^\s]+)',z.read('pip/_vendor/vendor.txt').decode(),re.M):
            folder='pkg_resources' if name=='setuptools' else name.lower().replace('-','_')
            matches=[x for x in z.namelist() if ('/'+folder+'/' in x and re.search(r'(license|copying|notice)',Path(x).name,re.I))]
            rows.append({'name':name,'version':version,'license':licenses.get(name,'UNRESOLVED'),'used_in':'workers/python/runtime/pip-bootstrap/pip.whl!/pip/_vendor/'+folder,'evidence':'pip/_vendor/vendor.txt','license_files':['licenses/third-party/pip-26.2.1/'+x for x in matches]})
    return rows

def write_notices(data):
    lines=['# Drittanbieterhinweise - Testumgebung','','Stand: 13.10.2026. Diese Datei vergibt keine Lizenz für den eigenen Projektcode. Die Fremdkomponenten behalten ihre jeweiligen Lizenzen.','',
      'Diese Sammlung begleitet die aktuelle Distribution. Historische Versionen werden nicht rückwirkend bereinigt. Zusätzliche Bedingungen stehen in THIRD-PARTY-TERMS.md.','',
      '## NuGet und .NET-Laufzeiten','']
    for row in sorted(data['packages'].values(),key=lambda r:r['name'].lower()):
        if not row['bundled_manifests']:continue
        meta=row['metadata'];lines+=['### '+row['name']+' '+row['version'],'',
          '- Lizenz: '+row['license_effective'],'- Copyright laut Paket: '+meta.get('copyright','Nicht in NuSpec angegeben'),
          '- Autoren laut Paket: '+meta.get('authors','Nicht angegeben'),
          '- Upstream: '+meta.get('repository',{}).get('url',meta.get('projectUrl',''))]
        for file in meta.get('license_files',[]):lines.append('- Lizenztext: ['+Path(file['path']).name+']('+file['path']+')')
        lines.append('')
    lines+=['## Python, pip und native Komponenten','',
      'CPython 3.14.8: [Originaltext](licenses/third-party/python/LICENSE.txt). Die pip-Vendor-Lizenzen liegen vollständig unter `licenses/third-party/pip-26.2.1/`. Die zusätzlichen nativen Texte liegen unter `licenses/third-party/python-native/`. Native Microsoft-Laufzeiten behalten ihre eigenen Weitergabebedingungen.','',
      'libtommath.dll wird seit dem bereinigten Build nicht mehr mitgeliefert; das Originalarchiv bleibt als historische Nachweisquelle erhalten.','',
      '## GNU-/w64devkit-Werkzeuge','',
      'Die gesamte Toolchain wird weitergegeben, nicht nur GCC und GDB. Das Gesamtpaket hat unterschiedliche Lizenzen. Volltexte und Copyright-Hinweise wurden aus den passenden Source-Archiven übernommen: `licenses/third-party/w64devkit/`.','',
      'Die zum Binärpaket 2.8.0 passenden Archive, Anpassungen und Buildskripte befinden sich im MSIX unter `toolchains/w64devkit/source/source.tar`, `toolchains/w64devkit/src/` und `toolchains/w64devkit/Dockerfile`. Der Quellen-SHA256 ist `5f76bc3b87ccfe4c3eb3d6045614cc983419a955f78f65f8921c10bb1f90e654`.','',
      'Bei jeder Weitergabe müssen diese Quellen und Hinweise erhalten bleiben. GNU-Bibliotheken mit LGPL können weitere Bedingungen für Modifikationen beziehungsweise Relinking haben; die Komponentenzuordnung ist im Prüfbericht erläutert.','',
      '## Extern installierte Treiber','',
      'NI-DAQmx/VISA, PEAK, Kvaser, Vector und benutzerdefinierte DLLs sind nicht Bestandteil des geprüften MSIX. Ihre Installation und Nutzung unterliegen den jeweiligen Hersteller-/Projektlizenzen.','']
    (ROOT/'THIRD-PARTY-NOTICES.md').write_text('\n'.join(lines),encoding='utf-8')

def write_inventory(data):
    lines=['# Vollständiges Abhängigkeitsinventar','','Prüfstand: 13.10.2026. Binärstand: MSIX '+data['release_version']+'. Die Version einer NuGet-Komponente kann von ihrer Assembly-/Dateiversion abweichen. Tabellen unterscheiden deshalb Paket-, Produkt- und Dateiversionen.','',
      '## Dateien für die genaue Auswertung','',
      '- [Alle eigenen Projekte](projects.csv)','- [NuGet-Versionen, Lizenzen und Verwendungsorte](nuget.csv)','- [Jede ausgelieferte Binär-/Bibliotheksdatei](binaries.csv)','- [Prüfsummen aller Lizenz- und Hinweisdateien](license-files.csv)',
      '- [Aufgelöste NuGet-Abhängigkeitskanten je Projekt/Framework](dependency-edges.csv)','- [Werkzeuge, Laufzeiten und native Bestandteile](tools.csv)',
      '- [In pip eingebettete Pakete](pip-vendors.csv)','- [Externe Treiber und DLLs](external.csv)','- [Vollständige Rohdaten und Release-Manifeste](inventory.json)','',
      'Die CSV-Dateien verwenden UTF-8 mit BOM und lassen sich in Excel importieren. `binaries.csv` enthält auch 945 statische `.a`-Bibliotheken; die '+str(data['summary']['binary_files'])+' Zeilen sind keine 2.104 unabhängigen Produkte. Mehrfach kopierte .NET-Worker-Dateien und Kit-Aliasprogramme sind absichtlich enthalten.','',
      '## Eigene Projekte','', '| Projekt | Framework | Version im Prüfstand | Aufgabe |','| --- | --- | --- | --- |']
    for project in data['projects']:lines.append('| '+ ' | '.join([project['project'],project['framework'],project['version'],project['purpose']])+' |')
    lines+=['','Die vollständigen Projektverweise und umgekehrten Verwendungsorte stehen in `projects.csv`; importierte Worker-Buildreferenzen sind dort ausdrücklich markiert.','',
      '## NuGet-Paketversionen','', '| Paket | Version | Lizenz | Verwendung im MSIX |','| --- | --- | --- | --- |']
    for row in sorted(data['packages'].values(),key=lambda r:(r['name'].lower(),r['version'])):
        manifests=', '.join(Path(p).parent.as_posix() for p in row['bundled_manifests']) or 'Nicht im Release-Manifestsatz; nur Restore/Build/Test'
        lines.append('| '+' | '.join([row['name'],row['version'],row['license_effective'],manifests])+' |')
    lines+=['','`nuget.csv` nennt zusätzlich direkte und transitive Verwendungsprojekte sowie konkrete Quelltextstellen für die direkt verwendeten APIs. Ein Restore-Eintrag allein bedeutet nicht, dass seine DLL im MSIX liegt. Die tatsächliche Zuordnung erfolgt über die Release-`.deps.json` und anschließend vorhandene Binärpfade.','',
      '## Werkzeuge und weitere Komponenten','', '| Komponente | Version | Lizenz/Status | Verwendung |','| --- | --- | --- | --- |']
    for row in data['tools']:lines.append('| '+' | '.join([row['name'],row['version'],row['license'],row['used_by']])+' |')
    lines+=['','## pip-Unterpakete','', '| Paket | Version | Lizenz |','| --- | --- | --- |']
    for row in data['pip_vendors']:lines.append('| '+' | '.join([row['name'],row['version'],row['license']])+' |')
    lines+=['','## Externe, nicht gebündelte Treiber','', '| Komponente | Datei | Version | Quellverwendung |','| --- | --- | --- | --- |']
    for row in data['external']:lines.append('| '+' | '.join([row['name'],row['file'],row['version'],row['used_in']])+' |')
    lines+=['','## Genauigkeit und Grenzen','',
      'Numerische Versionen werden nur genannt, wenn sie im Paket, im Quellarchiv, in einer PE-Versionsressource oder im versionsgenauen Buildskript belegt sind. `UNRESOLVED` bedeutet, dass kein belastbarer Versionsnachweis vorliegt. Für extern installierte Software lässt sich keine Version aus dem Projekt erzwingen.','',
      'Manche EXEs sind Alias-Launcher ohne eigene Versionsressource. Bei GNU-Utilities, Headern und statischen Bibliotheken beruht die Komponentenzuordnung auf dem unveränderten Kit und seinen Quellen; solche Zuordnungen sind in `attribution_evidence` als abgeleitet markiert. Die genaue Unterlizenz wird durch die Originaltexte und Quellkopfzeilen bestimmt, nicht durch den allgemeinen Kit-Namen.','',
      'Historische lokale Bundles 1.0.0.0 bis 1.0.12.0 wurden hinsichtlich ihrer vorhandenen Versionen erfasst, aber nicht alle erneut bis zur letzten Datei inventarisiert. Die genaue Dateiliste gilt für das aktuelle GitHub-MSIX 1.0.12.3. Frühere Veröffentlichungen erhalten fehlende Lizenztexte nicht rückwirkend.']
    (REPORT/'inventar.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')

def main():
    data=json.loads((REPORT/'inventory.json').read_text(encoding='utf-8'))
    match=re.search(r'Testumgebung_(\d+\.\d+\.\d+\.\d+)_',Path(data['bundle']).name)
    if not match:raise ValueError('Release version cannot be established from selected bundle')
    data['release_version']=match.group(1)
    attach_usage(data);map_manifests(data)
    for row in data['packages'].values():
        if row['name']=='xunit.abstractions':
            folder=LICENSES/'nuget'/'xunit.abstractions'/row['version']
            for name in ['LICENSE.txt','UPSTREAM-LICENSE-DECLARATION.md']:
                path=folder/name
                record={'path':path.relative_to(ROOT).as_posix(),'sha256':hashlib.sha256(path.read_bytes()).hexdigest()}
                row['metadata']['license_files']=[r for r in row['metadata'].get('license_files',[]) if r['path']!=record['path']]+[record]
    data['tools']=[dict(zip(['name','version','license','location','used_by'],t)) for t in TOOLS]
    data['external']=[{'name':name,'file':file,'version':'Externally installed; not pinned and not bundled','used_in':used,'license':license} for name,file,used,license in EXTERNAL]
    data['pip_vendors']=pip_vendors()
    data['summary']={'projects':len(data['projects']),'nuget_package_versions':len(data['packages']),'bundled_nuget_versions':sum(bool(r['bundled_manifests']) for r in data['packages'].values()),'binary_files':len(data['binaries']),'toolchain_archives':len(data['toolchain_sources']),'pip_vendors':len(data['pip_vendors'])}
    for row in data['packages'].values():
        meta=row['metadata'];copyright=meta.get('copyright','Copyright not specified in nuspec; see authors and upstream')
        target=LICENSES/'nuget'/meta.get('id',row['name'])/row['version']/'PACKAGE-NOTICE.txt'
        target.parent.mkdir(parents=True,exist_ok=True)
        text=row['name']+' '+row['version']+'\n'+copyright+'\nAuthors: '+meta.get('authors','')+'\nDeclared license: '+row['license_effective']+'\nRepository: '+meta.get('repository',{}).get('url','')+'\n'
        target.write_bytes(text.encode('utf-8'))
        record={'path':target.relative_to(ROOT).as_posix(),'sha256':hashlib.sha256(text.encode()).hexdigest()}
        meta['license_files']=[f for f in meta.get('license_files',[]) if f['path']!=record['path']]+[record]
    (REPORT/'inventory.json').write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf-8')
    audit.csv_write('projects.csv',data['projects']);audit.csv_write('binaries.csv',data['binaries'])
    audit.csv_write('tools.csv',data['tools']);audit.csv_write('external.csv',data['external']);audit.csv_write('pip-vendors.csv',data['pip_vendors'])
    audit.csv_write('nuget.csv',[{'name':r['name'],'version':r['version'],'license':r['license_effective'],'license_type':r['metadata'].get('license_type',''),'direct_in':r['direct_in'],'resolved_in':r['projects'],'code_references':r['code_references'],'bundled_manifests':r['bundled_manifests'],'copyright':r['metadata'].get('copyright',''),'authors':r['metadata'].get('authors',''),'license_files':[x['path'] for x in r['metadata'].get('license_files',[])]} for r in sorted(data['packages'].values(),key=lambda r:(r['name'].lower(),r['version']))])
    write_inventory(data);write_notices(data)
    audit.csv_write('license-files.csv',[{'path':p.relative_to(ROOT).as_posix(),'size':p.stat().st_size,'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in sorted(LICENSES.rglob('*')) if p.is_file()])
    print(json.dumps(data['summary'],indent=2));print('Unattributed binaries:',sum(not r['component'] for r in data['binaries']))
if __name__=='__main__':main()
