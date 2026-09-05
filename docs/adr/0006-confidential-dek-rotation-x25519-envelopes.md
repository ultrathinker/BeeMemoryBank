# ADR 0006: Confidential DEK rotation via per-peer X25519 envelopes

## Status

**Implemented** (2026-09-04/05, `d79794bd`, `94dfb46e`, `8eb0746b`). The design below was reviewed
before the crypto was written, per this file's original note; the option-B design (per-peer X25519
envelopes) is what shipped, along with the resilience fixes described in the closing section.

## Context

### What DEK rotation is for

Rotating the master DEK is meant to draw a line: content encrypted after the rotation must be
readable only by the nodes that are *still trusted at rotation time*. The obvious trigger is
revoking a peer — you rotate so the revoked node, even if it kept a copy of everything up to the
revocation, cannot read anything written afterwards.

### Why today's rotation does not actually achieve that

The new DEK is wrapped under the **old** DEK and shipped in the rotation event:

```
// DekRotationService.Propose.cs
var (encNewDek, ivNewDek) = MasterKeyManager.WrapMasterDek(newDek, oldDek);
... EncryptedNewDek = Convert.ToBase64String(encNewDek) ...   // rides in the signed payload
```

Every peer unwraps it with the old DEK it already holds:

```
// PeerDekRotationApplier.cs
newDek = MasterKeyManager.UnwrapMasterDek(encNewDekBytes, ivBytes, oldDek);
```

That payload is an Ed25519-signed event. It is **replicated to every node**, stored in `tbl_event`,
and kept across compaction as DEK-rotation chain material (see ADR 0003 and
`DekRewrapper.chain_encrypted_new_dek`). So the new DEK is derivable by **anyone who ever held the
old DEK** — which is exactly the set rotation is supposed to exclude:

- A revoked peer that keeps its database can read `encrypted_new_dek` from the retained event and
  unwrap the new DEK with the old one it still has. Rotation locked it out of nothing.
- Anyone who captured the old DEK at any point keeps deriving every future DEK, rotation after
  rotation, because each new key is only ever wrapped under its predecessor.

The confidentiality of rotation is therefore only as good as the *first* DEK ever leaked. This is
item 17's "rotation does not protect against a revoked node" caveat, currently documented in
`SECURITY.md` as a known limitation. Item 19 is the real fix.

### What we already have to build on

- Every node stores its own **Ed25519 identity private key** (`tbl_node_identity.ed25519_private_key`,
  master-DEK-wrapped in v1; `NodeIdentityRepository.GetDecryptedPrivateKey`).
- Every whitelist entry stores that peer's **Ed25519 identity public key**
  (`tbl_whitelist.ed25519_public_key`).
- BouncyCastle 2.7.0 is already referenced by `BeeMemoryBank.Crypto` and provides X25519 and the
  Ed25519→Curve25519 birational maps.

The elegant consequence: **no new key material has to be generated, distributed, or joined.** An
X25519 keypair is *derived* from the identity keys that already exist on every node and in every
whitelist row.

## Decision

Wrap the new DEK **once per currently-active peer**, each envelope openable only by that peer's
derived X25519 private key, and stop shipping the wrap-under-old-DEK copy on a confidential rotation.

### Key derivation (no new keys)

- Receiver derives its own X25519 **private** scalar from its Ed25519 **seed** (the standard
  `crypto_sign_ed25519_sk_to_curve25519`: SHA-512 the seed, clamp the low half).
- Initiator derives each peer's X25519 **public** point from that peer's stored Ed25519 **public**
  key (`crypto_sign_ed25519_pk_to_curve25519`: Edwards `y` → Montgomery `u`).

Both are pure functions of existing key material, so a node that is in the whitelist and holds its
identity key can always open its envelope, and nothing else can.

### Envelope construction (initiator, at propose time)

1. Enumerate the **active** whitelist entries (`status='A'`) — this snapshot IS the definition of
   "who the rotation includes." A revoked row is not enumerated and gets no envelope.
2. Generate one ephemeral X25519 keypair for the whole rotation.
3. For each active peer *P* (and for the initiator itself):
   - `shared = X25519(ephemeral_priv, curve25519_pub(P.ed25519_pub))`
   - `wrapKey = HKDF-SHA256(shared, salt = rotation_event_id, info = "bmb-dek-rotation-v1" || P.node_id)`
   - `envelope_P = AES-256-GCM(newDek, wrapKey, nonce_P)` with `node_id` bound as AAD.
4. Payload gains an additive field:
   ```
   "dek_envelopes": {
       "ephemeral_pub": "<base64 X25519 public>",
       "peers": { "<NODE_ID uppercase>": { "wrapped": "<b64>", "nonce": "<b64>" }, ... }
   }
   ```
   `encrypted_new_dek` / `iv` are **omitted** on a confidential rotation (see the rollout decision).

The signature format is unchanged: `dek_envelopes` is just another payload field the existing
Ed25519 signature covers. (Note: bind `node_id` in AAD with `upper()` — the Guid-case trap that has
already bitten tags and the rotation epoch read.)

### Envelope opening (receiver, at commit time)

`PeerDekRotationApplier` looks up `dek_envelopes.peers[myNodeId]`, derives its X25519 private from its
Ed25519 seed, recomputes `shared` and `wrapKey`, and AES-GCM-opens its envelope to get `newDek`.
Everything downstream (`DekRewrapper.RewrapAllAsync`, epoch bump, chain material) is unchanged — only
the *source* of `newDek` moves from "unwrap under old DEK" to "open my envelope."

## The decision that needs the maintainer: rollout vs. confidentiality

There is a genuine tension, and it is the reason this is an ADR and not just a commit:

- **If the confidential rotation still ships `encrypted_new_dek`** (for old nodes to read), the
  revoked/old-key-holder can *still* read it. The confidentiality gain is **zero**. So a rotation is
  only confidential if it OMITS the old field.
- **If it omits the old field**, a node running pre-19 code cannot apply the rotation at all — it has
  no envelope reader and no old-DEK field, so it falls permanently behind.

So a confidential rotation requires **every active peer to be on post-19 code**. Options, for the
maintainer to choose:

1. **Gate on capability (recommended).** Add a per-node "supports envelope rotation" capability
   (surfaced at join / in the whitelist, or inferred from a version the node already advertises over
   mDNS/`/api/version`). `Propose` refuses a *confidential* rotation while any active peer lacks it,
   and tells the operator which nodes to upgrade first. Until then, rotation still works in the old
   (non-confidential) mode, explicitly labelled as such. This keeps the mesh always-live and makes
   the confidentiality property honest rather than accidental.
2. **Dual-ship for one release, then flip.** Ship both fields for a transition window, accepting that
   rotations in that window are not confidential, then drop the old field. Simpler, but the window is
   silently insecure and easy to forget to close.
3. **Hard cutover.** Bump the sync protocol version; a mixed mesh must upgrade before it can rotate.
   Cleanest cryptographically, worst operationally.

Recommendation: **option 1**. It never breaks a running mesh, never silently ships a non-confidential
rotation while claiming otherwise, and turns "is this rotation actually confidential?" into a
checkable precondition.

## Consequences and edge cases to handle in implementation

- **A peer added AFTER a rotation** was never enumerated, so it has no envelope for that rotation's
  DEK. It must receive the current DEK the same way a fresh joiner already does — through the
  master-password-authenticated join / snapshot path (`InitEndpoints`/`JoinEndpoints`), which
  transfers the live DEK directly. Confirm that path already covers "join into an already-rotated
  vault" (it should — join transfers the *current* DEK, not the chain).
- **An offline peer** at rotation time is fine: its envelope sits in the replicated event and it
  opens it whenever it comes back and pulls the event.
- **`LazySlotRewrapService` and chain material** (ADR 0003): today a user's key slot is re-wrappable
  after compaction because `chain_encrypted_new_dek` (wrap-under-old-DEK) survives. Under envelopes,
  the node stores *its own opened* `newDek` chain locally exactly as it does now
  (`PeerDekRotationApplier` already persists the base64 it needs) — but the cross-node recovery
  material is no longer a single old-DEK wrap. Verify the lazy-rewrap walk still has what it needs
  from the node's own perspective; it should, since a node only ever rewraps *its own* slots.
- **The initiator's own copy**: give the initiator an envelope too (uniform), rather than a special
  case that keeps `newDek` in the clear in the payload.
- **AAD / KDF hygiene**: bind `node_id` and `rotation_event_id` into HKDF salt/info and GCM AAD so an
  envelope cannot be replayed against a different node or a different rotation.
- **`SECURITY.md` / `docs/sync.md`**: the trust-model section that currently says rotation does not
  exclude a revoked node gets rewritten once confidential rotation is the default — but only for
  rotations that actually went confidential (see the rollout decision).

## Test plan (on the local `test1`/`test2` mesh, never prod)

1. Two-node mesh, both on post-19 code: rotate, confirm both nodes open their envelope and converge
   on the same new epoch, and that `encrypted_new_dek` is absent from the event.
2. Revoke `test2`, rotate on `test1`, and assert that a copy of `test2`'s pre-revocation database
   (throwaway container, as in the migration rehearsal) **cannot** derive the new DEK from the
   retained event — the concrete proof the old scheme fails and this one does not.
3. A third node joining after the rotation gets the current DEK via join and reads post-rotation
   content.
4. Mixed-version mesh: `Propose` refuses a confidential rotation and names the lagging node
   (option 1).

## Estimated effort

~1 week: the crypto helpers + envelope format (2 days), the propose/apply wiring and the capability
gate (2 days), migration/`SECURITY.md`/docs and the mesh test matrix (1–2 days). The dedup question
for media (item 16b) is deliberately folded into this review because it is the same class of
decision — what the holder of the database can learn — and may share the same X25519 machinery.

## Resolution (what actually shipped)

The rollout question above was answered with **option 2 (dual-ship), not the recommended option 1
(capability gate)**: `Propose` always builds per-peer envelopes, and the legacy
`AES-256-GCM(newDek, oldDek)` field is still emitted alongside them as a fallback for a peer that
hasn't upgraded — there is no capability negotiation and no refusal for a mixed-version mesh. This
means a mesh that still contains a pre-ADR-0006 peer gets a rotation that is *not* fully
confidential for that specific rotation (the legacy field is exactly as exposed as it always was),
without the honesty check the recommended option would have provided. `SECURITY.md`'s "What rotation
does not protect against" section states this trade-off explicitly rather than assuming it away.

Edge cases from the "Consequences" section above resolved as expected: envelope construction
validates the peer's Ed25519 key is a canonical prime-order-subgroup point (rejecting the
off-curve/small-order case the birational map alone would silently mishandle) and excludes an
unusable peer from that rotation's envelope set rather than aborting for the whole mesh; the
initiator gets a uniform envelope like any other peer; HKDF salt/info is bound to the rotation id
and recipient node id as specified; and the node's own Ed25519 identity seed — not called out above
— turned out to need re-wrapping on every rotation too (missed in the first implementation pass,
which worked once and then permanently locked out the node on the *second* rotation, since the seed
is needed to open the node's own envelope in the first place).
