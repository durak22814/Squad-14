using Content.Shared.Actions;
using Content.Shared.Squad14;

namespace Content.Server.Squad14;

[RegisterComponent]
public sealed partial class SquadFrontlineRuleComponent : Component
{
    [DataField]
    public float PreparationTime = 150f;

    [ViewVariables]
    public float PreparationRemaining;

    [ViewVariables]
    public bool IsPreparationActive;

    [ViewVariables]
    public bool OneMinuteAnnounced;

    [ViewVariables]
    public bool ThirtySecondsAnnounced;

    [ViewVariables]
    public SquadTeam Winner = SquadTeam.Neutral;

    [ViewVariables]
    public bool RedHqExposedAnnounced;

    [ViewVariables]
    public bool BlueHqExposedAnnounced;
}