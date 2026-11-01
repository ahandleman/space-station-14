using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Content.StationArchitect;

internal static class DesignEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/design/default", () => Guard(() => DesignPipeline.New()));
        app.MapPost("/api/design/new", (NewDesignRequest request) => Guard(() => DesignPipeline.New(request.Definition)));
        app.MapPost("/api/design/import", (StationDesignProject project) => Guard(() => DesignPipeline.Import(project)));
        app.MapPost("/api/design/stage/{stage}", (string stage, StationDesignProject project, int? attempt) => Guard(() => DesignPipeline.RunStage(project, stage, attempt)));
        app.MapPost("/api/design/generate", (StationDesignProject project) => Guard(() => DesignPipeline.Generate(project)));
        app.MapPost("/api/design/definition", (DesignDefinitionRequest request) => Guard(() => DesignPipeline.UpdateDefinition(request.Project, request.Definition)));
        app.MapPost("/api/design/settings", (DesignSettingsRequest request) => Guard(() => DesignPipeline.UpdateSettings(request.Project, request.Settings)));
        app.MapPost("/api/design/edit", (DesignEditRequest request) => Guard(() =>
        {
            DesignPipeline.CheckStructure(request.Project);
            var project = ProjectEdits.Apply(request.Project, request.Edit);
            var editDiagnostics = project.Report?.Diagnostics ?? [];
            if (DesignStages.Index(request.Project.Stage) > DesignStages.Index(project.Stage)) project.Stage = request.Project.Stage;
            project.Report = DesignGeometryValidator.Validate(project, refreshReports: true);
            project.Report.Diagnostics = project.Report.Diagnostics.Concat(editDiagnostics).Distinct().ToList();
            project.Tree = ProjectTree.Build(project);
            return project;
        }));
        app.MapPost("/api/design/validate", (StationDesignProject project) => Guard(() => DesignPipeline.RunStage(project, "validation")));
        app.MapPost("/api/design/export/svg", (StationDesignProject project) => Guard(() =>
        {
            DesignPipeline.CheckStructure(project);
            return Results.Text(DesignSvgExporter.Export(project), "image/svg+xml");
        }));
        app.MapPost("/api/design/export/map", (StationDesignProject project) => Guard(() =>
        {
            DesignPipeline.CheckStructure(project);
            return Results.Text(DesignMapExporter.Export(project).MapYaml, "application/yaml");
        }));
        app.MapPost("/api/design/export/descriptor", (StationDesignProject project) => Guard(() =>
        {
            DesignPipeline.CheckStructure(project);
            return Results.Text(DesignMapExporter.Export(project).DescriptorYaml, "application/yaml");
        }));
        app.MapPost("/api/design/export/manifest", (StationDesignProject project) => Guard(() =>
        {
            DesignPipeline.CheckStructure(project);
            return Results.Text(DesignMapExporter.Export(project).ManifestJson, "application/json");
        }));
        app.MapPost("/api/design/export/bundle", (StationDesignProject project) => Guard(() =>
        {
            DesignPipeline.CheckStructure(project);
            return Results.File(Bundle(project), "application/zip", "station-architect-draft.zip");
        }));
    }

    public static byte[] Bundle(StationDesignProject project)
    {
        var exported = DesignMapExporter.Export(project);
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            void Add(string name, string content)
            {
                using var entry = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
                using var writer = new StreamWriter(entry, new UTF8Encoding(false));
                writer.Write(content);
            }
            var mapName = Path.GetFileName(exported.MapFileName.Replace('\\', '/'));
            var descriptorName = Path.GetFileName(exported.DescriptorFileName.Replace('\\', '/'));
            Add("Resources/Maps/Generated/" + mapName, exported.MapYaml);
            Add("Resources/Prototypes/Maps/Generated/" + descriptorName, exported.DescriptorYaml);
            Add("systems-manifest.json", exported.ManifestJson);
            Add("project.station.json", JsonSerializer.Serialize(project, ProgramJson.Options));
            Add("room-program.yaml", ProgramYaml.Export(project.Definition));
            Add("station-plan.svg", DesignSvgExporter.Export(project));
            Add("README.txt", "Station Architect map draft\n\nOpen station-plan.svg to review the geometry.\nproject.station.json restores the editable design and its requirements.\nResources contains the map and companion gameMap descriptor.\nReview systems-manifest.json before loading the map in SS14.\nGeneration and scalar validation do not certify an operational station.\n");
        }
        return stream.ToArray();
    }

    private static IResult Guard(Func<object> action)
    {
        try
        {
            var result = action();
            return result is IResult response ? response : Results.Json(result, ProgramJson.Options);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException or JsonException or OverflowException)
        { return Results.BadRequest(new { error = "Unable to apply this design operation.", detail = exception.Message }); }
    }
}

internal sealed class NewDesignRequest { public StationProgramDocument? Definition { get; set; } }
internal sealed class DesignEditRequest { public StationDesignProject Project { get; set; } = new(); public DesignEdit Edit { get; set; } = new(); }
internal sealed class DesignDefinitionRequest { public StationDesignProject Project { get; set; } = new(); public StationProgramDocument Definition { get; set; } = new(); }
internal sealed class DesignSettingsRequest { public StationDesignProject Project { get; set; } = new(); public DesignSettings Settings { get; set; } = new(); }
