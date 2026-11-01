"""Render planning masks for visual QA (Pillow required; no browser/ECS coverage)."""
import json
import sys
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont


def render(source):
    project = json.loads(source.read_text(encoding="utf-8"))
    config, plan = project["config"], project["spatialPlan"]
    scale, margin, heading = 10, 24, 64
    image = Image.new("RGB", (config["width"] * scale + margin * 2,
                              config["height"] * scale + heading + 64), "#111d27")
    draw = ImageDraw.Draw(image)
    font_path = Path("C:/Windows/Fonts/segoeui.ttf")
    font = ImageFont.truetype(str(font_path), 14) if font_path.exists() else ImageFont.load_default()
    small = ImageFont.truetype(str(font_path), 11) if font_path.exists() else font

    def layer(cells, color):
        for cell in cells:
            x, y = cell["x"] * scale + margin, cell["y"] * scale + heading
            draw.rectangle((x, y, x + scale - 1, y + scale - 1), fill=color)

    def label(cells, value, fill="#edf3e7", face=font):
        if not cells:
            return
        cx = sum(p["x"] for p in cells) / len(cells)
        cy = sum(p["y"] for p in cells) / len(cells)
        center = min(cells, key=lambda p: (p["x"] - cx) ** 2 + (p["y"] - cy) ** 2)
        width = (max(p["x"] for p in cells) - min(p["x"] for p in cells) + 1) * scale - 4
        if font_path.exists() and face.getlength(value) > width:
            face = ImageFont.truetype(str(font_path), max(6, int(face.size * width / face.getlength(value))))
        draw.text((margin + (center["x"] + .5) * scale, heading + (center["y"] + .5) * scale),
                  value, fill=fill, font=face, anchor="mm", stroke_width=2, stroke_fill="#172630")

    draw.text((margin, 14), f"SS14 STATION PROGRAM / {config['archetype'].upper()} / SEED {config['seed']}", font=font, fill="#d6e5e2")
    draw.text((margin, 36), "Planning reservations: departments, rooms, maintenance and external solar access", font=small, fill="#a5bcc0")
    layer(plan["envelope"], "#24343d")
    layer(plan["structuralReserve"], "#2c3c45")
    colors = {d["id"]: d["color"] for d in config["departments"]}
    for region in plan["regions"]:
        layer(region["cells"], colors[region["id"]])
    layer(plan["publicCorridors"], "#ced6cd")
    layer(plan["maintenance"], "#798064")
    layer(plan["walls"], "#536472")
    layer(plan["hull"], "#91a2af")
    for port in plan["ports"]:
        layer(port["cells"], "#e0bd75" if port["kind"] == "maintenance" else "#69e3cd")
    hierarchy = plan.get("hierarchy") or {}
    for sub in hierarchy.get("subdepartments", []):
        keys = {(p["x"], p["y"]) for p in sub["cells"]}
        for x, y in keys:
            px, py = margin + x * scale, heading + y * scale
            for dx, dy, a, b in ((0, -1, (px, py), (px + scale, py)), (1, 0, (px + scale, py), (px + scale, py + scale)),
                                (0, 1, (px, py + scale), (px + scale, py + scale)), (-1, 0, (px, py), (px, py + scale))):
                if (x + dx, y + dy) not in keys:
                    draw.line((a, b), fill="#dce8d7", width=1)
    for room in hierarchy.get("roomFeatures", []):
        layer(room["walls"], "#31434d")
        layer(room["doors"], "#f1d798")
        label(room["cells"], room["definition"].split("/")[-1], face=small)
    for sub in hierarchy.get("subdepartments", []):
        if not any(room["subdepartment"] == sub["id"] for room in hierarchy.get("roomFeatures", [])):
            label(sub["cells"], sub["id"].split("/")[-1], face=small)
    for room in plan.get("deepMaintenanceRooms", []):
        layer(room["cells"], "#a3a277")
        layer(room["walls"], "#586459")
        layer(room["portCells"], "#e0bd75")
        label(room["cells"], "DEEP", face=small)
    for solar in plan.get("solarInstallations", []):
        layer(solar["cells"], "#517faf")
        layer(solar["catwalk"], "#91a7ac")
        layer(solar["connectionCells"], "#caaf81")
        label(solar["cells"], solar["id"], face=small)
    for room in plan.get("commonMaintenanceRooms", []):
        layer(room["cells"], "#8e957d")
        layer(room["walls"], "#586459")
        layer(room["publicDoorCells"], "#69e3cd")
        layer(room["maintenanceDoorCells"], "#e0bd75")
        label(room["cells"], "MAINT", face=small)
    for access in plan.get("maintenanceSpaceAccess", []):
        layer(access["barrierCells"], "#7edce3" if access["barrier"] == "window" else "#cad2d1")
    for region in plan["regions"]:
        cells = sorted(region["cells"], key=lambda p: (p["y"], p["x"]))
        p = cells[len(cells) // 4]
        draw.text((margin + p["x"] * scale, heading + p["y"] * scale - 8), region["id"].upper(),
                  font=font, fill="#edf3e7", stroke_width=2, stroke_fill="#172630")
    metrics = project["result"]["metrics"]
    draw.text((margin, image.height - 46), f"{metrics['maintenanceArea']} maintenance tiles | {metrics['deepMaintenanceRooms']} deep rooms | "
              f"{metrics['solarInstallations']} solars | {metrics['plannedRoomFeatures']} room/features | "
              f"{metrics['touchingDepartmentPairs']} touching department pairs", font=small, fill="#c3d7d6")
    output = source.with_suffix(".png")
    image.save(output)
    print(output)


for argument in sys.argv[1:]:
    render(Path(argument))
