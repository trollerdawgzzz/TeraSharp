// SPDX-License-Identifier: MIT
// Copyright (c) 2026 the TeraSharp contributors

using TeraSharp.Arbiter.Handlers;
using TeraSharp.Arbiter.Persistence;
using TeraSharp.Arbiter.World;

namespace TeraSharp.Arbiter.Tests;

// =============================================================================================
// T219c - an expired account benefit wedges World's account tick.
//
// The A/B, from the two taps:
//
//   cap_makeitem  15690  A->W 0x2829 `makeitem 88384 1` player 10 (account 2)
//                 15691  W->A 0x2768 len=886   <- the create atom, 1 ms later
//   cap_item_new  76558  A->W 0x2829 `makeitem 88384 1` player  9 (account 1)
//                        (nothing from World for the next 40 s)
//
// Same command, same template. World's create path reads no benefit at all -
// WorldQACommandHandler::MakeItem(User, MakeItemInfo) guards only on templateId < 1000000,
// ItemTemplate[templateId] != null, amount > 0 and Item::IsStackableItem - so the difference is the
// ACCOUNT, and the account tick is where the benefit is read:
//
//   User::OnTickAccountTrait
//     -> AccountTrait::CheckAccountBenefitInterval   every tenth tick
//     -> AccountTrait::GetExpiredPackges             collects ids whose expiry has passed
//     -> AccountTrait::DeletePropertyList            resolves each id in the UserTrait datasheet
//        -> "Unknown user trait 1000", assert AccountTrait.cpp(523), leave the loop
//
// The package is not removed, so the next interval asserts again. T181 seeded 533/534/1000 on every
// listed operator account and 1000's captured expiry is 2026-08-15 - already past. That is DB
// state, which is why the ae1b8b5 rollback changed nothing, and it is per account, which is why
// every character on account 1 failed and account 2 was fine. T182 found the teleport rule
// elsewhere, so the seeding is gone.
// =============================================================================================
public static partial class Tests
{
    private static readonly byte[] T219cBenefitRequest = Convert.FromHexString("1B000000EB030000");

    /// <summary>T219c. A listed operator's benefit load seeds nothing and answers empty.</summary>
    [Test] public static void T219c_operator_benefit_load_seeds_nothing()
    {
        using var store = T181Store();
        T181WithOperators("acct2", () =>
        {
            var (op, body) = RunHandler1(0x28BB, T219cBenefitRequest, store);
            Hex.Eq(T180Frame(op, body), Convert.FromHexString("13000000BC2813000000000000001B00000001"),
                "the same 19-byte empty answer a player account gets");
            Hex.True(store.GetAccountBenefits(store.GetAccount("acct2")!.Id).Count == 0, "nothing persisted");
        });
    }

    /// <summary>
    /// T219c. The rule the brief asked for: an expired benefit never reaches World. The row stays
    /// in the table - only the admin API removes a real grant - but it is withheld from 0x28BC.
    /// </summary>
    [Test] public static void T219c_an_expired_benefit_never_reaches_world()
    {
        using var store = T181Store();
        long account = store.GetAccount("acct2")!.Id;
        long past = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
        long future = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
        store.GrantAccountBenefit(account, 1000, past);
        store.GrantAccountBenefit(account, 533, future);

        var (op, body) = RunHandler1(0x28BB, T219cBenefitRequest, store);
        var frame = T180Frame(op, body);
        Hex.True(BitConverter.ToUInt32(frame, 10) == 16, $"exactly one 16-byte package travels, got {BitConverter.ToUInt32(frame, 10)} B");
        Hex.True(BitConverter.ToInt32(frame, 19) == 533, $"and it is the live one, got {BitConverter.ToInt32(frame, 19)}");
        Hex.True(store.GetAccountBenefits(account).Count == 2, "the expired row is withheld, not deleted");
    }

    /// <summary>
    /// T219c. An account still carrying the retired experiment's rows is repaired by its next
    /// benefit load - this is account 1's shape in the live DB (533, 534 and the expired 1000).
    /// </summary>
    [Test] public static void T219c_experiment_rows_are_swept_on_the_next_load()
    {
        using var store = T181Store();
        long account = store.GetAccount("acct2")!.Id;
        SeedRetiredExperiment(store, account);
        Hex.True(store.GetAccountBenefits(account).Count == 3, "the live DB's three rows");

        T181WithOperators("acct2", () => RunHandler1(0x28BB, T219cBenefitRequest, store));
        Hex.True(store.GetAccountBenefits(account).Count == 0, "all three are gone");
    }

    /// <summary>T219c. The sweep only takes experiment rows; a real grant is left alone.</summary>
    [Test] public static void T219c_sweep_leaves_a_real_grant_alone()
    {
        using var store = T181Store();
        long account = store.GetAccount("acct2")!.Id;
        SeedRetiredExperiment(store, account);
        store.GrantAccountBenefit(account, 777, DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds(), 99);

        Hex.True(store.RemoveBenefitExperimentRows(null) == 3, "the whole-table sweep takes the three");
        var rows = store.GetAccountBenefits(account);
        Hex.True(rows.Count == 1 && rows[0].PackageId == 777 && rows[0].Value == 99, "the real grant survives");
        Hex.True(store.RemoveBenefitExperimentRows(null) == 0, "and the sweep is idempotent");
    }

    /// <summary>T219c. POST /api/remove-benefit: one package, or the experiment sweep.</summary>
    [Test] public static void T219c_remove_benefit_endpoint_takes_one_package_or_sweeps()
    {
        using var store = T181Store();
        long account = store.GetAccount("acct2")!.Id;
        SeedRetiredExperiment(store, account);
        store.GrantAccountBenefit(account, 777, DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds(), 99);
        var api = NewAdminApi(store);

        var one = api.Handle("POST", "/api/remove-benefit", null,
            "{\"account\":" + account + ",\"package\":777,\"reason\":\"T219c\"}", T101Token, "127.0.0.1");
        Hex.True(one.Status == 200, $"the named package is removed, got {one.Status}");

        var sweep = api.Handle("POST", "/api/remove-benefit", null, "{\"reason\":\"T219c\"}", T101Token, "127.0.0.1");
        Hex.True(sweep.Status == 200 && sweep.Body.Contains("\"removed\":3", StringComparison.Ordinal),
            $"the sweep reports the three experiment rows, got {sweep.Body}");
        Hex.True(store.GetAccountBenefits(account).Count == 0, "nothing left on the account");

        var missing = api.Handle("POST", "/api/remove-benefit", null,
            "{\"name\":\"nobody\",\"package\":1,\"reason\":\"T219c\"}", T101Token, "127.0.0.1");
        Hex.True(missing.Status == 404, $"an unknown account is 404, got {missing.Status}");
    }

    /// <summary>
    /// The three rows T181 used to write, as the live DB still holds them for account 1. Written
    /// through the connection because nothing in the Arbiter seeds a teleport_experiment row now.
    /// </summary>
    private static void SeedRetiredExperiment(CharacterStore store, long account)
    {
        var sql = (Microsoft.Data.Sqlite.SqliteConnection)typeof(CharacterStore)
            .GetField("_db", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(store)!;
        foreach (var p in T181CapturedPackages)
        {
            using var cmd = sql.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO account_benefits"
                + "(account_id,package_id,expires_at,value,teleport_experiment) VALUES($a,$p,$e,0,1)";
            cmd.Parameters.AddWithValue("$a", account);
            cmd.Parameters.AddWithValue("$p", p.PackageId);
            cmd.Parameters.AddWithValue("$e", p.ExpiresAt);
            cmd.ExecuteNonQuery();
        }
    }
}
