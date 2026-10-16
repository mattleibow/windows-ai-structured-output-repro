# Native Windows AI structured-output repro

A plain C# WinUI desktop app using only the Windows App SDK and .NET.
There are no MAUI, Microsoft.Extensions.AI, test-runner, or repository references.
The prompt and schema are literal strings passed to the official
`Microsoft.Windows.AI.Text.LanguageModel` API.

## Question being investigated

For `who is the fastest gun in the west?`, ordinary text generation can complete
while schema-constrained generation produces neither progress nor a completed
result within 25 seconds. The behavior is intermittent; a successful run does
not rule it out.

The repro does not claim that the latest SDK introduced the problem.
It provides two package graphs, separate build outputs, and actual loaded native
DLL versions and SHA256 hashes to compare them.
The [captured results](Evidence/README.md) include successful calls and bounded
stalls on the previous and current experimental SDKs.

## Requirements

- Windows 11 24H2+ on hardware supported by the Windows AI language model.
- .NET 10 SDK (tested with 10.0.401), Windows Developer Mode, and x64.
- An available language model. This app checks readiness but does not initiate
  a model download or change feature-access policy.

The app is MSIX-packaged and declares `systemAIModels`. It uses self-contained
experimental Windows App SDK packages; this is a development repro, not a Store app.
Stable Phi Silica runtime access can require a Limited Access Feature token.
`Build.ps1` stages the files listed in the SDK-generated `.build.appxrecipe`;
`Run.ps1` registers that loose layout with `Add-AppxPackage`.

## Interactive reproduction

From Windows PowerShell or PowerShell:

```powershell
.\Run.ps1 -SdkVariant current
```

1. Leave the default question and schema unchanged.
2. Click **Structured JSON**. Watch the timestamps for native entry, return of
   the async operation, progress, and completion.
3. A cancelled wait is not a successful response or proof the native model stopped.
4. Close and restart the app. Click **Plain text** for the same question.
5. Repeat in fresh processes. The **Prefix prompt** checkbox tests the same
   question with or without the literal `User: ` prefix.

The default schema requires four properties: a summary string, an array of key
points, a category string, and a sentiment enum. You can edit both schema and
prompt to isolate a smaller case.

## Automated fresh-process comparison

These commands activate the MSIX through the official Windows
`IApplicationActivationManager` API and close the repro after one trial:

```powershell
.\Run.ps1 -SdkVariant current -Case plain -Label current-plain-1
.\Run.ps1 -SdkVariant current -Case structured -Label current-json-1 -SkipBuild
.\Run.ps1 -SdkVariant current -Case structured -Label current-json-2 -SkipBuild

.\Run.ps1 -SdkVariant previous -Case plain -Label previous-plain-1
.\Run.ps1 -SdkVariant previous -Case structured -Label previous-json-1 -SkipBuild
.\Run.ps1 -SdkVariant previous -Case structured -Label previous-json-2 -SkipBuild
```

Add `-Raw` to omit the prompt prefix, or `-MinimalSchema` to send this literal
one-property schema instead of the four-field schema:

```json
{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"]}
```

This smaller case has no descriptions, enum, array, or `additionalProperties`.
`Results\` receives one JSON report per
automatic trial. Interactive reports are saved under the app's LocalState folder,
whose path appears in the window. Reports contain the prompt, schema, generated
text, outcome, timestamps, progress count, OS and native module fingerprints.
When switching SDK variants, the script archives this repro's local JSON reports,
removes only this repro's development package, and registers the other layout.
It verifies the registered path and loaded AI DLL hash; this prevents Windows
from silently activating a previously registered build with the same identity.

| Package | `current` | `previous` |
|---------|-----------|------------|
| Microsoft.WindowsAppSDK | 2.5.4-experimental | 2.4.1-experimental |
| Microsoft.WindowsAppSDK.AI | 2.5.4-experimental | 2.4.8-experimental |
| Microsoft.WindowsAppSDK.Foundation | 2.3.13-experimental | 2.3.11-experimental |

The remaining dependencies are those declared by the official SDK packages.
`NuGet.Config` uses Microsoft's public dnceng package mirror.

## Native calls

`App.xaml.cs` calls `LanguageModel.CreateAsync()`, then either:

```csharp
using var context = model.CreateContext();
var operation = model.GenerateResponseAsync(context, prompt, new LanguageModelOptions());
operation.Progress = (_, text) => { /* record callback */ };
var result = await operation;
```

or:

```csharp
var operation = model.GenerateStructuredJsonResponseAsync(prompt, schema, new LanguageModelOptions());
operation.Progress = (_, text) => { /* record callback */ };
var result = await operation;
```

The calls run on a worker so a blocking native entry point does not freeze WinUI.
The UI bounds its wait to 25 seconds and requests native cancellation. It does not
retry, fabricate progress, alter output, or change the schema at runtime.
After an abandoned request, restart the process before another trial.

## Remove the development package

```powershell
powershell.exe -NoProfile -Command "Get-AppxPackage -Name WindowsAIStructuredRepro | Remove-AppxPackage"
```
