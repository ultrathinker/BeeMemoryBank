# bmb-release — BeeMemoryBank Release Signing Tool

A small CLI wrapper around the project's existing Ed25519 primitives
(`BeeMemoryBank.Crypto.Ed25519Signer`) used to sign and verify the
`releases.json` auto-update manifest.

## File format

All key and signature files are **base64-encoded text files** (standard
Base64, no line breaks, UTF-8). This makes them trivial to copy-paste,
store in a password manager, and inspect with any text editor.

| File              | Contents                                      | Size on disk |
|-------------------|-----------------------------------------------|--------------|
| `release-private.key` | Base64(32-byte Ed25519 seed / private key)| ~44 chars    |
| `release-public.key`  | Base64(32-byte Ed25519 public key)        | ~44 chars    |
| `*.sig`               | Base64(64-byte Ed25519 signature)         | ~88 chars    |

The signing operation operates on the **raw bytes of the file exactly as
they exist on disk** — no JSON canonicalization, no whitespace
normalization. This matches the superplan requirement ("Ed25519 над
байтами файла (без канонизации JSON)").

## Usage

### 1. Generate a keypair

```
bmb-release gen-key --out <dir> [--force]
```

Writes `<dir>/release-private.key` and `<dir>/release-public.key`.
**Refuses to overwrite** existing key files unless `--force` is
specified — a release signing key must never be silently clobbered.

**Keep `release-private.key` offline and secret.** Only the public key
needs to be distributed (it will be embedded in clients for signature
verification).

### 2. Sign a file

```
bmb-release sign --key <private-key-path> --file <path-to-releases.json> --out <path-to-.sig>
```

Reads the file bytes verbatim, signs them with the private key, and
writes the signature to the output path.

### 3. Verify a file

```
bmb-release verify --pubkey <public-key-path> --file <path> --sig <path-to-.sig>
```

Prints a clear success or failure message. Exit codes:

| Code | Meaning                                                     |
|------|-------------------------------------------------------------|
| 0    | Signature is **valid** — file is authentic and unmodified   |
| 1    | Usage or I/O error (bad arguments, file not found, etc.)    |
| 2    | Signature is **invalid** — tamper detected or wrong key     |

## Typical release workflow

```bash
# One-time: generate and store the signing key somewhere safe/offline
bmb-release gen-key --out ~/secure/bmb-keys

# Per release: sign the manifest
bmb-release sign \
  --key ~/secure/bmb-keys/release-private.key \
  --file releases.json \
  --out releases.json.sig

# Publish releases.json + releases.json.sig to GitHub Pages / Releases.

# Anyone can verify:
bmb-release verify \
  --pubkey release-public.key \
  --file releases.json \
  --sig releases.json.sig
```

## Security notes

- The private key is a raw 32-byte Ed25519 seed encoded as Base64.
  Treat it like a password — it should live offline, not in CI.
- The public key is safe to commit and distribute widely.
- Ed25519 is a deterministic scheme; the same (key, data) pair always
  produces the same signature, which aids reproducibility checks.
