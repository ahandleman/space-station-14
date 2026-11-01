using System.Text.Json;

namespace Content.StationArchitect;

/// <summary>Gas names accepted by this checkout's GasArraySerializer/Gas enum, without a game-ECS dependency.</summary>
public static class DesignEnvironmentGases
{
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    { "Oxygen", "Nitrogen", "CarbonDioxide", "Plasma", "Tritium", "WaterVapor", "Ammonia", "NitrousOxide", "Frezon" };

    public static (double? PressureKpa, Dictionary<string, double> Fractions) ParseMixture(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 16384) throw new ArgumentException("Provide a bounded JSON mixture with pressureKpa and gasFractions.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("The mixture must be a JSON object.");
            var fields = new HashSet<string>(StringComparer.Ordinal); double? pressure = null; var fractions = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!fields.Add(property.Name)) throw new ArgumentException("Mixture fields cannot be duplicated.");
                if (property.Name == "pressureKpa")
                {
                    if (property.Value.ValueKind == JsonValueKind.Null) continue;
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetDouble(out var value) || !double.IsFinite(value) || value is < 0 or > 100000)
                        throw new ArgumentException("Mixture pressure must be null or finite between 0 and 100000 kPa.");
                    pressure = value;
                }
                else if (property.Name == "gasFractions")
                {
                    if (property.Value.ValueKind != JsonValueKind.Object) throw new ArgumentException("gasFractions must be an object of gas names and numerical fractions.");
                    foreach (var gas in property.Value.EnumerateObject())
                    {
                        if (!Supported.Contains(gas.Name) || fractions.ContainsKey(gas.Name)) throw new ArgumentException("Use unique gas names supported by this SS14 export format.");
                        if (gas.Value.ValueKind != JsonValueKind.Number || !gas.Value.TryGetDouble(out var fraction) || !double.IsFinite(fraction) || fraction is < 0 or > 1)
                            throw new ArgumentException("Gas fractions must be finite numbers from zero to one.");
                        fractions[gas.Name] = fraction;
                    }
                }
                else throw new ArgumentException("Mixture JSON only accepts pressureKpa and gasFractions.");
            }
            if (fractions.Count > 0 && Math.Abs(fractions.Values.Sum() - 1) > 1e-6) throw new ArgumentException("Nonempty gas fractions must add up to one.");
            if (pressure == null && fractions.Count > 0 || pressure > 0 && fractions.Count == 0) throw new ArgumentException("A positive-pressure mixture needs gas fractions, and fractions need an explicit pressure.");
            return (pressure, fractions);
        }
        catch (JsonException exception) { throw new ArgumentException("The mixture is not valid JSON.", exception); }
    }
}
