using TeraSharp.Arbiter.Persistence;

namespace TeraSharp.Arbiter.Game;

/// <summary>Character view used by the login/select handlers. Built from a CharacterRecord.</summary>
public sealed class FakeCharacter
{
    public uint Id { get; init; } = 1;
    public string Name { get; init; } = "dob";
    public int TemplateId { get; init; } = 11013;
    public int Gender { get; init; } = 1;
    public int Race { get; init; } = 4;
    public int Class { get; init; } = 12;
    public int Level { get; init; } = 1;
    public long Hp { get; init; } = 100000;
    public int Mp { get; init; } = 100000;
    public int Zone { get; init; } = 7005;
    public float X { get; init; } = -449f;
    public float Y { get; init; } = 6239f;
    public float Z { get; init; } = 1956f;
    public int WorldId { get; init; } = 0;
    public int GuardId { get; init; } = 0;
    public int SectionId { get; init; } = 0;
    public int Position { get; init; } = 1;
    public int Weapon { get; init; } = 59053;
    public int Body { get; init; } = 15004;
    public int Hand { get; init; } = 15005;
    public int Feet { get; init; } = 15006;

    public byte[] Appearance { get; init; } = { 0x65, 0x01, 0x07, 0x04, 0x0E, 0x0E, 0x04, 0x00 };

    public byte[] Details { get; init; } =
    {
        0x00, 0x0A, 0x08, 0x0C, 0x00, 0x00, 0x00, 0x00, 0x1A, 0x15, 0x1D, 0x00, 0x0B, 0x15, 0x05, 0x00,
        0x10, 0x00, 0x0C, 0x0D, 0x00, 0x00, 0x00, 0x0F, 0x10, 0x17, 0x10, 0x12, 0x19, 0x10, 0x0E, 0x09
    };

    public byte[] Shape { get; init; } =
    {
        0x01, 0x13, 0x10, 0x13, 0x13, 0x10, 0x13, 0x13, 0x13, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F,
        0x10, 0x13, 0x0A, 0x00, 0x05, 0x0B, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    };

    public static FakeCharacter FromRecord(CharacterRecord r) => new()
    {
        Id = (uint)r.Id, Name = r.Name, TemplateId = r.TemplateId,
        Gender = r.Gender, Race = r.Race, Class = r.Class, Level = r.Level,
        Zone = r.Zone, X = r.X, Y = r.Y, Z = r.Z, Position = r.Position,
        Weapon = r.Weapon, Body = r.Body, Hand = r.Hand, Feet = r.Feet,
        Appearance = r.Appearance, Details = r.Details, Shape = r.Shape,
    };
}

public sealed class FakeAccount
{
    public ulong AccountId { get; set; } = 1;
    public string Name { get; set; } = "1";
    public List<FakeCharacter> Characters { get; } = new();

    /// <summary>Load account + characters from the store (creates the account if new).</summary>
    public void LoadFromStore(CharacterStore store, string accountName)
    {
        var acct = store.GetOrCreateAccount(accountName);
        AccountId = (ulong)acct.Id;
        Name = acct.Name;
        Characters.Clear();
        foreach (var r in store.GetCharacters(acct.Id))
            Characters.Add(FakeCharacter.FromRecord(r));
    }
}
