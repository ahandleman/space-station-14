using System.Diagnostics;
using System.Text.Json;
using Content.StationGeneration;

var arguments = args.ToList();
string? Option(string name) => arguments.IndexOf(name) is var i && i >= 0 && i + 1 < arguments.Count ? arguments[i + 1] : null;
if (arguments.Contains("--help"))
{
    Console.WriteLine("Station Creator: --port 8420 [--open] | --export DIRECTORY [--seed 14014] [--archetype ring] [--config FILE | --project FILE | --definition FILE] | --batch 100 [--parallel 4] [--report FILE]");
    return 0;
}
if (Option("--batch") is { } batch)
    return RegressionChecks.Run(int.Parse(batch), Option("--report"), int.TryParse(Option("--parallel"), out var workers) ? workers : 1);
if (Option("--export") is { } directory)
{
    var input = Option("--definition") is { } definitionFile
        ? StationDefinitionYaml.Parse(File.ReadAllText(definitionFile)).ToProject()
        : Option("--project") is { } projectFile
        ? JsonSerializer.Deserialize<StationProject>(File.ReadAllText(projectFile), ProjectJson.Options)!
        : new StationProject();
    var config = Option("--config") is { } file
        ? JsonSerializer.Deserialize<StationGenerationConfig>(File.ReadAllText(file), ProjectJson.Options)!
        : input.Config;
    if (Option("--seed") is { } seed) config.Seed = int.Parse(seed);
    if (Option("--archetype") is { } archetype) config.Archetype = archetype;
    input.Config = config;
    var project = arguments.Contains("--legacy-rectangles") ? StationGenerator.Generate(input) : StationSpatialPlanner.Generate(input);
    Directory.CreateDirectory(directory);
    var stem = Path.Combine(directory, $"{config.Archetype}-{config.Seed}");
    File.WriteAllText(stem + ".json", ProjectJson.Serialize(project));
    File.WriteAllText(stem + ".svg", SvgExporter.Export(project));
    File.WriteAllText(stem + ".yaml", StationDefinitionYaml.Export(project));
    Console.WriteLine(ProjectJson.Serialize(project.Result));
    Console.WriteLine(Path.GetFullPath(stem + ".svg"));
    return project.Result?.Success == true ? 0 : 1;
}

var portNumber = int.TryParse(Option("--port"), out var requestedPort) ? requestedPort : 8420;
if (portNumber is < 1024 or > 65535) throw new ArgumentException("Port must be 1024–65535.");
var webRoot = new[] { Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"), Path.Combine(AppContext.BaseDirectory, "wwwroot") }
    .FirstOrDefault(path => File.Exists(Path.Combine(path, "index.html")))
    ?? throw new DirectoryNotFoundException("Station Creator wwwroot/index.html is missing; rebuild or publish the project.");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = [],
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = webRoot
});
builder.WebHost.UseUrls($"http://127.0.0.1:{portNumber}");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32 * 1024 * 1024);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
var app = builder.Build();
app.Use(async (context, next) =>
{
    // No CORS: this local authoring API is only used by its own origin.
    var origin = context.Request.Headers.Origin.ToString();
    if (origin.Length != 0 && origin != $"http://127.0.0.1:{portNumber}")
    {
        context.Response.StatusCode = 403;
        return;
    }
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    await next(context);
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/api/default", () =>
{
    var project = new StationProject { Stage = "topology" };
    project.Topology = StationGenerator.GenerateTopology(project.Config);
    return Results.Json(project, ProjectJson.Options);
});
app.MapPost("/api/generate", (JsonElement payload) => Guard(() =>
{
    if (payload.TryGetProperty("config", out _))
    {
        var project = payload.Deserialize<StationProject>(ProjectJson.Options) ?? throw new ArgumentException("A project is required.");
        CheckDraft(project);
        return StationGenerator.Generate(project);
    }
    var config = payload.Deserialize<StationGenerationConfig>(ProjectJson.Options) ?? throw new ArgumentException("A specification is required.");
    return StationGenerator.Generate(config);
}));
app.MapPost("/api/topology/validate", (StationProject project) => Guard(() =>
{
    CheckDraft(project);
    var diagnostics = StationSpatialPlanner.Check(project);
    return new { valid = diagnostics.All(d => d.Severity != "error"), diagnostics };
}));
app.MapPost("/api/definition/export", (StationProject project) => Guard(() =>
{
    CheckDraft(project);
    return Results.Text(StationDefinitionYaml.Export(project), "application/yaml");
}));
app.MapPost("/api/definition/import", async (HttpRequest request) =>
{
    if (request.ContentLength > 2 * 1024 * 1024)
        return Results.BadRequest(new { error = "Definition file exceeds 2 MiB." });
    using var reader = new StreamReader(request.Body);
    var buffer = new char[2 * 1024 * 1024 + 1];
    var count = 0;
    while (count < buffer.Length)
    {
        var read = await reader.ReadAsync(buffer.AsMemory(count));
        if (read == 0) break;
        count += read;
    }
    if (count == buffer.Length) return Results.BadRequest(new { error = "Definition file exceeds 2 MiB." });
    return Guard(() =>
    {
        var imported = StationDefinitionYaml.Import(new string(buffer, 0, count));
        if (!imported.Success)
            return Results.BadRequest(new { error = "Invalid station definition.", detail = string.Join(" ", imported.Diagnostics.Select(d => d.Message)), diagnostics = imported.Diagnostics });
        var project = imported.Document!.ToProject();
        project.Topology ??= StationGenerator.GenerateTopology(project.Config);
        CheckDraft(project);
        return project;
    });
});
app.MapPost("/api/topology/template", (StationProject project, bool? replaceAll) => Guard(() =>
{
    CheckDraft(project);
    var errors = StationSpatialPlanner.Check(project).Where(d => d.Stage != "topology").ToList();
    if (errors.Count != 0) throw new ArgumentException(string.Join(" ", errors.Select(d => d.Message)));
    var template = StationGenerator.GenerateTopology(project.Config);
    if (replaceAll != true && project.Topology != null)
    {
        var authored = project.Topology.Edges.Where(e => e.Origin != "template").ToList();
        template.SuppressedTemplateIds = project.Topology.SuppressedTemplateIds.ToList();
        template.Edges.RemoveAll(e => template.SuppressedTemplateIds.Contains(e.Id) || authored.Any(a => a.Id == e.Id || SameDependency(a, e)));
        template.Edges.AddRange(authored);
    }
    project.Topology = template;
    project.Layout = null;
    if (project.SpatialPlan is { } spatial)
    {
        spatial.Regions.Clear(); spatial.PublicReservations.Clear(); spatial.ServiceReservations.Clear(); spatial.InitialServiceReservations.Clear();
        spatial.PublicCorridors.Clear(); spatial.Maintenance.Clear(); spatial.Walls.Clear(); spatial.Hull.Clear();
        spatial.ReservedExterior.Clear(); spatial.ExteriorBoundary.Clear(); spatial.StructuralReserve.Clear(); spatial.Ports.Clear();
        spatial.DeepMaintenanceRooms.Clear(); spatial.CommonMaintenanceRooms.Clear(); spatial.SolarInstallations.Clear(); spatial.MaintenanceSpaceAccess.Clear();
        spatial.Hierarchy = new StationHierarchyPlan();
        spatial.Stage = "placement";
    }
    project.Result = null;
    project.Stage = "topology";
    CheckDraft(project);
    return project;
}));
app.MapPost("/api/stage/{stage}", (string stage, StationProject project) => Guard(() =>
{
    CheckDraft(project);
    return StationGenerator.RunStage(project, stage);
}));
app.MapPost("/api/spatial/generate", (StationProject project) => Guard(() =>
{
    CheckDraft(project);
    return StationSpatialPlanner.Generate(project);
}));
app.MapPost("/api/spatial/{stage}", (string stage, StationProject project) => Guard(() =>
{
    CheckDraft(project);
    return StationSpatialPlanner.RunStage(project, stage);
}));
app.MapPost("/api/import", (StationProject project) => Guard(() =>
{
    if (project.Version != 1) throw new ArgumentException("Unsupported project version.");
    CheckDraft(project);
    var configErrors = StationSpatialPlanner.Check(project).Where(d => d.Stage != "topology").ToList();
    if (configErrors.Count != 0) throw new ArgumentException(string.Join(" ", configErrors.Select(d => d.Message)));
    project.Topology ??= StationGenerator.GenerateTopology(project.Config);
    if (project.SpatialPlan != null)
    {
        // Intermediate drafts remain importable; validation reports actual mask issues.
        project.Result = StationSpatialPlanner.Validate(project);
        project.Stage = project.Result.Success ? "validation" : project.SpatialPlan.Stage;
    }
    else if (project.Layout == null)
    {
        project.Stage = "topology";
        project.Result = null;
    }
    else
        project.Result = StationValidator.Validate(project);
    return project;
}));
app.MapPost("/api/hull", (StationProject project) => Guard(() =>
{
    CheckDraft(project);
    var configErrors = StationGenerator.CheckConfig(project.Config);
    if (configErrors.Count != 0 || project.Layout == null) throw new ArgumentException("Valid specification and layout required.");
    project.Layout.Hull = StationGenerator.Sorted(StationGenerator.ExpectedHull(project.Config, project.Layout));
    project.Result = null;
    return project;
}));
app.MapPost("/api/svg", (StationProject project) => Guard(() =>
{
    CheckDraft(project);
    project.Result = project.SpatialPlan != null ? StationSpatialPlanner.Validate(project) : StationValidator.Validate(project);
    return Results.Text(SvgExporter.Export(project), "image/svg+xml");
}));
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
if (arguments.Contains("--open"))
    app.Lifetime.ApplicationStarted.Register(() => Process.Start(new ProcessStartInfo($"http://127.0.0.1:{portNumber}") { UseShellExecute = true }));
Console.WriteLine($"Station Creator: http://127.0.0.1:{portNumber} — Ctrl+C to stop.");
await app.RunAsync();
return 0;

static IResult Guard(Func<object> action)
{
    try
    {
        var value = action();
        return value is IResult response ? response : Results.Json(value, ProjectJson.Options);
    }
    catch (Exception e) when (e is ArgumentException or InvalidOperationException or NullReferenceException or KeyNotFoundException or InvalidDataException)
    {
        return Results.BadRequest(new { error = "Malformed project data. Reload a valid project or correct its JSON.", detail = e.Message });
    }
}

static bool SameDependency(TopologyEdge a, TopologyEdge b) =>
    a.Priority == b.Priority && a.Relation == b.Relation && a.Purpose == b.Purpose && a.Direction == b.Direction &&
    (a.From == b.From && a.To == b.To || a.Direction == "both" && a.From == b.To && a.To == b.From) &&
    a.AllowedModes != null && b.AllowedModes != null && a.AllowedModes.ToHashSet(StringComparer.Ordinal).SetEquals(b.AllowedModes);

static void CheckDraft(StationProject project)
{
    if (project.Version != 1 || project.Config == null) throw new ArgumentException("A version 1 project and specification are required.");
    if (project.Topology != null && (project.Topology.Nodes == null || project.Topology.Edges == null || project.Topology.Nodes.Count > 16 || project.Topology.Edges.Count > 512))
        throw new ArgumentException("Invalid topology data.");
    if (project.Topology != null)
        StationGenerator.NormalizeTopology(project.Topology);
    if (project.Config.Departments?.Count > 16 || project.Config.HierarchyRelations?.Count > 2048 ||
        project.Config.Departments?.Any(d => d?.Subdepartments?.Count > 64 || d?.Subdepartments?.Any(s => s?.Rooms?.Count > 64 || s?.Rooms?.Any(r => r?.AccessibleFrom?.Count > 1024) == true) == true) == true)
        throw new ArgumentException("Station program exceeds the authoring limits.");
    if (project.SpatialPlan != null)
        CheckSpatialData(project.SpatialPlan);
    var l = project.Layout;
    if (l == null) return;
    if (l.Departments == null || l.Doors == null || l.Routes == null || l.PublicCorridors == null || l.Maintenance == null || l.Hull == null || l.ReservedExterior == null ||
        l.Departments.Count > 16 || l.Doors.Count > 64 || l.Routes.Count > 512 ||
        l.Departments.Any(d => d == null || d.Bounds.Width is < 1 or > 64 || d.Bounds.Height is < 1 or > 64 || d.Bounds.X < -256 || d.Bounds.Y < -256 || d.Bounds.X > 256 || d.Bounds.Y > 256) ||
        l.Doors.Any(d => d == null || d.Cells == null || d.Throat == null || d.Cells.Count > 5 || d.Throat.Count > 40) ||
        l.Routes.Any(r => r == null || r.Centerline == null || r.Centerline.Count > 65536) ||
        new[] { l.PublicCorridors, l.Maintenance, l.Hull, l.ReservedExterior }.Any(layer => layer.Count > 65536 || layer.Any(p => p.X < -256 || p.Y < -256 || p.X > 512 || p.Y > 512)))
        throw new ArgumentException("Malformed or oversized layout data.");
}

static void CheckSpatialData(StationSpatialPlan plan)
{
    if (plan.Envelope == null || plan.Seeds == null || plan.Regions == null || plan.Ports == null ||
        plan.Seeds.Count > 16 || plan.Regions.Count > 16 || plan.Ports.Count > 256 ||
        plan.Seeds.Any(seed => seed == null || seed.CoreFootprint == null || seed.CoreFootprint.Count > 4096 ||
            seed.Position.X < -256 || seed.Position.Y < -256 || seed.Position.X > 512 || seed.Position.Y > 512) ||
        plan.Regions.Any(region => region == null || region.Territory == null || region.Cells == null) ||
        plan.Ports.Any(port => port == null || port.Cells == null || port.Cells.Count > 64 ||
            port.Center.X < -256 || port.Center.Y < -256 || port.Center.X > 512 || port.Center.Y > 512))
        throw new ArgumentException("Malformed spatial planning data.");
    if (plan.DeepMaintenanceRooms == null || plan.CommonMaintenanceRooms == null || plan.SolarInstallations == null || plan.MaintenanceSpaceAccess == null ||
        plan.DeepMaintenanceRooms.Count > 32 || plan.CommonMaintenanceRooms.Count > 32 || plan.SolarInstallations.Count > 32 || plan.MaintenanceSpaceAccess.Count > 32 ||
        plan.DeepMaintenanceRooms.Any(r => r == null || r.Cells == null || r.Walls == null || r.PortCells == null) ||
        plan.SolarInstallations.Any(s => s == null || s.Cells == null || s.Catwalk == null || s.ConnectionCells == null) ||
        plan.MaintenanceSpaceAccess.Any(a => a == null || a.BarrierCells == null || a.ExteriorCells == null) ||
        plan.CommonMaintenanceRooms.Any(r => r == null || r.Cells == null || r.Walls == null || r.PublicDoorCells == null || r.MaintenanceDoorCells == null))
        throw new ArgumentException("Malformed maintenance room or exterior installation data.");
    if (plan.Hierarchy == null || plan.Hierarchy.Subdepartments == null || plan.Hierarchy.RoomFeatures == null || plan.Hierarchy.InternalCirculation == null ||
        plan.Hierarchy.Subdepartments.Count > 1024 || plan.Hierarchy.RoomFeatures.Count > 4096 || plan.Hierarchy.InternalCirculation.Count > 16 ||
        plan.Hierarchy.Subdepartments.Any(s => s == null || s.Cells == null) ||
        plan.Hierarchy.RoomFeatures.Any(r => r == null || r.Cells == null || r.Walls == null || r.Doors == null || r.AccessRoutes == null ||
            r.AccessRoutes.Count > 1024 || r.AccessRoutes.Any(a => a == null || a.Cells == null)) ||
        plan.Hierarchy.InternalCirculation.Any(c => c == null || c.Cells == null))
        throw new ArgumentException("Malformed subdepartment or room planning data.");
    var layers = new[] { plan.Envelope, plan.PublicReservations, plan.ServiceReservations, plan.InitialServiceReservations, plan.PublicCorridors,
        plan.Maintenance, plan.Walls, plan.Hull, plan.ReservedExterior, plan.ExteriorBoundary, plan.StructuralReserve }
        .Concat(plan.Regions.SelectMany(region => new[] { region.Territory, region.Cells }))
        .Concat(plan.Seeds.Select(seed => seed.CoreFootprint))
        .Concat(plan.DeepMaintenanceRooms.SelectMany(room => new[] { room.Cells, room.Walls, room.PortCells }))
        .Concat(plan.CommonMaintenanceRooms.SelectMany(room => new[] { room.Cells, room.Walls, room.PublicDoorCells, room.MaintenanceDoorCells }))
        .Concat(plan.SolarInstallations.SelectMany(solar => new[] { solar.Cells, solar.Catwalk, solar.ConnectionCells }))
        .Concat(plan.MaintenanceSpaceAccess.SelectMany(access => new[] { access.BarrierCells, access.ExteriorCells }))
        .Concat(plan.Hierarchy.Subdepartments.Select(s => s.Cells))
        .Concat(plan.Hierarchy.RoomFeatures.SelectMany(r => new[] { r.Cells, r.Walls, r.Doors }.Concat(r.AccessRoutes.Select(a => a.Cells))))
        .Concat(plan.Hierarchy.InternalCirculation.Select(c => c.Cells));
    if (layers.Any(layer => layer == null || layer.Count > 65536 || layer.Any(p => p.X < -256 || p.Y < -256 || p.X > 512 || p.Y > 512)))
        throw new ArgumentException("Malformed or oversized spatial masks.");
}
