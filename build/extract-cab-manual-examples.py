"""Local-only PDF input. Never writes manufacturer examples into the repository."""
import argparse
import json
import re
from importlib.metadata import version
from pathlib import Path
from pdfminer.high_level import extract_pages
from pdfminer.layout import LTChar, LTTextLine, LTRect


def walk(node, kind):
    if isinstance(node, kind):
        yield node
    elif hasattr(node, '__iter__'):
        for child in node:
            yield from walk(child, kind)


def courier_lines(chars):
    rows=[]
    italic_runs=[]
    for c in sorted((c for c in chars if 'Italic' in c.fontname),key=lambda c:(-c.y0,c.x0)):
        if italic_runs and abs(italic_runs[-1][-1].y0-c.y0)<2 and abs(c.x0-italic_runs[-1][-1].x1)<2:
            italic_runs[-1].append(c)
        else:italic_runs.append([c])
    markers={id(c) for run in italic_runs if ''.join(c.get_text() for c in run).strip() in ('CR','LF') for c in run}
    for char in sorted((c for c in chars if id(c) not in markers), key=lambda c: (-c.y0, c.x0)):
        row=next((r for r in rows if abs(r[0]-char.y0)<2),None)
        if row is None:
            row=[char.y0,[]]; rows.append(row)
        row[1].append(char)
    for char in (c for c in chars if id(c) in markers):
        row=min(rows,key=lambda r:abs(r[0]-char.y0),default=None)
        if row is not None and abs(row[0]-char.y0)<7:row[1].append(char)
    return sorted(rows, key=lambda r:-r[0])


def line_text(chars):
    runs=[]
    for char in sorted(chars,key=lambda c:c.x0):
        if runs and runs[-1][-1].fontname==char.fontname and abs(char.x0-runs[-1][-1].x1)<2:
            runs[-1].append(char)
        else:runs.append([char])
    text='';previous=None
    for run in runs:
        first=run[0];last=run[-1]
        if previous is not None:
            gap=first.x0-previous.x1
            if gap>first.size*0.3:text+=' '*max(1,round(gap/(first.size*0.6)))
        value=''.join(c.get_text() for c in run)
        if 'Courier' in first.fontname and 'Italic' in first.fontname and value in ('CR','LF'):
            value={'CR':'\r','LF':'\n'}[value]
        elif 'Courier' in first.fontname and 'Bold' in first.fontname and 'ESC' in value:
            value=value.replace('ESC','\x1b')
        text+=value;previous=last
    return text if re.match(r'^[TB](?:[ \t:0-9])',text) else text.rstrip(' \t')


def extract(path):
    records=[]
    for number,page in enumerate(extract_pages(path),1):
        anchors=[line for line in walk(page,LTTextLine) if line.get_text().lstrip().startswith('Example:')]
        anchors.sort(key=lambda line:-line.y1)
        chars=list(walk(page,LTChar))
        rectangles=list(walk(page,LTRect))
        for ordinal,anchor in enumerate(anchors,1):
            bottom=anchors[ordinal].y1 if ordinal<len(anchors) else 0
            boxes=[r for r in rectangles if r.fill and r.width>150 and anchor.x1+5<r.x0<anchor.x1+50 and r.y0<anchor.y0<r.y1+2]
            box=min(boxes,key=lambda r:r.width*r.height,default=None)
            if box:
                code=[c for c in chars if 'Courier' in c.fontname and box.x0<=c.x0<box.x1 and c.y1>box.y0 and c.y0<box.y1]
            else:
                code=[c for c in chars if 'Courier' in c.fontname and bottom<c.y0<=anchor.y1+2 and c.x0>=anchor.x0-2]
            rows=courier_lines(code);chosen=[];previous=None
            for y,row in rows:
                if previous is None and anchor.y1-y>85:break
                if box is None and previous is not None and previous-y>22:break
                chosen.append(line_text(row));previous=y
            kind='code'
            if not chosen:
                match=re.search(r'Example:\s*((?:[0-9A-Fa-f]{2}\s+)+[0-9A-Fa-f]{2})',anchor.get_text())
                if match:chosen=[match[1].strip()];kind='data-fragment'
            source,wraps=join_wrapped_code(chosen)
            source=source.replace('[CR]','\r').replace('[LF]','\n')
            source=re.sub(r'(^|[\r\n])([ \t]*)ESC',lambda m:m[1]+m[2]+'\x1b',source)
            records.append({'page':number,'ordinal':ordinal,'source':source,
                            'extracted':bool(chosen),'kind':kind,'typographic_wraps':wraps,'layout':'box' if box else 'inline'})
    return records


def join_wrapped_code(rows):
    statements=[];wraps=0;current=''
    command=re.compile(r'^(?:[a-zA-Z](?:[ \t:;]|[0-9])|A\[|M[cdflrsu](?:[ \t;]|$)|[a-zA-Z]$|;|<)')
    for row in rows:
        stripped=row.lstrip()
        continuation=statements and not command.match(stripped)
        open_option=statements and statements[-1].count('[')>statements[-1].count(']')
        open_tag=statements and statements[-1].rfind('<')>statements[-1].rfind('>')
        if continuation and (open_option or current in ('T','B','R') or current=='W' and open_tag):
            separator='' if open_option or statements[-1].endswith('-') else ' '
            statements[-1]+=separator+stripped;wraps+=1
        else:
            statements.append(row)
            if command.match(stripped):current=stripped[:1]
    return '\r'.join(statements),wraps


if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('pdf');parser.add_argument('output')
    args=parser.parse_args()
    for pin in Path(__file__).with_name('requirements-cab-manual.txt').read_text(encoding='utf8').splitlines():
        if not pin or pin.startswith('#'):continue
        name,expected=pin.split('==')
        if version(name)!=expected:raise RuntimeError('Erwartet: '+pin)
    output=Path(args.output);output.parent.mkdir(parents=True,exist_ok=True)
    records=extract(args.pdf)
    output.write_text(json.dumps({'examples':records},ensure_ascii=False,indent=2),encoding='utf8')
    print(json.dumps({'anchors':len(records),'extracted':sum(r['extracted'] for r in records)}))
