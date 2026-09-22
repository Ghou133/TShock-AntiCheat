using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace AntiCheat.Plugin.TShock;

public sealed record TargetRuntimeStatus(bool Verified, string GameVersion, string Fingerprint, string Reason);
public sealed record LoadedImageEvidence(string Assembly, string DiskPath, string DiskSha256,
    string LoadedLocation, Guid LoadedMvid, string Comparison, int Methods, int Resources, int RvaFields);

/// <summary>
/// Acceptance of the audited development candidate. This is not production rule qualification and
/// does not claim to hash JIT/native memory. Local host administrators and plugins remain trusted.
/// </summary>
public static class TargetRuntime
{
    public const string TargetVersion = "1.4.5.8";
    public const int ProtocolVersion = 326;
    public const string AcceptedClrVersion = "9.0.7";
    public const string TShockCommit = "9508768863c24ae9088e947ad2c24a8383341558";
    public const string TsApiCommit = "d4add121c2463fe4dffe12ca7b20a4f057ea66b8";
    public const string TShockSha256 = "9704798C992623E09C847EA3DE1D2BC50B74F2F584A58CB405557C5B0DA7219F";
    // Fixed commits plus preserved M7 diagnostics/M10 admission and the explicitly received
    // M12 receive-ownership and native liquid-batching adapter; exact migration is in the lock.
    public const string TsApiSha256 = "78C5A8B18D36C6C3CE6B8FFD016FB87FE407686845D0EEC363EC5ECAEF85C739";
    public const string OtApiSha256 = "C7FF9B092B296C86E1EA91B9BA8426E9F4955B4FA62DB7EA7FCFAE2EE3D1D511";
    public const string OtApiRuntimeSha256 = "E67962545DBB6392C00F561433594352215345644321D05954115E8D27080046";
    public const string LauncherSha256 = "F4A6AC8EF6847A9210E7E2C325484D50548B1C812B9E1F4781CA20FA394B0C62";
    public const string Fingerprint = "tshock1458:" + TShockSha256 + ":" + TsApiSha256 + ":" + OtApiSha256;
    public static IReadOnlyList<LoadedImageEvidence> LastEvidence { get; private set; } = Array.Empty<LoadedImageEvidence>();

    public static TargetRuntimeStatus Inspect()
    {
        var evidence = new List<LoadedImageEvidence>(3);
        try
        {
            if (!AcceptsHostRuntime(Environment.Version, OperatingSystem.IsWindows(), RuntimeInformation.ProcessArchitecture))
                return new(false, Main.versionNumber, Fingerprint, "unverified-host-runtime:" + Environment.Version + ":" + RuntimeInformation.ProcessArchitecture);
            if (Main.versionNumber.TrimStart('v') != TargetVersion)
                return new(false, Main.versionNumber, Fingerprint, "game-version-mismatch");
            // Core assemblies are loaded from actual paths. TShockAPI is loaded from bytes by
            // the audited TSAPI loader, so its complete managed image content is checked below.
            InspectLocated(typeof(Main).Assembly, OtApiSha256, evidence);
            InspectLocated(typeof(TerrariaPlugin).Assembly, TsApiSha256, evidence);
            var tshock = typeof(TSPlayer).Assembly;
            if (string.IsNullOrEmpty(tshock.Location))
                InspectByteLoaded(tshock, Path.Combine(AppContext.BaseDirectory, "ServerPlugins", "TShockAPI.dll"), TShockSha256, evidence);
            else InspectLocated(tshock, TShockSha256, evidence);
            LastEvidence = evidence.AsReadOnly();
            return new(true, Main.versionNumber, Fingerprint, "locked-candidate-managed-identity-verified");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or NotSupportedException or BadImageFormatException or TypeLoadException or ReflectionTypeLoadException)
        {
            LastEvidence = evidence.AsReadOnly();
            return new(false, Main.versionNumber, Fingerprint, "loaded-identity-unverified:" + ex.Message);
        }
    }

    public static bool AcceptsHostRuntime(Version version, bool isWindows, Architecture architecture) =>
        version == new Version(AcceptedClrVersion) && isWindows && architecture == Architecture.X64;

    private static unsafe void InspectLocated(Assembly assembly, string expected, List<LoadedImageEvidence> evidence)
    {
        if (string.IsNullOrEmpty(assembly.Location)) throw new InvalidOperationException(assembly.GetName().Name + ":missing-loaded-path");
        using var stream = File.OpenRead(assembly.Location);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (actual != expected) throw new InvalidOperationException(assembly.GetName().Name + ":loaded-path-hash-mismatch");
        stream.Position = 0;
        using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
        if (!assembly.TryGetRawMetadata(out byte* pointer, out int length) || pointer == null || length <= 0 || length > 64 * 1024 * 1024 ||
            !new ReadOnlySpan<byte>(pointer, length).SequenceEqual(pe.GetMetadata().GetContent().AsSpan()))
            throw new InvalidOperationException(assembly.GetName().Name + ":loaded-path-metadata-mismatch");
        // The path is genuinely reported by the loaded assembly; do not substitute neighboring DLLs.
        evidence.Add(new(assembly.GetName().Name!, assembly.Location, actual, assembly.Location,
            assembly.ManifestModule.ModuleVersionId, "loaded-path-sha256+loaded-raw-metadata", 0, 0, 0));
    }

    private static unsafe void InspectByteLoaded(Assembly assembly, string path, string expected,
        List<LoadedImageEvidence> evidence)
    {
        var bytes = File.ReadAllBytes(path);
        var actual = Convert.ToHexString(SHA256.HashData(bytes));
        if (actual != expected) throw new InvalidOperationException("TShockAPI:reference-image-hash-mismatch");
        using var pe = new PEReader(new MemoryStream(bytes, writable: false));
        var metadata = pe.GetMetadataReader();
        if (!assembly.TryGetRawMetadata(out byte* pointer, out int length) || pointer == null || length <= 0 || length > 64 * 1024 * 1024)
            throw new InvalidOperationException("TShockAPI:loaded-metadata-unavailable");
        if (!new ReadOnlySpan<byte>(pointer, length).SequenceEqual(pe.GetMetadata().GetContent().AsSpan()))
            throw new InvalidOperationException("TShockAPI:loaded-metadata-mismatch");
        var module = assembly.ManifestModule;
        if (module.ModuleVersionId != metadata.GetGuid(metadata.GetModuleDefinition().Mvid))
            throw new InvalidOperationException("TShockAPI:loaded-mvid-mismatch");
        int methods = 0, resources = 0, rvaFields = 0;
        foreach (var handle in metadata.MethodDefinitions)
        {
            var definition = metadata.GetMethodDefinition(handle);
            var method = module.ResolveMethod(MetadataTokens.GetToken(handle))
                ?? throw new InvalidOperationException("TShockAPI:method-resolution-incomplete");
            var loaded = method.GetMethodBody();
            if (definition.RelativeVirtualAddress == 0)
            {
                if (loaded is not null) throw new InvalidOperationException("TShockAPI:unexpected-method-body");
                continue;
            }
            var disk = pe.GetMethodBody(definition.RelativeVirtualAddress);
            if (loaded is null || !(loaded.GetILAsByteArray() ?? Array.Empty<byte>()).AsSpan().SequenceEqual(disk.GetILContent().AsSpan()) ||
                loaded.MaxStackSize != disk.MaxStack || loaded.InitLocals != disk.LocalVariablesInitialized ||
                loaded.LocalSignatureMetadataToken != (disk.LocalSignature.IsNil ? 0 : MetadataTokens.GetToken(disk.LocalSignature)) ||
                loaded.ExceptionHandlingClauses.Count != disk.ExceptionRegions.Length)
                throw new InvalidOperationException("TShockAPI:method-body-mismatch:" + MetadataTokens.GetToken(handle));
            for (int i = 0; i < disk.ExceptionRegions.Length; i++)
            {
                var left = loaded.ExceptionHandlingClauses[i];
                var right = disk.ExceptionRegions[i];
                if ((int)left.Flags != (int)right.Kind || left.TryOffset != right.TryOffset || left.TryLength != right.TryLength ||
                    left.HandlerOffset != right.HandlerOffset || left.HandlerLength != right.HandlerLength ||
                    (right.Kind == ExceptionRegionKind.Filter && left.FilterOffset != right.FilterOffset) ||
                    (right.Kind == ExceptionRegionKind.Catch && left.CatchType != module.ResolveType(MetadataTokens.GetToken(right.CatchType))))
                    throw new InvalidOperationException("TShockAPI:exception-region-mismatch");
            }
            methods++;
        }
        foreach (var handle in metadata.FieldDefinitions)
        {
            var definition = metadata.GetFieldDefinition(handle);
            int rva = definition.GetRelativeVirtualAddress();
            if (rva == 0) continue;
            var field = module.ResolveField(MetadataTokens.GetToken(handle))
                ?? throw new InvalidOperationException("TShockAPI:rva-field-unresolved");
            int size = field.FieldType.StructLayoutAttribute?.Size ?? 0;
            if (size <= 0 || size > 1024 * 1024) throw new InvalidOperationException("TShockAPI:rva-field-size-unverified");
            var loaded = new byte[size];
            RuntimeHelpers.InitializeArray(loaded, field.FieldHandle);
            if (!loaded.AsSpan().SequenceEqual(pe.GetSectionData(rva).GetContent(0, size).AsSpan()))
                throw new InvalidOperationException("TShockAPI:rva-field-mismatch");
            rvaFields++;
        }
        foreach (var handle in metadata.ManifestResources)
        {
            var resource = metadata.GetManifestResource(handle);
            if (!resource.Implementation.IsNil) throw new InvalidOperationException("TShockAPI:external-resource-unverified");
            var directory = pe.PEHeaders.CorHeader!.ResourcesDirectory;
            var raw = pe.GetSectionData(directory.RelativeVirtualAddress).GetContent();
            int offset = checked((int)resource.Offset);
            int size = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(offset, 4));
            if (size < 0 || size > 64 * 1024 * 1024) throw new InvalidOperationException("TShockAPI:resource-size-unverified");
            using var loaded = assembly.GetManifestResourceStream(metadata.GetString(resource.Name))
                ?? throw new InvalidOperationException("TShockAPI:resource-unavailable");
            using var buffer = new MemoryStream();
            loaded.CopyTo(buffer);
            if (!buffer.ToArray().AsSpan().SequenceEqual(raw.AsSpan(offset + 4, size)))
                throw new InvalidOperationException("TShockAPI:resource-mismatch");
            resources++;
        }
        evidence.Add(new(assembly.GetName().Name!, Path.GetFullPath(path), actual, assembly.Location,
            module.ModuleVersionId, "loaded-raw-metadata+all-method-il-headers-eh+all-rva-fields+all-managed-resources", methods, resources, rvaFields));
    }
}
