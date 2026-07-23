using ArkadeHeroes.Core;
using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;

namespace ArkadeHeroes.Tests;

/// <summary>
/// 3v3 team synergy — an elemental-diversity damage nudge gated behind <c>CombatConfig.SquadSynergy</c>
/// (DEFAULT OFF). Verifies the pure bonus math, that the flag OFF is a byte-identical no-op (so every
/// existing squad receipt + <c>FairnessAudit.VerifySquad</c> still verifies), and that ON actually rewards
/// a more diverse lineup while never hurting it.
/// </summary>
public class SquadSynergyTests
{
    // A uniform non-zero genome → all heroes share identical stats; only the element gene (Bytes[5] % 8)
    // differs, so a lineup's elemental diversity is the sole variable under test.
    static Hero H(string id, int element, int level = 5)
    {
        var g = new byte[32];
        Array.Fill(g, (byte)40);
        g[5] = (byte)element;
        return new Hero { Id = id, OwnerId = "t", Name = id, Genome = new Genome(g), Level = level };
    }

    static byte[] Seed(byte v) { var s = new byte[32]; Array.Fill(s, v); return s; }

    static GameConfig Synergy(bool on) =>
        GameConfig.Default with { Combat = GameConfig.Default.Combat with { SquadSynergy = on } };

    [Fact]
    public void Multiplier_ScalesFromZeroMonoToCappedDiverse()
    {
        var mono = new[] { H("a", 0), H("b", 0), H("c", 0) };       // 1 distinct element → no bonus
        var two = new[] { H("a", 0), H("b", 0), H("c", 1) };        // 2 distinct → half
        var diverse = new[] { H("a", 0), H("b", 1), H("c", 2) };    // 3 distinct → full

        Assert.Equal(1.0, SquadSynergy.Multiplier(mono), 6);
        Assert.Equal(1.0 + SquadSynergy.MaxBonus / 2, SquadSynergy.Multiplier(two), 6);
        Assert.Equal(1.0 + SquadSynergy.MaxBonus, SquadSynergy.Multiplier(diverse), 6);
    }

    [Fact]
    public void FightAdvantage_DefaultOne_IsAByteIdenticalNoOp()
    {
        // The advantage params default to 1.0 — proving every non-squad caller (1v1 / gauntlet / tournament /
        // death-match) is untouched: a no-op multiplier must reproduce the plain fight exactly.
        var a = H("a", 0);
        var b = H("b", 3);
        var plain = BattleEngine.Fight(a, b, Seed(9));
        var explicitOne = BattleEngine.Fight(a, b, Seed(9), null, 1.0, 1.0);

        Assert.Equal(plain.WinnerId, explicitOne.WinnerId);
        Assert.Equal(plain.Turns, explicitOne.Turns);
        Assert.Equal(plain.WinnerRemainingHp, explicitOne.WinnerRemainingHp);
    }

    [Fact]
    public void FightAdvantage_AboveOne_BoostsDamage_AndDecidesAnEvenFight()
    {
        // Same element (no matchup skew), identical stats → an otherwise-even fight. A decisive advantage
        // makes the boosted hero win — the seam the small, capped squad bonus rides on.
        var a = H("a", 0);
        var b = H("b", 0);
        var boosted = BattleEngine.Fight(a, b, Seed(4), null, 3.0, 1.0);
        Assert.Equal(a.Id, boosted.WinnerId);
    }

    [Fact]
    public void SquadSynergyOff_IsIdenticalToAPlainBestOfThree()
    {
        // The default (flag off) resolves exactly as before — what keeps every existing squad receipt and
        // VerifySquad valid. An explicit synergy-off config equals GameConfig.Default over many seeds.
        var diverse = new[] { H("d0", 0), H("d1", 1), H("d2", 2) };
        var mono = new[] { H("m0", 0), H("m1", 0), H("m2", 0) };
        for (byte s = 1; s <= 8; s++)
        {
            var def = SquadBattle.Resolve(diverse, mono, Seed(s));                 // GameConfig.Default (off)
            var off = SquadBattle.Resolve(diverse, mono, Seed(s), Synergy(false));
            Assert.Equal(def.Duels.Select(d => (d.Result.WinnerId, d.Result.Turns)),
                         off.Duels.Select(d => (d.Result.WinnerId, d.Result.Turns)));
        }
    }

    [Fact]
    public void SquadSynergyOn_NeverHurtsTheDiverseSquad_AndTipsAtLeastOneMatch()
    {
        // On: only the more-diverse side is boosted, so it can never win FEWER duels than with synergy off,
        // and across a spread of seeds the capped edge tips at least one otherwise-even duel — proving it is
        // wired all the way through SquadBattle -> Fight -> damage.
        var diverse = new[] { H("d0", 0), H("d1", 1), H("d2", 2) };   // full-diversity bonus
        var mono = new[] { H("m0", 0), H("m1", 0), H("m2", 0) };      // no bonus
        var tippedSomewhere = false;
        for (byte s = 1; s <= 30; s++)
        {
            var off = SquadBattle.Resolve(diverse, mono, Seed(s), Synergy(false)).ChallengerWins;
            var on = SquadBattle.Resolve(diverse, mono, Seed(s), Synergy(true)).ChallengerWins;
            Assert.True(on >= off, $"seed {s}: synergy made the diverse squad do WORSE ({on} < {off})");
            if (on > off) tippedSomewhere = true;
        }
        Assert.True(tippedSomewhere, "synergy changed no duel across 30 seeds — is the bonus wired through?");
    }
}
