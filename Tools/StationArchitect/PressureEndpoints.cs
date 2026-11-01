using Content.StationArchitect;

internal static class PressureEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/pressure/sample", (StationProgramDocument document) => Guard(() =>
        {
            RequireProgram(document);
            var state = new PressureState();
            var selected = InstanceSelection.Resolve(new() { Definition = document });
            if (selected.Report?.Success != true) throw new ArgumentException("The program cannot select a bounded sample within its requirements.");
            var definitions = RequirementCompiler.Rooms(document).ToDictionary(pair => pair.Path, pair => pair.Room, StringComparer.Ordinal);
            foreach (var room in selected.Layout.Rooms.Where(room => room.Enabled && !room.Orphaned))
            {
                if (state.Rooms.Count >= 4096) throw new ArgumentException("The interactive pressure sample supports up to 4096 instances. Use the core evaluator for larger samples.");
                // No made-up geometry: zero dimensions explicitly leave geometric checks pending.
                state.Rooms.Add(new RoomMeasurement { Path = room.DefinitionPath, InstanceId = room.Instance, ParentInstanceId = room.ParentInstanceId, Area = definitions[room.DefinitionPath].Area.Target });
            }
            return state;
        }));
        app.MapPost("/api/pressure/transfer", (PressureTransferRequest request) => Guard(() =>
        {
            RequireProgram(request.Document);
            if (request.State == null || request.State.Rooms == null || request.State.Rooms.Any(r => r == null) || request.State.Departments == null || request.State.Connections == null)
                throw new ArgumentException("A pressure sample needs non-null rooms, departments and connections.");
            if (request.Phase is not ("growth" or "final")) throw new ArgumentException("Phase must be growth or final.");
            if (request.Area < 1 || request.From == request.To) throw new ArgumentException("Transfer positive floor area between different room instances.");
            var next = ProgramJson.Clone(request.State);
            RoomMeasurement Find(string key) => next.Rooms.FirstOrDefault(r => RoomHierarchy.MeasurementKey(r) == key)
                ?? throw new ArgumentException($"Room instance '{key}' is not in this sample.");
            var from = Find(request.From);
            var to = Find(request.To);
            if (from.Area < request.Area) throw new ArgumentException("The source room does not own that much floor.");
            from.Area -= request.Area;
            to.Area = checked(to.Area + request.Area);
            return PressureEvaluator.EvaluateTransfer(request.Document, request.State, next, request.Phase == "final");
        }));
    }

    private static void RequireProgram(StationProgramDocument document)
    {
        var compiled = RequirementCompiler.Compile(document);
        if (!compiled.Success) throw new ArgumentException("Correct program definition errors before creating a pressure sample.");
    }

    private static IResult Guard(Func<object> action)
    {
        try { return Results.Json(action(), ProgramJson.Options); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException or System.Text.Json.JsonException)
        { return Results.BadRequest(new { error = exception.Message }); }
    }
}

internal sealed class PressureTransferRequest
{
    public StationProgramDocument Document { get; set; } = new();
    public PressureState State { get; set; } = new();
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public int Area { get; set; }
    public string Phase { get; set; } = "growth";
}
