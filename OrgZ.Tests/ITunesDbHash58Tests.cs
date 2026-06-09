// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// Structural tests for the hash58 checksum (port of libgpod itdb_hash58.c).
/// A true known-answer vector requires a real device's FireWireGuid + DB, so the
/// authoritative check is a boot-test on a Classic; these lock the mechanics:
/// scheme field set, 20-byte hash placed at 0x58, deterministic, GUID-sensitive,
/// and the zeroed-then-restored fields preserved.
/// </summary>
public class ITunesDbHash58Tests
{
    private const string Guid = "000A27001597690A";

    private static byte[] MakeDb()
    {
        var db = new byte[0x100];
        db[0] = (byte)'m'; db[1] = (byte)'h'; db[2] = (byte)'b'; db[3] = (byte)'d';
        // give db_id (0x18) and unk_0x32 (0x32) distinctive non-zero content
        for (int i = 0; i < 8; i++) db[0x18 + i] = (byte)(0xA0 + i);
        for (int i = 0; i < 20; i++) db[0x32 + i] = (byte)(0x10 + i);
        return db;
    }

    [Fact]
    public void Sets_scheme_writes_hash_and_preserves_zeroed_fields()
    {
        var db = MakeDb();
        var before18 = db[0x18..0x20];
        var before32 = db[0x32..0x46];

        ITunesDbHash58.Apply(db, Guid);

        Assert.Equal(1, db[0x30]);                       // hashing_scheme = HASH58
        Assert.Equal(0, db[0x31]);
        Assert.Contains(db[0x58..0x6C], b => b != 0);    // 20-byte hash present
        Assert.Equal(before18, db[0x18..0x20]);          // db_id restored
        Assert.Equal(before32, db[0x32..0x46]);          // unk_0x32 restored
    }

    [Fact]
    public void Is_deterministic()
    {
        var a = MakeDb(); ITunesDbHash58.Apply(a, Guid);
        var b = MakeDb(); ITunesDbHash58.Apply(b, Guid);
        Assert.Equal(a[0x58..0x6C], b[0x58..0x6C]);
    }

    [Fact]
    public void Hash_depends_on_firewire_guid()
    {
        var a = MakeDb(); ITunesDbHash58.Apply(a, "000A27001597690A");
        var b = MakeDb(); ITunesDbHash58.Apply(b, "000A2700DEADBEEF");
        Assert.NotEqual(a[0x58..0x6C], b[0x58..0x6C]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00A")]
    public void Rejects_missing_or_short_guid(string? guid)
    {
        Assert.Throws<InvalidOperationException>(() => ITunesDbHash58.Apply(MakeDb(), guid));
    }
}
