// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

public class IPodFirmwarePartitionTests
{
    // ===== ReadUInt32LE - little-endian uint32 reader =====

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

    // ===== ScanForImageEntries - finds firmware image directory entries =====

    [Fact]
    public void Scan_finds_plain_osos_entry_with_parsed_fields()
    {
        // Use a 32 KB buffer so we can place the entry at offset 0x4204 - the actual
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
        // 50 osos entries, ask for max 10 - verify we stop early
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

    // ===== FindFirstPlausibleOsos - filters out garbage matches =====

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
            // rsrc with valid fields - not osos, must be skipped
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

    // ===== Test helper - write a 40-byte image directory entry =====

    private static void WriteImageDirectoryEntry(byte[] buf, int offset, string fourCharName, uint devOffset, uint length, uint vers)
    {
        if (fourCharName.Length != 4) throw new ArgumentException("Image name must be exactly 4 chars", nameof(fourCharName));

        // Name (4 bytes ASCII)
        for (int i = 0; i < 4; i++)
        {
            buf[offset + i] = (byte)fourCharName[i];
        }

        // id (4 bytes) - leave 0
        // devOffset at +0x08
        WriteUInt32LE(buf, offset + 0x08, devOffset);
        // length at +0x0C
        WriteUInt32LE(buf, offset + 0x0C, length);
        // addr at +0x10, entryOffset at +0x14, checksum at +0x18 - leave 0
        // vers at +0x1C
        WriteUInt32LE(buf, offset + 0x1C, vers);
        // loadAddr at +0x20 - leave 0
    }

    private static void WriteUInt32LE(byte[] dest, int offset, uint value)
    {
        dest[offset]     = (byte)(value & 0xFF);
        dest[offset + 1] = (byte)((value >> 8) & 0xFF);
        dest[offset + 2] = (byte)((value >> 16) & 0xFF);
        dest[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    // ===== ScanSysCfg - reads serial + model number from the firmware SysInfo record =====

    private static byte[] Ascii(string s) => System.Text.Encoding.ASCII.GetBytes(s);

    // Builds the firmware SysInfo record as it actually sits on a 5.5G: the board-name string
    // "iPod M<nn>", then the null-terminated serial a few bytes later, with the model number
    // in the same record. Mirrors the real BriPod dump (serial 8L645KA1V9M, model MA446).
    private static byte[] BuildSysInfoRecord(string board, string serial, string modelNumber)
    {
        var buf = new List<byte>();
        buf.AddRange(Ascii(board)); buf.Add(0);        // "iPod M25\0"
        buf.AddRange(new byte[8]);                     // padding
        buf.AddRange(Ascii(serial)); buf.Add(0);       // serial, null-terminated
        buf.AddRange(new byte[16]);                    // FireWire GUID region etc.
        buf.AddRange(Ascii(modelNumber)); buf.Add(0);  // "MA446\0"
        return buf.ToArray();
    }

    [Fact]
    public void ScanSysCfg_reads_board_anchored_serial_and_model_number()
    {
        var record = BuildSysInfoRecord("iPod M25", "8L645KA1V9M", "MA446");
        var buf = new byte[record.Length + 200];
        Array.Copy(record, 0, buf, 60, record.Length);   // embed in a larger buffer

        var (serial, model) = IPodFirmwarePartition.ScanSysCfg(buf, new System.Text.StringBuilder());
        Assert.Equal("8L645KA1V9M", serial);
        Assert.Equal("MA446", model);
    }

    [Fact]
    public void ScanSysCfg_ignores_a_serial_shaped_run_with_no_board_name()
    {
        // A valid-looking 11-char run without an "iPod M" board name before it is firmware
        // noise, not the serial - the whole reason we anchor (32k such runs in a real dump).
        var buf = new byte[600];
        Array.Copy(Ascii("8L645KA1V9M"), 0, buf, 300, 11);

        var (serial, _) = IPodFirmwarePartition.ScanSysCfg(buf, new System.Text.StringBuilder());
        Assert.Null(serial);
    }

    [Fact]
    public void ScanSysCfg_returns_nulls_on_empty_firmware()
    {
        var (serial, model) = IPodFirmwarePartition.ScanSysCfg(new byte[2000], new System.Text.StringBuilder());
        Assert.Null(serial);
        Assert.Null(model);
    }

    [Fact]
    public void ScanSysCfg_reads_real_5_5G_firmware_bytes()
    {
        // 160 bytes lifted verbatim from a real iPod Video 5.5G firmware dump (BriPod) - the
        // SysInfo record: board name "iPod M25", serial "8L645KA1V9M", FireWire GUID, "MA446".
        // Proves the parser against actual hardware bytes, not just synthetic ones.
        var record = Convert.FromHexString("f800000069506f64204d32350000000000000000384c3634354b413156394d0000000000000000000000000000000000000000006653d31500270a0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000b6f3f9ff11000b00425531313141202000000100020000004d4134343600000000000000");

        var (serial, model) = IPodFirmwarePartition.ScanSysCfg(record, new System.Text.StringBuilder());
        Assert.Equal("8L645KA1V9M", serial);
        Assert.Equal("MA446", model);
    }

    [Fact]
    public void ScanSysCfg_falls_back_to_SCfg_dict_for_NOR_gens()
    {
        // The freemyipod 'SCfg' dictionary (Nano 3G / Classic NOR flash): 24-byte header
        // (magic 'SCfg' LE = "gfCS", num_entries @0x14), then 20-byte entries (4-byte LE tag +
        // 16 data). No board name, so the HDD parser misses it and the SCfg fallback takes over.
        var buf = new List<byte>();
        buf.AddRange(new byte[] { 0x67, 0x66, 0x43, 0x53 });          // 'SCfg' little-endian
        buf.AddRange(new byte[16]);                                    // size/unk/version/unk
        buf.AddRange(BitConverter.GetBytes(2));                        // num_entries @0x14
        void Entry(string tag, string val)
        {
            var t = System.Text.Encoding.ASCII.GetBytes(tag);
            buf.Add(t[3]); buf.Add(t[2]); buf.Add(t[1]); buf.Add(t[0]);   // FourCC → little-endian
            var d = new byte[16];
            Array.Copy(Ascii(val), d, val.Length);
            buf.AddRange(d);
        }
        Entry("SrNm", "YM8290ABQ2X");   // arbitrary serial value in the dict
        Entry("ModN", "MB147");         // Classic 6G Black 80GB model number

        var (serial, model) = IPodFirmwarePartition.ScanSysCfg(buf.ToArray(), new System.Text.StringBuilder());
        Assert.Equal("YM8290ABQ2X", serial);
        Assert.Equal("MB147", model);
    }
}
