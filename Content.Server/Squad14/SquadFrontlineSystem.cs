using Content.Server.Chat.Systems;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Rules;
using Content.Server.Mind;
using Content.Server.RoundEnd;
using Content.Shared.Damage;
using Content.Shared.Destructible;
using Content.Shared.Examine;
using Content.Shared.GameTicking.Components;
using Content.Shared.Inventory;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Roles.Jobs;
using Content.Shared.Squad14;
using Content.Shared.Tag;
using Robust.Server.GameObjects;
using Robust.Shared.Audio.Systems;

namespace Content.Server.Squad14;

public sealed class SquadFrontlineSystem : GameRuleSystem<SquadFrontlineRuleComponent>
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly TagSystem _tag = default!;
    [Dependency] private readonly MindSystem _mind = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedJobSystem _jobs = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly PointLightSystem _light = default!;
    [Dependency] private readonly ChatSystem _chatSystem = default!;
    [Dependency] private readonly RoundEndSystem _roundEnd = default!;

    private float _accumulator;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SquadCapturePointComponent, ExaminedEvent>(OnPointExamined);
        SubscribeLocalEvent<SquadHqObjectiveComponent, ExaminedEvent>(OnHqExamined);
        SubscribeLocalEvent<SquadHqObjectiveComponent, DamageModifyEvent>(OnHqDamageModify);
        SubscribeLocalEvent<SquadHqObjectiveComponent, DestructionEventArgs>(OnHqDestroyed);
    }

        protected override void Started(EntityUid uid, SquadFrontlineRuleComponent component, GameRuleComponent gameRule, GameRuleStartedEvent args)
    {
        base.Started(uid, component, gameRule, args);

        component.PreparationRemaining = component.PreparationTime;
        component.IsPreparationActive = component.PreparationTime > 0f;
        component.OneMinuteAnnounced = false;
        component.ThirtySecondsAnnounced = false;

        if (component.IsPreparationActive)
        {
            _chatSystem.DispatchGlobalAnnouncement(
                $"Этап подготовки начался! У команд есть {FormatTime(component.PreparationRemaining)} на развёртывание и укрепление позиций.",
                "Командование",
                colorOverride: Color.Gold);
        }
        else
        {
            RemovePrepBarriers();
        }
    }


    protected override void AppendRoundEndText(EntityUid uid, SquadFrontlineRuleComponent component, GameRuleComponent gameRule, ref RoundEndTextAppendEvent args)
    {
        base.AppendRoundEndText(uid, component, gameRule, ref args);

        var winnerText = component.Winner switch
        {
            SquadTeam.Red => "[color=red]Победа команды Красных.[/color] Штаб противника уничтожен.",
            SquadTeam.Blue => "[color=deepskyblue]Победа команды Синих.[/color] Штаб противника уничтожен.",
            _ => "Раунд завершен без победителя."
        };

        args.AddLine(winnerText);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _accumulator += frameTime;
        if (_accumulator < 1f)
            return;

        var dt = _accumulator;
        _accumulator = 0f;

        if (UpdatePreparationTimer(dt))
            return;

        ProcessCapturePoints(dt);
        UpdateHqVulnerability();
    }
    private void ProcessCapturePoints(float dt)
    {
        var query = EntityQueryEnumerator<SquadCapturePointComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var point, out var xform))
        {
            var redCount = 0;
            var blueCount = 0;

            foreach (var ent in _lookup.GetEntitiesInRange<MobStateComponent>(xform.Coordinates, point.CaptureRadius))
            {
                if (!_mobState.IsAlive(ent))
                    continue;

                var team = GetEntityTeam(ent);
                if (team == SquadTeam.Red)
                    redCount++;
                else if (team == SquadTeam.Blue)
                    blueCount++;
            }

            var diff = blueCount - redCount;
            if (diff == 0)
                continue;

            var oldProgress = point.Progress;
            point.Progress = Math.Clamp(point.Progress + diff * point.CaptureSpeedPerPlayer * dt, -100f, 100f);

            if (Math.Abs(point.Progress - oldProgress) > 0.01f)
            {
                Dirty(uid, point);

                var t = Math.Clamp(Math.Abs(point.Progress) / 100f, 0f, 1f);
                var lightColor = point.Progress < 0
                    ? Color.InterpolateBetween(Color.White, Color.Red, t)
                    : Color.InterpolateBetween(Color.White, Color.DeepSkyBlue, t);
                _light.SetColor(uid, lightColor);

                if (Math.Abs(point.Progress) < 100f)
                {
                    _audio.PlayPvs(point.TickSound, uid);
                }
            }

            if (point.Progress <= -100f && point.OwnerTeam != SquadTeam.Red)
            {
                point.OwnerTeam = SquadTeam.Red;
                Dirty(uid, point);
                _light.SetColor(uid, Color.Red);
                _audio.PlayPvs(point.CapturedSound, uid);
                _chatSystem.DispatchGlobalAnnouncement($"{point.PointName} захвачена силами Красных!", "Фронт", colorOverride: Color.Red);
            }
            else if (point.Progress >= 100f && point.OwnerTeam != SquadTeam.Blue)
            {
                point.OwnerTeam = SquadTeam.Blue;
                Dirty(uid, point);
                _light.SetColor(uid, Color.DeepSkyBlue);
                _audio.PlayPvs(point.CapturedSound, uid);
                _chatSystem.DispatchGlobalAnnouncement($"{point.PointName} захвачена силами Синих!", "Фронт", colorOverride: Color.DeepSkyBlue);
            }
            else if (point.OwnerTeam != SquadTeam.Neutral && Math.Abs(point.Progress) < 50f)
            {
                point.OwnerTeam = SquadTeam.Neutral;
                Dirty(uid, point);
                _chatSystem.DispatchGlobalAnnouncement($"{point.PointName} нейтрализована!", "Фронт", colorOverride: Color.Yellow);
            }
        }
    }

    private void UpdateHqVulnerability()
    {
        var totalPoints = 0;
        var redPoints = 0;
        var bluePoints = 0;

        var pointQuery = EntityQueryEnumerator<SquadCapturePointComponent>();
        while (pointQuery.MoveNext(out _, out var point))
        {
            totalPoints++;
            if (point.OwnerTeam == SquadTeam.Red)
                redPoints++;
            else if (point.OwnerTeam == SquadTeam.Blue)
                bluePoints++;
        }

        if (totalPoints == 0)
            return;

        var blueCanAttackRedHq = bluePoints >= totalPoints;
        var redCanAttackBlueHq = redPoints >= totalPoints;

        var hqQuery = EntityQueryEnumerator<SquadHqObjectiveComponent>();
        while (hqQuery.MoveNext(out var uid, out var hq))
        {
            var shouldBeVulnerable = hq.Team == SquadTeam.Red ? blueCanAttackRedHq : redCanAttackBlueHq;
            if (hq.IsVulnerable == shouldBeVulnerable)
                continue;

            hq.IsVulnerable = shouldBeVulnerable;
            Dirty(uid, hq);

            if (shouldBeVulnerable)
            {
                var teamName = hq.Team == SquadTeam.Red ? "Красных" : "Синих";
                _chatSystem.DispatchGlobalAnnouncement($"ВНИМАНИЕ! Все точки захвачены - защита штаба {teamName} отключена! Уничтожьте командный узел!", "Штаб", colorOverride: Color.OrangeRed);
            }
        }
    }    
    private SquadTeam GetEntityTeam(EntityUid uid)
    {
        if (_mind.TryGetMind(uid, out var mindId, out _) && _jobs.MindTryGetJobId(mindId, out var jobId) && jobId != null)
        {
            var id = jobId.Value.Id;
            if (id.StartsWith("Red", StringComparison.OrdinalIgnoreCase))
                return SquadTeam.Red;
            if (id.StartsWith("Blue", StringComparison.OrdinalIgnoreCase))
                return SquadTeam.Blue;
        }

        if (_inventory.TryGetSlotEntity(uid, "id", out var idCard) && idCard != null)
        {
            var proto = MetaData(idCard.Value).EntityPrototype?.ID ?? string.Empty;
            if (proto.Contains("Red", StringComparison.OrdinalIgnoreCase))
                return SquadTeam.Red;
            if (proto.Contains("Blue", StringComparison.OrdinalIgnoreCase))
                return SquadTeam.Blue;
        }

        return SquadTeam.Neutral;
    }

    private void OnPointExamined(Entity<SquadCapturePointComponent> ent, ref ExaminedEvent args)
    {
        var status = ent.Comp.OwnerTeam switch
        {
            SquadTeam.Red => "[color=red]Под контролем Красных[/color]",
            SquadTeam.Blue => "[color=deepskyblue]Под контролем Синих[/color]",
            _ => "[color=yellow]Нейтральна[/color]"
        };

        args.PushMarkup($"{ent.Comp.PointName}: {status} (Прогресс: {Math.Round(ent.Comp.Progress)}%)");
    }

    private void OnHqExamined(Entity<SquadHqObjectiveComponent> ent, ref ExaminedEvent args)
    {
        if (ent.Comp.IsVulnerable)
            args.PushMarkup("[color=red]ЗАЩИТА ОТКЛЮЧЕНА! Командный узел уязвим для атак![/color]");
        else
            args.PushMarkup("[color=green]Силовой щит активен. Захватите все контрольные точки, чтобы снять защиту.[/color]");
    }

    private void OnHqDamageModify(Entity<SquadHqObjectiveComponent> ent, ref DamageModifyEvent args)
    {
        if (!ent.Comp.IsVulnerable)
            args.Damage = new DamageSpecifier();
    }

    private void OnHqDestroyed(Entity<SquadHqObjectiveComponent> ent, ref DestructionEventArgs args)
    {
        var winner = ent.Comp.Team == SquadTeam.Red ? SquadTeam.Blue : SquadTeam.Red;
        var winnerSaved = false;

        var ruleQuery = EntityQueryEnumerator<SquadFrontlineRuleComponent>();
        while (ruleQuery.MoveNext(out _, out var rule))
        {
            if (rule.Winner != SquadTeam.Neutral)
                return;

            rule.Winner = winner;
            winnerSaved = true;
        }

        if (!winnerSaved && _roundEnd.IsRoundEndRequested())
            return;

        var winText = winner == SquadTeam.Red
            ? "Команда Красных уничтожила вражеский штаб и одержала победу!"
            : "Команда Синих уничтожила вражеский штаб и одержала победу!";
        _chatSystem.DispatchGlobalAnnouncement(winText, "Командование", colorOverride: Color.Gold);
        _roundEnd.EndRound();
    }

    private bool UpdatePreparationTimer(float dt)
    {
        var active = false;
        var query = EntityQueryEnumerator<SquadFrontlineRuleComponent>();
                while (query.MoveNext(out _, out var rule))
        {
            if (!rule.IsPreparationActive)
                continue;

            rule.PreparationRemaining = Math.Max(0f, rule.PreparationRemaining - dt);

            if (rule.PreparationRemaining <= 60f && !rule.OneMinuteAnnounced && rule.PreparationRemaining > 30f)
            {
                rule.OneMinuteAnnounced = true;
                _chatSystem.DispatchGlobalAnnouncement("До окончания подготовки осталась 1 минута!", "Командование", colorOverride: Color.Yellow);
            }
            else if (rule.PreparationRemaining <= 30f && !rule.ThirtySecondsAnnounced && rule.PreparationRemaining > 0f)
            {
                rule.ThirtySecondsAnnounced = true;
                _chatSystem.DispatchGlobalAnnouncement("До начала боя осталось 30 секунд!", "Командование", colorOverride: Color.Orange);
            }

            if (rule.PreparationRemaining <= 0f)
            {
                rule.IsPreparationActive = false;
                _chatSystem.DispatchGlobalAnnouncement("Этап подготовки завершён! Барьеры сняты, контрольные точки открыты!", "Командование", colorOverride: Color.LimeGreen);

                var barrierQuery = EntityQueryEnumerator<SquadPrepBarrierComponent>();
                while (barrierQuery.MoveNext(out var barrierUid, out _))
                {
                    QueueDel(barrierUid);
                }
            }
            else
            {
                active = true;
            }
        }

        return active;
    }


        private void RemovePrepBarriers()
    {
        var tagQuery = EntityQueryEnumerator<TagComponent>();
        while (tagQuery.MoveNext(out var barrierUid, out _))
        {
            if (_tag.HasTag(barrierUid, "SquadPrepBarrier"))
                QueueDel(barrierUid);
        }
    }

    private static string FormatTime(float seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Ceiling(seconds));
        return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}";
    }
}
