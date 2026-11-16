using Content.Shared._vg.TileMovement;
using Content.Shared.Audio;
using Content.Shared.Destructible;
using Content.Shared.DragDrop;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Jittering;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.Movement.Systems;
using Content.Shared.Popups;
using Content.Shared.Squad14;
using Content.Shared.Storage.Components;
using Content.Shared.Verbs;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Physics.Components;

namespace Content.Server.Squad14;

public sealed class SquadTruckSystem : SharedSquadTruckSystem
{
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedJitteringSystem _jitter = default!;
    [Dependency] private readonly SharedAmbientSoundSystem _ambient = default!;
    [Dependency] private readonly PullingSystem _pulling = default!;
    [Dependency] private readonly SharedMoverController _mover = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SquadTruckComponent, InteractHandEvent>(OnInteractHand);
        SubscribeLocalEvent<SquadTruckComponent, InteractUsingEvent>(OnInteractUsing);
        SubscribeLocalEvent<SquadTruckComponent, DragDropTargetEvent>(OnDragDrop);
        SubscribeLocalEvent<SquadTruckComponent, GetVerbsEvent<AlternativeVerb>>(OnGetVerbs);
        SubscribeLocalEvent<SquadTruckComponent, AccessibleOverrideEvent>(OnAccessibleOverride);
        SubscribeLocalEvent<SquadTruckComponent, DestructionEventArgs>(OnDestroyed);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<SquadTruckComponent, PhysicsComponent>();
        while (query.MoveNext(out var uid, out _, out var physics))
        {
            if (physics.LinearVelocity.LengthSquared() > 0.25f)
            {
                _jitter.DoJitter(uid, TimeSpan.FromSeconds(0.15), true, amplitude: 1.2f, frequency: 14f);
            }
        }
    }

    private int GetUsedCargoSpace(SquadTruckComponent comp)
    {
        return comp.CargoContainer.ContainedEntities.Count + comp.PassengerContainer.ContainedEntities.Count;
    }

    private void OnAccessibleOverride(Entity<SquadTruckComponent> truck, ref AccessibleOverrideEvent args)
    {
        if (truck.Comp.DriverContainer.ContainedEntity == args.User || truck.Comp.PassengerContainer.Contains(args.User))
            args.Accessible = true;
    }

    private void TryEnterDriver(Entity<SquadTruckComponent> truck, EntityUid user)
    {
        if (truck.Comp.DriverContainer.ContainedEntity != null)
        {
            _popup.PopupEntity("Место водителя уже занято!", truck, user);
            return;
        }

        if (TryComp<PullableComponent>(user, out var pullable) && pullable.BeingPulled)
            _pulling.TryStopPull(user, pullable, user);

        if (!Container.Insert(user, truck.Comp.DriverContainer, force: true))
            return;

        _mover.SetRelay(user, truck);
        if (HasComp<TileMovementComponent>(user))
            EnsureComp<TileMovementComponent>(truck);

        _ambient.SetAmbience(truck, true);
        _audio.PlayPvs(truck.Comp.LoadSound, truck);
        _popup.PopupEntity("Вы сели за руль", truck, user);
    }

    private void ExitDriver(Entity<SquadTruckComponent> truck, EntityUid user)
    {
        if (truck.Comp.DriverContainer.ContainedEntity != user)
            return;

        Container.Remove(user, truck.Comp.DriverContainer, destination: Transform(truck).Coordinates, force: true);
        RemComp<RelayInputMoverComponent>(user);
        RemComp<TileMovementComponent>(truck);

        _ambient.SetAmbience(truck, false);
        _audio.PlayPvs(truck.Comp.UnloadSound, truck);
        _popup.PopupEntity("Вы вышли из-за руля", truck, user);
    }

    private void ExitPassenger(Entity<SquadTruckComponent> truck, EntityUid user)
    {
        if (!truck.Comp.PassengerContainer.Contains(user))
            return;

        Container.Remove(user, truck.Comp.PassengerContainer, destination: Transform(truck).Coordinates, force: true);
        _audio.PlayPvs(truck.Comp.UnloadSound, truck);
        _popup.PopupEntity("Вы вышли из кузова", truck, user);
    }

    private void OnInteractHand(Entity<SquadTruckComponent> truck, ref InteractHandEvent args)
    {
        if (args.Handled)
            return;

        if (truck.Comp.DriverContainer.ContainedEntity == args.User)
        {
            ExitDriver(truck, args.User);
            args.Handled = true;
            return;
        }

        if (truck.Comp.PassengerContainer.Contains(args.User))
        {
            ExitPassenger(truck, args.User);
            args.Handled = true;
            return;
        }

        if (TryComp<PullerComponent>(args.User, out var puller) && puller.Pulling is { } pulled && HasComp<EntityStorageComponent>(pulled))
        {
            if (TryLoadCargo(truck, pulled, args.User))
                args.Handled = true;
            return;
        }

        if (truck.Comp.DriverContainer.ContainedEntity == null && HasComp<MobStateComponent>(args.User))
        {
            TryEnterDriver(truck, args.User);
            args.Handled = true;
        }
    }
    private void OnInteractUsing(Entity<SquadTruckComponent> truck, ref InteractUsingEvent args)
    {
        if (args.Handled)
            return;

        if (!HasComp<EntityStorageComponent>(args.Used))
            return;

        if (TryLoadCargo(truck, args.Used, args.User))
            args.Handled = true;
    }

    private void OnDragDrop(Entity<SquadTruckComponent> truck, ref DragDropTargetEvent args)
    {
        if (args.Handled)
            return;

        if (!HasComp<EntityStorageComponent>(args.Dragged))
            return;

        if (TryLoadCargo(truck, args.Dragged, args.User))
            args.Handled = true;
    }

    private bool TryLoadCargo(Entity<SquadTruckComponent> truck, EntityUid cargo, EntityUid user)
    {
        if (GetUsedCargoSpace(truck.Comp) >= truck.Comp.MaxCargoUnits)
        {
            _popup.PopupEntity($"Кузов переполнен! ({GetUsedCargoSpace(truck.Comp)}/{truck.Comp.MaxCargoUnits})", truck, user);
            return false;
        }

        if (TryComp<PullableComponent>(cargo, out var pullable) && pullable.BeingPulled)
            _pulling.TryStopPull(cargo, pullable, user);

        if (!Container.Insert(cargo, truck.Comp.CargoContainer, force: true))
            return false;

        _audio.PlayPvs(truck.Comp.LoadSound, truck);
        _popup.PopupEntity($"Загружено ({GetUsedCargoSpace(truck.Comp)}/{truck.Comp.MaxCargoUnits})", truck, user);
        return true;
    }

    private void TryEnterPassenger(Entity<SquadTruckComponent> truck, EntityUid user)
    {
        if (GetUsedCargoSpace(truck.Comp) >= truck.Comp.MaxCargoUnits)
        {
            _popup.PopupEntity($"В кузове нет места! ({GetUsedCargoSpace(truck.Comp)}/{truck.Comp.MaxCargoUnits})", truck, user);
            return;
        }

        if (TryComp<PullableComponent>(user, out var pullable) && pullable.BeingPulled)
            _pulling.TryStopPull(user, pullable, user);

        if (!Container.Insert(user, truck.Comp.PassengerContainer, force: true))
            return;

        _audio.PlayPvs(truck.Comp.LoadSound, truck);
        _popup.PopupEntity($"Вы сели в кузов ({GetUsedCargoSpace(truck.Comp)}/{truck.Comp.MaxCargoUnits})", truck, user);
    }

    private void OnGetVerbs(Entity<SquadTruckComponent> truck, ref GetVerbsEvent<AlternativeVerb> args)
    {
        var user = args.User;

        if (truck.Comp.DriverContainer.ContainedEntity == user)
        {
            args.Verbs.Add(new AlternativeVerb
            {
                Text = "Выйти из-за руля",
                Act = () => ExitDriver(truck, user)
            });
            return;
        }

        if (truck.Comp.PassengerContainer.Contains(user))
        {
            args.Verbs.Add(new AlternativeVerb
            {
                Text = "Выйти из грузовика",
                Act = () => ExitPassenger(truck, user)
            });
            return;
        }

        if (!args.CanAccess || !args.CanInteract)
            return;

        var usedSpace = GetUsedCargoSpace(truck.Comp);

        if (HasComp<MobStateComponent>(user))
        {
            if (truck.Comp.DriverContainer.ContainedEntity == null)
            {
                args.Verbs.Add(new AlternativeVerb
                {
                    Text = "Сесть за руль",
                    Act = () => TryEnterDriver(truck, user)
                });
            }

            args.Verbs.Add(new AlternativeVerb
            {
                Text = $"Сесть в кузов ({usedSpace}/{truck.Comp.MaxCargoUnits})",
                Act = () => TryEnterPassenger(truck, user)
            });
        }

        if (TryComp<PullerComponent>(user, out var puller) && puller.Pulling is { } pulled && HasComp<EntityStorageComponent>(pulled))
        {
            args.Verbs.Add(new AlternativeVerb
            {
                Text = $"Загрузить ящик в кузов ({usedSpace}/{truck.Comp.MaxCargoUnits})",
                Act = () => TryLoadCargo(truck, pulled, user)
            });
        }

        if (truck.Comp.CargoContainer.ContainedEntities.Count > 0)
        {
            args.Verbs.Add(new AlternativeVerb
            {
                Text = $"Выгрузить груз ({truck.Comp.CargoContainer.ContainedEntities.Count})",
                Act = () =>
                {
                    Container.EmptyContainer(truck.Comp.CargoContainer, destination: Transform(truck).Coordinates);
                    _audio.PlayPvs(truck.Comp.UnloadSound, truck);
                    _popup.PopupEntity("Кузов разгружен", truck, user);
                }
            });
        }

        if (truck.Comp.PassengerContainer.ContainedEntities.Count > 0)
        {
            args.Verbs.Add(new AlternativeVerb
            {
                Text = $"Высадить пассажиров ({truck.Comp.PassengerContainer.ContainedEntities.Count})",
                Act = () =>
                {
                    Container.EmptyContainer(truck.Comp.PassengerContainer, destination: Transform(truck).Coordinates);
                    _audio.PlayPvs(truck.Comp.UnloadSound, truck);
                    _popup.PopupEntity("Пассажиры высажены", truck, user);
                }
            });
        }
    }

    private void OnDestroyed(Entity<SquadTruckComponent> truck, ref DestructionEventArgs args)
    {
        var coords = Transform(truck).Coordinates;
        if (truck.Comp.DriverContainer.ContainedEntity is { } driver)
            ExitDriver(truck, driver);
        Container.EmptyContainer(truck.Comp.CargoContainer, destination: coords);
        Container.EmptyContainer(truck.Comp.PassengerContainer, destination: coords);
    }
}
