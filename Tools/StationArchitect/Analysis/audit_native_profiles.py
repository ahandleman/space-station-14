"""Independently compare a profile library's ports with native source facts.

This command never writes profiles and does not start the game. A mismatch is an
error, rather than a declaration silently becoming its own validation evidence.
"""
import argparse
import json
from pathlib import Path

from native_system_sources import audit_profiles, prototypes


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[3])
    parser.add_argument("--profiles", type=Path, default=Path("Tools/StationArchitect/Catalog/native-profiles.json"))
    args = parser.parse_args()
    root = args.repo.resolve()
    path = args.profiles if args.profiles.is_absolute() else root / args.profiles
    library = json.loads(path.read_text(encoding="utf-8"))
    errors = audit_profiles(prototypes(root), library["profiles"])
    print(json.dumps({"profiles": len(library["profiles"]), "errors": errors, "simulationVerified": False}, indent=2))
    return 1 if errors else 0


if __name__ == "__main__":
    raise SystemExit(main())
