using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Trimūrti.Server;

/// <summary>Battle report registered by the UDP battle server (opcode 132), consumed by the TCP settlement (54032).</summary>
public sealed record BattleReport(int Result, bool Win, int BattleTimeMs, BattleStats Stats);

/// <summary>Shared registry between the UDP battle server and TCP settlement handlers.</summary>
public sealed class BattleResultStore
{
    private readonly ConcurrentDictionary<ulong, BattleReport> _results = new();
    public void Put(ulong battleId, BattleReport report)
    {
        _results[battleId] = report;
        if (_results.Count > 200)
            foreach (var key in _results.Keys.Take(_results.Count - 200)) _results.TryRemove(key, out _);
    }
    public BattleReport? Take(ulong battleId) => _results.TryRemove(battleId, out var r) ? r : null;
}

/// <summary>
/// UDP/KCP battle server (port of Alpha-Persei-Cluster battle_server.py, wire format verified against official
/// packet captures). Single-player PVE: answers handshake, ping/pong clock sync, join room, player ready and the
/// battle result (132 → 134/135). The client simulates the fight locally; no 124 lock-step frames are sent.
/// Wire: UDP [type 1B][conn u32][conv u32][KCP segments]; client segments have a 24-byte header (with conv),
/// server segments a 20-byte header (no conv); frames are [flag 1B][opcode u16][rpc u32 if flag 1/2][body].
/// </summary>
public sealed class BattleUdpServer(BattleResultStore results, ILogger<BattleUdpServer> logger) : BackgroundService
{
    public const int Port = 6105;

    private const byte Syn = 1, Ack = 2, Fin = 3, Msg = 4, Err = 5;
    private const byte KcpPush = 81, KcpAck = 82, KcpWask = 83, KcpWins = 84;
    private const ushort OpPing = 100, OpPong = 101, OpConnectRequest = 102, OpConnectResponse = 103,
        OpStartMatch = 125, OpJoinRoom = 126, OpAckJoinRoom = 127, OpFetchTeamInfo = 128, OpAckFetchTeamInfo = 129,
        OpPlayerReady = 130, OpAckPlayerReady = 131, OpBattleResult = 132, OpAckBattleResult = 134, OpAckBattleGsResult = 135;
    // Pong serverTime - clientTime deltas (official capture): a converging curve, constant deltas fail SyncTime.
    private static readonly long[] PongDeltas = [-585, -313, -338, -315];

    private sealed class PendingSeg(uint sn, byte[] frame, int retriesLeft, TimeSpan interval, DateTime retryAt)
    {
        public uint Sn = sn;
        public byte[] Frame = frame;
        public int RetriesLeft = retriesLeft;
        public TimeSpan Interval = interval;
        public DateTime RetryAt = retryAt;
    }

    private sealed class Session
    {
        public uint Conn, Conv, Sn = uint.MaxValue, AckUna;
        public bool SnAligned;
        public IPEndPoint Addr = null!;
        public DateTime LastSeen = DateTime.UtcNow;
        public int Pings;
        public byte[] FragBuffer = [];
        public int FragPending = -1;
        public ulong BattleId;
        public readonly ConcurrentDictionary<uint, PendingSeg> SndBuf = new();
    }

    private static bool SnLt(uint a, uint b) => unchecked(b - a) is > 0 and < 0x80000000;

    private readonly ConcurrentDictionary<uint, Session> _sessions = new();
    private UdpClient? _udp;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { _udp = new UdpClient(new IPEndPoint(IPAddress.Any, Port)); }
        catch (SocketException ex)
        {
            logger.LogError(ex, "Battle UDP server could not bind port {Port}", Port);
            return;
        }
        // Windows: ignore ICMP "port unreachable" resets on UDP sockets.
        try { _udp.Client.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null); } catch { }
        logger.LogInformation("Trimūrti battle UDP/KCP server listening on 0.0.0.0:{Port}", Port);
        _ = Task.Run(() => CollectIdleAsync(stoppingToken), stoppingToken);
        _ = Task.Run(() => RetransmitLoopAsync(stoppingToken), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try { packet = await _udp.ReceiveAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }
            try { HandlePacket(packet.Buffer, packet.RemoteEndPoint); }
            catch (Exception ex) { logger.LogWarning("Battle UDP packet error: {Error}", ex.Message); }
        }
        _udp.Dispose();
    }

    private async Task CollectIdleAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch { return; }
            foreach (var (id, s) in _sessions)
                if (DateTime.UtcNow - s.LastSeen > TimeSpan.FromMinutes(10)) _sessions.TryRemove(id, out _);
        }
    }

    private async Task RetransmitLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(50, ct); } catch { return; }
            var now = DateTime.UtcNow;
            foreach (var (_, st) in _sessions)
            {
                if (st.SndBuf.IsEmpty) continue;
                foreach (var (sn, seg) in st.SndBuf)
                {
                    if (now < seg.RetryAt) continue;
                    if (seg.RetriesLeft <= 0) { st.SndBuf.TryRemove(sn, out _); continue; }
                    seg.RetriesLeft--;
                    seg.RetryAt = now + seg.Interval;
                    SendKcp(st, KcpPush, seg.Frame, sn, null);
                }
            }
        }
    }

    private void HandlePacket(byte[] data, IPEndPoint addr)
    {
        if (data.Length < 5) return;
        var type = data[0];
        var localConn = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(1));
        _sessions.TryGetValue(localConn, out var st);

        if (type == Syn && data.Length >= 9)
        {
            var clientId = data.Length >= 11 && data[2] == 1 ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(3)) : localConn;
            var serverId = (uint)((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() ^ 0x11A04C54 ^ clientId) & 0xFFFFFFFF);
            _sessions[clientId] = new Session { Conn = serverId, Conv = clientId, Addr = addr };
            var ack = new byte[9];
            ack[0] = Ack;
            BinaryPrimitives.WriteUInt32LittleEndian(ack.AsSpan(1), serverId);
            BinaryPrimitives.WriteUInt32LittleEndian(ack.AsSpan(5), clientId);
            Send(ack, addr);
            logger.LogInformation("Battle UDP SYN client={Client} -> server={Server}", clientId, serverId);
            return;
        }
        if (type == Ack && data.Length >= 9)
        {
            if (st is null)
            {
                var clientId = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(5));
                _sessions[clientId] = new Session { Conn = localConn, Conv = clientId, Addr = addr };
            }
            return;
        }
        if (type is Fin or Err) { _sessions.TryRemove(localConn, out _); return; }
        if (type != Msg) return;

        if (st is null)
        {
            var peer = data.Length >= 9 ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(5)) : 0;
            st = _sessions[localConn] = new Session { Conn = peer, Conv = localConn, Addr = addr };
        }
        st.Addr = addr; // the client changes UDP source port per packet
        st.LastSeen = DateTime.UtcNow;

        var off = 5;
        if (data.Length >= off + 24 && st.Conn != 0 && BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off)) != st.Conn) off += 2;
        while (off + 24 <= data.Length)
        {
            var seg = data.AsSpan(off);
            var cmd = seg[4];
            var frg = seg[5];
            var ts = BinaryPrimitives.ReadUInt32LittleEndian(seg[8..]);
            var sn = BinaryPrimitives.ReadUInt32LittleEndian(seg[12..]);
            var una = BinaryPrimitives.ReadUInt32LittleEndian(seg[16..]);
            var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(seg[20..]);
            if (24 + len > seg.Length) break;
            var payload = seg.Slice(24, len).ToArray();
            off += 24 + len;

            // Server sn must start at the client's rcv_nxt or the client drops our segments as old.
            if (!st.SnAligned && cmd == KcpPush) { st.SnAligned = true; st.Sn = sn - 1; }
            if (cmd == KcpAck)
            {
                st.AckUna = Math.Max(st.AckUna, una);
                st.SndBuf.TryRemove(sn, out _);
                foreach (var s in st.SndBuf.Keys) if (SnLt(s, una)) st.SndBuf.TryRemove(s, out _);
                continue;
            }
            foreach (var s in st.SndBuf.Keys) if (SnLt(s, una)) st.SndBuf.TryRemove(s, out _);
            if (cmd == KcpPush && payload.Length > 0)
            {
                st.AckUna = Math.Max(st.AckUna, sn + 1);
                SendKcp(st, KcpAck, [], sn, ts);
                if (frg > 0) { st.FragBuffer = [.. st.FragBuffer, .. payload]; st.FragPending = frg; }
                else if (st.FragPending > 0)
                {
                    var full = st.FragBuffer.Concat(payload).ToArray();
                    st.FragBuffer = []; st.FragPending = -1;
                    HandleFrame(st, full);
                }
                else HandleFrame(st, payload);
            }
            else if (cmd is KcpWask or KcpWins) SendKcp(st, KcpAck, [], uint.MaxValue, ts);
        }
    }

    private void HandleFrame(Session st, byte[] frame)
    {
        if (frame.Length < 3) return;
        var flag = frame[0];
        var op = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1));
        uint rpc = 0;
        var bodyOffset = 3;
        if (flag is 1 or 2)
        {
            if (frame.Length < 7) return;
            rpc = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(3));
            bodyOffset = 7;
        }
        var body = frame.AsSpan(bodyOffset).ToArray();
        switch (op)
        {
            case OpPing:
            {
                var clientMs = body.Length >= 8 ? BinaryPrimitives.ReadUInt64LittleEndian(body) : 0UL;
                var serverMs = (ulong)((long)clientMs + PongDeltas[Math.Min(st.Pings++, PongDeltas.Length - 1)]);
                var pong = new byte[16];
                BinaryPrimitives.WriteUInt64LittleEndian(pong, clientMs);
                BinaryPrimitives.WriteUInt64LittleEndian(pong.AsSpan(8), serverMs);
                SendFrame(st, OpPong, pong, 2, rpc, (uint)serverMs);
                break;
            }
            case OpConnectRequest:
                SendFrame(st, OpConnectResponse, [.. Pb(1, 0), .. Pb(2, 1), .. Pb(3, 1)], 2, rpc);
                break;
            case OpJoinRoom:
            {
                var f = ParseTop(body);
                st.BattleId = f.GetValueOrDefault(1);
                SendFrame(st, OpAckJoinRoom, [.. Pb(1, st.BattleId), .. Pb(2, 1), .. Pb(3, f.GetValueOrDefault(3))], 2, rpc);
                SendFrame(st, OpStartMatch, [], 0, 0);
                break;
            }
            case OpPlayerReady:
            {
                var f = ParseTop(body);
                var battleId = f.GetValueOrDefault(2);
                if (battleId > 0) st.BattleId = battleId;
                SendFrame(st, OpAckPlayerReady, Pb(1, battleId > 0 ? battleId : 1), 2, rpc);
                SendFrame(st, OpStartMatch, [], 0, 0);
                logger.LogInformation("Battle UDP ready battle={Battle}", st.BattleId);
                break;
            }
            case OpFetchTeamInfo:
                SendFrame(st, OpAckFetchTeamInfo, [], 2, rpc);
                break;
            case OpBattleResult:
            {
                var f = ParseTop(body, out var nested);
                var battleId = f.GetValueOrDefault(1);
                var result = (int)f.GetValueOrDefault(3);
                var info = nested.TryGetValue(4, out var raw) ? ParseTop(raw) : new Dictionary<int, ulong>();
                // Register before acknowledging: the client's 54032 can arrive inside the ack resend window.
                // BattleResultInfo: 1 result, 2 battle_time(ms), 3 total_dead_num, 4 total_hitted_num, 5 injured_num,
                // 6 fall_down_num, 7 knockout_num, 8 qte_count, 9 resurrect_times, 11 enemy_dead_num.
                int F(int n) => (int)Math.Min(int.MaxValue, info.GetValueOrDefault(n));
                var ids = nested.TryGetValue(4, out var rawInfo) ? Repeated(rawInfo, 12) : [];
                var nums = nested.TryGetValue(4, out rawInfo) ? Repeated(rawInfo, 13) : [];
                var items = new Dictionary<long, long>();
                for (var i = 0; i < Math.Min(ids.Count, nums.Count); i++) items[(long)ids[i]] = items.GetValueOrDefault((long)ids[i]) + (long)nums[i];
                var recIds = nested.TryGetValue(4, out rawInfo) ? Repeated(rawInfo, 14) : [];
                var recNums = nested.TryGetValue(4, out rawInfo) ? Repeated(rawInfo, 15) : [];
                var records = new Dictionary<long, long>();
                for (var i = 0; i < Math.Min(recIds.Count, recNums.Count); i++) records[(long)recIds[i]] = (long)recNums[i];
                var stats = new BattleStats(F(2), F(3), F(4), F(5), F(6), F(7), F(9), F(11)) { Items = items, Records = records };
                results.Put(battleId, new BattleReport(result, info.GetValueOrDefault(1) != 0, F(2), stats));
                logger.LogInformation("Battle UDP result battle={Battle} result={Result}", battleId, result);
                var code = (ulong)Math.Max(1, result);
                // Reliable KCP delivery for 134/135 (20 retries × 150ms = 3s window, matching Alpha-Persei 955833b).
                SendReliableFrame(st, OpAckBattleResult, Pb(1, code), 2, rpc, retries: 20, intervalMs: 150);
                SendReliableFrame(st, OpAckBattleGsResult, Pb(1, code), 0, 0, retries: 20, intervalMs: 150);
                break;
            }
            // 0 / 123: client battle frame upload — KCP ACK is enough.
        }
    }

    private void SendFrame(Session st, ushort opcode, byte[] body, byte flag, uint rpc, uint? ts = null) =>
        SendReliableFrame(st, opcode, body, flag, rpc, ts, retries: 12, intervalMs: 200);

    private void SendReliableFrame(Session st, ushort opcode, byte[] body, byte flag, uint rpc, uint? ts = null, int retries = 12, int intervalMs = 200)
    {
        var header = flag == 0 ? 3 : 7;
        var frame = new byte[header + body.Length];
        frame[0] = flag;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1), opcode);
        if (flag != 0) BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(3), rpc);
        body.CopyTo(frame, header);
        var sn = ++st.Sn;
        if (st.SndBuf.Count >= 64)
            foreach (var old in st.SndBuf.Keys.Order().Take(16)) st.SndBuf.TryRemove(old, out _);
        var interval = TimeSpan.FromMilliseconds(intervalMs);
        st.SndBuf[sn] = new PendingSeg(sn, frame, retries, interval, DateTime.UtcNow + interval);
        SendKcp(st, KcpPush, frame, sn, ts);
    }

    private void SendKcp(Session st, byte cmd, byte[] data, uint? sn, uint? ts)
    {
        var seq = sn ?? ++st.Sn;
        var packet = new byte[9 + 20 + data.Length];
        packet[0] = Msg;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), st.Conn);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(5), st.Conv);
        var seg = packet.AsSpan(9);
        seg[0] = cmd;
        seg[1] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(seg[2..], 256);
        BinaryPrimitives.WriteUInt32LittleEndian(seg[4..], ts ?? (uint)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() & 0xFFFFFFFF));
        BinaryPrimitives.WriteUInt32LittleEndian(seg[8..], seq);
        BinaryPrimitives.WriteUInt32LittleEndian(seg[12..], st.AckUna);
        BinaryPrimitives.WriteUInt32LittleEndian(seg[16..], (uint)data.Length);
        data.CopyTo(packet, 29);
        Send(packet, st.Addr);
    }

    private void Send(byte[] packet, IPEndPoint addr)
    {
        try { _udp?.Send(packet, packet.Length, addr); }
        catch (SocketException ex) { logger.LogDebug("Battle UDP send failed: {Error}", ex.Message); }
    }

    private static byte[] Pb(int field, ulong value)
    {
        var bytes = new List<byte>();
        void Varint(ulong v) { while (v >= 0x80) { bytes.Add((byte)(v | 0x80)); v >>= 7; } bytes.Add((byte)v); }
        Varint((ulong)(field << 3));
        Varint(value);
        return [.. bytes];
    }

    private static Dictionary<int, ulong> ParseTop(byte[] payload) => ParseTop(payload, out _);

    /// <summary>All values of a repeated varint field, packed or unpacked.</summary>
    private static List<ulong> Repeated(byte[] payload, int wanted)
    {
        var list = new List<ulong>();
        var i = 0;
        try
        {
            while (i < payload.Length)
            {
                var tag = ReadVarint(payload, ref i);
                var field = (int)(tag >> 3);
                switch (tag & 7)
                {
                    case 0:
                        var v = ReadVarint(payload, ref i);
                        if (field == wanted) list.Add(v);
                        break;
                    case 2:
                        var len = (int)ReadVarint(payload, ref i);
                        var end = i + len;
                        if (field == wanted) while (i < end) list.Add(ReadVarint(payload, ref i));
                        i = end;
                        break;
                    case 1: i += 8; break;
                    case 5: i += 4; break;
                    default: return list;
                }
            }
        }
        catch (IndexOutOfRangeException) { }
        return list;
    }

    private static Dictionary<int, ulong> ParseTop(byte[] payload, out Dictionary<int, byte[]> nested)
    {
        var values = new Dictionary<int, ulong>();
        nested = [];
        var i = 0;
        try
        {
            while (i < payload.Length)
            {
                var tag = ReadVarint(payload, ref i);
                var field = (int)(tag >> 3);
                switch (tag & 7)
                {
                    case 0: values[field] = ReadVarint(payload, ref i); break;
                    case 2:
                        var len = (int)ReadVarint(payload, ref i);
                        nested[field] = payload.AsSpan(i, len).ToArray();
                        i += len;
                        break;
                    case 1: i += 8; break;
                    case 5: i += 4; break;
                    default: return values;
                }
            }
        }
        catch (ArgumentOutOfRangeException) { }
        catch (IndexOutOfRangeException) { }
        return values;
    }

    private static ulong ReadVarint(byte[] b, ref int i)
    {
        ulong v = 0;
        for (var shift = 0; ; shift += 7)
        {
            var x = b[i++];
            v |= (ulong)(x & 0x7F) << shift;
            if ((x & 0x80) == 0) return v;
        }
    }
}
