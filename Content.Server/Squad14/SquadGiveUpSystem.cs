using Content.Shared.Actions;
using Content.Shared.Actions.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Popups;
using Content.Shared.Squad14;
using Content.Shared.Verbs;
using Robust.Shared.Prototypes;

namespace Content.Server.Squad14;

public sealed class SquadGiveUpSystem : EntitySystem
{
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;

    private static readonly EntProtoId GiveUpActionId = "ActionSquadGiveUp";

    private readonly Dictionary<EntityUid, EntityUid?> _giveUpActions = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<MobStateChangedEvent>(OnMobStateChanged);
        SubscribeLocalEvent<MobStateComponent, EntityTerminatingEvent>(OnMobTerminating);
        SubscribeLocalEvent<MobStateComponent, GetVerbsEvent<AlternativeVerb>>(OnGetGiveUpVerb);
        SubscribeLocalEvent<SquadGiveUpActionEvent>(OnGiveUpAction);
    }

    private void OnMobTerminating(Entity<MobStateComponent> ent, ref EntityTerminatingEvent args)
    {
        _giveUpActions.Remove(ent);
    }

    private void OnMobStateChanged(MobStateChangedEvent args)
    {
        var uid = args.Target;

        if (args.NewMobState == MobState.Critical)
        {
            if (_giveUpActions.ContainsKey(uid))
                return;

            EnsureComp<ActionsComponent>(uid);
            EntityUid? actionUid = null;
            _actions.AddAction(uid, ref actionUid, GiveUpActionId);
            _giveUpActions[uid] = actionUid;
            _popup.PopupEntity("Вы в критическом состоянии. Вы можете дождаться медика или нажать «Принять смерть».", uid, uid);
        }
        else if (_giveUpActions.Remove(uid, out var actionUid) && actionUid != null)
        {
            _actions.RemoveAction(uid, actionUid);
        }
    }

    private void OnGiveUpAction(SquadGiveUpActionEvent args)
    {
        if (args.Handled)
            return;

        var uid = args.Performer;
        if (!_mobState.IsCritical(uid))
            return;

        _mobState.ChangeMobState(uid, MobState.Dead);
        args.Handled = true;
    }

    private void OnGetGiveUpVerb(Entity<MobStateComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (args.User != ent.Owner || !_mobState.IsCritical(ent.Owner))
            return;

        args.Verbs.Add(new AlternativeVerb
        {
            Text = "Принять смерть",
             Priority = 100,
            Act = () => _mobState.ChangeMobState(ent.Owner, MobState.Dead)
        });
    }
}