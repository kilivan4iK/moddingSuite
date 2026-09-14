using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using moddingSuite.BL.Ndf;
using moddingSuite.Model.Ndfbin;
using moddingSuite.Model.Ndfbin.Types;
using moddingSuite.Model.Ndfbin.Types.AllTypes;

namespace moddingSuite.Cli
{
public static class GfxCliRunner
{
    private const int ExitOk = 0;
    private const int ExitHardError = 3;

    // Bump when the shape or the meaning of the cached index changes.
    private const int GfxIndexCacheVersion = 1;

    private static readonly Regex HeaderRegex =
        new(@"^\s*(?:export\s+)?(?<name>\S+)\s+is\s+(?<class>[A-Za-z0-9_./$~]+)\b", RegexOptions.Compiled);
    private static readonly Regex UnnamedHeaderRegex =
        new(@"^\s*unnamed\s+(?<class>[A-Za-z0-9_./$~]+)\b", RegexOptions.Compiled);
    private static readonly Regex QuotedStringRegex =
        new(@"[""'](?<value>[^""']+)[""']", RegexOptions.Compiled);
    private static readonly Regex ReferenceMeshRegex =
        new(@"ReferenceMesh\s*=\s*\$/GFX/DepictionResources/(?<name>[A-Za-z0-9_]+)", RegexOptions.Compiled);
    private static readonly Regex WeaponDescriptorRegex =
        new(@"\$/GFX/Weapon/(?<name>[A-Za-z0-9_]+)", RegexOptions.Compiled);
    private static readonly Regex MimeticNameRegex =
        new(@"MimeticName\s*=\s*""(?<value>[^""]+)""", RegexOptions.Compiled);
    private static readonly Regex ClassNameForDebugRegex =
        new(@"ClassNameForDebug\s*=\s*'(?<value>[^']+)'", RegexOptions.Compiled);
    private static readonly Regex GameplayBBoxBoneRegex =
        new(@"GameplayBBoxBoneName\s*=\s*""(?<value>[^""]+)""", RegexOptions.Compiled);
    private static readonly Regex ConstMeshRegex =
        new(@"^\s*(?:export\s+)?(?<name>[A-Za-z0-9_]+)\s+is\s+[""'](?:GameData:)?/?(?<path>[^""']+?\.fbx)[""']\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ResourceMeshRegex =
        new(@"^\s*(?:export\s+)?(?<name>[A-Za-z0-9_]+)\s+is\s+TResourceMesh\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MeshAssignRegex =
        new(@"^\s*Mesh\s*=\s*(?<value>.+?)\s*(?:,)?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TurretHeaderRegex =
        new(@"\bTTurret[A-Za-z0-9_]*\s*\(", RegexOptions.Compiled);
    private static readonly Regex TagRegex =
        new(@"Tag\s*=\s*""(?<value>[^""]+)""", RegexOptions.Compiled);
    private static readonly Regex YulBoneOrdinalRegex =
        new(@"YulBoneOrdinal\s*=\s*(?<value>[0-9]+)", RegexOptions.Compiled);
    private static readonly Regex CoatingNameRegex =
        new(@"CoatingName\s*=\s*['""](?<value>[^'""]+)['""]", RegexOptions.Compiled);
    private static readonly Regex AlternativesRegex =
        new(@"Alternatives\s*=\s*(?<value>[A-Za-z0-9_]+)", RegexOptions.Compiled);
    private static readonly Regex MeshDescriptorRegex =
        new(@"MeshDescriptor\s*=\s*\$/GFX/DepictionResources/(?<name>[A-Za-z0-9_]+)", RegexOptions.Compiled);
    private static readonly Regex NumericRegex =
        new(@"[-+]?(?:\d+(?:\.\d+)?|\.\d+)(?:[eE][-+]?\d+)?", RegexOptions.Compiled);

    private sealed class CliOptions
    {
        public string WarnoRoot { get; init; } = "";
        public string AssetPath { get; init; } = "";
        public string OutJson { get; init; } = "";
        public string CacheDir { get; init; } = "";
        public bool Verbose { get; init; }
    }

    private sealed class ScriptBlock
    {
        public string Name { get; init; } = "";
        public string ClassName { get; init; } = "";
        public string Text { get; init; } = "";
    }

    private sealed class BinaryIndex
    {
        public string SourcePath { get; init; } = "";
        public NdfBinary Binary { get; init; } = new();
        public Dictionary<uint, string> NameById { get; init; } = new();
    }

    private sealed class LegacyResourceMeshMapInfo
    {
        public Dictionary<string, string> AssetPathByDescriptor { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);

        public List<string> SourceFiles { get; init; } = new();
    }

    private sealed class LegacyResourceMeshCachePayload
    {
        public Dictionary<string, string> AssetPathByDescriptor { get; init; } = new();
        public List<string> SourceFiles { get; init; } = new();
    }

    /// <summary>
    /// Everything the runner derives from the game's GFX dump before it looks at the
    /// requested asset: the unit table, the weapon and weapon-operator tables and the
    /// depiction registrations.  None of it depends on --asset-path, yet rebuilding it
    /// meant reading four ndfbin files and re-parsing ~15 MB of decompiled script on
    /// every single invocation - about 60 s per asset, paid again for each new model a
    /// user imports.  Cached to disk under a signature of the source files so the first
    /// asset pays it once and the rest load in a fraction of a second.
    /// </summary>
    private sealed class GfxIndexCachePayload
    {
        public int Version { get; init; } = GfxIndexCacheVersion;
        public string CliVersion { get; init; } = "";
        public string SourcesSignature { get; init; } = "";
        public List<UnitInfo> Units { get; init; } = new();
        public Dictionary<string, WeaponDescriptorInfo> Weapons { get; init; } = new();
        public Dictionary<string, WeaponOperatorInfo> WeaponOperators { get; init; } = new();
        public Dictionary<string, DepictionRegistrationInfo> Depictions { get; init; } = new();
        public List<string> LegacySourceFiles { get; init; } = new();
    }

    private sealed class UnitInfo
    {
        public string Descriptor { get; init; } = "";
        public string ClassNameForDebug { get; init; } = "";
        public string CoatingName { get; init; } = "";
        public string GameplayBBoxBoneName { get; init; } = "";
        public string ReferenceMeshDescriptor { get; init; } = "";
        public string ReferenceMeshAssetPath { get; set; } = "";
        public string WeaponDescriptor { get; init; } = "";
    }

    private sealed class TurretInfo
    {
        public string Tag { get; init; } = "";
        public int YulBoneOrdinal { get; init; }
    }

    private sealed class WeaponDescriptorInfo
    {
        public string Name { get; init; } = "";
        public List<TurretInfo> Turrets { get; init; } = new();
    }

    private sealed class WeaponOperatorInfo
    {
        public string Name { get; init; } = "";
        public List<string> Anchors { get; init; } = new();
    }

    private sealed class SubDepictionInfo
    {
        public string CoatingName { get; init; } = "";
        public List<string> Anchors { get; init; } = new();
        public string MeshDescriptor { get; init; } = "";
        public string MeshAssetPath { get; set; } = "";
        public List<string> Operators { get; init; } = new();
        public List<string> WeaponFxAnchors { get; init; } = new();
        public List<TransformDebugInfo> TransformDebug { get; init; } = new();
        public List<SemanticNodeInfo> SemanticNodes { get; init; } = new();
    }

    private sealed class DepictionRegistrationInfo
    {
        public string CoatingName { get; init; } = "";
        public string Alternatives { get; init; } = "";
        public List<string> Operators { get; init; } = new();
        public List<string> WeaponFxAnchors { get; init; } = new();
        public List<SubDepictionInfo> SubDepictions { get; init; } = new();
        public List<TransformDebugInfo> TransformDebug { get; init; } = new();
        public List<SemanticNodeInfo> SemanticNodes { get; init; } = new();
    }

    private sealed class SemanticNodeInfo
    {
        public string Name { get; init; } = "";
        public string Role { get; init; } = "";
        public string ParentName { get; init; } = "";
        public string SourceKind { get; init; } = "";
        public List<double> LocalTranslation { get; init; } = new();
        public List<List<double>> LocalRotationBasis { get; init; } = new();
        public List<double> LocalScale { get; init; } = new();
        public Dictionary<string, object?> Provenance { get; init; } = new();
    }

    private sealed class TransformDebugInfo
    {
        public string Scope { get; init; } = "";
        public string OwnerName { get; init; } = "";
        public string OwnerClass { get; init; } = "";
        public string Path { get; init; } = "";
        public string PropertyName { get; init; } = "";
        public string ValueType { get; init; } = "";
        public string ValueText { get; init; } = "";
        public List<double> ScalarValues { get; init; } = new();
        public Dictionary<string, object?> ValueData { get; init; } = new();
    }

    public static int Run(string[] args)
    {
        try
        {
            return RunCore(ParseArgs(args));
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitHardError;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"gfx_cli_error: {ex.Message}");
            return ExitHardError;
        }
    }

    private static int RunCore(CliOptions options)
    {
        string warnoRoot = Path.GetFullPath(options.WarnoRoot);
        string assetPath = NormalizeAssetPath(options.AssetPath);
        string outJsonPath = ResolveOutputPath(options.OutJson);
        string cacheDir = Path.GetFullPath(options.CacheDir);

        Directory.CreateDirectory(Path.GetDirectoryName(outJsonPath) ?? ".");
        Directory.CreateDirectory(cacheDir);

        if (!Directory.Exists(warnoRoot))
        {
            Console.Error.WriteLine($"WARNO root not found: {warnoRoot}");
            return ExitHardError;
        }

        Dictionary<string, string> sourceFiles = ResolveSourceFiles(warnoRoot);
        string cliVersionForCache =
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";

        // The whole GFX index below is asset-independent, so it is loaded from disk when the
        // game files have not changed.  --verbose always rebuilds, because it also writes the
        // decompiled debug scripts.
        string indexSignature = ComputeGfxIndexSignature(sourceFiles, warnoRoot);
        GfxIndexCachePayload? cachedIndex = options.Verbose
            ? null
            : LoadGfxIndexCache(cacheDir, indexSignature, cliVersionForCache);

        List<UnitInfo> units;
        Dictionary<string, WeaponDescriptorInfo> weapons;
        Dictionary<string, WeaponOperatorInfo> weaponOperators;
        Dictionary<string, DepictionRegistrationInfo> depictions;
        List<string> legacySourceFiles;

        if (cachedIndex != null)
        {
            units = cachedIndex.Units;
            // Rebuild with the case-insensitive comparer the lookups below rely on: a
            // deserialized dictionary comes back ordinal/case-SENSITIVE, which would turn
            // every descriptor lookup into a silent miss.
            weapons = new Dictionary<string, WeaponDescriptorInfo>(cachedIndex.Weapons, StringComparer.OrdinalIgnoreCase);
            weaponOperators = new Dictionary<string, WeaponOperatorInfo>(cachedIndex.WeaponOperators, StringComparer.OrdinalIgnoreCase);
            depictions = new Dictionary<string, DepictionRegistrationInfo>(cachedIndex.Depictions, StringComparer.OrdinalIgnoreCase);
            legacySourceFiles = cachedIndex.LegacySourceFiles;
        }
        else
        {
            Dictionary<string, BinaryIndex> binaries = sourceFiles.ToDictionary(
                kvp => kvp.Key,
                kvp => LoadBinaryIndex(kvp.Value, options.Verbose),
                StringComparer.OrdinalIgnoreCase
            );
            string weaponScript = LoadCachedOrBuildScript(sourceFiles["Weapon"], cacheDir, "Weapon", options.Verbose);
            string depictionScript = LoadCachedOrBuildScript(sourceFiles["Depiction"], cacheDir, "Depiction", options.Verbose);

            if (options.Verbose)
            {
                var scripts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Unit"] = LoadCachedOrBuildScript(sourceFiles["Unit"], cacheDir, "Unit", false),
                    ["Weapon"] = weaponScript,
                    ["Depiction"] = depictionScript,
                    ["DepictionResources"] = LoadCachedOrBuildScript(sourceFiles["DepictionResources"], cacheDir, "DepictionResources", false),
                };
                WriteDebugScripts(cacheDir, scripts);
            }

            LegacyResourceMeshMapInfo legacyResourceMap = BuildLegacyResourceMeshMap(warnoRoot, cacheDir);
            Dictionary<string, string> resourceMeshMap = legacyResourceMap.AssetPathByDescriptor;
            units = ParseUnitsDirect(binaries["Unit"], resourceMeshMap);
            weapons = MergeWeaponInfos(
                ParseWeaponsDirect(binaries["Weapon"]),
                ParseWeapons(weaponScript)
            );
            weaponOperators = ParseWeaponOperators(depictionScript);
            depictions = MergeDepictionInfos(
                ParseDepictionRegistrationsDirect(
                    binaries["Depiction"],
                    resourceMeshMap
                ),
                ParseDepictionRegistrations(
                    depictionScript,
                    resourceMeshMap,
                    weaponOperators
                )
            );
            legacySourceFiles = legacyResourceMap.SourceFiles;

            if (!options.Verbose)
            {
                SaveGfxIndexCache(
                    cacheDir,
                    indexSignature,
                    cliVersionForCache,
                    units,
                    weapons,
                    weaponOperators,
                    depictions,
                    legacySourceFiles
                );
            }
        }

        List<UnitInfo> matchedUnits = MatchUnits(assetPath, units);
        List<string> operators = new();
        List<TurretInfo> turrets = new();
        List<string> weaponFxAnchors = new();
        List<SubDepictionInfo> subdepictions = new();
        List<TransformDebugInfo> transformDebug = new();
        List<SemanticNodeInfo> semanticNodes = new();

        foreach (UnitInfo unit in matchedUnits)
        {
            if (!string.IsNullOrWhiteSpace(unit.WeaponDescriptor) &&
                weapons.TryGetValue(unit.WeaponDescriptor, out WeaponDescriptorInfo? weapon))
            {
                foreach (TurretInfo turret in weapon.Turrets)
                {
                    if (!turrets.Any(x =>
                        string.Equals(x.Tag, turret.Tag, StringComparison.OrdinalIgnoreCase) &&
                        x.YulBoneOrdinal == turret.YulBoneOrdinal))
                    {
                        turrets.Add(turret);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(unit.CoatingName) &&
                depictions.TryGetValue(unit.CoatingName, out DepictionRegistrationInfo? depiction))
            {
                AppendDistinct(operators, depiction.Operators);
                AppendDistinct(weaponFxAnchors, depiction.WeaponFxAnchors);
                transformDebug.AddRange(depiction.TransformDebug);
                semanticNodes.AddRange(depiction.SemanticNodes);
                AppendDerivedTurrets(turrets, depiction.Operators);
                AppendDerivedTurrets(turrets, depiction.WeaponFxAnchors);
                foreach (SubDepictionInfo sub in depiction.SubDepictions)
                {
                    if (!subdepictions.Any(existing =>
                        string.Equals(existing.MeshDescriptor, sub.MeshDescriptor, StringComparison.OrdinalIgnoreCase) &&
                        string.Join("|", existing.Anchors).Equals(string.Join("|", sub.Anchors), StringComparison.OrdinalIgnoreCase)))
                    {
                        subdepictions.Add(sub);
                    }
                    transformDebug.AddRange(sub.TransformDebug);
                    semanticNodes.AddRange(sub.SemanticNodes);
                    AppendDistinct(weaponFxAnchors, sub.WeaponFxAnchors);
                    AppendDerivedTurrets(turrets, sub.Anchors);
                    AppendDerivedTurrets(turrets, sub.Operators);
                    AppendDerivedTurrets(turrets, sub.WeaponFxAnchors);
                }
            }
        }

        string trackKind = ResolveTrackKind(operators);
        List<object> referenceMeshes = matchedUnits
            .Select(unit => new
            {
                descriptor = unit.ReferenceMeshDescriptor,
                asset_path = unit.ReferenceMeshAssetPath,
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.descriptor))
            .Distinct()
            .OrderBy(item => item.descriptor, StringComparer.OrdinalIgnoreCase)
            .Cast<object>()
            .ToList();

        string cliVersion = cliVersionForCache;
        List<string> manifestSourceFiles = sourceFiles.Values
            .Concat(legacySourceFiles)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var jsonPayload = new
        {
            schema_version = 2,
            asset_path = assetPath,
            source_files = manifestSourceFiles,
            matched_units = matchedUnits
                .OrderBy(x => x.CoatingName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Descriptor, StringComparer.OrdinalIgnoreCase)
                .Select(x => new
                {
                    descriptor = x.Descriptor,
                    class_name_for_debug = x.ClassNameForDebug,
                    coating_name = x.CoatingName,
                    gameplay_bbox_bone_name = x.GameplayBBoxBoneName,
                    reference_mesh_descriptor = x.ReferenceMeshDescriptor,
                    reference_mesh_asset_path = x.ReferenceMeshAssetPath,
                    weapon_descriptor = x.WeaponDescriptor,
                }),
            reference_meshes = referenceMeshes,
            operators = operators.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            turrets = turrets
                .OrderBy(x => x.YulBoneOrdinal)
                .ThenBy(x => x.Tag, StringComparer.OrdinalIgnoreCase)
                .Select(x => new
                {
                    tag = x.Tag,
                    yul_bone_ordinal = x.YulBoneOrdinal,
                }),
            weapon_fx_anchors = weaponFxAnchors.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            subdepictions = subdepictions
                .OrderBy(x => x.CoatingName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.MeshDescriptor, StringComparer.OrdinalIgnoreCase)
                .Select(x => new
                {
                    coating_name = x.CoatingName,
                    anchors = x.Anchors.OrderBy(y => y, StringComparer.OrdinalIgnoreCase).ToList(),
                    mesh_descriptor = x.MeshDescriptor,
                    mesh_asset_path = x.MeshAssetPath,
                    operators = x.Operators.OrderBy(y => y, StringComparer.OrdinalIgnoreCase).ToList(),
                    weapon_fx_anchors = x.WeaponFxAnchors.OrderBy(y => y, StringComparer.OrdinalIgnoreCase).ToList(),
                }),
            track_kind = trackKind,
            semantic_nodes = semanticNodes
                .OrderBy(x => x.Role, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => new
                {
                    name = x.Name,
                    role = x.Role,
                    parent_name = x.ParentName,
                    source_kind = x.SourceKind,
                    local_translation = x.LocalTranslation,
                    local_rotation_basis = x.LocalRotationBasis,
                    local_scale = x.LocalScale,
                    provenance = x.Provenance,
                }),
            transform_debug = transformDebug
                .OrderBy(x => x.Scope, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.OwnerName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .Select(x => new
                {
                    scope = x.Scope,
                    owner_name = x.OwnerName,
                    owner_class = x.OwnerClass,
                    path = x.Path,
                    property_name = x.PropertyName,
                    value_type = x.ValueType,
                    value_text = x.ValueText,
                    scalar_values = x.ScalarValues,
                    value_data = x.ValueData,
                }),
            generated_at = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            cli_version = cliVersion,
        };

        File.WriteAllText(
            outJsonPath,
            JsonSerializer.Serialize(jsonPayload, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8
        );
        Console.WriteLine($"[OK] gfx json exported: {outJsonPath} matched_units={matchedUnits.Count}");
        return ExitOk;
    }

    private static CliOptions ParseArgs(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool verbose = false;

        for (int i = 0; i < args.Length; i++)
        {
            string token = args[i].Trim();
            if (token.Equals("--verbose", StringComparison.OrdinalIgnoreCase))
            {
                verbose = true;
                continue;
            }

            if (!token.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unknown argument: {token}");
            if (i + 1 >= args.Length)
                throw new ArgumentException($"Missing value for argument: {token}");
            map[token] = args[++i].Trim();
        }

        string Require(string key)
        {
            if (map.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
                return value;
            throw new ArgumentException(
                "Required arguments: --warno-root --asset-path --out-json --cache-dir [--verbose]"
            );
        }

        return new CliOptions
        {
            WarnoRoot = Require("--warno-root"),
            AssetPath = Require("--asset-path"),
            OutJson = Require("--out-json"),
            CacheDir = Require("--cache-dir"),
            Verbose = verbose,
        };
    }

    private static string ResolveOutputPath(string rawPath)
    {
        string full = Path.GetFullPath(rawPath);
        if (full.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return full;
        return Path.Combine(full, "gfx_manifest.json");
    }

    private static string NormalizeAssetPath(string path)
    {
        string raw = (path ?? string.Empty).Replace('\\', '/').Trim();
        raw = raw.TrimStart('/');
        while (raw.Contains("//", StringComparison.Ordinal))
            raw = raw.Replace("//", "/", StringComparison.Ordinal);
        return raw;
    }

    private static Dictionary<string, string> ResolveSourceFiles(string warnoRoot)
    {
        string gfxRoot = Path.Combine(warnoRoot, "Output", "AllPlatforms", "NDF", "GFX");
        var required = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Unit"] = Path.Combine(gfxRoot, "Unit.ndfbin"),
            ["Weapon"] = Path.Combine(gfxRoot, "Weapon.ndfbin"),
            ["Depiction"] = Path.Combine(gfxRoot, "Depiction.ndfbin"),
            ["DepictionResources"] = Path.Combine(gfxRoot, "DepictionResources.ndfbin"),
        };

        foreach (KeyValuePair<string, string> item in required)
        {
            if (!File.Exists(item.Value))
                throw new FileNotFoundException($"Required GFX ndfbin not found: {item.Value}");
        }

        return required;
    }

    private static BinaryIndex LoadBinaryIndex(string ndfPath, bool verbose)
    {
        if (verbose)
            Console.Error.WriteLine($"gfx_source={ndfPath}");

        var reader = new NdfbinReader();
        NdfBinary binary = reader.Read(File.ReadAllBytes(ndfPath));
        Dictionary<uint, string> nameById = NdfScriptNameResolver.Resolve(binary, ndfPath);

        return new BinaryIndex
        {
            SourcePath = ndfPath,
            Binary = binary,
            NameById = nameById,
        };
    }

    private static string ResolveObjectName(NdfObject obj, IReadOnlyDictionary<uint, string> nameById)
    {
        if (obj == null)
            return string.Empty;

        if (nameById != null && nameById.TryGetValue(obj.Id, out string? resolved) && !string.IsNullOrWhiteSpace(resolved))
            return resolved.Trim();

        return NdfTextWriter.GetObjectName(obj.Id);
    }

    private static NdfPropertyValue? FindProperty(NdfObject obj, string propertyName)
    {
        if (obj == null || string.IsNullOrWhiteSpace(propertyName))
            return null;

        return obj.PropertyValues.FirstOrDefault(x =>
            x != null &&
            x.Property != null &&
            x.Type != NdfType.Unset &&
            string.Equals(x.Property.Name, propertyName, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetStringFromValue(NdfValueWrapper? value, IReadOnlyDictionary<uint, string>? nameById)
    {
        if (value == null)
            return string.Empty;

        if (value is NdfObjectReference objectReference)
            return ResolveObjectReferenceName(objectReference, nameById);

        if (value is NdfFlatValueWrapper flat && flat.Value != null)
        {
            if (flat.Value is NdfStringReference stringReference)
                return stringReference.Value ?? string.Empty;

            if (flat.Value is NdfTranReference tranReference)
                return tranReference.Value ?? string.Empty;

            return flat.Value.ToString() ?? string.Empty;
        }

        return value.ToString() ?? string.Empty;
    }

    private static string GetStringProperty(NdfObject obj, string propertyName, IReadOnlyDictionary<uint, string>? nameById = null)
    {
        NdfPropertyValue? property = FindProperty(obj, propertyName);
        return property == null ? string.Empty : GetStringFromValue(property.Value, nameById).Trim();
    }

    private static int GetIntProperty(NdfObject obj, string propertyName)
    {
        string raw = GetStringProperty(obj, propertyName);
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            return parsed;
        return 0;
    }

    private static bool IsTransformLikePropertyName(string propertyName)
    {
        string low = (propertyName ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(low))
            return false;
        return low.Contains("offset", StringComparison.Ordinal) ||
               low.Contains("position", StringComparison.Ordinal) ||
               low.Contains("translation", StringComparison.Ordinal) ||
               low.Contains("rotation", StringComparison.Ordinal) ||
               low.Contains("scale", StringComparison.Ordinal) ||
               low.Contains("angle", StringComparison.Ordinal) ||
               low.Contains("transform", StringComparison.Ordinal);
    }

    private static List<double> ExtractNumericScalars(string text)
    {
        var scalars = new List<double>();
        foreach (Match match in NumericRegex.Matches(text ?? string.Empty))
        {
            if (!double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                continue;
            scalars.Add(value);
        }
        return scalars;
    }

    private static Dictionary<string, object?> DescribeSimpleObject(object? value, int depth = 0)
    {
        var outMap = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (value == null || depth > 2)
            return outMap;

        Type type = value.GetType();
        outMap["type"] = type.FullName ?? type.Name;

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0)
                continue;
            try
            {
                object? propValue = property.GetValue(value);
                if (propValue == null)
                    continue;
                Type propType = propValue.GetType();
                if (propType.IsEnum || propType == typeof(string) || propType == typeof(bool) || propType == typeof(Guid))
                {
                    outMap[property.Name] = propValue.ToString();
                    continue;
                }
                if (propType == typeof(int) || propType == typeof(uint) ||
                    propType == typeof(short) || propType == typeof(ushort) ||
                    propType == typeof(long) || propType == typeof(ulong) ||
                    propType == typeof(float) || propType == typeof(double) || propType == typeof(decimal))
                {
                    outMap[property.Name] = Convert.ToString(propValue, CultureInfo.InvariantCulture);
                    continue;
                }
                if (propValue is IEnumerable enumerable && propValue is not string)
                {
                    var items = new List<string>();
                    foreach (object? item in enumerable)
                    {
                        if (item == null)
                            continue;
                        items.Add(item.ToString() ?? string.Empty);
                        if (items.Count >= 8)
                            break;
                    }
                    if (items.Count > 0)
                        outMap[property.Name] = items;
                    continue;
                }

                if (depth < 2)
                {
                    Dictionary<string, object?> nested = DescribeSimpleObject(propValue, depth + 1);
                    if (nested.Count > 1)
                        outMap[property.Name] = nested;
                }
            }
            catch
            {
                // debug-only export; best-effort is enough
            }
        }

        return outMap;
    }

    private static string DescribeValueText(NdfValueWrapper? value, IReadOnlyDictionary<uint, string>? nameById)
    {
        if (value == null)
            return string.Empty;
        if (value is NdfCollection || value is NdfMap)
            return value.ToString() ?? string.Empty;
        return GetStringFromValue(value, nameById);
    }

    private static List<TransformDebugInfo> CollectTransformDebugEntries(
        NdfObject obj,
        IReadOnlyDictionary<uint, string> nameById,
        string scope,
        string ownerName,
        int depth = 0,
        string path = "$")
    {
        var outEntries = new List<TransformDebugInfo>();
        if (obj == null || depth > 4)
            return outEntries;

        string ownerClass = obj.Class?.Name ?? string.Empty;
        foreach (NdfPropertyValue property in obj.PropertyValues.Where(x => x != null && x.Property != null && x.Type != NdfType.Unset))
        {
            string propertyName = property.Property?.Name ?? string.Empty;
            string childPath = string.Concat(path, ".", propertyName);
            if (IsTransformLikePropertyName(propertyName))
            {
                string text = DescribeValueText(property.Value, nameById);
                Dictionary<string, object?> data = DescribeSimpleObject(property.Value);
                outEntries.Add(new TransformDebugInfo
                {
                    Scope = scope,
                    OwnerName = ownerName,
                    OwnerClass = ownerClass,
                    Path = childPath,
                    PropertyName = propertyName,
                    ValueType = property.Value?.GetType().Name ?? string.Empty,
                    ValueText = text,
                    ScalarValues = ExtractNumericScalars(text),
                    ValueData = data,
                });
            }

            if (property.Value is NdfObjectReference objectReference && objectReference.Instance != null)
            {
                outEntries.AddRange(CollectTransformDebugEntries(
                    objectReference.Instance,
                    nameById,
                    scope,
                    ownerName,
                    depth + 1,
                    childPath
                ));
                continue;
            }

            if (property.Value is NdfCollection collection)
            {
                int itemIndex = 0;
                foreach (CollectionItemValueHolder item in collection)
                {
                    if (item?.Value is NdfObjectReference itemRef && itemRef.Instance != null)
                    {
                        outEntries.AddRange(CollectTransformDebugEntries(
                            itemRef.Instance,
                            nameById,
                            scope,
                            ownerName,
                            depth + 1,
                            $"{childPath}[{itemIndex}]"
                        ));
                    }
                    itemIndex++;
                }
            }
        }

        return outEntries;
    }

    private static NdfObject? GetObjectPropertyInstance(NdfObject obj, string propertyName)
    {
        NdfPropertyValue? property = FindProperty(obj, propertyName);
        if (property?.Value is NdfObjectReference objectReference)
            return objectReference.Instance;
        return null;
    }

    private static List<NdfValueWrapper> GetCollectionValues(NdfObject obj, string propertyName)
    {
        var values = new List<NdfValueWrapper>();
        NdfPropertyValue? property = FindProperty(obj, propertyName);
        if (property?.Value is not NdfCollection collection)
            return values;

        foreach (CollectionItemValueHolder item in collection)
        {
            if (item?.Value != null)
                values.Add(item.Value);
        }

        return values;
    }

    private static List<NdfObject> GetCollectionObjectInstances(NdfObject obj, string propertyName)
    {
        var objects = new List<NdfObject>();
        foreach (NdfValueWrapper value in GetCollectionValues(obj, propertyName))
        {
            if (value is NdfObjectReference objectReference && objectReference.Instance != null)
                objects.Add(objectReference.Instance);
        }
        return objects;
    }

    private static List<string> GetCollectionTokens(
        NdfObject obj,
        string propertyName,
        IReadOnlyDictionary<uint, string>? nameById)
    {
        var values = new List<string>();
        foreach (NdfValueWrapper value in GetCollectionValues(obj, propertyName))
        {
            string token = GetStringFromValue(value, nameById).Trim();
            if (!string.IsNullOrWhiteSpace(token))
                AppendDistinct(values, new[] { token });
        }
        return values;
    }

    private static List<KeyValuePair<string, NdfObject>> GetStringObjectPairsFromCollection(
        NdfObject obj,
        string propertyName,
        IReadOnlyDictionary<uint, string>? nameById)
    {
        var pairs = new List<KeyValuePair<string, NdfObject>>();
        foreach (NdfValueWrapper value in GetCollectionValues(obj, propertyName))
        {
            if (value is not NdfMap map)
                continue;

            string key = GetStringFromValue(map.Key?.Value, nameById).Trim();
            NdfObject? item = (map.Value as MapValueHolder)?.Value is NdfObjectReference objectReference
                ? objectReference.Instance
                : null;

            if (!string.IsNullOrWhiteSpace(key) && item != null)
                pairs.Add(new KeyValuePair<string, NdfObject>(key, item));
        }
        return pairs;
    }

    private static string ResolveObjectReferenceName(
        NdfObjectReference objectReference,
        IReadOnlyDictionary<uint, string>? nameById)
    {
        if (objectReference == null || objectReference.Class == null)
            return string.Empty;

        if (nameById != null && nameById.TryGetValue(objectReference.InstanceId, out string? resolved) && !string.IsNullOrWhiteSpace(resolved))
            return resolved.Trim();

        if (objectReference.Instance != null)
            return NdfTextWriter.GetObjectName(objectReference.Instance.Id);

        return string.Empty;
    }

    private static string NormalizeDescriptorToken(string token)
    {
        return (token ?? string.Empty).Trim().Trim('"').Trim('\'');
    }

    private static bool DescriptorMatchesCoating(string descriptor, string coatingName, string prefix)
    {
        if (string.IsNullOrWhiteSpace(descriptor) || string.IsNullOrWhiteSpace(prefix))
            return false;

        if (!descriptor.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(coatingName))
            return true;

        string trimmed = descriptor[prefix.Length..];
        return string.Equals(trimmed, coatingName, StringComparison.OrdinalIgnoreCase) ||
               trimmed.EndsWith("_" + coatingName, StringComparison.OrdinalIgnoreCase) ||
               coatingName.EndsWith("_" + trimmed, StringComparison.OrdinalIgnoreCase);
    }

    private static string DeriveCoatingName(UnitInfo unit)
    {
        if (!string.IsNullOrWhiteSpace(unit.CoatingName))
            return unit.CoatingName;

        if (!string.IsNullOrWhiteSpace(unit.ClassNameForDebug) &&
            unit.ClassNameForDebug.StartsWith("Unit_", StringComparison.OrdinalIgnoreCase))
        {
            return unit.ClassNameForDebug[5..];
        }

        if (!string.IsNullOrWhiteSpace(unit.Descriptor) &&
            unit.Descriptor.StartsWith("Descriptor_Unit_", StringComparison.OrdinalIgnoreCase))
        {
            return unit.Descriptor["Descriptor_Unit_".Length..];
        }

        return string.Empty;
    }

    private static LegacyResourceMeshMapInfo BuildLegacyResourceMeshMap(string warnoRoot)
    {
        return BuildLegacyResourceMeshMap(warnoRoot, string.Empty);
    }

    private static readonly JsonSerializerOptions GfxIndexCacheJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private static string GfxIndexCachePath(string cacheDir, string signature)
    {
        string sharedDir = Path.Combine(cacheDir, "_gfx_shared");
        Directory.CreateDirectory(sharedDir);
        return Path.Combine(sharedDir, $"gfx_index.v{GfxIndexCacheVersion}.{signature}.json");
    }

    /// <summary>
    /// Identity of the game data the index was built from: every source file's path, size and
    /// last-write time.  A game patch changes at least one of those, so the cache is rebuilt.
    /// </summary>
    private static string ComputeGfxIndexSignature(Dictionary<string, string> sourceFiles, string warnoRoot)
    {
        var sb = new StringBuilder();
        sb.Append("root=").AppendLine(Path.GetFullPath(warnoRoot).ToLowerInvariant());
        foreach (KeyValuePair<string, string> kvp in sourceFiles.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(kvp.Key).Append('=').Append(Path.GetFullPath(kvp.Value).ToLowerInvariant());
            try
            {
                var fi = new FileInfo(kvp.Value);
                sb.Append('|').Append(fi.Length).Append('|').Append(fi.LastWriteTimeUtc.Ticks);
            }
            catch
            {
                sb.Append("|?|?");
            }

            sb.AppendLine();
        }

        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).Substring(0, 32).ToLowerInvariant();
    }

    private static GfxIndexCachePayload? LoadGfxIndexCache(string cacheDir, string signature, string cliVersion)
    {
        if (string.IsNullOrWhiteSpace(cacheDir))
            return null;

        try
        {
            string path = GfxIndexCachePath(cacheDir, signature);
            if (!File.Exists(path))
                return null;

            GfxIndexCachePayload? payload = JsonSerializer.Deserialize<GfxIndexCachePayload>(
                File.ReadAllText(path, Encoding.UTF8),
                GfxIndexCacheJsonOptions
            );
            if (payload == null)
                return null;
            if (payload.Version != GfxIndexCacheVersion)
                return null;
            if (!string.Equals(payload.SourcesSignature, signature, StringComparison.Ordinal))
                return null;
            if (!string.Equals(payload.CliVersion, cliVersion, StringComparison.Ordinal))
                return null;
            // An index with no units at all would make every asset look unmatched; treat a
            // payload that thin as corrupt and rebuild rather than serve it.
            if (payload.Units.Count == 0)
                return null;
            return payload;
        }
        catch
        {
            return null; // unreadable or corrupt cache: rebuild
        }
    }

    private static void SaveGfxIndexCache(
        string cacheDir,
        string signature,
        string cliVersion,
        List<UnitInfo> units,
        Dictionary<string, WeaponDescriptorInfo> weapons,
        Dictionary<string, WeaponOperatorInfo> weaponOperators,
        Dictionary<string, DepictionRegistrationInfo> depictions,
        List<string> legacySourceFiles)
    {
        if (string.IsNullOrWhiteSpace(cacheDir) || units.Count == 0)
            return;

        try
        {
            string path = GfxIndexCachePath(cacheDir, signature);
            var payload = new GfxIndexCachePayload
            {
                Version = GfxIndexCacheVersion,
                CliVersion = cliVersion,
                SourcesSignature = signature,
                Units = units,
                Weapons = weapons,
                WeaponOperators = weaponOperators,
                Depictions = depictions,
                LegacySourceFiles = legacySourceFiles,
            };
            // Write to a temp file and move into place so a killed run cannot leave a
            // half-written cache that later loads as truncated data.
            string temp = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(payload, GfxIndexCacheJsonOptions), Encoding.UTF8);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // best-effort cache only
        }
    }

    private static LegacyResourceMeshMapInfo BuildLegacyResourceMeshMap(string warnoRoot, string cacheDir)
    {
        string cachePath = string.Empty;
        if (!string.IsNullOrWhiteSpace(cacheDir))
        {
            string sharedDir = Path.Combine(cacheDir, "_gfx_shared");
            Directory.CreateDirectory(sharedDir);
            cachePath = Path.Combine(sharedDir, "legacy_depiction_resources_map.v1.json");
            if (File.Exists(cachePath))
            {
                try
                {
                    LegacyResourceMeshCachePayload? cached = JsonSerializer.Deserialize<LegacyResourceMeshCachePayload>(
                        File.ReadAllText(cachePath, Encoding.UTF8)
                    );
                    if (cached != null && cached.AssetPathByDescriptor.Count > 0)
                    {
                        return new LegacyResourceMeshMapInfo
                        {
                            AssetPathByDescriptor = new Dictionary<string, string>(
                                cached.AssetPathByDescriptor,
                                StringComparer.OrdinalIgnoreCase
                            ),
                            SourceFiles = cached.SourceFiles
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                                .ToList(),
                        };
                    }
                }
                catch
                {
                    // ignore corrupt cache and rebuild
                }
            }
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sourceFiles = new List<string>();

        foreach (string root in ResolveLegacyDepictionResourceRoots(warnoRoot))
        {
            foreach (string ndfPath in Directory.EnumerateFiles(root, "*.ndf", SearchOption.AllDirectories))
            {
                ParseLegacyDepictionResourceFile(ndfPath, result);
                sourceFiles.Add(ndfPath);
            }
        }

        var info = new LegacyResourceMeshMapInfo
        {
            AssetPathByDescriptor = result,
            SourceFiles = sourceFiles
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };

        if (!string.IsNullOrWhiteSpace(cachePath))
        {
            try
            {
                var payload = new LegacyResourceMeshCachePayload
                {
                    AssetPathByDescriptor = info.AssetPathByDescriptor
                        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase),
                    SourceFiles = info.SourceFiles.ToList(),
                };
                File.WriteAllText(
                    cachePath,
                    JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
                    Encoding.UTF8
                );
            }
            catch
            {
                // best-effort cache only
            }
        }

        return info;
    }

    private static string LoadCachedOrBuildScript(string ndfPath, string cacheDir, string cacheKey, bool verbose)
    {
        if (!string.IsNullOrWhiteSpace(cacheDir))
        {
            string sharedDir = Path.Combine(cacheDir, "_gfx_shared");
            Directory.CreateDirectory(sharedDir);
            string cachePath = Path.Combine(sharedDir, $"{cacheKey}.compiled.ndf.txt");
            if (File.Exists(cachePath))
            {
                try
                {
                    DateTime srcTime = File.GetLastWriteTimeUtc(ndfPath);
                    DateTime cacheTime = File.GetLastWriteTimeUtc(cachePath);
                    if (cacheTime >= srcTime)
                        return File.ReadAllText(cachePath, Encoding.UTF8);
                }
                catch
                {
                    // ignore cache read errors and rebuild
                }
            }

            string built = LoadNdfScript(ndfPath, verbose);
            try
            {
                File.WriteAllText(cachePath, built, Encoding.UTF8);
            }
            catch
            {
                // best-effort cache only
            }
            return built;
        }

        return LoadNdfScript(ndfPath, verbose);
    }

    private static IEnumerable<string> ResolveLegacyDepictionResourceRoots(string warnoRoot)
    {
        var candidates = new[]
        {
            Path.Combine(warnoRoot, "Mods", "ModData", "base", "GameData", "Gameplay", "Gfx", "DepictionResources"),
            Path.Combine(warnoRoot, "ModData", "base", "GameData", "Gameplay", "Gfx", "DepictionResources"),
            Path.Combine(warnoRoot, "base", "GameData", "Gameplay", "Gfx", "DepictionResources"),
            Path.Combine(warnoRoot, "GameData", "Gameplay", "Gfx", "DepictionResources"),
        };

        foreach (string candidate in candidates)
        {
            if (Directory.Exists(candidate))
                yield return candidate;
        }
    }

    private static void ParseLegacyDepictionResourceFile(string ndfPath, Dictionary<string, string> outMap)
    {
        var constants = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string currentExport = string.Empty;
        string currentMeshExpr = string.Empty;
        bool inResource = false;

        foreach (string rawLine in File.ReadLines(ndfPath))
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            Match constMatch = ConstMeshRegex.Match(line);
            if (constMatch.Success)
            {
                string exportName = NormalizeDescriptorToken(constMatch.Groups["name"].Value);
                string assetPath = NormalizeGameDataAssetPath(constMatch.Groups["path"].Value);
                if (!string.IsNullOrWhiteSpace(exportName) && !string.IsNullOrWhiteSpace(assetPath))
                    constants[exportName] = assetPath;
                continue;
            }

            Match resourceMatch = ResourceMeshRegex.Match(line);
            if (resourceMatch.Success)
            {
                currentExport = NormalizeDescriptorToken(resourceMatch.Groups["name"].Value);
                currentMeshExpr = string.Empty;
                inResource = true;
                continue;
            }

            if (!inResource)
                continue;

            Match meshAssign = MeshAssignRegex.Match(line);
            if (meshAssign.Success)
                currentMeshExpr = meshAssign.Groups["value"].Value.Trim();

            if (line == ")")
            {
                string asset = ResolveMeshExpr(currentMeshExpr, constants);
                if (!string.IsNullOrWhiteSpace(currentExport) &&
                    !string.IsNullOrWhiteSpace(asset) &&
                    !outMap.ContainsKey(currentExport))
                {
                    outMap[currentExport] = asset;
                }

                currentExport = string.Empty;
                currentMeshExpr = string.Empty;
                inResource = false;
            }
        }
    }

    private static string LoadNdfScript(string ndfPath, bool verbose)
    {
        if (verbose)
            Console.Error.WriteLine($"gfx_source={ndfPath}");

        var reader = new NdfbinReader();
        NdfBinary binary = reader.Read(File.ReadAllBytes(ndfPath));
        var writer = new NdfTextWriter();
        byte[] scriptBytes = writer.CreateNdfScript(binary, ndfPath);
        return Encoding.UTF8.GetString(scriptBytes);
    }

    private static void WriteDebugScripts(string cacheDir, IReadOnlyDictionary<string, string> scripts)
    {
        string debugDir = Path.Combine(cacheDir, "_gfx_debug");
        Directory.CreateDirectory(debugDir);

        foreach (KeyValuePair<string, string> item in scripts)
        {
            string path = Path.Combine(debugDir, $"{item.Key}.ndf.txt");
            File.WriteAllText(path, item.Value ?? string.Empty, Encoding.UTF8);
        }
    }

    private static List<UnitInfo> ParseUnitsDirect(
        BinaryIndex unitIndex,
        IReadOnlyDictionary<string, string> resourceMeshMap)
    {
        var outUnits = new List<UnitInfo>();

        foreach (NdfObject obj in unitIndex.Binary.Instances.Where(x =>
                     x != null &&
                     x.Class != null &&
                     string.Equals(x.Class.Name, "TEntityDescriptor", StringComparison.OrdinalIgnoreCase)))
        {
            string descriptor = ResolveObjectName(obj, unitIndex.NameById);
            string classNameForDebug = GetStringProperty(obj, "ClassNameForDebug", unitIndex.NameById);
            var unitSeed = new UnitInfo
            {
                Descriptor = descriptor,
                ClassNameForDebug = classNameForDebug,
            };
            string coatingName = DeriveCoatingName(unitSeed);

            List<string> moduleTokens = GetCollectionTokens(obj, "ModulesDescriptors", unitIndex.NameById)
                .Select(NormalizeDescriptorToken)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            string derivedReferenceMeshDescriptor = string.IsNullOrWhiteSpace(coatingName)
                ? string.Empty
                : "Modele_" + coatingName;
            string referenceMeshDescriptor = derivedReferenceMeshDescriptor;
            if (string.IsNullOrWhiteSpace(referenceMeshDescriptor) ||
                (!resourceMeshMap.ContainsKey(referenceMeshDescriptor) && !string.IsNullOrWhiteSpace(coatingName)))
            {
                referenceMeshDescriptor = moduleTokens.FirstOrDefault(x =>
                    DescriptorMatchesCoating(x, coatingName, "Modele_")) ?? referenceMeshDescriptor;
            }
            if (string.IsNullOrWhiteSpace(referenceMeshDescriptor))
            {
                referenceMeshDescriptor = moduleTokens.FirstOrDefault(x =>
                    resourceMeshMap.ContainsKey(x) ||
                    x.StartsWith("Modele_", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
            }

            string derivedWeaponDescriptor = string.IsNullOrWhiteSpace(coatingName)
                ? string.Empty
                : "WeaponDescriptor_" + coatingName;
            string weaponDescriptor = derivedWeaponDescriptor;
            if (string.IsNullOrWhiteSpace(weaponDescriptor))
            {
                weaponDescriptor = moduleTokens.FirstOrDefault(x =>
                    DescriptorMatchesCoating(x, coatingName, "WeaponDescriptor_")) ?? string.Empty;
            }
            if (string.IsNullOrWhiteSpace(weaponDescriptor))
            {
                weaponDescriptor = moduleTokens.FirstOrDefault(x =>
                    x.StartsWith("WeaponDescriptor_", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
            }

            resourceMeshMap.TryGetValue(referenceMeshDescriptor, out string? assetPath);

            outUnits.Add(new UnitInfo
            {
                Descriptor = descriptor,
                ClassNameForDebug = classNameForDebug,
                CoatingName = coatingName,
                GameplayBBoxBoneName = GetStringProperty(obj, "GameplayBBoxBoneName", unitIndex.NameById),
                ReferenceMeshDescriptor = referenceMeshDescriptor,
                ReferenceMeshAssetPath = assetPath ?? string.Empty,
                WeaponDescriptor = weaponDescriptor,
            });
        }

        return outUnits;
    }

    private static Dictionary<string, WeaponDescriptorInfo> ParseWeaponsDirect(BinaryIndex weaponIndex)
    {
        var outWeapons = new Dictionary<string, WeaponDescriptorInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (NdfObject obj in weaponIndex.Binary.Instances.Where(x =>
                     x != null &&
                     x.Class != null &&
                     string.Equals(x.Class.Name, "TWeaponManagerModuleDescriptor", StringComparison.OrdinalIgnoreCase)))
        {
            string name = ResolveObjectName(obj, weaponIndex.NameById);
            var turrets = new List<TurretInfo>();

            foreach (NdfObject turretObj in GetCollectionObjectInstances(obj, "TurretDescriptorList"))
            {
                string tag = GetStringProperty(turretObj, "Tag", weaponIndex.NameById);
                int ordinal = GetIntProperty(turretObj, "YulBoneOrdinal");
                if (string.IsNullOrWhiteSpace(tag) || ordinal <= 0)
                    continue;

                if (!turrets.Any(x =>
                        string.Equals(x.Tag, tag, StringComparison.OrdinalIgnoreCase) &&
                        x.YulBoneOrdinal == ordinal))
                {
                    turrets.Add(new TurretInfo
                    {
                        Tag = tag,
                        YulBoneOrdinal = ordinal,
                    });
                }
            }

            outWeapons[name] = new WeaponDescriptorInfo
            {
                Name = name,
                Turrets = turrets,
            };
        }

        return outWeapons;
    }

    private static Dictionary<string, DepictionRegistrationInfo> ParseDepictionRegistrationsDirect(
        BinaryIndex depictionIndex,
        IReadOnlyDictionary<string, string> resourceMeshMap)
    {
        var outDepictions = new Dictionary<string, DepictionRegistrationInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (NdfObject registration in depictionIndex.Binary.Instances.Where(x =>
                     x != null &&
                     x.Class != null &&
                     string.Equals(x.Class.Name, "TMimeticUnitRegistration", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (KeyValuePair<string, NdfObject> pair in GetStringObjectPairsFromCollection(
                         registration,
                         "MimeticUnit",
                         depictionIndex.NameById))
            {
                string coatingName = pair.Key.Trim();
                if (string.IsNullOrWhiteSpace(coatingName))
                    continue;

                DepictionRegistrationInfo info = BuildDepictionRegistrationInfo(
                    coatingName,
                    pair.Value,
                    depictionIndex,
                    resourceMeshMap
                );

                outDepictions[coatingName] = info;
            }
        }

        return outDepictions;
    }

    private static DepictionRegistrationInfo BuildDepictionRegistrationInfo(
        string coatingName,
        NdfObject depiction,
        BinaryIndex depictionIndex,
        IReadOnlyDictionary<string, string> resourceMeshMap)
    {
        List<NdfObject> operatorObjects = GetCollectionObjectInstances(depiction, "Operators");
        var operators = CollectOperatorLabels(operatorObjects, depictionIndex.NameById);
        var subdepictions = new List<SubDepictionInfo>();
        var transformDebug = new List<TransformDebugInfo>();
        transformDebug.AddRange(CollectTransformDebugEntries(
            depiction,
            depictionIndex.NameById,
            "depiction",
            coatingName
        ));
        foreach (NdfObject op in operatorObjects)
        {
            transformDebug.AddRange(CollectTransformDebugEntries(
                op,
                depictionIndex.NameById,
                "operator",
                ResolveSemanticOperatorName(op, depictionIndex.NameById)
            ));
        }

        foreach (NdfObject subObj in GetCollectionObjectInstances(depiction, "SubDepictions"))
        {
            List<string> anchors = GetCollectionTokens(subObj, "Anchors", depictionIndex.NameById);
            string singleAnchor = GetStringProperty(subObj, "Anchor", depictionIndex.NameById);
            if (!string.IsNullOrWhiteSpace(singleAnchor))
                AppendDistinct(anchors, new[] { singleAnchor });
            NdfObject? subDepiction = GetObjectPropertyInstance(subObj, "Depiction");
            List<NdfObject> subOperators = subDepiction == null
                ? new List<NdfObject>()
                : GetCollectionObjectInstances(subDepiction, "Operators");

            string meshDescriptor = subDepiction == null
                ? string.Empty
                : ResolveDepictionMeshDescriptor(subDepiction, depictionIndex.NameById);

            resourceMeshMap.TryGetValue(meshDescriptor, out string? meshAssetPath);
            var subDebug = new List<TransformDebugInfo>();
            subDebug.AddRange(CollectTransformDebugEntries(
                subObj,
                depictionIndex.NameById,
                "subdepiction_ref",
                coatingName
            ));
            if (subDepiction != null)
            {
                subDebug.AddRange(CollectTransformDebugEntries(
                    subDepiction,
                    depictionIndex.NameById,
                    "subdepiction",
                    coatingName
                ));
            }
            foreach (NdfObject subOperator in subOperators)
            {
                subDebug.AddRange(CollectTransformDebugEntries(
                    subOperator,
                    depictionIndex.NameById,
                    "sub_operator",
                    ResolveSemanticOperatorName(subOperator, depictionIndex.NameById)
                ));
            }

            subdepictions.Add(new SubDepictionInfo
            {
                CoatingName = coatingName,
                Anchors = anchors,
                MeshDescriptor = meshDescriptor,
                MeshAssetPath = meshAssetPath ?? string.Empty,
                Operators = CollectOperatorLabels(subOperators, depictionIndex.NameById),
                WeaponFxAnchors = CollectOperatorAnchors(subOperators, depictionIndex.NameById),
                TransformDebug = subDebug,
                SemanticNodes = new List<SemanticNodeInfo>(),
            });
        }

        return new DepictionRegistrationInfo
        {
            CoatingName = coatingName,
            Alternatives = string.Empty,
            Operators = operators,
            WeaponFxAnchors = CollectOperatorAnchors(operatorObjects, depictionIndex.NameById),
            SubDepictions = subdepictions,
            TransformDebug = transformDebug,
            SemanticNodes = new List<SemanticNodeInfo>(),
        };
    }

    private static Dictionary<string, WeaponDescriptorInfo> MergeWeaponInfos(
        IReadOnlyDictionary<string, WeaponDescriptorInfo> primary,
        IReadOnlyDictionary<string, WeaponDescriptorInfo> secondary)
    {
        var merged = new Dictionary<string, WeaponDescriptorInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in primary.Keys.Concat(secondary.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var turrets = new List<TurretInfo>();
            if (primary.TryGetValue(key, out WeaponDescriptorInfo? p))
            {
                foreach (TurretInfo turret in p.Turrets)
                    AppendTurret(turrets, turret.Tag, turret.YulBoneOrdinal);
            }
            if (secondary.TryGetValue(key, out WeaponDescriptorInfo? s))
            {
                foreach (TurretInfo turret in s.Turrets)
                    AppendTurret(turrets, turret.Tag, turret.YulBoneOrdinal);
            }

            merged[key] = new WeaponDescriptorInfo
            {
                Name = key,
                Turrets = turrets
                    .OrderBy(x => x.YulBoneOrdinal)
                    .ThenBy(x => x.Tag, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };
        }

        return merged;
    }

    private static Dictionary<string, DepictionRegistrationInfo> MergeDepictionInfos(
        IReadOnlyDictionary<string, DepictionRegistrationInfo> primary,
        IReadOnlyDictionary<string, DepictionRegistrationInfo> secondary)
    {
        var merged = new Dictionary<string, DepictionRegistrationInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in primary.Keys.Concat(secondary.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            primary.TryGetValue(key, out DepictionRegistrationInfo? p);
            secondary.TryGetValue(key, out DepictionRegistrationInfo? s);

            var operators = new List<string>();
            var weaponFxAnchors = new List<string>();
            var subdepictions = new List<SubDepictionInfo>();
            var transformDebug = new List<TransformDebugInfo>();
            var semanticNodes = new List<SemanticNodeInfo>();

            if (p != null)
            {
                AppendDistinct(operators, p.Operators);
                AppendDistinct(weaponFxAnchors, p.WeaponFxAnchors);
                foreach (SubDepictionInfo sub in p.SubDepictions)
                    MergeSubDepiction(subdepictions, sub);
                transformDebug.AddRange(p.TransformDebug);
                semanticNodes.AddRange(p.SemanticNodes);
            }

            if (s != null)
            {
                AppendDistinct(operators, s.Operators);
                AppendDistinct(weaponFxAnchors, s.WeaponFxAnchors);
                foreach (SubDepictionInfo sub in s.SubDepictions)
                    MergeSubDepiction(subdepictions, sub);
                transformDebug.AddRange(s.TransformDebug);
                semanticNodes.AddRange(s.SemanticNodes);
            }

            merged[key] = new DepictionRegistrationInfo
            {
                CoatingName = key,
                Alternatives = !string.IsNullOrWhiteSpace(p?.Alternatives) ? p.Alternatives : s?.Alternatives ?? string.Empty,
                Operators = operators,
                WeaponFxAnchors = weaponFxAnchors,
                SubDepictions = subdepictions,
                TransformDebug = transformDebug,
                SemanticNodes = semanticNodes,
            };
        }

        return merged;
    }

    private static void MergeSubDepiction(List<SubDepictionInfo> target, SubDepictionInfo incoming)
    {
        if (incoming == null)
            return;

        int index = target.FindIndex(existing =>
            string.Equals(existing.MeshDescriptor, incoming.MeshDescriptor, StringComparison.OrdinalIgnoreCase) &&
            string.Join("|", existing.Anchors.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                .Equals(
                    string.Join("|", incoming.Anchors.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                    StringComparison.OrdinalIgnoreCase
                ));

        if (index < 0)
        {
            target.Add(new SubDepictionInfo
            {
                CoatingName = incoming.CoatingName,
                Anchors = incoming.Anchors.ToList(),
                MeshDescriptor = incoming.MeshDescriptor,
                MeshAssetPath = incoming.MeshAssetPath,
                Operators = incoming.Operators.ToList(),
                WeaponFxAnchors = incoming.WeaponFxAnchors.ToList(),
                TransformDebug = incoming.TransformDebug.ToList(),
                SemanticNodes = incoming.SemanticNodes.ToList(),
            });
            return;
        }

        SubDepictionInfo current = target[index];
        var anchors = current.Anchors.ToList();
        var operators = current.Operators.ToList();
        var fxAnchors = current.WeaponFxAnchors.ToList();
        var transformDebug = current.TransformDebug.ToList();
        var semanticNodes = current.SemanticNodes.ToList();
        AppendDistinct(anchors, incoming.Anchors);
        AppendDistinct(operators, incoming.Operators);
        AppendDistinct(fxAnchors, incoming.WeaponFxAnchors);
        transformDebug.AddRange(incoming.TransformDebug);
        semanticNodes.AddRange(incoming.SemanticNodes);
        target[index] = new SubDepictionInfo
        {
            CoatingName = !string.IsNullOrWhiteSpace(current.CoatingName) ? current.CoatingName : incoming.CoatingName,
            Anchors = anchors,
            MeshDescriptor = !string.IsNullOrWhiteSpace(current.MeshDescriptor) ? current.MeshDescriptor : incoming.MeshDescriptor,
            MeshAssetPath = !string.IsNullOrWhiteSpace(current.MeshAssetPath) ? current.MeshAssetPath : incoming.MeshAssetPath,
            Operators = operators,
            WeaponFxAnchors = fxAnchors,
            TransformDebug = transformDebug,
            SemanticNodes = semanticNodes,
        };
    }

    private static void AppendTurret(List<TurretInfo> turrets, string tag, int ordinal)
    {
        string cleanTag = (tag ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cleanTag) || ordinal <= 0)
            return;

        if (turrets.Any(x =>
                string.Equals(x.Tag, cleanTag, StringComparison.OrdinalIgnoreCase) &&
                x.YulBoneOrdinal == ordinal))
        {
            return;
        }

        turrets.Add(new TurretInfo
        {
            Tag = cleanTag,
            YulBoneOrdinal = ordinal,
        });
    }

    private static void AppendDerivedTurrets(List<TurretInfo> turrets, IEnumerable<string> tokens)
    {
        foreach (string token in tokens ?? Enumerable.Empty<string>())
        {
            foreach (int ordinal in ExtractTurretOrdinals(token))
                AppendTurret(turrets, $"tourelle{ordinal}", ordinal);
        }
    }

    private static IEnumerable<int> ExtractTurretOrdinals(string token)
    {
        string raw = (token ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
            yield break;

        foreach (Match match in Regex.Matches(raw, @"(?i)(?:fx_tourelle|base_tourelle_|tourelle)([0-9]+)|(?i)turret_([0-9]+)_"))
        {
            string numText = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            if (!int.TryParse(numText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ordinal))
                continue;
            if (ordinal <= 0)
                continue;
            yield return ordinal;
        }
    }

    private static string ResolveDepictionMeshDescriptor(
        NdfObject depiction,
        IReadOnlyDictionary<uint, string> nameById)
    {
        string direct = NormalizeDescriptorToken(GetStringProperty(depiction, "MeshDescriptor", nameById));
        if (!string.IsNullOrWhiteSpace(direct))
            return direct;

        foreach (NdfObject alternative in GetCollectionObjectInstances(depiction, "DepictionAlternatives"))
        {
            string altMesh = NormalizeDescriptorToken(GetStringProperty(alternative, "MeshDescriptor", nameById));
            if (!string.IsNullOrWhiteSpace(altMesh))
                return altMesh;

            altMesh = NormalizeDescriptorToken(GetStringProperty(alternative, "ReferenceMesh", nameById));
            if (!string.IsNullOrWhiteSpace(altMesh))
                return altMesh;
        }

        direct = NormalizeDescriptorToken(GetStringProperty(depiction, "ReferenceMesh", nameById));
        if (!string.IsNullOrWhiteSpace(direct))
            return direct;

        return string.Empty;
    }

    private static List<string> CollectOperatorLabels(
        IEnumerable<NdfObject> operatorObjects,
        IReadOnlyDictionary<uint, string> nameById)
    {
        var labels = new List<string>();
        foreach (NdfObject op in operatorObjects)
        {
            string label = ResolveSemanticOperatorName(op, nameById);
            if (!string.IsNullOrWhiteSpace(label))
                AppendDistinct(labels, new[] { label });
        }
        return labels;
    }

    private static List<string> CollectOperatorAnchors(
        IEnumerable<NdfObject> operatorObjects,
        IReadOnlyDictionary<uint, string> nameById)
    {
        var anchors = new List<string>();
        foreach (NdfObject op in operatorObjects)
        {
            AppendDistinct(anchors, GetCollectionTokens(op, "Anchors", nameById));
        }
        return anchors;
    }

    private static string ResolveSemanticOperatorName(
        NdfObject op,
        IReadOnlyDictionary<uint, string> nameById)
    {
        string className = op.Class?.Name ?? string.Empty;
        string resolvedName = ResolveObjectName(op, nameById);
        string operatorId = GetStringProperty(op, "OperatorId", nameById);

        if (className.Contains("CaterpillarTrack", StringComparison.OrdinalIgnoreCase))
            return "DepictionOperator_Propulsion_ContinuousTrack";

        if (className.Contains("WheelsOperator", StringComparison.OrdinalIgnoreCase))
            return "DepictionOperator_Propulsion_Wheels";

        if (!string.IsNullOrWhiteSpace(resolvedName) &&
            !resolvedName.StartsWith("public_", StringComparison.OrdinalIgnoreCase) &&
            !resolvedName.StartsWith(className + "_", StringComparison.OrdinalIgnoreCase))
        {
            return resolvedName;
        }

        if (!string.IsNullOrWhiteSpace(operatorId))
            return string.IsNullOrWhiteSpace(className) ? operatorId : $"{className}:{operatorId}";

        return className;
    }

    private static List<ScriptBlock> ParseTopLevelBlocks(string script)
    {
        string normalized = (script ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        string[] lines = normalized.Split('\n');
        var blocks = new List<ScriptBlock>();

        for (int i = 0; i < lines.Length; i++)
        {
            Match header = HeaderRegex.Match(lines[i]);
            Match unnamedHeader = header.Success ? Match.Empty : UnnamedHeaderRegex.Match(lines[i]);
            if (!header.Success && !unnamedHeader.Success)
                continue;

            bool isUnnamed = !header.Success && unnamedHeader.Success;
            string name = isUnnamed ? $"unnamed_{blocks.Count + 1}" : header.Groups["name"].Value.Trim();
            string className = isUnnamed ? unnamedHeader.Groups["class"].Value.Trim() : header.Groups["class"].Value.Trim();
            var chunk = new List<string> { lines[i] };

            int parenDepth = CountChar(lines[i], '(') - CountChar(lines[i], ')');
            int cursor = i + 1;
            if (parenDepth <= 0 && cursor < lines.Length && lines[cursor].TrimStart().StartsWith("(", StringComparison.Ordinal))
            {
                chunk.Add(lines[cursor]);
                parenDepth += CountChar(lines[cursor], '(') - CountChar(lines[cursor], ')');
                cursor++;
            }

            if (parenDepth <= 0)
                continue;

            for (; cursor < lines.Length; cursor++)
            {
                chunk.Add(lines[cursor]);
                parenDepth += CountChar(lines[cursor], '(') - CountChar(lines[cursor], ')');
                if (parenDepth <= 0)
                    break;
            }

            i = Math.Max(i, cursor - 1);
            blocks.Add(new ScriptBlock
            {
                Name = name,
                ClassName = className,
                Text = string.Join("\n", chunk),
            });
        }

        return blocks;
    }

    private static Dictionary<string, string> ParseDepictionResources(string script)
    {
        var constants = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var outMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string currentExport = string.Empty;
        string currentMeshExpr = string.Empty;
        bool inResource = false;

        foreach (string rawLine in (script ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            Match constMatch = ConstMeshRegex.Match(line);
            if (constMatch.Success)
            {
                string exportName = constMatch.Groups["name"].Value.Trim();
                string assetPath = NormalizeGameDataAssetPath(constMatch.Groups["path"].Value);
                if (!string.IsNullOrWhiteSpace(exportName) && !string.IsNullOrWhiteSpace(assetPath))
                    constants[exportName] = assetPath;
                continue;
            }

            Match resourceMatch = ResourceMeshRegex.Match(line);
            if (resourceMatch.Success)
            {
                currentExport = resourceMatch.Groups["name"].Value.Trim();
                currentMeshExpr = string.Empty;
                inResource = true;
                continue;
            }

            if (!inResource)
                continue;

            Match meshAssign = MeshAssignRegex.Match(line);
            if (meshAssign.Success)
                currentMeshExpr = meshAssign.Groups["value"].Value.Trim();

            if (line == ")")
            {
                string asset = ResolveMeshExpr(currentMeshExpr, constants);
                if (!string.IsNullOrWhiteSpace(currentExport) && !string.IsNullOrWhiteSpace(asset))
                    outMap[currentExport] = asset;
                currentExport = string.Empty;
                currentMeshExpr = string.Empty;
                inResource = false;
            }
        }

        return outMap;
    }

    private static List<UnitInfo> ParseUnits(string script, IReadOnlyDictionary<string, string> resourceMeshMap)
    {
        var outUnits = new List<UnitInfo>();

        foreach (ScriptBlock block in ParseTopLevelBlocks(script))
        {
            if (!string.Equals(block.ClassName, "TEntityDescriptor", StringComparison.OrdinalIgnoreCase))
                continue;

            Match refMeshMatch = ReferenceMeshRegex.Match(block.Text);
            if (!refMeshMatch.Success)
                continue;

            string referenceMesh = refMeshMatch.Groups["name"].Value.Trim();
            if (string.IsNullOrWhiteSpace(referenceMesh))
                continue;

            string coatingName = MimeticNameRegex.Match(block.Text).Groups["value"].Value.Trim();
            string classNameForDebug = ClassNameForDebugRegex.Match(block.Text).Groups["value"].Value.Trim();
            string gameplayBBoxBone = GameplayBBoxBoneRegex.Match(block.Text).Groups["value"].Value.Trim();
            string weaponDescriptor = WeaponDescriptorRegex.Match(block.Text).Groups["name"].Value.Trim();

            resourceMeshMap.TryGetValue(referenceMesh, out string? assetPath);

            outUnits.Add(new UnitInfo
            {
                Descriptor = block.Name,
                ClassNameForDebug = classNameForDebug,
                CoatingName = coatingName,
                GameplayBBoxBoneName = gameplayBBoxBone,
                ReferenceMeshDescriptor = referenceMesh,
                ReferenceMeshAssetPath = assetPath ?? string.Empty,
                WeaponDescriptor = weaponDescriptor,
            });
        }

        return outUnits;
    }

    private static Dictionary<string, WeaponDescriptorInfo> ParseWeapons(string script)
    {
        var outWeapons = new Dictionary<string, WeaponDescriptorInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (ScriptBlock block in ParseTopLevelBlocks(script))
        {
            if (!string.Equals(block.ClassName, "TWeaponManagerModuleDescriptor", StringComparison.OrdinalIgnoreCase))
                continue;

            var turrets = new List<TurretInfo>();
            foreach (string turretBlock in ExtractNamedParenBlocks(block.Text, TurretHeaderRegex))
            {
                string tag = TagRegex.Match(turretBlock).Groups["value"].Value.Trim();
                string ordinalText = YulBoneOrdinalRegex.Match(turretBlock).Groups["value"].Value.Trim();
                if (string.IsNullOrWhiteSpace(tag) || !int.TryParse(ordinalText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ordinal))
                    continue;
                if (!turrets.Any(x =>
                    string.Equals(x.Tag, tag, StringComparison.OrdinalIgnoreCase) &&
                    x.YulBoneOrdinal == ordinal))
                {
                    turrets.Add(new TurretInfo
                    {
                        Tag = tag,
                        YulBoneOrdinal = ordinal,
                    });
                }
            }

            outWeapons[block.Name] = new WeaponDescriptorInfo
            {
                Name = block.Name,
                Turrets = turrets,
            };
        }

        return outWeapons;
    }

    private static Dictionary<string, WeaponOperatorInfo> ParseWeaponOperators(string script)
    {
        var outOperators = new Dictionary<string, WeaponOperatorInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (ScriptBlock block in ParseTopLevelBlocks(script))
        {
            if (!block.Name.StartsWith("DepictionOperator_", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!block.Name.Contains("_Weapon", StringComparison.OrdinalIgnoreCase))
                continue;

            List<string> anchors = ExtractQuotedStrings(ExtractListBody(block.Text, "Anchors"));
            outOperators[block.Name] = new WeaponOperatorInfo
            {
                Name = block.Name,
                Anchors = anchors,
            };
        }

        return outOperators;
    }

    private static Dictionary<string, DepictionRegistrationInfo> ParseDepictionRegistrations(
        string script,
        IReadOnlyDictionary<string, string> resourceMeshMap,
        IReadOnlyDictionary<string, WeaponOperatorInfo> weaponOperators)
    {
        var outDepictions = new Dictionary<string, DepictionRegistrationInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (ScriptBlock block in ParseTopLevelBlocks(script))
        {
            if (!string.Equals(block.ClassName, "TacticVehicleDepictionRegistration", StringComparison.OrdinalIgnoreCase))
                continue;

            string coatingName = CoatingNameRegex.Match(block.Text).Groups["value"].Value.Trim();
            if (string.IsNullOrWhiteSpace(coatingName))
                continue;

            string alternatives = AlternativesRegex.Match(block.Text).Groups["value"].Value.Trim();
            List<string> operators = ParseReferenceList(ExtractListBody(block.Text, "Operators"));
            List<SubDepictionInfo> subdepictions = new();
            string subdepictionsBody = ExtractListBody(block.Text, "SubDepictions");
            foreach (string subBlock in ExtractNamedParenBlocks(subdepictionsBody, new Regex(@"\bTSubDepiction\s*\(", RegexOptions.Compiled)))
            {
                List<string> anchors = ExtractQuotedStrings(ExtractListBody(subBlock, "Anchors"));
                Match singleAnchorMatch = Regex.Match(subBlock, @"Anchor\s*=\s*[""'](?<value>[^""']+)[""']", RegexOptions.IgnoreCase);
                if (singleAnchorMatch.Success)
                    AppendDistinct(anchors, new[] { singleAnchorMatch.Groups["value"].Value.Trim() });
                List<string> subOperators = ParseReferenceList(ExtractListBody(subBlock, "Operators"));
                string meshDescriptor = MeshDescriptorRegex.Match(subBlock).Groups["name"].Value.Trim();
                resourceMeshMap.TryGetValue(meshDescriptor, out string? meshAssetPath);

                subdepictions.Add(new SubDepictionInfo
                {
                    CoatingName = coatingName,
                    Anchors = anchors,
                    MeshDescriptor = meshDescriptor,
                    MeshAssetPath = meshAssetPath ?? string.Empty,
                    Operators = subOperators,
                    WeaponFxAnchors = ResolveOperatorAnchors(subOperators, weaponOperators),
                });
            }

            outDepictions[coatingName] = new DepictionRegistrationInfo
            {
                CoatingName = coatingName,
                Alternatives = alternatives,
                Operators = operators,
                WeaponFxAnchors = ResolveOperatorAnchors(operators, weaponOperators),
                SubDepictions = subdepictions,
            };
        }

        return outDepictions;
    }

    private static List<UnitInfo> MatchUnits(string assetPath, IEnumerable<UnitInfo> units)
    {
        string assetNorm = NormalizeAssetPath(assetPath);
        string assetFile = Path.GetFileName(assetNorm).ToLowerInvariant();
        string assetStem = Path.GetFileNameWithoutExtension(assetNorm).ToLowerInvariant();

        List<UnitInfo> exact = units
            .Where(unit => string.Equals(NormalizeAssetPath(unit.ReferenceMeshAssetPath), assetNorm, StringComparison.OrdinalIgnoreCase))
            .OrderBy(unit => unit.CoatingName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (exact.Count > 0)
            return exact;

        List<UnitInfo> sameFile = units
            .Where(unit => Path.GetFileName(NormalizeAssetPath(unit.ReferenceMeshAssetPath)).Equals(assetFile, StringComparison.OrdinalIgnoreCase))
            .Where(unit => Path.GetFileNameWithoutExtension(NormalizeAssetPath(unit.ReferenceMeshAssetPath)).Equals(assetStem, StringComparison.OrdinalIgnoreCase))
            .OrderBy(unit => unit.CoatingName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sameFile.Count > 0)
            return sameFile;

        return new List<UnitInfo>();
    }

    private static string ResolveTrackKind(IEnumerable<string> operators)
    {
        foreach (string op in operators)
        {
            string low = op.Trim().ToLowerInvariant();
            if (low.Contains("propulsion_continuoustrack", StringComparison.Ordinal) ||
                low.Contains("caterpillartrack", StringComparison.Ordinal) ||
                low.Contains("tracks", StringComparison.Ordinal))
                return "continuous_track";
            if (low.Contains("propulsion_wheels", StringComparison.Ordinal) ||
                low.Contains("wheelsoperator", StringComparison.Ordinal) ||
                low.Contains(":wheels", StringComparison.Ordinal))
                return "wheeled";
        }
        return string.Empty;
    }

    private static List<string> ResolveOperatorAnchors(
        IEnumerable<string> operatorRefs,
        IReadOnlyDictionary<string, WeaponOperatorInfo> weaponOperators)
    {
        var anchors = new List<string>();
        foreach (string opRef in operatorRefs)
        {
            string key = CanonicalReferenceToken(opRef);
            if (string.IsNullOrWhiteSpace(key))
                continue;
            if (!weaponOperators.TryGetValue(key, out WeaponOperatorInfo? op))
                continue;
            AppendDistinct(anchors, op.Anchors);
        }
        return anchors;
    }

    private static string ResolveMeshExpr(string expr, IReadOnlyDictionary<string, string> constants)
    {
        string cleaned = (expr ?? string.Empty).Split(new[] { "//" }, StringSplitOptions.None)[0].Trim().TrimEnd(',');
        if (string.IsNullOrWhiteSpace(cleaned))
            return string.Empty;

        Match literal = Regex.Match(cleaned, @"^[""'](?:GameData:)?/?(?<path>[^""']+?\.fbx)[""']$", RegexOptions.IgnoreCase);
        if (literal.Success)
            return NormalizeGameDataAssetPath(literal.Groups["path"].Value);

        Match symbol = Regex.Match(cleaned, @"^(?<name>[A-Za-z0-9_]+)$");
        if (symbol.Success && constants.TryGetValue(symbol.Groups["name"].Value, out string? resolved))
            return resolved;

        return string.Empty;
    }

    private static string NormalizeGameDataAssetPath(string path)
    {
        string raw = path ?? string.Empty;
        raw = raw.Replace('\\', '/').Trim().Trim('"').Trim('\'');
        raw = Regex.Replace(raw, @"(?i)^gamedata:/", string.Empty);
        raw = Regex.Replace(raw, @"(?i)^zz:", string.Empty);
        raw = raw.TrimStart('/');
        return NormalizeAssetPath(raw);
    }

    private static string ExtractListBody(string text, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(propertyName))
            return string.Empty;

        Match propertyMatch = Regex.Match(text, $@"\b{Regex.Escape(propertyName)}\s*=", RegexOptions.IgnoreCase);
        if (!propertyMatch.Success)
            return string.Empty;

        int start = text.IndexOf('[', propertyMatch.Index + propertyMatch.Length);
        if (start < 0)
            return string.Empty;

        int depth = 0;
        for (int i = start; i < text.Length; i++)
        {
            if (text[i] == '[')
            {
                depth++;
            }
            else if (text[i] == ']')
            {
                depth--;
                if (depth == 0)
                    return text.Substring(start + 1, i - start - 1);
            }
        }

        return string.Empty;
    }

    private static List<string> ExtractQuotedStrings(string text)
    {
        var values = new List<string>();
        foreach (Match match in QuotedStringRegex.Matches(text ?? string.Empty))
        {
            string value = match.Groups["value"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(value) &&
                !values.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase)))
            {
                values.Add(value);
            }
        }
        return values;
    }

    private static List<string> ParseReferenceList(string text)
    {
        var outTokens = new List<string>();
        foreach (string rawLine in (text ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            string line = rawLine.Trim().TrimEnd(',');
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (line == "[" || line == "]")
                continue;
            string token = CanonicalReferenceToken(line);
            if (string.IsNullOrWhiteSpace(token))
                continue;
            if (!outTokens.Any(x => string.Equals(x, token, StringComparison.OrdinalIgnoreCase)))
                outTokens.Add(token);
        }
        return outTokens;
    }

    private static string CanonicalReferenceToken(string text)
    {
        string raw = (text ?? string.Empty).Trim().TrimEnd(',');
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        int slash = raw.LastIndexOf('/');
        if (slash >= 0 && slash + 1 < raw.Length)
            raw = raw[(slash + 1)..];
        raw = raw.Trim().TrimStart('~').TrimStart('$');
        return raw;
    }

    private static List<string> ExtractNamedParenBlocks(string text, Regex headerRegex)
    {
        var blocks = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return blocks;

        string source = text;
        int cursor = 0;
        while (cursor < source.Length)
        {
            Match match = headerRegex.Match(source, cursor);
            if (!match.Success)
                break;

            int openParen = source.IndexOf('(', match.Index);
            if (openParen < 0)
                break;

            int depth = 0;
            int end = -1;
            for (int i = openParen; i < source.Length; i++)
            {
                if (source[i] == '(')
                {
                    depth++;
                }
                else if (source[i] == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        end = i;
                        break;
                    }
                }
            }

            if (end < 0)
                break;

            blocks.Add(source.Substring(match.Index, end - match.Index + 1));
            cursor = end + 1;
        }

        return blocks;
    }

    private static int CountChar(string text, char ch)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        int count = 0;
        foreach (char current in text)
        {
            if (current == ch)
                count++;
        }
        return count;
    }

    private static void AppendDistinct(ICollection<string> target, IEnumerable<string> values)
    {
        foreach (string value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;
            if (!target.Any(existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)))
                target.Add(value);
        }
    }
}
}
