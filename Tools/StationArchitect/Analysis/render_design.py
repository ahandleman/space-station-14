"""Render saved Station Architect geometry for visual QA without a game or browser.

Requires Pillow. This reads actual masks and objects; it does not generate a map.
"""
import argparse
import json
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont


def render(project, output, scale=7):
    project = project.get("project", project)
    layout = project["layout"]
    cells = [*layout["envelope"], *layout["publicCorridors"], *layout["maintenance"],
             *(cell for room in layout["rooms"] for cell in room["cells"]),
             *(cell for dock in layout.get("docks", []) for key in ("boundaryCells", "berthCells", "approachCells") for cell in dock[key])]
    if not cells:
        raise ValueError("This design has no geometry to render.")
    min_x, min_y = min(cell["x"] for cell in cells)-5, min(cell["y"] for cell in cells)-8
    max_x, max_y = max(cell["x"] for cell in cells)+5, max(cell["y"] for cell in cells)+5
    image = Image.new("RGB", ((max_x-min_x+1)*scale, (max_y-min_y+1)*scale+40), "#0b141d")
    draw = ImageDraw.Draw(image)
    try:
        font = ImageFont.truetype("C:/Windows/Fonts/consola.ttf", max(8, scale+1))
        title_font = ImageFont.truetype("C:/Windows/Fonts/consolab.ttf", 16)
    except OSError:
        font = title_font = ImageFont.load_default()

    def point(x, y):
        return ((x-min_x)*scale, (max_y-y)*scale+32)

    def paint(items, color):
        for cell in items:
            x, y = point(cell["x"], cell["y"])
            draw.rectangle((x, y-scale+1, x+scale-1, y), fill=color)

    colors = {owner["id"]: owner["color"] for owner in project["definition"]["departments"]}
    colors["_maintenance"] = "#87946e"
    paint(layout["envelope"], "#172932")
    for dock in layout.get("docks", []):
        paint(dock["approachCells"], "#10242b")
        paint(dock["berthCells"], "#163a47")
        for key in ("berthCells", "approachCells"):
            points = dock[key]
            if not points:
                continue
            left, top = point(min(cell["x"] for cell in points), max(cell["y"] for cell in points)+1)
            right, bottom = point(max(cell["x"] for cell in points)+1, min(cell["y"] for cell in points))
            draw.rectangle((left, top, right, bottom), outline="#4c8193", width=1)
    for room in layout["rooms"]:
        paint(room["cells"], "#a26767" if room["orphaned"] else colors.get(room["ownerId"], "#87946e"))
    for key, color in [("publicCorridors", "#d2d9cc"), ("maintenance", "#7e8760"),
                       ("walls", "#33444d"), ("hull", "#5f7278"), ("windows", "#72b4cd")]:
        paint(layout[key], color)
    for interface in layout["interfaces"]:
        paint(interface["cells"], {"walking": "#83d9b6", "interaction": "#73bed6", "utility": "#e5ba6f"}.get(interface["kind"], "white"))
    for utility in layout["utilities"]:
        points = [point(cell["x"]+.5, cell["y"]+.5) for cell in utility["cells"]]
        if len(points) > 1:
            draw.line(points, fill="#e5ba6f" if utility["type"] == "pipe" else "#78aee1", width=1)
    for network in layout.get("transportNetworks", []):
        points = [point(cell["x"]+.5, cell["y"]+.5) for cell in network["cells"]]
        if len(points) > 1:
            draw.line(points, fill="#cda7df", width=1)
    for equipment in layout["equipment"]:
        paint(equipment["footprint"], "#d2b57b")
    for room in layout["rooms"]:
        if not room["cells"]:
            continue
        box = room["bounds"]
        label = room["definitionPath"].split("/")[-1]
        if room["instance"] != "1":
            label += " " + room["instance"]
        x, y = point(box["x"]+box["width"]/2, box["y"]+box["height"]/2)
        limit = max(2, int(box["width"]*scale/(scale*.6)))
        if len(label) > limit:
            label = label[:max(1, limit-1)]+"…"
        draw.text((x, y), label, font=font, fill="#e7f5ea", stroke_width=1, stroke_fill="#17252b", anchor="mm")
    for dock in layout.get("docks", []):
        points = dock["berthCells"]
        if not points or not dock["boundaryCells"]:
            continue
        center = lambda items: (sum(cell["x"]+.5 for cell in items)/len(items), sum(cell["y"]+.5 for cell in items)/len(items))
        start, end = point(*center(points)), point(*center(dock["approachCells"] or points))
        draw.line([start, end], fill="#75bbcf", width=1)
        draw.text(point(*center(points)), dock["requirementId"]+" / empty berth", font=font,
                  fill="#a7ddec", stroke_width=1, stroke_fill="#10242b", anchor="mm")
    report = project.get("report") or {}
    state = ("MANDATORY GEOMETRY PASSED" if report.get("complete") else project.get("stage", "draft").upper()+" STAGE PASSED") if report.get("success") else "DRAFT — REQUIREMENTS NEED REVIEW"
    draw.text((12, 9), f"{project['definition']['name']} · {project['settings']['topology']} · seed {project['settings']['seed']} · {state}", font=title_font, fill="#c7dfd0")
    output.parent.mkdir(parents=True, exist_ok=True)
    image.save(output)
    return image.size


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("project", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--scale", type=int, default=7)
    args = parser.parse_args()
    if not 2 <= args.scale <= 20:
        parser.error("Scale must be 2–20 pixels per tile.")
    size = render(json.loads(args.project.read_text(encoding="utf-8-sig")), args.output, args.scale)
    print(f"Rendered {args.output} ({size[0]}×{size[1]})")


if __name__ == "__main__":
    main()
