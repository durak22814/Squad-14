using Robust.Shared.Audio;
using Robust.Shared.Containers;
using Robust.Shared.GameStates;

namespace Content.Shared.Squad14;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class SquadTruckComponent : Component
{
    [DataField, AutoNetworkedField]
    public int MaxCargoUnits = 6;

    [DataField]
    public string DriverContainerId = "truck_driver";

    [DataField]
    public string CargoContainerId = "truck_cargo";

    [DataField]
    public string PassengerContainerId = "truck_passengers";

    [DataField]
    public SoundSpecifier LoadSound = new SoundPathSpecifier("/Audio/Machines/airlock_open.ogg");

    [DataField]
    public SoundSpecifier UnloadSound = new SoundPathSpecifier("/Audio/Machines/airlock_close.ogg");

    [ViewVariables]
    public ContainerSlot DriverContainer = default!;

    [ViewVariables]
    public Container CargoContainer = default!;

    [ViewVariables]
    public Container PassengerContainer = default!;
}
