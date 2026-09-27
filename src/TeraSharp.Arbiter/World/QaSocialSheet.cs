// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Xml.Linq;

namespace TeraSharp.Arbiter.World;

/// <summary>Native ShowFriendshipInfo resolves StrSheet_Friend1003 for QA condition6 (Arb030:5809).</summary>
public static class QaSocialSheet
{
    public static readonly SheetValue<IReadOnlyDictionary<int, string>> Entry = new("StrSheet_Friend.xml",
        "QA friendship acquisition messages", new Dictionary<int, string>(), Read, d => d.Count);
    public static IReadOnlyDictionary<int, string>? Read(string directory)
    {
        string path = Path.Combine(directory, "StrSheet_Friend.xml");
        if (!File.Exists(path)) return null;
        return XDocument.Load(path).Descendants("String").Where(x => (int?)x.Attribute("id") != null)
            .ToDictionary(x => (int)x.Attribute("id")!, x => (string?)x.Attribute("string") ?? "");
    }
}
