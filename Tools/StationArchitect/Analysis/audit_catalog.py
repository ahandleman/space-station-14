"""Check the authored catalog's source references against actual repository data.

Run after analyze_maps.py. This complements the C# semantic validator: it checks
entity IDs and map UIDs without making the independent core depend on game data.
It does not infer a required room merely from a beacon or a machine count.
"""
from __future__ import annotations

import argparse
import collections
import json
import pathlib
import re

from analyze_maps import load, prototype_index, sha256


def audit(repo: pathlib.Path, catalog_path: pathlib.Path, evidence_path: pathlib.Path):
    document = load(catalog_path)
    evidence = json.loads(evidence_path.read_text(encoding="utf-8"))
    prototypes = prototype_index(repo)
    map_entities = {}
    errors, warnings, references = [], [], []
    maps = {item["sourcePath"]: item for item in evidence["maps"]}
    global_counts = collections.Counter()
    for item in maps.values():
        global_counts.update(item["mainGridPrototypeCounts"])
        if sha256(repo / item["sourcePath"]) != item["sha256"]:
            errors.append(f"Evidence is stale for {item['sourcePath']}; rerun analyze_maps.py.")

    def source(owner, reference):
        parts = reference.split("#", 1)
        path = (repo / parts[0]).resolve()
        if not path.is_relative_to(repo) or not path.is_file():
            errors.append(f"{owner}: source file does not exist inside this repository: {reference}")
            return
        detail = {"owner": owner, "reference": reference}
        if len(parts) > 1:
            if re.fullmatch(r"uid=\d+", parts[1]):
                key = parts[0]
                if key not in map_entities:
                    data = load(path)
                    map_entities[key] = {int(entity["uid"]): group.get("proto", "")
                                         for group in data.get("entities", [])
                                         for entity in group.get("entities", [])}
                uid = int(parts[1][4:])
                if uid not in map_entities[key]:
                    errors.append(f"{owner}: mapped UID is missing: {reference}")
                else:
                    detail["prototype"] = map_entities[key][uid]
            elif parts[1].startswith("id="):
                entity_id = parts[1][3:]
                if entity_id not in prototypes or prototypes[entity_id].get("sourcePath") != parts[0]:
                    errors.append(f"{owner}: prototype ID is not declared in its source file: {reference}")
                else:
                    detail["prototype"] = entity_id
            else:
                errors.append(f"{owner}: use #uid=N or #id=Prototype for a concrete source reference: {reference}")
        references.append(detail)

    for ref in document.get("sourceRefs", []):
        source("$station", ref)
    contents, rooms = [], []
    content_requirements = 0
    def walk_rooms(children, parent):
        for room in children:
            path = parent + "/" + room["id"]
            yield path, room
            yield from walk_rooms(room.get("rooms", []), path)
    for owner in [*document["departments"], {"id": "_maintenance", **document["maintenance"]}]:
        owner_id = owner["id"]
        for ref in owner.get("sourceRefs", []):
            source(owner_id, ref)
        for path, room in walk_rooms(owner["rooms"], owner_id):
            rooms.append(path)
            content_requirements += len(room.get("contents", []))
            if not room.get("sourceRefs"):
                errors.append(f"{path}: no source references; mark the supporting map or authored design source explicitly.")
            for ref in room.get("sourceRefs", []):
                source(path, ref)
            for dock in room.get("exterior", {}).get("docks", []):
                for ref in dock.get("sourceRefs", []):
                    source(path + "/dock:" + dock["id"], ref)
            for requirement in room.get("contents", []):
                for entity_id in requirement["prototypes"]:
                    if entity_id not in prototypes:
                        errors.append(f"{path}/{requirement['id']}: unknown entity prototype {entity_id}")
                    elif global_counts[entity_id] == 0:
                        warnings.append(f"{path}/{requirement['id']}: {entity_id} is defined but not explicitly mapped on the surveyed main grids.")
                    contents.append({"room": path, "requirement": requirement["id"], "prototype": entity_id,
                                     "prototypeSource": prototypes.get(entity_id, {}).get("sourcePath"),
                                     "surveyedMainGridInstances": global_counts[entity_id]})
    return {"success": not errors, "catalog": catalog_path.relative_to(repo).as_posix(),
            "catalogSha256": sha256(catalog_path), "evidenceSha256": sha256(evidence_path),
            "stationMapsSurveyed": len(maps), "defaultPoolMapsSurveyed": evidence["defaultPoolMapCount"],
            "departments": len(document["departments"]), "roomTemplates": len(rooms),
            "contentRequirements": content_requirements,
            "distinctContentPrototypes": len({entry["prototype"] for entry in contents}),
            "sourceReferenceCount": len(references), "errors": errors, "warnings": sorted(set(warnings)),
            "references": references, "contents": contents}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=pathlib.Path, default=pathlib.Path(__file__).resolve().parents[3])
    parser.add_argument("--catalog", type=pathlib.Path)
    parser.add_argument("--evidence", type=pathlib.Path, default=pathlib.Path(__file__).with_name("map-evidence.json"))
    parser.add_argument("--output", type=pathlib.Path, default=pathlib.Path(__file__).with_name("catalog-audit.json"))
    args = parser.parse_args()
    repo = args.repo.resolve()
    catalog = args.catalog or repo / "Tools/StationArchitect/Catalog/standard-station.yaml"
    report = audit(repo, catalog.resolve(), args.evidence.resolve())
    args.output.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Catalog audit: {report['roomTemplates']} rooms, {report['contentRequirements']} content requirements, "
          f"{report['sourceReferenceCount']} source references; {len(report['errors'])} errors, {len(report['warnings'])} warnings.")
    for error in report["errors"]:
        print(error)
    for warning in report["warnings"]:
        print("Warning: " + warning)
    return 0 if report["success"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
