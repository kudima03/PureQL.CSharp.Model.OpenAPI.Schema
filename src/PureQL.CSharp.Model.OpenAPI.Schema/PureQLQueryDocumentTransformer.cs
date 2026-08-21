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
    private const string DefinitionsPointerPrefix = "#/definitions/";
    private const string ComponentsSchemasPointerPrefix = "#/components/schemas/";

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

        // Every "#/definitions/..." pointer the specification actually uses. A node that
        // owns one of these as a child is a grouping node, never a schema of its own.
        HashSet<string> pointedAtPaths = new HashSet<string>(StringComparer.Ordinal);
        CollectDefinitionPointers(specNode, pointedAtPaths);

        // Collect leaf schemas from the nested definitions tree.
        // Nested path "aggregates/date/average_date" → flat name "aggregates_date_average_date".
        Dictionary<string, JsonNode> defs = new Dictionary<string, JsonNode>(
            StringComparer.Ordinal
        );
        CollectDefinitions(
            specNode["definitions"]?.AsObject() ?? [],
            string.Empty,
            pointedAtPaths,
            defs
        );

        // Build root query schema node (strip "definitions" and "$schema" metadata).
        JsonObject rootNode = specNode.DeepClone().AsObject();
        _ = rootNode.Remove("definitions");
        _ = rootNode.Remove("$schema");

        // Assemble schemas: root query + all flat definitions.
        JsonObject schemasNode = new JsonObject { [QuerySchemaKey] = rootNode };
        foreach ((string path, JsonNode defNode) in defs)
        {
            schemasNode[ToFlatName(path)] = defNode.DeepClone();
        }

        // Rewrite all "#/definitions/X/Y" → "#/components/schemas/X_Y" so that
        // every $ref is a valid OpenAPI component pointer and resolvable by Scalar.
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

    // Recursively rewrites "$ref" values from "#/definitions/X/Y/Z"
    // to "#/components/schemas/X_Y_Z" in place.
    private static void RewriteRefs(JsonNode? node)
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
                    obj["$ref"] =
                        $"{ComponentsSchemasPointerPrefix}{ToFlatName(refStr[DefinitionsPointerPrefix.Length..])}";
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

    private static string ToFlatName(string path)
    {
        return path.Replace('/', '_');
    }
}
