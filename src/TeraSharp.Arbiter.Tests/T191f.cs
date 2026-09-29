// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Persistence;

namespace TeraSharp.Arbiter.Tests;

// =============================================================================================
// T191f - two fields the lobby got wrong on every login.
//
// 1. S_GET_USER_LIST (0x6759) entry+425, isNewCharacter. The list is a walked record list: the
//    body header is [u16 count][u16 firstOffset] and each record starts with [u16 here][u16 next],
//    so the brief's "+460" is entry 0 (which starts at 35) plus 425.
//
//      classic_live3 record 11   count 7, entries at 35/603/1195/1783/2371/2961/3563
//                                +425 = 01 00 00 00 00 00 00   <- true for ONE character
//      cap_queue4_client1  11    count 2                +425 = 01 01
//      cap_wasd2_client    11    count 5                +425 = 01 01 01 01 01
//
//    We hard-coded true, for every character, on every login. The record that decides it is
//    characters.entered_world, written by SDB_USER_ENTERWORLD.
//
// 2. S_LOGIN_ARBITER (record 7) status, body +2 / packet offset 6, a u32:
//
//      classic_live3        17 00 A6 92 01 00 00 00 00 00 00 00 00 00 06 00 ...   status 0
//      cap_queue4_client1   17 00 A6 92 01 00 1F 00 00 00 00 00 00 00 06 00 ...   status 31
//
//    T89b read 31 off proxy captures and called 0 "a brand-new account". The live Classic+ server
//    is the better witness: ordinary accounts get 0, and 33 stays for the operator (T104, Alt+A).
// =============================================================================================
public static partial class Tests
{
    /// <summary>classic_live3 record 7 - the retail S_LOGIN_ARBITER for an ordinary account.</summary>
    static readonly byte[] T191fRetailLoginArbiter =
        Hex.B("17 00 A6 92 01 00 00 00 00 00 00 00 00 00 06 00 00 00 00 00 00 00 00");

    /// <summary>cap_queue4_client1 record 7 - ours, the one field that differed.</summary>
    static readonly byte[] T191fOursLoginArbiter =
        Hex.B("17 00 A6 92 01 00 1F 00 00 00 00 00 00 00 06 00 00 00 00 00 00 00 00");

    /// <summary>T191f. The two captured S_LOGIN_ARBITERs differ in one field, and it is the status.</summary>
    [Test] public static void T191f_login_arbiter_status_is_zero_for_an_ordinary_account()
    {
        var differ = Enumerable.Range(0, T191fRetailLoginArbiter.Length)
            .Where(i => T191fRetailLoginArbiter[i] != T191fOursLoginArbiter[i]).ToArray();
        Hex.True(differ.Length == 1 && differ[0] == 6,
            $"one byte differs, at packet offset 6 (status, body +2), got [{string.Join(",", differ)}]");
        Hex.True(BitConverter.ToUInt32(T191fRetailLoginArbiter, 6) == GmAccounts.LoginStatusNormal,
            $"and we now send classic_live3's value, got {GmAccounts.LoginStatusNormal}");
        Hex.True(GmAccounts.LoginStatusOperator == 33,
            "the operator keeps 33 - that is the Alt+A gate, not a privilege list");
    }

    /// <summary>T191f. The gate is the operator list, not the value.</summary>
    [Test] public static void T191f_an_operator_still_gets_thirty_three()
    {
        string? saved = Environment.GetEnvironmentVariable(GmAccounts.EnvVariable);
        try
        {
            Environment.SetEnvironmentVariable(GmAccounts.EnvVariable, "t191f-op");
            TerasConfig.ResetForTests();
            Hex.True(GmAccounts.LoginStatusFor("t191f-op", 0) == GmAccounts.LoginStatusOperator,
                "a listed account opens the tool");
            Hex.True(GmAccounts.LoginStatusFor("someone-else", 0) == GmAccounts.LoginStatusNormal,
                "and everyone else gets classic_live3's 0");
        }
        finally { Environment.SetEnvironmentVariable(GmAccounts.EnvVariable, saved); TerasConfig.ResetForTests(); }
    }

    /// <summary>
    /// T191f. isNewCharacter follows the enter-world record: true until SDB_USER_ENTERWORLD has
    /// been seen once, false afterwards - which is caludesucks on cap_wasd2_client's second login.
    /// </summary>
    [Test] public static void T191f_is_new_character_is_true_only_before_the_first_world_entry()
    {
        using var store = StoreWithTwoAccounts();
        int id = (int)store.GetAccount("acct1")!.Id;
        var chr = store.GetCharacters(store.GetAccount("acct1")!.Id).FirstOrDefault();
        Hex.True(chr != null, "the fixture has a character");
        int character = (int)chr!.Id;

        Hex.True(!store.HasEnteredWorld(character), "a character that has never entered world");
        var element = new Dictionary<string, object> { ["isNewCharacter"] = true };
        CharacterHandlers.FillLobbyFields(element, store, character);
        Hex.True((bool)element["isNewCharacter"], "is still announced as new - classic_live3 entry 0");

        store.MarkCharacterEnteredWorld(character);   // SDB_USER_ENTERWORLD
        Hex.True(store.HasEnteredWorld(character), "the record is written on the first entry");
        element["isNewCharacter"] = true;             // the caller's template value, as before
        CharacterHandlers.FillLobbyFields(element, store, character);
        Hex.True(!(bool)element["isNewCharacter"],
            "and every later login carries 0 - cap_wasd2_client's second login must not say 01");

        store.MarkCharacterEnteredWorld(character);   // a relog does not undo it
        element["isNewCharacter"] = true;
        CharacterHandlers.FillLobbyFields(element, store, character);
        Hex.True(!(bool)element["isNewCharacter"], "and it stays 0 for every entry after that");
    }
}
