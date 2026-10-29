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

    /// <summary>
    /// The installed client, or null when it is missing <b>or unreadable</b> (truncated/corrupt/locked DLL). Never throws
    /// for a bad file; use <see cref="Inspect(string, out string?)"/> to tell "missing" from "unreadable".
    /// </summary>
    public static LmpClientInfo? Inspect(string gameDir) => Inspect(gameDir, out _);

    /// <summary>
    /// Like <see cref="Inspect(string)"/>, and <paramref name="unreadableReason"/> is non-null only when the DLL exists
    /// but could not be read (null result + null reason = not installed).
    /// </summary>
    public static LmpClientInfo? Inspect(string gameDir, out string? unreadableReason)
    {
        unreadableReason = null;
        var path = DllPathFor(gameDir);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return InspectDll(path);
        }
        catch (Exception e) when (IsUnreadableImage(e))
        {
            unreadableReason = $"{System.IO.Path.GetFileName(path)} could not be read: {e.Message}";
            return null;
        }
    }

    /// <summary>Reads one assembly. Throws <see cref="BadImageFormatException"/>, <see cref="InvalidOperationException"/> or <see cref="IOException"/> for a truncated or non-.NET file.</summary>
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

    private static bool IsUnreadableImage(Exception e)
        => e is BadImageFormatException or InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException;

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
