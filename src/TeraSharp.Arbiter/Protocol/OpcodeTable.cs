// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Text.Json;

namespace TeraSharp.Arbiter.Protocol;

/// <summary>
/// Maps packet names to/from their numeric opcodes for a given protocol version.
///
/// Opcodes are remapped every TERA version, so this loads from the same
/// authoritative source the proxy uses: data.json, shaped as
/// { "maps": { "&lt;version&gt;": { "C_NAME": 12345, "S_NAME": 678, ... } } }.
///
/// Reusing the proxy's map (rather than hardcoding) means our opcodes are
/// guaranteed to match the client that the proxy already talks to. For 100.02
/// the version key is "376012".
/// </summary>
public sealed class OpcodeTable
{
    private readonly Dictionary<string, ushort> _nameToCode;
    private readonly Dictionary<ushort, string> _codeToName;

    public string Version { get; }

    private OpcodeTable(string version, Dictionary<string, ushort> nameToCode)
    {
        Version = version;
        _nameToCode = nameToCode;
        _codeToName = new Dictionary<ushort, string>(nameToCode.Count);
        foreach (var (name, code) in nameToCode)
            _codeToName[code] = name;
    }

    /// <summary>
    /// Load the opcode map for <paramref name="version"/> from a data.json file.
    /// </summary>
    public static OpcodeTable LoadFromFile(string dataJsonPath, string version)
    {
        using var stream = File.OpenRead(dataJsonPath);
        using var doc = JsonDocument.Parse(stream);

        if (!doc.RootElement.TryGetProperty("maps", out var maps))
            throw new InvalidDataException($"'maps' not found in {dataJsonPath}");

        if (!maps.TryGetProperty(version, out var versionMap))
            throw new InvalidDataException($"protocol version '{version}' not found in {dataJsonPath}");

        var dict = new Dictionary<string, ushort>(versionMap.EnumerateObject().Count());
        foreach (var prop in versionMap.EnumerateObject())
        {
            int code = prop.Value.GetInt32();
            if (code < 0 || code > ushort.MaxValue)
                throw new InvalidDataException($"opcode {code} for {prop.Name} out of ushort range");
            dict[prop.Name] = (ushort)code;
        }

        return new OpcodeTable(version, dict);
    }

    /// <summary>Opcode for a packet name. Throws if the name is unknown.</summary>
    public ushort this[string name] =>
        _nameToCode.TryGetValue(name, out var code)
            ? code
            : throw new KeyNotFoundException($"opcode name '{name}' not in map for version {Version}");

    public bool TryGetCode(string name, out ushort code) => _nameToCode.TryGetValue(name, out code);

    /// <summary>Name for an opcode, or the numeric form if unmapped (for logging).</summary>
    public string NameOf(ushort code) =>
        _codeToName.TryGetValue(code, out var name) ? name : $"(opcode {code})";

    public int Count => _nameToCode.Count;
}
