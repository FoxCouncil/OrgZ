// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Buffers.Binary;
using System.Text;

namespace OrgZ.Services.Sharing;

/// <summary>
/// Minimal DNS wire format for mDNS/DNS-SD (RFC 1035 records over RFC 6762 multicast).
/// Just the subset library sharing needs: parse an incoming query's questions, and build
/// a response carrying PTR + SRV + TXT + A. Hand-rolled rather than pulling in a stack -
/// the encoding is small, and keeping it pure makes it exhaustively testable.
/// </summary>
public static class MdnsWire
{
    public const string MulticastAddress = "224.0.0.251";
    public const int Port = 5353;

    /// <summary>DNS-SD service type OrgZ libraries advertise under.</summary>
    public const string ServiceType = "_orgz._tcp.local";

    public const ushort TypeA = 1;
    public const ushort TypePtr = 12;
    public const ushort TypeTxt = 16;
    public const ushort TypeSrv = 33;
    public const ushort TypeAny = 255;

    private const ushort ClassIn = 1;
    private const ushort FlagResponseAuthoritative = 0x8400;
    private const ushort CacheFlush = 0x8000;

    public sealed record Question(string Name, ushort Type);

    /// <summary>One discovered/advertised service instance.</summary>
    public sealed record ServiceInstance(string InstanceName, string HostName, ushort Port, string[] TxtRecords, string? Address = null);

    // ── Names ────────────────────────────────────────────────

    /// <summary>Encodes a dotted name as length-prefixed labels ending in a zero byte.</summary>
    internal static void WriteName(List<byte> output, string name)
    {
        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length > 63)
            {
                throw new ArgumentException($"DNS label '{label}' exceeds 63 bytes.", nameof(name));
            }

            output.Add((byte)bytes.Length);
            output.AddRange(bytes);
        }

        output.Add(0);
    }

    /// <summary>
    /// Reads a name at <paramref name="offset"/>, following compression pointers.
    /// Returns false on a malformed name (bad pointer, loop, truncation) - hostile
    /// packets must not hang or throw us out of the receive loop.
    /// </summary>
    internal static bool TryReadName(ReadOnlySpan<byte> packet, ref int offset, out string name)
    {
        name = string.Empty;
        var labels = new List<string>();
        var hops = 0;
        var cursor = offset;
        var jumped = false;

        while (true)
        {
            if (cursor < 0 || cursor >= packet.Length)
            {
                return false;
            }

            var len = packet[cursor];

            if (len == 0)
            {
                cursor++;
                break;
            }

            if ((len & 0xC0) == 0xC0)
            {
                if (cursor + 1 >= packet.Length || ++hops > 16)
                {
                    return false;   // truncated pointer, or a pointer loop
                }

                var target = ((len & 0x3F) << 8) | packet[cursor + 1];
                if (!jumped)
                {
                    offset = cursor + 2;
                    jumped = true;
                }

                cursor = target;
                continue;
            }

            cursor++;
            if (cursor + len > packet.Length)
            {
                return false;
            }

            labels.Add(Encoding.UTF8.GetString(packet.Slice(cursor, len)));
            cursor += len;
        }

        if (!jumped)
        {
            offset = cursor;
        }

        name = string.Join('.', labels);
        return true;
    }

    // ── Queries ──────────────────────────────────────────────

    /// <summary>Parses the questions out of a packet. Empty list for anything malformed.</summary>
    public static List<Question> ReadQuestions(ReadOnlySpan<byte> packet)
    {
        var questions = new List<Question>();
        if (packet.Length < 12)
        {
            return questions;
        }

        // Responses aren't queries - ignore them here.
        var flags = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        if ((flags & 0x8000) != 0)
        {
            return questions;
        }

        var count = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
        var offset = 12;

        for (var i = 0; i < count; i++)
        {
            if (!TryReadName(packet, ref offset, out var name) || offset + 4 > packet.Length)
            {
                return questions;   // keep whatever parsed cleanly
            }

            var type = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
            offset += 4;            // type + class
            questions.Add(new Question(name, type));
        }

        return questions;
    }

    /// <summary>Builds a standard DNS-SD browse query for <see cref="ServiceType"/>.</summary>
    public static byte[] BuildQuery(string serviceType = ServiceType)
    {
        var packet = new List<byte>(64);
        packet.AddRange([0, 0]);                       // id 0, per mDNS
        packet.AddRange([0, 0]);                       // flags: standard query
        packet.AddRange([0, 1]);                       // 1 question
        packet.AddRange([0, 0, 0, 0, 0, 0]);           // no answer/authority/additional
        WriteName(packet, serviceType);
        packet.AddRange([(byte)(TypePtr >> 8), (byte)TypePtr]);
        packet.AddRange([(byte)(ClassIn >> 8), (byte)ClassIn]);
        return [.. packet];
    }

    // ── Responses ────────────────────────────────────────────

    /// <summary>The packet's transaction id - zero for anything too short to carry one.</summary>
    public static ushort ReadId(ReadOnlySpan<byte> packet)
        => packet.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(packet) : (ushort)0;

    /// <summary>
    /// Builds the announcement for one service instance: PTR (service type → instance),
    /// SRV (instance → host:port), TXT (metadata), and A (host → IPv4) when known.
    /// <paramref name="id"/> and <paramref name="cacheFlush"/> exist for RFC 6762 §6.7
    /// legacy unicast replies, which must echo the query's id and must NOT set the
    /// cache-flush bit (a legacy client reads that bit as part of the class).
    /// </summary>
    public static byte[] BuildResponse(ServiceInstance instance, string serviceType = ServiceType, uint ttlSeconds = 120, ushort id = 0, bool cacheFlush = true)
    {
        var records = new List<byte>();
        var answers = 0;

        var instanceFqdn = $"{instance.InstanceName}.{serviceType}";

        // PTR: service type → this instance
        WriteRecord(records, serviceType, TypePtr, ttlSeconds, cacheFlush, data =>
        {
            WriteName(data, instanceFqdn);
        });
        answers++;

        // SRV: instance → host:port
        WriteRecord(records, instanceFqdn, TypeSrv, ttlSeconds, cacheFlush, data =>
        {
            data.AddRange([0, 0]);                                                     // priority
            data.AddRange([0, 0]);                                                     // weight
            data.AddRange([(byte)(instance.Port >> 8), (byte)instance.Port]);
            WriteName(data, instance.HostName);
        });
        answers++;

        // TXT: key=value strings, each length-prefixed
        WriteRecord(records, instanceFqdn, TypeTxt, ttlSeconds, cacheFlush, data =>
        {
            if (instance.TxtRecords.Length == 0)
            {
                data.Add(0);
                return;
            }

            foreach (var entry in instance.TxtRecords)
            {
                var bytes = Encoding.UTF8.GetBytes(entry);
                data.Add((byte)Math.Min(bytes.Length, 255));
                data.AddRange(bytes.Take(255));
            }
        });
        answers++;

        if (instance.Address is { } address && System.Net.IPAddress.TryParse(address, out var ip)
            && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            WriteRecord(records, instance.HostName, TypeA, ttlSeconds, cacheFlush, data => data.AddRange(ip.GetAddressBytes()));
            answers++;
        }

        var packet = new List<byte>(records.Count + 12);
        packet.AddRange([(byte)(id >> 8), (byte)id]);
        packet.AddRange([(byte)(FlagResponseAuthoritative >> 8), unchecked((byte)FlagResponseAuthoritative)]);
        packet.AddRange([0, 0]);                                   // no questions
        packet.AddRange([(byte)(answers >> 8), (byte)answers]);
        packet.AddRange([0, 0, 0, 0]);                             // no authority/additional
        packet.AddRange(records);
        return [.. packet];
    }

    private static void WriteRecord(List<byte> output, string name, ushort type, uint ttl, bool cacheFlush, Action<List<byte>> writeData)
    {
        WriteName(output, name);
        output.AddRange([(byte)(type >> 8), (byte)type]);

        var cls = cacheFlush ? (ushort)(ClassIn | CacheFlush) : ClassIn;
        output.AddRange([(byte)(cls >> 8), (byte)cls]);

        Span<byte> ttlBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(ttlBytes, ttl);
        output.AddRange(ttlBytes.ToArray());

        var data = new List<byte>();
        writeData(data);

        // RDLENGTH is 16 bits. Nothing OrgZ advertises comes close, but writing the low
        // two bytes of an oversized length would emit a record claiming a size it doesn't
        // have - corruption that reads as a malformed packet on every client. Fail loudly
        // at the composing end instead.
        if (data.Count > ushort.MaxValue)
        {
            throw new InvalidOperationException($"mDNS record data is {data.Count} bytes; RDLENGTH tops out at {ushort.MaxValue}.");
        }

        output.AddRange([(byte)(data.Count >> 8), (byte)data.Count]);
        output.AddRange(data);
    }

    /// <summary>
    /// Reads service instances out of a response packet (PTR/SRV/TXT/A answers merged
    /// by instance name). Malformed packets yield whatever parsed cleanly.
    /// </summary>
    public static List<ServiceInstance> ReadResponse(ReadOnlySpan<byte> packet, string serviceType = ServiceType)
    {
        var result = new List<ServiceInstance>();
        if (packet.Length < 12)
        {
            return result;
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        if ((flags & 0x8000) == 0)
        {
            return result;   // a query, not a response
        }

        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
        var answerCount = BinaryPrimitives.ReadUInt16BigEndian(packet[6..])
                        + BinaryPrimitives.ReadUInt16BigEndian(packet[8..])
                        + BinaryPrimitives.ReadUInt16BigEndian(packet[10..]);
        var offset = 12;

        for (var i = 0; i < questionCount; i++)
        {
            if (!TryReadName(packet, ref offset, out _) || offset + 4 > packet.Length)
            {
                return result;
            }
            offset += 4;
        }

        var instances = new Dictionary<string, (string? Host, ushort Port, List<string> Txt, string? Addr)>(StringComparer.OrdinalIgnoreCase);
        var addresses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < answerCount; i++)
        {
            if (!TryReadName(packet, ref offset, out var name) || offset + 10 > packet.Length)
            {
                break;
            }

            var type = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
            var dataLen = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 8)..]);
            offset += 10;

            if (offset + dataLen > packet.Length)
            {
                break;
            }

            var dataStart = offset;

            switch (type)
            {
                case TypeSrv when dataLen >= 6:
                {
                    var port = BinaryPrimitives.ReadUInt16BigEndian(packet[(dataStart + 4)..]);
                    var nameOffset = dataStart + 6;
                    TryReadName(packet, ref nameOffset, out var target);
                    var entry = instances.GetValueOrDefault(name);
                    instances[name] = (target, port, entry.Txt ?? [], entry.Addr);
                    break;
                }

                case TypeTxt:
                {
                    var txt = new List<string>();
                    var cursor = dataStart;
                    while (cursor < dataStart + dataLen && cursor < packet.Length)
                    {
                        var len = packet[cursor++];
                        if (len == 0 || cursor + len > packet.Length)
                        {
                            break;
                        }
                        txt.Add(Encoding.UTF8.GetString(packet.Slice(cursor, len)));
                        cursor += len;
                    }

                    var entry = instances.GetValueOrDefault(name);
                    instances[name] = (entry.Host, entry.Port, txt, entry.Addr);
                    break;
                }

                case TypeA when dataLen == 4:
                {
                    addresses[name] = new System.Net.IPAddress(packet.Slice(dataStart, 4).ToArray()).ToString();
                    break;
                }
            }

            offset += dataLen;
        }

        foreach (var (fqdn, entry) in instances)
        {
            if (entry.Host is null)
            {
                continue;
            }

            var suffix = "." + serviceType;
            var instanceName = fqdn.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? fqdn[..^suffix.Length]
                : fqdn;

            result.Add(new ServiceInstance(instanceName, entry.Host, entry.Port, [.. entry.Txt], addresses.GetValueOrDefault(entry.Host)));
        }

        return result;
    }
}
