using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Content.StationGeneration;

public sealed class StationDefinitionImportResult
{
    public StationDefinitionDocument? Document { get; set; }
    public List<Diagnostic> Diagnostics { get; set; } = [];
    public bool Success => Document != null && Diagnostics.All(d => d.Severity != "error");
}

/// <summary>
/// Versioned authoring YAML. Parsing uses only the fixed model's scalar/collection types;
/// custom tags, aliases, duplicate keys, unknown fields and generated tile layers are rejected.
/// </summary>
public static class StationDefinitionYaml
{
    private const int MaximumBytes = 2 * 1024 * 1024;
    private const int MaximumEvents = 100000;
    private const int MaximumDepth = 48;

    public static string Export(StationProject project) => Export(StationDefinitionDocument.FromProject(project));

    public static string Export(StationDefinitionDocument document)
    {
        var serialized = JsonSerializer.SerializeToElement(document, ProjectJson.Options);
        var stream = new YamlStream(new YamlDocument(ToYaml(serialized)));
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        writer.WriteLine("# SS14 station authoring definition: departments, programs, dependencies and generation settings.");
        writer.WriteLine("# Paths are Department/Subdepartment/Room; generated geometry is saved in project JSON instead.");
        stream.Save(writer, false);
        return writer.ToString();
    }

    public static StationDefinitionDocument Parse(string yaml)
    {
        var result = Import(yaml);
        if (!result.Success) throw new InvalidDataException(string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));
        return result.Document!;
    }

    public static StationDefinitionImportResult Import(string yaml)
    {
        var result = new StationDefinitionImportResult();
        try
        {
            if (string.IsNullOrWhiteSpace(yaml) || Encoding.UTF8.GetByteCount(yaml) > MaximumBytes)
                throw new InvalidDataException("Station YAML must contain a definition and fit within 2 MiB.");
            CheckSyntax(yaml);
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
                throw new InvalidDataException("Provide one YAML mapping document with schemaVersion, config and optional topology.");
            if (!root.Children.ContainsKey(new YamlScalarNode("schemaVersion")) || !root.Children.ContainsKey(new YamlScalarNode("config")))
                throw new InvalidDataException("Station YAML requires schemaVersion: 1 and a config mapping.");
            var json = ToJson(root, typeof(StationDefinitionDocument), "$", 0);
            var options = new JsonSerializerOptions(ProjectJson.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
            var document = json!.Deserialize<StationDefinitionDocument>(options) ?? throw new InvalidDataException("The YAML definition is empty.");
            if (document.SchemaVersion != 1) throw new InvalidDataException($"Unsupported station definition schemaVersion {document.SchemaVersion}; expected 1.");
            result.Diagnostics.AddRange(StationHierarchy.Validate(document.Config));
            // Spatial checks validate authoring bounds and topology without generating cells.
            var project = document.ToProject();
            var checks = StationSpatialPlanner.Check(project);
            if (project.Topology == null && checks.All(d => d.Stage == "topology" && d.Code == "missing"))
            {
                project.Topology = StationGenerator.GenerateTopology(project.Config);
                checks = StationSpatialPlanner.Check(project);
            }
            result.Diagnostics.AddRange(checks);
            result.Diagnostics = result.Diagnostics.Distinct().ToList();
            if (result.Diagnostics.All(d => d.Severity != "error")) result.Document = document;
        }
        catch (Exception exception) when (exception is YamlException or JsonException or InvalidDataException or ArgumentException or OverflowException or FormatException)
        {
            result.Diagnostics.Add(new("definition", "yaml", exception.Message));
        }
        return result;
    }

    private static void CheckSyntax(string yaml)
    {
        var parser = new Parser(new StringReader(yaml));
        var count = 0;
        var depth = 0;
        while (parser.MoveNext())
        {
            if (++count > MaximumEvents) throw new InvalidDataException("Station YAML contains too many nodes.");
            if (parser.Current is AnchorAlias) throw new InvalidDataException("YAML aliases are not supported; write each definition explicitly.");
            if (parser.Current is NodeEvent node && !node.Tag.IsEmpty && !node.Tag.ToString().StartsWith("tag:yaml.org,2002:", StringComparison.Ordinal))
                throw new InvalidDataException("Custom YAML tags are not supported in station definitions.");
            if (parser.Current is MappingStart or SequenceStart)
            {
                if (++depth > MaximumDepth) throw new InvalidDataException("Station YAML nesting is too deep.");
            }
            if (parser.Current is MappingEnd or SequenceEnd) depth--;
        }
    }

    private static YamlNode ToYaml(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => new YamlMappingNode(value.EnumerateObject().Select(p =>
            new KeyValuePair<YamlNode, YamlNode>(new YamlScalarNode(p.Name), ToYaml(p.Value)))),
        JsonValueKind.Array => new YamlSequenceNode(value.EnumerateArray().Select(ToYaml)),
        JsonValueKind.String => new YamlScalarNode(value.GetString()) { Style = ScalarStyle.DoubleQuoted },
        JsonValueKind.Null => new YamlScalarNode("null") { Style = ScalarStyle.Plain },
        _ => new YamlScalarNode(value.GetRawText()) { Style = ScalarStyle.Plain }
    };

    private static JsonNode? ToJson(YamlNode node, Type requestedType, string path, int depth)
    {
        if (depth > MaximumDepth) throw new InvalidDataException($"{path}: nesting is too deep.");
        var nullable = Nullable.GetUnderlyingType(requestedType);
        var type = nullable ?? requestedType;
        if (node is YamlScalarNode scalar)
        {
            var text = scalar.Value;
            var plain = scalar.Style is ScalarStyle.Plain or ScalarStyle.Any;
            var isNull = text == null || plain && (text.Length == 0 || text is "~" or "null" or "Null" or "NULL");
            if (isNull)
            {
                if (nullable != null || type == typeof(StationTopology)) return null;
                throw new InvalidDataException($"{path}: null is not allowed; use a value or an empty list.");
            }
            if (type == typeof(string)) return JsonValue.Create(text!);
            if (type == typeof(bool) && bool.TryParse(text, out var boolean)) return JsonValue.Create(boolean);
            if (type == typeof(int) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return JsonValue.Create(integer);
            if (type == typeof(double) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)) return JsonValue.Create(number);
            throw new InvalidDataException($"{path}: expected a {type.Name} value.");
        }
        if (node is YamlSequenceNode sequence)
        {
            if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(List<>))
                throw new InvalidDataException($"{path}: a list is not valid here.");
            var elementType = type.GetGenericArguments()[0];
            var array = new JsonArray();
            for (var i = 0; i < sequence.Children.Count; i++) array.Add(ToJson(sequence.Children[i], elementType, $"{path}[{i}]", depth + 1));
            return array;
        }
        if (node is not YamlMappingNode mapping) throw new InvalidDataException($"{path}: unsupported YAML value.");
        var dictionary = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>) && type.GetGenericArguments()[0] == typeof(string);
        if (type == typeof(string) || type.IsPrimitive || type.IsGenericType && !dictionary)
            throw new InvalidDataException($"{path}: a mapping is not valid here.");
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetMethod != null && p.SetMethod != null && p.GetCustomAttribute<JsonIgnoreAttribute>() == null)
            .ToDictionary(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(p.Name), StringComparer.OrdinalIgnoreCase);
        var result = new JsonObject();
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is not YamlScalarNode key || key.Value == null) throw new InvalidDataException($"{path}: mapping keys must be strings.");
            var name = key.Value;
            Type valueType;
            if (dictionary) valueType = type.GetGenericArguments()[1];
            else
            {
                if (!properties.TryGetValue(name, out var property)) throw new InvalidDataException($"{path}: unknown field '{name}'.");
                name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                valueType = property.PropertyType;
            }
            if (result.ContainsKey(name)) throw new InvalidDataException($"{path}: duplicate field '{name}'.");
            result.Add(name, ToJson(pair.Value, valueType, $"{path}.{name}", depth + 1));
        }
        return result;
    }
}
