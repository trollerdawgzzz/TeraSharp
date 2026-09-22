// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using Microsoft.Extensions.Logging;

namespace TeraSharp.Arbiter.Protocol;

public sealed class DefinitionRegistry
{
    private readonly Dictionary<string, PacketDef> _latest = new();
    private readonly Dictionary<(string, int), PacketDef> _byVersion = new();
    private readonly ILogger _log;

    public DefinitionRegistry(ILogger log) => _log = log;

    public int Count => _latest.Count;

    public static DefinitionRegistry LoadFromFolder(string folder, ILogger log)
    {
        var reg = new DefinitionRegistry(log);
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"definition folder not found: {folder}");

        int files = 0, skipped = 0;
        var unknownTypes = new HashSet<string>();
        var skippedNames = new List<string>();

        foreach (var path in Directory.EnumerateFiles(folder, "*.def"))
        {
            PacketDef def;
            try
            {
                def = DefinitionParser.ParseFile(path);
            }
            catch (Exception ex)
            {
                log.LogWarning("Failed to parse {File}: {Msg}", Path.GetFileName(path), ex.Message);
                continue;
            }

            if (def.Skipped)
            {
                skipped++;
                skippedNames.Add(Path.GetFileName(path));
                continue;
            }

            files++;
            reg._byVersion[(def.Name, def.Version)] = def;
            if (!reg._latest.TryGetValue(def.Name, out var existing) || def.Version > existing.Version)
                reg._latest[def.Name] = def;

            foreach (var tok in def.UnknownTypes)
                unknownTypes.Add(tok);
        }

        log.LogInformation("Loaded {Files} schema defs ({Names} distinct packets), skipped {Skipped}: {Skipped2}",
            files, reg._latest.Count, skipped, string.Join(", ", skippedNames));

        if (unknownTypes.Count > 0)
            log.LogWarning("Unrecognised type token(s): {Types}", string.Join(", ", unknownTypes.OrderBy(x => x)));

        return reg;
    }

    public PacketDef? Get(string name) => _latest.TryGetValue(name, out var def) ? def : null;
    public PacketDef? Get(string name, int version) => _byVersion.TryGetValue((name, version), out var def) ? def : null;
    public bool Has(string name) => _latest.ContainsKey(name);

    /// <summary>Register a programmatically-built def (e.g. for packets not in the data folder).</summary>
    public void Register(PacketDef def)
    {
        _byVersion[(def.Name, def.Version)] = def;
        if (!_latest.TryGetValue(def.Name, out var existing) || def.Version > existing.Version)
            _latest[def.Name] = def;
    }

    /// <summary>
    /// Register a simple flat packet definition if no def is already loaded for that name.
    /// Fields are (typeName, fieldName) tuples using the same type tokens as .def files.
    /// </summary>
    public void RegisterIfMissing(string packetName, params (string type, string name)[] fields)
    {
        if (_latest.ContainsKey(packetName)) return;
        var fieldDefs = new List<FieldDef>();
        foreach (var (t, n) in fields)
        {
            var kind = t.ToLowerInvariant() switch
            {
                "byte" or "uint8" => FieldKind.Byte,
                "bool" => FieldKind.Bool,
                "int16" => FieldKind.Int16,
                "uint16" => FieldKind.UInt16,
                "int32" => FieldKind.Int32,
                "uint32" => FieldKind.UInt32,
                "int64" => FieldKind.Int64,
                "uint64" => FieldKind.UInt64,
                "float" => FieldKind.Float,
                _ => FieldKind.Int32,
            };
            fieldDefs.Add(new FieldDef { Kind = kind, Name = n, RawType = t });
        }
        var def = new PacketDef { Name = packetName, Version = 1, Fields = fieldDefs };
        Register(def);
        _log.LogInformation("Registered inline def for {Name} ({Count} fields)", packetName, fields.Length);
    }

    /// <summary>
    /// Register a packet definition from inline def text (supports arrays, strings, refs).
    /// Only registers if no def already exists for this packet name.
    /// </summary>
    internal void RegisterFromDef(string name, string defText)
    {
        if (_latest.ContainsKey(name)) return;
        var def = DefinitionParser.ParseText(name, defText);
        Register(def);
        _log.LogInformation("Registered def-text for {Name} ({Count} fields)", name, def.Fields.Count);
    }
}
