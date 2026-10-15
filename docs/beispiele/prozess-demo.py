"""Ungefaehrliches Beispiel fuer den externen Python-Prozessbaustein: keine Hardwarezugriffe."""
import argparse
import json
import sys

parser = argparse.ArgumentParser()
parser.add_argument('--serial', required=True)
parser.add_argument('--exit', type=int, default=0)
args = parser.parse_args()
print(json.dumps({'serial': args.serial, 'message': 'Testprozess beendet'}, ensure_ascii=False))
print('Demonstrationsausgabe auf stderr', file=sys.stderr)
sys.exit(args.exit)
