// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Xml.Linq;

namespace TeraSharp.Arbiter.World;

/// <summary>T190. Arb085:5368-5422 loads card quantities/points; :4632-4692 loads the
/// collection level curve. These values also drive the native perfect_card_collection command.</summary>
public static class CardCollectionSheet
{
    public sealed record Template(int Id, int ActivationAmount, int BookPoints);
    public sealed record Level(int Id, int NeedPoints);
    public sealed record Data(IReadOnlyDictionary<int, Template> Templates, IReadOnlyList<Level> Levels)
    {
        public bool Available => Templates.Count != 0 && Levels.Count != 0;

        // Arb084:14372-14450 FindCollectionBookLevel: first higher threshold's level - 1,
        // otherwise the highest configured level. The live sheet is levels1..6.
        public int LevelFor(int points)
        {
            foreach (var level in Levels)
                if (points < level.NeedPoints) return level.Id - 1;
            return Levels.Count == 0 ? 1 : Levels[^1].Id;
        }
    }

    public static readonly SheetValue<Data> Entry = new(
        "CardTemplate.xml + CollectionBook.xml", "card collection quantities, points and levels",
        new Data(new Dictionary<int, Template>(), Array.Empty<Level>()), Read, d => d.Templates.Count);

    public static Data? Read(string directory)
    {
        string cards = Path.Combine(directory, "CardTemplate.xml"), book = Path.Combine(directory, "CollectionBook.xml");
        return File.Exists(cards) && File.Exists(book) ? Parse(XDocument.Load(cards), XDocument.Load(book)) : null;
    }

    public static Data Parse(XDocument cards, XDocument book)
    {
        static int N(XElement e, string key) => (int?)e.Attribute(key)
            ?? throw new FormatException($"Missing card attribute {key}");
        var templates = cards.Descendants("CardTemplate").Select(e => new Template(
            N(e, "id"), N(e, "needAmountForActivation"), N(e, "collectionBookPoint")))
            .ToDictionary(t => t.Id);
        var levels = book.Descendants("BookTypeList").Where(e => (string?)e.Attribute("type") == "card")
            .Elements("BookLevelData").Elements("LevelData")
            .Select(e => new Level(N(e, "level"), N(e, "needPoint"))).OrderBy(e => e.Id).ToArray();
        return new Data(templates, levels);
    }
}
