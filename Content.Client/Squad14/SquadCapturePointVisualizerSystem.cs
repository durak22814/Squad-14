using Content.Shared.Squad14;
using Robust.Client.GameObjects;

namespace Content.Client.Squad14;

public sealed class SquadCapturePointVisualizerSystem : EntitySystem
{
    [Dependency] private readonly SpriteSystem _sprite = default!;

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        var query = EntityQueryEnumerator<SquadCapturePointComponent, SpriteComponent>();
        while (query.MoveNext(out var uid, out var point, out var sprite))
        {
            var t = Math.Clamp(Math.Abs(point.Progress) / 100f, 0f, 1f);
            var targetColor = point.Progress < 0
                ? Color.InterpolateBetween(Color.White, Color.Red, t)
                : Color.InterpolateBetween(Color.White, Color.DeepSkyBlue, t);

            if (_sprite.TryGetLayer((uid, sprite), 1, out var layer, false))
            {
                _sprite.LayerSetColor((uid, sprite), 1, targetColor);
            }
        }
    }
}
