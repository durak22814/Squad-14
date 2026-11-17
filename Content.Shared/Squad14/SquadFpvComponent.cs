using Content.Shared.Actions;
using Robust.Shared.GameStates;

namespace Content.Shared.Squad14;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class SquadFpvDroneComponent : Component
{
    [ViewVariables, AutoNetworkedField]
    public EntityUid? Operator;

    [ViewVariables]
    public EntityUid? OperatorSoundStream;

    [DataField]
    public float MaxRange = 65f;

    [DataField]
    public bool DetonateOnCollide = true;

    [DataField]
    public float ExplosionIntensity = 35f;

    [DataField]
    public float ExplosionSlope = 3f;

    [DataField]
    public float ExplosionMaxIntensity = 4f;
}

[RegisterComponent, NetworkedComponent]
public sealed partial class SquadFpvControllerComponent : Component
{
    [DataField]
    public float LinkRadius = 15f;

    [ViewVariables]
    public EntityUid? LinkedDrone;

    [ViewVariables]
    public EntityUid? DisconnectAction;

    [ViewVariables]
    public EntityUid? DetonateAction;
}

public sealed partial class FpvDisconnectActionEvent : InstantActionEvent;

public sealed partial class FpvDetonateActionEvent : InstantActionEvent;
