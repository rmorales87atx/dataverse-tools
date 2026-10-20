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
| `--exclude-fields a,b` | Drop these columns from every table (wins over any include) |
| `--include-fields a,b` | Keep these columns even when read-only |
| `--include-audit` | Keep `createdon`, `createdby`, `modifiedon`, `modifiedby` and the `...onbehalfby` columns |
| `--no-owner` | Drop `ownerid` (use when user GUIDs differ across environments) |
| `--all-m2m` | Include N:N relationships whose other table is not in the schema |
| `--disable-plugins` | Set `disableplugins="true"` on every entity |
| `--tenant <id\|domain>` | Entra tenant (default `organizations`) |
| `--client-id <guid>` | Public client app ID (default: Microsoft's Dataverse sample app) |
| `--authority-host <url>` | Entra host (default `https://login.microsoftonline.com`) |
| `--no-cache` | Do not read or save the sign-in cache |
| `--logout` | Delete the sign-in cache and exit |

`--entities` and `--solution` can be combined.

## Authentication

Device code flow against `login.microsoftonline.com` (correct for commercial
and GCC Moderate; use `--authority-host https://login.microsoftonline.us` for
GCC High). If Conditional Access blocks device code sign-in, set
`DATAVERSE_TOKEN` to a bearer token for the org and the tool skips sign-in.

### Sign-in cache

After the first device code sign-in, the refresh token is saved and later runs
sign in silently. The token is rotated on every run, so regular use keeps it
alive until your tenant's policy forces a new sign-in.

- Location: `%LOCALAPPDATA%\cmt-schema-gen\tokens.bin` (`~/.local/share/...` on Linux/macOS)
- Windows: encrypted with DPAPI, current-user scope. Only your Windows account on that machine can read it.
- Linux/macOS: plain JSON with file mode `0600`.
- One entry per authority host + tenant + client ID + org URL.
- An expired, revoked, or unreadable entry is discarded and you get a device code prompt again.

| Option | Meaning |
|---|---|
| `--no-cache` | Do not read or save the cache for this run |
| `--logout` | Delete the cache file and exit (no `--url` needed) |

The cache holds a credential. Delete it with `--logout` on any shared or
temporary machine.

## Field selection

Included: the primary key, and every column that is valid for create or
update and is not a shadow column (`AttributeOf`).

Excluded automatically: system audit columns (`createdon`, `modifiedby`,
`versionnumber`, ...), calculated and rollup columns, and shadow name columns.
Use `--include-audit` or `--include-fields` to keep them in the export.

Dataverse ignores audit columns on import: the target records get the import
user and the import time. To keep the original created date, the data must
carry `overriddencreatedon`, which is writable and included by default.
`createdby`, `modifiedon`, and `modifiedby` cannot be preserved through the
Web API.

Skipped with a warning: multi-select choice, file, image, and other types
without a known CMT schema type.

N:N relationships are written once, on `Entity1`, when both tables are in the
schema.

## Status

Schema generation used successfully against a real GCC Moderate environment.
Not yet diffed against a schema produced by the CMT GUI.

The sign-in cache is tested against a mock token endpoint (silent refresh,
token rotation, revoked token, corrupt cache, logout). The DPAPI path is
Windows-only and is not covered by those tests.
