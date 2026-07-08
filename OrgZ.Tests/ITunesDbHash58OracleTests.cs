// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Diagnostics;
using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// Slice C, hash58 tier: cross-checks OrgZ's <see cref="ITunesDbHash58"/> against an INDEPENDENT
/// implementation of the algorithm (<c>OrgZ.Tests/oracle/hash58_independent.py</c>) that generates the
/// AES S-box from GF(2^8) first principles and uses Python's stdlib HMAC-SHA1 - sharing nothing with
/// OrgZ but the documented <c>Fixed[]</c> constant and the algorithm itself. Agreement rules out a
/// porting bug in the y-derivation, the zeroed regions or the HMAC construction - the parts OrgZ's own
/// self-consistency tests can't catch. (The canonical libgpod-binary cross-check is the further
/// confirmation noted in the roadmap.)
/// </summary>
public class ITunesDbHash58OracleTests
{
    // The FireWire GUID and the reference hash the independent oracle produced for the committed plain
    // fixture. Reproduce with: python OrgZ.Tests/oracle/hash58_independent.py \
    //   OrgZ.Tests/Fixtures/itunesdb-write/orgz-emitted.iTunesDB 000A27001597690A
    private const string Guid = "000A27001597690A";
    private const string IndependentReference = "a986963f9d5808bad66a167a48460cc723878ccb";

    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "itunesdb-write", "orgz-emitted.iTunesDB");

    private static byte[] OrgZHash()
    {
        var db = File.ReadAllBytes(FixturePath);
        ITunesDbHash58.Apply(db, Guid);
        return db[0x58..0x6C];
    }

    [Fact]
    public void OrgZ_hash58_matches_the_independent_reference()
    {
        Assert.Equal(IndependentReference, Convert.ToHexString(OrgZHash()).ToLowerInvariant());
    }

    [Fact]
    public void Independent_oracle_live_agrees_with_OrgZ()
    {
        var python = FindPython();
        if (python is null)
        {
            // No Python on this host - the golden above still pins OrgZ to the independent value; run
            // the script by hand (see the const comment) to re-derive it from scratch.
            return;
        }

        var script = Path.Combine(AppContext.BaseDirectory, "oracle", "hash58_independent.py");
        var psi = new ProcessStartInfo(python, $"\"{script}\" \"{FixturePath}\" {Guid}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        Assert.True(proc.ExitCode == 0, $"independent oracle failed ({proc.ExitCode}): {stderr}");
        var reported = stdout.Trim();
        Assert.Equal($"independent_hash58={Convert.ToHexString(OrgZHash()).ToLowerInvariant()}", reported);
    }

    private static string? FindPython()
    {
        foreach (var name in new[] { "python3", "python" })
        {
            try
            {
                var psi = new ProcessStartInfo(name, "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                using var p = Process.Start(psi);
                if (p is null)
                {
                    continue;
                }
                p.WaitForExit(5000);
                if (p.HasExited && p.ExitCode == 0)
                {
                    return name;
                }
            }
            catch
            {
                // not on PATH - try the next name
            }
        }
        return null;
    }
}
