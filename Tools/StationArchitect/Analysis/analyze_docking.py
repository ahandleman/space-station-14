"""Read native shuttle envelopes and station docking hardware without running ECS.

Uses complete non-space tile bounds plus resolved entity fixture AABBs. The result
is a conservative rectangular berth profile, not a maneuvering simulation.
"""
import argparse
import base64
import hashlib
import json
import math
from pathlib import Path
import struct

from analyze_maps import load, map_entities, prototype_index


def angle(value):
    value = str(value or "0").split()
    return float(value[0]) * (math.pi / 180 if len(value) > 1 and value[1] == "deg" else 1)


def pair(value):
    return tuple(map(float, str(value).split(",")))


def inspect_map(path, prototypes):
    data = load(path); entities = map_entities(data, prototypes); grids = set(data["grids"]); poses = {}; active = set()

    def pose(uid):
        if uid in poses:
            return poses[uid]
        if uid in grids:
            return (uid, 0., 0., 0.)
        if uid not in entities or uid in active:
            return None
        active.add(uid); transform = entities[uid]["components"].get("Transform", {}); parent = pose(transform.get("parent"))
        active.remove(uid)
        if parent is None:
            poses[uid] = None
        else:
            x, y = pair(transform.get("pos", "0,0")); c, s = math.cos(parent[3]), math.sin(parent[3])
            poses[uid] = (parent[0], parent[1] + c*x-s*y, parent[2] + s*x+c*y, parent[3] + angle(transform.get("rot")))
        return poses[uid]

    result = []
    for grid in sorted(grids):
        points, tile_cells, fixture_count, docking = [], [], 0, []
        for chunk in entities[grid]["components"]["MapGrid"]["chunks"].values():
            cx, cy = map(int, str(chunk["ind"]).split(",")); size = chunk.get("size", 16)
            for index, (tile, *_rest) in enumerate(struct.iter_unpack("<iBBB", base64.b64decode(chunk["tiles"]))):
                if data["tilemap"][tile] != "Space":
                    x, y = cx*size+index % size, cy*size+index//size
                    tile_cells.append((x, y)); points.extend([(x, y), (x+1, y+1)])
        for entity in entities.values():
            located = pose(entity["uid"])
            if located is None or located[0] != grid or entity["uid"] == grid:
                continue
            _grid, x, y, rotation = located; c, s = math.cos(rotation), math.sin(rotation)
            for fixture in entity["components"].get("Fixtures", {}).get("fixtures", {}).values():
                shape = fixture.get("shape", {}); fixture_count += 1
                if "bounds" in shape:
                    x0, y0, x1, y1 = map(float, shape["bounds"].split(",")); local = [(x0,y0), (x0,y1), (x1,y0), (x1,y1)]
                elif "vertices" in shape:
                    local = [pair(vertex) for vertex in shape["vertices"]]
                elif "radius" in shape:
                    radius = float(shape["radius"]); center = pair(shape.get("position", "0,0"))
                    local = [(center[0]+dx*radius, center[1]+dy*radius) for dx,dy in [(-1,-1),(-1,1),(1,-1),(1,1)]]
                elif not shape:
                    local = [(-.5,-.5), (-.5,.5), (.5,-.5), (.5,.5)]  # default unit AABB
                else:
                    raise ValueError(f"Unrecognized collision shape on {path} uid{entity['uid']}: {shape}")
                points.extend((x+c*dx-s*dy, y+s*dx+c*dy) for dx,dy in local)
            if "Docking" in entity["components"]:
                docking.append({"uid": entity["uid"], "prototype": entity["prototype"], "position": [x,y], "rotationDegrees": round(rotation*180/math.pi, 8),
                                "priorityDock": entity["components"].get("PriorityDock"), "docking": entity["components"]["Docking"], "hasDoor": "Door" in entity["components"]})
        if not points:
            continue
        bounds = [math.floor(min(x for x,y in points)+1e-7), math.floor(min(y for x,y in points)+1e-7),
                  math.ceil(max(x for x,y in points)-1e-7), math.ceil(max(y for x,y in points)-1e-7)]
        result.append({"gridUid": grid, "nonSpaceTiles": len(tile_cells), "resolvedFixtures": fixture_count,
                       "envelopeMin": bounds[:2], "envelopeMaxExclusive": bounds[2:], "docks": docking})
    return {"source": path.as_posix(), "sha256": hashlib.sha256(path.read_bytes()).hexdigest(), "grids": result}


def analyze(repo):
    prototypes = prototype_index(repo); shuttles = []
    choices = [("arrivals",179), ("emergency",6), ("cargo",50)]
    for name, uid in choices:
        path = repo / "Resources/Maps/Shuttles" / (name + ".yml"); item = inspect_map(path, prototypes); grid = next(grid for grid in item["grids"] if any(dock["uid"] == uid for dock in grid["docks"]))
        dock = next(dock for dock in grid["docks"] if dock["uid"] == uid); x0,y0 = grid["envelopeMin"]; x1,y1 = grid["envelopeMaxExclusive"]
        # These explicitly selected source ports face west. The station faces east;
        # tangent is north, and the adjacent shuttle airlock starts at normal tile1.
        assert round(dock["rotationDegrees"]) == -90 and math.floor(dock["position"][0]) == x0
        width, depth = y1-y0, x1-x0; minimum_tangent = y0-math.floor(dock["position"][1])
        item["recommendedProfile"] = {"dockUid": uid, "berthWidth": width, "berthDepth": depth, "berthOffset": minimum_tangent + width//2,
                                       "clearance": 1, "approachLength": 16, "approachWidth": 0}
        shuttles.append(item)
    stations = [inspect_map(repo / "Resources/Maps" / (name + ".yml"), prototypes) for name in ("saltern","bagel","box","packed")]
    for station in stations:
        for grid in station["grids"]:
            grid.pop("envelopeMin", None); grid.pop("envelopeMaxExclusive", None)
            grid["docks"] = [dock for dock in grid["docks"] if dock["priorityDock"] or dock["docking"].get("dockLegendCategory") == "Cargo"]
    for item in shuttles + stations:
        item["source"] = Path(item["source"]).relative_to(repo).as_posix()
    return {"format":"StationArchitectDockEvidence", "version":1, "simulationVerified":False,
            "frame":"Outward N; tangent=(-N.y,N.x). berthOffset shifts the centered tangent span; clearance pads only lateral/far exterior sides.",
            "assumptions":["Complete non-Space tile rectangle includes hull/plating/lattice; all resolved fixture bounds are also included.",
                           "AABB, circle, polygon and default unit fixture shapes are conservatively enclosed after entity transforms.",
                           "The selected native docking port determines asymmetric lateral alignment. Other ports may need different offsets.",
                           "One-tile adjacent port alignment follows DockingSystem.Shuttle.cs; sprite clamp extension is not a collision fixture.",
                           "Profiles reserve empty space and a straight exit. FTL docking choice, thrust and maneuvering still need in-engine verification."],
            "shuttles":shuttles, "stations":stations,
            "engineReferences":["Content.Server/Shuttles/Systems/DockingSystem.Shuttle.cs", "Content.Shared/Shuttles/Systems/SharedDockingSystem.cs", "Resources/Prototypes/Entities/Structures/Doors/Airlocks/shuttle.yml"]}


def main():
    parser=argparse.ArgumentParser(description=__doc__); parser.add_argument("--repo",type=Path,default=Path(__file__).resolve().parents[3]); parser.add_argument("--output",type=Path)
    args=parser.parse_args(); result=analyze(args.repo.resolve())
    if args.output:
        args.output.parent.mkdir(parents=True,exist_ok=True); args.output.write_text(json.dumps(result,indent=2)+"\n",encoding="utf-8")
    print(json.dumps({"profiles":[item["recommendedProfile"] for item in result["shuttles"]],"stationDockRecords":sum(len(grid["docks"]) for station in result["stations"] for grid in station["grids"]),"simulationVerified":False},indent=2))


if __name__ == "__main__": main()
