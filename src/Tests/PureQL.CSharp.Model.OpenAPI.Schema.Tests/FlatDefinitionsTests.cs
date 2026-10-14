using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace PureQL.CSharp.Model.OpenAPI.Schema.Tests;

public sealed record FlatDefinitionsTests
{
    // Shaped like PureQL 0.1.0-preview.2.0.x: flat "$defs" named "<operator>.<type>@<context>",
    // "$id" and "version" on the root, boolean subschemas and boolean consts.
    // lang=json,strict
    private const string Specification = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "https://example.com/PureQL-Specification.json",
          "version": "1.2.3",
          "title": "PureQL specification",
          "$ref": "#/$defs/expr@row",
          "$defs": {
            "expr@row": {
              "type": "object",
              "required": ["operator"],
              "properties": {
                "operator": { "const": "add" },
                "left": true,
                "right": false,
                "type": { "$ref": "#/$defs/type.nullable" }
              },
              "allOf": [true, { "$ref": "#/$defs/add.integer@row" }],
              "if": { "required": ["left"] },
              "then": true,
              "else": false,
              "additionalProperties": { "properties": { "extra": true } }
            },
            "add.integer@row": {
              "properties": { "values": { "items": true } }
            },
            "type.nullable": {
              "properties": {
                "nullable": { "const": true },
                "scale": { "const": 2, "enum": [1, 2] },
                "precision": { "const": 3, "enum": [1, 2], "allOf": [{ "minimum": 0 }] }
              }
            },
            "unused": null
          }
        }
        """;

    private static async Task<OpenApiDocument> TransformedDocument()
    {
        OpenApiDocument document = new OpenApiDocument();

        await new PureQLQueryDocumentTransformer(Specification).TransformAsync(
            document,
            null!,
            CancellationToken.None
        );

        return document;
    }

    private static async Task<JsonObject> Schemas()
    {
        OpenApiDocument document = await TransformedDocument();

        string serialized = await document.SerializeAsJsonAsync(
            OpenApiSpecVersion.OpenApi3_1
        );

        return JsonNode.Parse(serialized)!["components"]!["schemas"]!.AsObject();
    }

    [Fact]
    public async Task EmitsEveryDefinitionUnderAValidComponentName()
    {
        OpenApiDocument document = await TransformedDocument();

        Assert.Equal(
            ["Query", "add.integer_row", "expr_row", "type.nullable"],
            document.Components!.Schemas!.Keys.Order(StringComparer.Ordinal)
        );
    }

    [Fact]
    public async Task RewritesDefsReferencesToComponents()
    {
        JsonObject schemas = await Schemas();

        Assert.Equal("#/components/schemas/expr_row", (string?)schemas["Query"]!["$ref"]);
        Assert.Equal(
            "#/components/schemas/add.integer_row",
            (string?)schemas["expr_row"]!["allOf"]![1]!["$ref"]
        );
    }

    [Theory]
    [InlineData("$defs")]
    [InlineData("$id")]
    [InlineData("$schema")]
    [InlineData("version")]
    public async Task StripsDocumentKeywordsFromQuery(string keyword)
    {
        JsonObject schemas = await Schemas();

        Assert.False(schemas["Query"]!.AsObject().ContainsKey(keyword));
    }

    [Fact]
    public async Task KeepsQueryTitle()
    {
        JsonObject schemas = await Schemas();

        Assert.Equal("PureQL specification", (string?)schemas["Query"]!["title"]);
    }

    [Theory]
    [InlineData( /*lang=json,strict*/
        "{}",
        "properties",
        "left"
    )]
    [InlineData( /*lang=json,strict*/
        """{"not":{}}""",
        "properties",
        "right"
    )]
    [InlineData( /*lang=json,strict*/
        "{}",
        "then"
    )]
    [InlineData( /*lang=json,strict*/
        """{"not":{}}""",
        "else"
    )]
    [InlineData( /*lang=json,strict*/
        "{}",
        "additionalProperties",
        "properties",
        "extra"
    )]
    public async Task SpellsBooleanSubschemasAsObjects(
        string expected,
        params string[] path
    )
    {
        JsonObject schemas = await Schemas();
        JsonNode actual = path.Aggregate(schemas["expr_row"]!, (x, key) => x[key]!);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), actual));
    }

    [Fact]
    public async Task SpellsBooleanSubschemasInArraysAsObjects()
    {
        JsonObject schemas = await Schemas();

        Assert.True(
            JsonNode.DeepEquals(new JsonObject(), schemas["expr_row"]!["allOf"]![0])
        );
    }

    [Fact]
    public async Task SpellsBooleanSubschemasInNestedSchemasAsObjects()
    {
        JsonObject schemas = await Schemas();

        Assert.True(
            JsonNode.DeepEquals(
                new JsonObject(),
                schemas["add.integer_row"]!["properties"]!["values"]!["items"]
            )
        );
    }

    [Fact]
    public async Task KeepsStringConst()
    {
        JsonObject schemas = await Schemas();

        Assert.Equal(
            "add",
            (string?)schemas["expr_row"]!["properties"]!["operator"]!["const"]
        );
    }

    [Fact]
    public async Task SpellsNonStringConstAsEnum()
    {
        JsonObject schemas = await Schemas();
        JsonNode properties = schemas["type.nullable"]!["properties"]!;
        JsonObject schema = properties["nullable"]!.AsObject();

        Assert.False(schema.ContainsKey("const"));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("[true]"), schema["enum"]));
    }

    // The intersection of "const": 3 and "enum": [1, 2] is empty, which an empty "enum" would
    // not keep: Microsoft.OpenApi drops it, and the schema would accept anything.
    [Theory]
    [InlineData(
        "scale", /*lang=json,strict*/
        """[{"enum":[2]}]"""
    )]
    [InlineData(
        "precision", /*lang=json,strict*/
        """[{"minimum":0},{"enum":[3]}]"""
    )]
    public async Task JoinsNonStringConstToExistingEnumThroughAllOf(
        string property,
        string expectedAllOf
    )
    {
        JsonObject schemas = await Schemas();
        JsonNode properties = schemas["type.nullable"]!["properties"]!;
        JsonObject schema = properties[property]!.AsObject();

        Assert.False(schema.ContainsKey("const"));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("[1,2]"), schema["enum"]));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expectedAllOf), schema["allOf"]));
    }
}
