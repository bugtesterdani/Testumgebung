"""Create the combined handbook from all project documentation (bundled Python/reportlab)."""
from pathlib import Path
import re, html, json, textwrap, os
from reportlab.pdfgen import canvas
from reportlab.platypus import SimpleDocTemplate, Paragraph, Spacer, PageBreak, Image, Table, TableStyle, Preformatted
from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
from reportlab.lib import colors
from reportlab.lib.enums import TA_LEFT
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus.tableofcontents import TableOfContents
from reportlab.lib.utils import ImageReader

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'output/pdf/Testumgebung-Gesamthandbuch.pdf'
TMP = ROOT / 'tmp/pdfs'
OUT.parent.mkdir(parents=True, exist_ok=True)
TMP.mkdir(parents=True, exist_ok=True)
for name, file in [('Body','segoeui.ttf'),('Bold','segoeuib.ttf'),('Mono','consola.ttf')]:
    pdfmetrics.registerFont(TTFont(name, str(Path('C:/Windows/Fonts') / file)))
pdfmetrics.registerFontFamily('Body',normal='Body',bold='Bold',italic='Body',boldItalic='Bold')
styles=getSampleStyleSheet()
styles.add(ParagraphStyle(name='Text',fontName='Body',fontSize=10,leading=14,spaceAfter=7,textColor=colors.HexColor('#243247')))
styles.add(ParagraphStyle(name='Chapter',fontName='Bold',fontSize=22,leading=27,spaceAfter=18,textColor=colors.HexColor('#123d65'),keepWithNext=True))
styles.add(ParagraphStyle(name='Section',fontName='Bold',fontSize=14,leading=19,spaceBefore=12,spaceAfter=8,keepWithNext=True,textColor=colors.HexColor('#123d65')))
styles.add(ParagraphStyle(name='Subsection',fontName='Bold',fontSize=11,leading=15,spaceBefore=9,spaceAfter=6,keepWithNext=True))
styles.add(ParagraphStyle(name='Small',fontName='Body',fontSize=8,leading=11,spaceAfter=7,textColor=colors.HexColor('#56677b')))
styles.add(ParagraphStyle(name='Cell',fontName='Body',fontSize=8.3,leading=11,spaceAfter=0))
styles.byName['Code']=ParagraphStyle(name='Code',fontName='Mono',fontSize=8,leading=10,spaceAfter=0,backColor=colors.HexColor('#f1f4f8'),borderPadding=3)
W,H=595.276,841.89
CW=W-88
order=['handbuch/benutzerhandbuch.md','handbuch/werker.md','handbuch/entwickler.md','handbuch/administrator.md','installation.md','msix-release.md','betriebsmodus-und-laufzeit.md','entra-registrierung.md','beispiele/README-auftragsauswahl.md','zwei-pruefplaetze.md','geraete.md','chroma-und-eaq.md','eigene-geraete.md','dynamische-verbindungen.md','dotnet-dlls.md','handbuch-csharp-dll.md','portable-python.md','portable-cpp-toolchain.md','testabdeckung-und-wartbarkeit.md','pilot/abnahmeprotokoll.md','handbuch/bilder/README.md']
known=[ROOT/'docs'/p for p in order]
additional=sorted(p for p in (ROOT/'docs').rglob('*.md') if p not in known and 'adr' not in p.parts)
files=known+additional+sorted((ROOT/'docs/adr').glob('*.md'))
assert set(files)==set((ROOT/'docs').rglob('*.md')), 'A documentation file is missing from the manifest'
def clean(s):
    return s.replace('\u2013','-').replace('\u2014','-').replace('\u2011','-').replace('\u00a0',' ')
def slug(s):
    return re.sub(r'[^\w\- ]','',s.lower()).replace(' ','-')
anchors={}
chapter_titles=[]
for i,p in enumerate(files):
    headings=re.findall(r'^#{1,6}\s+(.+)$',p.read_text(encoding='utf-8-sig'),re.M)
    chapter_titles.append(headings[0] if headings else p.stem)
    anchors[(str(p.resolve()),'')]=f'doc{i}'
    for j,title in enumerate(headings):
        anchors[(str(p.resolve()),slug(title))]=f'doc{i}h{j}'
def inline(s,p):
    s=html.escape(clean(s))
    def link(m):
        label,url=m.groups(); url=html.unescape(url)
        if url.startswith(('https://','http://','mailto:')):
            return f'<link href="{html.escape(url,quote=True)}" color="#145ea8">{label}</link>'
        path,_,fragment=url.partition('#')
        target=(p.parent/path).resolve() if path else p.resolve()
        key=anchors.get((str(target),fragment)) or anchors.get((str(target),''))
        if key:return f'<link href="#{key}" color="#145ea8">{label}</link>'
        return label+' <font color="#56677b">('+html.escape(url)+')</font>'
    s=re.sub(r'\[([^\]]+)\]\(([^)]+)\)',link,s)
    s=re.sub(r'`([^`]+)`',r'<font name="Mono">\1</font>',s)
    s=re.sub(r'\*\*(.+?)\*\*',r'<b>\1</b>',s)
    return s

class Manual(SimpleDocTemplate):
    def afterFlowable(self,f):
        if hasattr(f,'bookmark'):
            self.canv.bookmarkPage(f.bookmark)
            self.canv.addOutlineEntry(f.outline,f.bookmark,f.level,False)
            if f.level<=1:
                self.notify('TOCEntry',(f.level,f.outline,self.page,f.bookmark))
def footer(c,d):
    c.saveState();c.setStrokeColor(colors.HexColor('#d9e2ed'));c.line(44,40,W-44,40)
    c.setFont('Body',8);c.setFillColor(colors.HexColor('#56677b'))
    c.drawString(44,27,'Testumgebung | Gesamthandbuch | 12.10.2026')
    c.drawRightString(W-44,27,str(d.page));c.restoreState()
story=[]; images=[]; code_count=0; table_count=0
story += [Spacer(1,85),Paragraph('Testumgebung',styles['Chapter']),Paragraph('Gesamthandbuch',ParagraphStyle('Cover',parent=styles['Chapter'],fontSize=32,leading=39)),Spacer(1,24),Paragraph('Bedienung, Einrichtung und Entwicklung',styles['Section']),Paragraph('Stand der Dokumentation: 12.10.2026<br/>Softwarestand: 1.0.12.0',styles['Text']),Spacer(1,25),Paragraph('Alle Anleitungen des Projekts in einem Dokument. Mit Abbildungen, vollständigen Codebeispielen und einem technischen Anhang.',styles['Text']),Paragraph('Die ergänzenden Anleitungen enthalten die neueren Funktionen. Versionsangaben innerhalb einzelner Kapitel kennzeichnen deren ursprünglichen Dokumentationsstand. Für die Doppelplatzanzeige gilt die aktuelle Einrichtung unter Einstellungen → Werkeransicht, wie im Kapitel „Zwei Prüfplätze“ beschrieben.',styles['Text']),Paragraph('Navigation: Inhaltsverzeichnis und PDF-Lesezeichen führen direkt zu den Kapiteln. Verweise auf enthaltene Markdown-Anleitungen sind im PDF intern verknüpft. Andere Dateien und Beispielprojekte verbleiben im Projekt; deren Pfade werden genannt.',styles['Text']),PageBreak(),Paragraph('Inhaltsverzeichnis',styles['Chapter'])]
toc=TableOfContents();toc.levelStyles=[ParagraphStyle('Toc0',fontName='Bold',fontSize=10,leading=14,spaceBefore=7),ParagraphStyle('Toc1',fontName='Body',fontSize=8.7,leading=12,leftIndent=14,spaceBefore=2)]
story.extend([toc,PageBreak()])
for idx,p in enumerate(files):
    lines=p.read_text(encoding='utf-8-sig').splitlines();hi=0;k=0
    chapter=Paragraph(f'{idx+1}. {inline(chapter_titles[idx],p)}',styles['Chapter'])
    chapter.bookmark=f'doc{idx}';chapter.outline=clean(f'{idx+1}. {chapter_titles[idx]}');chapter.level=0
    story.append(chapter);story.append(Paragraph('Quelle: docs/'+p.relative_to(ROOT/'docs').as_posix(),styles['Small']))
    if p.parent.name=='adr' and (idx==0 or files[idx-1].parent.name!='adr'):story.append(Paragraph('Technischer Anhang: Architekturentscheidungen. Diese Dokumente erklären Entwurfsentscheidungen und historische Entwicklungsstände; für die Bedienung sind die vorangehenden Anleitungen maßgeblich.',styles['Text']))
    while k<len(lines):
        line=lines[k];k+=1
        if not line.strip():continue
        hm=re.match(r'^(#{1,6})\s+(.+)',line)
        if hm:
            level,title=hm.groups();key=f'doc{idx}h{hi}';hi+=1
            if len(level)==1 and hi==1:
                story.append(Paragraph(f'<a name="{key}"/>',styles['Small']));continue
            f=Paragraph(f'<a name="{key}"/>'+inline(title,p),styles['Section' if len(level)==2 else 'Subsection'])
            if len(level)==2:f.bookmark=key;f.outline=clean(title);f.level=1
            story.append(f);continue
        if line.strip().startswith('```'):
            code=[]
            while k<len(lines) and not lines[k].strip().startswith('```'):
                raw=clean(lines[k]).expandtabs(4)
                code.extend(textwrap.wrap(raw, width=99,replace_whitespace=False,drop_whitespace=False,subsequent_indent='    ') or ['']);k+=1
            k+=1;code_count+=1
            # Individual lines keep even lengthy examples splittable across pages.
            for row in code:story.append(Preformatted(row,styles['Code']))
            story.append(Spacer(1,5));continue
        im=re.fullmatch(r'\s*!\[([^\]]*)\]\(([^)]+)\)\s*',line)
        if im:
            caption,path=im.groups();ip=(p.parent/path).resolve()
            if not ip.exists():raise FileNotFoundError(ip)
            iw,ih=ImageReader(str(ip)).getSize();scale=min(CW/iw,350/ih)
            story.append(Image(str(ip),width=iw*scale,height=ih*scale));story.append(Paragraph(inline(caption,p),styles['Small']));images.append(str(ip));continue
        if line.strip().startswith('|') and k<len(lines) and re.match(r'^\s*\|?[ :\-|]+\|\s*$',lines[k]):
            rows=[[c.strip() for c in line.strip().strip('|').split('|')]];k+=1
            while k<len(lines) and lines[k].strip().startswith('|'):
                rows.append([c.strip() for c in lines[k].strip().strip('|').split('|')]);k+=1
            n=max(map(len,rows));data=[]
            for ri,row in enumerate(rows):
                row+=['']*(n-len(row))
                data.append([Paragraph(('<b>'+inline(c,p)+'</b>') if ri==0 else inline(c,p),styles['Cell']) for c in row])
            weights=[max(8,min(45,max(len(row[j]) for row in rows))) for j in range(n)];total=sum(weights)
            t=Table(data,colWidths=[CW*w/total for w in weights],repeatRows=1,hAlign='LEFT')
            t.setStyle(TableStyle([('BACKGROUND',(0,0),(-1,0),colors.HexColor('#e5edf6')),('VALIGN',(0,0),(-1,-1),'TOP'),('GRID',(0,0),(-1,-1),0.35,colors.HexColor('#c9d5e2')),('LEFTPADDING',(0,0),(-1,-1),6),('RIGHTPADDING',(0,0),(-1,-1),6),('TOPPADDING',(0,0),(-1,-1),5),('BOTTOMPADDING',(0,0),(-1,-1),5)]))
            story.extend([t,Spacer(1,9)]);table_count+=1;continue
        if re.match(r'^\s*([-*_])\1{2,}\s*$',line):story.append(Spacer(1,7));continue
        bullet=re.match(r'^\s*(?:[-*+] |(\d+)\. )(.*)',line)
        if bullet:
            num,body=bullet.groups();story.append(Paragraph((num+'. ' if num else '• ')+inline(body,p),styles['Text']));continue
        if line.lstrip().startswith('>'):line=line.lstrip()[1:].strip()
        paragraph=[line]
        while k<len(lines) and lines[k].strip() and not re.match(r'^\s*(?:#|```|\||!\[|[-*+] |\d+\. |> )',lines[k]):paragraph.append(lines[k]);k+=1
        story.append(Paragraph(inline(' '.join(paragraph),p),styles['Text']))
    if idx<len(files)-1:story.append(PageBreak())
doc=Manual(str(OUT),pagesize=(W,H),rightMargin=44,leftMargin=44,topMargin=43,bottomMargin=54,title='Testumgebung - Gesamthandbuch',author='Testumgebung',allowSplitting=True)
doc.multiBuild(story,onFirstPage=footer,onLaterPages=footer)
(TMP/'manual-manifest.json').write_text(json.dumps({'sources':[str(p.relative_to(ROOT)) for p in files],'images':images,'code_blocks':code_count,'tables':table_count,'output':str(OUT)},ensure_ascii=False,indent=2),encoding='utf-8')
print(f'Created {OUT}; {len(files)} sources, {len(images)} images, {code_count} code examples, {table_count} tables')
