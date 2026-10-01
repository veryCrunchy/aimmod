"""aimmod-workshop: prepare Steam Workshop publish bundles for AimMod map ports.

Nothing is uploaded. Review the bundle, then publish it yourself (see PUBLISH.txt in each bundle).
"""
from __future__ import annotations

import argparse
import sys
from typing import List, Optional

from .bundle import BundleError, Source, VISIBILITY, prepare


def main(argv: Optional[List[str]] = None) -> int:
    ap = argparse.ArgumentParser(prog="aimmod-workshop", description=__doc__)
    sub = ap.add_subparsers(dest="command", required=True)
    p = sub.add_parser("prepare", help="build publish bundles from a map-port output folder")
    p.add_argument("port_dir", help="map-port --out folder (holds *.report.json, Scenarios/, maps/)")
    p.add_argument("--out", required=True, help="folder for the bundles (one subfolder per scenario)")
    p.add_argument("--scenario", help="only this scenario name")
    p.add_argument("--source-author", default="", help="original map author(s)")
    p.add_argument("--source-url", default="", help="where the original map was published")
    p.add_argument("--source-license", default="", help="license or permission for the port")
    p.add_argument("--credit", action="append", default=[], help="extra credit line (repeatable)")
    p.add_argument("--preview", help="preview image to use instead of the converter's (8-bit PNG)")
    p.add_argument("--published-file-id", help="existing Workshop item id, for an update")
    p.add_argument("--change-note", default="", help="Workshop change note")
    p.add_argument("--visibility", choices=sorted(VISIBILITY), default="private")
    p.add_argument("--include-map-files", action="store_true",
                   help="also put the map .json and ability into content/ (the .sce already embeds them)")
    args = ap.parse_args(argv)
    try:
        bundles = prepare(args.port_dir, args.out, args.scenario,
                          Source(args.source_author, args.source_url, args.source_license, args.credit),
                          args.preview, args.published_file_id, args.change_note, args.visibility, args.include_map_files)
    except (BundleError, OSError, ValueError) as error:
        print(f"aimmod-workshop: {error}", file=sys.stderr)
        return 1
    for bundle in bundles:
        print(f"Bundle ready: {bundle} (read PUBLISH.txt; nothing was uploaded)")
    return 0
