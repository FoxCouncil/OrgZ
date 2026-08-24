// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services;
using OrgZ.Services.AudioOutput.AirPlay;
using SkiaSharp;

namespace OrgZ.AirPlayLab;

/// <summary>
/// Command-line lab for the AirPlay 2 stack against a REAL receiver. Everything here goes
/// through the same classes the app ships (<see cref="AirPlay2Session"/>,
/// <see cref="AirPlay2Pairing"/>, <see cref="RtspClient"/>) - perfecting the tool IS
/// perfecting OrgZ, there is nothing to port afterwards except deleting scaffolding.
///
/// Commands, in the order a bring-up runs them:
///   info   - plaintext GET /info, no pairing. Prints features/statusFlags decoded.
///   pair   - transient pair-setup only, then proves the HAP-encrypted channel by
///            fetching /info AGAIN through it. The crypto check without any audio.
///   tone   - full session: pair, SETUP, stream a sine tone with metadata. The ears check.
///   meta   - full session streaming silence through two track changes, held past the
///            ~30s event-channel point. The display/lifecycle check.
///   hold   - opens PAUSED holding silence the way selecting a speaker does, seeds a track
///            mid-hold, then releases as a playing client. The hold-shape check.
///
/// Passwords: --password wins, then ORGZ_AIRPLAY_PASSWORD, then the password the app
/// already stored for this receiver (settings.json DPAPI blob, same user). --no-password
/// forces the bare transient PIN, which a receiver flying statusFlags 0x80 will refuse -
/// running exactly that refusal on purpose is what the flag is for.
/// </summary>
internal static class Program
{
    private const int SampleRate = 44100;
    private const int RtspPort = 7000;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine("usage: airplaylab <info|pair|tone|meta|hold> [host] [--password <p>] [--no-password] [--seconds <n>] [--freq <hz>] [--volume <0..1>]");
            Console.WriteLine("       host may be omitted when ORGZ_LAB_HOST names a receiver");
            return args.Length == 0 ? 1 : 0;
        }

        Logging.Initialize();

        var command = args[0].ToLowerInvariant();
        // No hard-coded default: whose receiver it is belongs in the environment, not the repo.
        var host = args.Length > 1 && !args[1].StartsWith('-')
            ? args[1]
            : Environment.GetEnvironmentVariable("ORGZ_LAB_HOST");

        if (string.IsNullOrWhiteSpace(host))
        {
            Console.Error.WriteLine("no receiver given: pass a host, or set ORGZ_LAB_HOST");
            return 1;
        }

        string? explicitPassword = null;
        var noPassword = false;
        var seconds = command == "meta" ? 40 : 5;
        var freq = 440.0;
        var volume = 0.35f;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--password" when i + 1 < args.Length:
                {
                    explicitPassword = args[++i];
                }
                break;

                case "--no-password":
                {
                    noPassword = true;
                }
                break;

                case "--seconds" when i + 1 < args.Length:
                {
                    seconds = int.Parse(args[++i]);
                }
                break;

                case "--freq" when i + 1 < args.Length:
                {
                    freq = double.Parse(args[++i]);
                }
                break;

                case "--volume" when i + 1 < args.Length:
                {
                    volume = float.Parse(args[++i]);
                }
                break;
            }
        }

        var password = ResolvePassword(host, explicitPassword, noPassword);

        try
        {
            switch (command)
            {
                case "info":
                {
                    return await InfoAsync(host);
                }

                case "pair":
                {
                    return await PairAsync(host, password);
                }

                case "tone":
                {
                    return await ToneAsync(host, password, seconds, freq, volume);
                }

                case "meta":
                {
                    return await MetaAsync(host, password, seconds, volume);
                }

                case "hold":
                {
                    return await HoldAsync(host, password, seconds, volume);
                }

                default:
                {
                    Console.Error.WriteLine($"unknown command '{command}'");
                    return 1;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {ex.Message}");
            return 1;
        }
        finally
        {
            Logging.Shutdown();
        }
    }

    // ---- password ------------------------------------------------------------------------

    /// <summary>
    /// The password the receiver will be offered, from the most explicit source available.
    /// Falls back to what the APP already stored for this receiver, because "the tool works
    /// but the app doesn't" has previously come down to exactly this input differing.
    /// </summary>
    private static string? ResolvePassword(string host, string? explicitPassword, bool noPassword)
    {
        if (noPassword)
        {
            Console.WriteLine("password: NONE (forced transient PIN)");
            return null;
        }

        if (!string.IsNullOrEmpty(explicitPassword))
        {
            Console.WriteLine($"password: from --password (len={explicitPassword.Length})");
            return explicitPassword;
        }

        var env = Environment.GetEnvironmentVariable("ORGZ_AIRPLAY_PASSWORD");
        if (!string.IsNullOrEmpty(env))
        {
            Console.WriteLine($"password: from ORGZ_AIRPLAY_PASSWORD (len={env.Length})");
            return env;
        }

        // The app keys credentials by mDNS instance name ("<mac>@<name>._raop..."),
        // which we don't know from a bare hostname - match on the receiver's short name.
        var shortName = host.Split('.')[0];
        var blobs = Settings.Get<Dictionary<string, string>>("OrgZ.Secrets", null!) ?? new(StringComparer.OrdinalIgnoreCase);

        foreach (var key in blobs.Keys)
        {
            if (key.StartsWith("AirPlay/", StringComparison.OrdinalIgnoreCase) && key.Contains(shortName, StringComparison.OrdinalIgnoreCase))
            {
                var stored = SecretStore.Get(key);
                if (!string.IsNullOrEmpty(stored))
                {
                    Console.WriteLine($"password: from app secret store '{key}' (len={stored.Length})");
                    return stored;
                }
            }
        }

        Console.WriteLine("password: none found (transient PIN)");
        return null;
    }

    // ---- info ------------------------------------------------------------------------------

    private static async Task<int> InfoAsync(string host)
    {
        using var rtsp = new RtspClient();
        await rtsp.ConnectAsync(host, RtspPort, CancellationToken.None);
        rtsp.DefaultHeaders["User-Agent"] = "AirPlay/550.10";

        var info = await rtsp.SendAsync("GET", "/info", ct: CancellationToken.None);
        if (!info.IsSuccess)
        {
            Console.Error.WriteLine($"FAIL: /info returned {info.StatusCode} {info.StatusText}");
            return 1;
        }

        PrintInfo(host, info.BodyBytes);
        return 0;
    }

    private static void PrintInfo(string host, byte[] body)
    {
        if (BinaryPlist.Read(body) is not Dictionary<string, object?> plist)
        {
            Console.Error.WriteLine("FAIL: /info body was not a plist dictionary");
            return;
        }

        Console.WriteLine($"=== {host} /info ===");
        foreach (var (key, value) in plist.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {key} = {Describe(value)}");
        }

        if (plist.TryGetValue("features", out var features) && features is long bits)
        {
            Console.WriteLine($"  features bits set: {string.Join(", ", Enumerable.Range(0, 64).Where(b => (bits & (1L << b)) != 0))}");
        }

        if (plist.TryGetValue("statusFlags", out var status) && status is long flags)
        {
            Console.WriteLine($"  statusFlags = 0x{flags:X} (password={(flags & 0x80) != 0}, pin={(flags & 0x8) != 0}, oneTimePairing={(flags & 0x200) != 0})");
        }
    }

    private static string Describe(object? value) => value switch
    {
        null => "(null)",
        byte[] data => $"<{data.Length} bytes> {Convert.ToHexString(data[..Math.Min(24, data.Length)])}",
        List<object?> list => $"[{string.Join(", ", list.Select(Describe))}]",
        Dictionary<string, object?> map => $"{{{string.Join(", ", map.Select(kv => $"{kv.Key}: {Describe(kv.Value)}"))}}}",
        _ => value.ToString() ?? "",
    };

    // ---- pair ------------------------------------------------------------------------------

    /// <summary>
    /// Pairing in isolation, then /info a second time THROUGH the encrypted channel. If the
    /// second fetch parses, both directions of the HAP layer agree with the receiver - the
    /// whole handshake proven without touching audio.
    /// </summary>
    private static async Task<int> PairAsync(string host, string? password)
    {
        using var rtsp = new RtspClient();
        await rtsp.ConnectAsync(host, RtspPort, CancellationToken.None);
        rtsp.DefaultHeaders["User-Agent"] = "AirPlay/550.10";

        var pairing = new AirPlay2Pairing(password);
        await pairing.PairAsync(rtsp, CancellationToken.None);

        Console.WriteLine($"paired: session key fingerprint {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pairing.SessionKey!))[..12]}");

        var encrypted = await rtsp.SendAsync("GET", "/info", ct: CancellationToken.None);
        if (!encrypted.IsSuccess)
        {
            Console.Error.WriteLine($"FAIL: encrypted /info returned {encrypted.StatusCode} {encrypted.StatusText}");
            return 1;
        }

        PrintInfo($"{host} (encrypted)", encrypted.BodyBytes);
        Console.WriteLine("PASS: transient pairing + HAP channel round trip");
        return 0;
    }

    // ---- tone ------------------------------------------------------------------------------

    private static async Task<int> ToneAsync(string host, string? password, int seconds, double freq, float volume)
    {
        using var session = new AirPlay2Session(host, RtspPort, password) { InitialVolume = volume };
        await session.ConnectAsync(CancellationToken.None);
        Console.WriteLine("connected: session is up");

        // Audio first, then the announcement - metadata is anchored to an RTP timestamp,
        // and a timestamp means nothing in a stream that has never sent a packet.
        var packets = seconds * SampleRate / AirPlay2Session.FramesPerPacket;
        var announceAt = SampleRate / AirPlay2Session.FramesPerPacket / 2;
        var phase = 0.0;

        for (var i = 0; i < packets; i++)
        {
            var pcm = new byte[AirPlay2Session.FramesPerPacket * 4];
            for (var frame = 0; frame < AirPlay2Session.FramesPerPacket; frame++)
            {
                var sample = (short)(0.05 * short.MaxValue * Math.Sin(phase));
                phase += 2 * Math.PI * freq / SampleRate;

                var offset = frame * 4;
                pcm[offset] = (byte)(sample & 0xFF);
                pcm[offset + 1] = (byte)((sample >> 8) & 0xFF);
                pcm[offset + 2] = pcm[offset];
                pcm[offset + 3] = pcm[offset + 1];
            }

            await session.SendPacketAsync(pcm, CancellationToken.None);

            if (i == announceAt)
            {
                await session.SetTrackInfoAsync($"Lab Tone {freq:0} Hz", "OrgZ AirPlay Lab", "Bring-Up", TimeSpan.FromSeconds(seconds), Artwork());
                Console.WriteLine("announced: track metadata + artwork sent");
            }
        }

        if (!session.IsConnected)
        {
            Console.Error.WriteLine("FAIL: session dropped while streaming");
            return 1;
        }

        Console.WriteLine($"PASS: streamed {seconds}s of {freq:0} Hz (audible position {session.AudiblePosition?.TotalSeconds:0.0}s)");
        return 0;
    }

    // ---- meta ------------------------------------------------------------------------------

    private static async Task<int> MetaAsync(string host, string? password, int seconds, float volume)
    {
        using var session = new AirPlay2Session(host, RtspPort, password) { InitialVolume = volume };
        await session.ConnectAsync(CancellationToken.None);
        Console.WriteLine("connected: session is up");

        var packets = seconds * SampleRate / AirPlay2Session.FramesPerPacket;
        var silence = new byte[AirPlay2Session.FramesPerPacket * 4];

        for (var i = 0; i < packets; i++)
        {
            await session.SendPacketAsync(silence, CancellationToken.None);

            if (i == packets / 10)
            {
                await session.SetTrackInfoAsync("First Track", "OrgZ AirPlay Lab", "Bring-Up", TimeSpan.FromSeconds(seconds), Artwork());
                Console.WriteLine($"announced: First Track at {i * AirPlay2Session.FramesPerPacket / (double)SampleRate:0.0}s");
            }

            if (i == packets / 2)
            {
                await session.SetTrackInfoAsync("Second Track", "OrgZ AirPlay Lab", "Bring-Up", TimeSpan.FromSeconds(seconds), null);
                Console.WriteLine($"announced: Second Track at {i * AirPlay2Session.FramesPerPacket / (double)SampleRate:0.0}s");
            }
        }

        if (!session.IsConnected)
        {
            Console.Error.WriteLine("FAIL: session dropped while streaming");
            return 1;
        }

        Console.WriteLine($"PASS: held the session {seconds}s through two track changes");
        return 0;
    }

    /// <summary>
    /// The APP's exact shape, distilled: connect HOLDING (StartPaused, the receiver told
    /// nothing on MediaRemote), pump silence, seed the track mid-hold, then release the way
    /// the sink's pump does - announce as a playing client, then real audio. If `meta`
    /// paints a receiver and this doesn't, the hold sequence is the difference; if both
    /// paint, the app's remaining difference is not in this session at all.
    /// </summary>
    private static async Task<int> HoldAsync(string host, string? password, int seconds, float volume)
    {
        using var session = new AirPlay2Session(host, RtspPort, password) { InitialVolume = volume, StartPaused = true };
        await session.ConnectAsync(CancellationToken.None);
        Console.WriteLine("connected: session is up, HOLDING (paused, silence)");

        var silence = new byte[AirPlay2Session.FramesPerPacket * 4];
        var holdPackets = 10 * SampleRate / AirPlay2Session.FramesPerPacket;

        for (var i = 0; i < holdPackets; i++)
        {
            await session.SendPacketAsync(silence, CancellationToken.None);

            if (i == holdPackets / 2)
            {
                // Mid-hold seed, like the bus replaying the current track at select: the
                // session must SAY nothing yet.
                await session.SetTrackInfoAsync("Held Track", "OrgZ AirPlay Lab", "Hold Shape", TimeSpan.FromSeconds(seconds), Artwork());
                Console.WriteLine("seeded: track info during hold (nothing should be on the wire)");
            }
        }

        // The release, exactly as the sink pump does it: announce as playing, then audio.
        await session.NotifyPlaybackStartedAsync(CancellationToken.None);
        Console.WriteLine("released: hold ended, announced as playing");

        var packets = seconds * SampleRate / AirPlay2Session.FramesPerPacket;
        for (var i = 0; i < packets; i++)
        {
            await session.SendPacketAsync(silence, CancellationToken.None);
        }

        if (!session.IsConnected)
        {
            Console.Error.WriteLine("FAIL: session dropped while streaming");
            return 1;
        }

        Console.WriteLine($"PASS: held 10s then streamed {seconds}s announced as playing");
        return 0;
    }

    /// <summary>Real (if minimal) JPEG artwork - a receiver may ignore a stub that only has magic bytes.</summary>
    private static byte[] Artwork()
    {
        using var bitmap = new SKBitmap(300, 300);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(0xE8, 0x7A, 0x00));
            using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
            canvas.DrawCircle(150, 150, 90, paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }
}
