# STAGE 31 Report: Carry-Over Fixes & Sync-Embeddings Split

## 1. Done
- **Item 1: Carry-over fixes from Stage 2 review (Findings 2 & 3)**:
  - **Cleared key buffers on all paths including Keystore exception (`mobile/BeeMemoryBank.BlindMobile/Services/Blind/BlindMobilePairing.cs:189-232`)**:
    - Removed raw-return `PhoneCode()` API that exposed live secret byte arrays to callers.
    - Updated `WithPhoneCode<TResult>(Func<BlindPhoneCode, TResult> consume)` so that both `keys.LoadPairingSecret()` and `keys.LoadBackupKey()` occur inside an outer `try/finally` block.
    - If `LoadBackupKey()` throws an exception (e.g. Android Keystore decryption failure), the `finally` block runs immediately and clears the previously loaded pairing secret buffer using `CryptographicOperations.ZeroMemory`.
    - Added overload `WithPhoneCode(Action<BlindPhoneCode> consume)` and retained `PhoneCodeText()` which wipes all buffers immediately upon formatting.
    - Verified test-first with `WithPhoneCode_WhenSecondLoadThrows_ClearsFirstBufferImmediately`.
  - **Rejected control characters in `displayName` (`mobile/BeeMemoryBank.BlindMobile/Services/Blind/BlindMobilePairing.cs:60-63`)**:
    - Added `displayName.Any(char.IsControl)` check in `CreateIdentityAsync`, matching `BlindPhoneCode.TryParse` in `libs/BeeMemoryBank.Core/Models/BlindPhoneCodes.cs:47`.
    - Throws `ArgumentException` when control characters are detected, preventing generation of codes the Windows/hub side would reject.
    - Verified test-first with theory test `CreateIdentityAsync_RejectsControlCharactersInDisplayName` covering `\n`, `\r`, `\t`, `\0`.

## 2. Not done
- Item 2 in progress.

## 3. Deviations
- None.

## 4. Tests
- `dotnet test tests/BeeMemoryBank.BlindMobile.Tests --filter "WithPhoneCode_WhenSecondLoadThrows_ClearsFirstBufferImmediately|CreateIdentityAsync_RejectsControlCharactersInDisplayName"`: Passed: 5, Failed: 0, Skipped: 0.
  - `WithPhoneCode_WhenSecondLoadThrows_ClearsFirstBufferImmediately`: Saw RED first with `Expected keys.SecretBuffersClearedCount to be 1 because first buffer must be cleared when second load throws, but found 0`. Turned GREEN after moving secret load and backup key load inside `try/finally`.
  - `CreateIdentityAsync_RejectsControlCharactersInDisplayName`: Saw RED first with `Expected a <System.ArgumentException> to be thrown, but no exception was thrown` across all 4 control character inputs. Turned GREEN after checking `displayName.Any(char.IsControl)`.
- `dotnet test tests/BeeMemoryBank.BlindMobile.Tests`: Passed: 49, Failed: 0, Skipped: 0.
- `dotnet test tests/BeeMemoryBank.BlindMobile.Tests --filter "FullyQualifiedName~Forbidden"`: Passed: 4, Failed: 0, Skipped: 0.

## 5. Numbers
- `dotnet build mobile/BeeMemoryBank.BlindMobile/BeeMemoryBank.BlindMobile.csproj -f net10.0-android`: 0 errors.

## 6. Needs the orchestrator
- None so far.

## 7. Open risks
- None identified for Item 1.
