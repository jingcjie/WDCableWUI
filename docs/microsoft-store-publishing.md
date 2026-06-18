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

Clean and run tests before packaging:

```powershell
dotnet clean WDCableWUI.sln -c Release
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

Quick manifest verification from the actual MSIX:

```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($x64Package.FullName)
try {
  $entry = $zip.GetEntry("AppxManifest.xml")
  $reader = [System.IO.StreamReader]::new($entry.Open())
  try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
} finally { $zip.Dispose() }

$identity = $manifest.Package.Identity
[pscustomobject]@{
  Package = $x64Package.FullName
  SHA256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $x64Package.FullName).Hash
  Name = [string]$identity.Name
  Publisher = [string]$identity.Publisher
  Version = [string]$identity.Version
  ProcessorArchitecture = [string]$identity.ProcessorArchitecture
} | Format-List
```

ARM64 is optional. Only build and upload it when intentionally expanding Store package coverage:

```powershell
msstore package . --version $Version --arch arm64 --output "$Stage\arm64"
```

For Partner Center, Microsoft recommends uploading an app package upload file (`.msixupload` or `.appxupload`) when available. For this repo's manual process, upload the generated x64 `.msix` above unless a proper `.msixupload` is produced and verified.

## 5. Upload package draft

Fast x64-only CLI path:

```powershell
$UploadDir = Join-Path $Stage "upload-x64-only"
New-Item -ItemType Directory -Force -Path $UploadDir | Out-Null
Get-ChildItem -LiteralPath $UploadDir -Force -ErrorAction SilentlyContinue | Remove-Item -Force
Copy-Item -LiteralPath $x64Package.FullName -Destination $UploadDir

$uploadPackage = Join-Path $UploadDir (Split-Path -Leaf $x64Package.FullName)
msstore publish "$uploadPackage" --appId 9MZQMRHFFJJW --noCommit --verbose
```

Important: pass the `.msix` file itself as the positional `pathOrUrl` argument. Do not pass the repo root with `--inputDirectory` for this loose-MSIX upload.

The verbose log must show:

- `This seems to be a MSIX project.`
- `Trying to publish these 1 files: '<path>\WDCableWUI_<MSIX_VERSION>_x64.msix'`
- `Copying '<path>\WDCableWUI_<MSIX_VERSION>_x64.msix' to zip bundle folder.`
- `Successfully uploaded the application package.`
- `Skipping submission commit.`

If the log says `Trying to publish these 0 files`, stop. Delete that draft if it was created for the current release, then rerun the command with the `.msix` file path as shown above:

```powershell
msstore submission delete 9MZQMRHFFJJW --no-confirm
msstore publish "$uploadPackage" --appId 9MZQMRHFFJJW --noCommit --verbose
```

After the draft upload, verify the draft package state before committing. Do not paste raw `msstore submission get` output into logs or commits because it can include a temporary `FileUploadUrl`.

Expected package state for a normal replacement:

- Old package: `FileStatus` is `PendingDelete`.
- New package: `FileName` is `WDCableWUI_<MSIX_VERSION>_x64.msix`.
- New package: `FileStatus` is `PendingUpload`.

PowerShell package-only summary:

```powershell
$out = & msstore submission get 9MZQMRHFFJJW 2>$null
$text = [string]::Join("`n", [string[]]$out)
$id = [regex]::Match($text, '"Id"\s*:\s*"(?<v>[^"]+)"').Groups["v"].Value
$status = [regex]::Match($text, '"Status"\s*:\s*"(?<v>[^"]+)"').Groups["v"].Value
$pkgBlock = [regex]::Match(
  $text,
  '"ApplicationPackages"\s*:\s*\[(?<v>.*?)\]\s*,\s*"PackageDeliveryOptions"',
  [System.Text.RegularExpressions.RegexOptions]::Singleline)

Write-Host "SubmissionId: $id"
Write-Host "Status: $status"
foreach ($match in [regex]::Matches($pkgBlock.Groups["v"].Value, '\{(?<obj>.*?)\}', [System.Text.RegularExpressions.RegexOptions]::Singleline)) {
  $obj = $match.Groups["obj"].Value
  [pscustomobject]@{
    FileName = [regex]::Match($obj, '"FileName"\s*:\s*"(?<v>[^"]+)"').Groups["v"].Value
    FileStatus = [regex]::Match($obj, '"FileStatus"\s*:\s*"(?<v>[^"]+)"').Groups["v"].Value
    Version = [regex]::Match($obj, '"Version"\s*:\s*"(?<v>[^"]+)"').Groups["v"].Value
    Architecture = [regex]::Match($obj, '"Architecture"\s*:\s*"(?<v>[^"]+)"').Groups["v"].Value
  } | Format-List
}
```

Do not rely on `msstore publish . --inputDirectory <folder>`. In this repo, that uses the WinUI project publisher path and can create a draft while logging `Trying to publish these 0 files`, leaving the package table unchanged. The correct loose-MSIX publisher path is `msstore publish "<verified .msix path>" --appId 9MZQMRHFFJJW --noCommit`.

Manual Partner Center fallback:

1. Open Partner Center for product `9MZQMRHFFJJW`.
2. Start or open a package update submission.
3. On the Packages page, upload the generated x64 app MSIX: `$x64Package.FullName`.
4. Wait for package validation.
5. Confirm the Partner Center package table shows the new `$Version`, expected file name ending in `_x64.msix`, and architecture `x64` before submitting.

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

`CommitStarted` means Partner Center accepted the commit and is processing ingestion/certification asynchronously. The final Store availability depends on Microsoft certification.

## 8. Troubleshooting

- If Partner Center rejects the package, confirm the manifest version is higher than the last published version.
- If `msstore submission get` fails immediately after draft creation, wait a minute and retry once.
- If `msstore publish` times out while retrieving the app and no draft was created, rerun the same command.
- If a bad draft was created for the current release, delete it with `msstore submission delete 9MZQMRHFFJJW --no-confirm` before retrying.
- If `msstore` reports a telemetry settings lock, check for running `msstore` processes and rerun the command serially.
- If metadata update fails, inspect the generated JSON for malformed listing fields, missing localized listings, or keyword word-count violations.
