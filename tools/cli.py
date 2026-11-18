"""CLI for the offline harness.

Usage:
  python -m tools.cli inventory <pkg.unitypackage> [--out DIR]
  python -m tools.cli dryrun <pkg.unitypackage> --out DIR
"""
from __future__ import annotations

import argparse
import sys

from .dryrun import dry_run, report_markdown
from .unitypackage import UnityPackage


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="tools.cli", description="VrcToVrm offline harness")
    sub = ap.add_subparsers(dest="cmd", required=True)
    inv = sub.add_parser("inventory", help="list assets in a .unitypackage")
    inv.add_argument("package")
    dr = sub.add_parser("dryrun", help="simulate conversion and emit a manifest")
    dr.add_argument("package")
    dr.add_argument("--out", required=True, help="output directory for manifest json/md")
    args = ap.parse_args(argv)
    if args.cmd == "inventory":
        pkg = UnityPackage(args.package)
        for p in pkg.paths():
            print(p)
        print(f"# {len(pkg.paths())} assets", file=sys.stderr)
        return 0
    if args.cmd == "dryrun":
        from .dryrun import write_report
        path = write_report(args.package, args.out)
        print(f"Wrote {path}")
        report = dry_run(args.package)
        print(report_markdown(report))
        return 0
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
