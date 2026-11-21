using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

var builder = WebApplication.CreateBuilder(args);
// TRIMURTI_LOG_DIR: set by Trimūrti GM Tools when it runs the compiled server from its managed folder.
var liveLogPath = Environment.GetEnvironmentVariable("TRIMURTI_LOG_DIR") is { Length: > 0 } logDir
    ? Path.GetFullPath(Path.Combine(logDir, "trimurti-live.log"))
    : Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "logs", "trimurti-live.log"));
builder.Logging.AddProvider(new Trimūrti.Server.LiveFileLoggerProvider(liveLogPath));
builder.WebHost.ConfigureKestrel(options =>
{
    var httpPort = int.TryParse(Environment.GetEnvironmentVariable("TRIMURTI_HTTP_PORT"), out var hp) ? hp : 5180;
    options.ListenLocalhost(httpPort);
    if (httpPort != 5180) options.ListenLocalhost(5180);

    if (int.TryParse(Environment.GetEnvironmentVariable("TRIMURTI_HTTPS_PORT"), out var httpsPort) && httpsPort > 0)
    {
        var pfxPath = Environment.GetEnvironmentVariable("TRIMURTI_CERT_PFX");
        X509Certificate2 pfx;
        if (!string.IsNullOrWhiteSpace(pfxPath) && File.Exists(pfxPath))
        {
            pfx = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, Environment.GetEnvironmentVariable("TRIMURTI_CERT_PASSWORD"));
        }
        else
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=TrimurtiLocal", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            san.AddDnsName("*.ys4fun.com");
            san.AddDnsName("*.yongshigame.com");
            san.AddDnsName("*.yostar.net");
            san.AddIpAddress(System.Net.IPAddress.Loopback);
            req.CertificateExtensions.Add(san.Build());
            var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
            pfx = X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
        }
        options.ListenLocalhost(httpsPort, listen => listen.UseHttps(pfx));
    }
});
builder.Services.AddSingleton<Trimūrti.Server.AccountStore>();
builder.Services.AddSingleton<Trimūrti.Server.GameAccountRepository>();
builder.Services.AddSingleton<Trimūrti.Server.SourceGameConfig>();
builder.Services.AddSingleton<Trimūrti.Server.ClientBuildCatalog>();
builder.Services.AddSingleton<Trimūrti.Server.LoginBootstrap>();
builder.Services.AddSingleton<Trimūrti.Server.GameTcpGateway>();
builder.Services.AddSingleton<Trimūrti.Server.GatewayTcpService>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.ProtoSchema>(_ => Trimūrti.Server.Protocol.ProtoSchema.Load(Path.Combine(AppContext.BaseDirectory, "Data", "lua_proto_messages.json")));
builder.Services.AddSingleton<Trimūrti.Server.Protocol.SchemaDefaults>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.ProtoCodec>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.FallbackCatalog>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.BattleSessionStore>();
builder.Services.AddSingleton<Trimūrti.Server.BattleResultStore>();
builder.Services.AddSingleton<Trimūrti.Server.TrialHeroes>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.BossChallenge>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.Mythic>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.CoreVerification>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.EquipSeizure>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.CoreChallenge>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.WarChess>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.Polyhedron>();
// Event combat modes (own settlement + login state).
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.AshMode>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.OsirisMode>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.SkuldMode>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.IStageMode>(sp => sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.AshMode>());
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.IStageMode>(sp => sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.OsirisMode>());
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.AbyssMode>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.IStageMode>(sp => sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.AbyssMode>());
foreach (var c in Trimūrti.Server.Protocol.Handlers.AbyssMode.Commands)
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ModeCommandHandler(c, sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.AbyssMode>().HandleAsync));
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.PlayerFrames>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.IStageMode>(sp => sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.PlayerFrames>());
foreach (var c in Trimūrti.Server.Protocol.Handlers.PlayerFrames.Commands)
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ModeCommandHandler(c, sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.PlayerFrames>().HandleAsync));
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.SystemStates>();
foreach (var c in Trimūrti.Server.Protocol.Handlers.SystemStates.Commands)
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ModeCommandHandler(c, sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.SystemStates>().HandleAsync));
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.MatrixMode>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.IStageMode>(sp => sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.MatrixMode>());
foreach (var c in Trimūrti.Server.Protocol.Handlers.MatrixMode.Commands)
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ModeCommandHandler(c, sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.MatrixMode>().HandleAsync));
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.IStageMode>(sp => sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.SkuldMode>());
foreach (var t in new[] { typeof(Trimūrti.Server.Protocol.Handlers.PubMode), typeof(Trimūrti.Server.Protocol.Handlers.HodurMode), typeof(Trimūrti.Server.Protocol.Handlers.PinballMode), typeof(Trimūrti.Server.Protocol.Handlers.SpheroMode), typeof(Trimūrti.Server.Protocol.Handlers.QuanzhouMode), typeof(Trimūrti.Server.Protocol.Handlers.HeartDemon), typeof(Trimūrti.Server.Protocol.Handlers.RogueTeam), typeof(Trimūrti.Server.Protocol.Handlers.SummerBoard) })
{
    builder.Services.AddSingleton(t);
    builder.Services.AddSingleton(typeof(Trimūrti.Server.Protocol.Handlers.IStageMode), sp => sp.GetRequiredService(t));
}
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.StageModes>();
foreach (var c in new uint[] { 89002, 89004, 89006, 89008 })
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ModeCommandHandler(c, sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.PubMode>().HandleAsync));
foreach (var c in new uint[] { 89422, 89424, 89426 })
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ModeCommandHandler(c, sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.HodurMode>().HandleAsync));
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ModeCommandHandler(79602, sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.QuanzhouMode>().HandleAsync));
foreach (var c in new uint[] { 75002, 75020, 75022, 75026, 75028 })
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ModeCommandHandler(c, sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.HeartDemon>().HandleAsync));
foreach (var c in new uint[] { 88004, 88008, 88024, 88030, 88100, 88102, 88200, 88202, 88208, 88212, 88218, 88222, 88224, 88300, 88306, 88312, 88316 })
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ModeCommandHandler(c, sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.RogueTeam>().HandleAsync));
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.AutoChess>();
foreach (var c in new uint[] { 89102, 89104, 89108, 89116, 89118, 89120, 89122, 89126, 89128, 89130, 89134, 89138, 89140, 89142, 89144, 89146, 89148, 89206, 89208, 89212 })
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ModeCommandHandler(c, sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.AutoChess>().HandleAsync));
foreach (var c in new uint[] { 89402, 89404 })
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ModeCommandHandler(c, sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.PinballMode>().HandleAsync));
foreach (var c in new uint[] { 24052, 24054 })
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ModeCommandHandler(c, sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.SkuldMode>().HandleAsync));
foreach (var c in new uint[] { 18010, 18012, 18014, 18016, 18018, 18020, 18022, 18024, 18026, 18028, 18030, 18032, 18034, 66000, 66002, 66004, 66034, 66036, 66038 })
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.PolyhedronHandler(sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.Polyhedron>(), sp.GetRequiredService<Trimūrti.Server.GameAccountRepository>(), sp.GetRequiredService<Trimūrti.Server.Protocol.ProtoCodec>(), c));
foreach (var c in new uint[] { 49002, 49004, 49006, 49008, 49016, 49024 })
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.WarChessHandler(sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.WarChess>(), sp.GetRequiredService<Trimūrti.Server.Protocol.ProtoCodec>(), c));
foreach (var c in new uint[] { 89014, 89026 })
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.CoreChallengeAffixHandler(sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.CoreChallenge>(), sp.GetRequiredService<Trimūrti.Server.Protocol.ProtoCodec>(), c));
foreach (var c in new uint[] { 89018, 89030, 89434, 89804 })
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.CoreChallengeRewardHandler(sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.CoreChallenge>(), sp.GetRequiredService<Trimūrti.Server.GameAccountRepository>(), sp.GetRequiredService<Trimūrti.Server.Protocol.ProtoCodec>(), c));
foreach (var c in new uint[] { 89016, 89022, 89028, 89034, 89432, 89436, 89802, 89806 })
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.CoreChallengeResetHandler(sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.CoreChallenge>(), sp.GetRequiredService<Trimūrti.Server.Protocol.ProtoCodec>(), c));
foreach (var c in new uint[] { 89036, 89038, 89438 })
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.CoreChallengeTipHandler(sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.CoreChallenge>(), sp.GetRequiredService<Trimūrti.Server.Protocol.ProtoCodec>(), c));
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.EquipSeizureRewardHandler>();
builder.Services.AddHostedService<Trimūrti.Server.BattleUdpServer>();
foreach (var silentCommand in Trimūrti.Server.Protocol.Handlers.NoReplyHandler.Commands)
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(_ => new Trimūrti.Server.Protocol.Handlers.NoReplyHandler(silentCommand));
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.RefreshTimeHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.ServerTimestampHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.DailySignHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.HeartbeatHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.PingTimestampHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.ClientLogUploadHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.MonthCardClaimHandler>();
// Hero / equip / oath extras (ported from Alpha-Persei-Cluster)
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.HeroHeartRateHandler>();
// Backhome / dorm (ported from Alpha-Persei-Cluster); other 58xxx use the schema-complete fallback
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.BackHome>();
foreach (var c in Trimūrti.Server.Protocol.Handlers.BackHome.Commands)
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ModeCommandHandler(c, sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.BackHome>().HandleAsync));
// Profile / personalization / social (ported from Alpha-Persei-Cluster)
foreach (var profileAck in Trimūrti.Server.Protocol.Handlers.ProfileAcks.Commands)
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.SimpleAckHandler(profileAck, sp.GetRequiredService<Trimūrti.Server.Protocol.ProtoCodec>()));
foreach (var (decoCommand, decoField, decoColumn) in new[] { (32114u, "id", "cur_card_bg"), (32120u, "chat_bubble", "cur_bubble") })
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ProfileDecorationHandler(decoCommand, decoField, decoColumn,
        sp.GetRequiredService<Trimūrti.Server.GameAccountRepository>(), sp.GetRequiredService<Trimūrti.Server.Protocol.ProtoCodec>()));
foreach (var (overdueCommand, overdueField) in new[] { (32062u, "icon_list"), (32066u, "chat_bubble_list") })
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.OverdueQueryHandler(overdueCommand, overdueField, sp.GetRequiredService<Trimūrti.Server.Protocol.ProtoCodec>()));
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.ProfileTagsHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.SimpleForeignInfoHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.ForeignHeroInfoHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.StickerSuitRewardHandler>();
// Shop / payment / newbie / battle pass (ported from Alpha-Persei-Cluster)
foreach (var ackCommand in Trimūrti.Server.Protocol.Handlers.SimpleAckHandler.Commands)
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.SimpleAckHandler(ackCommand, sp.GetRequiredService<Trimūrti.Server.Protocol.ProtoCodec>()));
foreach (var noobCommand in Trimūrti.Server.Protocol.Handlers.NewbieRewardHandler.Commands)
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.NewbieRewardHandler(noobCommand,
        sp.GetRequiredService<Trimūrti.Server.GameAccountRepository>(), sp.GetRequiredService<Trimūrti.Server.SourceGameConfig>(), sp.GetRequiredService<Trimūrti.Server.Protocol.ProtoCodec>()));
foreach (var (rechargeCommand, rechargeField) in new[] { (34012u, "receive_total_recharge_list"), (34118u, "receive_time_limit_recharge_list") })
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.RechargeBonusHandler(rechargeCommand, rechargeField,
        sp.GetRequiredService<Trimūrti.Server.GameAccountRepository>(), sp.GetRequiredService<Trimūrti.Server.SourceGameConfig>(), sp.GetRequiredService<Trimūrti.Server.Protocol.ProtoCodec>()));
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.SetBgmHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.SetBirthdayHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.ActivitySignHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.DailyFatigueDessertHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.FlowerToDiamondHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.AccumulateSignRewardHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.LocalPayOrderHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.BattlePassInfoHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.BattlePassClaimHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.BattlePassClaimAllHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.BigMonthCardSignHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.BigMonthCardBuyHandler>();

builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.ClientVersionHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.StoryProgressHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.MainGuideHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.WeakGuideHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.NicknameChangeHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.HeroAddFavoriteHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.HeroRemoveFavoriteHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.BattleStartHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.BattleQuickStartHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.BattleSettleHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.SweepHandler>();
// 迭代校验 (core verification, regular mode)
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.CoreVerificationRewardHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.CoreVerificationResetHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.CoreVerificationAffixHandler>();
// 黑区净化 (mythic)
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.MythicQueryHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.MythicStarRewardHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.MythicDifficultyHandler>();
foreach (var mythicMutation in Trimūrti.Server.Protocol.Handlers.MythicFinalMutationHandler.Commands)
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.MythicFinalMutationHandler(mythicMutation,
        sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.Mythic>(), sp.GetRequiredService<Trimūrti.Server.Protocol.ProtoCodec>()));
foreach (var mythicReward in Trimūrti.Server.Protocol.Handlers.MythicFinalRewardHandler.Commands)
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.MythicFinalRewardHandler(mythicReward,
        sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.Mythic>(), sp.GetRequiredService<Trimūrti.Server.GameAccountRepository>(), sp.GetRequiredService<Trimūrti.Server.Protocol.ProtoCodec>()));
// 梦境再构 (boss challenge)
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.BossSelectModeHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.BossAffixHandler>();
foreach (var bossRewardCommand in Trimūrti.Server.Protocol.Handlers.BossRewardHandler.Commands)
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.BossRewardHandler(bossRewardCommand,
        sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.BossChallenge>(), sp.GetRequiredService<Trimūrti.Server.GameAccountRepository>(), sp.GetRequiredService<Trimūrti.Server.Protocol.ProtoCodec>()));
foreach (var bossResetCommand in Trimūrti.Server.Protocol.Handlers.BossResetHandler.Commands)
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.BossResetHandler(bossResetCommand,
        sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.BossChallenge>(), sp.GetRequiredService<Trimūrti.Server.GameAccountRepository>(), sp.GetRequiredService<Trimūrti.Server.Protocol.ProtoCodec>()));
foreach (var bossTeamCommand in Trimūrti.Server.Protocol.Handlers.BossTeamHandler.Commands)
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.BossTeamHandler(bossTeamCommand,
        sp.GetRequiredService<Trimūrti.Server.GameAccountRepository>(), sp.GetRequiredService<Trimūrti.Server.Protocol.ProtoCodec>()));
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.BattleActivitySettleHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.ChapterTreasureHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.CurrencyExchangeHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.UseItemHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.TaskSystem>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.MiniGames>();
foreach (var c in new uint[] { 60054, 85102, 61048, 61050, 89702, 89704, 89706, 89708, 84332, 84334 })
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ModeCommandHandler(c, sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.MiniGames>().HandleAsync));
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.RogueCard>();
foreach (var c in new uint[] { 89602, 89604, 89606, 89608, 89610, 89612, 89616, 89618, 89620, 89622, 89624, 89626, 89628 })
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ModeCommandHandler(c, sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.RogueCard>().HandleAsync));
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.SpheroLife>();
foreach (var c in new uint[] { 83003, 83005, 83007, 83009, 83011, 83013, 83018, 83022, 83024, 83040 })
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ModeCommandHandler(c, sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.SpheroLife>().HandleAsync));
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.QuanzhouQuests>();
foreach (var c in new uint[] { 28820, 28822, 28830, 28832, 28834 })
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ModeCommandHandler(c, sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.QuanzhouQuests>().HandleAsync));
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.Explore>();
foreach (var c in new uint[] { 67002, 67004, 67006, 67008, 67010, 67012 })
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ModeCommandHandler(c, sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.Explore>().HandleAsync));
foreach (var c in new uint[] { 85000, 85010, 85012, 85014, 85016, 85018, 85020, 85022, 85024, 85026, 85028, 85036, 85038 })
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ModeCommandHandler(c, sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.SummerBoard>().HandleAsync));
foreach (var c in new uint[] { 28010, 28014, 28016 })
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ModeCommandHandler(c, sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.TaskSystem>().HandleAsync));
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.HeroGrowth>();
foreach (var c in Trimūrti.Server.Protocol.Handlers.HeroGrowth.Commands)
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ModeCommandHandler(c, sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.HeroGrowth>().HandleAsync));
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.WeaponSystem>();
foreach (var c in Trimūrti.Server.Protocol.Handlers.WeaponSystem.Commands)
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ModeCommandHandler(c, sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.WeaponSystem>().HandleAsync));
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.TrustSystem>();
foreach (var c in Trimūrti.Server.Protocol.Handlers.TrustSystem.Commands)
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ModeCommandHandler(c, sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.TrustSystem>().HandleAsync));
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.ChapterMap>();
foreach (var c in Trimūrti.Server.Protocol.Handlers.ChapterMap.Commands)
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ModeCommandHandler(c, sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.ChapterMap>().HandleAsync));
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.OathSystem>();
foreach (var c in Trimūrti.Server.Protocol.Handlers.OathSystem.Commands)
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ModeCommandHandler(c, sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.OathSystem>().HandleAsync));
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.ChipSystem>();
foreach (var c in Trimūrti.Server.Protocol.Handlers.ChipSystem.Commands)
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ModeCommandHandler(c, sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.ChipSystem>().HandleAsync));
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.ShopSystem>();
foreach (var c in Trimūrti.Server.Protocol.Handlers.ShopSystem.Commands)
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ModeCommandHandler(c, sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.ShopSystem>().HandleAsync));
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.GachaSystem>();
foreach (var c in Trimūrti.Server.Protocol.Handlers.GachaSystem.Commands)
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ModeCommandHandler(c, sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.GachaSystem>().HandleAsync));
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.RelationSystem>();
foreach (var c in Trimūrti.Server.Protocol.Handlers.RelationSystem.Commands)
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ModeCommandHandler(c, sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.RelationSystem>().HandleAsync));
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.RhythmGame>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ModeCommandHandler(83126, sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.RhythmGame>().HandleAsync));
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.AchievementSystem>();
foreach (var c in Trimūrti.Server.Protocol.Handlers.AchievementSystem.Commands)
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ModeCommandHandler(c, sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.AchievementSystem>().HandleAsync));
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.MailSystem>();
foreach (var c in Trimūrti.Server.Protocol.Handlers.MailSystem.Commands)
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ModeCommandHandler(c, sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.MailSystem>().HandleAsync));
builder.Services.AddSingleton<Trimūrti.Server.Protocol.Handlers.SigilSystem>();
foreach (var c in Trimūrti.Server.Protocol.Handlers.SigilSystem.Commands)
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp => new Trimūrti.Server.Protocol.Handlers.ModeCommandHandler(c, sp.GetRequiredService<Trimūrti.Server.Protocol.Handlers.SigilSystem>().HandleAsync));
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.FormationSaveHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.FormationDeleteHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.ProfileSignatureHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.ProfileShowcaseHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.ProfileBoardHeroHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.ProfileCardQueryHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.ProfilePortraitHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.ProfileIconFrameHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.ProfileHomeSceneHandler>();
foreach (var lobbyRandomCmd in Trimūrti.Server.Protocol.Handlers.ProfileLobbyRandomHandler.Commands)
{
    var cmd = lobbyRandomCmd;
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp =>
        new Trimūrti.Server.Protocol.Handlers.ProfileLobbyRandomHandler(cmd, sp.GetRequiredService<Trimūrti.Server.GameAccountRepository>(), sp.GetRequiredService<Trimūrti.Server.Protocol.ProtoCodec>()));
}
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.ProfileStickerWallHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.ArchivePictureHandler>();
foreach (var illuCmd in Trimūrti.Server.Protocol.Handlers.IllustratedHandler.Commands)
{
    var cmd = illuCmd;
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp =>
        new Trimūrti.Server.Protocol.Handlers.IllustratedHandler(cmd, sp.GetRequiredService<Trimūrti.Server.GameAccountRepository>(), sp.GetRequiredService<Trimūrti.Server.Protocol.ProtoCodec>()));
}
foreach (var resCmd in Trimūrti.Server.Protocol.Handlers.ReserveTeamHandler.Commands)
{
    var cmd = resCmd;
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp =>
        new Trimūrti.Server.Protocol.Handlers.ReserveTeamHandler(cmd, sp.GetRequiredService<Trimūrti.Server.GameAccountRepository>(), sp.GetRequiredService<Trimūrti.Server.Protocol.ProtoCodec>()));
}
foreach (var miscCmd in Trimūrti.Server.Protocol.Handlers.MiscSinglePlayerHandler.Commands)
{
    var cmd = miscCmd;
    builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler>(sp =>
        new Trimūrti.Server.Protocol.Handlers.MiscSinglePlayerHandler(cmd, sp.GetRequiredService<Trimūrti.Server.GameAccountRepository>(), sp.GetRequiredService<Trimūrti.Server.Protocol.ProtoCodec>()));
}
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.ProfileLobbySyncHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.IGameCommandHandler, Trimūrti.Server.Protocol.Handlers.ProfileSpecialSceneViewHandler>();
builder.Services.AddSingleton<Trimūrti.Server.Protocol.CommandDispatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Trimūrti.Server.GameTcpGateway>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<Trimūrti.Server.GatewayTcpService>());

var app = builder.Build();

app.Use(async (context, next) =>
{
    app.Logger.LogInformation("HTTP {Method} {Scheme}://{Host}{Path}{Query}",
        context.Request.Method, context.Request.Scheme, context.Request.Host, context.Request.Path, context.Request.QueryString);
    await next();
});

app.MapGet("/", () => Results.Ok(new
{
    name = "Trimūrti",
    description = "Local Aether Gazer server research project",
    status = "prototype",
    endpoints = new[] { "/health" }
}));

app.MapGet("/api/client-build", (HttpRequest request, Trimūrti.Server.ClientBuildCatalog builds) =>
{
    var path = request.Query["path"].FirstOrDefault();
    var detection = builds.Detect(path);
    return Results.Ok(new
    {
        installedClient = detection,
        supportedPcResourceBuilds = builds.SupportedBuilds,
        defaultPcResourceBuild = builds.DefaultBuild
    });
});

app.MapGet("/health", (Trimūrti.Server.GameTcpGateway gateway, Trimūrti.Server.GatewayTcpService gatewayTcp, Trimūrti.Server.Protocol.ProtoSchema schema, Trimūrti.Server.SourceGameConfig config, Trimūrti.Server.ClientBuildCatalog builds) => Results.Ok(new
{
    status = "ok",
    server = "Trimūrti",
    gameTcp = gateway.IsListening ? "listening" : "starting",
    gameTcpEndpoint = "127.0.0.1:8105",
    gatewayTcp = gatewayTcp.IsListening ? "listening" : "starting",
    gatewayTcpEndpoint = "127.0.0.1:8102",
    protobufMessages = schema.MessageCount,
    sourceConfigJsonFiles = config.AvailableFiles,
    supportedPcResourceBuilds = builds.SupportedBuilds,
    defaultPcResourceBuild = builds.DefaultBuild,
    activePcResourceBuild = builds.ActiveBuild,
    installedClient = builds.InstalledClient,
    timestamp = DateTimeOffset.UtcNow
}));

app.MapPost("/api/accounts", async (Trimūrti.Server.CreateAccountRequest request, Trimūrti.Server.AccountStore accounts) =>
{
    try
    {
        var account = await accounts.CreateAsync(request.Login, request.Password, CancellationToken.None);
        return Results.Created($"/api/accounts/{account.Id}", new { account.Id, account.Login, account.CreatedAt });
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});

app.MapPost("/api/sessions", async (Trimūrti.Server.LoginRequest request, Trimūrti.Server.AccountStore accounts) =>
{
    var session = await accounts.LoginAsync(request.Login, request.Password, CancellationToken.None);
    return session is null ? Results.Unauthorized() : Results.Ok(session);
});

app.MapPost("/sdk-api/pass/user/login", Trimūrti.Server.SdkLogin.HandleAsync);
app.MapPost("/pass/user/login", Trimūrti.Server.SdkLogin.HandleAsync);
Trimūrti.Server.SdkEndpoints.Map(app);
Trimūrti.Server.GmEndpoints.Map(app);

// Diagnostic catch-all for unmapped SDK/client routes during live client testing
app.MapMethods("/sdk-api/{**rest}", ["GET", "POST"], (HttpRequest req, ILogger<Program> log) =>
{
    log.LogInformation("Unhandled SDK request: {Method} {Path}{Query}", req.Method, req.Path, req.QueryString);
    return Results.Ok(new { errorCode = "0", data = new { } });
});
app.MapMethods("/pass/{**rest}", ["GET", "POST"], (HttpRequest req, ILogger<Program> log) =>
{
    log.LogInformation("Unhandled Pass request: {Method} {Path}{Query}", req.Method, req.Path, req.QueryString);
    return Results.Ok(new { errorCode = "0", data = new { } });
});
app.MapFallback((HttpRequest req, ILogger<Program> log) =>
{
    if (req.Path.Value?.Contains("geetest.html", StringComparison.OrdinalIgnoreCase) == true)
    {
        return Results.Content("""
            <!DOCTYPE html>
            <html><head><meta charset="utf-8"></head><body>
            <script>
              var p = new URLSearchParams(window.location.search);
              var res = {
                geetest_challenge: p.get('challenge') || 'local_challenge',
                geetest_validate: 'local_validate',
                geetest_seccode: 'local_seccode|jordan'
              };
              if (window.unityPostMessage) { window.unityPostMessage(res); }
              else if (window.chrome && window.chrome.webview) { window.chrome.webview.postMessage(JSON.stringify(res)); }
            </script>
            </body></html>
            """, "text/html; charset=utf-8");
    }
    log.LogInformation("Unhandled client HTTP request: {Method} {Host}{Path}{Query}", req.Method, req.Host, req.Path, req.QueryString);
    return Results.Ok(new { errorCode = "0", code = 0, data = new { } });
});

app.Run();
