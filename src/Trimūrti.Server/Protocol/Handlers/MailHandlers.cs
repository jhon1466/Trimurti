using System.Text.Json;

namespace Trimūrti.Server.Protocol.Handlers;

/// <summary>
/// 邮件 and 修正者信件 (port of Alpha-Persei mail_service.py, flags as in Hoshimi-Public communications.py) on the client
/// MailTemplateCfg / MailSpecialLetterCfg (→ Data/Configs/mail_cfg.json).
///  - client flags: read_flag 1 unread / 2 read, attach_flag 0 none / 1 pending / 2 claimed; expired mails are hidden unless
///    collected; collected mails leave the inbox (30020 list, 30022 detail).
///  - 30004 claim one / all pending attachments (heroes, sigils … as entities), 30006 delete only read, claimed, not collected,
///    30014 collect (opt 1, attachments must be claimed first) / uncollect (0), 30008 detail marks read.
///  - letters: a hero's letter (MailSpecialLetterCfg) arrives once its hero is owned and its send date has passed, with its
///    template mail and rewards; 30017 list at login, 30018 read (0 = all). 30001 brief after every change.
/// </summary>
public sealed class MailSystem(GameAccountRepository accounts, SourceGameConfig config, ProtoCodec codec)
{
    private JsonElement Cfg => config.Get("mail_cfg.json") ?? throw new ModeError("Missing mail_cfg.json");

    public sealed class Letters { public List<uint> Sent { get; set; } = []; public List<uint> Viewed { get; set; } = []; }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static bool Visible(MailSummary m, long collected) => collected > 0 || m.ExpireTime == 0 || m.ExpireTime > Now;

    private static object Attach(MailSummary m) => m.Attachments.Select(a => (object)new Dictionary<string, object?> { ["id"] = (uint)a.Id, ["number"] = (uint)a.Count }).ToArray();
    private static uint ReadFlag(MailSummary m) => m.ReadFlag == 1 ? 2u : 1u;
    private static uint AttachFlag(MailSummary m) => m.Attachments.Count == 0 ? 0u : m.AttachFlag == 1 ? 2u : 1u;

    private async Task<(List<MailSummary> Inbox, List<(MailSummary M, long At)> Collected)> MailsAsync(long uid, CancellationToken ct)
    {
        var all = await accounts.GetMailsAsync(uid, ct);
        var collect = await accounts.GetMailCollectTimesAsync(uid, ct);
        var inbox = all.Where(m => collect.GetValueOrDefault(m.Id) == 0 && Visible(m, 0)).ToList();
        var col = all.Where(m => collect.GetValueOrDefault(m.Id) > 0).Select(m => (m, collect[m.Id])).ToList();
        return (inbox, col);
    }

    public async Task<byte[]> BriefAsync(long uid, CancellationToken ct)
    {
        var (inbox, _) = await MailsAsync(uid, ct);
        return codec.Encode("sc_30001", new Dictionary<string, object?> { ["unread_number"] = (uint)inbox.Count(m => m.ReadFlag != 1), ["total_number"] = (uint)inbox.Count });
    }

    /// <summary>Delivers due hero letters (once) as template mails.</summary>
    private async Task<Letters> DeliverLettersAsync(long uid, CancellationToken ct)
    {
        var st = await accounts.GetJsonAsync<Letters>(uid, "letters_v1", ct) ?? new Letters();
        var owned = (await accounts.GetOwnedHeroIdsAsync(uid, ct)).ToHashSet();
        var archives = config.Get("trust_cfg.json") is { } t ? t.GetProperty("hero_to_archive") : default;
        var ownedArch = owned.Select(h => archives.ValueKind == JsonValueKind.Object && archives.TryGetProperty(h.ToString(), out var a) ? a.GetInt64() : h).ToHashSet();
        var today = GameClock.Today;
        var changed = false;
        foreach (var p in Cfg.GetProperty("letters").EnumerateObject())
        {
            var id = uint.Parse(p.Name);
            if (st.Sent.Contains(id)) continue;
            var hero = p.Value.GetProperty("hero").GetInt64();
            if (!owned.Contains(hero) && !ownedArch.Contains(hero)) continue;
            var due = p.Value.GetProperty("dates").EnumerateArray().Select(d => d.GetString()!.Split(',').Select(int.Parse).ToArray())
                .Any(d => d.Length == 3 && new DateTime(d[0], d[1], d[2]) <= today);
            if (!due) continue;
            var tplId = p.Value.GetProperty("mail").GetInt32();
            var tpl = Cfg.GetProperty("templates").TryGetProperty(tplId.ToString(), out var tp) ? tp : default;
            var rewards = p.Value.GetProperty("reward").EnumerateArray().Select(x => (x[0].GetInt64(), x[1].GetInt64())).ToList();
            await accounts.SendTemplateMailAsync(uid, tplId,
                tpl.ValueKind == JsonValueKind.Object ? tpl.GetProperty("title").GetString() ?? "" : p.Value.GetProperty("title").GetString() ?? "",
                p.Value.GetProperty("sender").GetString() ?? "", tpl.ValueKind == JsonValueKind.Object ? tpl.GetProperty("desc").GetString() ?? "" : "", rewards, ct);
            st.Sent.Add(id);
            changed = true;
        }
        if (changed) await accounts.SetJsonAsync(uid, "letters_v1", st, ct);
        return st;
    }

    public async Task<IReadOnlyList<(uint, byte[])>> LoginFramesAsync(long uid, CancellationToken ct)
    {
        var st = await DeliverLettersAsync(uid, ct);
        return
        [
            (30017, codec.Encode("sc_30017", new Dictionary<string, object?> { ["letter_list"] = st.Sent.Order().Select(id => (object)new Dictionary<string, object?> { ["id"] = id, ["is_viewed"] = st.Viewed.Contains(id) }).ToArray() })),
            (30001, await BriefAsync(uid, ct))
        ];
    }

    private static object Detail(long id, MailDetail d) => new Dictionary<string, object?>
    {
        ["id"] = (ulong)id, ["content_list"] = new object[] { new Dictionary<string, object?> { ["content_type"] = 2u, ["text"] = d.Content } },
        ["attachment_list"] = d.Attachments.Select(a => (object)new Dictionary<string, object?> { ["id"] = (uint)a.Id, ["number"] = (uint)a.Count }).ToArray(),
        ["sender"] = d.Sender, ["mail_template_id"] = (uint)d.TemplateId, ["sender_format"] = Array.Empty<string>(), ["content_format"] = Array.Empty<object>(),
        ["i18n_info"] = Array.Empty<object>(), ["link_param"] = Array.Empty<uint>()
    };

    private static object CollectRec(MailSummary m, long at) => new Dictionary<string, object?>
    {
        ["id"] = (ulong)m.Id, ["collect_date"] = (uint)at, ["title"] = m.Title, ["mail_template_id"] = (uint)m.TemplateId,
        ["title_format"] = Array.Empty<object>(), ["i18n_info"] = Array.Empty<object>(), ["mail_timestamp"] = (uint)m.SendTime
    };

    public async Task<IReadOnlyList<GameResponse>> HandleAsync(GameRequest r, uint cmd, CancellationToken ct)
    {
        var uid = r.AccountId;
        var srv = (ushort)(r.ServerIndex + 1);
        var id = (long)(HandlerHelpers.ReadULong(r.Body, "id") is var x and > 0 ? x : HandlerHelpers.ReadULong(r.Body, "mail_id"));
        var (inbox, collected) = await MailsAsync(uid, ct);
        var frames = new List<GameResponse>();
        Dictionary<string, object?> reply;
        switch (cmd)
        {
            case 30002:
                await DeliverLettersAsync(uid, ct);
                (inbox, _) = await MailsAsync(uid, ct);
                reply = new()
                {
                    ["mail_list"] = inbox.Select(m => (object)new Dictionary<string, object?>
                    {
                        ["id"] = (ulong)m.Id, ["date"] = (uint)m.SendTime, ["title"] = m.Title, ["attach_flag"] = AttachFlag(m), ["read_flag"] = ReadFlag(m),
                        ["attachment_list"] = Attach(m), ["timeout_timestamp"] = (uint)m.ExpireTime, ["star_state"] = 0u, ["mail_template_id"] = (uint)m.TemplateId,
                        ["title_format"] = Array.Empty<object>(), ["i18n_info"] = Array.Empty<object>()
                    }).ToArray(),
                    ["total_num"] = (uint)inbox.Count
                };
                break;
            case 30020:
                reply = new() { ["collect_total_num"] = (uint)collected.Count, ["collect_mail_list"] = collected.Select(c => CollectRec(c.M, c.At)).ToArray() };
                break;
            case 30008:
            case 30022:
                {
                    var visible = inbox.Any(m => m.Id == id) || collected.Any(c => c.M.Id == id);
                    var d = visible ? await accounts.GetMailDetailAsync(uid, id, ct) : null;
                    if (d is null) return [HandlerHelpers.ResultOnly(codec, r, cmd + 1, 2)];
                    reply = new() { ["result"] = 0u, ["detail_info"] = Detail(id, d) };
                    break;
                }
            case 30004:
                {
                    var targets = inbox.Where(m => (id == 0 || m.Id == id) && AttachFlag(m) == 1).ToList();
                    if (targets.Count == 0) return [HandlerHelpers.ResultOnly(codec, r, 30005, 2)];
                    var got = new List<(long, long)>();
                    var ok = new List<long>();
                    foreach (var m in targets)
                    {
                        var res = await accounts.ClaimMailAsync(uid, m.Id, ct);
                        if (!res.Success) continue;
                        ok.AddRange(res.ClaimedIds);
                        got.AddRange(res.Attachments);
                    }
                    frames.AddRange(await InventorySync.FramesAsync(accounts, codec, r, got.Select(g => g.Item1), ct));
                    reply = new() { ["result"] = 0u, ["attachment_list"] = HandlerHelpers.ToItemNetRecList(got.GroupBy(g => g.Item1).Select(g => (g.Key, g.Sum(v => v.Item2)))), ["success_mail_ids"] = ok.Select(v => (ulong)v).ToArray() };
                    break;
                }
            case 30006:
                {
                    var targets = inbox.Where(m => (id == 0 || m.Id == id) && m.ReadFlag == 1 && AttachFlag(m) != 1).Select(m => m.Id).ToList();
                    if (id != 0 && targets.Count == 0) return [HandlerHelpers.Reply(codec, r, 30007, new Dictionary<string, object?> { ["id_list"] = Array.Empty<ulong>(), ["no_need_data"] = new[] { (ulong)id } })];
                    var deleted = await accounts.DeleteFinishedMailsAsync(uid, targets, ct);
                    reply = new() { ["id_list"] = deleted.Select(v => (ulong)v).ToArray(), ["no_need_data"] = Array.Empty<ulong>() };
                    break;
                }
            case 30014:
                {
                    var opt = HandlerHelpers.ReadUInt(r.Body, "opt");
                    var m = inbox.FirstOrDefault(v => v.Id == id) ?? collected.Select(c => c.M).FirstOrDefault(v => v.Id == id);
                    if (m is null || opt > 1 || (opt == 1 && AttachFlag(m) == 1)) return [HandlerHelpers.ResultOnly(codec, r, 30015, 2)];
                    await accounts.SetMailCollectAsync(uid, id, opt == 1, ct);
                    reply = new() { ["result"] = 0u, ["collect_mail"] = CollectRec(m, opt == 1 ? Now : 0) };
                    break;
                }
            case 30018:
                {
                    var st = await accounts.GetJsonAsync<Letters>(uid, "letters_v1", ct) ?? new Letters();
                    var lid = HandlerHelpers.ReadUInt(r.Body, "id");
                    if (lid != 0 && !st.Sent.Contains(lid)) return [HandlerHelpers.ResultOnly(codec, r, 30019, 2)];
                    foreach (var l in lid == 0 ? st.Sent : [lid]) if (!st.Viewed.Contains(l)) st.Viewed.Add(l);
                    await accounts.SetJsonAsync(uid, "letters_v1", st, ct);
                    reply = new() { ["result"] = 0u };
                    break;
                }
            default:
                return [HandlerHelpers.ResultOnly(codec, r, cmd + 1, 2)];
        }
        if (cmd is 30004 or 30006 or 30008 or 30014) frames.Add(new GameResponse(30001, 0, srv, await BriefAsync(uid, ct)));
        frames.Add(HandlerHelpers.Reply(codec, r, cmd + 1, reply));
        return frames;
    }

    public static readonly uint[] Commands = [30002, 30004, 30006, 30008, 30014, 30018, 30020, 30022];
}
