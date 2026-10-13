"""Check reachable Git blobs for selected high-confidence secret patterns.

No secret contents are printed. This is a bounded preparation check, not proof
of absence of every credential or sensitive personal/business datum.
"""
from pathlib import Path
import json, re, subprocess, sys

ROOT = Path(__file__).resolve().parents[1]
patterns = {
    "private_key": re.compile(rb"-----BEGIN (?:RSA |EC |OPENSSH |DSA |ENCRYPTED )?PRIVATE KEY-----"),
    "github_token": re.compile(rb"\b(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{70,})\b"),
    "aws_access_key": re.compile(rb"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b"),
}

def main():
    objects = subprocess.check_output(["git", "rev-list", "--objects", "--all"], cwd=ROOT).splitlines()
    names = {}
    for row in objects:
        oid, _, path = row.partition(b" ")
        names[oid] = path.decode("utf-8", "replace")
    proc = subprocess.Popen(["git", "cat-file", "--batch"], cwd=ROOT,
                            stdin=subprocess.PIPE, stdout=subprocess.PIPE)
    findings = []; scanned = 0
    try:
        for oid, path in names.items():
            proc.stdin.write(oid + b"\n"); proc.stdin.flush()
            header = proc.stdout.readline().split()
            if len(header) != 3: raise RuntimeError("Unexpected Git object header")
            size = int(header[2]); payload = proc.stdout.read(size)
            if proc.stdout.read(1) != b"\n": raise RuntimeError("Invalid Git object boundary")
            if header[1] != b"blob": continue
            scanned += 1
            if Path(path).suffix.lower() in (".pfx", ".p12", ".key", ".pem"):
                findings.append({"path": path, "object": oid.decode(), "type": "private_material_extension"})
            for kind, pattern in patterns.items():
                if pattern.search(payload):
                    findings.append({"path": path, "object": oid.decode(), "type": kind})
    finally:
        proc.stdin.close(); proc.stdout.close(); proc.wait()
    report = {"reachable_blobs_scanned": scanned, "findings": findings,
              "limits": "Selected token/key patterns and private-material filenames only; not a full personal-data or all-secret audit."}
    out = ROOT / "artifacts/licensing/publication-scan.json"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))
    return 1 if findings else 0

if __name__ == "__main__": sys.exit(main())
