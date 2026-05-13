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

        // Collect leaf schemas from the nested definitions tree.
        // Nested path "aggregates/date/average_date" → flat name "aggregates_date_average_date".
        Dictionary<string, JsonNode> defs = new Dictionary<string, JsonNode>(
            StringComparer.Ordinal
        );
        CollectDefinitions(specNode["definitions"]?.AsObject() ?? [], string.Empty, defs);

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

        // Transfer all PureQL schemas into the real document, replacing the
        // auto-generated (incorrect) Query schema along the way.
        if (parseResult.Document?.Components?.Schemas is { } pureqlSchemas)
        {
            foreach ((string name, IOpenApiSchema schema) in pureqlSchemas)
            {
                document.Components.Schemas![name] = schema;
            }
        }

        return Task.CompletedTask;
    }

    // Recursively collects leaf schema nodes from the nested definitions tree.
    // A node is a schema when it contains recognised JSON Schema keywords.
    private static void CollectDefinitions(
        JsonObject node,
        string prefix,
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

            if (child.Any(p => SchemaKeywords.Contains(p.Key)))
            {
                result[path] = child;
            }
            else
            {
                CollectDefinitions(child, path, result);
            }
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
