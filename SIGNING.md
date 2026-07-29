# Code signing

OrgZ ships unsigned today. That costs real things, and they're measurable:

- **Windows**: SmartScreen warns on first run, and Defender scans every unsigned file it
  hasn't seen. On a clean VM install that was a meaningful share of a 25-second first
  launch (see the startup section in `roadmap.md`).
- **macOS**: Gatekeeper *refuses to run* an unsigned, un-notarized app at all. This isn't a
  warning - it's a hard stop, so the macOS build is effectively undeliverable without it.

The release pipeline is already wired for both. Every signing step is conditional on its
secrets existing, so a release still builds unsigned until the accounts below are set up,
and an expired credential degrades to an unsigned build rather than a red pipeline. The
pack step emits a `::warning::` when it signs nothing, so it can't go unnoticed.

Velopack has to do the signing itself rather than us signing afterwards: `Update.exe` and
`Setup.exe` are signed at different points inside the package build.

---

## Windows — Azure Artifact Signing

Formerly "Trusted Signing". ~**USD $10/month**, cloud-only (no HSM posted to you), and it
gets **instant SmartScreen reputation** - the same benefit as an EV certificate, which
otherwise costs several hundred a year. An OV certificate would still leave users seeing
warnings until reputation accrues, so this is both cheaper and better.

**Eligibility:** individual developers must be in the **US or Canada**, and identity
validation needs a government photo ID plus a selfie check. Microsoft dropped the earlier
"three years of trading history" rule for individuals.

### Steps (Fox - these need a human and a credit card)

1. Azure account with an active subscription.
2. Register the Artifact Signing **resource provider**.
3. Create the **Artifact Signing account** - note its **name** and **region/endpoint**.
4. Create an **identity validation request** (Individual). Microsoft verifies this; it takes
   a while. Everything after this waits on approval.
5. Create a **certificate profile** using the **Public Trust** model. Note its name.
6. Create a federated credential so GitHub Actions can log in without a stored secret:
   an App Registration with a federated credential scoped to this repo.

### Repo secrets to add

| Secret | Value |
|---|---|
| `AZURE_CLIENT_ID` | App registration (client) ID |
| `AZURE_TENANT_ID` | Directory (tenant) ID |
| `AZURE_SUBSCRIPTION_ID` | Subscription ID |
| `AZURE_SIGNING_ENDPOINT` | e.g. `https://wus2.codesigning.azure.net` |
| `AZURE_SIGNING_ACCOUNT` | Artifact Signing account name |
| `AZURE_SIGNING_PROFILE` | Certificate profile name |

The workflow writes these into the `metadata.json` that `signtool.exe` expects and passes
`--azureTrustedSignFile` to `vpk pack`. Velopack bundles a compatible `signtool.exe` and the
dlib package, so nothing else needs installing on the runner.

---

## macOS — Developer ID + notarization

Needs a paid **Apple Developer Program** membership (~USD $99/year). Note this is *not* the
same as the "Apple Development" identity already on build-mac - that one signs for local
development only. Distribution outside the App Store needs **Developer ID**.

### Steps (Fox)

1. Join the Apple Developer Program.
2. Create **both** certificates at https://developer.apple.com/account/resources/certificates:
   - `Developer ID Application`
   - `Developer ID Installer`
   Velopack needs both - one signs the .app, the other the .pkg.
3. Download and install them, then export each as a **.p12** with a password.
4. Create an **app-specific password** (https://account.apple.com) for notarytool. It is
   shown once.
5. Find your **Team ID** in the membership details.

### Repo secrets to add

| Secret | Value |
|---|---|
| `APPLE_CERT_APPLICATION_P12` | base64 of the Developer ID **Application** .p12 |
| `APPLE_CERT_INSTALLER_P12` | base64 of the Developer ID **Installer** .p12 |
| `APPLE_CERT_PASSWORD` | password used when exporting the .p12 files |
| `APPLE_ID` | Apple account email |
| `APPLE_TEAM_ID` | Team ID |
| `APPLE_APP_SPECIFIC_PASSWORD` | the app-specific password from step 4 |
| `APPLE_SIGN_APP_IDENTITY` | e.g. `Developer ID Application: Your Name (TEAMID)` |
| `APPLE_SIGN_INSTALL_IDENTITY` | e.g. `Developer ID Installer: Your Name (TEAMID)` |

To base64 a certificate for the secret:

```sh
base64 -i DeveloperIDApplication.p12 | pbcopy
```

The workflow imports both into a throwaway keychain on the runner, sets the key partition
list (without it `codesign` prompts and hangs a headless runner), stores a notarytool
profile, and passes `--signAppIdentity` / `--signInstallIdentity` / `--notaryProfile` to
`vpk pack`.

---

## Linux

Nothing to do. AppImages aren't signed in any way users check, and there's no Gatekeeper or
SmartScreen equivalent.

## Verifying it worked

- **Windows**: `signtool verify /pa /v OrgZ-win.msi`, or check the file's Digital Signatures
  tab. The pack log should no longer say `No signing parameters provided`.
- **macOS**: `codesign -dv --verbose=4 OrgZ.app` and
  `spctl -a -vvv -t install OrgZ.app` - the latter is what Gatekeeper actually asks.
  `xcrun stapler validate` confirms the notarization ticket is attached.
