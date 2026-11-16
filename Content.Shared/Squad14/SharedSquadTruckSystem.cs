using Content.Shared.DragDrop;
using Content.Shared.Movement.Pulling.Events;
using Content.Shared.Storage.Components;
using Robust.Shared.Containers;

namespace Content.Shared.Squad14;

public abstract class SharedSquadTruckSystem : EntitySystem
{
    [Dependency] protected readonly SharedContainerSystem Container = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SquadTruckComponent, ComponentInit>(OnInit);
        SubscribeLocalEvent<SquadTruckComponent, CanDropTargetEvent>(OnCanDrop);
        SubscribeLocalEvent<SquadTruckComponent, PullAttemptEvent>(OnPullAttempt);
    }

    private void OnInit(Entity<SquadTruckComponent> truck, ref ComponentInit args)
    {
        truck.Comp.DriverContainer = Container.EnsureContainer<ContainerSlot>(truck, truck.Comp.DriverContainerId);
        truck.Comp.CargoContainer = Container.EnsureContainer<Container>(truck, truck.Comp.CargoContainerId);
        truck.Comp.PassengerContainer = Container.EnsureContainer<Container>(truck, truck.Comp.PassengerContainerId);
    }

    private void OnCanDrop(Entity<SquadTruckComponent> truck, ref CanDropTargetEvent args)
    {
        if (HasComp<EntityStorageComponent>(args.Dragged))
        {
            args.CanDrop = true;
            args.Handled = true;
        }
    }

    private void OnPullAttempt(Entity<SquadTruckComponent> truck, ref PullAttemptEvent args)
    {
        if (args.PulledUid == truck.Owner)
            args.Cancelled = true;
    }
}
