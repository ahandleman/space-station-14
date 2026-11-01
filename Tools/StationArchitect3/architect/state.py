"""Shared in-memory contracts; native export is a separate verification facet."""
from __future__ import annotations

from dataclasses import dataclass, field
import copy

Cell = tuple[int, int]


@dataclass
class InstanceProgram:
    # Space dictionaries: id, template, parent, owner (department instance), kind,
    # children (instance IDs), node (resolved authored node), requirements.
    spaces: dict[str, dict] = field(default_factory=dict)
    # Feature dictionaries: id, template, room, node, prototype, index.
    features: dict[str, dict] = field(default_factory=dict)
    # Bound rules: id, authoredId, declarationScope, owner (declaring instance),
    # type, priority, weight, tier, parameters, source, target (concrete endpoint).
    relationships: list[dict] = field(default_factory=list)
    definitions: dict[str, dict[str, dict]] = field(default_factory=dict)
    diagnostics: list[dict] = field(default_factory=list)
    source: dict = field(default_factory=dict)
    source_hash: str = ""

    def descendants(self, instance: str, physical_only: bool = False) -> list[str]:
        result = []
        pending = [instance]
        while pending:
            key = pending.pop()
            if key not in self.spaces:
                continue
            node = self.spaces[key]
            if not physical_only or node['kind'] in ('room', 'passage'):
                result.append(key)
            pending.extend(reversed(node['children']))
        return result

    def ancestors(self, instance: str) -> list[str]:
        if instance in self.features:
            instance = self.features[instance]['room']
        result = []
        while instance in self.spaces:
            result.append(instance)
            instance = self.spaces[instance]['parent']
        return result

    def common_ancestor(self, instances: list[str]) -> str | None:
        if not instances:
            return None
        chains = [self.ancestors(i) for i in instances]
        return next((i for i in chains[0] if all(i in c for c in chains[1:])), None)


@dataclass
class LayoutState:
    program: InstanceProgram
    floors: dict[str, set[Cell]] = field(default_factory=dict)
    # One full tile of structure between adjacent room floors; openings replace
    # structure. Each interface: id,a,b,profile,cells(set),normal(tuple).
    walls: set[Cell] = field(default_factory=set)
    interfaces: list[dict] = field(default_factory=list)
    # Feature poses: room,cells(set),clearance(set),rotation.
    features: dict[str, dict] = field(default_factory=dict)
    # Reservation: id,owner,kind,cells(set), plus evaluator-specific dimensions.
    reservations: list[dict] = field(default_factory=list)
    envelopes: dict[str, set[Cell]] = field(default_factory=dict)
    # Passage -> list of actual rectangular 2/3-wide strip cell sets.
    strips: dict[str, list[set[Cell]]] = field(default_factory=dict)
    bounds: tuple[int, int, int, int] = (0, 0, 0, 0)
    revision: int = 0
    trace: list[dict] = field(default_factory=list)
    summaries: dict[str, dict] = field(default_factory=dict)
    metadata: dict = field(default_factory=dict)

    def clone(self) -> 'LayoutState':
        return copy.deepcopy(self)

    def aggregate(self, instance: str) -> set[Cell]:
        cells = set()
        for key in self.program.descendants(instance, physical_only=True):
            cells.update(self.floors.get(key, set()))
        return cells


def json_ready(value):
    """Stable serialization, including sets used by exact geometry."""
    if isinstance(value, set):
        return [json_ready(v) for v in sorted(value)]
    if isinstance(value, tuple):
        return [json_ready(v) for v in value]
    if isinstance(value, list):
        return [json_ready(v) for v in value]
    if isinstance(value, dict):
        return {k: json_ready(v) for k, v in sorted(value.items())}
    return value
