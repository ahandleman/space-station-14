import copy
import unittest

from audit_native_systems import audit_native_systems, crew_collision, disposal_reaches


def fixture():
    prototypes = {
        "Battery": {"components": {"NodeContainer": {"nodes": {"output": {"_nativeNodeType": "CableDeviceNode", "nodeGroupID": "Apc"}}},
                                   "Battery": {"startingCharge": 1000}, "BatteryDischarger": {"voltage": "Apc", "node": "output"}, "PowerNetworkBattery": {"maxSupply": 100}}},
        "Cable": {"components": {"NodeContainer": {"nodes": {"power": {"_nativeNodeType": "CableNode", "nodeGroupID": "Apc"}}},
                                 "PowerProvider": {"voltage": "Apc"}, "ExtensionCableProvider": {"transferRange": 3}}},
        "Receiver": {"components": {"ApcPowerReceiver": {}, "ExtensionCableReceiver": {"receptionRange": 3}}},
    }
    def entity(prototype, x, y):
        return {"prototype": prototype, "components": [{"type": "Transform", "parent": 2, "pos": f"{x+.5},{y+.5}", "anchored": True}]}
    entities = {3: entity("Battery", 3, 2), 4: entity("Cable", 3, 2), 5: entity("Receiver", 6, 2)}
    design = {"definition": {"systems": {"enabled": True, "profiles": [], "controls": []}}, "layout": {"systemDevices": [], "systemControls": []}}
    return design, {}, entities, prototypes


class NativeSystemsAuditTests(unittest.TestCase):
    def check(self, values):
        errors, warnings = [], []
        report = audit_native_systems(*values, 2, errors, warnings)
        return report, errors

    def test_actual_same_tile_cable_and_receiver_range_deliver_initial_power(self):
        report, errors = self.check(fixture())
        self.assertEqual(errors, [])
        self.assertEqual(report["verifiedPoweredReceiverCount"], 1)

    def test_stored_success_cannot_replace_missing_cable(self):
        values = fixture(); del values[2][4]
        values[1]["systems"] = {"complete": True, "terminals": [{"sourceReachable": True}]}
        report, errors = self.check(values)
        self.assertEqual(report["verifiedPoweredReceiverCount"], 0)
        self.assertTrue(any("no serialized source" in error for error in errors))

    def test_wrong_voltage_cable_does_not_power_the_receiver(self):
        values = fixture(); values[3]["Cable"]["components"]["NodeContainer"]["nodes"]["power"]["nodeGroupID"] = "MVPower"
        self.assertTrue(any("no serialized source" in error for error in self.check(values)[1]))

    def test_an_empty_battery_is_not_a_native_source(self):
        values = fixture(); values[3]["Battery"]["components"]["Battery"]["startingCharge"] = 0
        self.assertTrue(any("no serialized source" in error for error in self.check(values)[1]))

    def test_actual_controller_uid_allow_list_and_editable_capacity_are_checked(self):
        values = fixture(); design, manifest, entities, prototypes = values
        entities[6] = copy.deepcopy(entities[5]); entities[6]["components"][0]["pos"] = "5.5,2.5"
        prototypes["Receiver"]["components"]["DeviceNetwork"] = {"deviceNetId": "AtmosDevices", "transmitFrequencyId": "AtmosMonitor", "receiveFrequencyId": "AtmosMonitor", "sendBroadcastAttemptEvent": True}
        entities[5]["components"].append({"type": "DeviceList", "devices": [6], "deviceLimit": 96, "isAllowList": True})
        design["definition"]["systems"]["profiles"] = [{"id": "Device", "prototype": "Receiver", "ports": []}]
        design["definition"]["systems"]["controls"] = [{"id": "Alarm", "maxDevices": 96}]
        design["layout"]["systemDevices"] = [{"id": "controller", "profileId": "Device"}, {"id": "sensor", "profileId": "Device"}]
        design["layout"]["systemControls"] = [{"id": "control", "requirementId": "Alarm", "controllerDeviceId": "controller", "deviceIds": ["sensor"]}]
        manifest["nativeMaterialization"] = {"devices": [{"binding": design["layout"]["systemDevices"][0], "entityUid": 5}, {"binding": design["layout"]["systemDevices"][1], "entityUid": 6}]}
        self.assertEqual(self.check(values)[1], [])
        entities[5]["components"][-1]["devices"] = [999]
        self.assertTrue(any("UID allow-list" in error for error in self.check(values)[1]))
        entities[5]["components"][-1]["devices"] = [6]; entities[5]["components"][-1]["deviceLimit"] = 32
        self.assertTrue(any("deviceLimit" in error for error in self.check(values)[1]))
        entities[5]["components"][-1]["deviceLimit"] = 96
        entities[6]["components"].append({"type": "DeviceNetwork", "receiveFrequencyId": "WrongChannel"})
        self.assertTrue(any("network/frequency" in error for error in self.check(values)[1]))


    def test_native_noncolliding_vent_can_share_a_machine_tile_but_collision_claims_are_checked(self):
        self.assertFalse(crew_collision({"Physics": {"canCollide": False}, "Fixtures": {"fixtures": {"body": {"layer": ["MachineLayer"]}}}}))
        self.assertTrue(crew_collision({"Fixtures": {"fixtures": {"body": {"layer": ["MachineLayer"], "mask": ["MachineMask"]}}}}))
        self.assertTrue(crew_collision({"Physics": {"canCollide": False}, "CollideOnAnchor": {"enable": True}, "Fixtures": {"fixtures": {"body": {"layer": ["MachineLayer"]}}}}))
        self.assertFalse(crew_collision({"Fixtures": {"fixtures": {"body": {"layer": ["TabletopMachineLayer"], "mask": ["TabletopMachineMask"]}}}}))

    def test_native_segment_uid_pose_and_required_components_are_independently_checked(self):
        values = fixture(); design, manifest, entities, prototypes = values
        design["definition"]["systems"]["profiles"] = [{"id": "Cable", "prototype": "Cable", "requiredComponents": ["NodeContainer"], "ports": [{"id": "power", "kind": "power", "connection": "cable", "nodeGroup": "Apc"}]}]
        design["layout"]["systemNetworks"] = [{"id": "Net", "segments": [{"id": "segment", "profileId": "Cable", "position": {"x": 3, "y": 2}, "rotation": 0}]}]
        manifest["nativeMaterialization"] = {"segmentUids": {"segment": 4}}
        self.assertEqual(self.check(values)[1], [])
        design["layout"]["systemNetworks"][0]["segments"][0]["rotation"] = 90
        self.assertTrue(any("serialized position/orientation" in error for error in self.check(values)[1]))
        manifest["nativeMaterialization"]["segmentUids"]["segment"] = 5
        self.assertTrue(any("exact serialized UID/prototype" in error for error in self.check(values)[1]))

    def test_native_gas_source_requires_actual_rotated_matching_layer_pipe_contacts(self):
        values = fixture(); design, manifest, entities, prototypes = values
        prototypes.update({
            "Canister": {"components": {"NodeContainer": {"nodes": {"port": {"_nativeNodeType": "PortablePipeNode", "nodeGroupID": "Pipe", "volume": 1}}}, "GasCanister": {"air": {"volume": 1000, "temperature": 293.15, "moles": {"Oxygen": 100, "Nitrogen": 300}}}}},
            "Connector": {"components": {"NodeContainer": {"nodes": {"pipe": {"_nativeNodeType": "PortPipeNode", "nodeGroupID": "Pipe", "pipeDirection": "South", "volume": 1}}}}},
            "Vent": {"components": {"NodeContainer": {"nodes": {"pipe": {"_nativeNodeType": "PipeNode", "nodeGroupID": "Pipe", "pipeDirection": "North", "volume": 200}}}, "GasVentPump": {}}},
        })
        def put(uid, prototype, x, y):
            entities[uid] = {"prototype": prototype, "components": [{"type": "Transform", "parent": 2, "pos": f"{x+.5},{y+.5}", "anchored": True}]}
        put(6, "Canister", 8, 8); put(7, "Connector", 8, 8); put(8, "Vent", 8, 7)
        report, errors = self.check(values)
        self.assertEqual(errors, []); self.assertGreater(report["finiteGasReservoirs"][0]["initialPressureLowerBoundKpa"], 100)
        entities[8]["components"].append({"type": "AtmosPipeLayers", "pipeLayer": 1})
        self.assertTrue(any("finite air source" in error for error in self.check(values)[1]))
        entities[8]["components"].pop(); entities[7]["components"][0]["rot"] = "180 deg"
        self.assertTrue(any("finite air source" in error for error in self.check(values)[1]))

    def test_ordered_disposal_delivery_rejects_a_missing_segment_and_a_random_dead_branch(self):
        def port(key, x, y, directions, connection="disposal", uid=1):
            return {"key": key, "uid": uid, "connection": connection, "position": (x, y), "directions": directions}
        ports = {"in": port("in", 1, 3, [], "disposalUnit"), "trunk": port("trunk", 1, 3, [(0,-1)], "disposalEntry"),
                 "pipe": port("pipe", 1, 2, [(0,-1),(0,1)]), "out": port("out", 1, 1, [(0,1)], "disposalEntry", uid=2)}
        def index():
            result = {}
            for item in ports.values():
                if item["connection"] != "disposalUnit": result.setdefault(item["position"], []).append(item)
            return result
        self.assertTrue(disposal_reaches("in", {2}, ports, index()))
        removed = ports.pop("pipe"); self.assertFalse(disposal_reaches("in", {2}, ports, index())); ports["pipe"] = removed
        # Entering the preferred north face permits random fallback through every remaining exit.
        ports["pipe"]["directions"] = [(0,1),(0,-1),(1,0)]
        self.assertFalse(disposal_reaches("in", {2}, ports, index()))


if __name__ == "__main__":
    unittest.main()
