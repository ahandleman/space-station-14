"""Exact, deterministic integer-tile geometry used by generation and verification.

Bounding boxes are half open. Interfaces occupy real tiles between floor cells;
their cardinal normal points from endpoint a to endpoint b.
"""
from __future__ import annotations

from collections import deque

DIRECTIONS = ((1, 0), (0, 1), (-1, 0), (0, -1))


def rect(x, y, width, height):
    return {(xx, yy) for xx in range(x, x + width) for yy in range(y, y + height)}


def neighbors(cell):
    x, y = cell
    return tuple((x + dx, y + dy) for dx, dy in DIRECTIONS)


def bbox(cells):
    if not cells:
        return (0, 0, 0, 0)
    return (min(x for x, y in cells), min(y for x, y in cells),
            max(x for x, y in cells) + 1, max(y for x, y in cells) + 1)


def components(cells):
    remaining = set(cells)
    result = []
    while remaining:
        seed = min(remaining)
        remaining.remove(seed)
        component, pending = {seed}, [seed]
        while pending:
            for n in neighbors(pending.pop()):
                if n in remaining:
                    remaining.remove(n)
                    component.add(n)
                    pending.append(n)
        result.append(component)
    return result


def connected(cells):
    return bool(cells) and len(components(cells)) == 1


def boundary(cells):
    return {c for c in cells if any(n not in cells for n in neighbors(c))}


def outline(cells):
    return {n for c in cells for n in neighbors(c) if n not in cells}


def square_anchors(cells, width):
    """Lower-left anchors of all clear width-by-width footprints."""
    if width < 1:
        return set()
    offsets = [(dx, dy) for dx in range(width) for dy in range(width)]
    return {(x, y) for x, y in cells
            if all((x + dx, y + dy) in cells for dx, dy in offsets)}


def covered_by_squares(cells, width):
    return {p for x, y in square_anchors(cells, width) for p in rect(x, y, width, width)}


def shortest_path(cells, starts, goals):
    starts = sorted(set(starts) & cells)
    goals = set(goals) & cells
    queue = deque(starts)
    previous = {p: None for p in starts}
    while queue:
        p = queue.popleft()
        if p in goals:
            path = []
            while p is not None:
                path.append(p)
                p = previous[p]
            return list(reversed(path))
        for n in neighbors(p):
            if n in cells and n not in previous:
                previous[n] = p
                queue.append(n)
    return None


def seam_runs(a, b):
    """Possible straight one-tile seams with opposite floor approaches."""
    result = []
    for nx, ny in DIRECTIONS:
        seam = {(x + nx, y + ny) for x, y in a if (x + 2 * nx, y + 2 * ny) in b}
        # Normal coordinate is constant; tangent-neighbor components are runs.
        lines = {}
        for x, y in seam:
            lines.setdefault(x * nx + y * ny, set()).add((x, y))
        for key in sorted(lines):
            for cells in components(lines[key]):
                result.append(((nx, ny), cells))
    return sorted(result, key=lambda item: (-len(item[1]), item[0], min(item[1])))


def open_edge_runs(a, b):
    """Straight abutting floor edges; cells are on endpoint a, normal points to b.

    No structural tile or invented threshold lies between these floor owners.
    """
    result = []
    for nx, ny in DIRECTIONS:
        seam = {(x, y) for x, y in a if (x + nx, y + ny) in b}
        lines = {}
        for x, y in seam:
            lines.setdefault(x * nx + y * ny, set()).add((x, y))
        for key in sorted(lines):
            result.extend(((nx, ny), cells) for cells in components(lines[key]))
    return sorted(result, key=lambda item: (-len(item[1]), item[0], min(item[1])))


def docking_cells(interface, parameters):
    """Required empty berth plus approach, measured outward from a dock seam.

    Clearance pads each rectangle laterally and in depth, never occupying the
    dock's own structural row. The discrete seam center is stable for even runs.
    """
    cells = set(interface.get('cells', ()))
    normal = tuple(interface.get('normal', (0, 0)))
    if not cells or normal not in DIRECTIONS:
        return set()
    nx, ny = normal
    tx, ty = -ny, nx
    ordered = sorted(cells, key=lambda p: p[0] * tx + p[1] * ty)
    cx, cy = ordered[(len(ordered) - 1) // 2]
    offset = parameters.get('portOffset', 0)
    pad = parameters.get('clearance', 0)
    berth_width = parameters['berthWidth']
    berth_depth = parameters['berthDepth']
    approach_width = parameters['approachWidth']
    approach_length = parameters['approachLength']
    result = set()
    for width, start, depth in ((berth_width, 1, berth_depth),
                                (approach_width, berth_depth + 1, approach_length)):
        left = offset - (width - 1) // 2
        for lateral in range(left - pad, left + width + pad):
            for d in range(max(1, start - pad), start + depth + pad):
                result.add((cx + nx * d + tx * lateral, cy + ny * d + ty * lateral))
    return result


def docking_exterior_lane(interface, parameters, obstacles):
    """Continue the padded approach straight beyond every realized obstruction.

    Exterior flood-fill alone would incorrectly accept a berth in a courtyard
    connected to space by a one-tile gap. This checks its actual shuttle-width lane.
    """
    normal = tuple(interface.get('normal', (0, 0)))
    if normal not in DIRECTIONS or not interface.get('cells'):
        return set()
    nx, ny = normal
    tx, ty = -ny, nx
    ordered = sorted(interface['cells'], key=lambda p: p[0] * tx + p[1] * ty)
    cx, cy = ordered[(len(ordered) - 1) // 2]
    pad = parameters.get('clearance', 0)
    width = parameters['approachWidth']
    left = parameters.get('portOffset', 0) - (width - 1) // 2
    start = max(1, parameters['berthDepth'] + 1 - pad)
    end = max((int((x - cx) * nx + (y - cy) * ny) + 2 for x, y in obstacles), default=start)
    end = max(end, start + parameters['approachLength'] + 2 * pad)
    return {(cx + nx * d + tx * t, cy + ny * d + ty * t)
            for d in range(start, end + 1) for t in range(left - pad, left + width + pad)}


def flood_exterior(obstacles, bounds=None, maximum_cells=2000000):
    """Exterior-connected empty cells on a padded finite canvas, or None if huge."""
    x0, y0, x1, y1 = bbox(obstacles)
    if bounds and bounds[2] > bounds[0] and bounds[3] > bounds[1]:
        x0, y0 = min(x0, bounds[0]), min(y0, bounds[1])
        x1, y1 = max(x1, bounds[2]), max(y1, bounds[3])
    x0, y0, x1, y1 = x0 - 2, y0 - 2, x1 + 2, y1 + 2
    if (x1 - x0) * (y1 - y0) > maximum_cells:
        return None
    seen = {(x0, y0)}
    queue = deque(seen)
    while queue:
        for x, y in neighbors(queue.popleft()):
            if x0 <= x < x1 and y0 <= y < y1 and (x, y) not in obstacles and (x, y) not in seen:
                seen.add((x, y))
                queue.append((x, y))
    return seen


def convex_hull_area(cells):
    points = sorted({(x + dx, y + dy) for x, y in cells for dx, dy in ((0, 0), (0, 1), (1, 0), (1, 1))})
    if len(points) < 3:
        return 0.0
    def cross(o, a, b):
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])
    lower = []
    for p in points:
        while len(lower) >= 2 and cross(lower[-2], lower[-1], p) <= 0:
            lower.pop()
        lower.append(p)
    upper = []
    for p in reversed(points):
        while len(upper) >= 2 and cross(upper[-2], upper[-1], p) <= 0:
            upper.pop()
        upper.append(p)
    hull = lower[:-1] + upper[:-1]
    return abs(sum(x * hull[(i + 1) % len(hull)][1] - y * hull[(i + 1) % len(hull)][0]
                   for i, (x, y) in enumerate(hull))) / 2
