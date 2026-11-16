using System.Linq;
using Content.Server.Chat.Managers;
using Content.Shared.GameTicking;
using Content.Server.Mind;
using Content.Server.Roles;
using Content.Server.Spawners.Components;
using Content.Server.Spawners.EntitySystems;
using Content.Server.Station.Components;
using Content.Server.Station.Events;
using Content.Server.Station.Systems;
using Content.Shared.Chat;
using Content.Shared.Light.Components;
using Content.Shared.Mind;
using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Shared.Mobs;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using Robust.Server.Console;
using Robust.Server.Player;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server.Squad14
{
    public sealed class SquadRespawnSystem : EntitySystem
    {
        [Dependency] private readonly IGameTiming _timing = default!;
        [Dependency] private readonly IRobustRandom _random = default!;
        [Dependency] private readonly AtmosphereSystem _atmosphere = default!;
        [Dependency] private readonly IPlayerManager _playerManager = default!;
        [Dependency] private readonly IServerConsoleHost _consoleHost = default!;
        [Dependency] private readonly MindSystem _mind = default!;
        [Dependency] private readonly RoleSystem _role = default!;
        [Dependency] private readonly StationSpawningSystem _spawning = default!;
        [Dependency] private readonly StationSystem _station = default!;
        [Dependency] private readonly IChatManager _chat = default!;
        [Dependency] private readonly IPrototypeManager _proto = default!;

        private static readonly TimeSpan DefaultRespawnDelay = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan SquadLeaderRespawnDelay = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

        private readonly List<PendingRespawn> _queue = new();

        private sealed class PendingRespawn
        {
            public EntityUid MindId;
            public string JobId = string.Empty;
            public TimeSpan RespawnTime;
        }

        public override void Initialize()
        {
            base.Initialize();

            SubscribeLocalEvent<MobStateChangedEvent>(OnMobStateChanged);
            SubscribeLocalEvent<RoundStartedEvent>(OnRoundStarted);
            SubscribeLocalEvent<PlayerSpawningEvent>(OnPlayerSpawning, before: new[] { typeof(SpawnPointSystem) });
        }

        private void OnPlayerSpawning(PlayerSpawningEvent args)
        {
            if (args.SpawnResult != null || args.Job == null)
                return;

            var jobId = args.Job.Value.Id;
            if (!IsSquadJob(jobId))
                return;

            var spawnCoords = FindSpawnPoints(jobId);
            if (spawnCoords.Count == 0)
                return;

            var chosenCoords = _random.Pick(spawnCoords);
            var jobProto = _proto.Index(args.Job.Value);

            args.SpawnResult = _spawning.SpawnPlayerMob(
                chosenCoords,
                jobProto,
                args.HumanoidCharacterProfile,
                args.Station);
        }

        private void OnRoundStarted(RoundStartedEvent args)
        {
            var targetGrids = new List<(EntityUid Uid, MapGridComponent Comp, MapId MapId)>();

            var gridQuery = EntityQueryEnumerator<MapGridComponent, TransformComponent>();
            while (gridQuery.MoveNext(out var gridUid, out var gridComp, out var xform))
            {
                var gridName = Name(gridUid).Replace(" ", "");
                if (!gridName.Contains("testarena", StringComparison.OrdinalIgnoreCase))
                    continue;

                targetGrids.Add((gridUid, gridComp, xform.MapID));
            }

            if (targetGrids.Count == 0)
                return;

            var mapId = targetGrids[0].MapId;
            _consoleHost.ExecuteCommand($"planet {(int)mapId} Grasslands");

            foreach (var (gridUid, gridComp, _) in targetGrids)
            {
                RemComp<ImplicitRoofComponent>(gridUid);

                var netId = GetNetEntity(gridUid);
                _consoleHost.ExecuteCommand($"addatmos {netId}");
                _consoleHost.ExecuteCommand($"fixgridatmos {netId}");
            }
        }

        private void OnMobStateChanged(MobStateChangedEvent args)
        {
            if (args.NewMobState != MobState.Dead)
                return;

            if (!_mind.TryGetMind(args.Target, out var mindId, out var mind))
                return;

            if (mind.UserId == null || !_playerManager.TryGetSessionById(mind.UserId.Value, out var session))
                return;

            var jobId = _role
                .MindGetAllRoleInfo(mindId)
                .FirstOrDefault(info => !string.IsNullOrWhiteSpace(info.Prototype))
                .Prototype;

            if (string.IsNullOrWhiteSpace(jobId) || !IsSquadJob(jobId))
                return;

            if (IsTeamLeader(jobId))
            {
                const string noRespawnMsg = "Вы погибли! Командиры команд не подлежат возрождению.";
                _chat.ChatMessageToOne(
                    ChatChannel.Server,
                    noRespawnMsg,
                    noRespawnMsg,
                    EntityUid.Invalid,
                    false,
                    session.Channel);
                return;
            }

            if (_queue.Any(entry => entry.MindId == mindId))
                return;

            var delay = GetRespawnDelay(jobId);

            _queue.Add(new PendingRespawn
            {
                MindId = mindId,
                JobId = jobId,
                RespawnTime = _timing.CurTime + delay
            });

            var msg = $"Вы погибли! Возрождение через {(int)delay.TotalSeconds} секунд.";
            _chat.ChatMessageToOne(
                ChatChannel.Server,
                msg,
                msg,
                EntityUid.Invalid,
                false,
                session.Channel);
        }

        public override void Update(float frameTime)
        {
            base.Update(frameTime);

            if (_queue.Count == 0)
                return;

            var now = _timing.CurTime;

            for (var i = _queue.Count - 1; i >= 0; i--)
            {
                var entry = _queue[i];

                if (now < entry.RespawnTime)
                    continue;

                if (RespawnPlayer(entry))
                {
                    _queue.RemoveAt(i);
                }
                else
                {
                    entry.RespawnTime = now + RetryDelay;
                }
            }
        }

        private bool RespawnPlayer(PendingRespawn entry)
        {
            if (!TryComp<MindComponent>(entry.MindId, out var mind) ||
                mind.UserId == null ||
                !_playerManager.TryGetSessionById(mind.UserId.Value, out _))
            {
                return true;
            }

            if (!_proto.TryIndex<JobPrototype>(entry.JobId, out var jobProto))
                return false;

            var spawnCoords = FindSpawnPoints(entry.JobId);

            if (spawnCoords.Count == 0)
                return false;

            var chosenCoords = _random.Pick(spawnCoords);
            var profile = HumanoidCharacterProfile.Random();

            var newMob = _spawning.SpawnPlayerMob(
                chosenCoords,
                jobProto,
                profile,
                null);

            var oldGhost = mind.VisitingEntity;
            _mind.TransferTo(entry.MindId, newMob, mind: mind);
            _mind.UnVisit(entry.MindId, mind);

            if (oldGhost != null && Exists(oldGhost.Value))
                QueueDel(oldGhost.Value);

            return true;
        }

        private List<EntityCoordinates> FindSpawnPoints(string jobId)
        {
            var result = new List<EntityCoordinates>();
            var factionPrefix = GetFactionPrefix(jobId);

            if (factionPrefix == null)
                return result;

            var exactMatches = new List<EntityCoordinates>();
            var factionMatches = new List<EntityCoordinates>();

            var query = EntityQueryEnumerator<SpawnPointComponent, TransformComponent>();

            while (query.MoveNext(out var uid, out var spawnPoint, out var transform))
            {
                var spawnJobId = spawnPoint.Job?.Id;

                if (string.IsNullOrEmpty(spawnJobId))
                {
                    var protoId = MetaData(uid).EntityPrototype?.ID;
                    if (!string.IsNullOrEmpty(protoId) && protoId.StartsWith("SpawnPoint", StringComparison.Ordinal))
                    {
                        spawnJobId = protoId.Substring("SpawnPoint".Length);
                    }
                }

                if (string.IsNullOrEmpty(spawnJobId))
                    continue;

                if (spawnJobId == jobId)
                {
                    exactMatches.Add(transform.Coordinates);
                    continue;
                }

                if (GetFactionPrefix(spawnJobId) == factionPrefix)
                    factionMatches.Add(transform.Coordinates);
            }

            if (exactMatches.Count > 0)
                return exactMatches;

            return factionMatches;
        }

        private static bool IsTeamLeader(string jobId)
        {
            return jobId.EndsWith("TeamLeader", StringComparison.Ordinal);
        }

        private static TimeSpan GetRespawnDelay(string jobId)
        {
            if (jobId.EndsWith("SquadLeader", StringComparison.Ordinal))
                return SquadLeaderRespawnDelay;

            return DefaultRespawnDelay;
        }

        private static bool IsSquadJob(string jobId)
        {
            return GetFactionPrefix(jobId) != null;
        }

        private static string? GetFactionPrefix(string jobId)
        {
            if (jobId.StartsWith("Red", StringComparison.Ordinal))
                return "Red";

            if (jobId.StartsWith("Blue", StringComparison.Ordinal))
                return "Blue";

            return null;
        }
    }
}

