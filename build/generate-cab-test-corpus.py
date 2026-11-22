import json,re,base64
from pathlib import Path
root=Path('.')
catalog=json.loads((root/'docs/cab/jscript-befehlskatalog.json').read_text(encoding='utf-8-sig'))['eintraege']
content={r['id']:r['source'] for r in json.loads((root/'tests/Fixtures/CabJScript/content-fields.json').read_text(encoding='utf8'))}
out=root/'tests/Fixtures/cab';out.mkdir(exist_ok=True)
scripts={
'1.3':'m m\rJ\rT 00,01,0,3,3;Spacing\rA 01\r',
'1.4':'\x1ba'+'m m\rJ\rT 0,0,0,3,3;[DAY]\r; own comment\rA 1\r',
'1.5':'J\rM l LBL;/usbmem/demo\rA 1\r','1.6':'J\rT 0,0,0,3,3;One\rA 1\r',
'2.1':'\x1b\x1b','2.2':'\x1b!\x1b!','2.3':'\x1b.\0opaque\x1b.','2.4':'\x1b:\0opaque\x1bend-of-data',
'2.9':'\x1bend-of-data','2.15':'\x1boUTF-8;','2.16':'\x1bp0','2.17':'\x1bp1','2.21':'\x1bv1','2.22':'\x1bxinSTART;','2.23':'\x1bxout',
'3.1':'a\r','3.2':'c\r','3.3':'d BMP;demo\r\x1b.\0opaque\x1b.\r',
'3.4':'e BMP;demo\r','3.5':'f\r','3.6':'i Synthetic\r','3.7':'j ExampleJob\r','3.8':'l GR\r','3.9':'m m\r',
'3.10':'p 0\r','3.11':'q b;demo\r','3.12':'r\r','3.13':'s 261122120000\r','3.14':'t 0\r','3.15':'v\r','3.16':'x 1\r','3.17':'z 0\r',
'4.1':'J\rA 1\r','4.2':'J\rB 0,0,0,CODE128,10,0.3;123\rA 1\r',
'4.3':'J\rC 1\rA 1\r','4.4':'J\rD 0,0\rA 1\r','4.5':'J\rE DBF;demo\rA 1\r',
'4.5.1':'J\rE DBF;demo\rA 1\r','4.5.2':'J\rE LOG;demo,C:UTF-8\rA 1\r',
'4.5.3':'J\rE OPCUA-1;opc.tcp://localhost:4840\rA 1\r','4.5.4':'J\rE RFID;T:Auto,R:1,C:UTF-8,P:0,E:10\rA 1\r',
'4.5.5':'J\rE SQL;127.0.0.1:3306\rA 1\r','4.5.6':'J\rE SQLITE;/usbmem/demo.db\rA 1\r','4.5.7':'J\rE TMP;demo\rA 1\r',
'4.6':'J\rF 100;demo\rA 1\r','4.7':'J\rG 0,0,0;L:10,0.3\rA 1\r',
'4.7.1':'J\rG 0,0,0;C:10,5,0.3,[F:25%]\rA 1\r','4.7.2':'J\rG 0,0,0;L:10,0.3,s,a,[D:1,1]\rA 1\r',
'4.7.3':'J\rG 0,0,0;R:10,5,0.3,0.3,[O]\rA 1\r','4.8':'J\rH 100,0\rA 1\r','4.9':'J\rI 0,0,0,1,1;demo\rA 1\r',
'4.10':'J\rA 1\r','4.11':'J\rM r\rA 1\r','4.11.1':'J\rMc usbmem\rA 1\r','4.11.2':'J\rMd LBL;demo\rA 1\r',
'4.11.3':'J\rM f;demo\rA 1\r','4.11.4':'J\rM l LBL;demo\rA 1\r','4.11.5':'J\rM r\rA 1\r','4.11.6':'J\rM s LBL;demo\rA 1\r','4.11.7':'J\rM u LBL;demo\rA 1\r',
'4.12':'J\rO M\rA 1\r','4.13':'J\rP\rA 1\r','4.14':'J\rT:Data;0,0,0,3,3;Before\rR Data;After\rA 1\r',
'4.15':'J\rS e;0,0,40,40,60\rA 1\r','4.16':'J\rT:Text;0,0,0,3,3;Own text\rA 1\r',
'4.17':'J\rW:Rich;0,0,0,40,20,3,3;<HTML>\r<p>Own text</p>\r</HTML>\rA 1\r','4.18':'J\rX 0;aF\rA 1\r',
'6.1':'; Own synthetic comment\r','6.2':'<ABC>\rprint 2\r</ABC>\r','6.3':'<ENCRYPTED LABEL: 123456789012>\ropaque\r','6.4':'<ENCRYPTED JOB>\ropaque\r</ENCRYPTED JOB>\r'}
refs={'4.2.1':'4.2','4.2.2':'4.2','4.2.17':'4.2.19','4.2.32':'4.2.3','4.2.40':'4.2.9'}
scripts['4.2.1']=scripts['4.2.2']=scripts['4.2']
scripts['4.2.17']='J\rB 0,0,0,EAN128,10,0.3;[U:FNC1]00012345678901234560\rA 1\r'
scripts['4.2.32']='J\rB 0,0,0,2OF5INTERLEAVED+MOD10,10,0.3,2;0123456789012\rA 1\r'
scripts['4.2.40']='J\rB 0,0,0,CODE39,10,0.3,2;-0000000\rA 1\r'
data={3:'1234',4:'12',5:'12345',7:'A12B',13:'12345678901',15:'1234567',16:'123456789012',18:'A',20:'1234',21:'1234',22:'00000000000000',23:'00000000000000',24:'00000000000000',25:'00000000000000',26:'00000000000000',32:'0123456789012',36:'1234',38:'ABCD09',39:'12345',40:'-0000000',43:'12345678901',44:'1234567',45:'0123456'}
attrs=[];manifest=[];samples=[];witnesses=[]
def cs(v):return json.dumps(v,ensure_ascii=False)
for entry in catalog:
    id=entry['id'];section=entry['abschnitt'];mode='command';expected='JS-'+refs.get(section,section)
    if entry['art']=='inhaltsfeld':
        source='J\rT:Data;0,0,0,3,3;ABCDE[I]\rT:Num;0,0,0,3,3;12,5[I]\rT:List;0,0,0,3,3;Red[U:GS]Blue[I]\rT:HexData;0,0,0,3,3;FF[I]\rT 0,0,0,3,3;'+content[id]+'\rA 1\r';mode='content'
    elif section in scripts:source=scripts[section]
    elif entry['art']=='esc':source=entry['befehl'].replace('ESC','\x1b')
    elif entry['art']=='barcode':
        n=int(section.split('.')[-1]);ps={p['name']:p for p in entry['parameter']};typ=ps['type']['werte'][0]['wert'];dims=[]
        if 'dotsize' in ps:dims=['0.3']
        elif 'size' in ps:dims=['SC2']
        elif 'height' in ps:
            dims=['10']
            if 'ne' in ps:dims+=['0.3']
            if 'ratio' in ps:dims+=['2']
        source='J\rB 0,0,0,'+typ+(','+','.join(dims) if dims else '')+';'+data.get(n,'ABC123')+'\rA 1\r'
    else:raise ValueError(id)
    if section.startswith('1.'):mode='context'
    elif section in refs:mode='reference'
    file=id+'.lbl';(out/file).write_bytes(source.encode('utf8'))
    attrs.append(f'[CatalogCase({cs(id)}, {cs(file)}, Mode = {cs(mode)}, DefinitionId = {cs(expected)})]')
    manifest.append({'id':id,'file':file,'mode':mode,'definition':expected})
    samples.append({'id':id,'mode':mode,'definition':expected,'bytes':base64.b64encode(source.encode('utf8')).decode('ascii'),'content':content.get(id)})
    if entry['art']=='barcode':
        invalid=source.replace('B 0,0,0,','B 0,0,45,',1)
        (out/(id+'-invalid.lbl')).write_bytes(invalid.encode('utf8'))
(out/'catalog-cases.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n',encoding='utf8')
templates={'frn':'fr1','fln':'fl1','fun':'fu1','fdn':'fd1','qn':'q100','hn':'h1','mn':'m1','+WSn':'+WS1','+ELx':'+EL1','+CCn':'+CC1','+ROWSx':'+ROWS1','+COLSx':'+COLS1','+MODELx':'+MODEL2','+MODEx':'+MODE2','+VERSIONx':'+VERSION1','+MODxx':'+MOD10','[D:line,gap]':'[D:1,1]','[F:value]':'[F:25%]','[ECE:...]':'[ECE:1]','[APPEND:m,n,id1,id2]':'[APPEND:1,2,A,B]','[APPEND:x,id]':'[APPEND:1,A]','[U:xxxx]':'[U:GS]','[S:%1]':'[S:0]','[S:%1,%2]':'[S:0,100]','[S:%1,%2,direction]':'[S:0,100,90]','<num>':'1','[<VAR>]':'[Counter]','[PREVIEW[:y]]':'[PREVIEW:ALL]'}
for entry in catalog:
    for p in entry['parameter']:
        for v in p['werte']:
            value=v['wert'];actual=templates.get(value,value)
            if value.endswith(';name'):actual=value[:-5]
            if re.match(r'^\+(GOODBAD|VERIFY|EXTERN)a',value):actual='+'+re.match(r'^\+(GOODBAD|VERIFY|EXTERN)',value)[1]+'1:2:3'
            if re.match(r'^A[0-4]=y$',value):actual=value[:-1]+'1'
            if entry['id']=='JS-4.12' and value=='Wix':actual='Wi1'
            if entry['id']=='JS-5.66' and p['name']=='Mx' and value=='!':actual='!1'
            if entry['id']=='JS-5.68' and p['name'] in ('source','target') and value=='U':actual='U:01'
            def availability(meta,g,inherit=None):
                cells=meta.get('generationen');return inherit if cells is None else cells.get(g)
            gen=next((g for g in ('X4','X3','X2','Ax') if availability(v,g,availability(p,g,availability(entry,g)))=='y'),'X4')
            attrs.append(f'[CatalogCase({cs(entry["id"])}, "parameter-probe.lbl", Parameter = {cs(p["name"])}, Value = {cs(value)}, Input = {cs(actual)}, Generation = CabPrinterGeneration.{gen})]')
            witnesses.append({'id':entry['id'],'parameter':p['name'],'value':value,'example':actual})
(out/'parameter-probe.lbl').write_bytes(b'J\rT:Probe;0,0,0,3,3;__VALUE__\rA 1\r')
code='// Generated by build/generate-cab-test-corpus.py; changes require deliberate review.\nusing Testumgebung.Domain.Labels.CabJScript;\n\nnamespace Testumgebung.Engine.Tests;\n\n'+'\n'.join(attrs)+'\ninternal sealed class CabCatalogCases { }\n'
(root/'tests/Engine.Tests/CabCatalogCases.Generated.cs').write_text(code,encoding='utf8')
edges={
    'line-endings':b'm m\rJ\nT 00,01,0,3,3;First\r\nA 01\rJ\r\nF 100;Alias\nT 0,0,0,3,3;Second\nA 1\r',
    'units-jobs':b'm i\rJ\rT .1,.2,0,3,.1;Inches\rA 1\rm m\nJ\nT 1,2,0,3,3;Millimetres\nA 1\n',
    'esc-middle':b'J\rT:Text;0,0,0,3,3;'+bytes([0xc3,27,ord('?'),0xa4])+b'\rA 1\r',
    'downloads-three':b'd BMP;one\r\x1b.\x00\xff\x1b.\rd BMP;two\r\x1b:\x00\xff\x1bend-of-data\rd BMP;three\r<BASE64>\rAP8=\r</BASE64>\rJ\rA 1\r',
    'codepages':b'J\rT 0,0,0,3,3;Latin\x1boISO-8859-1;\xe4\r\x1boUTF-8;T 0,0,0,3,3;'+ '漢字'.encode('utf8')+b'\rA 1\r',
    'encrypted':b'<ENCRYPTED JOB>\ropaque\r</ENCRYPTED JOB>\r<ENCRYPTED LABEL: 123456789012>\ropaque\x00\xff',
    'comments':b'; synthetic\x1baJ\rJ\r; another comment\rT 0,0,0,3,3;[literal]\rA 1\r'
}
for name,data in edges.items():(out/('edge-'+name+'.lbl')).write_bytes(data)
for file in ('mixed-jobs.lbl','opaque-blocks.lbl'):
    (out/('retained-'+file)).write_bytes((root/'tests/Fixtures/CabJScript'/file).read_bytes())
parameters=json.loads((root/'tests/Fixtures/CabJScript/parameter-values.json').read_text(encoding='utf8'))
(root/'docs/cab/jscript-vorlagen.json').write_text(json.dumps({'version':1,'samples':samples,'values':witnesses,'parameters':parameters},ensure_ascii=False,indent=2)+'\n',encoding='utf8')
