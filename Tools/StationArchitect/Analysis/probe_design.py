"""Run the actual local designer stages and retain inspectable intermediate projects.

Start StationArchitect on the requested port first. This is a developer probe,
not an alternate generator. Every mask and diagnostic comes from the live API.
"""
import argparse
from collections import Counter
import json
from pathlib import Path
import time
from urllib.request import Request, urlopen


def request(base, path, body=None):
    data = None if body is None else json.dumps(body).encode()
    req = Request(base + "/api/" + path, data=data,
                  headers={"Content-Type": "application/json"})
    with urlopen(req, timeout=600) as response:
        content = response.read()
        return json.loads(content) if "json" in response.headers.get("Content-Type", "") else content


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    parser.add_argument("--base", default="http://127.0.0.1:8423")
    parser.add_argument("--project", type=Path, help="Import a saved project instead of the default program.")
    parser.add_argument("--topology", choices=["ring", "hub", "spine"])
    parser.add_argument("--seed", type=int)
    parser.add_argument("--compactness", type=float)
    parser.add_argument("--bundling", type=float)
    parser.add_argument("--interlock", type=float)
    parser.add_argument("--internal-variation", type=float)
    parser.add_argument("--silhouette-variation", type=float)
    stages = ["selection", "placement", "growth", "circulation", "gameflow", "contents", "systemsDevices", "power", "atmos", "disposals", "validation"]
    parser.add_argument("--start-at", default="selection", choices=stages)
    parser.add_argument("--stop-after", default="validation", choices=stages)
    args = parser.parse_args()
    if stages.index(args.start_at) > stages.index(args.stop_after):
        parser.error("The first stage must not follow the last stage.")
    args.output.mkdir(parents=True, exist_ok=True)
    if args.project:
        project = json.loads(args.project.read_text(encoding="utf-8-sig"))
        project = request(args.base, "design/import", project.get("project", project))
    else:
        project = request(args.base, "design/default")
    if args.topology is not None:
        project["settings"]["topology"] = args.topology
    if args.seed is not None:
        project["settings"]["seed"] = args.seed
    if args.compactness is not None:
        project["settings"]["packing"]["compactness"] = args.compactness
    if args.bundling is not None:
        project["settings"]["packing"]["departmentBundling"] = args.bundling
    for attribute, key in [("interlock", "interlock"), ("internal_variation", "internalVariation"),
                           ("silhouette_variation", "silhouetteVariation")]:
        value = getattr(args, attribute)
        if value is not None:
            if not 0 <= value <= 1:
                parser.error("Packing controls must be between 0 and 1.")
            project["settings"]["packing"][key] = value
    summary = []
    for stage in stages[stages.index(args.start_at):]:
        started = time.monotonic()
        project = request(args.base, "design/stage/" + stage, project)
        (args.output / (stage + ".station.json")).write_text(json.dumps(project), encoding="utf-8")
        errors = [item for item in (project.get("report") or {}).get("diagnostics", []) if item["severity"] == "error"]
        record = {"stage": stage, "seconds": round(time.monotonic() - started, 3),
                  "rooms": len(project["layout"]["rooms"]), "errors": dict(Counter(item["code"] for item in errors)),
                  "success": (project.get("report") or {}).get("success", False),
                  "complete": (project.get("report") or {}).get("complete", False),
                  "metrics": (project.get("report") or {}).get("metrics", {})}
        summary.append(record)
        print(json.dumps(record), flush=True)
        (args.output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
        if stage == args.stop_after or stage == "selection" and errors:
            break
    (args.output / "plan.svg").write_bytes(request(args.base, "design/export/svg", project))
    return 0 if not errors else 1


if __name__ == "__main__":
    raise SystemExit(main())
