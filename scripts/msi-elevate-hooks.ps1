# Make Velopack's install/uninstall hooks run elevated.
#
# WHY THIS EXISTS: vpk emits the app hooks as deferred but IMPERSONATED custom actions
# (msidbCustomActionTypeInScript, without msidbCustomActionTypeNoImpersonate). A deferred
# impersonated action runs with the installing user's token, not the installer's elevated
# one, so `sc create` inside OrgZ's OnAfterInstall hook fails with access denied and the
# background service is silently never registered. Velopack's own RustCleanup action is
# already type 3137 - the same flags PLUS NoImpersonate - which is what a custom action
# that touches machine state is supposed to be.
#
# The failure is invisible AND machine-dependent: an install driven from a process that
# already holds a full administrator token (PowerShell Direct, an elevated terminal) has
# nothing to impersonate down to and the hook succeeds, so this passes on a test VM and
# fails on a real desktop where UAC hands out a filtered token. Observed doing exactly
# that between v0.12.0 (VM, worked) and v0.13.1 (real machine, no service): the log there
# reads "Skipping service install: the hook is not running elevated", preceded by an
# UnauthorizedAccessException creating a directory under Program Files.
#
# SIGNING: this rewrites the MSI, which invalidates its Authenticode signature. The caller
# MUST re-sign afterwards. Failing to do so trades a missing service for SmartScreen.
#
# Usage: scripts/msi-elevate-hooks.ps1 -MsiPath releases/OrgZ-win.msi

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $MsiPath
)

$ErrorActionPreference = 'Stop'

$MsiPath = (Resolve-Path -LiteralPath $MsiPath).Path

# msidbCustomActionTypeNoImpersonate. OR'd into whatever flags the action already carries
# rather than assigning a literal, so a Velopack change to the other bits is preserved.
$NoImpersonate = 2048

# Both hooks need it. Install runs `sc create` + `sc start`; uninstall runs `sc delete`,
# and leaving THAT unelevated orphans a LocalSystem service pointing at a deleted exe.
$Actions = @('InstallHookDeferred', 'UninstallHookDeferred')

# Every COM object this script touches, newest first, so it can drop them deterministically.
#
# Releasing matters more than it looks: the Installer/Database RCWs hold the .msi open, and
# waiting for the GC to notice leaves the file locked against the re-sign step that has to
# follow. [GC]::Collect alone proved unreliable here - the verification re-open failed with
# "OpenDatabase,DatabasePath,OpenMode" while a stale view was still alive.
$script:ComObjects = @()

function New-Com([string] $progId)
{
    $object = New-Object -ComObject $progId
    $script:ComObjects = @($object) + $script:ComObjects
    return $object
}

function Invoke-Com($object, [string] $name, $arguments)
{
    $result = $object.GetType().InvokeMember($name, 'InvokeMethod', $null, $object, $arguments)
    if ($result -is [__ComObject])
    {
        $script:ComObjects = @($result) + $script:ComObjects
    }

    return $result
}

function Get-ComProperty($object, [string] $name, $arguments)
{
    return $object.GetType().InvokeMember($name, 'GetProperty', $null, $object, $arguments)
}

function Release-Com
{
    foreach ($object in $script:ComObjects)
    {
        try
        {
            [void] [Runtime.InteropServices.Marshal]::FinalReleaseComObject($object)
        }
        catch
        {
            # A double release is not worth failing a build over.
        }
    }

    $script:ComObjects = @()
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}

function Get-ActionType($database, [string] $action)
{
    $view = Invoke-Com $database 'OpenView' @("SELECT Type FROM CustomAction WHERE Action = '$action'")

    # [void] on Execute/Close: InvokeMember returns a value even for those, and an
    # unswallowed return joins this function's output stream - which turned the [int]
    # below into an Object[] and made -band fail with "no method op_BitwiseAnd".
    [void] (Invoke-Com $view 'Execute' $null)
    $record = Invoke-Com $view 'Fetch' $null
    [void] (Invoke-Com $view 'Close' $null)

    if ($null -eq $record)
    {
        return $null
    }

    return [int] (Get-ComProperty $record 'IntegerData' @(1))
}

try
{
    # 1 = msiOpenDatabaseModeTransact: changes are staged and only land on Commit.
    $installer = New-Com 'WindowsInstaller.Installer'
    $database = Invoke-Com $installer 'OpenDatabase' @($MsiPath, 1)

    $changed = 0

    foreach ($action in $Actions)
    {
        $type = Get-ActionType $database $action

        if ($null -eq $type)
        {
            # Loud, not skipped. If Velopack renames these actions a silent no-op would ship
            # an MSI that looks patched, and the service would go missing again with no signal.
            throw "Custom action '$action' is not in this MSI. Velopack's hook layout changed - re-check the CustomAction table before shipping."
        }

        if ($type -band $NoImpersonate)
        {
            Write-Host "$action is already no-impersonate (type $type); nothing to do."
            continue
        }

        $updated = $type -bor $NoImpersonate
        $view = Invoke-Com $database 'OpenView' @("UPDATE CustomAction SET Type = $updated WHERE Action = '$action'")
        [void] (Invoke-Com $view 'Execute' $null)
        [void] (Invoke-Com $view 'Close' $null)

        Write-Host "$action : $type -> $updated (added NoImpersonate)"
        $changed++
    }

    if ($changed -gt 0)
    {
        [void] (Invoke-Com $database 'Commit' $null)
    }
}
finally
{
    Release-Com
}

# Re-open from scratch and re-read rather than trusting the write: a Commit that silently
# did nothing looks identical to one that worked, and the whole point of this file is that
# a silent no-op is a class of bug nobody notices until a user reports a missing service.
try
{
    $verifier = New-Com 'WindowsInstaller.Installer'
    $database = Invoke-Com $verifier 'OpenDatabase' @($MsiPath, 0)

    foreach ($action in $Actions)
    {
        $type = Get-ActionType $database $action
        if ($null -eq $type -or -not ($type -band $NoImpersonate))
        {
            throw "$action is type '$type' after the update - the MSI was NOT patched."
        }

        Write-Host "verified: $action type $type"
    }
}
finally
{
    Release-Com
}
