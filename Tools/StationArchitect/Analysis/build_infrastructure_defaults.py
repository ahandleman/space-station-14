"""Compose editable standard infrastructure policy from native prototype evidence.

Writes a standalone YAML fragment by default. --apply installs it into the two
canonical programs; it does not alter any native map/prototype or room budget.
"""
import argparse
import copy
import json
import re
from pathlib import Path

import yaml
from native_system_sources import native_carries_crew
from analyze_maps import SourceLoader, prototype_index


class ProgramDumper(yaml.SafeDumper):
    # Station Architect deliberately accepts explicit YAML trees, not aliases.
    # Shared Python source-reference lists must therefore be expanded in output.
    def ignore_aliases(self, data):
        return True


def program_yaml(value):
    return yaml.dump(value, Dumper=ProgramDumper, sort_keys=False, width=110)


def build(repo, document):
    # Preserve exact native node types while resolving prototype inheritance.
    for name in ["CableNode", "CableDeviceNode", "CableTerminalPortNode"]:
        def node(loader, value, kind=name):
            return dict(loader.construct_mapping(value, deep=True), _nodeType=kind)
        SourceLoader.add_constructor("!type:" + name, node)
    prototypes = prototype_index(repo)
    profiles = copy.deepcopy(json.loads((repo / "Tools/StationArchitect/Catalog/native-profiles.json").read_text())["profiles"])
    by_id = {profile["id"]: profile for profile in profiles}
    devices, networks, controls = [], [], []
    all_zones = {"networks": ["rooms", "publicHall", "maintenance", "exteriorMaintenance"]}
    breathable = {"networks": ["rooms", "publicHall", "maintenance"], "pressures": ["standard"], "placements": ["interior", "either"]}

    def device(id, profile, selector=None, scope="room", count=(1, 1, 1), placement="boundary", work=0, coverage=None, **extra):
        value = {"id": id, "profileId": profile, "selector": copy.deepcopy(selector or all_zones), "scope": scope,
                 "content": {"id": id, "prototypes": [by_id[profile]["prototype"]],
                             "count": dict(zip(["min", "target", "max"], count)), "placement": placement, "minArea": work},
                 "sourceRefs": by_id[profile].get("sourceRefs", []), **extra}
        if coverage is not None:
            value["maxCoverageDistance"] = coverage
        devices.append(value)
        return value

    def terminal(rule, port, role="consumer", required=True):
        return {"deviceRuleId": rule, "portId": port, "role": role, "required": required}

    def network(id, kind, segments, terminals, scope="station", **extra):
        networks.append({"id": id, "kind": kind, "scope": scope, "priority": "required",
                         "segmentProfiles": segments, "terminals": terminals, "maxLength": 4096, **extra})

    smes = device("StorageBanks", "smesbasic", {"networks": ["rooms"]}, scope="room", count=(0, 0, 64),
                  placement="floor", bindExisting=True, profileIds=["smesbasic", "smesadvanced"],
                  description="Bind every actual selected SMES variant. This proves finite stored energy, not generator startup or recharge.")
    smes["content"]["prototypes"] = ["SMESBasic", "SMESAdvanced"]
    device("Substations", "substation", scope="owner", placement="floor", work=1,
           description="One native HV-to-MV substation for each selected owner, including station circulation.")
    device("AreaPower", "apc", scope="compartment", count=(1, 1, 2),
           description="Native APCs divide LV service by actual closed-door air compartments.")
    device("RoomLighting", "light", {"networks": ["rooms"]}, count=(1, 1, 8), coverage=8,
           description="Wall lights cover room floor within a configurable geodesic distance.")
    device("PassageLighting", "light", {"networks": ["publicHall", "maintenance", "exteriorMaintenance"]},
           scope="compartment", count=(1, 2, 64), coverage=10)
    device("AirSupplyVents", "vent", breathable, scope="compartment", count=(1, 1, 64), placement="floor", work=0, coverage=16)
    device("AirScrubbers", "scrubber", breathable, scope="compartment", count=(1, 1, 64), placement="floor", work=0, coverage=16)
    device("AirSensors", "air-sensor", breathable, scope="compartment", placement="floor", work=0)
    device("AirAlarms", "air-alarm", breathable, scope="compartment")
    device("FireAlarms", "fire-alarm", breathable, scope="compartment", count=(1, 1, 64), coverage=16)
    door_profiles = [profile["id"] for profile in profiles if profile["id"].startswith("door-")]
    doors = device("WalkingDoorPower", door_profiles[0], {**all_zones, "interfaceTypes": ["doorway", "airlock", "dockingPort"]},
                   count=(0, 0, 64), bindInterfaces=True, profileIds=door_profiles,
                   description="Bind actual exported doorway hardware, including shuttle docks, without creating duplicate doors.")
    doors["content"]["prototypes"] = [by_id[id]["prototype"] for id in door_profiles]
    doors["profileIds"].append("firelock")
    doors["content"]["prototypes"].append("Firelock")
    device("ExistingFirelocks", "firelock", {"networks": ["rooms"]}, count=(0, 0, 64), placement="floor", bindExisting=True,
           description="Bind authored real Firelock hardware. Ordinary airlocks are not automatic firelock outputs.")
    mail = copy.deepcopy(by_id["waste-unit"])
    mail.update(id="mail-unit", prototype="MailingUnit", sourceRefs=[prototypes["MailingUnit"]["sourcePath"]])
    profiles.append(mail); by_id[mail["id"]] = mail
    transport = device("TransportTerminalPower", "waste-unit", {"networks": ["rooms"]}, count=(0, 0, 64),
                       placement="floor", bindTransport=True, profileIds=["mail-unit"],
                       description="Power actual crew-tube and mail endpoints without connecting their separate tubes to the waste network.")
    transport["content"]["prototypes"] = ["DisposalUnit", "MailingUnit"]
    device("AirReserve", "air-reserve", {"paths": ["Engineering/GasStorage"]}, placement="floor", work=2, count=(12, 12, 16),
           bindExisting=True, contentIds=["BreathingAirCanisters"],
           description="Bind the room program's twelve native 1500-litre air canisters. Their mandatory working area pressures room growth before infrastructure routing; capacity and diluted supply pressure are checked separately.")
    device("ScrubReserve", "scrub-reserve", {"paths": ["Engineering/GasStorage"]}, placement="floor", work=2, count=(2, 2, 6),
           bindExisting=True, contentIds=["ScrubberStorageCanisters"],
           description="Bind the room program's finite empty scrubber storage. Reclamation and replenishment require later plant design.")
    device("ExistingWasteUnits", "waste-unit", {"networks": ["rooms"]}, placement="floor", count=(0, 0, 64), bindExisting=True)
    device("PublicWasteUnits", "waste-unit", {"paths": ["Commons/Concourse", "Service/ServiceCirculation", "Cargo/CargoCirculation"]},
           placement="floor", work=1, count=(1, 1, 2))
    outlet = copy.deepcopy(by_id["waste-trunk"])
    outlet.update(id="waste-receiving-trunk", kind="device")
    profiles.append(outlet); by_id[outlet["id"]] = outlet
    device("WasteReceivingTrunk", outlet["id"], {"paths": ["Engineering/Disposals"]}, placement="floor", work=4,
           description="A real unoccupied native disposal trunk ejects into reserved receiving floor beside the recycling area.")

    # Existing machinery is bound by actual prototype, never inferred from a room name.
    native_by_prototype = {profile["prototype"]: profile for profile in profiles if profile["kind"] == "device"}
    content_prototypes = set()
    def rooms(children):
        for room in children:
            yield room
            yield from rooms(room.get("rooms", []))
    for owner in [*document["departments"], document["maintenance"]]:
        for room in rooms(owner["rooms"]):
            for content in room.get("contents", []):
                content_prototypes.update(content["prototypes"])
    existing_receivers = []
    direct_power = {"HVPower": [], "MVPower": [], "Apc": []}
    for prototype in sorted(content_prototypes):
        native = prototypes[prototype]; components = native["components"]
        if prototype in native_by_prototype:
            continue
        ports = []
        receiver = "ExtensionCableReceiver" in components and "ApcPowerReceiver" in components
        if receiver:
            ports.append({"id": "power" if "Door" in components else "receiver", "kind": "power", "connection": "rangeReceiver", "nodeGroup": "Apc",
                          "range": components["ExtensionCableReceiver"].get("receptionRange", 3)})
        for id, node in components.get("NodeContainer", {}).get("nodes", {}).items():
            if node.get("_nodeType") == "CableDeviceNode" and node.get("nodeGroupID") in direct_power:
                ports.append({"id": id, "kind": "power", "connection": "cableDevice", "nodeGroup": node["nodeGroupID"]})
        if not ports:
            continue
        id = "native-" + re.sub(r"[^A-Za-z0-9]", "", prototype).lower()
        profile = {"id": id, "prototype": prototype, "kind": "device", "ports": ports,
                   "requiredComponents": (["ExtensionCableReceiver", "ApcPowerReceiver"] if receiver else ["NodeContainer"]) + (["Door"] if "Door" in components else []),
                   "requiresPower": receiver or "PowerConsumer" in components, "sourceRefs": [native["sourcePath"]]}
        profiles.append(profile); by_id[id] = profile
        rule = "Bind" + prototype
        device(rule, id, {"networks": ["rooms"]}, placement="floor", count=(0, 0, 64), bindExisting=True)
        for port in ports:
            target = terminal(rule, port["id"], required=False)
            if port["connection"] == "rangeReceiver":
                existing_receivers.append(target)
            else:
                direct_power[port["nodeGroup"]].append(target)
    # Door profiles include exact authored catalog variants as well as export defaults.
    # Their uniform 'power' receiver name keeps one virtual-door binding rule usable.
    doors["profileIds"] = [profile["id"] for profile in profiles if profile["kind"] == "device" and "Door" in prototypes[profile["prototype"]]["components"]]
    doors["content"]["prototypes"] = list(dict.fromkeys(by_id[id]["prototype"] for id in doors["profileIds"]))
    network("HighVoltageDistribution", "power", ["hv-cable", "cable-terminal"],
            [terminal("StorageBanks", "output", "source", False), terminal("Substations", "input"), *direct_power["HVPower"]])
    network("MediumVoltageDistribution", "power", ["mv-cable"],
            [terminal("Substations", "output", "source"), terminal("AreaPower", "input"), *direct_power["MVPower"]], scope="owner")
    lv_devices = ["RoomLighting", "PassageLighting", "AirSupplyVents", "AirScrubbers", "AirSensors", "AirAlarms", "FireAlarms", "WalkingDoorPower", "ExistingFirelocks", "TransportTerminalPower", "ExistingWasteUnits", "PublicWasteUnits"]
    network("LowVoltageService", "power", ["lv-cable"], [terminal("AreaPower", "output", "source")]+
            [terminal(rule, "power", required=False) for rule in lv_devices]+existing_receivers+direct_power["Apc"], scope="compartment")
    network("BreathingAir", "gas", [id for id in by_id if id.startswith("gas-0-")],
            [terminal("AirReserve", "port", "source"), terminal("AirSupplyVents", "pipe")], minPressureKpa=101.325)
    network("ScrubberExhaust", "gas", [id for id in by_id if id.startswith("gas-1-")],
            [terminal("AirScrubbers", "pipe", "inlet"), terminal("ScrubReserve", "port", "outlet")])
    network("StationWaste", "disposal", ["waste-trunk", "waste-straight", "waste-bend", "waste-junction", "waste-junction-flipped", "waste-y-junction"],
            [terminal("ExistingWasteUnits", "entry", "inlet", False), terminal("PublicWasteUnits", "entry", "inlet", False), terminal("WasteReceivingTrunk", "tube", "outlet")])
    controls.append({"id": "CompartmentAirControls", "controllerRuleId": "AirAlarms", "deviceRuleIds": ["AirSupplyVents", "AirScrubbers", "AirSensors"],
                     "scope": "compartment", "component": "DeviceList", "maxDevices": 192, "priority": "required"})
    controls.append({"id": "CompartmentFireControls", "controllerRuleId": "FireAlarms",
                     "deviceRuleIds": ["AirSupplyVents", "AirScrubbers", "AirSensors", "ExistingFirelocks"],
                     "scope": "compartment", "component": "DeviceList", "maxDevices": 192, "priority": "required"})
    # These are placement exclusivity groups, not invented native render layers.
    # Non-colliding unary atmos devices do not consume a solid furniture tile.
    # Every alias of one prototype receives the same evidence-based behavior.
    for profile in profiles:
        components = prototypes[profile["prototype"]]["components"]
        profile["carriesCrew"] = native_carries_crew(components)
        if profile["carriesCrew"]:
            if not any(port.get("connection") == "disposalUnit" for port in profile.get("ports", [])):
                profile["ports"].append({"id": "nativeCrewEntry", "kind": "disposal", "connection": "disposalUnit", "nodeGroup": "Disposal", "directions": []})
            if "DisposalUnit" not in profile["requiredComponents"]:
                profile["requiredComponents"].append("DisposalUnit")
        nonblocking = components.get("Physics", {}).get("canCollide") is False and "Door" not in components
        profile["blocksCrew"] = not nonblocking
        if "SubFloorHide" in components and "AtmosPipeLayers" in components:
            profile["occupancyLayer"] = "atmosphere" + str(components["AtmosPipeLayers"].get("pipeLayer", 0))
        elif "SubFloorHide" in components and "DisposalTube" in components:
            profile["occupancyLayer"] = "disposal"
        elif "WallMount" in components:
            profile["occupancyLayer"] = "boundary"
        elif nonblocking and "AtmosMonitor" in components:
            profile["occupancyLayer"] = "sensor"
        else:
            profile["occupancyLayer"] = "auto"
    return {"enabled": True, "profiles": profiles, "devices": devices, "networks": networks, "controls": controls,
            "generation": {"maxStatesPerConnection": 50000, "maxTotalSearchStates": 2000000, "maxGeneratedSegments": 65536,
                           "maxDeviceCandidatesPerScope": 16384, "maxDevices": 4096, "maxWitnessSteps": 32}}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apply", action="store_true")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[3]
    catalog = root / "Tools/StationArchitect/Catalog/standard-station.yaml"
    document = yaml.safe_load(catalog.read_text(encoding="utf-8"))
    systems = build(root, document)
    output = catalog.with_name("infrastructure-defaults.yaml")
    output.write_text(program_yaml(systems), encoding="utf-8")
    if args.apply:
        for path in [catalog, root / "Tools/StationArchitect/examples/station-program.yaml"]:
            value = yaml.safe_load(path.read_text(encoding="utf-8"))
            value["systems"] = systems
            path.write_text(program_yaml(value), encoding="utf-8")
    print(json.dumps({"profiles": len(systems["profiles"]), "deviceRules": len(systems["devices"]), "networks": len(systems["networks"]), "output": str(output), "applied": args.apply}))
