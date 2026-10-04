using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Sync.Recovery;

/// <summary>
/// The password opened none of the boxes it was tried on, and <see cref="Remaining"/> boxes were not tried
/// (per-key cap, budget). Nothing is written; the user may try them (<see cref="RecoveryAttemptBudget.Unlimited"/>).
/// </summary>
public sealed class RecoveryBoxesRemainingException(int remaining)
    : Exception($"{remaining} recovery box(es) were not tried; nothing proves which key is the newest until they are.")
{
    public int Remaining { get; } = remaining;
}

/// <summary>What a master password recovered from a recovery set.</summary>
public sealed class RecoveredKeys : IDisposable
{
    private readonly Dictionary<string, byte[]> _all;
    private readonly Dictionary<string, int> _epochByFp;

    internal RecoveredKeys(Dictionary<string, byte[]> all, IReadOnlyList<string> heads, string current, Dictionary<string, int> epochByFp,
        int remainingBoxes = 0, bool budgetExhausted = false)
    {
        _all = all;
        _epochByFp = epochByFp;
        Heads = heads;
        CurrentFingerprint = current;
        RemainingBoxes = remainingBoxes;
        BudgetExhausted = budgetExhausted;
    }

    /// <summary>
    /// A budget class reached its limit (<see cref="RecoveryAttemptBudget.AnyLimitReached"/>), even on the
    /// very last box: the search stopped at its bound rather than because it ran out of boxes.
    /// </summary>
    public bool BudgetExhausted { get; }

    /// <summary>
    /// Boxes the password was not tried on — past the per-key cap or the budget — whose key is still not
    /// open. Any of them might hold a newer key, so while one remains nothing proves the head.
    /// </summary>
    public int RemainingBoxes { get; }

    /// <summary>
    /// Keys no verified chain link retires. More than one means the material does not prove which of
    /// them is the newest (a link between them is missing or was removed).
    /// </summary>
    public IReadOnlyList<string> Heads { get; }

    /// <summary>
    /// Exactly one head, every box tried and no budget class at its limit: every other opened key is proven
    /// older through links that only a holder of the newer key can have made, and no untried box can hide a
    /// newer one. Only then may an anchor under <see cref="Current"/> confirm anything.
    /// </summary>
    public bool HeadProven => Heads.Count == 1 && RemainingBoxes == 0 && !BudgetExhausted;

    public string CurrentFingerprint { get; private set; }

    /// <summary>The key the vault uses now: new writes, the new slot and the new strong box go under it.</summary>
    public byte[] Current => _all[CurrentFingerprint];

    /// <summary>Every other key — old bodies that arrive late still open.</summary>
    public IReadOnlyDictionary<string, byte[]> Retired =>
        _all.Where(kv => kv.Key != CurrentFingerprint).ToDictionary(kv => kv.Key, kv => kv.Value);

    /// <summary>Highest epoch any box of the current key claims (a hint for the local dek_epoch).</summary>
    public int EpochHint => _epochByFp.GetValueOrDefault(CurrentFingerprint);

    /// <summary>
    /// When the head is not proven, the caller may pick among <see cref="Heads"/> by other evidence (which
    /// key opens the newest body). It stays unproven: confirmation still needs <see cref="HeadProven"/>.
    /// </summary>
    public void UseHead(string fingerprint)
    {
        if (!Heads.Contains(fingerprint)) throw new ArgumentException("Not one of the heads.", nameof(fingerprint));
        CurrentFingerprint = fingerprint;
    }

    public void Dispose()
    {
        foreach (var k in _all.Values) Array.Clear(k);
    }
}

/// <summary>
/// The key side of a restore (plan 6.5, 6.7, 6.8). The password is tried on the boxes in an order no
/// crafted metadata decides (<see cref="AttemptOrder"/>) and every box that opens to the key it claims
/// (fingerprint check) is kept.
/// From each such key the chain is unwound downwards through every link that opens under a key already
/// held and yields the fingerprint it claims — forged links simply fail.
///
/// <para><b>The head.</b> Anchors and link rows in the set are unauthenticated, so neither may pick the
/// trust root. What can be verified after unwrapping is the order a link proves: "old retired by new",
/// wrapped under the new key — only its holder could have written it. The head is therefore the key no
/// verified link retires, among ALL keys the password opens. One head: proven. Several (a link between
/// them missing or removed): not proven — the current key is then a guess (highest epoch hint, which the
/// restore may replace by the key that opens the newest body), and nothing is confirmed.</para>
///
/// <para><b>Skipped boxes.</b> The set itself is open to anyone who can edit a file or a peer: junk boxes
/// under the genuine fingerprint (past the per-key cap) or under many fake ones (past the budget) can keep
/// the real newest box from being tried. A skipped box never disproves anything, but it voids the proof:
/// <see cref="RecoveredKeys.RemainingBoxes"/> counts them, and the head is proven only when it is zero.
/// The caller then offers "try the remaining boxes" (<see cref="RecoveryAttemptBudget.Unlimited"/>).
/// Exact duplicates are tried once; a box differing in any byte is its own box — dropping it as a "copy"
/// of another would let a forged copy (same ciphertext, other salt) hide the real one.</para>
/// </summary>
public static class RecoveryKeyResolver
{
    /// <summary>Sees every key the moment it is opened (tests: that a failed resolve wipes it).</summary>
    internal static readonly AsyncLocal<Action<byte[]>?> KeyOpened = new();

    /// <returns>Null when the password opens none of the boxes, all of them tried.</returns>
    /// <exception cref="RecoveryBoxesRemainingException">
    /// None of the boxes tried opened, and some were not tried (<see cref="RecoveryBoxesRemainingException.Remaining"/>).
    /// </exception>
    /// <param name="budget">Derivations this restore may spend (defaults: <see cref="RecoveryAttemptBudget"/>).</param>
    /// <remarks>
    /// The opened keys belong to the returned <see cref="RecoveredKeys"/> only once it is returned. On any
    /// other exit — cancellation, an exception — every key opened so far is wiped before it propagates.
    /// </remarks>
    public static async Task<RecoveredKeys?> ResolveAsync(
        RecoverySet set, string password, CancellationToken ct = default, RecoveryAttemptBudget? budget = null)
    {
        budget ??= new RecoveryAttemptBudget();
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var handedOver = false;
        try
        {
            var result = await ResolveCoreAsync(set, password, keys, budget, ct);
            handedOver = result != null;
            return result;
        }
        finally
        {
            if (!handedOver)
                foreach (var key in keys.Values) Array.Clear(key);
        }
    }

    private static async Task<RecoveredKeys?> ResolveCoreAsync(
        RecoverySet set, string password, Dictionary<string, byte[]> keys, RecoveryAttemptBudget budget, CancellationToken ct)
    {
        var epochByFp = new Dictionary<string, int>(StringComparer.Ordinal);
        var triedPerKey = new Dictionary<string, int>(StringComparer.Ordinal);
        var skipped = new List<RecoverySetBox>();

        foreach (var box in AttemptOrder(set.Boxes))
        {
            ct.ThrowIfCancellationRequested();
            epochByFp[box.DekFingerprint] = Math.Max(epochByFp.GetValueOrDefault(box.DekFingerprint), (int)Math.Min(box.EpochHint, int.MaxValue));
            if (keys.ContainsKey(box.DekFingerprint)) continue; // this key is already open, skip the derivation

            byte[] salt, wrapped, iv;
            try
            {
                salt = Convert.FromBase64String(box.Salt);
                wrapped = Convert.FromBase64String(box.Wrapped);
                iv = Convert.FromBase64String(box.Iv);
            }
            catch (FormatException) { continue; }

            // Cheap checks before the expensive one: a box the unwrap would refuse costs nothing.
            if (!RecoveryBoxKdf.IsWellFormed(box.Kind, salt, wrapped, iv)) continue;
            var preset = RecoveryBoxKdf.Resolve(box.KdfPreset);
            if (triedPerKey.GetValueOrDefault(box.DekFingerprint) >= budget.MaxPerKey || !budget.TryTake(preset))
            {
                skipped.Add(box);
                continue;
            }
            triedPerKey[box.DekFingerprint] = triedPerKey.GetValueOrDefault(box.DekFingerprint) + 1;
            byte[]? dek = preset.IsHeavy
                ? await HeavyDerivationQueue.RunAsync(() => RecoveryBoxCrypto.TryUnwrap(password, box.KdfPreset, salt, wrapped, iv), ct)
                : RecoveryBoxCrypto.TryUnwrap(password, box.KdfPreset, salt, wrapped, iv);
            if (dek == null) continue;
            if (DekFingerprint.Of(dek) != box.DekFingerprint) { Array.Clear(dek); continue; }
            keys[box.DekFingerprint] = dek;
            KeyOpened.Value?.Invoke(dek);
        }
        if (keys.Count == 0)
            return skipped.Count > 0 ? throw new RecoveryBoxesRemainingException(skipped.Count) : null;

        // Unwind: repeat until no link adds a key.
        var verifiedLinks = new List<RecoverySetLink>();
        bool progress;
        do
        {
            progress = false;
            foreach (var link in set.Links)
            {
                if (!keys.TryGetValue(link.NewFingerprint, out var newer)) continue;
                var opened = TryOpenLink(link, newer);
                if (opened == null) continue;
                if (!verifiedLinks.Contains(link)) verifiedLinks.Add(link);
                if (keys.ContainsKey(link.OldFingerprint)) { Array.Clear(opened); continue; }
                keys[link.OldFingerprint] = opened;
                progress = true;
            }
        } while (progress);

        // Heads: keys no verified link retires. A cycle of links (only possible with forged keys the
        // password opened, which the fingerprint check rules out) would leave none: then nothing is proven.
        var retiredFps = verifiedLinks.Select(l => l.OldFingerprint).ToHashSet(StringComparer.Ordinal);
        var heads = keys.Keys.Where(fp => !retiredFps.Contains(fp)).OrderBy(fp => fp, StringComparer.Ordinal).ToList();
        var currentFp = heads
            .OrderByDescending(fp => epochByFp.GetValueOrDefault(fp))
            .FirstOrDefault() ?? keys.Keys.First();
        if (heads.Count == 0) heads = [.. keys.Keys];

        // A skipped box whose key is open anyway (through another box or a link) has nothing left to say.
        var remaining = skipped.Count(b => !keys.ContainsKey(b.DekFingerprint));
        return new RecoveredKeys(keys, heads, currentFp, epochByFp, remaining, budget.AnyLimitReached);
    }

    /// <summary>
    /// The order boxes are tried in, decided by nothing a crafted set can use to push the real box out of
    /// the budget: no anchor, link or epoch (all unauthenticated at this point). Strong boxes first (their
    /// preset class is enforced), then round-robin over the claimed key fingerprints — every key's first box
    /// before any key's second. Exact duplicates (every field the unwrap reads equal) appear once. The
    /// per-key cap and the budget are applied by the caller, which counts what they leave out.
    /// </summary>
    internal static IEnumerable<RecoverySetBox> AttemptOrder(IEnumerable<RecoverySetBox> boxes)
    {
        var allowed = boxes
            .Where(b => RecoveryBoxKdf.IsAllowed(b.Kind, b.KdfPreset)) // the separate box validator
            .DistinctBy(b => (b.Kind, b.AuthorNodeId, b.DekFingerprint, b.KdfPreset, b.Salt, b.Wrapped, b.Iv))
            .ToList();
        foreach (var kind in new[] { RecoveryBoxKdf.KindStrong, RecoveryBoxKdf.KindDevice })
        {
            var byKey = allowed.Where(b => b.Kind == kind)
                .GroupBy(b => b.DekFingerprint, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.OrderBy(b => b.BoxId, StringComparer.Ordinal).ToList())
                .ToList();
            var rounds = byKey.Count == 0 ? 0 : byKey.Max(k => k.Count);
            for (var round = 0; round < rounds; round++)
                foreach (var key in byKey)
                    if (round < key.Count) yield return key[round];
        }
    }

    private static byte[]? TryOpenLink(RecoverySetLink link, byte[] newer)
    {
        try
        {
            var opened = MasterKeyManager.UnwrapMasterDek(Convert.FromBase64String(link.Wrapped), Convert.FromBase64String(link.Iv), newer);
            if (DekFingerprint.Of(opened) == link.OldFingerprint) return opened;
            Array.Clear(opened);
            return null;
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            return null;
        }
    }
}
