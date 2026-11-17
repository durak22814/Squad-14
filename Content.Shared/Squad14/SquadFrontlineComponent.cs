using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared.Squad14;

[Serializable, NetSerializable]
public enum SquadTeam : byte
{
    Neutral = 0,
    Red = 1,
    Blue = 2
}

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class SquadCapturePointComponent : Component
{
    [DataField]
    public string PointName = "Контрольная точка";

    [DataField]
    public float CaptureRadius = 4.5f;

    [DataField]
    public float CaptureSpeedPerPlayer = 10f;

    [DataField]
    public SoundSpecifier TickSound = new SoundPathSpecifier("/Audio/Machines/button.ogg", new AudioParams { Volume = -8f });

    [DataField]
    public SoundSpecifier CapturedSound = new SoundPathSpecifier("/Audio/Effects/Cargo/ping.ogg", new AudioParams { Volume = -3f });

    /// <summary>
    /// От -100 (Red) до +100 (Blue).
    /// </summary>
    [ViewVariables, AutoNetworkedField]
    public float Progress = 0f;

    [ViewVariables, AutoNetworkedField]
    public SquadTeam OwnerTeam = SquadTeam.Neutral;
}

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class SquadHqObjectiveComponent : Component
{
    [DataField(required: true)]
    public SquadTeam Team = SquadTeam.Neutral;

    [ViewVariables, AutoNetworkedField]
    public bool IsVulnerable = false;
}
