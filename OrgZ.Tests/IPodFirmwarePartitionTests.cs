// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

public class IPodFirmwarePartitionTests
{
    // ===== ReadUInt32LE — little-endian uint32 reader =====

    [Fact]
    public void ReadUInt32LE_reads_bytes_in_little_endian_order()
    {
        var bytes = new byte[] { 0x12, 0x34, 0x56, 0x78 };
        Assert.Equal(0x78563412u, IPodFirmwarePartition.ReadUInt32LE(bytes, 0));
    }

    [Fact]
    public void ReadUInt32LE_reads_with_offset()
    {
        var bytes = new byte[] { 0xFF, 0xFF, 0x12, 0x34, 0x56, 0x78, 0xFF };
        Assert.Equal(0x78563412u, IPodFirmwarePartition.ReadUInt32LE(bytes, 2));
    }

    [Fact]
    public void ReadUInt32LE_returns_zero_when_offset_overruns_buffer()
    {
        var bytes = new byte[] { 0x01, 0x02, 0x03 };
        Assert.Equal(0u, IPodFirmwarePartition.ReadUInt32LE(bytes, 0));   // not enough bytes
        Assert.Equal(0u, IPodFirmwarePartition.ReadUInt32LE(bytes, 100)); // way past end
    }

    [Fact]
    public void ReadUInt32LE_handles_high_bit_set()
    {
        var bytes = new byte[] { 0x00, 0x00, 0x00, 0x80 };
        Assert.Equal(0x80000000u, IPodFirmwarePartition.ReadUInt32LE(bytes, 0));
    }

    // ===== ScanForImageEntries — finds firmware image directory entries =====

    [Fact]
    public void Scan_finds_plain_osos_entry_with_parsed_fields()
    {
        // Use a 32 KB buffer so we can place the entry at offset 0x4204 — the actual
        // location Apple uses on 5.5G iPods (firmware directory at +0x4204 from
        // partition start, not at the +0x200 some older docs claim).
        var buf = new byte[32 * 1024];
        const int entryOffset = 0x4204;
        WriteImageDirectoryEntry(buf, entryOffset, "osos", devOffset: 0x10000, length: 0x800000, vers: 0x02308000);

        var matches = IPodFirmwarePartition.ScanForImageEntries(buf);
        Assert.Single(matches);
        var m = matches[0];
        Assert.Equal(entryOffset, m.Offset);
        Assert.Equal("osos", m.Display);
        Assert.Equal("osos", m.Canonical);
        Assert.Equal(0x10000u, m.DevOffset);
        Assert.Equal(0x800000u, m.Length);
        Assert.Equal(0x02308000u, m.Vers);
    }

    [Fact]
    public void Scan_finds_word_swapped_soso_and_normalizes_canonical_to_osos()
    {
        // 5G/5.5G/6G iPods store image names byte-swapped within each 16-bit word.
        // "osos" appears on disk as 0x73 0x6F 0x73 0x6F ("soso").
        var buf = new byte[8 * 1024];
        WriteImageDirectoryEntry(buf, 0x100, "soso", devOffset: 0x20000, length: 0x900000, vers: 0x0000B012);

        var matches = IPodFirmwarePartition.ScanForImageEntries(buf);
        Assert.Single(matches);
        Assert.Equal("soso", matches[0].Display);
        Assert.Equal("osos", matches[0].Canonical);   // normalized for downstream use
        Assert.Equal(0x0000B012u, matches[0].Vers);
    }

    [Theory]
    [InlineData("osos", "osos")]   // plain
    [InlineData("aupd", "aupd")]   // plain
    [InlineData("rsrc", "rsrc")]   // plain
    [InlineData("osbk", "osbk")]   // plain
    [InlineData("hibe", "hibe")]   // plain
    [InlineData("fhdr", "fhdr")]   // plain
    [InlineData("diag", "diag")]   // plain
    [InlineData("soso", "osos")]   // swapped
    [InlineData("dpua", "aupd")]   // swapped
    [InlineData("crsr", "rsrc")]   // swapped
    [InlineData("kbso", "osbk")]   // swapped
    [InlineData("ebih", "hibe")]   // swapped
    [InlineData("rdhf", "fhdr")]   // swapped
    [InlineData("gaid", "diag")]   // swapped
    public void Scan_recognizes_all_known_image_names(string display, string canonical)
    {
        var buf = new byte[256];
        WriteImageDirectoryEntry(buf, 0x40, display, devOffset: 0x100, length: 0x1000, vers: 0x12345678);

        var matches = IPodFirmwarePartition.ScanForImageEntries(buf);
        Assert.Single(matches);
        Assert.Equal(display, matches[0].Display);
        Assert.Equal(canonical, matches[0].Canonical);
    }

    [Fact]
    public void Scan_finds_multiple_entries_in_scan_order()
    {
        var buf = new byte[1024];
        WriteImageDirectoryEntry(buf, 0x100, "osos", devOffset: 0x10000, length: 0x800000, vers: 0x0000B012);
        WriteImageDirectoryEntry(buf, 0x200, "rsrc", devOffset: 0x20000, length: 0x100000, vers: 0x0000B013);
        WriteImageDirectoryEntry(buf, 0x300, "aupd", devOffset: 0x30000, length: 0x080000, vers: 0x0000B014);

        var matches = IPodFirmwarePartition.ScanForImageEntries(buf);
        Assert.Equal(3, matches.Count);
        Assert.Equal("osos", matches[0].Canonical);
        Assert.Equal("rsrc", matches[1].Canonical);
        Assert.Equal("aupd", matches[2].Canonical);
    }

    [Fact]
    public void Scan_respects_max_matches_limit()
    {
        // 50 osos entries, ask for max 10 — verify we stop early
        var buf = new byte[100 * 1024];
        for (int i = 0; i < 50; i++)
        {
            WriteImageDirectoryEntry(buf, 0x100 + i * 100, "osos", devOffset: 1, length: 1, vers: 1);
        }

        var matches = IPodFirmwarePartition.ScanForImageEntries(buf, maxMatches: 10);
        Assert.Equal(10, matches.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x00, 0x01, 0x02 })]   // shorter than 40-byte entry
    public void Scan_returns_empty_for_too_small_or_null_buffer(byte[]? buf)
    {
        var matches = IPodFirmwarePartition.ScanForImageEntries(buf!);
        Assert.Empty(matches);
    }

    [Fact]
    public void Scan_returns_empty_when_no_known_names_present()
    {
        var buf = new byte[1024];
        // Fill with zeros (default); scanner should find nothing
        var matches = IPodFirmwarePartition.ScanForImageEntries(buf);
        Assert.Empty(matches);
    }

    // ===== FindFirstPlausibleOsos — filters out garbage matches =====

    [Fact]
    public void FindFirstPlausibleOsos_returns_first_real_osos_skipping_garbage()
    {
        var matches = new List<IPodFirmwarePartition.ImageEntry>
        {
            // Garbage: zero devOffset and length (false positive in encrypted payload)
            new(0x100, "osos", "osos", DevOffset: 0,         Length: 0,        Vers: 0),
            // Garbage: huge length way over partition size
            new(0x200, "soso", "osos", DevOffset: 0x10000,   Length: 0xFFFFFFFFu, Vers: 0xDEADBEEF),
            // Valid: small dev/length within partition
            new(0x300, "osos", "osos", DevOffset: 0x10000,   Length: 0x800000, Vers: 0x0000B012),
            // Wouldn't be reached
            new(0x400, "osos", "osos", DevOffset: 0x10000,   Length: 0x800000, Vers: 0xCAFEBABE),
        };

        var result = IPodFirmwarePartition.FindFirstPlausibleOsos(matches, partitionBytes: 0x2_800_000);
        Assert.NotNull(result);
        Assert.Equal(0x300, result!.Offset);
        Assert.Equal(0x0000B012u, result.Vers);
    }

    [Fact]
    public void FindFirstPlausibleOsos_skips_non_osos_entries()
    {
        var matches = new List<IPodFirmwarePartition.ImageEntry>
        {
            // rsrc with valid fields — not osos, must be skipped
            new(0x100, "rsrc", "rsrc", DevOffset: 0x10000, Length: 0x100000, Vers: 0x12345678),
            // osos with valid fields
            new(0x200, "osos", "osos", DevOffset: 0x20000, Length: 0x800000, Vers: 0x0000B012),
        };

        var result = IPodFirmwarePartition.FindFirstPlausibleOsos(matches, partitionBytes: 0x2_800_000);
        Assert.NotNull(result);
        Assert.Equal(0x200, result!.Offset);
    }

    [Fact]
    public void FindFirstPlausibleOsos_returns_null_when_no_valid_match()
    {
        var matches = new List<IPodFirmwarePartition.ImageEntry>
        {
            new(0x100, "osos", "osos", DevOffset: 0,         Length: 0,           Vers: 0),
            new(0x200, "osos", "osos", DevOffset: 0xFFFFFFF, Length: 0xFFFFFFFFu, Vers: 0),
        };

        Assert.Null(IPodFirmwarePartition.FindFirstPlausibleOsos(matches, partitionBytes: 0x2_800_000));
    }

    [Fact]
    public void FindFirstPlausibleOsos_returns_null_for_empty_matches()
    {
        Assert.Null(IPodFirmwarePartition.FindFirstPlausibleOsos([], partitionBytes: 0x2_800_000));
    }

    // ===== Test helper — write a 40-byte image directory entry =====

    private static void WriteImageDirectoryEntry(byte[] buf, int offset, string fourCharName, uint devOffset, uint length, uint vers)
    {
        if (fourCharName.Length != 4) throw new ArgumentException("Image name must be exactly 4 chars", nameof(fourCharName));

        // Name (4 bytes ASCII)
        for (int i = 0; i < 4; i++)
        {
            buf[offset + i] = (byte)fourCharName[i];
        }

        // id (4 bytes) — leave 0
        // devOffset at +0x08
        WriteUInt32LE(buf, offset + 0x08, devOffset);
        // length at +0x0C
        WriteUInt32LE(buf, offset + 0x0C, length);
        // addr at +0x10, entryOffset at +0x14, checksum at +0x18 — leave 0
        // vers at +0x1C
        WriteUInt32LE(buf, offset + 0x1C, vers);
        // loadAddr at +0x20 — leave 0
    }

    private static void WriteUInt32LE(byte[] dest, int offset, uint value)
    {
        dest[offset]     = (byte)(value & 0xFF);
        dest[offset + 1] = (byte)((value >> 8) & 0xFF);
        dest[offset + 2] = (byte)((value >> 16) & 0xFF);
        dest[offset + 3] = (byte)((value >> 24) & 0xFF);
    }
}
