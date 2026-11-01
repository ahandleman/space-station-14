using System.Globalization;
using System.Net;
using System.Text;

namespace Content.StationArchitect;

/// <summary>A standalone review artifact; all geometry comes from the saved design masks.</summary>
public static class DesignSvgExporter
{
    public static string Export(StationDesignProject project)
    {
        var layout = project.Layout;
        var all = layout.Envelope.Concat(layout.Rooms.SelectMany(room => room.Cells)).Concat(layout.PublicCorridors).Concat(layout.Maintenance).Concat(layout.Equipment.Select(entity => entity.Position))
            .Concat(layout.Docks.SelectMany(dock => dock.BoundaryCells.Concat(dock.BerthCells).Concat(dock.ApproachCells)))
            .Concat(layout.CirculationReservations.SelectMany(reservation => reservation.ApproachCells))
            .Concat(layout.SystemNetworks.SelectMany(network => network.Segments.Select(segment => segment.Position))).ToList();
        var bounds = all.Count == 0 ? new GridBounds(0, 0, project.Settings.Width, project.Settings.Height)
            : new GridBounds(all.Min(cell => cell.X) - 5, all.Min(cell => cell.Y) - 7, all.Max(cell => cell.X) - all.Min(cell => cell.X) + 11, all.Max(cell => cell.Y) - all.Min(cell => cell.Y) + 13);
        var output = new StringBuilder();
        string E(string? text) => WebUtility.HtmlEncode(text ?? "");
        output.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{bounds.Width * 8}\" height=\"{bounds.Height * 8}\" viewBox=\"{bounds.X} {bounds.Y} {bounds.Width} {bounds.Height}\">");
        output.Append("<title>").Append(E(project.Definition.Name)).Append(" — editable station draft</title><rect x=\"").Append(bounds.X).Append("\" y=\"").Append(bounds.Y).Append("\" width=\"").Append(bounds.Width).Append("\" height=\"").Append(bounds.Height).Append("\" fill=\"#0b141d\"/>");
        void Cells(IEnumerable<Cell> cells, string color, string label)
        {
            output.Append("<g fill=\"").Append(color).Append("\"><title>").Append(E(label)).Append("</title>");
            foreach (var row in cells.Distinct().GroupBy(cell => cell.Y).OrderBy(row => row.Key))
            {
                var xs = row.Select(cell => cell.X).Order().ToList();
                for (var index = 0; index < xs.Count; index++)
                {
                    var first = xs[index]; var last = first;
                    while (index + 1 < xs.Count && xs[index + 1] == last + 1) last = xs[++index];
                    output.Append(CultureInfo.InvariantCulture, $"<rect x=\"{first}\" y=\"{row.Key}\" width=\"{last - first + 1}\" height=\"1\"/>");
                }
            }
            output.Append("</g>");
        }
        var colors = project.Definition.Departments.ToDictionary(owner => owner.Id, owner => owner.Color, StringComparer.Ordinal);
        colors[RequirementCompiler.MaintenanceOwner] = "#87946e";
        Cells(layout.Envelope, "#172932", "Planning envelope; unbuilt cells are not maintenance floor");
        foreach (var dock in layout.Docks.OrderBy(dock => dock.Id, StringComparer.Ordinal))
        {
            output.Append("<g fill-opacity=\".12\" stroke=\"#8fc5ec\" stroke-opacity=\".65\" stroke-width=\".07\" stroke-dasharray=\".4 .3\">");
            Cells(dock.BerthCells, "#8fc5ec", dock.Id + ": reserved empty shuttle berth");
            Cells(dock.ApproachCells, "#b5dfc2", dock.Id + ": reserved empty exterior approach");
            output.Append("</g>");
        }
        foreach (var room in layout.Rooms.OrderBy(room => room.Id, StringComparer.Ordinal))
        {
            var color = colors.GetValueOrDefault(room.OwnerId, "#a26767");
            if (!System.Text.RegularExpressions.Regex.IsMatch(color, "^#[a-fA-F0-9]{6}$")) color = "#87946e";
            Cells(room.Cells, color, room.Id);
        }
        Cells(layout.PublicCorridors, "#d2d9cc", "Public halls"); Cells(layout.Maintenance, "#7e8760", "Maintenance passages");
        Cells(layout.Walls, "#33444d", "Walls"); Cells(layout.Hull, "#5f7278", "Hull"); Cells(layout.Windows, "#72b4cd", "Windows");
        foreach (var port in layout.Interfaces) Cells(port.Cells, port.Kind == "walking" ? "#83d9b6" : port.Kind == "interaction" ? "#73bed6" : "#e6bc6b", port.Type + " " + port.From + " → " + port.To);
        foreach (var dock in layout.Docks.Where(dock => dock.BoundaryCells.Count > 0))
        {
            var cell = dock.BoundaryCells[0];
            output.Append(CultureInfo.InvariantCulture, $"<path fill=\"none\" stroke=\"#a9ddff\" stroke-width=\".2\" d=\"M{cell.X + .5} {cell.Y + .5} l{dock.Outward.X * 2} {dock.Outward.Y * 2}\"><title>{E(dock.Id + ": docking face and outward approach")}</title></path>");
        }
        foreach (var utility in layout.Utilities)
        {
            if (utility.Cells.Count == 0 || project.Definition.Systems.Enabled && project.Definition.Systems.Networks.Any(network => network.Kind == (utility.Type == "pipe" ? "gas" : "power"))) continue;
            output.Append("<path fill=\"none\" stroke=\"").Append(utility.Type == "pipe" ? "#e5ba6f" : "#78aee1").Append("\" stroke-width=\".17\" d=\"");
            for (var index = 0; index < utility.Cells.Count; index++) output.Append(CultureInfo.InvariantCulture, $"{(index == 0 ? 'M' : 'L')}{utility.Cells[index].X + .5} {utility.Cells[index].Y + .5}");
            output.Append("\"><title>").Append(E(utility.Id)).Append("</title></path>");
        }
        var nativeProfiles = project.Definition.Systems.Profiles.GroupBy(profile => profile.Id).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var network in layout.SystemNetworks.OrderBy(network => network.Id, StringComparer.Ordinal))
        foreach (var segment in network.Segments.OrderBy(segment => segment.Id, StringComparer.Ordinal))
        {
            if (!nativeProfiles.TryGetValue(segment.ProfileId, out var native)) continue;
            var port = native.Ports.FirstOrDefault();
            var color = native.Kind == "pipe" ? port?.PipeLayer == 1 ? "#ad81de" : "#5fc1d5" : native.Kind == "disposal" ? "#aa927c" : port?.NodeGroup == "HVPower" ? "#e7a44f" : port?.NodeGroup == "MVPower" ? "#59afda" : "#dc6b73";
            output.Append(CultureInfo.InvariantCulture, $"<g fill=\"{color}\" stroke=\"{color}\" stroke-width=\".12\"><title>{E(network.Id + ": " + segment.Id + " " + native.Prototype)}</title><circle cx=\"{segment.Position.X + .5}\" cy=\"{segment.Position.Y + .5}\" r=\".12\"/>");
            foreach (var geometry in DesignSystems.Ports(native, segment.Position, segment.Rotation, segment.Id))
            foreach (var direction in geometry.Directions)
                output.Append(CultureInfo.InvariantCulture, $"<path fill=\"none\" d=\"M{geometry.Position.X + .5} {geometry.Position.Y + .5} l{direction.X * .48} {direction.Y * .48}\"/>");
            output.Append("</g>");
        }
        foreach (var entity in layout.Equipment) Cells(entity.Footprint.Count > 0 ? entity.Footprint : [entity.Position], "#d2b57b", entity.Prototype + " " + entity.RoomId);
        output.Append("<g fill=\"#e3ede3\" font-family=\"sans-serif\" text-anchor=\"middle\" paint-order=\"stroke\" stroke=\"#122027\" stroke-width=\".25\">");
        foreach (var room in layout.Rooms.Where(room => room.Cells.Count > 0))
            output.Append(CultureInfo.InvariantCulture, $"<text x=\"{room.Bounds.X + room.Bounds.Width / 2.0}\" y=\"{room.Bounds.Y + room.Bounds.Height / 2.0}\" font-size=\"1.1\">{E(room.DefinitionPath.Split('/').Last() + (room.Instance == "1" ? "" : " " + room.Instance))}</text>");
        output.Append("</g></svg>");
        return output.ToString();
    }
}
