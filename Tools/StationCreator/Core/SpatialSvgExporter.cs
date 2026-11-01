using System.Net;
using System.Text;

namespace Content.StationGeneration;

public static class SpatialSvgExporter
{
    public static string Export(StationProject project)
    {
        var plan = project.SpatialPlan ?? throw new ArgumentException("A spatial plan is required.");
        var c = project.Config;
        var svg = new StringBuilder();
        string E(string value) => WebUtility.HtmlEncode(value);
        svg.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"-3 -9 {c.Width + 6} {c.Height + 16}\" role=\"img\" aria-label=\"Station regions seed {c.Seed}\"><rect x=\"-3\" y=\"-9\" width=\"{c.Width + 6}\" height=\"{c.Height + 16}\" fill=\"#111d27\"/><style>text{{font-family:system-ui,sans-serif}}.label{{fill:#edf3e7;font-size:2px;text-anchor:middle;paint-order:stroke;stroke:#15252d;stroke-width:.5px}}</style>");
        svg.Append($"<text x=\"1\" y=\"-4\" fill=\"#d6e5e2\" font-size=\"2.4\">STATION REGIONS / {E(c.Archetype.ToUpperInvariant())} / SEED {c.Seed} / {(project.Result?.Success == true ? "GEOMETRY CHECKED" : "SPATIAL DRAFT")}</text>");
        Layer(plan.Envelope, "#24343d", "envelope");
        Layer(plan.StructuralReserve, "#2c3c45", "structural-reserve");
        Layer(plan.PublicReservations, "#49676d", "public-reservation");
        Layer(plan.ServiceReservations, "#535e41", "service-reservation");
        Layer(plan.Walls, "#536472", "walls");
        foreach (var region in plan.Regions)
        {
            var definition = c.Departments.First(d => d.Id == region.Id);
            var color = definition.Color;
            if (color == null || color.Length != 7 || color[0] != '#' || !color.Skip(1).All(Uri.IsHexDigit)) color = "#6e90b0";
            Layer(region.Cells, color, region.Id);
        }
        Layer(plan.PublicCorridors, "#ced6cd", "public");
        Layer(plan.Maintenance, "#798064", "maintenance");
        Layer(plan.Hull, "#91a2af", "hull");
        foreach (var room in plan.DeepMaintenanceRooms)
        {
            Layer(room.Cells, "#a3a277", room.Id);
            Layer(room.Walls, "#586459", room.Id + "-walls");
            Layer(room.PortCells, "#e0bd75", room.Id + "-door");
            Label(room.Cells, "Deep maint", 1.1);
        }
        foreach (var solar in plan.SolarInstallations)
        {
            Layer(solar.Cells, "#517faf", solar.Id + "-panels");
            Layer(solar.Catwalk, "#91a7ac", solar.Id + "-catwalk");
            Layer(solar.ConnectionCells, "#caaf81", solar.Id + "-airlock");
            Label(solar.Cells, solar.Id, 1.3);
        }
        foreach (var room in plan.CommonMaintenanceRooms)
        {
            Layer(room.Cells, "#8e957d", room.Id);
            Layer(room.Walls, "#586459", room.Id + "-walls");
            Layer(room.PublicDoorCells, "#69e3cd", room.Id + "-hall-door");
            Layer(room.MaintenanceDoorCells, "#e0bd75", room.Id + "-service-door");
            Label(room.Cells, "Maint room", .9);
        }
        foreach (var access in plan.MaintenanceSpaceAccess)
            Layer(access.BarrierCells, access.Barrier == "window" ? "#7edce3" : "#cad2d1", access.Id + "-" + access.Barrier);
        if (plan.Hierarchy != null)
        {
            foreach (var subdepartment in plan.Hierarchy.Subdepartments)
            {
                var cells = subdepartment.Cells.ToHashSet();
                svg.Append($"<g data-layer=\"{E(subdepartment.Id)}\" fill=\"none\" stroke=\"#e6efdb\" stroke-opacity=\".6\" stroke-width=\".13\"><title>{E(subdepartment.Id)} / host {E(subdepartment.HostDepartment)}</title>");
                foreach (var cell in cells)
                {
                    if (!cells.Contains(cell + new Cell(0, -1))) svg.Append($"<path d=\"M{cell.X},{cell.Y}h1\"/>");
                    if (!cells.Contains(cell + new Cell(1, 0))) svg.Append($"<path d=\"M{cell.X + 1},{cell.Y}v1\"/>");
                    if (!cells.Contains(cell + new Cell(0, 1))) svg.Append($"<path d=\"M{cell.X},{cell.Y + 1}h1\"/>");
                    if (!cells.Contains(cell + new Cell(-1, 0))) svg.Append($"<path d=\"M{cell.X},{cell.Y}v1\"/>");
                }
                svg.Append("</g>");
                Label(subdepartment.Cells, subdepartment.Id.Split('/')[^1] + (subdepartment.IsSatellite ? " (satellite)" : ""), 1.1);
            }
            foreach (var room in plan.Hierarchy.RoomFeatures)
            {
                Layer(room.Walls, "#31434d", room.Id + "-partitions");
                Layer(room.Doors, "#f1d798", room.Id + "-doors");
                Label(room.Cells, room.Definition.Split('/')[^1], .85);
            }
        }
        foreach (var port in plan.Ports)
            Layer(port.Cells, port.Kind == "maintenance" ? "#e0bd75" : "#69e3cd", "port-" + port.Kind);
        foreach (var seed in plan.Seeds)
        {
            var region = plan.Regions.FirstOrDefault(r => r.Id == seed.Id);
            var center = seed.Position;
            if (region?.Cells.Count > 0)
            {
                var x = region.Cells.Average(p => p.X);
                var y = region.Cells.Average(p => p.Y);
                center = region.Cells.MinBy(p => (p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y));
            }
            else
                svg.Append($"<circle cx=\"{center.X}\" cy=\"{center.Y}\" r=\"1.7\" fill=\"#172a32\" stroke=\"#a2cbbc\" stroke-width=\".5\"/>");
            svg.Append($"<text class=\"label\" x=\"{center.X}\" y=\"{center.Y}\">{E(seed.Id)}</text><text class=\"label\" style=\"font-size:1.4px\" x=\"{center.X}\" y=\"{center.Y + 3}\">{(region == null ? $"{seed.TargetArea} target" : $"{region.Cells.Count} floor tiles")}</text>");
        }
        var legend = new[] { ("Public halls", "#ced6cd"), ("Maintenance", "#798064"), ("Hull", "#91a2af"), ("Public ports", "#69e3cd"), ("Service ports", "#e0bd75") };
        for (var i = 0; i < legend.Length; i++)
        {
            var x = 1 + i * c.Width / 5;
            svg.Append($"<rect x=\"{x}\" y=\"{c.Height + 2}\" width=\"1\" height=\"1\" fill=\"{legend[i].Item2}\"/><text x=\"{x + 2}\" y=\"{c.Height + 3}\" fill=\"#aabdc5\" font-size=\"1.4\">{legend[i].Item1}</text>");
        }
        svg.Append("</svg>");
        return svg.ToString();

        void Label(List<Cell> cells, string value, double size)
        {
            if (cells.Count == 0) return;
            var x = cells.Average(p => p.X); var y = cells.Average(p => p.Y);
            var center = cells.MinBy(p => (p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y));
            var availableWidth = cells.Max(p => p.X) - cells.Min(p => p.X) + 1;
            size = Math.Min(size, Math.Max(.45, availableWidth * 1.5 / Math.Max(1, value.Length)));
            svg.Append(FormattableString.Invariant($"<text class=\"label\" style=\"font-size:{size}px\" x=\"{center.X + .5}\" y=\"{center.Y + .5}\">{E(value)}</text>"));
        }

        void Layer(IEnumerable<Cell> cells, string color, string name)
        {
            svg.Append($"<g data-layer=\"{E(name)}\" fill=\"{color}\">");
            foreach (var row in cells.Distinct().GroupBy(p => p.Y).OrderBy(g => g.Key))
            {
                var xs = row.Select(p => p.X).Order().ToArray();
                for (var i = 0; i < xs.Length; i++)
                {
                    var first = xs[i]; var last = first;
                    while (i + 1 < xs.Length && xs[i + 1] == last + 1) last = xs[++i];
                    svg.Append($"<rect x=\"{first}\" y=\"{row.Key}\" width=\"{last - first + 1}\" height=\"1\"/>");
                }
            }
            svg.Append("</g>");
        }
    }
}
