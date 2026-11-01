"""Exercise nested per-parent selection, complex moves and import over the running HTTP API.

The API is stateless: this creates a local test draft and does not alter saved files.
Run against the current Station Architect server; no game assemblies are needed.
"""
import argparse
import copy
import json
from pathlib import Path
import urllib.error
import urllib.request


def check(base_url):
    def post(path, payload):
        request = urllib.request.Request(base_url.rstrip("/") + "/api/design/" + path,
                                         json.dumps(payload).encode(), {"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(request, timeout=120) as response:
                return json.load(response)
        except urllib.error.HTTPError as exception:
            raise AssertionError(f"{path}: HTTP {exception.code}: {exception.read().decode()}") from exception

    def active(project):
        return [room for room in project["layout"]["rooms"] if room["enabled"] and not room["orphaned"]]

    def flatten(node):
        yield node
        for child in node.get("children", []):
            yield from flatten(child)

    definition = {
        "schemaVersion": 2, "id": "NestedApiFixture", "name": "Nested API fixture",
        "departments": [{
            "id": "Alpha", "area": {"min": 20, "target": 132, "max": 1000},
            "shape": {"maxAspectRatio": 10, "minFillRatio": .01},
            "rooms": [{
                "id": "Suite", "count": {"min": 1, "target": 2, "max": 2},
                "area": {"min": 20, "target": 60, "max": 160},
                "rooms": [{"id": "Booth", "count": {"min": 0, "target": 1, "max": 2},
                           "area": {"min": 2, "target": 6, "max": 12}}]
            }]
        }]
    }
    project = post("new", {"definition": definition})
    assert project["report"]["success"], project["report"]
    key = "Alpha/Suite#1/Booth"
    project = post("edit", {"project": project, "edit": {"kind": "set-count", "nodeId": key, "value": 2}})
    assert project["selection"]["parentRoomCounts"][key] == 2
    assert len([room for room in active(project) if room["parentInstanceId"] == "Alpha/Suite#1"]) == 2
    assert len([room for room in active(project) if room["parentInstanceId"] == "Alpha/Suite#2"]) == 1
    project = post("stage/placement", project)
    project = post("stage/growth", project)
    assert all(room["cells"] for room in active(project)), project["report"]
    before = copy.deepcopy(project)
    rooms_before = {room["id"]: room for room in active(before)}
    parent_id = "Alpha/Suite#1"
    parent_before = rooms_before[parent_id]
    target = {"x": parent_before["seed"]["x"] + 3, "y": parent_before["seed"]["y"] + 4}
    moved = post("edit", {"project": project, "edit": {"kind": "move-room", "nodeId": parent_id, "position": target}})
    moved_ids = {parent_id} | {room["id"] for room in active(before) if room["parentInstanceId"] == parent_id}
    rooms_after = {room["id"]: room for room in active(moved)}
    for room_id, old_room in rooms_before.items():
        expected = [{"x": cell["x"] + 3, "y": cell["y"] + 4} for cell in old_room["cells"]] if room_id in moved_ids else old_room["cells"]
        assert rooms_after[room_id]["cells"] == expected, room_id
        assert rooms_after[room_id]["parentInstanceId"] == old_room["parentInstanceId"]
    assert moved["definition"] == before["definition"], "Geometry edits must preserve authored requirements."
    assert any(state["status"] == "dirty" for state in moved["stageStates"]), "Moving a complex must invalidate dependent stages."
    nodes = {node["id"]: node for node in flatten(moved["tree"])}
    parent_node = nodes[parent_id]
    assert parent_node["ownFloorArea"] == len(rooms_after[parent_id]["cells"])
    assert parent_node["floorArea"] == sum(len(rooms_after[room_id]["cells"]) for room_id in moved_ids)
    assert moved["tree"]["floorArea"] == sum(len(room["cells"]) for room in active(moved))
    child_id = "Alpha/Suite#1/Booth#1"
    nodes[child_id]["requirements"]["area"]["max"] = 999  # Saved annotations must not become requirements.
    imported = post("import", moved)
    imported_nodes = {node["id"]: node for node in flatten(imported["tree"])}
    assert imported_nodes[child_id]["requirements"]["area"]["max"] == 12
    assert imported["selection"]["parentRoomCounts"][key] == 2
    assert {room["id"]: room["cells"] for room in active(imported)} == {room["id"]: room["cells"] for room in active(moved)}
    return {"success": True, "baseUrl": base_url, "activeInstances": len(active(imported)),
            "movedComplexInstances": len(moved_ids), "perParentCount": imported["selection"]["parentRoomCounts"][key],
            "exclusiveParentFloor": imported_nodes[parent_id]["ownFloorArea"],
            "subtreeFloor": imported_nodes[parent_id]["floorArea"], "freshChildMaximum": 12}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default="http://127.0.0.1:8423")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = check(args.base_url)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
