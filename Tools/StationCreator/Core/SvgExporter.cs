using System.Globalization;
using System.Net;
using System.Text;

namespace Content.StationGeneration;

public static class SvgExporter
{
    public static string Export(StationProject project, bool graph = false)
    {
        if (project.SpatialPlan != null)
            return SpatialSvgExporter.Export(project);
        var c = project.Config;
        var l = project.Layout;
        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"-2 -7 {c.Width + 4} {c.Height + 10}\" role=\"img\" aria-label=\"Station floor plan seed {c.Seed}\"><rect x=\"-2\" y=\"-7\" width=\"{c.Width + 4}\" height=\"{c.Height + 10}\" fill=\"#101923\"/><style>text{{font-family:system-ui,sans-serif}}.label{{font-size:2.1px;fill:#f4f7fa;text-anchor:middle;paint-order:stroke;stroke:#17212d;stroke-width:.5px}}</style>");
        svg.Append($"<text x=\"1\" y=\"-3\" fill=\"#d9e5ed\" font-size=\"2.5\">STATION CREATOR / {E(c.Archetype.ToUpperInvariant())} / SEED {c.Seed} / {(project.Result?.Success == true ? "VALIDATED FLOOR PLAN" : "DRAFT")}</text>");
        if (l != null)
        {
            Layer(l.ReservedExterior, "#223a48", "exterior");
            Layer(l.Hull, "#8597a5", "hull");
            Layer(l.Maintenance, "#666d55", "maintenance");
            Layer(l.PublicCorridors, "#ced6cd", "public");
            foreach (var room in l.Departments)
            {
                var r = room.Bounds;
                var definition = c.Departments.FirstOrDefault(d => d.Id == room.Id);
                var color = definition?.Color ?? "#6e90b0";
                if (color.Length != 7 || color[0] != '#' || !color.Skip(1).All(Uri.IsHexDigit)) color = "#6e90b0";
                svg.Append($"<g data-department=\"{E(room.Id)}\"><rect x=\"{r.X - 1}\" y=\"{r.Y - 1}\" width=\"{r.Width + 2}\" height=\"{r.Height + 2}\" fill=\"#516171\"/><rect x=\"{r.X}\" y=\"{r.Y}\" width=\"{r.Width}\" height=\"{r.Height}\" fill=\"{color}\" fill-opacity=\".67\"/><text class=\"label\" x=\"{r.Center.X}\" y=\"{r.Center.Y}\">{E(room.Id)}</text><text class=\"label\" style=\"font-size:1.5px\" x=\"{r.Center.X}\" y=\"{r.Center.Y + 3}\">{r.Width} × {r.Height} / {E(definition?.Access ?? "")}</text></g>");
            }
            Layer(l.Doors.SelectMany(d => d.Cells).ToList(), "#69e3cd", "doors");
            if (graph && project.Topology != null)
            {
                svg.Append("<g opacity=\".55\" stroke-width=\".3\">");
                foreach (var edge in project.Topology.Edges)
                {
                    var a = l.Departments.FirstOrDefault(d => d.Id == edge.From)?.Bounds.Center;
                    var b = l.Departments.FirstOrDefault(d => d.Id == edge.To)?.Bounds.Center;
                    if (a != null && b != null)
                        svg.Append($"<path d=\"M{a.Value.X},{a.Value.Y} L{b.Value.X},{b.Value.Y}\" stroke=\"{(edge.Hard ? "#8fe7e0" : "#e0ad63")}\" stroke-dasharray=\"{(edge.Hard ? "0" : "1 1")}\"/>");
                }
                svg.Append("</g>");
            }
        }
        var legend = new[] { ("Public", "#ced6cd"), ("Maintenance", "#666d55"), ("Hull", "#8597a5"), ("Entrances", "#69e3cd"), ("Dock clearance", "#223a48") };
        for (var i = 0; i < legend.Length; i++)
        {
            var x = 1 + i * c.Width / 5;
            svg.Append($"<rect x=\"{x}\" y=\"{c.Height + 1}\" width=\"1\" height=\"1\" fill=\"{legend[i].Item2}\"/><text x=\"{x + 2}\" y=\"{c.Height + 2}\" fill=\"#aabbc7\" font-size=\"1.4\">{legend[i].Item1}</text>");
        }
        svg.Append("</svg>");
        return svg.ToString();

        void Layer(IEnumerable<Cell> cells, string color, string name)
        {
            svg.Append($"<g data-layer=\"{name}\" fill=\"{color}\">");
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

    private static string E(string text) => WebUtility.HtmlEncode(text);
}
