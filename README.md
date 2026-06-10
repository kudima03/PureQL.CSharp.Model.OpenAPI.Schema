# PureQL.CSharp.Model.OpenAPI.Schema

OpenAPI document transformer for **PureQL** — injects the full PureQL JSON Schema into ASP.NET Core OpenAPI documents so API consumers see correct, complete query type schemas.

[![.NET build & test](https://github.com/kudima03/PureQL.CSharp.Model.OpenAPI.Schema/actions/workflows/build-and-test.yml/badge.svg?branch=main)](https://github.com/kudima03/PureQL.CSharp.Model.OpenAPI.Schema/actions/workflows/build-and-test.yml)
[![Build and Deploy](https://github.com/kudima03/PureQL.CSharp.Model.OpenAPI.Schema/actions/workflows/publish-nuget.yml/badge.svg?branch=main)](https://github.com/kudima03/PureQL.CSharp.Model.OpenAPI.Schema/actions/workflows/publish-nuget.yml)
[![NuGet](https://img.shields.io/nuget/v/PureQL.CSharp.Model.OpenAPI.Schema)](https://www.nuget.org/packages/PureQL.CSharp.Model.OpenAPI.Schema)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

## Overview

When ASP.NET Core generates an OpenAPI document for endpoints that accept PureQL `Query` objects, the generated schema reflects .NET's type-system view of the C# model — not the actual JSON structure that PureQL consumers need to send. `PureQL.CSharp.Model.OpenAPI.Schema` provides `PureQLQueryDocumentTransformer`, an `IOpenApiDocumentTransformer` that replaces the auto-generated schema with the canonical PureQL JSON Schema.

The transformer parses the PureQL JSON Schema, flattens its nested `definitions` tree into a set of named components, rewrites all `$ref` pointers from `#/definitions/...` to `#/components/schemas/...`, and injects the resulting schemas into the document's `components.schemas` section.

## API

| Type | Kind | Description |
|------|------|-------------|
| `PureQLQueryDocumentTransformer` | `sealed record` | Implements `IOpenApiDocumentTransformer`. Accepts the raw PureQL JSON Schema string in its constructor and replaces the `Query` schema component on transform. |

### Registration

```csharp
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer(
        new PureQLQueryDocumentTransformer(pureqlJsonSchema));
});
```

## Design Principles

- **Schema-driven** — the transformer accepts the JSON Schema string directly, keeping it decoupled from any specific PureQL version.
- **Non-destructive** — only the `Query` schema and its referenced definitions are modified; all other schemas in the document are preserved.

## Dependencies

- [`PureQL.CSharp.Model`](https://github.com/kudima03/PureQL.CSharp.Model) — PureQL C# AST types whose schema this transformer corrects
- `Microsoft.AspNetCore.OpenApi` — `IOpenApiDocumentTransformer` interface
