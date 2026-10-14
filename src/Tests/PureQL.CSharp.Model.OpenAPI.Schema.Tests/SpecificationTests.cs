using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.OpenApi;

namespace PureQL.CSharp.Model.OpenAPI.Schema.Tests;

/// <summary>
/// Transforms PureQL specification 0.1.0-preview.2.0.1, from the
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

    [GeneratedRegex("^[a-zA-Z0-9.\\-_]+$")]
    private static partial Regex ComponentName();

    [GeneratedRegex(""""\$ref":\s*"#/components/schemas/(?<name>[^"]+)"""")]
    private static partial Regex ComponentReference();

    [GeneratedRegex(""""\$ref":\s*"(?!#/components/schemas/)"""")]
    private static partial Regex ForeignReference();

    [GeneratedRegex(@"""const"":\s*""(True|False)""")]
    private static partial Regex StringifiedBoolean();
}
