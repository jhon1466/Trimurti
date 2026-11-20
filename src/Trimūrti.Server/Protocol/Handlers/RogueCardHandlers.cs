using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>
/// 诡谈夜话 (RogueCard), port of Alpha-Persei rogue_card_service: the card game runs in the client; the server owns
/// posts (investigations), story/challenge battle identities, save/rollback snapshots, collections and challenge
/// deck/difficulty progress (Data/Configs/rogue_card_cfg.json). Ops 89602–89628, state frame 89601.
/// </summary>
public sealed class RogueCard(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec)
{
    private JsonElement Cfg => config.Get("rogue_card_cfg.json") ?? throw new ModeError("Missing rogue_card_cfg.json");
    private static void Require(bool ok, string m) { if (!ok) throw new ModeError(m); }
    private static int I(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
    private const string SaveMsg = "impermanence_save_data_net_rec";

    public sealed class Mode { public long BattleId { get; set; } public long StageId { get; set; } public string Phase { get; set; } = ""; public string? Save { get; set; } public string? Rollback { get; set; } public long ThreadId { get; set; } }
    public sealed class Status { public long Deck { get; set; } public long MaxDiff { get; set; } }
    public sealed class State
    {
        public long ThreadId { get; set; }
        public int ThreadState { get; set; }
        public long BattleId { get; set; }
        public List<long> Finished { get; set; } = [];
        public List<long> Liked { get; set; } = [];
        public List<long> Posted { get; set; } = [];
        public List<long> Viewed { get; set; } = [];
        public long Deck { get; set; }
        public long Diff { get; set; }
        public int ChallengeState { get; set; }
        public long ChallengeBattleId { get; set; }
        public List<Status> StatusList { get; set; } = [];
        public List<long> Cards { get; set; } = [];
        public List<long> Enhances { get; set; } = [];
        public List<long> WealWoe { get; set; } = [];
        public Mode Story { get; set; } = new();
        public Mode Challenge { get; set; } = new();
    }

    private async Task<State> LoadAsync(long uid, CancellationToken ct) => await accounts.GetJsonAsync<State>(uid, "rogue_card_v1", ct) ?? new State();

    private Dictionary<string, object?> Payload(State s) => new()
    {
        ["story"] = new Dictionary<string, object?>
        {
            ["thread_id"] = (uint)s.ThreadId, ["thread_state"] = (uint)s.ThreadState, ["battle_id"] = (ulong)s.BattleId,
            ["finish_thread_list"] = s.Finished.Select(x => (uint)x).ToArray(), ["like_thread_list"] = s.Liked.Select(x => (uint)x).ToArray(),
            ["post_thread_list"] = s.Posted.Select(x => (uint)x).ToArray(), ["view_thread_list"] = s.Viewed.Select(x => (uint)x).ToArray()
        },
        ["challenge"] = new Dictionary<string, object?>
        {
            ["deck"] = (uint)s.Deck, ["diff"] = (uint)s.Diff, ["state"] = (uint)s.ChallengeState, ["battle_id"] = (ulong)s.ChallengeBattleId,
            ["status_list"] = s.StatusList.Select(x => (object)new Dictionary<string, object?> { ["deck"] = (uint)x.Deck, ["max_diff"] = (uint)x.MaxDiff }).ToArray()
        },
        ["gather_card_list"] = s.Cards.Select(x => (uint)x).ToArray(), ["gather_enhance_list"] = s.Enhances.Select(x => (uint)x).ToArray(),
        ["gather_weal_woe_list"] = s.WealWoe.Select(x => (uint)x).ToArray()
    };

    public async Task<(uint, byte[])> LoginFrameAsync(long uid, CancellationToken ct) => (89601, codec.Encode("sc_89601", Payload(await LoadAsync(uid, ct))));

    private JsonElement Post(State s, long tid)
    {
        Require(Cfg.GetProperty("posts").TryGetProperty(tid.ToString(), out var p), "Post does not exist");
        Require(I(p, "pre_id") == 0 || s.Finished.Contains(I(p, "pre_id")), "Post locked");
        return p;
    }

    private Mode ModeOf(State s, long bid, int kind)
    {
        Require(kind is 1 or 2, "Bad battle type");
        var m = kind == 1 ? s.Story : s.Challenge;
        var expected = kind == 1 ? s.BattleId : s.ChallengeBattleId;
        Require(bid > 0 && bid == expected && m.BattleId == bid && m.Phase == "active", "Battle expired");
        return m;
    }

    private void Collect(State s, IReadOnlyDictionary<string, object?>? info)
    {
        if (info is null) return;
        foreach (var (src, list, table) in new[] { ("use_item_id", s.Cards, "cards"), ("enhance_id", s.Enhances, "enhances"), ("weal_woe_id", s.WealWoe, "weal_woe") })
            foreach (var v in HandlerHelpers.ReadIdList(info, src))
            {
                Require(Cfg.GetProperty(table).TryGetProperty(v.ToString(), out _), "Unknown collection item");
                if (!list.Contains(v)) list.Add(v);
            }
    }

    private string? Snapshot(object? body) => body is IReadOnlyDictionary<string, object?> d ? Convert.ToBase64String(codec.Encode(SaveMsg, d)) : null;
    private IReadOnlyDictionary<string, object?>? Restore(string? b64) => b64 is null ? null : codec.Decode(SaveMsg, Convert.FromBase64String(b64));

    private static long NewBattleId() => (long)(BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0) & long.MaxValue);

    private void Begin(State s, long tid)
    {
        var post = Post(s, tid);
        Require(I(post, "type") == 1, "Post cannot be investigated");
        s.ThreadId = tid; s.ThreadState = 1; s.BattleId = 0;
        if (!s.Posted.Contains(tid)) s.Posted.Add(tid);
        Require(I(post, "balatro_id") != 0 || I(post, "story_id1") > 0, "Post has no content");
        s.Story = new Mode { Phase = "intro", ThreadId = tid, StageId = I(post, "balatro_id") };
    }

    private void Finish(State s, long tid) { if (!s.Finished.Contains(tid)) s.Finished.Add(tid); }

    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, uint cmd, CancellationToken ct)
    {
        var uid = r.AccountId;
        var srv = (ushort)(r.ServerIndex + 1);
        var s = await LoadAsync(uid, ct);
        var b = r.Body;
        var reply = new Dictionary<string, object?> { ["result"] = 0u };
        var stateChanged = cmd is 89602 or 89604 or 89608 or 89610 or 89612 or 89616 or 89618 or 89624;
        try
        {
            var tid = (long)HandlerHelpers.ReadUInt(b, "thread_id");
            var bid = (long)HandlerHelpers.ReadULong(b, "battle_id");
            var kind = (int)HandlerHelpers.ReadUInt(b, "battle_type");
            switch (cmd)
            {
                case 89602 or 89608: Begin(s, tid); break;
                case 89604:
                    {
                        var post = Post(s, tid);
                        var idx = (int)HandlerHelpers.ReadUInt(b, "story_id_index");
                        Require(idx is 1 or 2 && HandlerHelpers.ReadUInt(b, "story_id") == I(post, "story_id" + idx), "Story does not belong to the post");
                        if (s.Finished.Contains(tid) && s.ThreadId != tid) { stateChanged = false; break; }
                        Require(s.ThreadId == tid, "Investigated post mismatch");
                        if (idx == 1)
                        {
                            Require(s.ThreadState is 1 or 2 or 3, "Wrong story phase");
                            if (I(post, "balatro_id") == 0) { Finish(s, tid); s.ThreadState = 0; s.ThreadId = 0; s.BattleId = 0; s.Story.Phase = "done"; }
                            else if (s.ThreadState < 3) s.ThreadState = 2;
                        }
                        else
                        {
                            Require(s.Finished.Contains(tid), "Investigation not finished");
                            s.ThreadState = 0; s.ThreadId = 0; s.BattleId = 0; s.Story.Phase = "done";
                        }
                        break;
                    }
                case 89606: Post(s, tid); if (!s.Liked.Remove(tid)) s.Liked.Add(tid); break;
                case 89610: Post(s, tid); if (!s.Viewed.Contains(tid)) s.Viewed.Add(tid); else stateChanged = false; break;
                case 89612:
                    {
                        var post = Post(s, tid);
                        if (s.Story.Phase is "won" or "done" && s.Story.ThreadId == tid) { stateChanged = false; break; }
                        Require(s.ThreadId == tid && s.ThreadState == 3 && s.BattleId > 0, "Investigation battle mismatch");
                        Require(I(post, "balatro_id") == s.Story.StageId && HandlerHelpers.ReadUInt(b, "joker_result") == 1, "Wrong investigation result");
                        Collect(s, b.GetValueOrDefault("info") as IReadOnlyDictionary<string, object?>);
                        Finish(s, tid);
                        s.ThreadState = 4; s.BattleId = 0;
                        s.Story = new Mode { Phase = "won", ThreadId = tid, StageId = s.Story.StageId };
                        break;
                    }
                case 89616:
                    {
                        var deck = (long)HandlerHelpers.ReadUInt(b, "deck");
                        var diff = (long)HandlerHelpers.ReadUInt(b, "diff");
                        Require(s.ChallengeState == 1 && s.ChallengeBattleId > 0 && s.Challenge.Phase == "active" && s.Deck == deck && s.Diff == diff, "Challenge battle mismatch");
                        Collect(s, b.GetValueOrDefault("info") as IReadOnlyDictionary<string, object?>);
                        var st = s.StatusList.FirstOrDefault(x => x.Deck == deck);
                        if (st is null) s.StatusList.Add(new Status { Deck = deck, MaxDiff = diff }); else st.MaxDiff = Math.Max(st.MaxDiff, diff);
                        s.ChallengeState = 0; s.ChallengeBattleId = 0;
                        s.Challenge = new Mode { Phase = "won" };
                        break;
                    }
                case 89618:
                    {
                        var stageId = (long)HandlerHelpers.ReadUInt(b, "stage_id");
                        Require(Cfg.GetProperty("stages").TryGetProperty(stageId.ToString(), out var stage), "Stage does not exist");
                        var newBid = NewBattleId();
                        if (I(stage, "type") == 1)
                        {
                            var post = Post(s, s.ThreadId);
                            Require(I(post, "balatro_id") == stageId && s.ThreadState is 1 or 2 or 3, "Stage does not belong to the investigation");
                            s.ThreadState = 3; s.BattleId = newBid;
                            if (!s.Posted.Contains(s.ThreadId)) s.Posted.Add(s.ThreadId);
                            s.Story = new Mode { BattleId = newBid, StageId = stageId, Phase = "active", ThreadId = s.ThreadId };
                        }
                        else
                        {
                            var deck = (long)HandlerHelpers.ReadUInt(b, "deck");
                            var diff = (long)HandlerHelpers.ReadUInt(b, "diff");
                            Require(Cfg.GetProperty("decks").TryGetProperty(deck.ToString(), out var dc) && Cfg.GetProperty("difficulties").TryGetProperty(diff.ToString(), out _), "Bad challenge parameters");
                            if (Cfg.GetProperty("conditions").TryGetProperty(I(dc, "condition").ToString(), out var cond))
                            {
                                var a = cond.GetProperty("params");
                                Require(I(cond, "type") == 12701 ? s.Finished.Contains(a[0].GetInt64()) : s.StatusList.Any(x => x.Deck == a[0].GetInt64() && x.MaxDiff >= a[1].GetInt64()), "Deck locked");
                            }
                            Require(diff <= s.StatusList.FirstOrDefault(x => x.Deck == deck)?.MaxDiff + 1 || (diff <= 1 && s.StatusList.All(x => x.Deck != deck)), "Difficulty locked");
                            s.Deck = deck; s.Diff = diff; s.ChallengeState = 1; s.ChallengeBattleId = newBid;
                            s.Challenge = new Mode { BattleId = newBid, StageId = stageId, Phase = "active" };
                        }
                        reply["battle_id"] = (ulong)newBid;
                        break;
                    }
                case 89620:
                    {
                        var m = ModeOf(s, bid, kind);
                        var save = b.GetValueOrDefault("save_data") as IReadOnlyDictionary<string, object?>;
                        Require(save is not null && HandlerHelpers.ReadUInt(save, "stage_id") == m.StageId, "Save stage mismatch");
                        m.Save = Snapshot(save);
                        break;
                    }
                case 89622:
                    {
                        var m = ModeOf(s, bid, kind);
                        Require(m.Save is not null, "No save to continue");
                        reply["battle_id"] = (ulong)bid;
                        reply["save_data"] = Restore(m.Save);
                        if (m.Rollback is not null) reply["save_rollback"] = Restore(m.Rollback);
                        break;
                    }
                case 89624:
                    {
                        var m = ModeOf(s, bid, kind);
                        var info = Restore(m.Save)?.GetValueOrDefault("settle_data") as IReadOnlyDictionary<string, object?>;
                        Collect(s, info);
                        if (kind == 1) { s.ThreadId = 0; s.ThreadState = 0; s.BattleId = 0; } else { s.ChallengeState = 0; s.ChallengeBattleId = 0; }
                        m.Phase = "interrupted"; m.Save = null; m.Rollback = null;
                        reply["battle_id"] = (ulong)bid; reply["battle_type"] = (uint)kind;
                        if (info is not null) reply["settle_info"] = info;
                        break;
                    }
                case 89626:
                    {
                        var m = ModeOf(s, bid, kind);
                        Require(kind == 1, "Challenge has no rollback");
                        var rb = b.GetValueOrDefault("save_rollback") as IReadOnlyDictionary<string, object?>;
                        Require(rb is not null && HandlerHelpers.ReadUInt(rb, "stage_id") == m.StageId, "Rollback stage mismatch");
                        m.Rollback = Snapshot(rb);
                        break;
                    }
                case 89628:
                    {
                        var m = ModeOf(s, bid, kind);
                        Require(m.Save is not null, "No save yet");
                        reply["battle_id"] = (ulong)bid;
                        reply["save_data"] = Restore(m.Save);
                        break;
                    }
            }
        }
        catch (ModeError) { return [HandlerHelpers.ResultOnly(codec, r, cmd + 1, 2)]; }
        await accounts.SetJsonAsync(uid, "rogue_card_v1", s, ct);
        var frames = new List<GameResponse>();
        if (stateChanged) frames.Add(new GameResponse(89601, 0, srv, codec.Encode("sc_89601", Payload(s))));
        frames.Add(HandlerHelpers.Reply(codec, r, cmd + 1, reply));
        return frames;
    }
}
