using Content.Shared.Squad14;

namespace Content.Server.Squad14;

[RegisterComponent]
public sealed partial class SquadFrontlineRuleComponent : Component
{
    [ViewVariables]
    public SquadTeam Winner = SquadTeam.Neutral;

    [ViewVariables]
    public bool RedHqExposedAnnounced;

    [ViewVariables]
    public bool BlueHqExposedAnnounced;
}
