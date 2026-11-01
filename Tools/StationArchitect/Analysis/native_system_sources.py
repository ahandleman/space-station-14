"""Read-only native hardware facts shared by the reproducible infrastructure scripts.

YAML type tags remain inert data. No game component or arbitrary Python object is
constructed. This is source validation, not a running SS14 simulation.
"""
from __future__ import annotations

import re

from analyze_maps import SourceLoader, prototype_index, prototype_inputs_hash, sha256

NODE_TYPES = ("CableNode", "CableDeviceNode", "CableTerminalNode", "CableTerminalPortNode",
              "PipeNode", "PortPipeNode", "PortablePipeNode")
CODE_SOURCES = (
    "Content.Server/Power/Nodes/CableDeviceNode.cs",
    "Content.Server/Power/Nodes/CableTerminalNode.cs",
    "Content.Server/Power/Nodes/CableTerminalPortNode.cs",
    "Content.Server/Power/Components/ExtensionCableProviderComponent.cs",
    "Content.Server/Power/Components/ExtensionCableReceiverComponent.cs",
    "Content.Server/Power/EntitySystems/ExtensionCableSystem.cs",
    "Content.Server/NodeContainer/Nodes/PipeNode.cs",
    "Content.Server/NodeContainer/Nodes/PortPipeNode.cs",
    "Content.Server/NodeContainer/Nodes/PortablePipeNode.cs",
    "Content.Shared/Atmos/PipeDirection.cs",
    "Content.Server/Atmos/Piping/Unary/EntitySystems/GasCanisterSystem.cs",
    "Content.Shared/DeviceConfigurator/Components/DeviceListComponent.cs",
    "Content.Shared/DeviceConfigurator/Systems/DeviceListSystem.cs",
    "Content.Shared/DeviceNetwork/Components/DeviceNetworkComponent.cs",
    "Content.Shared/DeviceNetwork/Systems/DeviceNetworkSystem.cs",
    "Content.Shared/DeviceNetwork/Systems/WiredNetworkSystem.cs",
    "Content.Server/Atmos/Monitor/Systems/FireAlarmSystem.cs",
    "Content.Server/Atmos/Monitor/Systems/AtmosAlarmableSystem.cs",
    "Content.Server/Doors/Systems/FirelockSystem.cs",
    "Content.Shared/Disposal/Tube/DisposalTubeSystem.cs",
    "Content.Shared/Disposal/Unit/SharedDisposalUnitSystem.cs",
    "Content.Shared/Disposal/Unit/SharedDisposalUnitSystem.Interactions.cs",
    "Content.Shared/Disposal/Unit/DisposalUnitComponent.cs",
)
_PIPE_ALIASES = {}


def prototypes(repo):
    _PIPE_ALIASES.clear()
    source = (repo / "Content.Shared/Atmos/PipeDirection.cs").read_text(encoding="utf-8-sig")
    body = re.search(r"public enum PipeDirection\s*\{(.*?)\}", source, re.S).group(1)
    cardinal = {"North", "South", "East", "West"}
    for name, expression in re.findall(r"(\w+)\s*=\s*([^,\r\n]+),", body):
        if name in cardinal:
            _PIPE_ALIASES[name] = {name}
        elif expression.strip() in ("0", "-1"):
            _PIPE_ALIASES[name] = set() if expression.strip() == "0" else cardinal
        else:
            _PIPE_ALIASES[name] = set().union(*(_PIPE_ALIASES[part.strip()] for part in expression.split("|")))
    for name in NODE_TYPES:
        def constructor(loader, node, kind=name):
            return dict(loader.construct_mapping(node, deep=True), _nodeType=kind)
        SourceLoader.add_constructor("!type:" + name, constructor)
    return prototype_index(repo)


def evidence(repo, index, names, extra_paths=()):
    paths = set(CODE_SOURCES) | set(extra_paths)
    for name in names:
        for source in [name, *index.get(name, {}).get("ancestors", [])]:
            path = index.get(source, {}).get("sourcePath")
            if path:
                paths.add(path)
    paths.update("Tools/StationArchitect/Analysis/" + name for name in
                 ("analyze_maps.py", "native_system_sources.py", "write_native_profiles.py", "survey_native_systems.py", "audit_native_profiles.py"))
    missing = sorted(path for path in paths if not (repo / path).is_file())
    if missing:
        raise ValueError("Evidence source is missing: " + ", ".join(missing))
    return {"prototypeInputsSha256": prototype_inputs_hash(repo),
            "sourceHashes": {path: sha256(repo / path) for path in sorted(paths)}}


def flags(value):
    parts = [str(item) for item in value] if isinstance(value, list) else re.split(r"[, |]+", str(value or ""))
    return sorted(set().union(*(_PIPE_ALIASES.get(part, {part}) for part in parts if part and part != "None")))


def native_occupancy(components):
    nonblocking = components.get("Physics", {}).get("canCollide") is False and "Door" not in components
    if "SubFloorHide" in components and "AtmosPipeLayers" in components:
        layer = "atmosphere" + str(components["AtmosPipeLayers"].get("pipeLayer", 0))
    elif "SubFloorHide" in components and "DisposalTube" in components:
        layer = "disposal"
    elif "WallMount" in components:
        layer = "boundary"
    elif nonblocking and "AtmosMonitor" in components:
        layer = "sensor"
    else:
        layer = "auto"
    return {"occupancyLayer": layer, "blocksCrew": not nonblocking}


def native_carries_crew(components):
    """Conservative native-unit capability; item-only mail is never crew travel."""
    if "DisposalUnit" not in components or "MailingUnit" in components:
        return False
    whitelist = components["DisposalUnit"].get("whitelist") or {}
    if whitelist.get("components") == ["Item"] and not whitelist.get("tags"):
        return False
    return True


def audit_profiles(index, profiles):
    """Compare declared ports against independently resolved native component facts."""
    errors = []
    node_connections = {"CableNode": "cable", "CableDeviceNode": "cableDevice",
                        "CableTerminalNode": "terminal", "CableTerminalPortNode": "terminalPort",
                        "PipeNode": "pipe", "PortPipeNode": "pipePort", "PortablePipeNode": "portable"}
    ids = set()
    for profile in profiles:
        label = profile["id"]
        if label in ids:
            errors.append(f"{label}: duplicate profile ID")
        ids.add(label)
        native = index.get(profile["prototype"])
        if native is None:
            errors.append(f"{label}: native prototype does not exist")
            continue
        components = native["components"]
        if profile.get("carriesCrew") is not None and profile["carriesCrew"] != native_carries_crew(components):
            errors.append(f"{label}: crew carrier declaration disagrees with native disposal filters/components")
        for field, expected in native_occupancy(components).items():
            if profile.get(field) != expected:
                errors.append(f"{label}: {field}={profile.get(field)} disagrees with resolved native behavior {expected}")
        for name in profile["requiredComponents"]:
            if name not in components:
                errors.append(f"{label}: native prototype lacks required {name}")
        for port in profile["ports"]:
            path = label + "." + port["id"]
            connection = port["connection"]
            if connection in node_connections.values():
                node = components.get("NodeContainer", {}).get("nodes", {}).get(port["id"], {})
                if node_connections.get(node.get("_nodeType")) != connection:
                    errors.append(f"{path}: connection {connection} disagrees with native {node.get('_nodeType')}")
                if port["nodeGroup"] != node.get("nodeGroupID"):
                    errors.append(f"{path}: node group {port['nodeGroup']} disagrees with native {node.get('nodeGroupID')}")
                if connection in ("pipe", "pipePort"):
                    if flags(port["directions"]) != flags(node.get("pipeDirection")):
                        errors.append(f"{path}: directions disagree with native pipeDirection")
                    layer = components.get("AtmosPipeLayers", {}).get("pipeLayer", node.get("pipeLayer", 0))
                    if port["pipeLayer"] != layer:
                        errors.append(f"{path}: layer {port['pipeLayer']} disagrees with native {layer}")
                if connection in ("pipe", "pipePort", "portable") and port["volumeLitres"] != node.get("volume", 200):
                    errors.append(f"{path}: volume {port['volumeLitres']} disagrees with native {node.get('volume', 200)}")
            elif connection == "rangeProvider":
                if not {"ExtensionCableProvider", "PowerProvider"}.issubset(components):
                    errors.append(f"{path}: native extension/provider components missing")
                if port["range"] != components.get("ExtensionCableProvider", {}).get("transferRange", 3):
                    errors.append(f"{path}: provider range disagrees with native component")
            elif connection == "rangeReceiver":
                if not {"ExtensionCableReceiver", "ApcPowerReceiver"}.issubset(components):
                    errors.append(f"{path}: native extension/receiver components missing")
                if port["range"] != components.get("ExtensionCableReceiver", {}).get("receptionRange", 3):
                    errors.append(f"{path}: receiver range disagrees with native component")
            elif connection in ("disposal", "disposalEntry"):
                # DisposalTube.SelectNextDirection uses the FIRST exit as its
                # preferred direction, then ordered fallback exits. Sets/flags
                # would incorrectly certify a different routing behavior.
                declared = [str(direction).casefold() for direction in port["directions"]]
                actual = [str(direction).casefold() for direction in components.get("DisposalTube", {}).get("exits", ["South"])]
                if "DisposalTube" not in components or declared != actual:
                    errors.append(f"{path}: ordered disposal directions disagree with native tube")
                if connection == "disposalEntry" and "DisposalEntry" not in components:
                    errors.append(f"{path}: disposal-entry component missing")
            elif connection == "disposalUnit" and "DisposalUnit" not in components:
                errors.append(f"{path}: disposal-unit component missing")
        if profile.get("transfers") and "BatteryCharger" in components:
            required = {"Battery", "BatteryCharger", "PowerNetworkBattery"}
            if "BatteryDischarger" in components:
                required.add("BatteryDischarger")
            if not required.issubset(profile["requiredComponents"]):
                errors.append(f"{label}: battery transfer omits required native components {sorted(required - set(profile['requiredComponents']))}")
    return errors
