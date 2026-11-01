"""Reproduce infrastructure observations from native prototypes and station maps.

Requires Python 3 and PyYAML. Run from any directory. The output records source
hashes, inherited component facts, grid-local counts and native UID control lists.
It neither changes game maps nor claims to have simulated those systems.
"""
from __future__ import annotations

import argparse
import collections
import json
from pathlib import Path

from analyze_maps import components, load, merge_components
from native_system_sources import evidence, prototypes

COMPONENTS = (
    "NodeContainer", "PowerSupplier", "Battery", "BatteryCharger", "BatteryDischarger",
    "PowerNetworkBattery", "PowerProvider", "ExtensionCableProvider", "ExtensionCableReceiver",
    "ApcPowerReceiver", "AtmosPipeLayers", "GasVentPump", "GasVentScrubber", "DeviceNetwork",
    "DeviceList", "DisposalTube", "DisposalEntry", "DisposalUnit", "PoweredLight", "AirAlarm",
    "FireAlarm", "Firelock", "AtmosAlarmable", "AtmosMonitor", "GasCanister",
)
PROTOTYPES = (
    "CableHV", "CableMV", "CableApcExtension", "CableTerminal", "SubstationBasic", "APCBasic",
    "SMESBasic", "SMESAdvanced", "GeneratorBasic", "GasPipeStraight", "GasPipeStraightAlt1",
    "GasPipeCap", "GasVentPump", "GasVentScrubber", "AirAlarm", "FireAlarm", "Firelock",
    "AirSensor", "DisposalUnit", "DisposalTrunk", "DisposalPipe", "DisposalBend",
    "DisposalJunction", "DisposalYJunction", "DisposalRouterWaste", "DisposalOutlet", "Poweredlight", "PoweredlightLED",
    "GasPort", "AirCanister", "StorageCanister",
)


def survey(repo, maps):
    index = prototypes(repo)
    report = {"simulationVerified": False, "profiles": {}, "maps": [],
              "unresolvedRequestedPrototypes": sorted(name for name in PROTOTYPES if name not in index)}
    for name in PROTOTYPES:
        if name not in index:
            continue
        report["profiles"][name] = {
            "sourcePath": index[name]["sourcePath"], "ancestors": index[name]["ancestors"],
            "components": {key: value for key, value in index[name]["components"].items() if key in COMPONENTS},
        }
    paths = []
    for name in maps:
        path = "Resources/Maps/" + name + ".yml"
        paths.append(path)
        data = load(repo / path)
        entities = {}
        for group in data["entities"]:
            prototype = group.get("proto", "")
            base = index.get(prototype, {}).get("components", {})
            for entity in group["entities"]:
                entities[entity["uid"]] = {"prototype": prototype,
                                            "components": merge_components(base, components(entity.get("components", [])))}
        parent = collections.Counter(entity["components"].get("Transform", {}).get("parent")
                                     for entity in entities.values()).most_common(1)[0][0]
        station = {uid: entity for uid, entity in entities.items()
                   if entity["components"].get("Transform", {}).get("parent") == parent}
        counts = collections.Counter(key for entity in station.values() for key in COMPONENTS if key in entity["components"])
        prototype_counts = collections.Counter(entity["prototype"] for entity in station.values())
        examples = {"AirAlarm": [], "FireAlarm": []}
        for uid, entity in station.items():
            kind = next((kind for kind in examples if kind in entity["components"]), None)
            if kind is None or len(examples[kind]) >= 2:
                continue
            linked = entity["components"].get("DeviceList", {}).get("devices", [])
            if linked:
                examples[kind].append({"uid": uid, "prototype": entity["prototype"],
                                       "position": entity["components"].get("Transform", {}).get("pos"),
                                       "devices": [{"uid": target, "prototype": entities.get(target, {}).get("prototype"),
                                                    "position": entities.get(target, {}).get("components", {}).get("Transform", {}).get("pos")}
                                                   for target in linked]})
        report["maps"].append({"path": path, "gridUid": parent, "entityCount": len(station),
                               "componentCounts": dict(sorted(counts.items())),
                               "cables": {key: value for key, value in sorted(prototype_counts.items()) if key.startswith("Cable")},
                               "airAlarmExamples": examples["AirAlarm"], "fireAlarmExamples": examples["FireAlarm"]})
    report["evidence"] = evidence(repo, index, report["profiles"], paths)
    report["evidence"]["generator"] = "Tools/StationArchitect/Analysis/survey_native_systems.py"
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[3])
    parser.add_argument("--maps", nargs="+", default=["box", "packed", "exo"])
    parser.add_argument("--output", type=Path, default=Path("Tools/StationArchitect/Catalog/native-systems-survey.json"))
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    root = args.repo.resolve()
    output = args.output if args.output.is_absolute() else root / args.output
    result = survey(root, args.maps)
    text = json.dumps(result, indent=2) + "\n"
    if args.check:
        if not output.is_file() or output.read_text(encoding="utf-8") != text:
            raise SystemExit("Native survey artifact differs; rerun this script without --check")
    else:
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(text, encoding="utf-8")
    print(json.dumps({"maps": len(result["maps"]), "profiles": len(result["profiles"]),
                      "sourceHashes": len(result["evidence"]["sourceHashes"]), "output": str(output),
                      "checked": args.check, "simulationVerified": False}))
