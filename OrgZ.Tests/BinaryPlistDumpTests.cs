// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services.AudioOutput.AirPlay;

namespace OrgZ.Tests;

/// <summary>
/// Writes a representative SETUP plist to disk so an EXTERNAL parser can check it.
///
/// The round-trip tests elsewhere only prove our writer agrees with our reader, which is
/// worthless if both share a misreading of the format - a receiver that closes the
/// connection is the only other feedback available, and it doesn't say why.
/// </summary>
public class BinaryPlistDumpTests
{
    [SkippableFact]
    public void Dump_setup_plist_for_external_validation()
    {
        var path = Environment.GetEnvironmentVariable("ORGZ_PLIST_DUMP");
        Skip.If(string.IsNullOrEmpty(path), "Set ORGZ_PLIST_DUMP to write the plist out.");

        var body = BinaryPlist.Write(new Dictionary<string, object?>
        {
            ["deviceID"] = "AA:BB:CC:DD:EE:FF",
            ["macAddress"] = "AA:BB:CC:DD:EE:FF",
            ["sessionUUID"] = "6A04F228-1AF2-4E7E-8829-84BFC0902A1F",
            ["timingPort"] = 61530L,
            ["timingProtocol"] = "NTP",
            ["isMultiSelectAirPlay"] = true,
            ["groupContainsGroupLeader"] = false,
            ["senderSupportsRelay"] = false,
            ["statsCollectionEnabled"] = false,
            ["model"] = "iPhone14,3",
            ["name"] = "OrgZ",
            ["osName"] = "iPhone OS",
            ["osVersion"] = "16.5",
            ["osBuildVersion"] = "20F66",
            ["sourceVersion"] = "690.7.1",
        });

        File.WriteAllBytes(path!, body);

        var streams = BinaryPlist.Write(new Dictionary<string, object?>
        {
            ["streams"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = 96L,
                    ["audioFormat"] = 0x800L,
                    ["audioMode"] = "default",
                    ["ct"] = 1L,
                    ["sr"] = 44100L,
                    ["spf"] = 352L,
                    ["controlPort"] = 61529L,
                    ["streamConnectionID"] = 1558684391L,
                    ["supportsDynamicStreamID"] = false,
                    ["shk"] = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(),
                    ["isMedia"] = true,
                    ["latencyMin"] = 11025L,
                    ["latencyMax"] = 88200L,
                },
            },
        });

        File.WriteAllBytes(path! + ".streams", streams);
    }
}
