# bmb-updater — Standalone Updater for BeeMemoryBank

`bmb-updater` is a small, standalone utility intended to run with elevated privileges (e.g. as a Windows Scheduled Task under the `SYSTEM` account) to orchestrate updates for a BeeMemoryBank installation.

## Directory Layout Contract

The tool expects a root installation directory with the following structure:

```
<install-root>/
├── current -> app-1.0.0 (Windows Junction pointing to the active app version)
├── app-1.0.0/ (Directory containing version 1.0.0 code/assets)
├── app-1.1.0/ (Directory containing version 1.1.0 code/assets, created during update)
└── updates/
    ├── update.request (JSON request file)
    └── release-1.1.0.zip (Update package artifact file)
```

### The `updates/update.request` File

This JSON file acts as the request marker. It is written by the API when an update is approved/applied, and has the following schema (mirroring the fields of `UpdateCheckRequest` plus `targetVersion`):

```json
{
  "targetVersion": "1.1.0",
  "manifestJson": "{\n  \"schemaVersion\": 1,\n  \"channels\": {\n    \"stable\": {\n      \"version\": \"1.1.0\",\n      \"protocolVersion\": 1,\n      \"artifacts\": [\n        {\n          \"name\": \"release-1.1.0.zip\",\n          \"sha256\": \"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\",\n          \"size\": 12345\n        }\n      ]\n    }\n  }\n}",
  "manifestSignatureBase64": "MHgxMjM0NTY3ODkwYWJjZGVm..."
}
```

## Update Flow Sequence

1. **Detection**: If `<root>/updates/update.request` is missing, the tool exits with `0` (nothing to do).
2. **Signature Verification**: Verifies `manifestJson` using `manifestSignatureBase64` against the release public keys (using Ed25519). If verification fails, it exits with `2`.
3. **Artifact Location**: Looks for the artifact file specified in the manifest (e.g., `release-1.1.0.zip`) under `<root>/updates/` (or the directory specified by `--artifact-source-dir`).
4. **SHA-256 Validation**: Computes the SHA-256 hash of the artifact and compares it to the hash in the manifest. If they do not match, it exits with `4`.
5. **Extraction**: Decompresses the ZIP archive into a new `<root>/app-<targetVersion>` folder.
6. **Junction Swapping**:
   - Resolves the existing target of the `current` junction (saving it as the rollback target).
   - Deletes the `current` junction.
   - Recreates the `current` junction to point to the new `<root>/app-<targetVersion>` directory.
7. **Health Check**: Runs a GET request to the configured health check URL (e.g., `http://localhost:5000/health`) with retries.
8. **Rollback or Success**:
   - If the health check succeeds, the tool deletes `updates/update.request` and exits with `0`.
   - If the health check fails after all retries, the tool switches the `current` junction back to the rollback target, reports the failure, and exits with `5`.

## Command Line Usage

```
bmb-updater --root <dir> [options]

Options:
  --root <dir>                   Root install directory (required)
  --health-check-url <url>       Health check URL (default: http://localhost:5000/health)
  --health-check-retries <n>     Number of health check retries (default: 5)
  --health-check-interval <sec>  Delay between retries in seconds (default: 2)
  --pubkey-0 <base64>            Base64 encoded release public key slot 0 override
  --pubkey-1 <base64>            Base64 encoded release public key slot 1 override
  --artifact-source-dir <dir>    Directory containing the artifact to extract (default: <root>/updates)
```
