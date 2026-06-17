# Microsoft Store publishing for WDCable

This repo publishes the Windows WinUI/MSIX build of WDCable to Microsoft Store.

Store identity:

- Product ID: `9MZQMRHFFJJW`
- App name: `WDCable`
- Package identity name: `JINGCJIE.4084573DC88A9`
- Publisher: `CN=79F5182C-F623-427B-BB35-112212334FEE`

Do not commit Partner Center client secrets, tenant secrets, certificates, generated submission JSON containing private upload URLs, or local credential files.

## 1. Prerequisites

- Microsoft Store Developer CLI installed and configured.
- Partner Center account access for product `9MZQMRHFFJJW`.
- Visual Studio/Windows SDK components needed for WinUI MSIX packaging.
- A release version chosen by the user and written as a four-part MSIX version, for example `<major>.<minor>.<build>.<revision>`.

Run Store CLI commands serially. Running multiple `msstore` commands at the same time can fail with a lock on `C:\Users\<user>\AppData\Local\Microsoft\MSStore.CLI\telemetrySettings.json`.

## 2. Preflight checks

From the repo root:

```powershell
msstore --version
msstore info
msstore apps get 9MZQMRHFFJJW
msstore submission status 9MZQMRHFFJJW
```

Expected before starting a normal release:

- `msstore apps get` shows `PendingApplicationSubmission: null`.
- `msstore submission status` reports the last submission as `Published`.
- The package identity name matches `JINGCJIE.4084573DC88A9`.

If a pending submission exists, inspect it before uploading a new package. Do not overwrite, delete, or commit a pending submission unless it belongs to the release currently being prepared.

## 3. Version and tests

Update `Package.appxmanifest`:

```xml
Version="<MSIX_VERSION>"
```

Run tests before packaging:

```powershell
dotnet test WDCableWUI.Tests\WDCableWUI.Tests.csproj -c Release
```

## 4. Package

Set release variables:

```powershell
$Version = "<MSIX_VERSION>"
$Stage = "AppPackages\Store_$Version"
```

Build the x64 package:

```powershell
msstore package . --version $Version --arch x64 --output "$Stage\x64"
```

Find the x64 app MSIX to upload:

```powershell
$x64Package = Get-ChildItem -LiteralPath "$Stage\x64" -Recurse -File -Filter "*_x64.msix" |
  Where-Object { $_.FullName -notlike "*\Dependencies\*" } |
  Select-Object -First 1
$x64Package.FullName
```

The file to upload should look like:

```text
AppPackages\Store_<MSIX_VERSION>\x64\WDCableWUI_<MSIX_VERSION>_X64_Test\WDCableWUI_<MSIX_VERSION>_x64.msix
```

Before uploading, inspect the generated x64 app MSIX and confirm:

- Version matches `$Version`.
- Identity name is `JINGCJIE.4084573DC88A9`.
- Publisher is `CN=79F5182C-F623-427B-BB35-112212334FEE`.
- Architecture is x64.

ARM64 is optional. Only build and upload it when intentionally expanding Store package coverage:

```powershell
msstore package . --version $Version --arch arm64 --output "$Stage\arm64"
```

For Partner Center, Microsoft recommends uploading an app package upload file (`.msixupload` or `.appxupload`) when available. For this repo's manual process, upload the generated x64 `.msix` above unless a proper `.msixupload` is produced and verified.

## 5. Upload package draft

Use Partner Center for package upload:

1. Open Partner Center for product `9MZQMRHFFJJW`.
2. Start or open a package update submission.
3. On the Packages page, upload the generated x64 app MSIX: `$x64Package.FullName`.
4. Wait for package validation.
5. Confirm the Partner Center package table shows the new `$Version`, expected file name ending in `_x64.msix`, and architecture `x64` before submitting.

Do not rely on `msstore publish --inputDirectory` with a manually created `.msixbundle`. The Store Developer CLI publish option documents `.msix` / `.msixupload` inputs, and in this repo it accepted the command while leaving the published package unchanged.

After the manual package upload, fetch the draft metadata if you need to update listing text:

```powershell
msstore submission get 9MZQMRHFFJJW
```

## 6. Listing metadata

If listing metadata needs to change, fetch the draft JSON, update only intended listing fields, and push the JSON text to `updateMetadata`.

Useful listing fields:

- `Listings.<locale>.BaseListing.Description`
- `Listings.<locale>.BaseListing.Features`
- `Listings.<locale>.BaseListing.Keywords`
- `Listings.<locale>.BaseListing.ReleaseNotes`
- `Listings.<locale>.BaseListing.ShortDescription`

Keep metadata within Partner Center limits. In particular, keyword entries are not the only limit; Partner Center also validates total keyword words.

Push updated metadata with an argument-array based invoker. The `updateMetadata` argument is the JSON text, not a file path, and Windows PowerShell 5.1 can split or strip quotes from a large JSON argument.

```powershell
node -e "const fs=require('fs'); const cp=require('child_process'); const metadata=fs.readFileSync(process.argv[1],'utf8').replace(/^\uFEFF/,''); const r=cp.spawnSync('msstore',['submission','updateMetadata','9MZQMRHFFJJW',metadata],{stdio:'inherit',windowsHide:true}); process.exit(r.status ?? 1);" "$Stage\store-submission.updated.json"
```

Remove local metadata JSON files after use if they contain temporary upload URLs.

## 7. Submit to certification

After verifying the bundle manifest, draft state, and listing text:

```powershell
msstore submission publish 9MZQMRHFFJJW
msstore submission status 9MZQMRHFFJJW
```

The final Store availability depends on Microsoft certification.

## 8. Troubleshooting

- If Partner Center rejects the package, confirm the manifest version is higher than the last published version.
- If `msstore submission get` fails immediately after draft creation, wait a minute and retry once.
- If `msstore` reports a telemetry settings lock, check for running `msstore` processes and rerun the command serially.
- If metadata update fails, inspect the generated JSON for malformed listing fields, missing localized listings, or keyword word-count violations.
