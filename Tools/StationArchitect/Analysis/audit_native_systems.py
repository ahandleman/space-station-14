"""Native component/port audit of serialized infrastructure; never executes ECS."""
import collections
import math
import re

from analyze_maps import SourceLoader
from native_system_sources import native_carries_crew


def preserve_native_node_types():
    for name in ("CableNode", "CableDeviceNode", "CableTerminalNode", "CableTerminalPortNode", "PipeNode", "PortPipeNode", "PortablePipeNode"):
        def read(loader, node, kind=name):
            return dict(loader.construct_mapping(node, deep=True), _nativeNodeType=kind)
        SourceLoader.add_constructor("!type:" + name, read)


def merge(base, override):
    result = dict(base)
    for key, value in override.items():
        result[key] = merge(result[key], value) if isinstance(value, dict) and isinstance(result.get(key), dict) else value
    return result


VECTORS = {"South": (0, -1), "East": (1, 0), "North": (0, 1), "West": (-1, 0)}
PIPE_DIRECTIONS = {"Longitudinal": ["South", "North"], "Lateral": ["East", "West"], "SWBend": ["South", "West"],
                   "SEBend": ["South", "East"], "NWBend": ["North", "West"], "NEBend": ["North", "East"],
                   "TSouth": ["South", "East", "West"], "TNorth": ["North", "East", "West"],
                   "TEast": ["East", "South", "North"], "TWest": ["West", "South", "North"], "Fourway": list(VECTORS), "All": list(VECTORS)}


def directions(value):
    if not value:
        return []
    if isinstance(value, int):
        return [name for name, bit in (("North", 1), ("South", 2), ("West", 4), ("East", 8)) if value & bit]
    names = value if isinstance(value, list) else re.split(r"[, |]+", str(value))
    result = []
    for raw in names:
        name = next((name for name in [*VECTORS, *PIPE_DIRECTIONS] if name.lower() == str(raw).lower()), None)
        for direction in PIPE_DIRECTIONS.get(name, [name] if name in VECTORS else []):
            if direction not in result:
                result.append(direction)
    return result


def rotate(vector, rotation):
    for _ in range(rotation % 360 // 90):
        vector = -vector[1], vector[0]
    return vector


def pose(components):
    transform = components.get("Transform", {})
    raw = str(transform.get("rot", "0 deg"))
    rotation = float(raw.split()[0]) * (180 / math.pi if "rad" in raw else 1)
    rounded = round(rotation / 90) * 90
    if not math.isclose(rotation, rounded, abs_tol=1e-5):
        raise ValueError("non-cardinal native rotation")
    position = tuple(map(float, str(transform.get("pos", "0,0")).split(",")))
    return tuple(math.floor(value) for value in position), rounded % 360, transform.get("parent"), transform.get("anchored", True)


def actual_ports(uid, components, position, rotation):
    ports = []
    def add(name, kind, connection, group, exits=(), layer=0, radius=0, volume=0):
        ports.append(dict(key=f"{uid}:{name}", uid=uid, name=name, kind=kind, connection=connection, group=group, layer=layer,
                          position=position, directions=[rotate(VECTORS[direction], rotation) for direction in exits if direction in VECTORS], radius=radius, volume=volume))
    for name, node in components.get("NodeContainer", {}).get("nodes", {}).items():
        kind = node.get("_nativeNodeType", node.get("_nodeType"))
        group = node.get("nodeGroupID", "Default")
        if node.get("enabled") is False or node.get("connectionsEnabled") is False:
            continue
        if kind in ("CableNode", "CableDeviceNode", "CableTerminalNode", "CableTerminalPortNode"):
            add(name, "power", {"CableNode": "cable", "CableDeviceNode": "cableDevice", "CableTerminalNode": "terminal", "CableTerminalPortNode": "terminalPort"}[kind], group,
                ["South"] if kind == "CableTerminalNode" else [])
        elif kind in ("PipeNode", "PortPipeNode", "PortablePipeNode"):
            layer = node.get("pipeLayer", 0)
            if "AtmosPipeLayers" in components and not components["AtmosPipeLayers"].get("pipeLayersLocked", False):
                layer = components["AtmosPipeLayers"].get("pipeLayer", 0)
            add(name, "gas", {"PipeNode": "pipe", "PortPipeNode": "pipePort", "PortablePipeNode": "portable"}[kind], group,
                directions(node.get("pipeDirection")), layer, volume=float(node.get("volume", 200)))
    if "ExtensionCableProvider" in components and "PowerProvider" in components:
        add("$provider", "power", "rangeProvider", "Apc", radius=float(components["ExtensionCableProvider"].get("transferRange", 3)))
    if "ExtensionCableReceiver" in components and "ApcPowerReceiver" in components and components["ApcPowerReceiver"].get("needsPower", True):
        add("$receiver", "power", "rangeReceiver", "Apc", radius=float(components["ExtensionCableReceiver"].get("receptionRange", 3)))
    if "DisposalTube" in components:
        add("$tube", "disposal", "disposalEntry" if "DisposalEntry" in components else "disposal", "Disposal", components["DisposalTube"].get("exits", ["South"]))
        ports[-1]["routingModified"] = any(name in components for name in ("DisposalRouter", "DisposalSignalRouter", "DisposalTagger"))
    if "DisposalUnit" in components:
        add("$entry", "disposal", "disposalUnit", "Disposal")
    return ports


def contact(a, b):
    if a["uid"] == b["uid"] or a["kind"] != b["kind"] or a["group"] != b["group"]:
        return False
    delta = b["position"][0] - a["position"][0], b["position"][1] - a["position"][1]
    distance = abs(delta[0]) + abs(delta[1]); pair = {a["connection"], b["connection"]}
    if a["kind"] == "power":
        if pair == {"rangeProvider", "rangeReceiver"}:
            return delta[0] ** 2 + delta[1] ** 2 <= min(a["radius"], b["radius"]) ** 2
        if pair == {"cable"}:
            return distance <= 1
        if pair in ({"cable", "cableDevice"}, {"cable", "terminal"}):
            return distance == 0
        if pair == {"terminal", "terminalPort"}:
            terminal = a if a["connection"] == "terminal" else b
            vector = delta if terminal is a else (-delta[0], -delta[1])
            return distance == 1 and vector in terminal["directions"]
        return False
    if a["kind"] == "gas":
        if pair == {"pipePort", "portable"}:
            return distance == 0
        return distance == 1 and a["layer"] == b["layer"] and delta in a["directions"] and (-delta[0], -delta[1]) in b["directions"]
    if pair == {"disposalUnit", "disposalEntry"}:
        return distance == 0
    return distance == 1 and delta in a["directions"] and (-delta[0], -delta[1]) in b["directions"]


def reach(edges, roots):
    found = set(roots); pending = collections.deque(found)
    while pending:
        for target in edges.get(pending.popleft(), ()):
            if target not in found:
                found.add(target); pending.append(target)
    return found


# Content.Shared/Physics/CollisionGroup.cs. These are collision bits, not fixture names.
COLLISION_BITS = dict(None_=0, Opaque=1, Impassable=2, MidImpassable=4, HighImpassable=8,
                      LowImpassable=16, GhostImpassable=32, BulletImpassable=64, InteractImpassable=128,
                      DoorPassable=256, AllMask=-1, MobMask=30, MobLayer=65, MachineMask=22,
                      MachineLayer=85, TabletopMachineMask=10, TabletopMachineLayer=65, SubfloorMask=18,
                      TableMask=6, TableLayer=4, FullTileMask=158, FullTileLayer=221, WallLayer=223,
                      GlassLayer=222, AirlockLayer=205, GlassAirlockLayer=204, HumanoidBlockLayer=12)


def collision_bits(value):
    if isinstance(value, int):
        return value
    names = value if isinstance(value, list) else str(value or "None").split(",")
    result = 0
    for name in names:
        name = str(name).strip()
        if name == "None":
            continue
        if name not in COLLISION_BITS:
            return None
        result |= COLLISION_BITS[name]
    return result


def crew_collision(components):
    # CollideOnAnchorSystem changes CanCollide at map startup; its default Enable is false.
    collides = components.get("Physics", {}).get("canCollide", True)
    if "CollideOnAnchor" in components:
        collides = components["CollideOnAnchor"].get("enable", False)
    elif "SubFloorHide" in components:
        collides = False  # SharedSubFloorHideSystem ensures default CollideOnAnchor on startup.
    if collides is False:
        return False
    unknown = False
    for fixture in components.get("Fixtures", {}).get("fixtures", {}).values():
        if not fixture.get("hard", True):
            continue
        layer = collision_bits(fixture.get("layer", [])); mask = collision_bits(fixture.get("mask", []))
        if layer is None or mask is None:
            unknown = True
        elif layer & 30 or mask & 65:
            return True
    return None if unknown else False


def disposal_reaches(start, outlets, ports, tube_index):
    """Native ordered-exit state graph; every random branch must reach a configured outlet."""
    entry = ports[start]
    if entry["connection"] == "disposalUnit":
        starts = [port for port in tube_index.get(entry["position"], []) if port["connection"] == "disposalEntry"]
        if len(starts) != 1:
            return False
        entry = starts[0]
    seed = (entry["key"], (0, 0)); pending = collections.deque([seed]); graph = {}; good = set(); dead = set(); reverse = collections.defaultdict(list)
    while pending:
        state = pending.popleft()
        if state in graph:
            continue
        graph[state] = []; port = ports[state[0]]; exits = port["directions"]; incoming = state[1]
        if port.get("routingModified"):
            dead.add(state); continue  # Generic ordered exits cannot prove custom tag/signal router state.
        if not exits:
            dead.add(state); continue
        back = -incoming[0], -incoming[1]
        if incoming != (0, 0) and exits[0] == back and len(exits) == 1:
            (good if port["uid"] in outlets else dead).add(state); continue
        choices = exits[1:] if incoming != (0, 0) and exits[0] == back else exits[:1]
        for direction in choices:
            next_position = port["position"][0] + direction[0], port["position"][1] + direction[1]
            candidates = [target for target in tube_index.get(next_position, []) if (-direction[0], -direction[1]) in target["directions"]]
            if len(candidates) != 1:
                dead.add(state); continue
            target = candidates[0]["key"], direction
            graph[state].append(target); reverse[target].append(state); pending.append(target)
    needed = {state: len(targets) for state, targets in graph.items()}; pending = collections.deque(good)
    while pending:
        for previous in reverse[pending.popleft()]:
            needed[previous] -= 1
            if needed[previous] == 0 and previous not in dead and previous not in good:
                good.add(previous); pending.append(previous)
    return seed in good


def audit_native_systems(design, manifest, entities, prototypes, grid_uid, errors, warnings):
    program = design.get("definition", {}).get("systems", {})
    if not program.get("enabled"):
        return {"enabled": False, "simulationVerified": False}
    components, poses, ports = {}, {}, {}
    for uid, entity in entities.items():
        inherited = prototypes.get(entity.get("prototype", ""), {}).get("components", {})
        explicit = {value["type"]: {key: item for key, item in value.items() if key != "type"} for value in entity.get("components", [])}
        actual = merge(inherited, explicit)
        try:
            position, rotation, parent, anchored = pose(actual)
        except (ValueError, TypeError):
            continue
        if parent != grid_uid or not anchored:
            continue
        components[uid] = actual; poses[uid] = (position, rotation)
        ports.update({port["key"]: port for port in actual_ports(uid, actual, position, rotation)})
    at = collections.defaultdict(list)
    for port in ports.values():
        at[port["position"]].append(port)
    edges = {key: set() for key in ports}; physical = {key: set() for key in ports}
    terminals = [port for port in ports.values() if port["connection"] == "terminal"]
    for a in ports.values():
        radius = math.ceil(a["radius"]) if a["connection"] == "rangeProvider" else 1
        if a["connection"] == "rangeReceiver":
            continue
        candidates = [b for x in range(a["position"][0] - radius, a["position"][0] + radius + 1) for y in range(a["position"][1] - radius, a["position"][1] + radius + 1) for b in at.get((x, y), ())]
        for b in candidates:
            if not contact(a, b):
                continue
            delta = b["position"][0] - a["position"][0], b["position"][1] - a["position"][1]
            if a["connection"] == b["connection"] == "cable" and any(t["position"] == a["position"] and delta in t["directions"] or t["position"] == b["position"] and (-delta[0], -delta[1]) in t["directions"] for t in terminals):
                continue
            edges[a["key"]].add(b["key"])
            if a["connection"] != "rangeProvider":
                edges[b["key"]].add(a["key"])
            physical[a["key"]].add(b["key"]); physical[b["key"]].add(a["key"])
    by_uid = collections.defaultdict(list)
    for port in ports.values():
        by_uid[port["uid"]].append(port)
    seeds, gas_sources, gas_sinks, gas_containers = set(), {}, set(), {}; stored_energy = 0
    voltage_group = {"High": "HVPower", "Medium": "MVPower", "Apc": "Apc"}
    def native_node(uid, component, default_voltage):
        group = voltage_group.get(component.get("voltage", default_voltage), component.get("voltage", default_voltage))
        return next((port for port in by_uid[uid] if port["kind"] == "power" and port["connection"] not in ("rangeProvider", "rangeReceiver") and port["group"] == group and (not component.get("node") or port["name"] == component["node"])), None)
    for uid, actual in components.items():
        provider = next((port for port in by_uid[uid] if port["connection"] == "rangeProvider"), None)
        if provider:
            source = native_node(uid, actual.get("PowerProvider", {}), "Apc")
            if source:
                edges[source["key"]].add(provider["key"])
        supplier = actual.get("PowerSupplier", {})
        if supplier.get("supplyRate", 0) > 0 and supplier.get("enabled", True):
            source = native_node(uid, supplier, "High")
            if source:
                seeds.add(source["key"])
        charger = native_node(uid, actual["BatteryCharger"], "High") if "BatteryCharger" in actual else None
        discharger = native_node(uid, actual["BatteryDischarger"], "High") if "BatteryDischarger" in actual else native_node(uid, actual["PowerProvider"], "Apc") if "Battery" in actual and "PowerProvider" in actual else None
        if "Battery" in actual and discharger:
            energy = min(float(actual["Battery"].get("startingCharge", 0)), float(actual["Battery"].get("maxCharge", math.inf)))
            if energy > 0 and actual.get("PowerNetworkBattery", {}).get("maxSupply", 1) > 0:
                seeds.add(discharger["key"]); stored_energy += energy
            if charger:
                edges[charger["key"]].add(discharger["key"])
        if "GasCanister" in actual:
            source = next((port for port in by_uid[uid] if port["name"] == actual["GasCanister"].get("port", "port") and port["connection"] == "portable"), None)
            air = actual["GasCanister"].get("air", {}); total = sum(air.get("moles", {}).values())
            if source and air.get("volume", 0) > 0:
                gas_containers[source["key"]] = air
                if total > 0:
                    gas_sources[source["key"]] = air
                else:
                    gas_sinks.add(source["key"])
    power = reach(edges, seeds)
    receivers = [port for port in ports.values() if port["connection"] == "rangeReceiver"]
    unpowered = sorted({port["uid"] for port in receivers if port["key"] not in power})
    if unpowered:
        errors.append("Native receiver hardware has no serialized source-to-receiver power path: " + ", ".join(map(str, unpowered[:32])))
    profiles = {profile["id"]: profile for profile in program.get("profiles", [])}
    materialization = manifest.get("nativeMaterialization", {})
    if "profiles" in materialization and materialization["profiles"] != program.get("profiles", []):
        errors.append("Native manifest profiles differ from the authoritative saved program.")
    if "networks" in materialization and materialization["networks"] != design["layout"].get("systemNetworks", []):
        errors.append("Native manifest segment/terminal records differ from the saved physical networks.")
    records = {item["binding"]["id"]: item for item in materialization.get("devices", [])}
    bindings = {item["id"]: item for item in design["layout"].get("systemDevices", [])}
    binding_ports = {}
    def inspect_profile(id, uid, profile):
        if uid not in components or entities[uid].get("prototype") != profile.get("prototype"):
            errors.append("Native hardware " + id + " lacks its exact serialized UID/prototype."); return
        actual = components[uid]
        for required in profile.get("requiredComponents", []):
            if required not in actual:
                errors.append(f"Native profile {profile.get('id')} requires missing actual component {required} on UID {uid}.")
        for declared in profile.get("ports", []):
            offset = declared.get("offset", {}); transformed = rotate((offset.get("x", 0), offset.get("y", 0)), poses[uid][1])
            expected_position = poses[uid][0][0] + transformed[0], poses[uid][0][1] + transformed[1]
            expected_directions = [rotate(VECTORS[name], poses[uid][1]) for name in directions(declared.get("directions", []))]
            def exits_match(port):
                return port["directions"] == expected_directions if port["kind"] == "disposal" else set(port["directions"]) == set(expected_directions)
            matching = [port for port in by_uid[uid] if port["kind"] == declared.get("kind") and port["connection"] == declared.get("connection") and port["group"] == declared.get("nodeGroup") and port["position"] == expected_position and exits_match(port) and
                        (port["kind"] != "gas" or port["layer"] == declared.get("pipeLayer", 0) and math.isclose(port["volume"], declared.get("volumeLitres", 0))) and
                        (port["connection"] not in ("rangeProvider", "rangeReceiver") or math.isclose(port["radius"], declared.get("range", 0)))]
            if len(matching) != 1:
                errors.append(f"Native profile {profile.get('id')} port {declared.get('id')} is not present uniquely in actual prototype/component geometry on UID {uid}.")
            else:
                binding_ports[id, declared.get("id")] = matching[0]["key"]
        for source in profile.get("sources", []):
            actual_source_port = binding_ports.get((id, source.get("portId")))
            expected_sources = seeds if source.get("kind") == "power" else gas_sources if source.get("kind") == "gas" else gas_sinks
            if actual_source_port not in expected_sources:
                errors.append(f"Native profile {profile.get('id')} source port is not an actual initial native source/sink node on UID {uid}.")
            if source.get("kind") == "power":
                battery = actual.get("Battery", {}); capacity = min(battery.get("startingCharge", 0), battery.get("maxCharge", math.inf))
                if source.get("storedEnergyJoules", 0) > capacity + 1e-4 or source.get("supplyWatts", 0) > actual.get("PowerSupplier", {}).get("supplyRate", 0) + 1e-4:
                    errors.append(f"Native profile {profile.get('id')} source overstates the actual initial battery/generator on UID {uid}.")
            elif source.get("kind") in ("gas", "gasSink"):
                air = actual.get("GasCanister", {}).get("air", {}); total = sum(air.get("moles", {}).values()); volume = air.get("volume", 0)
                pressure = total * 8.31446261815324 * air.get("temperature", 293.15) / volume if volume else 0
                if not math.isclose(source.get("volumeLitres", 0), volume, rel_tol=1e-4, abs_tol=.001) or not math.isclose(source.get("temperatureKelvin", 0), air.get("temperature", 0), abs_tol=.01) or not math.isclose(source.get("pressureKpa", 0), pressure, rel_tol=.01, abs_tol=.1) or source.get("kind") == "gasSink" and total > 1e-6:
                    errors.append(f"Native profile {profile.get('id')} source/sink disagrees with the actual canister mixture on UID {uid}.")
                fractions = {name: amount / total for name, amount in air.get("moles", {}).items()} if total else {}
                if source.get("kind") == "gas" and (set(fractions) != set(source.get("gasFractions", {})) or any(abs(fraction - source["gasFractions"][name]) > .001 for name, fraction in fractions.items())):
                    errors.append(f"Native profile {profile.get('id')} source gas fractions disagree with actual mixture on UID {uid}.")
        if profile.get("carriesCrew") is not None and profile["carriesCrew"] != native_carries_crew(actual):
            errors.append(f"Native crew carrier claim disagrees with actual unit/filter components on UID {uid}.")
        if profile.get("blocksCrew") is False and crew_collision(actual) is not False:
            errors.append(f"Native nonblocking occupancy claim is not supported by actual collider data on UID {uid}.")
    for id, binding in bindings.items():
        record = records.get(id, {}); uid = record.get("entityUid"); profile = profiles.get(binding.get("profileId"), {})
        inspect_profile(id, uid, profile)
        entity = next((entity for entity in design["layout"].get("equipment", []) if entity.get("id") == binding.get("entityId")), None)
        if entity and uid in poses and poses[uid] != ((entity["position"]["x"], entity["position"]["y"]), entity.get("rotation", 0) % 360):
            errors.append("Native device " + id + " serialized position/orientation differs from its actual design hardware.")
    segment_uids = materialization.get("segmentUids", {})
    for network in design["layout"].get("systemNetworks", []):
        for segment in network.get("segments", []):
            uid = segment_uids.get(segment["id"]); inspect_profile(segment["id"], uid, profiles.get(segment.get("profileId"), {}))
            if uid in poses and poses[uid] != ((segment["position"]["x"], segment["position"]["y"]), segment.get("rotation", 0) % 360):
                errors.append("Native segment " + segment["id"] + " serialized position/orientation differs from its design mask.")
    for control in design["layout"].get("systemControls", []):
        rule = next((rule for rule in program.get("controls", []) if rule["id"] == control.get("requirementId")), {})
        controller_uid = records.get(control.get("controllerDeviceId"), {}).get("entityUid")
        expected = {records.get(id, {}).get("entityUid") for id in control.get("deviceIds", [])}
        actual = components.get(controller_uid, {}).get("DeviceList", {})
        if None in expected or set(actual.get("devices", [])) != expected or not actual.get("isAllowList", True):
            errors.append("Native control " + control["id"] + " has a missing/wrong serialized UID allow-list.")
        if actual.get("deviceLimit", 32) != rule.get("maxDevices", 32):
            errors.append("Native control " + control["id"] + " deviceLimit differs from its configured editable capacity.")
        sender = components.get(controller_uid, {}).get("DeviceNetwork", {})
        if not sender or not sender.get("sendBroadcastAttemptEvent", False) or not sender.get("autoConnect", True):
            errors.append("Native control " + control["id"] + " cannot apply its UID allow-list to actual native broadcasts.")
        transmit = sender.get("transmitFrequency", sender.get("transmitFrequencyId"))
        for uid in expected - {None}:
            receiver = components.get(uid, {}).get("DeviceNetwork", {})
            receive = receiver.get("receiveFrequency", receiver.get("receiveFrequencyId"))
            if not receiver or sender.get("deviceNetId", "None") != receiver.get("deviceNetId", "None") or transmit is None or receive != transmit or not receiver.get("autoConnect", True):
                errors.append(f"Native control {control['id']} cannot reach device UID {uid} on the actual configured device network/frequency.")

    gas_pressure = []; seen = set()
    for source, air in gas_sources.items():
        if source in seen:
            continue
        connected = reach(physical, [source]); seen.update(connected)
        members = [key for key in connected if key in gas_sources]; source_volume = sum(gas_containers[key]["volume"] for key in connected if key in gas_containers)
        minimum_temperature = min(gas_sources[key].get("temperature", 293.15) for key in members)
        moles_temperature = sum(sum(gas_sources[key].get("moles", {}).values()) * minimum_temperature for key in members)
        pipe_volume = sum(ports[key]["volume"] for key in connected if ports[key]["kind"] == "gas")
        volume = source_volume + pipe_volume; pressure = moles_temperature * 8.314462618 / volume if volume else 0
        gas_pressure.append({"sourceUids": sorted({ports[key]["uid"] for key in members}), "sourceVolumeLitres": sum(gas_sources[key]["volume"] for key in members), "connectedContainerVolumeLitres": source_volume, "pipeVolumeLitres": pipe_volume, "initialPressureLowerBoundKpa": pressure})
        for key in connected:
            uid = ports[key]["uid"]; vent = components[uid].get("GasVentPump")
            if vent is not None and pressure * vent.get("pumpPower", 2) + 1e-6 < vent.get("externalPressureBound", 101.325):
                errors.append(f"Native air reserve dilution leaves vent UID {uid} below its configured external pressure bound even before gas is spent.")
    source_reached = reach(physical, gas_sources); sink_reached = reach(physical, gas_sinks)
    for uid, actual in components.items():
        for component, node_field, reachable in (("GasVentPump", "inlet", source_reached), ("GasVentScrubber", "outlet", sink_reached)):
            if component not in actual or actual[component].get("enabled") is False:
                continue
            node = actual[component].get(node_field, "pipe")
            candidates = [port for port in by_uid[uid] if port["name"] == node and port["kind"] == "gas"]
            if not candidates or not any(port["key"] in reachable for port in candidates):
                errors.append(f"Native {component} UID {uid} lacks its actual {'finite air source' if component == 'GasVentPump' else 'empty waste sink'} pipe connection.")
    tube_index = collections.defaultdict(list)
    for port in ports.values():
        if port["kind"] == "disposal" and port["connection"] != "disposalUnit":
            tube_index[port["position"]].append(port)
    verified_disposal = 0; crew_inlets = 0
    rules = {rule["id"]: rule for rule in program.get("networks", [])}
    for network in design["layout"].get("systemNetworks", []):
        if rules.get(network.get("requirementId"), {}).get("kind") != "disposal":
            continue
        outlets = {records.get(terminal.get("deviceId"), {}).get("entityUid") for terminal in network.get("terminals", []) if terminal.get("role") == "outlet"}
        for terminal in network.get("terminals", []):
            if terminal.get("role") != "inlet":
                continue
            start = binding_ports.get((terminal.get("deviceId"), terminal.get("portId")))
            if not start or not disposal_reaches(start, outlets, ports, tube_index):
                errors.append("Native waste inlet " + str(terminal.get("deviceId")) + " has no complete ordered-exit tube path to its configured outlet; branch dead ends and loops are not delivery.")
            else:
                verified_disposal += 1
                if native_carries_crew(components[ports[start]["uid"]]): crew_inlets += 1
    return {"enabled": True, "actualNativePorts": len(ports), "powerReceiverCount": len(receivers), "verifiedPoweredReceiverCount": len(receivers) - len(unpowered),
            "initialStoredEnergyJoules": stored_energy, "finiteGasReservoirs": gas_pressure, "controlCount": len(design["layout"].get("systemControls", [])),
            "verifiedWasteInlets": verified_disposal, "verifiedCrewCapableWasteInlets": crew_inlets, "repositoryComponentsInspected": True, "simulationVerified": False}
