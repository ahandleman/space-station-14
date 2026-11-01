namespace Content.StationArchitect;

/// <summary>The starting program is editable YAML data, embedded for reliable launch/publish.</summary>
public static class StationProgramDefaults
{
    private static readonly Lazy<string> Catalog = new(() =>
    {
        using var stream = typeof(StationProgramDefaults).Assembly.GetManifestResourceStream(
            "Content.StationArchitect.Catalog.standard-station.yaml")
            ?? throw new InvalidOperationException("The embedded standard station catalog is missing. Rebuild Station Architect.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>Returns a fresh document, so authoring edits cannot mutate the shared starting catalog.</summary>
    public static StationProgramDocument Create() => ProgramYaml.Parse(Catalog.Value);
}
