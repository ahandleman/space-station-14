#!/usr/bin/env python3
"""Audit v3 source references without loading either earlier station tool.

This proves that cited files/UIDs and declared entity prototype IDs exist in this
checkout. It deliberately does not prove a layout, inherited machine behavior,
interface capabilities, or runtime operation. Native YAML tags are inert data.

Run from the repository root:
    python -B Tools/StationArchitect3/research/audit_sources.py --output Tools/StationArchitect3/research/source-audit.json
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
from pathlib import Path, PureWindowsPath
from typing import Any

import yaml


class Variants(dict):
    """Inert marker for SS14's fixed ID-generation transform."""


class NativeLoader(getattr(yaml, "CSafeLoader", yaml.SafeLoader)):
    pass


def inert_tag(loader, suffix, node):
    if isinstance(node, yaml.MappingNode):
        return loader.construct_mapping(node, deep=True)
    if isinstance(node, yaml.SequenceNode):
        return loader.construct_sequence(node, deep=True)
    return loader.construct_scalar(node)


def variants_tag(loader, node):
    if not isinstance(node, yaml.MappingNode):
        raise ValueError("CreateVariants must be a mapping")
    return Variants(loader.construct_mapping(node, deep=True))


NativeLoader.add_multi_constructor("!", inert_tag)
NativeLoader.add_constructor("!type:CreateVariants", variants_tag)


def load_native(path: Path) -> Any:
    return yaml.load(path.read_text(encoding="utf-8-sig"), Loader=NativeLoader)


def file_hash(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def entity_ids(value: Any) -> list[str]:
    """Expand only ID values; other variant fields cannot affect ID existence."""
    if isinstance(value, str) and value:
        return [value]
    if isinstance(value, Variants):
        values = value.get("values")
        if isinstance(values, list) and values and all(isinstance(x, str) and x for x in values):
            return values
    raise ValueError("Unsupported entity ID declaration; expected string or CreateVariants.values strings")


def prototype_index(repo: Path) -> tuple[dict[str, list[str]], dict[str, list[str]], int, int]:
    index: dict[str, list[str]] = {}
    access: dict[str, list[str]] = {}
    file_count = variant_ids = 0
    directory = repo / "Resources" / "Prototypes"
    paths = sorted(set(directory.rglob("*.yml")) | set(directory.rglob("*.yaml")))
    for path in paths:
        data = load_native(path)
        file_count += 1
        if not isinstance(data, list):
            continue
        for item in data:
            if not isinstance(item, dict):
                continue
            if item.get("type") == "accessLevel" and isinstance(item.get("id"), str):
                access.setdefault(item["id"], []).append(path.relative_to(repo).as_posix())
            if item.get("type") != "entity":
                continue
            ids = entity_ids(item.get("id"))
            if isinstance(item.get("id"), Variants):
                variant_ids += len(ids)
            for prototype in ids:
                index.setdefault(prototype, []).append(path.relative_to(repo).as_posix())
    return index, access, file_count, variant_ids


def declared_prototypes(document: dict) -> list[dict]:
    result = []
    for i, interface in enumerate(document.get("interfaces", [])):
        for j, prototype in enumerate(interface.get("construction", [])):
            result.append({"path": f"$.interfaces[{i}].construction[{j}]", "prototype": prototype,
                           "role": "interface", "owner": interface.get("id")})
    for i, assembly in enumerate(document.get("assemblies", [])):
        for j, part in enumerate(assembly.get("parts", [])):
            for k, prototype in enumerate(part.get("prototypes", [])):
                result.append({"path": f"$.assemblies[{i}].parts[{j}].prototypes[{k}]", "prototype": prototype,
                               "role": "assembly_part", "owner": f"{assembly.get('id')}/{part.get('id')}"})

    def visit(spaces, parent="", location="$.spaces"):
        for i, space in enumerate(spaces):
            name = "/".join(x for x in [parent, space.get("id", "")] if x)
            here = f"{location}[{i}]"
            for j, feature in enumerate(space.get("features", [])):
                for k, prototype in enumerate(feature.get("prototypes", [])):
                    result.append({"path": f"{here}.features[{j}].prototypes[{k}]", "prototype": prototype,
                                   "role": "feature", "owner": f"{name}#{feature.get('id')}"})
            visit(space.get("spaces", []), name, f"{here}.spaces")
    visit(document.get("spaces", []))
    return result


def declared_access(document: dict) -> list[dict]:
    result = []
    for i, profile in enumerate(document.get("accessProfiles", [])):
        for j, level in enumerate(profile.get("levels", [])):
            result.append({"path": f"$.accessProfiles[{i}].levels[{j}]", "level": level,
                           "role": "actor_credentials", "owner": profile.get("id")})
    for i, interface in enumerate(document.get("interfaces", [])):
        access = interface.get("access", {})
        clauses = access.get("anyOf", []) if isinstance(access, dict) else [[x] for x in access]
        for j, clause in enumerate(clauses):
            for k, level in enumerate(clause):
                result.append({"path": f"$.interfaces[{i}].access.anyOf[{j}][{k}]", "level": level,
                               "role": "interface_clause", "owner": interface.get("id")})
    return result


def local_source(repo: Path, source: str) -> tuple[Path, str]:
    path_text, _, fragment = source.partition("#")
    if not path_text or Path(path_text).is_absolute() or PureWindowsPath(path_text).is_absolute():
        raise ValueError("Source must be a repository-relative file path")
    path = (repo / path_text).resolve()
    if not path.is_relative_to(repo.resolve()):
        raise ValueError("Source escapes the repository root")
    if not path.is_file():
        raise ValueError("Source file does not exist")
    return path, fragment


def audit(document: dict, repo: Path, program_path: Path | None = None,
          recorded_evidence: list[Path] | None = None) -> dict:
    repo = repo.resolve()
    issues: list[dict] = []
    index, access_index, prototype_files, variant_ids = prototype_index(repo)
    hashes: dict[str, str] = {}
    map_uids: dict[str, set[int]] = {}

    def issue(path, code, message):
        issues.append({"severity": "error", "path": path, "code": code, "message": message})

    def digest(path):
        name = path.relative_to(repo).as_posix()
        if name not in hashes:
            hashes[name] = file_hash(path)
        return hashes[name]

    evidence_results = []
    for i, evidence in enumerate(document.get("evidence", [])):
        kind = evidence.get("kind")
        if kind not in {"map", "native"}:
            continue
        location = f"$.evidence[{i}].source"
        record = {"id": evidence.get("id"), "kind": kind, "source": evidence.get("source"), "exists": False}
        evidence_results.append(record)
        try:
            path, fragment = local_source(repo, str(evidence.get("source", "")))
            record.update({"exists": True, "sha256": digest(path)})
            match = re.fullmatch(r"uid=(\d+)", fragment)
            if kind == "map" and not match:
                raise ValueError("Map evidence requires an explicit #uid=<integer> reference")
            if match:
                key = path.relative_to(repo).as_posix()
                if key not in map_uids:
                    native = load_native(path)
                    map_uids[key] = {int(entity["uid"]) for group in native.get("entities", [])
                                     for entity in group.get("entities", []) if "uid" in entity}
                uid = int(match.group(1))
                record["uid"] = uid
                record["uidExists"] = uid in map_uids[key]
                if not record["uidExists"]:
                    raise ValueError(f"Map does not contain UID {uid}")
        except (OSError, ValueError, TypeError, yaml.YAMLError) as exc:
            issue(location, "source-reference", str(exc))

    prototype_results = []
    for ref in declared_prototypes(document):
        record = ref | {"exists": ref["prototype"] in index}
        if not record["exists"]:
            issue(ref["path"], "prototype-reference", f"Unknown native entity prototype {ref['prototype']!r}")
        else:
            record["nativeSources"] = [{"path": source, "sha256": digest(repo / source)}
                                        for source in sorted(set(index[ref["prototype"]]))]
        prototype_results.append(record)

    access_results = []
    for ref in declared_access(document):
        marker = ref["level"] == "public"
        record = ref | {"exists": marker or ref["level"] in access_index, "planningMarker": marker}
        if not record["exists"]:
            issue(ref["path"], "access-reference", f"Unknown native accessLevel {ref['level']!r}")
        elif not marker:
            record["nativeSources"] = [{"path": source, "sha256": digest(repo / source)}
                                        for source in sorted(set(access_index[ref["level"]]))]
        access_results.append(record)

    recorded_results = []
    for evidence_path in recorded_evidence or []:
        data = json.loads(evidence_path.read_text(encoding="utf-8-sig"))
        for item in data.get("maps", []):
            source = item.get("sourcePath", item.get("source"))
            expected = item.get("sha256")
            if not source or not expected:
                continue
            location = f"{evidence_path.relative_to(repo).as_posix()}:{source}"
            try:
                path, _ = local_source(repo, source)
                actual = digest(path)
                record = {"recordedIn": evidence_path.relative_to(repo).as_posix(), "source": source,
                          "expectedSha256": expected, "actualSha256": actual, "matches": expected == actual}
                recorded_results.append(record)
                if expected != actual:
                    issue(location, "stale-evidence", "Recorded map hash differs from this checkout")
            except (OSError, ValueError) as exc:
                issue(location, "recorded-source", str(exc))
        for source, expected in data.get("sourceHashes", {}).items():
            location = f"{evidence_path.relative_to(repo).as_posix()}:{source}"
            try:
                path, _ = local_source(repo, source)
                actual = digest(path)
                recorded_results.append({"recordedIn": evidence_path.relative_to(repo).as_posix(), "source": source,
                                         "expectedSha256": expected, "actualSha256": actual, "matches": expected == actual})
                if expected != actual:
                    issue(location, "stale-evidence", "Recorded native source hash differs from this checkout")
            except (OSError, ValueError) as exc:
                issue(location, "recorded-source", str(exc))

    return {"format": "station_architect3_source_audit", "version": 1,
            "success": not issues, "program": str(program_path.relative_to(repo).as_posix()) if program_path else None,
            "programSha256": file_hash(program_path) if program_path else None,
            "scope": "Source existence and identity only; no geometric satisfiability, inherited native capability or runtime operation proof.",
            "method": "Standalone inert native YAML parser, complete entity ID inventory including CreateVariants.values; raw native map UID lookup; SHA-256 of cited input files and recorded research source hashes. No earlier-tool imports.",
            "counts": {"prototypeFiles": prototype_files, "nativeEntityPrototypeIds": len(index), "generatedVariantIds": variant_ids,
                       "nativeAccessLevelIds": len(access_index), "accessReferences": len(access_results),
                       "evidenceReferences": len(evidence_results), "distinctMaps": len(map_uids),
                       "prototypeReferences": len(prototype_results), "distinctDeclaredPrototypes": len({r['prototype'] for r in prototype_results}),
                       "recordedHashesChecked": len(recorded_results), "errors": len(issues)},
            "diagnostics": issues, "evidence": evidence_results, "prototypeReferences": prototype_results,
            "accessReferences": access_results,
            "recordedSourceHashes": recorded_results, "sourceHashes": dict(sorted(hashes.items()))}


def main():
    repo_default = Path(__file__).resolve().parents[3]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=repo_default)
    parser.add_argument("--program", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    repo = args.repo.resolve()
    program = (args.program or repo / "Tools/StationArchitect3/catalog/standard-station.yaml").resolve()
    document = yaml.safe_load(program.read_text(encoding="utf-8-sig"))
    research = Path(__file__).resolve().parent
    recorded = [p for p in [research / "engineering-evidence.json",
                           *sorted(research.glob("*.evidence.json"))] if p.exists()]
    result = audit(document, repo, program, recorded)
    if args.output:
        output = args.output.resolve()
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"success": result["success"], "counts": result["counts"], "diagnostics": result["diagnostics"]}, indent=2))
    return 0 if result["success"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
