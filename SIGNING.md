# Code signing

OrgZ ships unsigned today. That costs real things, and they're measurable:

- **Windows**: SmartScreen warns on first run, and Defender scans every unsigned file it
  hasn't seen - a meaningful share of the cold first launch measured in `roadmap.md`.
- **macOS**: Gatekeeper *refuses to run* an unsigned, un-notarized app. Not a warning, a
  hard stop - so the macOS build is effectively undeliverable without this.

The release pipeline is wired for both, reusing the **same Azure and Apple accounts as
NAPLPS**. Every signing step is conditional on its secrets existing, so a release still
builds unsigned until they're set, and an expired credential degrades to an unsigned build
rather than a red pipeline. The pack step emits a `::warning::` when it signs nothing, so it
can't pass unnoticed.

Velopack must do the signing itself rather than us signing afterwards: `Update.exe` and
`Setup.exe` are signed at different points inside the package build.

## What's already done vs what's left

Everything in the workflow is written and committed. What remains is account-side, and only
Fox can do it.

### 1. Add the `appstore` environment to this repo

The build job declares `environment: appstore`, matching NAPLPS. Create it under
**Settings → Environments** and add the secrets below. Using an environment (rather than
repo secrets) keeps the same human-approval gate NAPLPS has before any certificate is used.

### 2. Add a federated credential for this repo - the one new step

The Azure App Registration already exists, but its federated credential is scoped to the
NAPLPS repo. OIDC subjects are per-repo, so add a second credential:

- Issuer: `https://token.actions.githubusercontent.com`
- Subject: `repo:FoxCouncil/OrgZ:environment:appstore`
- Audience: `api://AzureADTokenExchange`

Without this, `azure/login` fails with an audience/subject mismatch even though the client
ID is correct.

### 3. Create a `Developer ID Installer` certificate - probably missing

NAPLPS ships a **.dmg**, which only needs `Developer ID Application`. Velopack ships a
**.pkg**, which `productbuild` signs with `Developer ID Installer`. If NAPLPS is the only
thing signed so far, that certificate likely doesn't exist yet.

Create it at https://developer.apple.com/account/resources/certificates, install it, export
as .p12, and base64 it:

```sh
base64 -i DeveloperIDInstaller.p12 | pbcopy
```

The workflow fails loudly with `Missing a Developer ID identity` rather than silently
producing an unsigned installer.

## Secrets (environment: `appstore`)

Reused from NAPLPS, unchanged:

| Secret | Notes |
|---|---|
| `AZURE_CLIENT_ID` | App registration - needs the new federated credential above |
| `AZURE_TENANT_ID` | |
| `AZURE_SUBSCRIPTION_ID` | |
| `AZURE_TS_ENDPOINT` | e.g. `https://wus2.codesigning.azure.net` |
| `AZURE_TS_ACCOUNT` | Artifact Signing account name |
| `AZURE_TS_PROFILE` | Certificate profile name |
| `DEVID_CERT_P12` | base64 Developer ID **Application** .p12 |
| `DEVID_CERT_PASSWORD` | |
| `ASC_KEY_ID` | App Store Connect API key - used for notarization |
| `ASC_ISSUER_ID` | |
| `ASC_KEY_P8` | base64 of the .p8 |

New for OrgZ:

| Secret | Notes |
|---|---|
| `DEVID_INSTALLER_CERT_P12` | base64 Developer ID **Installer** .p12 (see step 3) |
| `DEVID_INSTALLER_CERT_PASSWORD` | optional - falls back to `DEVID_CERT_PASSWORD` |

Identity *names* aren't secrets and aren't stored: the workflow reads them out of the
keychain with `security find-identity`, so a renamed certificate can't silently break the
build.

## How it works

**Windows** - `azure/login` authenticates via OIDC, the workflow writes the
`metadata.json` signtool expects, and passes `--azureTrustedSignFile` to `vpk pack`.
Velopack bundles a compatible `signtool.exe` and the dlib package, so nothing extra is
installed on the runner. Artifact Signing certificates carry **instant SmartScreen
reputation**, which an OV certificate does not.

**macOS** - both .p12s are imported into a throwaway keychain, `set-key-partition-list` is
applied (without it `codesign` prompts and hangs a headless runner), notarytool credentials
are stored from the ASC API key, and `--signAppIdentity` / `--signInstallIdentity` /
`--notaryProfile` / `--keychain` go to `vpk pack`.

**Linux** - nothing to do. AppImages aren't signed in a way users check.

## Verifying

- **Windows**: `signtool verify /pa /v OrgZ-win.msi`, or the file's Digital Signatures tab.
  The pack log should stop saying `No signing parameters provided`.
- **macOS**: `codesign -dv --verbose=4 OrgZ.app`, then `spctl -a -vvv -t install OrgZ.app`
  (what Gatekeeper actually asks), and `xcrun stapler validate` for the notarization ticket.
