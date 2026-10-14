using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace PureQL.CSharp.Model.OpenAPI.Schema;

public sealed record PureQLQueryDocumentTransformer : IOpenApiDocumentTransformer
{
    private readonly string _schema;

    public PureQLQueryDocumentTransformer(string schema)
    {
        _schema = schema;
    }

    private const string QuerySchemaKey = "Query";
    private const string DefsKey = "$defs";
    private const string DefinitionsKey = "definitions";
    private const string DefsPointerPrefix = "#/$defs/";
    private const string DefinitionsPointerPrefix = "#/definitions/";
    private const string ComponentsSchemasPointerPrefix = "#/components/schemas/";

    // Root keywords that describe the specification document rather than the query.
    // "$id" in particular would rebase every "#/components/schemas/..." pointer onto the
    // specification's release URL, where no such component exists.
    private static readonly string[] DocumentKeywords =
    [
        DefsKey,
        DefinitionsKey,
        "$schema",
        "$id",
        "version",
    ];

    // Keywords whose value is a subschema, a map of subschemas or an array of subschemas.
    // "additionalProperties" is left out: Microsoft.OpenApi reads it as a boolean too.
    private static readonly HashSet<string> SubschemaKeywords = new(
        StringComparer.Ordinal
    )
    {
        "items",
        "contains",
        "not",
        "if",
        "then",
        "else",
        "propertyNames",
    };

    private static readonly HashSet<string> SubschemaMapKeywords = new(
        StringComparer.Ordinal
    )
    {
        "properties",
        "patternProperties",
        "dependentSchemas",
    };

    private static readonly HashSet<string> SubschemaArrayKeywords = new(
        StringComparer.Ordinal
    )
    {
        "allOf",
        "anyOf",
        "oneOf",
        "prefixItems",
    };

    private static readonly HashSet<string> SchemaKeywords = new(StringComparer.Ordinal)
    {
        "type",
        "oneOf",
        "allOf",
        "anyOf",
        "properties",
        "$ref",
        "enum",
        "items",
        "required",
        "const",
        "title",
        "description",
        "if",
        "then",
        "else",
        "not",
    };

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken
    )
    {
        JsonObject specNode = JsonNode.Parse(_schema)!.AsObject();

        // Definitions keyed by their path below the definitions root.
        // "$defs" (draft 2020-12) is flat: "add.decimal@row" stays "add.decimal@row".
        // "definitions" (older specifications) is nested: "aggregates/date/average_date".
        Dictionary<string, JsonNode> defs = new Dictionary<string, JsonNode>(
            StringComparer.Ordinal
        );

        if (specNode[DefsKey] is JsonObject flatDefs)
        {
            foreach ((string name, JsonNode? defNode) in flatDefs)
            {
                if (defNode is not null)
                {
                    defs[name] = defNode;
                }
            }
        }

        if (specNode[DefinitionsKey] is JsonObject nestedDefs)
        {
            // Every "#/definitions/..." pointer the specification actually uses. A node that
            // owns one of these as a child is a grouping node, never a schema of its own.
            HashSet<string> pointedAtPaths = new HashSet<string>(StringComparer.Ordinal);
            CollectDefinitionPointers(specNode, pointedAtPaths);

            CollectDefinitions(nestedDefs, string.Empty, pointedAtPaths, defs);
        }

        // Build root query schema node (strip the specification document metadata).
        JsonObject rootNode = specNode.DeepClone().AsObject();
        foreach (string keyword in DocumentKeywords)
        {
            _ = rootNode.Remove(keyword);
        }

        // Assemble schemas: root query + all definitions.
        JsonObject schemasNode = new JsonObject { [QuerySchemaKey] = rootNode };
        foreach ((string path, JsonNode defNode) in defs)
        {
            schemasNode[ToComponentName(path)] = defNode.DeepClone();
        }

        // Spell the constructs Microsoft.OpenApi cannot read back in an equivalent form.
        foreach ((string _, JsonNode? schemaNode) in schemasNode)
        {
            NormalizeSchema(schemaNode);
        }

        // Rewrite all "#/$defs/X" and "#/definitions/X/Y" → "#/components/schemas/..." so
        // that every $ref is a valid OpenAPI component pointer and resolvable by Scalar.
        RewriteRefs(schemasNode);

        // Wrap in a minimal OpenAPI 3.1 document so Microsoft.OpenApi can
        // parse and validate schema objects (including full $ref resolution).
        string miniDocJson = new JsonObject
        {
            ["openapi"] = "3.1.0",
            ["info"] = new JsonObject { ["title"] = "t", ["version"] = "0" },
            ["paths"] = new JsonObject(),
            ["components"] = new JsonObject { ["schemas"] = schemasNode },
        }.ToJsonString();

        ReadResult parseResult = OpenApiDocument.Parse(miniDocJson, "json");

        document.Components ??= new OpenApiComponents();

        // A document that reached the transformer without any generated schema still has a
        // null Schemas dictionary, which the assignment below would throw on.
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>(
            StringComparer.Ordinal
        );

        // Transfer all PureQL schemas into the real document, replacing the
        // auto-generated (incorrect) Query schema along the way.
        if (parseResult.Document?.Components?.Schemas is { } pureqlSchemas)
        {
            foreach ((string name, IOpenApiSchema schema) in pureqlSchemas)
            {
                document.Components.Schemas[name] = schema;
            }
        }

        return Task.CompletedTask;
    }

    // Recursively collects leaf schema nodes from the nested definitions tree.
    // A node is a schema when it contains recognised JSON Schema keywords.
    private static void CollectDefinitions(
        JsonObject node,
        string prefix,
        HashSet<string> pointedAtPaths,
        Dictionary<string, JsonNode> result
    )
    {
        foreach ((string key, JsonNode? value) in node)
        {
            string path = prefix.Length > 0 ? $"{prefix}/{key}" : key;
            if (value is not JsonObject child)
            {
                continue;
            }

            // Checked before the keyword heuristic below, because grouping node names share
            // a namespace with JSON Schema keywords: "booleanOperations" groups "and", "or"
            // and "not", and "not" alone would make the heuristic mistake the whole group
            // for a schema, dropping its three members and leaving dangling $refs behind.
            if (child.Any(p => pointedAtPaths.Contains($"{path}/{p.Key}")))
            {
                CollectDefinitions(child, path, pointedAtPaths, result);
            }
            else if (child.Any(p => SchemaKeywords.Contains(p.Key)))
            {
                result[path] = child;
            }
            else
            {
                CollectDefinitions(child, path, pointedAtPaths, result);
            }
        }
    }

    // Recursively collects the targets of every "#/definitions/..." pointer in the document,
    // as paths relative to "definitions" ("booleanOperations/and", "scalars/booleanScalar").
    private static void CollectDefinitionPointers(JsonNode? node, HashSet<string> result)
    {
        switch (node)
        {
            case JsonObject obj:
                if (
                    obj.TryGetPropertyValue("$ref", out JsonNode? refNode)
                    && refNode is JsonValue refVal
                    && refVal.TryGetValue(out string? refStr)
                    && refStr.StartsWith(
                        DefinitionsPointerPrefix,
                        StringComparison.Ordinal
                    )
                )
                {
                    _ = result.Add(refStr[DefinitionsPointerPrefix.Length..]);
                }

                foreach ((string _, JsonNode? value) in obj)
                {
                    CollectDefinitionPointers(value, result);
                }

                break;

            case JsonArray arr:
                foreach (JsonNode? item in arr)
                {
                    CollectDefinitionPointers(item, result);
                }

                break;
            default:
                break;
        }
    }

    // Recursively rewrites "$ref" values from "#/$defs/X" and "#/definitions/X/Y/Z"
    // to "#/components/schemas/X" and "#/components/schemas/X_Y_Z" in place.
    private static void RewriteRefs(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (
                    obj.TryGetPropertyValue("$ref", out JsonNode? refNode)
                    && refNode is JsonValue refVal
                    && refVal.TryGetValue(out string? refStr)
                    && TryGetDefinitionPath(refStr, out string? path)
                )
                {
                    obj["$ref"] =
                        $"{ComponentsSchemasPointerPrefix}{ToComponentName(path)}";
                }
                foreach (string? key in obj.Select(p => p.Key).ToList())
                {
                    RewriteRefs(obj[key]);
                }

                break;

            case JsonArray arr:
                foreach (JsonNode? item in arr)
                {
                    RewriteRefs(item);
                }

                break;
            default:
                break;
        }
    }

    // Recursively rewrites the schema in place into an equivalent one that survives
    // Microsoft.OpenApi:
    // - a boolean subschema is read as null and then thrown on, so true → {} (accepts
    //   anything) and false → { "not": {} } (accepts nothing). Only schema positions are
    //   touched, never a value such as "const": true;
    // - "const" is read as a string, turning "const": true into "const": "True", so a
    //   const that is not a string becomes an "enum" with that one value, whose items
    //   keep their JSON type.
    private static void NormalizeSchema(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return;
        }

        foreach ((string key, JsonNode? value) in obj.ToList())
        {
            if (key == "const" && value?.GetValueKind() is not JsonValueKind.String)
            {
                ReplaceConstWithEnum(obj, value);
            }
            else if (SubschemaKeywords.Contains(key))
            {
                NormalizeSubschema(value, replacement => obj[key] = replacement);
            }
            else if (SubschemaMapKeywords.Contains(key) && value is JsonObject map)
            {
                foreach ((string name, JsonNode? subschema) in map.ToList())
                {
                    NormalizeSubschema(subschema, replacement => map[name] = replacement);
                }
            }
            else if (SubschemaArrayKeywords.Contains(key) && value is JsonArray arr)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    int index = i;
                    NormalizeSubschema(arr[i], replacement => arr[index] = replacement);
                }
            }
            else if (key == "additionalProperties")
            {
                NormalizeSchema(value);
            }
        }
    }

    // An existing "enum" is kept and the const joins it through "allOf" instead: their
    // intersection may be empty, and Microsoft.OpenApi drops an empty "enum".
    private static void ReplaceConstWithEnum(JsonObject schema, JsonNode? constValue)
    {
        _ = schema.Remove("const");
        JsonArray values = new JsonArray(constValue?.DeepClone());

        if (!schema.ContainsKey("enum"))
        {
            schema["enum"] = values;
            return;
        }

        if (schema["allOf"] is not JsonArray allOf)
        {
            allOf = [];
            schema["allOf"] = allOf;
        }

        JsonNode constSchema = new JsonObject { ["enum"] = values };
        allOf.Add(constSchema);
    }

    private static void NormalizeSubschema(JsonNode? node, Action<JsonObject> replace)
    {
        if (node is JsonValue value && value.TryGetValue(out bool accepts))
        {
            replace(accepts ? [] : new JsonObject { ["not"] = new JsonObject() });
        }
        else
        {
            NormalizeSchema(node);
        }
    }

    private static bool TryGetDefinitionPath(string pointer, out string path)
    {
        foreach (string prefix in (string[])[DefsPointerPrefix, DefinitionsPointerPrefix])
        {
            if (pointer.StartsWith(prefix, StringComparison.Ordinal))
            {
                path = pointer[prefix.Length..];
                return true;
            }
        }

        path = string.Empty;
        return false;
    }

    // OpenAPI component names must match ^[a-zA-Z0-9.\-_]+$, so every other character is
    // replaced: the "/" of a nested path and the "@" of a PureQL context
    // ("add.decimal@row" → "add.decimal_row").
    private static string ToComponentName(string path)
    {
        return string.Create(
            path.Length,
            path,
            static (span, source) =>
            {
                for (int i = 0; i < source.Length; i++)
                {
                    char c = source[i];
                    span[i] =
                        char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_';
                }
            }
        );
    }
}
