// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace TeraSharp.Arbiter.World;

public static class QaCommerceSheet
{
    public sealed record Data(int BrokerExpireDays, double RegisterFeeRate, int AverageDays, int AverageHour,
        int MaxReceived, int MaxSent, int EscrowSeconds);
    public static readonly SheetValue<Data> Entry = new("WorldData.xml (QA parcel/broker)",
        "mail limits and broker expiry/price calculations", new(0, 0, 0, 0, 0, 0, 0), directory =>
        {
            string path = Path.Combine(directory, "WorldData.xml");
            if (!File.Exists(path)) return null;
            var root = XDocument.Load(path).Root!;
            var broker = root.Element("TradeBroker"); var parcel = root.Element("Parcel");
            if (broker == null || parcel == null) return null;
            // Defaults5/3 are the native loader defaults at Arb058:13501-13504.
            return new((int?)broker.Attribute("expireDays") ?? 0, (double?)broker.Attribute("transactionFeeRate") ?? 0,
                (int?)broker.Attribute("mivAvgPriceCalcMaxDays") ?? 3, (int?)broker.Attribute("minAvgPriceRecalcTime") ?? 5,
                (int?)parcel.Attribute("maxRecvListCnt") ?? 0, (int?)parcel.Attribute("maxSendListCnt") ?? 0,
                (int?)parcel.Attribute("escrowReturnWait") ?? 0);
        }, x => x.BrokerExpireDays > 0 ? 1 : 0);
}

/// <summary>Reusable native QA item validation. No transcribed template or enchant-limit tables.</summary>
public static class QaItemSheet
{
    public sealed record Item(int Id, IReadOnlyDictionary<string, string> Attributes)
    {
        public int Int(string name) => Attributes.TryGetValue(name, out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;
        public double Number(string name) => Attributes.TryGetValue(name, out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0;
        public bool Flag(string name) => Attributes.TryGetValue(name, out var s) && bool.TryParse(s, out bool value) && value;
    }
    public sealed record Data(IReadOnlyDictionary<int, Item> Items, int NormalMax, int MasterworkMax)
    {
        public bool ValidPriceKey(int template, int enchant, bool masterwork)
            => enchant >= 0 && Items.TryGetValue(template, out var item)
                && (!masterwork || item.Number("masterpieceRate") > 0)
                && enchant <= (item.Flag("enchantEnable") ? masterwork ? MasterworkMax : NormalMax : 0);
    }
    public static readonly SheetValue<Data> Entry = new("ItemTemplate*.xml + EnchantData.xml (QA)",
        "native item validation and broker price keys", new(new Dictionary<int, Item>(), 0, 0), Read, x => x.Items.Count);

    public static Data? Read(string directory)
    {
        string enchantPath = Path.Combine(directory, "EnchantData.xml");
        var files = Directory.Exists(directory) ? Directory.GetFiles(directory, "ItemTemplate*.xml").OrderBy(x => x, StringComparer.Ordinal).ToArray() : Array.Empty<string>();
        if (!File.Exists(enchantPath) || files.Length == 0) return null;
        var enchant = XDocument.Load(enchantPath).Root!;
        int normal = (int?)enchant.Attribute("normalMaxCount") ?? -1, masterwork = (int?)enchant.Attribute("masterpieceMaxCount") ?? -1;
        if (normal < 0 || masterwork < 0) throw new FormatException("Invalid EnchantData limits");
        var items = new Dictionary<int, Item>();
        foreach (string file in files)
        {
            using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Item") continue;
                if (!int.TryParse(reader.GetAttribute("id"), out int id) || id < 0 || id >= 1000000) continue;
                var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
                if (reader.MoveToFirstAttribute()) do { attributes[reader.Name] = reader.Value; } while (reader.MoveToNextAttribute());
                reader.MoveToElement(); items.TryAdd(id, new(id, attributes));
            }
        }
        return new(items, normal, masterwork);
    }
}
