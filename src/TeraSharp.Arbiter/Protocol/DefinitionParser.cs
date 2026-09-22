// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

namespace TeraSharp.Arbiter.Protocol;

/// <summary>Field kinds in a TERA packet definition.</summary>
public enum FieldKind
{
    Byte, Bool, Int16, UInt16, Int32, UInt32, Int64, UInt64, Float, Double,
    Angle, Vec3, Vec3Fa, Customize, SkillId,
    String, Bytes, Array, Object,
    Unknown,
}

/// <summary>
/// One field in a packet definition. String/Bytes/Array split into a ref-header
/// (emitted first, in the order the def specifies) and the data (emitted later
/// at the referenced offset). RefId pairs a header with its data field.
/// </summary>
public sealed class FieldDef
{
    public required FieldKind Kind { get; init; }
    public required string Name { get; init; }
    public string RawType { get; init; } = "";
    public FieldKind? ElementKind { get; init; }
    public List<FieldDef> Children { get; } = new();

    public int RefId { get; set; }

    /// <summary>Non-null => synthetic header for the string/bytes/array with this RefId.</summary>
    public FieldKind? IsHeaderFor { get; set; }

    public int FixedSize => Kind switch
    {
        FieldKind.Byte or FieldKind.Bool => 1,
        FieldKind.Int16 or FieldKind.UInt16 or FieldKind.Angle => 2,
        FieldKind.Int32 or FieldKind.UInt32 or FieldKind.Float or FieldKind.SkillId => 4,
        FieldKind.Int64 or FieldKind.UInt64 or FieldKind.Double => 8,
        FieldKind.Customize => 8,
        FieldKind.Vec3 or FieldKind.Vec3Fa => 12,
        FieldKind.Unknown => 4,
        _ => 0,
    };
}

public sealed class PacketDef
{
    public required string Name { get; init; }
    public required int Version { get; init; }
    public required List<FieldDef> Fields { get; init; }
    public List<string> UnknownTypes { get; } = new();
    public bool Skipped { get; set; }
    public string? SkipReason { get; set; }
}

/// <summary>
/// Parses TERA .def files, replicating the tera-data parser's meta-ref model,
/// including EXPLICIT ref ordering.
///
/// A record on the wire is: [ref-header block][fixed fields][variable data].
/// The header block order is:
///   - If the record has explicit "ref NAME" lines, that order (each linked to
///     the later string/bytes/array field of the same NAME for its shape).
///   - Otherwise (implicit), the order the string/bytes/array fields appear.
/// Header shapes: string=offset(u16); bytes=offset(u16)+count(u16);
/// array=count(u16)+offset(u16). Offsets are packet-relative (include the header).
/// </summary>
public static class DefinitionParser
{
    // Intermediate node while parsing (before header insertion).
    private sealed class Node
    {
        public FieldKind Kind;
        public string Name = "";
        public string RawType = "";
        public FieldKind? ElementKind;
        public bool IsRef;               // this line was "ref NAME"
        public List<Node> Children = new();
    }

    public static PacketDef ParseFile(string path)
    {
        string fileName = System.IO.Path.GetFileNameWithoutExtension(path);
        int dot = fileName.LastIndexOf('.');
        string name; int version;
        if (dot > 0 && int.TryParse(fileName[(dot + 1)..], out version)) name = fileName[..dot];
        else { name = fileName; version = 1; }

        var lines = System.IO.File.ReadAllLines(path);
        var def = new PacketDef { Name = name, Version = version, Fields = new List<FieldDef>() };

        if (IsProbablyCode(lines)) { def.Skipped = true; def.SkipReason = "generated code"; return def; }

        var roots = ParseNodes(lines, def);
        int refCounter = 0;
        var fields = BuildRecord(roots, def, ref refCounter);
        def.Fields.AddRange(fields);
        return def;
    }

    /// <summary>Parse a def from inline text (for packets without .def files).</summary>
    internal static PacketDef ParseText(string name, string defText, int version = 1)
    {
        var lines = defText.Split('\n');
        var def = new PacketDef { Name = name, Version = version, Fields = new List<FieldDef>() };
        var roots = ParseNodes(lines, def);
        int refCounter = 0;
        var fields = BuildRecord(roots, def, ref refCounter);
        def.Fields.AddRange(fields);
        return def;
    }

    // Parse into a nested Node tree, preserving "ref" lines as IsRef nodes.
    private static List<Node> ParseNodes(string[] lines, PacketDef def)
    {
        var flat = new List<(int depth, Node node)>();

        foreach (var raw in lines)
        {
            string line = StripComment(raw).Trim();
            if (line.Length == 0) continue;

            int depth = 0, i = 0;
            while (i < line.Length)
            {
                while (i < line.Length && line[i] == ' ') i++;
                if (i < line.Length && line[i] == '-') { depth++; i++; }
                else break;
            }
            string rest = line[i..].Trim();
            if (rest.Length == 0) continue;

            var parts = rest.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            string typeToken = parts[0];
            string fieldName = parts[1];
            string lower = typeToken.ToLowerInvariant();

            if (lower == "count") continue;         // legacy, ignore
            if (lower is "ref" or "offset")          // explicit ref (offset = legacy ref)
            {
                flat.Add((depth, new Node { IsRef = true, Name = fieldName }));
                continue;
            }

            var (kind, elem) = MapKind(typeToken);
            if (kind == FieldKind.Unknown) def.UnknownTypes.Add(typeToken);
            flat.Add((depth, new Node { Kind = kind, Name = fieldName, RawType = typeToken, ElementKind = elem }));
        }

        // assemble tree
        var root = new List<Node>();
        var stack = new List<Node>();
        foreach (var (depth, node) in flat)
        {
            if (depth == 0) { root.Add(node); stack.Clear(); stack.Add(node); }
            else
            {
                while (stack.Count > depth) stack.RemoveAt(stack.Count - 1);
                if (stack.Count == 0) { root.Add(node); stack.Add(node); continue; }
                stack[depth - 1].Children.Add(node);
                if (stack.Count > depth) stack[depth] = node; else stack.Add(node);
            }
        }
        return root;
    }

    // Convert a record's nodes into the final FieldDef list with header block.
    private static List<FieldDef> BuildRecord(List<Node> nodes, PacketDef def, ref int refCounter)
    {
        // Separate explicit ref markers from real fields.
        var refOrder = new List<string>();          // names, in ref order
        var dataNodes = new List<Node>();
        foreach (var n in nodes)
        {
            if (n.IsRef) refOrder.Add(n.Name);
            else dataNodes.Add(n);
        }

        // Build FieldDefs for the data fields (recursing into arrays/objects).
        var built = new List<FieldDef>();
        var byName = new Dictionary<string, FieldDef>();
        foreach (var n in dataNodes)
        {
            FieldDef fd;
            if (n.Kind is FieldKind.Array or FieldKind.Object)
            {
                fd = new FieldDef { Kind = n.Kind, Name = n.Name, RawType = n.RawType, ElementKind = n.ElementKind };
                var childFields = BuildRecord(n.Children, def, ref refCounter);
                fd.Children.AddRange(childFields);
            }
            else
            {
                fd = new FieldDef { Kind = n.Kind, Name = n.Name, RawType = n.RawType, ElementKind = n.ElementKind };
            }
            if (fd.Kind is FieldKind.String or FieldKind.Bytes or FieldKind.Array)
                fd.RefId = ++refCounter;
            built.Add(fd);
            byName[fd.Name] = fd;
        }

        // Determine header order.
        List<FieldDef> headerSources;
        if (refOrder.Count > 0)
        {
            // Explicit: headers in ref-line order, linked by name.
            headerSources = new List<FieldDef>();
            foreach (var nm in refOrder)
                if (byName.TryGetValue(nm, out var fd) &&
                    fd.Kind is FieldKind.String or FieldKind.Bytes or FieldKind.Array)
                    headerSources.Add(fd);
        }
        else
        {
            // Implicit: headers in field-appearance order.
            headerSources = built.FindAll(fd =>
                fd.Kind is FieldKind.String or FieldKind.Bytes or FieldKind.Array);
        }

        var headers = new List<FieldDef>();
        foreach (var fd in headerSources)
            headers.Add(new FieldDef
            {
                Kind = FieldKind.UInt16,
                Name = fd.Name + "@hdr",
                RefId = fd.RefId,
                IsHeaderFor = fd.Kind,
            });

        var result = new List<FieldDef>(headers.Count + built.Count);
        result.AddRange(headers);
        result.AddRange(built);
        return result;
    }

    private static bool IsProbablyCode(string[] lines)
    {
        int signals = 0;
        foreach (var raw in lines)
        {
            string l = StripComment(raw).Trim();
            if (l.Length == 0) continue;
            if (l.Contains("module.exports") || l.Contains("=>") || l.Contains("function")
                || l.Contains("buffer.") || l.Contains("reader(") || l.Contains("writer(")
                || l.Contains(";") || l.Contains("{") || l.Contains("}")
                || l.StartsWith("return") || l.StartsWith("//") || l.StartsWith("let ")
                || l.StartsWith("if") || l.StartsWith("while") || l.StartsWith("throw"))
            {
                if (++signals >= 2) return true;
            }
        }
        return false;
    }

    private static string StripComment(string line)
    {
        int h = line.IndexOf('#');
        return h >= 0 ? line[..h] : line;
    }

    private static (FieldKind, FieldKind?) MapKind(string token)
    {
        string t = token.ToLowerInvariant();
        if (t.StartsWith("array<"))
        {
            int close = t.IndexOf('>');
            string inner = close > 6 ? t[6..close] : "int32";
            var (ek, _) = MapKind(inner);
            return (FieldKind.Array, ek);
        }
        if (t.StartsWith("array")) return (FieldKind.Array, null);

        return t switch
        {
            "byte" or "int8" or "uint8" => (FieldKind.Byte, null),
            "bool" => (FieldKind.Bool, null),
            "int16" => (FieldKind.Int16, null),
            "uint16" => (FieldKind.UInt16, null),
            "int32" => (FieldKind.Int32, null),
            "uint32" => (FieldKind.UInt32, null),
            "int64" => (FieldKind.Int64, null),
            "uint64" => (FieldKind.UInt64, null),
            "float" => (FieldKind.Float, null),
            "double" => (FieldKind.Double, null),
            "angle" => (FieldKind.Angle, null),
            "vec3" => (FieldKind.Vec3, null),
            "vec3fa" => (FieldKind.Vec3Fa, null),
            "customize" => (FieldKind.Customize, null),
            "skillid" or "skillid32" => (FieldKind.SkillId, null),
            "string" => (FieldKind.String, null),
            "bytes" => (FieldKind.Bytes, null),
            "object" => (FieldKind.Object, null),
            _ => (FieldKind.Unknown, null),
        };
    }
}
