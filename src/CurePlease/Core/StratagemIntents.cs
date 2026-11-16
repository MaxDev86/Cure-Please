using System;
using System.Collections.Generic;
using System.Linq;
using CurePlease.Core.Modules;
using FFXI.Memory;

namespace CurePlease.Core
{
    /// <summary>
    /// Cross-module claims on "next spell" buffs (Accession, Perpetuance, Divine
    /// Seal, Rapture, Majesty). The legacy flaw: a stratagem prepped for a buff
    /// gets eaten by an unrelated cure before the buff cast fires — Accession
    /// does not know what it was cast for.
    ///
    /// The engine records a claim (buff, owner module, intended spell) when a
    /// prep JA with ActionCandidate.PrepBuff executes, and suppresses centrally:
    ///  - other modules' spells that would consume the claimed buff, and
    ///  - other modules' prep JAs for the same buff — a second Accession on top
    ///    of a pending one just burns a charge;
    ///  - the owner's OWN spells that consume the buff but are not the intended
    ///    spell (e.g. BuffUpkeep claimed Accession for Protect V: its missing
    ///    Regen entry must wait, or it would eat the Accession itself).
    ///
    /// Emergency override: a cure at/below CureSettings.IntentEmergencyHpPercent
    /// (ActionCandidate.IsEmergency) bypasses the suppression — life beats
    /// buffs. The owner re-preps next cycle, costing a charge; same tradeoff as
    /// legacy, but only in genuine emergencies.
    ///
    /// A claim clears when the buff is consumed (observed up, then gone), the
    /// 10s TTL expires (same window as the per-module JA verify), or on zone.
    ///
    /// Divine Seal note: its claims only stop competing DS prep JAs — cures are
    /// never blocked by a DS claim (the life-saver use wins the shared recast),
    /// and -na consumption of DS is covered by the Debuff module's own
    /// critical-cure suppression before it preps.
    /// </summary>
    public sealed class StratagemIntents
    {
        public sealed class Claim
        {
            public StatusEffect Buff { get; set; }

            /// <summary>IDecisionModule.Name of the claiming module.</summary>
            public string OwnerModule { get; set; }

            /// <summary>The spell the buff was prepped for (empty when not fixed at prep time, e.g. Rapture).</summary>
            public string IntendedSpell { get; set; } = string.Empty;

            /// <summary>Candidate description at prep time (for the log line).</summary>
            public string Detail { get; set; } = string.Empty;

            public DateTime ExpiresUtc { get; set; }

            /// <summary>True once the buff has been observed on the player (packet-lag grace).</summary>
            public bool Seen { get; set; }
        }

        /// <summary>Same window as the per-module JA verify: a stuck claim cannot stall healing for long.</summary>
        private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(10);

        private readonly List<Claim> _claims = new List<Claim>();

        /// <summary>Raised when a claim is recorded (engine forwards this to the decision log).</summary>
        public event Action<string> ClaimAdded;

        /// <summary>Live claim count (tests/diagnostics).</summary>
        public int Count => _claims.Count;

        /// <summary>Records a claim after the prep JA executed. Replaces any stale claim on the same buff.</summary>
        public void Add(StatusEffect buff, string ownerModule, string intendedSpell, string detail)
        {
            for (int i = _claims.Count - 1; i >= 0; i--)
            {
                if (_claims[i].Buff == buff)
                {
                    _claims.RemoveAt(i);
                }
            }

            _claims.Add(new Claim
            {
                Buff = buff,
                OwnerModule = ownerModule,
                IntendedSpell = intendedSpell ?? string.Empty,
                Detail = detail ?? string.Empty,
                ExpiresUtc = DateTime.UtcNow + Ttl,
            });

            ClaimAdded?.Invoke(detail + " — claimed by " + ownerModule);
        }

        public bool TryGetClaim(StatusEffect buff, out Claim claim)
        {
            foreach (Claim c in _claims)
            {
                if (c.Buff == buff)
                {
                    claim = c;
                    return true;
                }
            }

            claim = null;
            return false;
        }

        /// <summary>True when another module holds a claim on this buff (suppress competing preps and consumers).</summary>
        public bool IsHeldByOther(StatusEffect buff, string moduleName)
        {
            return TryGetClaim(buff, out Claim claim)
                && !claim.OwnerModule.Equals(moduleName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when a live claim reserves a buff this spell would consume:
        /// another module's claim blocks the spell outright; the owner's own
        /// claim blocks it only when it is not the spell the buff was prepped
        /// for (a known IntendedSpell that does not match).
        /// </summary>
        public bool BlocksSpell(string spellName, string moduleName)
        {
            foreach (Claim claim in _claims)
            {
                if (!SpellConsumes(spellName, claim.Buff))
                {
                    continue;
                }

                // Divine Seal is shared between doubled cures and group -na
                // removal, and the life-saver wins: cures are never blocked by a
                // DS claim. (The claim still stops competing DS prep JAs, and the
                // removal side already yields to critical cures before prepping.)
                if (claim.Buff == StatusEffect.Divine_Seal
                    && (spellName.StartsWith("Cure", StringComparison.OrdinalIgnoreCase)
                        || spellName.StartsWith("Cura", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                bool own = claim.OwnerModule.Equals(moduleName, StringComparison.OrdinalIgnoreCase);
                if (!own)
                {
                    return true;
                }

                if (claim.IntendedSpell.Length != 0
                    && !claim.IntendedSpell.Equals(spellName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Per-tick housekeeping (engine): drop expired claims and claims whose buff
        /// was observed up and is now gone (consumed by the intended cast — or by an
        /// emergency override, which is exactly the re-prep signal the owner needs).
        /// A claim that never saw its buff survives on TTL alone (JA packet lag).
        /// </summary>
        public void Prune(short[] playerBuffs)
        {
            for (int i = _claims.Count - 1; i >= 0; i--)
            {
                Claim claim = _claims[i];
                if (DateTime.UtcNow >= claim.ExpiresUtc)
                {
                    _claims.RemoveAt(i);
                    continue;
                }

                if (HasBuff(playerBuffs, (short)claim.Buff))
                {
                    claim.Seen = true;
                }
                else if (claim.Seen)
                {
                    _claims.RemoveAt(i);
                }
            }
        }

        /// <summary>Zone transition strips every buff — drop all claims.</summary>
        public void Clear()
        {
            _claims.Clear();
        }

        /// <summary>
        /// True when casting this spell would consume the given "next spell" buff.
        ///  - Accession: Cure I–IV, Accession-compatible catalog buffs, -na/Erase
        ///    removals (the Addendum: White spell list; Raise lines never appear
        ///    as candidates, so their presence in the set is harmless).
        ///  - Perpetuance: Perpetuance-compatible catalog buffs only (it extends
        ///    enhancing white magic; cures and -na do not touch it).
        ///  - Divine Seal / Rapture / Majesty: any cure spell (Cure/Cura/Curaga
        ///    lines). Divine Seal's -na use is the Debuff module's OWN claim —
        ///    cures deliberately ignore Divine Seal claims (life-saver wins).
        /// </summary>
        public static bool SpellConsumes(string spellName, StatusEffect buff)
        {
            switch (buff)
            {
                case StatusEffect.Accession:
                    foreach (string cure in SpellBook.AccessionCureTierNames)
                    {
                        if (cure.Equals(spellName, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }

                    BuffCatalogEntry entry = BuffCatalog.Find(spellName);
                    if (entry != null)
                    {
                        return entry.AccessionCompatible;
                    }

                    return DebuffModule.AddendumWhiteSpells.Contains(spellName);

                case StatusEffect.Perpetuance:
                    BuffCatalogEntry buffEntry = BuffCatalog.Find(spellName);
                    return buffEntry != null && buffEntry.PerpetuanceCompatible;

                case StatusEffect.Divine_Seal:
                case StatusEffect.Rapture:
                    return spellName.StartsWith("Cure", StringComparison.OrdinalIgnoreCase)
                        || spellName.StartsWith("Cura", StringComparison.OrdinalIgnoreCase);

                case StatusEffect.Majesty:
                    // Majesty AoEs the next Cure — and also Protect/Shell.
                    if (spellName.StartsWith("Cure", StringComparison.OrdinalIgnoreCase)
                        || spellName.StartsWith("Cura", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    BuffCatalogEntry majestyEntry = BuffCatalog.Find(spellName);
                    return majestyEntry != null
                        && (majestyEntry.Group == "Protect" || majestyEntry.Group == "Shell");

                default:
                    return false;
            }
        }

        private static bool HasBuff(short[] buffs, short effect)
        {
            foreach (short buff in buffs)
            {
                if (buff == effect)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
