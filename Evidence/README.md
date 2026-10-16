# Captured plain WinUI results

Captured on 16 October 2026 using this project's direct native calls.
Windows 11 build **26200.9448**, AMD Ryzen AI 7 PRO 350, x64, .NET SDK
**10.0.401**. `GetReadyState()` returned `Ready` in these trials.

Each report is a separate process. The default question is
`who is the fastest gun in the west?`; "prefixed" adds the literal `User: `.
The full schema is the same on both versions.

| SDK variant | Case | Observed result |
|-------------|------|-----------------|
| Current: 2.5.4-experimental | Plain text, prefixed | Completed in 7.88 s with 42 progress callbacks |
| Current | Four-field JSON, prefixed, four trials | No completion before 25 s, zero progress callbacks |
| Current | Four-field JSON, unprefixed | No completion before 25 s, zero progress callbacks |
| Current | Minimal one-string schema, unprefixed | Completed in 3.08 s, zero progress callbacks |
| Current, returning after older-version trials | Plain text, prefixed | Blocked inside `CreateContext` until the 25 s bound |
| Previous: 2.4.1-experimental | Four-field JSON, prefixed, two trials | No completion before 25 s, zero progress callbacks |
| Previous | Four-field JSON, unprefixed | Completed in 5.14 s, zero progress callbacks |
| Previous | Minimal one-string schema, unprefixed | Completed in 5.46 s, zero progress callbacks |
| Previous | Plain text, prefixed | Blocked inside `CreateContext` until the 25 s bound |

The JSON reports include all measured events and generated text. A completed
response is not a factual-accuracy assertion. Zero progress followed by a
completed result is distinguished from zero progress and no completion.

## Verified native AI binaries

- Current AI package: **2.5.4-experimental**; native file **2.5.3.10969**.
  SHA256: `DACC7657BBD5880EAF1EC23B4A1F3CB7E65A11F4C1F27186F453087409258EF0`.
- Previous AI package: **2.4.8-experimental**; native file **2.4.7.28936**.
  SHA256: `77E09F777C6129E918CAE2C3FCA4664F6E675A2E57615BE353E29D2F210688A4`.

The reports also fingerprint WinUI and the Windows App Runtime. The launch
script checks package registration against the requested build layout and
rejects a result if the loaded AI hash does not match that layout.

## What this establishes

The native structured operation can return its async object promptly and then
produce neither progress nor completion within the bounded wait. The issue
reproduces without MAUI, Microsoft.Extensions.AI, or any chat adapter.
The same four-field, prefixed case stalls on the preceding experimental SDK
as well as the current one.

This is not proof of an infinite deadlock, the underlying cause, or an
SDK-version-independent failure rate. The wait is bounded at 25 seconds.
OS model packages and shared inference services are not reset between trials;
changing the NuGet SDK does not roll those back.
No automatic retries, prompt rewrites, or schema transformations are used.
