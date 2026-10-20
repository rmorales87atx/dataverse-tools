#:property UseAppHost=false
#:property PublishAot=false
#:property Nullable=enable
#:property ImplicitUsings=enable

// cmt-schema-gen.cs
// Generates a Configuration Migration Tool (CMT) data_schema.xml from live
// Dataverse metadata, for use with `pac data export --schemaFile`.
//
// No NuGet packages. Runs under the approved dotnet.exe (UseAppHost=false),
// so no unapproved EXE is produced.
//
// Usage:
//   dotnet run cmt-schema-gen.cs -- --url https://org.crm9.dynamics.com --entities account,contact
//   dotnet run cmt-schema-gen.cs -- --url https://org.crm9.dynamics.com --solution MyConfigSolution
//
// Options:
//   --url <org url>            Dataverse org URL (required)
//   --entities <a,b,c>         Comma-separated table logical names
//   --solution <uniquename>    Add every table in this solution
//   --out <path>               Output file (default: data_schema.xml)
//   --exclude-fields <a,b>     Field logical names to drop from every table
//   --include-fields <a,b>     Field logical names to keep even if read-only (exclude wins)
//   --include-audit            Keep createdon/createdby/modifiedon/modifiedby (+ onbehalfby); import ignores them
//   --no-owner                 Drop ownerid (useful when users differ across environments)
//   --all-m2m                  Include N:N relationships even when the other table is not in the schema
//   --disable-plugins          Set disableplugins="true" on every entity
//   --tenant <id|domain>       Entra tenant (default: organizations)
//   --client-id <guid>         Public client app ID (default: Microsoft's Dataverse sample app)
//   --authority-host <url>     Entra host (default: https://login.microsoftonline.com; GCC High: https://login.microsoftonline.us)
//   --no-cache                 Do not read or save the sign-in cache
//   --logout                   Delete the sign-in cache and exit
//
// Auth: device code flow. The refresh token is saved per org/tenant/client so later
// runs sign in silently. On Windows the cache is encrypted with DPAPI (current user);
// elsewhere it is a 0600 file. Cache: %LOCALAPPDATA%\cmt-schema-gen\tokens.bin
// Set DATAVERSE_TOKEN to skip sign-in and use a bearer token you already have.

using System.ComponentModel;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace CmtSchemaGen;

internal static class Program {
    private const string DefaultClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d";

    public static async Task<int> Main(string[] args) {
        Options options;
        try {
            options = Options.Parse(args);
        } catch (ArgumentException ex) {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine("usage: dotnet run cmt-schema-gen.cs -- --url <org url> (--entities a,b | --solution name) [--out data_schema.xml]");
            return 2;
        }

        if (options.Logout) {
            TokenCache.Clear();
            Console.Error.WriteLine($"deleted sign-in cache: {TokenCache.FilePath}");
            return 0;
        }

        try {
            var token = Environment.GetEnvironmentVariable("DATAVERSE_TOKEN");
            if (string.IsNullOrWhiteSpace(token)) {
                var auth = new Authenticator(options.AuthorityHost, options.Tenant, options.ClientId ?? DefaultClientId, options.Url);
                token = await auth.GetAccessTokenAsync(options.UseCache);
            }

            using var api = new DataverseApi(options.Url, token);

            var logicalNames = new SortedSet<string>(options.Entities, StringComparer.OrdinalIgnoreCase);
            if (options.Solution is not null) {
                foreach (var name in await api.GetSolutionTableNamesAsync(options.Solution)) {
                    logicalNames.Add(name);
                }
            }

            if (logicalNames.Count == 0) {
                Console.Error.WriteLine("error: no tables selected. Pass --entities or --solution.");
                return 2;
            }

            var tables = new List<TableMeta>();
            foreach (var name in logicalNames) {
                Console.Error.WriteLine($"reading metadata: {name}");
                tables.Add(await api.GetTableAsync(name));
            }

            var schema = SchemaWriter.Build(tables, options, await ResolveM2MTargetKeysAsync(api, tables, options));
            var settings = new XmlWriterSettings { Indent = true, OmitXmlDeclaration = false, Encoding = new UTF8Encoding(false) };
            using (var writer = XmlWriter.Create(options.OutPath, settings)) {
                schema.Save(writer);
            }

            Console.Error.WriteLine($"wrote {options.OutPath} ({tables.Count} tables)");
            return 0;
        } catch (DataverseException ex) {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<Dictionary<string, string>> ResolveM2MTargetKeysAsync(DataverseApi api, List<TableMeta> tables, Options options) {
        // Primary key of each N:N target table. Tables already loaded need no extra call.
        var keys = tables.ToDictionary(t => t.LogicalName, t => t.PrimaryIdAttribute, StringComparer.OrdinalIgnoreCase);
        if (!options.AllM2M) {
            return keys;
        }

        foreach (var rel in tables.SelectMany(t => t.ManyToMany.Select(r => r.TargetFor(t.LogicalName)))) {
            if (!keys.ContainsKey(rel)) {
                keys[rel] = await api.GetPrimaryIdAttributeAsync(rel);
            }
        }

        return keys;
    }
}

internal sealed class Options {
    public string Url { get; private set; } = "";

    public List<string> Entities { get; } = [];

    public string? Solution { get; private set; }

    public string OutPath { get; private set; } = "data_schema.xml";

    public HashSet<string> ExcludeFields { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> IncludeFields { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IncludeAudit { get; private set; }

    public bool NoOwner { get; private set; }

    public bool AllM2M { get; private set; }

    public bool DisablePlugins { get; private set; }

    public string Tenant { get; private set; } = "organizations";

    public string? ClientId { get; private set; }

    public string AuthorityHost { get; private set; } = "https://login.microsoftonline.com";

    public bool UseCache { get; private set; } = true;

    public bool Logout { get; private set; }

    public static Options Parse(string[] args) {
        var o = new Options();
        for (var i = 0; i < args.Length; i++) {
            var arg = args[i];
            string Next() {
                if ((i + 1) >= args.Length) {
                    throw new ArgumentException($"{arg} needs a value");
                }

                return args[++i];
            }

            switch (arg) {
                case "--url":
                    o.Url = Next().TrimEnd('/');
                    break;
                case "--entities":
                    o.Entities.AddRange(SplitList(Next()));
                    break;
                case "--solution":
                    o.Solution = Next();
                    break;
                case "--out":
                    o.OutPath = Next();
                    break;
                case "--exclude-fields":
                    foreach (var f in SplitList(Next())) {
                        o.ExcludeFields.Add(f);
                    }

                    break;
                case "--include-fields":
                    foreach (var f in SplitList(Next())) {
                        o.IncludeFields.Add(f);
                    }

                    break;
                case "--include-audit":
                    o.IncludeAudit = true;
                    break;
                case "--no-owner":
                    o.NoOwner = true;
                    break;
                case "--all-m2m":
                    o.AllM2M = true;
                    break;
                case "--disable-plugins":
                    o.DisablePlugins = true;
                    break;
                case "--tenant":
                    o.Tenant = Next();
                    break;
                case "--client-id":
                    o.ClientId = Next();
                    break;
                case "--authority-host":
                    o.AuthorityHost = Next().TrimEnd('/');
                    break;
                case "--no-cache":
                    o.UseCache = false;
                    break;
                case "--logout":
                    o.Logout = true;
                    break;
                default:
                    throw new ArgumentException($"unknown option {arg}");
            }
        }

        if (string.IsNullOrWhiteSpace(o.Url) && !o.Logout) {
            throw new ArgumentException("--url is required");
        }

        return o;
    }

    private static IEnumerable<string> SplitList(string value) {
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant());
    }
}

internal sealed record TableMeta(
    string LogicalName,
    string DisplayName,
    int ObjectTypeCode,
    string PrimaryIdAttribute,
    string? PrimaryNameAttribute,
    List<AttributeMeta> Attributes,
    List<ManyToManyMeta> ManyToMany);

internal sealed record AttributeMeta(
    string LogicalName,
    string DisplayName,
    string AttributeType,
    string? AttributeTypeName,
    bool IsValidForCreate,
    bool IsValidForUpdate,
    bool IsCustom,
    bool IsPrimaryId,
    string? AttributeOf,
    List<string> Targets);

internal sealed record ManyToManyMeta(
    string SchemaName,
    string IntersectEntityName,
    string Entity1LogicalName,
    string Entity2LogicalName) {
    public bool IsReflexive => string.Equals(Entity1LogicalName, Entity2LogicalName, StringComparison.OrdinalIgnoreCase);

    public string TargetFor(string table) {
        return string.Equals(Entity1LogicalName, table, StringComparison.OrdinalIgnoreCase) ? Entity2LogicalName : Entity1LogicalName;
    }
}

internal static class SchemaWriter {
    // Dataverse AttributeType -> CMT schema field type.
    // Types not listed (Virtual, EntityName, ManagedProperty, CalendarRules,
    // File, Image, MultiSelectPicklist) are skipped and reported.
    private static readonly Dictionary<string, string> TypeMap = new(StringComparer.OrdinalIgnoreCase) {
        ["String"] = "string",
        ["Memo"] = "string",
        ["Uniqueidentifier"] = "guid",
        ["Lookup"] = "entityreference",
        ["Customer"] = "entityreference",
        ["Owner"] = "owner",
        ["PartyList"] = "partylist",
        ["Picklist"] = "optionsetvalue",
        ["State"] = "state",
        ["Status"] = "status",
        ["Boolean"] = "bool",
        ["DateTime"] = "datetime",
        ["Decimal"] = "decimal",
        ["Double"] = "float",
        ["Money"] = "money",
        ["Integer"] = "number",
        ["BigInt"] = "bigint",
    };

    public static XDocument Build(List<TableMeta> tables, Options options, Dictionary<string, string> primaryKeys) {
        var root = new XElement("entities");
        var inSchema = tables.Select(t => t.LogicalName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables) {
            root.Add(BuildEntity(table, options, primaryKeys, inSchema));
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
    }

    private static XElement BuildEntity(TableMeta table, Options options, Dictionary<string, string> primaryKeys, HashSet<string> inSchema) {
        var entity = new XElement("entity",
            new XAttribute("name", table.LogicalName),
            new XAttribute("displayname", table.DisplayName),
            new XAttribute("etc", table.ObjectTypeCode),
            new XAttribute("primaryidfield", table.PrimaryIdAttribute),
            new XAttribute("primarynamefield", table.PrimaryNameAttribute ?? ""),
            new XAttribute("disableplugins", options.DisablePlugins ? "true" : "false"));

        var fields = new XElement("fields");
        var skipped = new List<string>();
        foreach (var attr in table.Attributes.OrderBy(a => a.LogicalName, StringComparer.Ordinal)) {
            if (!Include(attr, options)) {
                continue;
            }

            if (!TypeMap.TryGetValue(attr.AttributeType, out var cmtType)) {
                skipped.Add($"{attr.LogicalName} ({attr.AttributeTypeName ?? attr.AttributeType})");
                continue;
            }

            var field = new XElement("field",
                new XAttribute("displayname", attr.DisplayName),
                new XAttribute("name", attr.LogicalName),
                new XAttribute("type", cmtType));

            if ((cmtType is "entityreference" or "owner" or "partylist") && (attr.Targets.Count > 0)) {
                field.Add(new XAttribute("lookupType", string.Join('|', attr.Targets)));
            }

            if (attr.IsCustom) {
                field.Add(new XAttribute("customfield", "true"));
            }

            if (attr.IsPrimaryId) {
                field.Add(new XAttribute("primaryKey", "true"));
            }

            fields.Add(field);
        }

        if (skipped.Count > 0) {
            Console.Error.WriteLine($"  {table.LogicalName}: skipped unsupported types: {string.Join(", ", skipped)}");
        }

        entity.Add(fields);

        var relationships = new XElement("relationships");
        foreach (var rel in table.ManyToMany.OrderBy(r => r.SchemaName, StringComparer.Ordinal)) {
            var target = rel.TargetFor(table.LogicalName);
            if (!primaryKeys.TryGetValue(target, out var targetKey)) {
                continue;
            }

            // When both tables are in the schema, list the N:N once (on Entity1) so the import does not associate twice.
            if (!string.Equals(rel.Entity1LogicalName, table.LogicalName, StringComparison.OrdinalIgnoreCase)
                && inSchema.Contains(rel.Entity1LogicalName)) {
                continue;
            }

            relationships.Add(new XElement("relationship",
                new XAttribute("name", rel.SchemaName),
                new XAttribute("manyToMany", "true"),
                new XAttribute("isreflexive", rel.IsReflexive ? "true" : "false"),
                new XAttribute("relatedEntityName", rel.IntersectEntityName),
                new XAttribute("m2mTargetEntity", target),
                new XAttribute("m2mTargetEntityPrimaryKey", targetKey)));
        }

        entity.Add(relationships);
        return entity;
    }

    // Read-only audit columns. Dataverse ignores them on import; overriddencreatedon
    // (writable, so included by default) is the column that sets the created date.
    private static readonly HashSet<string> AuditFields = new(StringComparer.OrdinalIgnoreCase) {
        "createdon",
        "createdby",
        "createdonbehalfby",
        "modifiedon",
        "modifiedby",
        "modifiedonbehalfby",
    };

    private static bool Include(AttributeMeta attr, Options options) {
        if (attr.IsPrimaryId) {
            return true;
        }

        if (options.ExcludeFields.Contains(attr.LogicalName)) {
            return false;
        }

        if (options.NoOwner && (attr.LogicalName == "ownerid")) {
            return false;
        }

        if (options.IncludeFields.Contains(attr.LogicalName)) {
            return true;
        }

        if (options.IncludeAudit && AuditFields.Contains(attr.LogicalName)) {
            return true;
        }

        if (attr.AttributeOf is not null) {
            return false; // yomi names, lookup name shadows, and similar
        }

        if (!attr.IsValidForCreate && !attr.IsValidForUpdate) {
            return false; // createdon, modifiedby, versionnumber, rollups, calculated
        }

        return true;
    }
}

internal sealed class DataverseException(string message) : Exception(message);

internal sealed class DataverseApi : IDisposable {
    private readonly HttpClient _http;

    public DataverseApi(string orgUrl, string token) {
        _http = new HttpClient { BaseAddress = new Uri($"{orgUrl}/api/data/v9.2/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.Add("OData-MaxVersion", "4.0");
        _http.DefaultRequestHeaders.Add("OData-Version", "4.0");
    }

    public void Dispose() {
        _http.Dispose();
    }

    public async Task<TableMeta> GetTableAsync(string logicalName) {
        var path = $"EntityDefinitions(LogicalName='{logicalName}')"
            + "?$select=LogicalName,DisplayName,ObjectTypeCode,PrimaryIdAttribute,PrimaryNameAttribute"
            + "&$expand=Attributes($select=LogicalName,DisplayName,AttributeType,AttributeTypeName,IsValidForCreate,IsValidForUpdate,IsCustomAttribute,IsPrimaryId,AttributeOf),"
            + "ManyToManyRelationships($select=SchemaName,IntersectEntityName,Entity1LogicalName,Entity2LogicalName)";

        using var doc = await GetJsonAsync(path);
        var e = doc.RootElement;

        var targets = await GetLookupTargetsAsync(logicalName);

        var attributes = e.GetProperty("Attributes").EnumerateArray().Select(a => {
            var name = a.GetProperty("LogicalName").GetString()!;
            return new AttributeMeta(
                name,
                Label(a, "DisplayName") ?? name,
                a.GetProperty("AttributeType").GetString() ?? "",
                a.TryGetProperty("AttributeTypeName", out var tn) && tn.ValueKind == JsonValueKind.Object ? tn.GetProperty("Value").GetString() : null,
                ManagedBool(a, "IsValidForCreate"),
                ManagedBool(a, "IsValidForUpdate"),
                ManagedBool(a, "IsCustomAttribute"),
                ManagedBool(a, "IsPrimaryId"),
                a.TryGetProperty("AttributeOf", out var of) && of.ValueKind == JsonValueKind.String ? of.GetString() : null,
                targets.TryGetValue(name, out var t) ? t : []);
        }).ToList();

        var m2m = e.GetProperty("ManyToManyRelationships").EnumerateArray().Select(r => new ManyToManyMeta(
            r.GetProperty("SchemaName").GetString()!,
            r.GetProperty("IntersectEntityName").GetString()!,
            r.GetProperty("Entity1LogicalName").GetString()!,
            r.GetProperty("Entity2LogicalName").GetString()!)).ToList();

        return new TableMeta(
            e.GetProperty("LogicalName").GetString()!,
            Label(e, "DisplayName") ?? logicalName,
            e.GetProperty("ObjectTypeCode").GetInt32(),
            e.GetProperty("PrimaryIdAttribute").GetString()!,
            e.TryGetProperty("PrimaryNameAttribute", out var pn) && pn.ValueKind == JsonValueKind.String ? pn.GetString() : null,
            attributes,
            m2m);
    }

    public async Task<string> GetPrimaryIdAttributeAsync(string logicalName) {
        using var doc = await GetJsonAsync($"EntityDefinitions(LogicalName='{logicalName}')?$select=PrimaryIdAttribute");
        return doc.RootElement.GetProperty("PrimaryIdAttribute").GetString()!;
    }

    public async Task<List<string>> GetSolutionTableNamesAsync(string uniqueName) {
        var escaped = uniqueName.Replace("'", "''");
        using var sol = await GetJsonAsync($"solutions?$select=solutionid&$filter=uniquename eq '{escaped}'");
        var rows = sol.RootElement.GetProperty("value");
        if (rows.GetArrayLength() == 0) {
            throw new DataverseException($"solution '{uniqueName}' not found");
        }

        var solutionId = rows[0].GetProperty("solutionid").GetString();
        using var comps = await GetJsonAsync($"solutioncomponents?$select=objectid&$filter=_solutionid_value eq {solutionId} and componenttype eq 1");

        var names = new List<string>();
        foreach (var c in comps.RootElement.GetProperty("value").EnumerateArray()) {
            var metadataId = c.GetProperty("objectid").GetString();
            using var def = await GetJsonAsync($"EntityDefinitions({metadataId})?$select=LogicalName");
            names.Add(def.RootElement.GetProperty("LogicalName").GetString()!);
        }

        Console.Error.WriteLine($"solution {uniqueName}: {names.Count} tables");
        return names;
    }

    private async Task<Dictionary<string, List<string>>> GetLookupTargetsAsync(string logicalName) {
        // Targets live only on LookupAttributeMetadata (Lookup, Customer, Owner, PartyList all derive from it).
        using var doc = await GetJsonAsync(
            $"EntityDefinitions(LogicalName='{logicalName}')/Attributes/Microsoft.Dynamics.CRM.LookupAttributeMetadata?$select=LogicalName,Targets");

        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in doc.RootElement.GetProperty("value").EnumerateArray()) {
            var targets = a.TryGetProperty("Targets", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Select(x => x.GetString()!).ToList()
                : [];
            result[a.GetProperty("LogicalName").GetString()!] = targets;
        }

        return result;
    }

    private async Task<JsonDocument> GetJsonAsync(string path) {
        using var response = await _http.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) {
            var detail = TryGetODataError(body) ?? body;
            throw new DataverseException($"{(int)response.StatusCode} {response.StatusCode} on {path}: {detail}");
        }

        return JsonDocument.Parse(body);
    }

    private static string? TryGetODataError(string body) {
        try {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("error").GetProperty("message").GetString();
        } catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) {
            return null;
        }
    }

    private static string? Label(JsonElement e, string property) {
        if (!e.TryGetProperty(property, out var label) || (label.ValueKind != JsonValueKind.Object)) {
            return null;
        }

        if (label.TryGetProperty("UserLocalizedLabel", out var ul) && (ul.ValueKind == JsonValueKind.Object)) {
            return ul.GetProperty("Label").GetString();
        }

        return null;
    }

    private static bool ManagedBool(JsonElement e, string property) {
        if (!e.TryGetProperty(property, out var v)) {
            return false;
        }

        return v.ValueKind switch {
            JsonValueKind.True => true,
            JsonValueKind.Object => v.TryGetProperty("Value", out var inner) && (inner.ValueKind == JsonValueKind.True),
            _ => false,
        };
    }
}

internal sealed record TokenResult(string AccessToken, string? RefreshToken);

internal sealed class Authenticator {
    private readonly string _endpoint;
    private readonly string _clientId;
    private readonly string _scope;
    private readonly string _cacheKey;

    public Authenticator(string authorityHost, string tenant, string clientId, string orgUrl) {
        _endpoint = $"{authorityHost}/{tenant}/oauth2/v2.0";
        _clientId = clientId;
        _scope = $"{orgUrl}/user_impersonation offline_access";
        _cacheKey = $"{authorityHost}|{tenant}|{clientId}|{orgUrl}".ToLowerInvariant();
    }

    public async Task<string> GetAccessTokenAsync(bool useCache) {
        using var http = new HttpClient();

        if (useCache) {
            var refreshToken = TokenCache.Read(_cacheKey);
            if (refreshToken is not null) {
                var (result, error) = await RedeemAsync(http, new Dictionary<string, string> {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = _clientId,
                    ["refresh_token"] = refreshToken,
                    ["scope"] = _scope,
                });

                if (result is not null) {
                    // Entra rotates refresh tokens; save the new one so the sliding window keeps moving.
                    TokenCache.Write(_cacheKey, result.RefreshToken ?? refreshToken);
                    return result.AccessToken;
                }

                Console.Error.WriteLine($"saved sign-in not accepted ({error}); signing in again");
                TokenCache.Remove(_cacheKey);
            }
        }

        var fresh = await DeviceCodeAsync(http);
        if (useCache && (fresh.RefreshToken is not null)) {
            TokenCache.Write(_cacheKey, fresh.RefreshToken);
        }

        return fresh.AccessToken;
    }

    private async Task<TokenResult> DeviceCodeAsync(HttpClient http) {
        using var codeResponse = await http.PostAsync($"{_endpoint}/devicecode", new FormUrlEncodedContent(new Dictionary<string, string> {
            ["client_id"] = _clientId,
            ["scope"] = _scope,
        }));
        using var codeDoc = JsonDocument.Parse(await codeResponse.Content.ReadAsStringAsync());
        var code = codeDoc.RootElement;
        if (!codeResponse.IsSuccessStatusCode) {
            throw new DataverseException($"device code request failed: {code.GetProperty("error_description").GetString()}");
        }

        Console.Error.WriteLine(code.GetProperty("message").GetString());

        var deviceCode = code.GetProperty("device_code").GetString()!;
        var interval = code.GetProperty("interval").GetInt32();
        var deadline = DateTime.UtcNow.AddSeconds(code.GetProperty("expires_in").GetInt32());

        while (DateTime.UtcNow < deadline) {
            await Task.Delay(TimeSpan.FromSeconds(interval));

            var (result, error) = await RedeemAsync(http, new Dictionary<string, string> {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = _clientId,
                ["device_code"] = deviceCode,
            });

            if (result is not null) {
                return result;
            }

            if (error == "authorization_pending") {
                continue;
            }

            if (error == "slow_down") {
                interval += 5;
                continue;
            }

            throw new DataverseException($"sign-in failed: {error}");
        }

        throw new DataverseException("sign-in timed out");
    }

    private async Task<(TokenResult? Result, string? Error)> RedeemAsync(HttpClient http, Dictionary<string, string> form) {
        using var response = await http.PostAsync($"{_endpoint}/token", new FormUrlEncodedContent(form));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var t = doc.RootElement;

        if (response.IsSuccessStatusCode) {
            var refresh = t.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
            return (new TokenResult(t.GetProperty("access_token").GetString()!, refresh), null);
        }

        var error = t.TryGetProperty("error", out var e) ? e.GetString() : "unknown_error";
        if ((error is not "authorization_pending" and not "slow_down") && t.TryGetProperty("error_description", out var d)) {
            error = $"{error}: {FirstLine(d.GetString())}";
        }

        return (null, error);
    }

    private static string FirstLine(string? text) {
        return (text ?? "").Split('\n', 2)[0].Trim();
    }
}

internal static class TokenCache {
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "cmt-schema-gen",
        "tokens.bin");

    public static string? Read(string key) {
        return Load().TryGetValue(key, out var value) ? value : null;
    }

    public static void Write(string key, string refreshToken) {
        var entries = Load();
        entries[key] = refreshToken;
        Save(entries);
    }

    public static void Remove(string key) {
        var entries = Load();
        if (entries.Remove(key)) {
            Save(entries);
        }
    }

    public static void Clear() {
        if (File.Exists(FilePath)) {
            File.Delete(FilePath);
        }
    }

    private static Dictionary<string, string> Load() {
        if (!File.Exists(FilePath)) {
            return [];
        }

        try {
            var bytes = File.ReadAllBytes(FilePath);
            var json = OperatingSystem.IsWindows() ? Dpapi.Unprotect(bytes) : bytes;
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        } catch (Exception ex) when (ex is JsonException or Win32Exception or IOException) {
            // Unreadable cache (another user, another machine, or corrupt): start over.
            Console.Error.WriteLine($"ignoring unreadable sign-in cache: {ex.Message}");
            return [];
        }
    }

    private static void Save(Dictionary<string, string> entries) {
        var json = JsonSerializer.SerializeToUtf8Bytes(entries);
        var bytes = OperatingSystem.IsWindows() ? Dpapi.Protect(json) : json;

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        var fileOptions = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) {
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temp, fileOptions)) {
            stream.Write(bytes);
        }

        File.Move(temp, FilePath, overwrite: true);
    }
}

[SupportedOSPlatform("windows")]
internal static class Dpapi {
    private const int CryptProtectUiForbidden = 0x1;

    public static byte[] Protect(byte[] data) {
        return Transform(data, protect: true);
    }

    public static byte[] Unprotect(byte[] data) {
        return Transform(data, protect: false);
    }

    private static byte[] Transform(byte[] data, bool protect) {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try {
            var input = new DataBlob(data.Length, handle.AddrOfPinnedObject());
            var output = new DataBlob(0, IntPtr.Zero);
            var ok = protect
                ? CryptProtectData(ref input, "cmt-schema-gen", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output);
            if (!ok) {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try {
                var result = new byte[output.Length];
                Marshal.Copy(output.Pointer, result, 0, output.Length);
                return result;
            } finally {
                LocalFree(output.Pointer);
            }
        } finally {
            handle.Free();
        }
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob {
        private int _length;
        private IntPtr _pointer;

        public DataBlob(int length, IntPtr pointer) {
            _length = length;
            _pointer = pointer;
        }

        public readonly int Length => _length;

        public readonly IntPtr Pointer => _pointer;
    }
}
