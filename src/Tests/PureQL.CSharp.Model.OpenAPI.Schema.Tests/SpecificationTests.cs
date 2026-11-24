using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.OpenApi;

namespace PureQL.CSharp.Model.OpenAPI.Schema.Tests;

/// <summary>
/// Transforms PureQL specification 0.1.0-preview.3.0.0, from the
/// <c>src/Tests/PureQL-Specification</c> submodule.
/// </summary>
public sealed partial record SpecificationTests
{
    private static readonly string SpecificationPath = Path.Combine(
        AppContext.BaseDirectory,
        "Specification",
        "PureQL-Specification.json"
    );

    private static async Task<OpenApiDocument> TransformedDocument()
    {
        OpenApiDocument document = new OpenApiDocument();

        await new PureQLQueryDocumentTransformer(
            File.ReadAllText(SpecificationPath)
        ).TransformAsync(document, null!, CancellationToken.None);

        return document;
    }

    [Fact]
    public async Task EmitsQueryAndEveryDefinition()
    {
        JsonNode specification = JsonNode.Parse(File.ReadAllText(SpecificationPath))!;
        JsonObject defs = specification["$defs"]!.AsObject();

        OpenApiDocument document = await TransformedDocument();

        Assert.Equal(defs.Count + 1, document.Components!.Schemas!.Count);
    }

    [Fact]
    public async Task EmitsOnlyValidComponentNames()
    {
        OpenApiDocument document = await TransformedDocument();

        Assert.All(
            document.Components!.Schemas!.Keys,
            name => Assert.Matches(ComponentName(), name)
        );
    }

    [Fact]
    public async Task LeavesNoDanglingComponentReferences()
    {
        OpenApiDocument document = await TransformedDocument();

        string serialized = await document.SerializeAsJsonAsync(
            OpenApiSpecVersion.OpenApi3_1
        );

        IEnumerable<string> referenced = ComponentReference()
            .Matches(serialized)
            .Select(x => x.Groups["name"].Value);

        Assert.Empty(referenced.Except(document.Components!.Schemas!.Keys));
    }

    [Fact]
    public async Task LeavesNoReferenceOutsideComponents()
    {
        OpenApiDocument document = await TransformedDocument();

        string serialized = await document.SerializeAsJsonAsync(
            OpenApiSpecVersion.OpenApi3_1
        );

        Assert.DoesNotMatch(ForeignReference(), serialized);
    }

    [Fact]
    public async Task KeepsBooleanConstsBoolean()
    {
        OpenApiDocument document = await TransformedDocument();

        string serialized = await document.SerializeAsJsonAsync(
            OpenApiSpecVersion.OpenApi3_1
        );

        Assert.DoesNotMatch(StringifiedBoolean(), serialized);
    }

    // Microsoft.OpenApi reads a schema into an object model and silently drops a keyword it
    // has no place for, so every keyword the specification uses must come out as often as it
    // goes in. The transformer itself rewrites only "const", "enum" and "not" (see below).
    [Theory]
    [InlineData("$ref")]
    [InlineData("additionalProperties")]
    [InlineData("allOf")]
    [InlineData("anyOf")]
    [InlineData("contains")]
    [InlineData("dependentRequired")]
    [InlineData("dependentSchemas")]
    [InlineData("else")]
    [InlineData("if")]
    [InlineData("items")]
    [InlineData("maxContains")]
    [InlineData("maxItems")]
    [InlineData("maximum")]
    [InlineData("minContains")]
    [InlineData("minItems")]
    [InlineData("minProperties")]
    [InlineData("minimum")]
    [InlineData("oneOf")]
    [InlineData("pattern")]
    [InlineData("properties")]
    [InlineData("required")]
    [InlineData("then")]
    [InlineData("title")]
    [InlineData("type")]
    [InlineData("uniqueItems")]
    public async Task KeepsEveryOccurrenceOfKeyword(string keyword)
    {
        JsonNode specification = JsonNode.Parse(File.ReadAllText(SpecificationPath))!;
        JsonObject schemas = await Schemas();

        Assert.Equal(
            CountKeyword(specification, keyword),
            CountKeywordInComponents(schemas, keyword)
        );
    }

    // A "const" that is not a string becomes a one-value "enum", so neither count is kept on
    // its own, but their sum is.
    [Fact]
    public async Task KeepsEveryConstOrEnum()
    {
        JsonNode specification = JsonNode.Parse(File.ReadAllText(SpecificationPath))!;
        JsonObject schemas = await Schemas();

        Assert.Equal(
            CountKeyword(specification, "const") + CountKeyword(specification, "enum"),
            CountKeywordInComponents(schemas, "const")
                + CountKeywordInComponents(schemas, "enum")
        );
    }

    private static async Task<JsonObject> Schemas()
    {
        OpenApiDocument document = await TransformedDocument();

        string serialized = await document.SerializeAsJsonAsync(
            OpenApiSpecVersion.OpenApi3_1
        );

        return JsonNode.Parse(serialized)!["components"]!["schemas"]!.AsObject();
    }

    // The keys of "components.schemas" are component names, not keywords: "type" is one.
    private static int CountKeywordInComponents(JsonObject schemas, string keyword)
    {
        return schemas.Sum(x => CountKeyword(x.Value, keyword));
    }

    // Counts the schemas that use the keyword. Below a keyword whose value is a map of
    // subschemas ("properties", "$defs", ...) the keys are names, not keywords, and below one
    // whose value is data ("const", "required", ...) nothing is a schema. An empty "required"
    // is not counted: it requires nothing, and Microsoft.OpenApi drops it.
    private static int CountKeyword(JsonNode? node, string keyword)
    {
        switch (node)
        {
            case JsonObject obj:
                int count = 0;
                foreach ((string key, JsonNode? value) in obj)
                {
                    if (key == keyword && value is not JsonArray { Count: 0 })
                    {
                        count++;
                    }

                    if (SubschemaMapKeywords.Contains(key) && value is JsonObject map)
                    {
                        count += map.Sum(x => CountKeyword(x.Value, keyword));
                    }
                    else if (!DataKeywords.Contains(key))
                    {
                        count += CountKeyword(value, keyword);
                    }
                }

                return count;

            case JsonArray arr:
                return arr.Sum(x => CountKeyword(x, keyword));

            default:
                return 0;
        }
    }

    private static readonly HashSet<string> SubschemaMapKeywords =
    [
        with(StringComparer.Ordinal),
        "$defs",
        "properties",
        "patternProperties",
        "dependentSchemas",
    ];

    private static readonly HashSet<string> DataKeywords =
    [
        with(StringComparer.Ordinal),
        "const",
        "enum",
        "required",
        "dependentRequired",
    ];

    [GeneratedRegex("^[a-zA-Z0-9.\\-_]+$")]
    private static partial Regex ComponentName();

    [GeneratedRegex(""""\$ref":\s*"#/components/schemas/(?<name>[^"]+)"""")]
    private static partial Regex ComponentReference();

    [GeneratedRegex(""""\$ref":\s*"(?!#/components/schemas/)"""")]
    private static partial Regex ForeignReference();

    [GeneratedRegex(@"""const"":\s*""(True|False)""")]
    private static partial Regex StringifiedBoolean();
}
