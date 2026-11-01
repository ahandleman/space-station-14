using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Content.StationArchitect;

public sealed class ProgramImportResult
{
    public StationProgramDocument? Document { get; set; }
    public List<ProgramDiagnostic> Diagnostics { get; set; } = [];
    public bool Success => Document != null && Diagnostics.All(d => d.Severity != "error");
}

/// <summary>Bounded, fixed-model YAML. Never constructs types named by an input document.</summary>
public static class ProgramYaml
{
    public const int MaximumBytes = 2 * 1024 * 1024;
    private const int MaximumEvents = 100000;
    private const int MaximumDepth = 40;

    public static string Export(StationProgramDocument document)
    {
        var serialized = JsonSerializer.SerializeToElement(document, ProgramJson.Options);
        var stream = new YamlStream(new YamlDocument(ToYaml(serialized)));
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        writer.WriteLine("# Station Architect room-first program. Floor area excludes walls and public/maintenance passages.");
        writer.WriteLine("# Canonical references: Department/Room; true maintenance rooms: _maintenance/Room.");
        stream.Save(writer, false);
        return writer.ToString();
    }

    public static StationProgramDocument Parse(string yaml)
    {
        var result = Import(yaml);
        if (!result.Success) throw new InvalidDataException(string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Path}: {d.Message}")));
        return result.Document!;
    }

    public static ProgramImportResult Import(string yaml)
    {
        var result = new ProgramImportResult();
        try
        {
            if (string.IsNullOrWhiteSpace(yaml) || Encoding.UTF8.GetByteCount(yaml) > MaximumBytes)
                throw new InvalidDataException("Provide a room-first YAML program within 2 MiB.");
            CheckSyntax(yaml);
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
                throw new InvalidDataException("Provide exactly one YAML mapping document.");
            foreach (var key in new[] { "schemaVersion", "id", "departments" })
                if (!root.Children.ContainsKey(new YamlScalarNode(key))) throw new InvalidDataException($"Required root field '{key}' is missing.");
            var json = ToJson(root, typeof(StationProgramDocument), "$", 0);
            var document = json!.Deserialize<StationProgramDocument>(ProgramJson.Options) ?? throw new InvalidDataException("The program is empty.");
            result.Diagnostics.AddRange(ProgramValidator.Validate(document));
            if (result.Diagnostics.All(d => d.Severity != "error")) result.Document = document;
        }
        catch (Exception exception) when (exception is YamlException or JsonException or InvalidDataException or ArgumentException or OverflowException or FormatException)
        {
            result.Diagnostics.Add(new("$", "yaml", exception.Message));
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
            if (++count > MaximumEvents) throw new InvalidDataException("The program contains too many YAML nodes.");
            if (parser.Current is AnchorAlias) throw new InvalidDataException("YAML aliases are not supported; write every definition explicitly.");
            if (parser.Current is NodeEvent node)
            {
                if (!node.Tag.IsEmpty) throw new InvalidDataException("Explicit YAML tags are not supported.");
                if (!node.Anchor.IsEmpty) throw new InvalidDataException("YAML anchors are not supported.");
            }
            if (parser.Current is MappingStart or SequenceStart)
            {
                if (++depth > MaximumDepth) throw new InvalidDataException("The program is nested too deeply.");
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

    private static JsonNode? ToJson(YamlNode node, Type requestedType, string path, int depth, bool allowNull = false)
    {
        if (depth > MaximumDepth) throw new InvalidDataException($"{path}: nesting is too deep.");
        var nullable = Nullable.GetUnderlyingType(requestedType);
        var type = nullable ?? requestedType;
        // Native component payloads remain bounded data. No YAML tag can construct a CLR type.
        if (type == typeof(JsonElement)) return ToUntypedJson(node, path, depth);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>) && type.GetGenericArguments()[0] == typeof(string))
        {
            if (node is not YamlMappingNode dictionary) throw new InvalidDataException($"{path}: expected a mapping with string keys.");
            var values = new JsonObject(); var valueType = type.GetGenericArguments()[1];
            foreach (var pair in dictionary.Children)
            {
                if (pair.Key is not YamlScalarNode { Value: not null } key || key.Value.Length == 0) throw new InvalidDataException($"{path}: keys must be nonempty strings.");
                if (values.ContainsKey(key.Value)) throw new InvalidDataException($"{path}: duplicate field '{key.Value}'.");
                values.Add(key.Value, ToJson(pair.Value, valueType, $"{path}.{key.Value}", depth + 1));
            }
            return values;
        }
        if (node is YamlScalarNode scalar)
        {
            var text = scalar.Value;
            var plain = scalar.Style is ScalarStyle.Plain or ScalarStyle.Any;
            if (text == null || plain && (text.Length == 0 || text is "~" or "null" or "Null" or "NULL"))
            {
                if (nullable != null || allowNull) return null;
                throw new InvalidDataException($"{path}: null is not allowed; supply a value or an empty list.");
            }
            if (type == typeof(string)) return JsonValue.Create(text);
            if (type == typeof(bool) && bool.TryParse(text, out var boolean)) return JsonValue.Create(boolean);
            if (type == typeof(int) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return JsonValue.Create(integer);
            if (type == typeof(double) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)) return JsonValue.Create(number);
            throw new InvalidDataException($"{path}: expected {type.Name}.");
        }
        if (node is YamlSequenceNode sequence)
        {
            if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(List<>)) throw new InvalidDataException($"{path}: a list is not valid here.");
            var array = new JsonArray();
            var element = type.GetGenericArguments()[0];
            for (var i = 0; i < sequence.Children.Count; i++) array.Add(ToJson(sequence.Children[i], element, $"{path}[{i}]", depth + 1));
            return array;
        }
        if (node is not YamlMappingNode mapping || type.IsPrimitive || type == typeof(string) || type.IsGenericType)
            throw new InvalidDataException($"{path}: a mapping is not valid here.");
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetMethod != null && p.SetMethod != null && p.GetCustomAttribute<JsonIgnoreAttribute>() == null)
            .ToDictionary(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name), StringComparer.Ordinal);
        var result = new JsonObject();
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is not YamlScalarNode { Value: not null } key) throw new InvalidDataException($"{path}: keys must be strings.");
            if (!properties.TryGetValue(key.Value, out var property)) throw new InvalidDataException($"{path}: unknown field '{key.Value}'.");
            if (result.ContainsKey(key.Value)) throw new InvalidDataException($"{path}: duplicate field '{key.Value}'.");
            result.Add(key.Value, ToJson(pair.Value, property.PropertyType, $"{path}.{key.Value}", depth + 1, new NullabilityInfoContext().Create(property).ReadState == NullabilityState.Nullable));
        }
        return result;
    }

    private static JsonNode? ToUntypedJson(YamlNode node, string path, int depth)
    {
        if (depth > MaximumDepth) throw new InvalidDataException($"{path}: nesting is too deep.");
        if (node is YamlMappingNode mapping)
        {
            var result = new JsonObject();
            foreach (var pair in mapping.Children)
            {
                if (pair.Key is not YamlScalarNode { Value: not null } key || key.Value.Length == 0) throw new InvalidDataException($"{path}: keys must be nonempty strings.");
                if (result.ContainsKey(key.Value)) throw new InvalidDataException($"{path}: duplicate field '{key.Value}'.");
                result.Add(key.Value, ToUntypedJson(pair.Value, path + "." + key.Value, depth + 1));
            }
            return result;
        }
        if (node is YamlSequenceNode sequence)
        {
            var result = new JsonArray();
            for (var index = 0; index < sequence.Children.Count; index++) result.Add(ToUntypedJson(sequence.Children[index], path + "[" + index + "]", depth + 1));
            return result;
        }
        if (node is not YamlScalarNode scalar) throw new InvalidDataException($"{path}: unsupported component data.");
        var text = scalar.Value;
        if (scalar.Style is ScalarStyle.Plain or ScalarStyle.Any)
        {
            if (text == null || text.Length == 0 || text is "~" or "null" or "Null" or "NULL") return null;
            if (bool.TryParse(text, out var boolean)) return JsonValue.Create(boolean);
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return JsonValue.Create(integer);
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                if (!double.IsFinite(number)) throw new InvalidDataException($"{path}: component numbers must be finite.");
                return JsonValue.Create(number);
            }
        }
        return JsonValue.Create(text ?? "");
    }
}
