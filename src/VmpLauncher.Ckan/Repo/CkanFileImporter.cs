using System.Text.Json;
using System.Text.Json.Nodes;
using CKAN;

namespace VmpLauncher.Ckan.Repo;

/// <summary>
/// Normalizes hand-made <c>.ckan</c> metapackages before handing them to CKAN.
/// CKAN v1.36.4's <c>CkanModule.CheckHealth</c> rejects a module with neither <c>kind</c> nor <c>download</c>;
/// the host's <c>VMP-Career-Locked.ckan</c> is exactly that shape.
/// </summary>
public static class CkanFileImporter
{
    public sealed record Result(CkanModule Module, IReadOnlyList<string> Warnings);

    public static Result Import(string ckanJson)
    {
        var warnings = new List<string>();
        var node = JsonNode.Parse(ckanJson)?.AsObject()
                   ?? throw new InvalidDataException("The .ckan file is not a JSON object.");

        var hasKind = node.ContainsKey("kind");
        var hasDownload = node.ContainsKey("download");
        var depends = node["depends"] as JsonArray;
        if (!hasKind && !hasDownload && depends is { Count: > 0 })
        {
            node["kind"] = "metapackage";
            warnings.Add("Added missing \"kind\": \"metapackage\" (no download, has depends).");
        }
        if (node["spec_version"] is null)
        {
            node["spec_version"] = "v1.6";
            warnings.Add("Added missing spec_version v1.6.");
        }
        if (node["license"] is null)
        {
            node["license"] = "unknown";
            warnings.Add("Added missing license \"unknown\".");
        }
        var module = CkanModule.FromJson(node.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        return new Result(module, warnings);
    }

    /// <summary>Exact version pins from a metapackage: entries of depends that carry a "version".</summary>
    public static IReadOnlyList<(string Identifier, string Version)> ExactPins(CkanModule metapackage)
        => (metapackage.depends ?? new List<RelationshipDescriptor>())
            .OfType<ModuleRelationshipDescriptor>()
            .Where(d => d.version is not null)
            .Select(d => (d.name, d.version!.ToString()))
            .ToList();
}
