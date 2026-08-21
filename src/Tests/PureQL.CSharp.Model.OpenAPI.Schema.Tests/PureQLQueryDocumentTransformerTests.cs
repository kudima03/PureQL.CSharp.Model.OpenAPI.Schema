using System.Text.RegularExpressions;
using Microsoft.OpenApi;

namespace PureQL.CSharp.Model.OpenAPI.Schema.Tests;

public sealed partial record PureQLQueryDocumentTransformerTests
{
    // "booleanOperations" groups "and", "or" and "not". Its child named "not" collides with
    // the JSON Schema keyword of the same name, which is what used to make the transformer
    // mistake the whole group for a schema.
    // lang=json,strict
    private const string Specification = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["where"],
          "properties": {
            "where": { "$ref": "#/definitions/booleanReturning" }
          },
          "definitions": {
            "booleanReturning": {
              "oneOf": [
                { "$ref": "#/definitions/booleanOperations/and" },
                { "$ref": "#/definitions/booleanOperations/or" },
                { "$ref": "#/definitions/booleanOperations/not" }
              ]
            },
            "booleanOperations": {
              "and": { "type": "object", "properties": { "operator": { "const": "and" } } },
              "or": { "type": "object", "properties": { "operator": { "const": "or" } } },
              "not": { "type": "object", "properties": { "operator": { "const": "not" } } }
            }
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

    [Theory]
    [InlineData("booleanOperations_and")]
    [InlineData("booleanOperations_or")]
    [InlineData("booleanOperations_not")]
    public async Task EmitsMembersOfGroupNamedLikeSchemaKeyword(string name)
    {
        OpenApiDocument document = await TransformedDocument();

        Assert.Contains(name, document.Components!.Schemas!.Keys);
    }

    [Fact]
    public async Task DoesNotEmitGroupingNodeAsSchema()
    {
        OpenApiDocument document = await TransformedDocument();

        Assert.DoesNotContain("booleanOperations", document.Components!.Schemas!.Keys);
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

    [GeneratedRegex(""""\$ref":\s*"#/components/schemas/(?<name>[^"]+)"""")]
    private static partial Regex ComponentReference();
}
