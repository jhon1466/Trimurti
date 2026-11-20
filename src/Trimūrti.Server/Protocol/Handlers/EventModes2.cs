using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

// ======================================================================================================
// 夏日餐厅 (Summer pub, stage type 87): level chain (battle / pinball / cooking), first-clear rewards,
// illustrations and day/night; ops 89002 pinball result, 89004 view illustration, 89006 cook, 89008 time.
// ======================================================================================================
public sealed class PubMode(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec)
    : EventModeBase(accounts, config, codec, "summer_pub_contract.json"), IStageMode
{
    public sealed class State
    {
        public List<long> Balls { get; set; } = [];
        public Dictionary<long, int> Cooks { get; set; } = [];
        public List<long> Battles { get; set; } = [];
        public Dictionary<long, int> Illustrated { get; set; } = [];
        public int TimeState { get; set; } = 1;
    }

    private JsonElement Levels => Cfg.GetProperty("levels");
    private JsonElement Level(long id) => Levels.GetProperty(id.ToString());
    private long? LevelOf(long dest)
    {
        if (Levels.TryGetProperty(dest.ToString(), out var l) && l.GetProperty("type").GetInt32() == 1) return dest;
        foreach (var p in Levels.EnumerateObject())
            if (p.Value.GetProperty("type").GetInt32() == 1 && p.Value.GetProperty("stages").EnumerateArray().Any(s => s.GetInt64() == dest)) return long.Parse(p.Name);
        return null;
    }

    public bool Reserved(uint type, long stage) => LevelOf(stage) is not null;
    private async Task<State> LoadAsync(long uid, CancellationToken ct) => await Accounts.GetJsonAsync<State>(uid, "pub_v1", ct) ?? new State();
    private Task SaveAsync(long uid, State s, CancellationToken ct) => Accounts.SetJsonAsync(uid, "pub_v1", s, ct);

    private bool Passed(State s, long level)
    {
        var cfg = Level(level);
        var first = cfg.GetProperty("stages")[0].GetInt64();
        return cfg.GetProperty("type").GetInt32() switch { 1 => s.Battles.Contains(level), 2 => s.Balls.Contains(first), _ => s.Cooks.GetValueOrDefault(first) == 2 };
    }

    private bool Unlocked(State s, long level) { var prev = Level(level).GetProperty("prev_id").GetInt64(); return prev == 0 || Passed(s, prev); }

    private Task<IReadOnlyList<(long, long)>> FirstAsync(long uid, long level, bool was, CancellationToken ct) =>
        was ? Task.FromResult<IReadOnlyList<(long, long)>>([]) : GrantOnceAsync(uid, "summer_pub_first", level.ToString(), Pairs(Level(level).GetProperty("rewards")), ct);

    public async Task<(List<GameResponse>, IReadOnlyList<(long, long)>)> SettleAsync(ModeBattle b, CancellationToken ct)
    {
        var level = LevelOf(b.StageId)!.Value;
        var s = await LoadAsync(b.Uid, ct);
        Require(Unlocked(s, level), "Pub prerequisite not cleared");
        if (!b.Win) return ([], []);
        var was = Passed(s, level);
        if (!was) { s.Battles.Add(level); await SaveAsync(b.Uid, s, ct); }
        var granted = await FirstAsync(b.Uid, level, was, ct);
        var frames = await SyncAsync(b.Request, granted.Select(x => x.Item1), ct);
        frames.Add(Push(b.Srv, 89011, Payload(s)));
        await TaskAsync(frames, b, 350001, level);
        return (frames, granted);
    }

    private static Dictionary<string, object?> Payload(State s) => new()
    {
        ["stage_list"] = s.Balls.OrderBy(x => x).Select(x => (uint)x).ToArray(), ["battle_stage_list"] = s.Battles.OrderBy(x => x).Select(x => (uint)x).ToArray(),
        ["time_state"] = (uint)s.TimeState,
        ["cook_stage_list"] = s.Cooks.OrderBy(x => x.Key).Select(c => (object)new Dictionary<string, object?> { ["stage_id"] = (uint)c.Key, ["state"] = (uint)c.Value }).ToArray(),
        ["illustrated"] = s.Illustrated.OrderBy(x => x.Key).Select(c => (object)new Dictionary<string, object?> { ["illustrated_id"] = (uint)c.Key, ["view_state"] = (uint)c.Value }).ToArray()
    };

    public async Task<IReadOnlyList<(uint, byte[])>> LoginFramesAsync(ProtoCodec codec, long uid, CancellationToken ct) => [Frame(89011, Payload(await LoadAsync(uid, ct)))];

    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, uint cmd, CancellationToken ct)
    {
        try
        {
            Require(HandlerHelpers.ReadUInt(r.Body, "activity_id") == Cfg.GetProperty("activity_id").GetUInt32(), "Pub activity mismatch");
            var uid = r.AccountId;
            var s = await LoadAsync(uid, ct);
            IReadOnlyList<(long, long)> granted = [];
            switch (cmd)
            {
                case 89002:
                    {
                        var level = (long)HandlerHelpers.ReadUInt(r.Body, "stage_id");
                        var sub = (long)HandlerHelpers.ReadUInt(r.Body, "sub_stage_id");
                        var result = HandlerHelpers.ReadUInt(r.Body, "result");
                        Require(Levels.TryGetProperty(level.ToString(), out var cfg) && cfg.GetProperty("type").GetInt32() == 2, "Unknown pinball level");
                        var subs = cfg.GetProperty("stages").EnumerateArray().Select(x => x.GetInt64()).ToList();
                        Require(subs.Contains(sub) && result is 1 or 2 && Unlocked(s, level), "Invalid pinball result");
                        Require(sub == subs[0] || s.Balls.Contains(subs[0]), "Hard pinball needs the normal one first");
                        var ills = HandlerHelpers.ReadIdList(r.Body, "illustrated");
                        var allowed = Cfg.GetProperty("balls").GetProperty(sub.ToString()).GetProperty("illustrations").EnumerateArray().Select(x => x.GetInt64()).ToHashSet();
                        Require(ills.All(allowed.Contains), "Illustration does not belong to the level");
                        if (result == 1)
                        {
                            var was = Passed(s, level);
                            if (!s.Balls.Contains(sub)) s.Balls.Add(sub);
                            foreach (var i in ills) s.Illustrated.TryAdd(i, 1);
                            if (sub == subs[0]) granted = await FirstAsync(uid, level, was, ct);
                        }
                        break;
                    }
                case 89004:
                    {
                        var ill = (long)HandlerHelpers.ReadUInt(r.Body, "ill_id");
                        Require(s.Illustrated.ContainsKey(ill), "Illustration not obtained");
                        s.Illustrated[ill] = 2;
                        break;
                    }
                case 89006:
                    {
                        var dish = (long)HandlerHelpers.ReadUInt(r.Body, "stage_id");
                        var state = (int)HandlerHelpers.ReadUInt(r.Body, "stage");
                        Require(Cfg.GetProperty("cooks").TryGetProperty(dish.ToString(), out var cook) && state is 1 or 2, "Invalid dish/state");
                        Require(cook.GetProperty("unlocked_by_level").EnumerateArray().All(l =>
                        {
                            var lv = l.GetInt64();
                            return Level(lv).GetProperty("type").GetInt32() == 0 && Level(lv).GetProperty("stages").EnumerateArray().Any(x => x.GetInt64() == dish) ? Unlocked(s, lv) : Passed(s, lv);
                        }), "Dish prerequisite missing");
                        var old = s.Cooks.GetValueOrDefault(dish);
                        Require(state >= old, "Cooking state cannot go back");
                        s.Cooks[dish] = state;
                        break;
                    }
                default: s.TimeState = 3 - s.TimeState; break;
            }
            await SaveAsync(uid, s, ct);
            var frames = await SyncAsync(r, granted.Select(x => x.Item1), ct);
            frames.Add(HandlerHelpers.ResultOnly(Codec, r, cmd + 1));
            return frames;
        }
        catch (ModeError) { return [HandlerHelpers.ResultOnly(Codec, r, cmd + 1, 2)]; }
    }
}

// ======================================================================================================
// 谧光叙录 (Hodur, stage type 92): chapters 1–3 with event options and boss clears; chapter 4 is a four-boss
// challenge with self-limiting affixes and carried HP. Ops 89422 reset chapter, 89424 settle challenge,
// 89426 choose event option.
// ======================================================================================================
public sealed class HodurMode(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec)
    : EventModeBase(accounts, config, codec, "hodur_contract.json"), IStageMode
{
    public sealed class HeroHp { public long HeroId { get; set; } public long Blood { get; set; } public long MaxBlood { get; set; } }
    public sealed class Chapter { public List<long> Options { get; set; } = []; public Dictionary<long, int> Clears { get; set; } = []; public List<HeroHp> Heroes { get; set; } = []; public List<long> EventsEver { get; set; } = []; }
    public sealed class Challenge
    {
        public bool Active { get; set; }
        public List<long> Affixes { get; set; } = [];
        public List<HeroHp> Heroes { get; set; } = [];
        public Dictionary<long, long> Scores { get; set; } = [];
        public Dictionary<long, long> StageBest { get; set; } = [];
        public long BestScore { get; set; }
        public long BestAffixes { get; set; }
        public List<HeroHp> LastStatus { get; set; } = [];
        public List<long> ScoreList { get; set; } = [0, 0, 0, 0];
    }
    public sealed class State { public Dictionary<long, Chapter> Chapters { get; set; } = []; public Challenge Challenge { get; set; } = new(); }

    private JsonElement Chapters => Cfg.GetProperty("chapters");
    private List<long> StagesOf(long chapter) => Chapters.GetProperty(chapter.ToString()).GetProperty("stages").EnumerateArray().Select(x => x.GetInt64()).ToList();
    private long ChapterOf(long stage) => Cfg.GetProperty("stages").TryGetProperty(stage.ToString(), out var s) ? s.GetProperty("chapter_id").GetInt64() : 0;

    public bool Reserved(uint type, long stage) => Cfg.GetProperty("stages").TryGetProperty(stage.ToString(), out _);

    private async Task<State> LoadAsync(long uid, CancellationToken ct)
    {
        var s = await Accounts.GetJsonAsync<State>(uid, "hodur_v1", ct) ?? new State();
        foreach (var c in Chapters.EnumerateObject().Where(c => c.Value.GetProperty("chapter_type").GetInt32() == 1)) s.Chapters.TryAdd(long.Parse(c.Name), new Chapter());
        return s;
    }
    private Task SaveAsync(long uid, State s, CancellationToken ct) => Accounts.SetJsonAsync(uid, "hodur_v1", s, ct);

    private void ValidateChapter(State s, long chapter)
    {
        Require(s.Chapters.ContainsKey(chapter), "Unknown Hodur chapter");
        var prev = Chapters.GetProperty(chapter.ToString()).GetProperty("previous_chapter").GetInt64();
        while (prev != 0)
        {
            Require(StagesOf(prev).All(st => s.Chapters[prev].Clears.ContainsKey(st)), "Previous Hodur chapter not cleared");
            prev = Chapters.GetProperty(prev.ToString()).GetProperty("previous_chapter").GetInt64();
        }
    }

    private long EventOfOption(long option) =>
        Cfg.GetProperty("events").EnumerateObject().Where(e => e.Value.GetProperty("options").EnumerateArray().Any(o => o.GetInt64() == option)).Select(e => long.Parse(e.Name)).FirstOrDefault();

    private long EventChapter(long ev) =>
        Cfg.GetProperty("stages").EnumerateObject().Where(s => s.Value.GetProperty("event_lock").GetInt64() == ev).Select(s => s.Value.GetProperty("chapter_id").GetInt64()).FirstOrDefault();

    /// <summary>Private reward-calibrated score: four dimensions with time/hit bonuses × affix efficiency.</summary>
    private (long Total, List<long> Parts) Score(BattleStats stats, IReadOnlyList<long> affixes)
    {
        var sc = Cfg.GetProperty("scoring");
        var n = StagesOf(4).Count * 4;
        var floor = (sc.GetProperty("clear_floor").GetInt64() + n - 1) / n;
        var parts = new List<long> { floor, floor, floor, floor };
        var tl = sc.GetProperty("time_bonus_limit_ms").GetInt64();
        if (stats.BattleTimeMs > 0) parts[0] += floor * Math.Max(0, tl - stats.BattleTimeMs) / tl;
        var hl = sc.GetProperty("hit_bonus_limit").GetInt64();
        parts[1] += floor * Math.Max(0, hl - stats.HittedNum) / hl;
        var eff = 100 + affixes.Sum(a => Cfg.GetProperty("affixes").GetProperty(a.ToString()).GetProperty("point").GetInt64());
        return (parts.Sum() * eff / 100, parts);
    }

    public async Task<(List<GameResponse>, IReadOnlyList<(long, long)>)> SettleAsync(ModeBattle b, CancellationToken ct)
    {
        var s = await LoadAsync(b.Uid, ct);
        var chapter = ChapterOf(b.StageId);
        if (chapter == 4)
        {
            Require(s.Chapters.All(c => StagesOf(c.Key).All(st => c.Value.Clears.ContainsKey(st))), "Hodur challenge needs every normal chapter");
            var c = s.Challenge;
            var index = StagesOf(4).IndexOf(b.StageId);
            if (!c.Active)
            {
                Require(index == 0, "Hodur challenge starts at the first boss");
                c.Active = true;
                c.Heroes = b.Heroes.Select(h => new HeroHp { HeroId = h, Blood = 1000000, MaxBlood = 1000000 }).ToList();
            }
            c.LastStatus = c.Heroes.Select(h => new HeroHp { HeroId = h.HeroId, Blood = h.Blood, MaxBlood = h.MaxBlood }).ToList();
            if (b.Win)
            {
                var (pts, parts) = Score(b.Stats, c.Affixes);
                c.Scores[b.StageId] = pts;
                c.StageBest[b.StageId] = Math.Max(c.StageBest.GetValueOrDefault(b.StageId), pts);
                c.ScoreList = parts;
                if (c.Scores.Count == StagesOf(4).Count) c.BestAffixes = Math.Max(c.BestAffixes, c.Affixes.Count);
            }
        }
        else
        {
            ValidateChapter(s, chapter);
            var row = s.Chapters[chapter];
            var cfg = Cfg.GetProperty("stages").GetProperty(b.StageId.ToString());
            var prev = cfg.GetProperty("previous_stage").GetInt64();
            Require(prev == 0 || row.Clears.GetValueOrDefault(prev) == 1, "Hodur previous stage not cleared");
            var ev = cfg.GetProperty("event_lock").GetInt64();
            Require(ev == 0 || row.Options.Any(o => EventOfOption(o) == ev), "Hodur event option not chosen");
            if (!b.Win) return ([], []);
            row.Clears[b.StageId] = 1;
        }
        await SaveAsync(b.Uid, s, ct);
        var hodurFrames = new List<GameResponse> { Push(b.Srv, 89421, Payload(s)) };
        if (b.Win && chapter != 4) await TaskAsync(hodurFrames, b, 400004, b.StageId);
        return (hodurFrames, []);
    }

    private Dictionary<string, object?> Payload(State s)
    {
        object Hp(HeroHp h) => new Dictionary<string, object?> { ["hero_id"] = (uint)h.HeroId, ["hero_blood"] = (uint)h.Blood, ["hero_max_blood"] = (uint)h.MaxBlood };
        var c = s.Challenge;
        return new()
        {
            ["normal_stage_info"] = s.Chapters.OrderBy(x => x.Key).Select(ch => (object)new Dictionary<string, object?>
            {
                ["normal_stage_id"] = (uint)ch.Key, ["normal_option_info"] = ch.Value.Options.Select(x => (uint)x).ToArray(),
                ["normal_hero_stage_info"] = ch.Value.Heroes.Select(Hp).ToArray(),
                ["normal_boss_id_list"] = StagesOf(ch.Key).Where(ch.Value.Clears.ContainsKey).Select(st => (object)new Dictionary<string, object?> { ["boss_id"] = (uint)st, ["result"] = (uint)ch.Value.Clears[st] }).ToArray()
            }).ToArray(),
            ["challenge_stage_info"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["challenge_stage_id"] = 4u, ["challenge_buff_info"] = c.Affixes.Select(x => (uint)x).ToArray(), ["challenge_max_score"] = (uint)c.BestScore,
                    ["challenge_hero_stage_info"] = c.Heroes.Select(Hp).ToArray(), ["last_status"] = c.LastStatus.Select(Hp).ToArray(),
                    ["score_list"] = c.ScoreList.Select(x => (uint)x).ToArray(),
                    ["challenge_boss_list"] = StagesOf(4).Select(st => (object)new Dictionary<string, object?> { ["boss_id"] = (uint)st, ["score"] = (uint)c.Scores.GetValueOrDefault(st), ["max_score"] = (uint)c.StageBest.GetValueOrDefault(st) }).ToArray()
                }
            }
        };
    }

    public async Task<IReadOnlyList<(uint, byte[])>> LoginFramesAsync(ProtoCodec codec, long uid, CancellationToken ct) => [Frame(89421, Payload(await LoadAsync(uid, ct)))];

    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, uint cmd, CancellationToken ct)
    {
        try
        {
            Require(HandlerHelpers.ReadUInt(r.Body, "activity_id") == Cfg.GetProperty("activity_id").GetUInt32(), "Hodur activity mismatch");
            var s = await LoadAsync(r.AccountId, ct);
            var chapter = (long)HandlerHelpers.ReadUInt(r.Body, "chapter_id");
            Dictionary<string, object?> reply = new() { ["result"] = 0u };
            switch (cmd)
            {
                case 89422:
                    ValidateChapter(s, chapter);
                    s.Chapters[chapter] = new Chapter { Clears = s.Chapters[chapter].Clears.ToDictionary(x => x.Key, _ => 0), EventsEver = s.Chapters[chapter].EventsEver };
                    break;
                case 89424:
                    Require(s.Challenge.Active, "Hodur challenge not started");
                    s.Challenge = new Challenge { BestScore = Math.Max(s.Challenge.BestScore, s.Challenge.Scores.Values.Sum()), BestAffixes = s.Challenge.BestAffixes, StageBest = s.Challenge.StageBest };
                    break;
                case 89426:
                    {
                        ValidateChapter(s, chapter);
                        var option = (long)HandlerHelpers.ReadUInt(r.Body, "option_id");
                        var ev = EventOfOption(option);
                        Require(ev != 0 && EventChapter(ev) == chapter, "Hodur option not in this chapter");
                        var row = s.Chapters[chapter];
                        Require(!row.Options.Any(o => EventOfOption(o) == ev), "Hodur event already chosen");
                        var cond = Cfg.GetProperty("events").GetProperty(ev.ToString()).GetProperty("condition").GetInt64();
                        Require(cond == 0 || row.Clears.GetValueOrDefault(cond) == 1, "Hodur event condition not met");
                        row.Options.Add(option);
                        if (!row.EventsEver.Contains(ev)) row.EventsEver.Add(ev);
                        reply["round"] = (uint)row.Options.Count;
                        break;
                    }
            }
            await SaveAsync(r.AccountId, s, ct);
            return [Push((ushort)(r.ServerIndex + 1), 89421, Payload(s)), HandlerHelpers.Reply(Codec, r, cmd + 1, reply)];
        }
        catch (ModeError) { return [HandlerHelpers.ResultOnly(Codec, r, cmd + 1, 2)]; }
    }

    /// <summary>Affixes chosen on the challenge entry (54030 common_info opts key 1) before the first boss.</summary>
    public async Task SetAffixesAsync(long uid, IReadOnlyList<long> affixes, CancellationToken ct)
    {
        var s = await LoadAsync(uid, ct);
        if (s.Challenge.Active) return;
        s.Challenge.Affixes = affixes.Where(a => Cfg.GetProperty("affixes").TryGetProperty(a.ToString(), out _)).Distinct().OrderBy(x => x).ToList();
        await SaveAsync(uid, s, ct);
    }
}

// ======================================================================================================
// 远村异闻·海拉弹珠 (Pinball, stage type 603): story/battle/challenge barriers, skills; 89402 result, 89404 equip.
// ======================================================================================================
public sealed class PinballMode(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec)
    : EventModeBase(accounts, config, codec, "pinball_contract.json"), IStageMode
{
    public sealed class State { public int Level { get; set; } = 1; public List<long> Cleared { get; set; } = []; public List<long> Skills { get; set; } = []; public List<long> Equip { get; set; } = []; public long Score { get; set; } }

    private JsonElement Stages => Cfg.GetProperty("stages");
    private long? StageOfBattle(long dest) => Stages.EnumerateObject().Where(p => p.Value.GetProperty("barrier_type").GetInt32() == 2 && p.Value.GetProperty("barrier_id").GetInt64() == dest).Select(p => (long?)long.Parse(p.Name)).FirstOrDefault();

    public bool Reserved(uint type, long stage) => StageOfBattle(stage) is not null;
    private async Task<State> LoadAsync(long uid, CancellationToken ct) => await Accounts.GetJsonAsync<State>(uid, "pinball_v1", ct) ?? new State();

    private JsonElement Check(State s, long stage)
    {
        Require(Stages.TryGetProperty(stage.ToString(), out var row), "Unknown pinball stage");
        var pre = row.GetProperty("pre_stage").GetInt64();
        Require(pre == 0 || s.Cleared.Contains(pre), "Pinball prerequisite not cleared");
        if (row.GetProperty("barrier_type").GetInt32() == 3)
            Require(Stages.EnumerateObject().Where(p => p.Value.GetProperty("barrier_type").GetInt32() != 3).All(p => s.Cleared.Contains(long.Parse(p.Name))), "Pinball challenge needs every normal stage");
        return row;
    }

    private bool Apply(State s, long stage, long score)
    {
        var row = Check(s, stage);
        var before = JsonSerializer.Serialize(s);
        if (row.GetProperty("barrier_type").GetInt32() != 3)
        {
            if (!s.Cleared.Contains(stage)) s.Cleared.Add(stage);
            var skill = row.GetProperty("skill_unlock").GetInt64();
            if (skill != 0 && !s.Skills.Contains(skill)) s.Skills.Add(skill);
            s.Level = Math.Max(s.Level, row.GetProperty("lv_up").GetInt32());
        }
        else s.Score = Math.Max(s.Score, score);
        return before != JsonSerializer.Serialize(s);
    }

    private static Dictionary<string, object?> Payload(State s) => new()
    {
        ["hai_la_level"] = (uint)s.Level, ["stage_list"] = s.Cleared.OrderBy(x => x).Select(x => (uint)x).ToArray(),
        ["skills"] = s.Skills.OrderBy(x => x).Select(x => (uint)x).ToArray(), ["select_active_skills"] = s.Equip.Select(x => (uint)x).ToArray(), ["challenge_score"] = (uint)s.Score
    };

    public async Task<(List<GameResponse>, IReadOnlyList<(long, long)>)> SettleAsync(ModeBattle b, CancellationToken ct)
    {
        if (!b.Win) return ([], []);
        var s = await LoadAsync(b.Uid, ct);
        if (!Apply(s, StageOfBattle(b.StageId)!.Value, 0)) return ([], []);
        await Accounts.SetJsonAsync(b.Uid, "pinball_v1", s, ct);
        var pinFrames = new List<GameResponse> { Push(b.Srv, 89401, Payload(s)) };
        await TaskAsync(pinFrames, b, 392001, StageOfBattle(b.StageId)!.Value);
        return (pinFrames, []);
    }

    public async Task<IReadOnlyList<(uint, byte[])>> LoginFramesAsync(ProtoCodec codec, long uid, CancellationToken ct) => [Frame(89401, Payload(await LoadAsync(uid, ct)))];

    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, uint cmd, CancellationToken ct)
    {
        try
        {
            var s = await LoadAsync(r.AccountId, ct);
            var frames = new List<GameResponse>();
            if (cmd == 89402)
            {
                var stage = (long)HandlerHelpers.ReadUInt(r.Body, "stage_id");
                Require(Stages.TryGetProperty(stage.ToString(), out var row) && row.GetProperty("barrier_type").GetInt32() != 2, "Pinball stage settles through battle");
                if (Apply(s, stage, HandlerHelpers.ReadUInt(r.Body, "score"))) frames.Add(Push((ushort)(r.ServerIndex + 1), 89401, Payload(s)));
            }
            else
            {
                var skills = HandlerHelpers.ReadIdList(r.Body, "skill_list");
                Require(skills.Count <= Cfg.GetProperty("active_slots").GetInt32() && skills.Distinct().Count() == skills.Count
                    && skills.All(k => s.Skills.Contains(k) && Cfg.GetProperty("skills").GetProperty(k.ToString()).GetProperty("skill_type").GetInt32() == 1), "Invalid pinball skills");
                s.Equip = skills.ToList();
            }
            await Accounts.SetJsonAsync(r.AccountId, "pinball_v1", s, ct);
            frames.Add(HandlerHelpers.ResultOnly(Codec, r, cmd + 1));
            return frames;
        }
        catch (ModeError) { return [HandlerHelpers.ResultOnly(Codec, r, cmd + 1, 2)]; }
    }
}
