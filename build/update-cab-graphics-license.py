"""Add versioned ZXing evidence without claiming a new audit of the historical MSIX."""
from pathlib import Path
import csv
import hashlib
import importlib.util
import json
import urllib.request
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
KEY = "ZXing.Net/0.16.11"
VERSION = "0.16.11"
URL = "https://raw.githubusercontent.com/micjahn/ZXing.Net/v0.16.11.0/COPYING"
REPORT = ROOT / "docs/licensing"
spec = importlib.util.spec_from_file_location("audit", ROOT / "build/audit-dependencies.py")
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


def csv_row(name, row, key):
    path = REPORT / name
    encoding = "utf-8-sig" if path.read_bytes().startswith(b"\xef\xbb\xbf") else "utf-8"
    with path.open(encoding="utf-8-sig", newline="") as handle:
        reader = csv.DictReader(handle)
        fields = reader.fieldnames
        rows = [existing for existing in reader if existing[key] != row[key]]
    rows.append({field: row.get(field, "") for field in fields})
    with path.open("w", encoding=encoding, newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=fields)
        writer.writeheader()
        writer.writerows(rows)


def main():
    project = ROOT / "src/Testumgebung.Presentation/Testumgebung.Presentation.csproj"
    reference = ET.parse(project).getroot().find(".//PackageReference[@Include='ZXing.Net']")
    assert reference is not None and reference.get("Version") == VERSION
    assets = json.loads((project.parent / "obj/project.assets.json").read_text(encoding="utf-8-sig"))
    library = assets["libraries"][KEY]
    folder = next(Path(p) / library["path"] for p in assets["packageFolders"] if (Path(p) / library["path"]).exists())
    metadata = audit.metadata(folder)
    assert metadata["license"] == "Apache-2.0"
    upstream = urllib.request.urlopen(URL, timeout=30).read()
    metadata["license_files"].append(audit.save_license("nuget/ZXing.Net/" + VERSION, "COPYING", upstream))
    notice = ("ZXing.Net " + VERSION + "\nAuthors: Michael Jahn; upstream ZXing authors.\n"
              "Apache-2.0. Original source headers include Copyright 2008 ZXing authors.\n"
              "Unmodified NuGet assembly; no upstream source changes.\n"
              "Source tag: https://github.com/micjahn/ZXing.Net/tree/v0.16.11.0\n"
              "Original license: " + URL + "\n").encode()
    metadata["license_files"].append(audit.save_license("nuget/ZXing.Net/" + VERSION, "PACKAGE-NOTICE.txt", notice))
    data = json.loads((REPORT / "inventory.json").read_text(encoding="utf-8"))
    projects, files, targets = [], [], set()
    for asset in sorted(ROOT.glob("**/obj/project.assets.json")):
        if any(part in {"artifacts", "toolchains", ".tools", "tmp"} for part in asset.relative_to(ROOT).parts):
            continue
        document = json.loads(asset.read_text(encoding="utf-8-sig"))
        if KEY not in document.get("libraries", {}):
            continue
        files.append(asset.relative_to(ROOT).as_posix())
        for target, packages in document.get("targets", {}).items():
            if KEY in packages:
                targets.add(target)
        projects.extend(p.relative_to(ROOT).as_posix() for p in asset.parent.parent.glob("*.csproj"))
    row = dict(name="ZXing.Net", version=VERSION, projects=sorted(set(projects)), direct_in=[project.relative_to(ROOT).as_posix()],
               targets=sorted(targets), package_sha512=library.get("sha512", ""), assets_files=files, metadata=metadata,
               license_effective="Apache-2.0", code_references=["src/Testumgebung.Presentation/ViewModels/CabBarcodePreview.cs"],
               bundled_manifests=[], review_context="AP 6 source dependency; historical audited bundle unchanged")
    data["packages"][KEY] = row
    for item in data["projects"]:
        if item["project"] == project.relative_to(ROOT).as_posix():
            item["direct_packages"] = sorted(set(item["direct_packages"] + [KEY]))
    review = dict(component=KEY, date="2026-11-22", license="Apache-2.0", upstream_license=URL,
                  scope="Current source and resolved NuGet dependency; not a re-audit of historical bundled binaries")
    data["additional_reviews"] = [r for r in data.get("additional_reviews", []) if r.get("component") != KEY] + [review]
    data["summary"]["nuget_package_versions"] = len(data["packages"])
    (REPORT / "inventory.json").write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    csv_row("nuget.csv", dict(name=row["name"], version=VERSION, license="Apache-2.0", license_type="expression",
            direct_in="; ".join(row["direct_in"]), resolved_in="; ".join(row["projects"]), code_references="; ".join(row["code_references"]),
            authors="Michael Jahn", copyright="See versioned upstream notice", license_files="; ".join(r["path"] for r in metadata["license_files"])), "name")
    for record in metadata["license_files"]:
        path = ROOT / record["path"]
        assert hashlib.sha256(path.read_bytes()).hexdigest() == record["sha256"]
        csv_row("license-files.csv", dict(path=record["path"], size=path.stat().st_size, sha256=record["sha256"]), "path")
    print(KEY, "Apache-2.0", len(metadata["license_files"]), "versioned license files")


if __name__ == "__main__":
    main()
