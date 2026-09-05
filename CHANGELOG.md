# Changelog

All notable changes to PureQL.CSharp.Model.OpenAPI.Schema are documented here.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [0.1.0-preview.0.2.2] — 2026-08-21

### Fixed

- `PureQLQueryDocumentTransformer` no longer mistakes a grouping node in the
  PureQL JSON Schema for a schema of its own. Group names (e.g.
  `booleanOperations`, holding `and`/`or`/`not`) share a namespace with JSON
  Schema keywords, so a group whose only conflicting child was named `not`
  had its members silently dropped and left the `$ref`s pointing at them
  dangling.
- `PureQLQueryDocumentTransformer` no longer throws `NullReferenceException`
  when transforming a document that has no generated schemas yet.

## [0.1.0-preview.0.2.1] — 2026-08-21

- Maintenance release: dependency and build updates.

## [0.1.0-preview.0.2.0] — 2026-08-13

- Maintenance release: dependency and build updates.

## [0.1.0-preview.0.1.3] — 2026-08-12

- Maintenance release: dependency and build updates.

## [0.1.0-preview.0.1.2] — 2026-08-06

### Fixed

- Pinned `Microsoft.OpenApi` to 2.11.0, fixing a vulnerability
  (GHSA-v5pm-xwqc-g5wc / NU1903) reachable through the lower version that
  `Microsoft.AspNetCore.OpenApi` resolves by default.

## [0.1.0-preview.0.1.1] — 2026-06-10

- Maintenance release: dependency and build updates.

## [0.1.0-preview.0.1.0] — 2026-05-13

### Added

- Initial release: `PureQLQueryDocumentTransformer`, an
  `IOpenApiDocumentTransformer` that replaces the auto-generated `Query`
  schema in ASP.NET Core OpenAPI documents with the canonical PureQL JSON
  Schema, so API consumers see the correct query type schema.
- Targets .NET 10.0.
