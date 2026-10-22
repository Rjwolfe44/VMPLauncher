using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace VmpLauncher.Game;

/// <summary>What the installed VMP client reports about itself, read from the DLL's metadata without loading it.</summary>
public sealed record LmpClientInfo(string Path, string? InformationalVersion, string? AssemblyVersion, IReadOnlyDictionary<string, string> Metadata)
{
    /// <summary>The exact build string the server compares at handshake (AssemblyInformationalVersion).</summary>
    public string? ExactBuild => InformationalVersion;

    public bool SupportsAutoJoin => Metadata.TryGetValue("VmpLauncherFeatures", out var f) && f.Split(';', ',').Any(x => x.Trim().Equals("autojoin=1", StringComparison.OrdinalIgnoreCase));
}

public static class LmpClientInspector
{
    public static string DllPathFor(string gameDir) => System.IO.Path.Combine(gameDir, "GameData", "VladMultiplayer", "Plugins", "LmpClient.dll");

    public static LmpClientInfo? Inspect(string gameDir)
    {
        var path = DllPathFor(gameDir);
        return File.Exists(path) ? InspectDll(path) : null;
    }

    public static LmpClientInfo InspectDll(string dllPath)
    {
        using var fs = File.OpenRead(dllPath);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();
        var asm = md.GetAssemblyDefinition();
        string? info = null;
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var handle in asm.GetCustomAttributes())
        {
            var attr = md.GetCustomAttribute(handle);
            var name = AttributeTypeName(md, attr);
            if (name == "System.Reflection.AssemblyInformationalVersionAttribute")
            {
                info = ReadSingleString(md, attr);
            }
            else if (name == "System.Reflection.AssemblyMetadataAttribute")
            {
                var (k, v) = ReadTwoStrings(md, attr);
                if (k is not null)
                {
                    metadata[k] = v ?? string.Empty;
                }
            }
        }
        return new LmpClientInfo(dllPath, info, asm.Version.ToString(), metadata);
    }

    private static string? AttributeTypeName(MetadataReader md, CustomAttribute attr)
    {
        EntityHandle typeHandle;
        switch (attr.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                typeHandle = md.GetMemberReference((MemberReferenceHandle)attr.Constructor).Parent;
                break;
            case HandleKind.MethodDefinition:
                typeHandle = md.GetMethodDefinition((MethodDefinitionHandle)attr.Constructor).GetDeclaringType();
                break;
            default:
                return null;
        }
        return typeHandle.Kind switch
        {
            HandleKind.TypeReference => FullName(md, md.GetTypeReference((TypeReferenceHandle)typeHandle)),
            HandleKind.TypeDefinition => FullName(md, md.GetTypeDefinition((TypeDefinitionHandle)typeHandle)),
            _ => null,
        };
    }

    private static string FullName(MetadataReader md, TypeReference t) => $"{md.GetString(t.Namespace)}.{md.GetString(t.Name)}";
    private static string FullName(MetadataReader md, TypeDefinition t) => $"{md.GetString(t.Namespace)}.{md.GetString(t.Name)}";

    private static string? ReadSingleString(MetadataReader md, CustomAttribute attr)
    {
        var reader = md.GetBlobReader(attr.Value);
        if (reader.ReadUInt16() != 1)
        {
            return null;
        }
        return reader.ReadSerializedString();
    }

    private static (string?, string?) ReadTwoStrings(MetadataReader md, CustomAttribute attr)
    {
        var reader = md.GetBlobReader(attr.Value);
        if (reader.ReadUInt16() != 1)
        {
            return (null, null);
        }
        var a = reader.ReadSerializedString();
        var b = reader.ReadSerializedString();
        return (a, b);
    }
}
