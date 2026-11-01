using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Content.StationArchitect;

var arguments = args.ToList();
string? Option(string key) => arguments.IndexOf(key) is var index && index >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : null;
if (arguments.Contains("--help"))
{
    Console.WriteLine("Station Architect: [--open] [--port 8421] | --validate FILE [--tree FILE] | --write-default FILE | --build DIRECTORY [--program FILE | --project FILE] [--seed N] [--topology ring|hub|spine]");
    return 0;
}
if (Option("--write-default") is { } defaultFile)
{
    var document = StationProgramDefaults.Create();
    var compiled = RequirementCompiler.Compile(document);
    if (!compiled.Success) throw new InvalidDataException(JsonSerializer.Serialize(compiled.Diagnostics, ProgramJson.Options));
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(defaultFile))!);
    File.WriteAllText(defaultFile, ProgramYaml.Export(document), new UTF8Encoding(false));
    Console.WriteLine(Path.GetFullPath(defaultFile));
    return 0;
}
if (Option("--validate") is { } programFile)
{
    var imported = ProgramYaml.Import(File.ReadAllText(programFile));
    if (!imported.Success)
    {
        Console.WriteLine(JsonSerializer.Serialize(imported, ProgramJson.Options));
        return 1;
    }
    var compiled = RequirementCompiler.Compile(imported.Document!);
    var output = JsonSerializer.Serialize(compiled, ProgramJson.Options);
    if (Option("--tree") is { } treeFile) File.WriteAllText(treeFile, output, new UTF8Encoding(false));
    Console.WriteLine(output);
    return compiled.Success ? 0 : 1;
}
if (Option("--build") is { } buildDirectory)
{
    StationDesignProject input;
    if (Option("--project") is { } savedDesign)
    {
        using var saved = JsonDocument.Parse(File.ReadAllText(savedDesign));
        var root = saved.RootElement.TryGetProperty("project", out var wrapped) ? wrapped : saved.RootElement;
        input = DesignPipeline.Import(root.Deserialize<StationDesignProject>(ProgramJson.Options) ?? throw new InvalidDataException("The design file is empty."));
    }
    else input = DesignPipeline.New(Option("--program") is { } sourceProgram ? ProgramYaml.Parse(File.ReadAllText(sourceProgram)) : null);
    if (Option("--seed") is { } designSeed) input.Settings.Seed = int.Parse(designSeed);
    if (Option("--topology") is { } designTopology) input.Settings.Topology = designTopology;
    var project = DesignPipeline.Generate(input);
    Directory.CreateDirectory(buildDirectory);
    var stem = Path.Combine(buildDirectory, "station-" + project.Settings.Seed);
    var exported = DesignMapExporter.Export(project);
    File.WriteAllText(stem + ".station.json", JsonSerializer.Serialize(project, ProgramJson.Options), new UTF8Encoding(false));
    File.WriteAllText(stem + ".svg", DesignSvgExporter.Export(project), new UTF8Encoding(false));
    File.WriteAllText(stem + ".program.yaml", ProgramYaml.Export(project.Definition), new UTF8Encoding(false));
    var mapDirectory = Path.Combine(buildDirectory, "Resources", "Maps", "Generated");
    var descriptorDirectory = Path.Combine(buildDirectory, "Resources", "Prototypes", "Maps", "Generated");
    Directory.CreateDirectory(mapDirectory);
    Directory.CreateDirectory(descriptorDirectory);
    File.WriteAllText(Path.Combine(mapDirectory, exported.MapFileName), exported.MapYaml, new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(descriptorDirectory, exported.DescriptorFileName), exported.DescriptorYaml, new UTF8Encoding(false));
    File.WriteAllText(stem + ".manifest.json", exported.ManifestJson, new UTF8Encoding(false));
    File.WriteAllBytes(stem + ".zip", DesignEndpoints.Bundle(project));
    Console.WriteLine(JsonSerializer.Serialize(new { project.Report!.Success, project.Report.Complete, project.Report.Hash, project.Report.Metrics, errors = project.Report.Diagnostics.Where(item => item.Severity == "error"), directory = Path.GetFullPath(buildDirectory) }, ProgramJson.Options));
    return project.Report.Success ? 0 : 1;
}

var port = int.TryParse(Option("--port"), out var suppliedPort) ? suppliedPort : 8421;
if (port is < 1024 or > 65535) throw new ArgumentException("Port must be between 1024 and 65535.");
var webRoot = new[] { Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"), Path.Combine(AppContext.BaseDirectory, "wwwroot") }
    .FirstOrDefault(path => File.Exists(Path.Combine(path, "index.html")))
    ?? throw new DirectoryNotFoundException("Station Architect wwwroot is missing. Rebuild the tool.");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory, WebRootPath = webRoot });
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32 * 1024 * 1024);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
});
var app = builder.Build();
app.Use(async (context, next) =>
{
    var origin = context.Request.Headers.Origin.ToString();
    if (origin.Length > 0 && origin != $"http://127.0.0.1:{port}")
    {
        context.Response.StatusCode = 403;
        return;
    }
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    await next(context);
});
var defaultFiles = new DefaultFilesOptions();
defaultFiles.DefaultFileNames.Clear();
defaultFiles.DefaultFileNames.Add("designer.html");
app.UseDefaultFiles(defaultFiles);
app.UseStaticFiles();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", tool = "StationArchitect", schemaVersion = 2 }));
app.MapGet("/api/default", () => Results.Json(StationProgramDefaults.Create(), ProgramJson.Options));
app.MapGet("/api/templates", () => Results.Json(new { department = new DepartmentProgram(), room = new RoomDefinition(), connection = new ConnectionRequirement(), content = new RoomContentRequirement(), choice = new RoomChoiceRequirement(), dock = new DockRequirement() }, ProgramJson.Options));
app.MapPost("/api/compile", (StationProgramDocument document) => Guard(() => RequirementCompiler.Compile(document)));
app.MapPost("/api/draft/import", (StationProgramDocument document) => Guard(() =>
{
    CheckEditorStructure(document);
    return new { document, report = RequirementCompiler.Compile(document) };
}));
app.MapPost("/api/yaml/export", (StationProgramDocument document) => Guard(() => Results.Text(ProgramYaml.Export(document), "application/yaml")));
app.MapPost("/api/yaml/import", async (HttpRequest request) =>
{
    try
    {
        using var reader = new StreamReader(request.Body);
        var buffer = new char[ProgramYaml.MaximumBytes + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(count));
            if (read == 0) break;
            count += read;
        }
        if (count == buffer.Length) return Results.BadRequest(new { error = "Program files must fit within 2 MiB." });
        var imported = ProgramYaml.Import(new string(buffer, 0, count));
        if (!imported.Success) return Results.Json(imported, ProgramJson.Options);
        var compiled = RequirementCompiler.Compile(imported.Document!);
        return Results.Json(new { imported.Success, imported.Document, compiled.Diagnostics, compiled.Tree, compiled.Connections, compiled.Choices, compiled.PresenceChoices }, ProgramJson.Options);
    }
    catch (BadHttpRequestException exception) { return Results.BadRequest(new { error = exception.Message }); }
});
// Keep the scalar pressure sandbox alongside the staged physical-design APIs.
PressureEndpoints.Map(app);
DesignEndpoints.Map(app);
if (arguments.Contains("--open"))
    app.Lifetime.ApplicationStarted.Register(() => Process.Start(new ProcessStartInfo($"http://127.0.0.1:{port}") { UseShellExecute = true }));
Console.WriteLine($"Station Architect: http://127.0.0.1:{port} — Ctrl+C to stop.");
await app.RunAsync();
return 0;

static IResult Guard(Func<object> action)
{
    try
    {
        var result = action();
        return result is IResult response ? response : Results.Json(result, ProgramJson.Options);
    }
    catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = "Invalid room program.", detail = exception.Message });
    }
}

static void CheckEditorStructure(StationProgramDocument document)
{
    // Semantic errors remain editable. Null objects/arrays cannot be drawn by the form editor.
    if (document.Settings == null || document.SourceRefs == null || document.Choices == null || document.Departments == null || document.Connections == null || document.Maintenance == null ||
        document.Maintenance.Area == null || document.Maintenance.PassageWidth == null || document.Maintenance.Connectivity == null || document.Maintenance.Rooms == null)
        throw new InvalidDataException("Draft objects and collections cannot be null.");
    if (document.SchemaVersion != 2) throw new InvalidDataException("This tool opens schemaVersion: 2 programs.");
    foreach (var department in document.Departments)
        if (department == null || department.SourceRefs == null || department.Rooms == null || department.Area == null || department.Shape == null || department.Split == null || department.Tags == null)
            throw new InvalidDataException("Departments require non-null room collections, area, shape, split and tags.");
    var rooms = document.Departments.SelectMany(d => d.Rooms).Concat(document.Maintenance.Rooms).ToList();
    for (var index = 0; index < rooms.Count; index++)
    {
        var room = rooms[index];
        if (rooms.Count > 4096 || room == null || room.Rooms == null || room.Placement == null || room.Nesting == null || room.SourceRefs == null || room.Count == null || room.Area == null || room.Shape == null || room.Access == null || room.Access.EntryOwners == null || room.Access.RequireNetworkApproach == null || room.Exterior == null || room.Exterior.Docks == null || room.Tags == null || room.Connections == null || room.Contents == null || room.Environment == null)
            throw new InvalidDataException("Rooms require non-null counts, area, shape, access, exterior, tags and connections.");
        rooms.AddRange(room.Rooms);
    }
    foreach (var dock in rooms.SelectMany(room => room.Exterior.Docks))
        if (dock == null || dock.Directions == null || dock.SourceRefs == null) throw new InvalidDataException("Dock requirements need non-null directions and source references.");
    foreach (var choice in document.Choices)
        if (choice == null || choice.Rooms == null || choice.Count == null) throw new InvalidDataException("Room choices require non-null candidates and count bounds.");
    foreach (var content in rooms.SelectMany(r => r.Contents))
        if (content == null || content.Prototypes == null || content.Count == null) throw new InvalidDataException("Content requirements need non-null prototypes and count bounds.");
    foreach (var rule in document.Connections.Concat(rooms.SelectMany(r => r.Connections)))
        if (rule == null || rule.To == null || rule.Modes == null || rule.InterfaceTypes == null) throw new InvalidDataException("Connections require non-null targets, modes and interface types.");
}
