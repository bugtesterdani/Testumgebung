"""Summarize Doxygen's parsed declarations without claiming semantic completeness."""
import csv
import html
import json
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict
from pathlib import Path


def text(element):
    return "" if element is None else "".join(element.itertext()).strip()


def summarize(output):
    xml = output / "xml"
    if not (xml / "index.xml").is_file():
        raise RuntimeError("Doxygen XML index missing")
    rows, seen = [], set()
    # Only current index entries; old XML files in a reused output folder do not count.
    compounds = ET.parse(xml / "index.xml").getroot().findall("compound")
    for entry in compounds:
        path = xml / (entry.attrib["refid"] + ".xml")
        root = ET.parse(path).getroot()
        for compound in root.findall("compounddef"):
            owner = compound.findtext("compoundname", "")
            location = compound.find("location")
            source = "" if location is None else location.get("file", "")
            if compound.get("kind") in {"class", "struct", "interface"} and source.replace("\\", "/").startswith("src/"):
                description = text(compound.find("briefdescription")) or text(compound.find("detaileddescription"))
                rows.append({"Module": source.replace("\\", "/").split("/")[1],
                             "Owner": owner, "Member": owner, "Kind": compound.get("kind"),
                             "Access": compound.get("prot", ""), "Source": source,
                             "Line": location.get("line", ""), "HasDescription": bool(description),
                             "MissingParameterDescriptions": "", "Description": description})
            for member in compound.findall(".//memberdef"):
                identifier = member.get("id")
                if identifier in seen:
                    continue
                seen.add(identifier)
                location = member.find("location")
                source = "" if location is None else location.get("file", "")
                if not source.replace("\\", "/").startswith("src/"):
                    continue
                brief = text(member.find("briefdescription"))
                detail = text(member.find("detaileddescription"))
                parameters = [text(p.find("declname")) or text(p.find("defname"))
                              for p in member.findall("param")]
                documented = {text(name) for item in member.findall(
                    ".//parameterlist[@kind='param']/parameteritem")
                    if text(item.find("parameterdescription"))
                    for name in item.findall("parameternamelist/parametername")}
                rows.append({
                    "Module": source.replace("\\", "/").split("/")[1],
                    "Owner": owner, "Member": member.findtext("name", ""),
                    "Kind": member.get("kind", ""), "Access": member.get("prot", ""),
                    "Source": source, "Line": location.get("line", ""),
                    "HasDescription": bool(brief or detail),
                    "MissingParameterDescriptions": "; ".join(p for p in parameters if p and p not in documented),
                    "Description": brief or detail,
                })
    if not rows:
        raise RuntimeError("No own declarations parsed; check the Doxygen input")
    fields = list(rows[0])
    with (output / "Dokumentationsstatus.csv").open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fields)
        writer.writeheader()
        writer.writerows(rows)
    modules = defaultdict(lambda: {"ParsedMembers": 0, "DescribedMembers": 0, "MembersWithMissingParameterDescriptions": 0})
    for row in rows:
        stats = modules[row["Module"]]
        stats["ParsedMembers"] += 1
        stats["DescribedMembers"] += row["HasDescription"]
        stats["MembersWithMissingParameterDescriptions"] += bool(row["MissingParameterDescriptions"])
    summary = {"scope": "Doxygen-parsed own declarations; not all compiler-generated functions",
               "semanticQualityVerified": False, "modules": dict(sorted(modules.items()))}
    (output / "Dokumentationsstatus.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    escape = lambda value: html.escape(str(value))
    table = "".join("<tr>" + "".join("<td>" + escape(row[key]) + "</td>" for key in
                    ["Module", "Owner", "Member", "Kind", "Access", "HasDescription",
                     "MissingParameterDescriptions", "Source", "Line"]) + "</tr>" for row in rows)
    page = '''<!doctype html><html lang="de"><meta charset="utf-8"><title>Dokumentationsstatus</title>
<style>body{font:15px system-ui;margin:24px}table{border-collapse:collapse;width:100%}th,td{border:1px solid #ccd4df;padding:7px;text-align:left;overflow-wrap:anywhere}th{background:#eaf0f8}input{padding:8px}td:nth-child(2){max-width:400px}</style>
<h1>Dokumentationsstatus</h1><p><a href="html/index.html">API-Referenz öffnen</a></p>
<p>Erfasst sind von Doxygen erkannte Deklarationen des eigenen Quellcodes. „Beschreibung vorhanden“ prüft nur Text, nicht seine Richtigkeit oder Vollständigkeit. Geerbte Beschreibungen werden nicht automatisch übernommen. Eigenschaften, lokale Funktionen und moderne C#-Syntax können anders als im Compiler erfasst werden. Warnungen stehen in warnings.log.</p>
<label>Suche <input id="query"></label><label><input type="checkbox" id="gaps"> Nur fehlende Beschreibungen oder Parametertexte</label><p id="count"></p>
<table><thead><tr>'''
    page += "".join("<th>" + label + "</th>" for label in ["Modul", "Typ", "Mitglied", "Art", "Sichtbarkeit", "Beschreibung vorhanden", "Parameter ohne Text", "Quelldatei", "Zeile"])
    page += "</tr></thead><tbody>" + table + "</tbody></table>"
    page += '''<script>const rows=[...document.querySelectorAll('tbody tr')];function filter(){const q=document.querySelector('#query').value.toLowerCase(),g=document.querySelector('#gaps').checked;let n=0;for(const r of rows){const show=r.textContent.toLowerCase().includes(q)&&(!g||r.cells[5].textContent==='False'||r.cells[6].textContent!=='');r.hidden=!show;if(show)n++;}document.querySelector('#count').textContent=n+' Deklarationen angezeigt';}document.querySelector('#query').addEventListener('input',filter);document.querySelector('#gaps').addEventListener('change',filter);filter();</script></html>'''
    (output / "Dokumentationsstatus.html").write_text(page, encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False))


if __name__ == "__main__":
    summarize(Path(sys.argv[1]))
