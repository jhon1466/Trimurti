using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>
/// AI chips (port of Alpha-Persei chip_service.py) on the client ChipCfg / ConditionCfg export (Data/Configs/chip_unlock_cfg.json).
/// 50002 unlock: type 1 Mimir kernels and type 2 secondary chips by their new_condition (1014 player level, 1011 stage cleared),
/// type 3 hero AI chips (owned host + hero condition 1015 level / 1026 star / 1025 skill attributes / 1027 affection + costs),
/// type 4 support chips in their configured order with their module conditions; 50018 equip a hero AI chip in its role slot;
/// 50008 / 50010 / 50012 / 50014 Mimir proposals (≤ 2 unlocked secondary chips, one per character). Login frame 50001.
/// </summary>
public sealed class ChipSystem(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec)
{
    private JsonElement Cfg => config.Get("chip_unlock_cfg.json") ?? throw new ModeError("Missing chip_unlock_cfg.json");
    private static void Require(bool ok, string m) { if (!ok) throw new ModeError(m); }
    private static List<long> L(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt64()).ToList() : [];

    public sealed class Proposal { public string Name { get; set; } = ""; public List<uint> Chips { get; set; } = []; }
    public sealed class State
    {
        public SortedSet<uint> Kernel { get; set; } = [];
        public SortedSet<uint> Secondary { get; set; } = [];
        public SortedSet<uint> Hero { get; set; } = [];
        public SortedSet<uint> Reviser { get; set; } = [];
        public Dictionary<long, uint[]> HeroSlots { get; set; } = [];
        public Dictionary<uint, Proposal> Proposals { get; set; } = [];
        public Dictionary<uint, uint> KernelProposal { get; set; } = [];
    }

    private async Task<State> LoadAsync(long uid, CancellationToken ct) => await accounts.GetJsonAsync<State>(uid, "chips_v1", ct) ?? new State();

    private byte[] Frame(State s) => codec.Encode("sc_50001", new Dictionary<string, object?>
    {
        ["unlock_kernel_chip"] = s.Kernel.ToArray(), ["unlock_secondary_chip"] = s.Secondary.ToArray(), ["unlock_hero_chip"] = s.Hero.ToArray(),
        ["proposals"] = s.Proposals.OrderBy(p => p.Key).Select(p => (object)new Dictionary<string, object?> { ["id"] = p.Key, ["name"] = p.Value.Name, ["secondary"] = p.Value.Chips.ToArray() }).ToArray(),
        ["hero_chip_state"] = s.HeroSlots.OrderBy(h => h.Key).Select(h => (object)new Dictionary<string, object?> { ["hero_id"] = (uint)h.Key, ["secondary"] = h.Value }).ToArray(),
        ["unlock_reviser_chip"] = s.Reviser.ToArray()
    });

    public async Task<(uint, byte[])> LoginFrameAsync(long uid, CancellationToken ct) => (50001, Frame(await LoadAsync(uid, ct)));

    private async Task<bool> ConditionAsync(long uid, long id, CancellationToken ct)
    {
        if (id == 0) return true;
        JsonElement c;
        if (Cfg.GetProperty("conditions").TryGetProperty(id.ToString(), out var cc)) c = cc;
        else if (config.Get("condition_cfg.json") is { } all && all.TryGetProperty(id.ToString(), out var gc)) c = gc;
        else return false;
        var p = L(c.GetProperty("params"));
        switch (c.GetProperty("type").GetInt32())
        {
            case 1014: return ((await accounts.GetProfileAsync(uid, ct))?.Level ?? 1) >= p[0];
            case 1011: return (await accounts.GetLobbySnapshotAsync(uid, ct)).Chapters.Any(x => x.Id == p[0] && x.ClearTimes > 0);
            case 1015 or 1025 or 1026 or 1027:
                var h = await accounts.GetHeroRowAsync(uid, p[0], ct);
                if (h is null) return false;
                return c.GetProperty("type").GetInt32() switch
                {
                    1015 => h.Level >= p[1],
                    1026 => h.Star >= p[1],
                    1025 => h.Intensify.Sum(x => x[1]) >= p[1],
                    _ => (await accounts.GetArchiveAsync(uid, config.Get("trust_cfg.json") is { } t && t.GetProperty("hero_to_archive").TryGetProperty(p[0].ToString(), out var a) ? a.GetInt64() : p[0], ct)).Exp
                         >= L(Cfg.GetProperty("love_level_exp")).Take((int)p[1] - 1).Sum()
                };
            default: return false;
        }
    }

    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, uint cmd, CancellationToken ct)
    {
        var uid = r.AccountId;
        var b = r.Body;
        var srv = (ushort)(r.ServerIndex + 1);
        uint U(string k) => HandlerHelpers.ReadUInt(b, k);
        var s = await LoadAsync(uid, ct);
        var costs = new List<(long, long)>();
        try
        {
            switch (cmd)
            {
                case 50002:
                    {
                        var id = U("id");
                        Require(Cfg.GetProperty("chips").TryGetProperty(id.ToString(), out var c), "Unknown chip");
                        var type = c.GetProperty("type_id").GetInt32();
                        var set = type switch { 1 => s.Kernel, 2 => s.Secondary, 3 => s.Hero, _ => s.Reviser };
                        if (set.Contains(id)) break;
                        if (type == 4)
                        {
                            var seq = Cfg.GetProperty("reviser_sequences").TryGetProperty(c.GetProperty("base_id").GetInt64().ToString(), out var sq) ? L(sq) : [];
                            Require(seq.FirstOrDefault(x => !s.Reviser.Contains((uint)x)) == id, "Previous support chip locked");
                            Require(await accounts.GetHeroRowAsync(uid, c.GetProperty("host_id").GetInt64(), ct) is not null, "Host hero not owned");
                            foreach (var m in L(c.GetProperty("module_conditions"))) Require(await ConditionAsync(uid, m, ct), "Support chip condition not met");
                        }
                        else
                        {
                            if (type == 3) Require(await accounts.GetHeroRowAsync(uid, c.GetProperty("host_id").GetInt64(), ct) is not null, "Host hero not owned");
                            Require(await ConditionAsync(uid, c.GetProperty("new_condition").GetInt64(), ct), "Chip condition not met");
                        }
                        costs.AddRange(c.GetProperty("costs").EnumerateArray().Select(x => (x[0].GetInt64(), x[1].GetInt64())));
                        set.Add(id);
                        break;
                    }
                case 50018:
                    {
                        var hero = (long)U("hero_id");
                        var slot = (int)U("slot_id");
                        var chip = U("secondary");
                        Require(slot is >= 1 and <= 4 && await accounts.GetHeroRowAsync(uid, hero, ct) is not null, "Bad slot");
                        if (chip != 0)
                        {
                            Require(s.Hero.Contains(chip) && Cfg.GetProperty("chips").TryGetProperty(chip.ToString(), out var c), "Chip locked");
                            var cc = Cfg.GetProperty("chips").GetProperty(chip.ToString());
                            Require(cc.GetProperty("spec_char").GetInt64() == hero && cc.GetProperty("role_type_id").GetInt32() == slot, "Chip does not fit this slot");
                        }
                        if (!s.HeroSlots.TryGetValue(hero, out var slots)) s.HeroSlots[hero] = slots = new uint[4];
                        slots[slot - 1] = chip;
                        break;
                    }
                case 50008:
                    {
                        var id = U("id");
                        var name = HandlerHelpers.ReadString(b, "name");
                        var chips = HandlerHelpers.ReadIdList(b, "secondary").Select(x => (uint)x).ToList();
                        Require(id is >= 1 and <= 20 && name.Length <= 32 && chips.Count <= 2 && chips.Distinct().Count() == chips.Count, "Bad proposal");
                        var chars = new HashSet<long>();
                        foreach (var c in chips)
                        {
                            Require(Cfg.GetProperty("chips").TryGetProperty(c.ToString(), out var cc) && cc.GetProperty("type_id").GetInt32() == 2, "Not a secondary chip");
                            var ch = cc.GetProperty("spec_char").GetInt64();
                            Require(ch == 0 || chars.Add(ch), "Two chips of the same character");
                        }
                        s.Proposals[id] = new Proposal { Name = name, Chips = chips };
                        break;
                    }
                case 50010:
                    Require(s.Proposals.Remove(U("id")), "Proposal not found");
                    break;
                case 50012:
                    Require(s.Proposals.TryGetValue(U("id"), out var pr), "Proposal not found");
                    pr!.Name = HandlerHelpers.ReadString(b, "name");
                    break;
                case 50014:
                    {
                        var kernel = U("kernel_chip_id");
                        var pid = U("proposal_id");
                        Require(s.Kernel.Contains(kernel) && s.Proposals.TryGetValue(pid, out var p) && p.Chips.All(s.Secondary.Contains), "Proposal not usable");
                        s.KernelProposal[kernel] = pid;
                        break;
                    }
            }
        }
        catch (ModeError) { return [HandlerHelpers.ResultOnly(codec, r, cmd + 1, 2)]; }
        if (costs.Count > 0 && !await accounts.ConsumeAndGrantAsync(uid, costs, [], ct)) return [HandlerHelpers.ResultOnly(codec, r, cmd + 1, 2)];
        await accounts.SetJsonAsync(uid, "chips_v1", s, ct);
        var frames = await InventorySync.FramesAsync(accounts, codec, r, costs.Select(x => x.Item1), ct);
        frames.Add(new GameResponse(50001, 0, srv, Frame(s)));
        frames.Add(HandlerHelpers.ResultOnly(codec, r, cmd + 1));
        return frames;
    }

    public static readonly uint[] Commands = [50002, 50008, 50010, 50012, 50014, 50018];
}
