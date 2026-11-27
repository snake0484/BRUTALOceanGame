using BrutalGame.Designer.Ui;
using BrutalGame.Ships;
using BrutalGame.Ships.Aero;

namespace BrutalGame.Designer.App;

/// <summary>
/// Air loads in the designer: each moving part's exact surface (the ship's own, and each joint's) is baked into an
/// <see cref="AeroTable"/> on its own background worker when it changes; the Aero panel reads them with the parts' pose.
/// </summary>
public sealed partial class DesignerApp
{
    private readonly AeroPanel _aeroPanel = new();
    private readonly List<HullAeroBaker> _aeroBakers = new();
    private readonly AeroTable?[] _aeroTables = new AeroTable?[HullRenderer.MaxGroups];
    private readonly (HullCsgResult? Surface, Vector3d Reference)[] _aeroInputs = new (HullCsgResult?, Vector3d)[HullRenderer.MaxGroups];
    private double[] _poseAngles = new double[1];

    /// <summary>Each frame: starts a part's bake when its surface or pivot changed, and takes finished tables.</summary>
    private void PollAero()
    {
        var count = Math.Min(_groups.Count, HullRenderer.MaxGroups);

        for (var g = 0; g < HullRenderer.MaxGroups; g++)
        {
            HullCsgResult? surface = null;
            var reference = default(Vector3d);

            if (g == 0)
                surface = _surface;
            else if (g < count)
            {
                reference = _groups.Joints[g - 1].Pivot;

                if (g <= _groupBakes.Count && _groupBakes[g - 1].Active)
                    surface = _groupBakes[g - 1].Result;
            }

            if (surface == _aeroInputs[g].Surface && reference == _aeroInputs[g].Reference)
                continue;

            _aeroInputs[g] = (surface, reference);

            if (surface is null)
            {
                _aeroTables[g] = null;
                continue;
            }

            while (_aeroBakers.Count <= g)
                _aeroBakers.Add(new HullAeroBaker());

            _aeroBakers[g].Start(surface.Bricks, reference);
        }

        for (var g = 0; g < _aeroBakers.Count; g++)
        {
            if (_aeroBakers[g].Poll() && _aeroInputs[g].Surface is not null)
                _aeroTables[g] = _aeroBakers[g].Result;
        }
    }

    private bool AeroBusy => _aeroBakers.Exists(b => b.Busy);

    private void DrawAero()
    {
        var count = Math.Min(_groups.Count, HullRenderer.MaxGroups);
        _aeroPanel.Draw(_groups, _aeroTables.AsSpan(0, count), _poseRotations, _poseTranslations, _poseAngles, _stats.Mass, AeroBusy);
    }

    private void DisposeAero()
    {
        foreach (var baker in _aeroBakers)
            baker.Dispose();

        _aeroBakers.Clear();
    }
}
