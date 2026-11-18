using Content.Server.Explosion.EntitySystems;
using Content.Shared.Actions;
using Content.Shared.Actions.Components;
using Content.Shared.Audio;
using Content.Shared.Damage;
using Content.Shared.Destructible;
using Content.Shared.Interaction.Events;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.Popups;
using Content.Shared.Squad14;
using Content.Shared.Verbs;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Physics.Events;
using Robust.Shared.Prototypes;

namespace Content.Server.Squad14;

public sealed class SquadFpvSystem : EntitySystem
{
    [Dependency] private readonly SharedEyeSystem _eye = default!;
    [Dependency] private readonly SharedAudioSystem _sound = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly SharedMoverController _mover = default!;
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly SharedAmbientSoundSystem _audio = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly ExplosionSystem _explosion = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    private static readonly EntProtoId DisconnectActionId = "ActionFpvDisconnect";
    private static readonly EntProtoId DetonateActionId = "ActionFpvDetonate";

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SquadFpvControllerComponent, UseInHandEvent>(OnControllerUse);
        SubscribeLocalEvent<SquadFpvControllerComponent, GetVerbsEvent<AlternativeVerb>>(OnControllerVerbs);
        SubscribeLocalEvent<SquadFpvControllerComponent, DroppedEvent>(OnControllerDropped);

        SubscribeLocalEvent<SquadFpvDroneComponent, StartCollideEvent>(OnDroneCollide);
        SubscribeLocalEvent<SquadFpvDroneComponent, DestructionEventArgs>(OnDroneDestroyed);

        SubscribeLocalEvent<FpvDisconnectActionEvent>(OnDisconnectAction);
        SubscribeLocalEvent<FpvDetonateActionEvent>(OnDetonateAction);
        SubscribeLocalEvent<MobStateChangedEvent>(OnOperatorMobStateChanged);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<SquadFpvDroneComponent, TransformComponent>();
        while (query.MoveNext(out var droneUid, out var drone, out var droneXform))
        {
            if (drone.Operator is not { } opUid)
                continue;

            if (!Exists(opUid) || !TryComp<TransformComponent>(opUid, out var opXform))
            {
                DisconnectDrone((droneUid, drone));
                continue;
            }

            if (droneXform.MapID != opXform.MapID)
            {
                DisconnectDrone((droneUid, drone));
                continue;
            }

            var dist = (_transform.GetWorldPosition(droneXform) - _transform.GetWorldPosition(opXform)).Length();
            if (dist > drone.MaxRange)
            {
                _popup.PopupEntity("Сигнал с FPV-дроном потерян (превышена дальность)!", opUid, opUid);
                DisconnectDrone((droneUid, drone));
            }
        }
    }

    private void OnControllerUse(Entity<SquadFpvControllerComponent> controller, ref UseInHandEvent args)
    {
        if (args.Handled)
            return;

        if (controller.Comp.LinkedDrone is { } existing
            && Exists(existing)
            && TryComp<SquadFpvDroneComponent>(existing, out var existingComp)
            && existingComp.Operator == args.User)
        {
            DetonateDrone((existing, existingComp));
            args.Handled = true;
            return;
        }

        if (TryConnectToNearestDrone(controller, args.User))
            args.Handled = true;
    }

    private void OnControllerVerbs(Entity<SquadFpvControllerComponent> controller, ref GetVerbsEvent<AlternativeVerb> args)
    {
        var user = args.User;

        if (controller.Comp.LinkedDrone is { } linked && Exists(linked) && TryComp<SquadFpvDroneComponent>(linked, out var linkedComp) && linkedComp.Operator == user)
        {
            args.Verbs.Add(new AlternativeVerb
            {
                Text = "Подрыв FPV-заряда",
                Act = () => DetonateDrone((linked, linkedComp))
            });
            args.Verbs.Add(new AlternativeVerb
            {
                Text = "Отключиться от дрона",
                Act = () => DisconnectDrone((linked, linkedComp))
            });
            return;
        }

        if (!args.CanAccess || !args.CanInteract)
            return;

        args.Verbs.Add(new AlternativeVerb
        {
            Text = "Подключиться к FPV-дрону",
            Act = () => TryConnectToNearestDrone(controller, user)
        });
    }

    private void OnControllerDropped(Entity<SquadFpvControllerComponent> controller, ref DroppedEvent args)
    {
        if (controller.Comp.LinkedDrone is { } droneUid && TryComp<SquadFpvDroneComponent>(droneUid, out var drone))
        {
            if (drone.Operator == args.User)
                DisconnectDrone((droneUid, drone));
        }
    }

    private void OnOperatorMobStateChanged(MobStateChangedEvent args)
    {
        if (args.NewMobState == MobState.Alive)
            return;

        var query = EntityQueryEnumerator<SquadFpvDroneComponent>();
        while (query.MoveNext(out var uid, out var drone))
        {
            if (drone.Operator == args.Target)
                DisconnectDrone((uid, drone));
        }
    }

    private bool TryConnectToNearestDrone(Entity<SquadFpvControllerComponent> controller, EntityUid user)
    {
        if (controller.Comp.LinkedDrone is { } existing && Exists(existing) && TryComp<SquadFpvDroneComponent>(existing, out var existingComp) && existingComp.Operator == user)
        {
            DisconnectDrone((existing, existingComp));
            return true;
        }

        var userCoords = Transform(user).Coordinates;
        Entity<SquadFpvDroneComponent>? bestDrone = null;
        var bestDist = float.MaxValue;

        foreach (var drone in _lookup.GetEntitiesInRange<SquadFpvDroneComponent>(userCoords, controller.Comp.LinkRadius))
        {
            if (drone.Comp.Operator != null)
                continue;
                
            if (controller.Comp.Team != SquadTeam.Neutral
                && drone.Comp.Team != SquadTeam.Neutral
                && controller.Comp.Team != drone.Comp.Team)
                continue;

            var dist = (_transform.GetWorldPosition(drone) - _transform.GetWorldPosition(user)).Length();
            if (dist < bestDist)
            {
                bestDist = dist;
                bestDrone = drone;
            }
        }

        if (bestDrone is not { } targetDrone)
        {
            _popup.PopupEntity("Поблизости нет свободных FPV-дронов!", user, user);
            return false;
        }

        targetDrone.Comp.Operator = user;
        Dirty(targetDrone, targetDrone.Comp);
        controller.Comp.LinkedDrone = targetDrone;

        if (TryComp<EyeComponent>(user, out var eye))
            _eye.SetTarget(user, targetDrone, eye);

        _mover.SetRelay(user, targetDrone);
        _audio.SetAmbience(targetDrone, true);

        if (_playerManager.TryGetSessionByEntity(user, out var session))
{
    targetDrone.Comp.OperatorSoundStream = _sound.PlayGlobal(
        "/Audio/Effects/fpv.ogg",
        session,
        AudioParams.Default.WithLoop(true).WithVolume(-2f))?.Entity;
}


        EnsureComp<ActionsComponent>(user);
        _actions.AddAction(user, ref controller.Comp.DisconnectAction, DisconnectActionId);
        if (targetDrone.Comp.DetonateOnCollide || targetDrone.Comp.ExplosionIntensity > 0)
            _actions.AddAction(user, ref controller.Comp.DetonateAction, DetonateActionId);

        _popup.PopupEntity("Связь с FPV-дроном установлена!", user, user);
        return true;
    }

    private void DisconnectDrone(Entity<SquadFpvDroneComponent> drone)
    {
        if (drone.Comp.Operator is not { } user)
            return;

        drone.Comp.Operator = null;
        Dirty(drone, drone.Comp);

        if (Exists(user))
        {
            if (TryComp<EyeComponent>(user, out var eye))
                _eye.SetTarget(user, user, eye);

            RemComp<RelayInputMoverComponent>(user);
        }

        if (Exists(drone))
{
        _audio.SetAmbience(drone, false);
        drone.Comp.OperatorSoundStream = _sound.Stop(drone.Comp.OperatorSoundStream);
}


        var ctrlQuery = EntityQueryEnumerator<SquadFpvControllerComponent>();
        while (ctrlQuery.MoveNext(out _, out var ctrl))
        {
            if (ctrl.LinkedDrone != drone.Owner)
                continue;

            ctrl.LinkedDrone = null;
            if (Exists(user))
            {
                if (ctrl.DisconnectAction != null)
                {
                    _actions.RemoveAction(user, ctrl.DisconnectAction);
                    ctrl.DisconnectAction = null;
                }
                if (ctrl.DetonateAction != null)
                {
                    _actions.RemoveAction(user, ctrl.DetonateAction);
                    ctrl.DetonateAction = null;
                }
            }
        }
    }

    private void OnDroneCollide(Entity<SquadFpvDroneComponent> drone, ref StartCollideEvent args)
    {
        if (!drone.Comp.DetonateOnCollide || drone.Comp.Operator == null)
            return;

        if (args.OtherEntity == drone.Comp.Operator)
            return;

        if (!args.OtherFixture.Hard)
            return;

        DetonateDrone(drone);
    }

    private void OnDroneDestroyed(Entity<SquadFpvDroneComponent> drone, ref DestructionEventArgs args)
    {
        DisconnectDrone(drone);
    }

    private void OnDisconnectAction(FpvDisconnectActionEvent args)
    {
        if (args.Handled)
            return;

        var query = EntityQueryEnumerator<SquadFpvDroneComponent>();
        while (query.MoveNext(out var uid, out var drone))
        {
            if (drone.Operator == args.Performer)
            {
                DisconnectDrone((uid, drone));
                args.Handled = true;
                return;
            }
        }
    }

    private void OnDetonateAction(FpvDetonateActionEvent args)
    {
        if (args.Handled)
            return;

        var query = EntityQueryEnumerator<SquadFpvDroneComponent>();
        while (query.MoveNext(out var uid, out var drone))
        {
            if (drone.Operator == args.Performer)
            {
                DetonateDrone((uid, drone));
                args.Handled = true;
                return;
            }
        }
    }

    private void DetonateDrone(Entity<SquadFpvDroneComponent> drone)
    {
        var coords = Transform(drone).Coordinates;
        DisconnectDrone(drone);

        if (!Exists(drone))
            return;

        var shrapnelDamage = new DamageSpecifier
        {
            DamageDict =
            {
                ["Blunt"] = 45,
                ["Slash"] = 55,
                ["Heat"] = 35
            }
        };

        foreach (var target in _lookup.GetEntitiesInRange<MobStateComponent>(coords, 2.2f))
        {
            _damageable.TryChangeDamage(target.Owner, shrapnelDamage, ignoreResistances: false);
        }

        if (drone.Comp.StructuralDamage > 0f)
        {
            var structDamage = new DamageSpecifier
            {
                DamageDict =
                {
                    ["Blunt"] = drone.Comp.StructuralDamage * 0.6f,
                    ["Piercing"] = drone.Comp.StructuralDamage * 0.4f
                }
            };

            foreach (var target in _lookup.GetEntitiesInRange<DamageableComponent>(coords, 2.2f))
            {
                if (HasComp<MobStateComponent>(target))
                    continue;

                _damageable.TryChangeDamage(target.Owner, structDamage, ignoreResistances: true);
            }
        }

        _explosion.QueueExplosion(
            drone.Owner,
            "Default",
            120f,
            2.5f,
            15f,
            canCreateVacuum: false);

        QueueDel(drone);
    }
}
