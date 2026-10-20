# cmt-schema-gen

Generates a Configuration Migration Tool (CMT) `data_schema.xml` from live
Dataverse metadata, so `pac data export` can run without the CMT GUI.

Single C# file, no NuGet packages. Runs as a .NET 10 file-based app with
`UseAppHost=false`, so the build produces a DLL that `dotnet` loads and no
separate EXE.

## Requirements

- .NET 10 SDK
- A Dataverse user with read access to table metadata

## Usage

```
dotnet run cmt-schema-gen.cs -- --url https://<org>.crm9.dynamics.com --solution <SolutionUniqueName>
pac data export --schemaFile data_schema.xml --dataFile data.zip
```

| Option | Meaning |
|---|---|
| `--url <org url>` | Dataverse org URL (required) |
| `--entities a,b,c` | Table logical names |
| `--solution <uniquename>` | Add every table in the solution |
| `--out <path>` | Output file (default `data_schema.xml`) |
| `--exclude-fields a,b` | Drop these columns from every table |
| `--no-owner` | Drop `ownerid` (use when user GUIDs differ across environments) |
| `--all-m2m` | Include N:N relationships whose other table is not in the schema |
| `--disable-plugins` | Set `disableplugins="true"` on every entity |
| `--tenant <id\|domain>` | Entra tenant (default `organizations`) |
| `--client-id <guid>` | Public client app ID (default: Microsoft's Dataverse sample app) |

`--entities` and `--solution` can be combined.

## Authentication

Device code flow against `login.microsoftonline.com` (correct for commercial
and GCC Moderate). If Conditional Access blocks device code sign-in, set
`DATAVERSE_TOKEN` to a bearer token for the org and the tool skips sign-in.

## Field selection

Included: the primary key, and every column that is valid for create or
update and is not a shadow column (`AttributeOf`).

Excluded automatically: system audit columns (`createdon`, `modifiedby`,
`versionnumber`, ...), calculated and rollup columns, and shadow name columns.

Skipped with a warning: multi-select choice, file, image, and other types
without a known CMT schema type.

N:N relationships are written once, on `Entity1`, when both tables are in the
schema.

## Status

Tested against a mock Web API. Not yet verified against a schema produced by
the CMT GUI. Diff the output against a known-good CMT schema before relying
on it.
