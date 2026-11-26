using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace BrutalGame.Terrain;

/// <summary>
/// Compiles a <see cref="PlanetKind"/> for one body (planet-kinds.md §1.1, §7): resolves <c>[min, max]</c> ranges from
/// the kind seed (each parameter draws on its own path hash), flattens layers into the program's tables, lowers gates
/// to mask ops, builds the per-LOD tables, calibrates the shaped-octave means and canyon gradients by sampling, appends
/// the relief knee and the sea clamp, and hashes the result. Rejects a warp that can fold and a kind whose worst-case
/// height sum is over twice its relief range.
/// </summary>
public static class PlanetKindCompiler
{
    public const double MaxWarpFold = 0.25;
    public const float KneeShare = 0.8f;
    public const float TerraceSlope = 0.5f;

    private const int CalibrationPoints = 2048;
    private const uint OctaveSalt = 0x9E3779B9u;
    private const int None = PlanetKindProgram.None;

    public static PlanetKindProgram Compile(PlanetKind kind, in PlanetParams p)
    {
        var b = new Builder(kind, p);

        foreach (var layer in kind.Layers.EnumerateArray())
        {
            b.Layer(layer);
            b.ReleaseTemps();
        }

        return b.Finish();
    }

    internal static ulong Fnv(ReadOnlySpan<byte> bytes, ulong hash = 0xCBF29CE484222325UL)
    {
        foreach (var x in bytes)
            hash = (hash ^ x) * 0x100000001B3UL;

        return hash;
    }

    internal static ulong SplitMix(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private sealed class Builder(PlanetKind kind, PlanetParams p)
    {
        private readonly List<double> _frequency = [], _offsetX = [], _offsetY = [], _offsetZ = [];
        private readonly List<float> _wavelength = [], _amplitude = [], _aux = [], _params = [];
        private readonly List<uint> _seed = [];
        private readonly List<LayerOp> _op = [];
        private readonly List<byte> _dst = [], _srcA = [], _srcB = [], _gate = [], _scale = [], _warp = [], _mode = [];
        private readonly List<ushort> _firstOctave = [], _octaveCount = [], _paramBase = [];
        private readonly List<float> _band = [], _slope = [];
        private readonly List<string> _names = [];
        private readonly List<StampClass> _classes = [];
        private readonly Dictionary<string, int> _channels = new(StringComparer.Ordinal) { ["h"] = PlanetKindProgram.Height };
        private readonly HashSet<int> _written = [PlanetKindProgram.Height];
        private readonly Stack<int> _freeTemps = new();
        private readonly List<int> _liveTemps = [];
        private readonly uint _baseSeed = Noise.SeedOf(p.Seed);
        private int _channelCount = 1;
        private int _legacyOctave;
        private string _layer = "";

        private bool Compat => kind.IsCompatV1;

        private InvalidDataException Error(string message) => new($"Planet kind {kind.Id}, layer '{_layer}': {message}");

        private double Unit(string path)
        {
            var hash = SplitMix(p.KindSeed ^ Fnv(Encoding.UTF8.GetBytes(path)));
            return (hash >> 11) * (1.0 / (1UL << 53));
        }

        private double Resolve(JsonElement e, string path)
        {
            if (e.ValueKind == JsonValueKind.Number)
                return e.GetDouble();

            if (e.ValueKind == JsonValueKind.Array && e.GetArrayLength() == 2)
            {
                double min = e[0].GetDouble(), max = e[1].GetDouble();
                return min + (max - min) * Unit(path);
            }

            throw Error($"'{path}' is not a number or a [min, max] range");
        }

        private double Num(JsonElement obj, string name, double? fallback = null)
        {
            if (obj.TryGetProperty(name, out var e))
                return Resolve(e, $"layers.{_layer}.{name}");

            return fallback ?? throw Error($"missing '{name}'");
        }

        private (float A, float B) Pair(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != 2)
                throw Error($"'{name}' must be a pair");

            return (e[0].GetSingle(), e[1].GetSingle());
        }

        private static string? Text(JsonElement obj, string name) => obj.TryGetProperty(name, out var e) ? e.GetString() : null;

        private static bool Flag(JsonElement obj, string name) => obj.TryGetProperty(name, out var e) && e.GetBoolean();

        private int Existing(string name) => _channels.TryGetValue(name, out var c) ? c : throw Error($"no channel '{name}' yet");

        private int NewChannels(string name, int count)
        {
            var first = _channelCount;
            _channels.Add(name, first);
            _channelCount += count;
            return first;
        }

        private int Temp()
        {
            var c = _freeTemps.Count > 0 ? _freeTemps.Pop() : _channelCount++;
            _liveTemps.Add(c);
            return c;
        }

        public void ReleaseTemps()
        {
            foreach (var c in _liveTemps)
                _freeTemps.Push(c);

            _liveTemps.Clear();
        }

        private int Emit(LayerOp op, int dst, int a = None, int b = None, int gate = None, int scale = None, int warp = None, byte mode = 0, int firstOctave = 0, int octaves = 0, int paramBase = 0, float band = 0f)
        {
            if (_op.Count == PlanetKindProgram.MaxLayers)
                throw Error($"more than {PlanetKindProgram.MaxLayers} ops");

            _op.Add(op);
            _dst.Add((byte)dst);
            _srcA.Add((byte)a);
            _srcB.Add((byte)b);
            _gate.Add((byte)gate);
            _scale.Add((byte)scale);
            _warp.Add((byte)warp);
            _mode.Add(mode);
            _firstOctave.Add((ushort)firstOctave);
            _octaveCount.Add((ushort)octaves);
            _paramBase.Add((ushort)paramBase);
            _band.Add(band);
            _slope.Add(TerraceSlope);
            _names.Add(_layer);
            _written.Add(dst);
            return _op.Count - 1;
        }

        private int Push(params ReadOnlySpan<float> values)
        {
            var at = _params.Count;

            foreach (var v in values)
                _params.Add(v);

            return at;
        }

        /// <summary>dst += a × m (or sets it, on the channel's first write).</summary>
        private void AddInto(int dst, int a, int m)
        {
            if (_written.Contains(dst))
                Emit(LayerOp.Combine, dst, a, m, mode: LayerMode.AddMul);
            else if (m == None)
                Emit(LayerOp.Combine, dst, a, mode: LayerMode.Copy);
            else
                Emit(LayerOp.Combine, dst, a, m, mode: LayerMode.Mul);
        }

        private int MaskOp(byte mode, int src, float from, float to)
        {
            if (from == to)
                throw Error("a mask needs from != to");

            var dst = Temp();
            Emit(LayerOp.Mask, dst, src, mode: mode, paramBase: Push(from, 1f / (to - from)));
            return dst;
        }

        /// <summary>Lowers a gate expression to mask ops; returns the channel holding it.</summary>
        private int Gate(JsonElement e)
        {
            if (e.TryGetProperty("all", out var all) || e.TryGetProperty("any", out all))
            {
                var mode = e.TryGetProperty("all", out _) ? LayerMode.Mul : LayerMode.Max;
                var acc = None;

                foreach (var term in all.EnumerateArray())
                {
                    var c = Gate(term);

                    if (acc == None)
                    {
                        acc = c;
                        continue;
                    }

                    var dst = Temp();
                    Emit(LayerOp.Combine, dst, acc, c, mode: mode);
                    acc = dst;
                }

                return acc == None ? throw Error("empty gate") : acc;
            }

            if (e.TryGetProperty("not", out var not))
            {
                var dst = Temp();
                Emit(LayerOp.Combine, dst, Gate(not), mode: LayerMode.Invert);
                return dst;
            }

            if (e.TryGetProperty("alt", out _))
            {
                var (from, to) = Pair(e, "alt");
                return MaskOp(LayerMode.MaskChannel, PlanetKindProgram.Height, from, to);
            }

            if (e.TryGetProperty("lat", out _))
            {
                var (from, to) = Pair(e, "lat");
                return MaskOp(LayerMode.MaskLat, None, from, to);
            }

            var channel = Existing(Text(e, "channel") ?? throw Error("a gate needs all, any, not, alt, lat or channel"));

            if (!e.TryGetProperty("from", out var f))
                return channel;

            return MaskOp(LayerMode.MaskChannel, channel, f.GetSingle(), e.GetProperty("to").GetSingle());
        }

        /// <summary>gate × scale as one mask channel, or None.</summary>
        private int Masks(JsonElement layer)
        {
            var gate = layer.TryGetProperty("gate", out var g) ? Gate(g) : None;
            var scale = layer.TryGetProperty("scale", out var s) ? Gate(s) : None;

            if (gate == None || scale == None)
                return gate == None ? scale : gate;

            var dst = Temp();
            Emit(LayerOp.Combine, dst, gate, scale, mode: LayerMode.Mul);
            return dst;
        }

        public void Layer(JsonElement layer)
        {
            _layer = Text(layer, "name") ?? throw new InvalidDataException($"Planet kind {kind.Id}: a layer without a name");
            var type = Text(layer, "type") ?? throw Error("no type");

            switch (type)
            {
                case "fbm":
                    NoiseLayer(layer, LayerOp.Fbm);
                    break;
                case "ridged":
                    NoiseLayer(layer, LayerOp.Ridged);
                    break;
                case "billow":
                    NoiseLayer(layer, LayerOp.Billow);
                    break;
                case "eroded":
                    NoiseLayer(layer, LayerOp.Eroded);
                    break;
                case "warp":
                    NoiseLayer(layer, LayerOp.Warp);
                    break;
                case "curve":
                    CurveLayer(layer);
                    break;
                case "terrace":
                    TerraceLayer(layer);
                    break;
                case "canyon":
                    CanyonLayer(layer);
                    break;
                case "mask":
                {
                    var m = Gate(layer);
                    Emit(LayerOp.Combine, NewChannels(Text(layer, "out") ?? _layer, 1), m, mode: LayerMode.Copy);
                    break;
                }
                case "craters":
                    StampLayer(layer, LayerOp.Craters);
                    break;
                case "ergs":
                    StampLayer(layer, LayerOp.Ergs);
                    break;
                case "legacy":
                    LegacyLayer(layer);
                    break;
                default:
                    throw Error($"unknown type '{type}'");
            }
        }

        private void NoiseLayer(JsonElement layer, LayerOp op)
        {
            var octaves = (int)Math.Round(Num(layer, "octaves"));
            var lambda = layer.TryGetProperty("lambdaM", out _) ? Num(layer, "lambdaM") : Num(layer, "lambdaR") * p.Radius;
            var amplitude = (float)(layer.TryGetProperty("amplitudeM", out _) ? Num(layer, "amplitudeM") : Num(layer, "amplitude", 1.0));
            var gain = (float)Num(layer, "gain", 0.5);
            var outName = Text(layer, "out") ?? _layer;
            var fields = op == LayerOp.Warp ? 3 : 1;
            byte mode = 0;

            if (octaves < 1 || octaves > 24 || lambda <= 0.0 || amplitude < 0f)
                throw Error("octaves must be 1..24, wavelength positive, amplitude ≥ 0");

            if (Flag(layer, "precise"))
                mode |= LayerMode.Precise;

            if (op == LayerOp.Fbm && Flag(layer, "gradient"))
                mode |= LayerMode.Gradient;

            if (op == LayerOp.Ridged && Compat)
                mode |= LayerMode.ShapedV1;

            var warp = None;

            if (Text(layer, "warp") is { } warpName)
            {
                warp = Existing(warpName);

                if (op == LayerOp.Warp)
                    throw Error("a warp cannot be warped");
            }

            var first = _frequency.Count;
            var layerSeed = (uint)SplitMix(p.KindSeed ^ Fnv(Encoding.UTF8.GetBytes($"layers.{_layer}.seed")));
            var a = amplitude;
            var norm = 0f;
            var fold = 0.0;

            for (var i = 0; i < octaves; i++)
            {
                var wavelength = lambda / (1 << i);

                for (var c = 0; c < fields; c++)
                {
                    var h = Compat ? Noise.Mix(_baseSeed + (uint)_legacyOctave++ * OctaveSalt) : Noise.Mix(layerSeed + (uint)(i * fields + c) * OctaveSalt);
                    _frequency.Add(1.0 / wavelength);
                    _wavelength.Add((float)wavelength);
                    _amplitude.Add(a);
                    _aux.Add(0f);
                    _seed.Add(h);
                    _offsetX.Add((Noise.Mix(h ^ 0x68E31DA4u) & 0xFFFF) * (1.0 / 64.0));
                    _offsetY.Add((Noise.Mix(h ^ 0xB5297A4Du) & 0xFFFF) * (1.0 / 64.0));
                    _offsetZ.Add((Noise.Mix(h ^ 0x1B56C4E9u) & 0xFFFF) * (1.0 / 64.0));
                }

                norm += a;
                fold += a / wavelength;
                a *= gain;
            }

            if (Flag(layer, "normalize"))
            {
                for (var i = first; i < _amplitude.Count; i++)
                    _amplitude[i] /= norm;
            }

            if (op == LayerOp.Warp && fold > MaxWarpFold)
                throw Error($"warp folds: sum of amplitude / wavelength is {fold:F3}, over {MaxWarpFold}");

            var paramBase = op switch
            {
                LayerOp.Ridged => Push((float)Num(layer, "ridgeGain", 2.0)),
                LayerOp.Eroded => Push((float)Num(layer, "damping", 1.0)),
                _ => 0,
            };

            if (op == LayerOp.Eroded)
            {
                // The running slope is at most the noise's steepest gradient × Σ a·f, which floors each octave's damping.
                var steepest = 0.0;

                for (var i = first; i < _amplitude.Count; i++)
                {
                    steepest += _amplitude[i] * _frequency[i] * Noise.GradientLipschitz;
                    _aux[i] = (float)(1.0 / (1.0 + _params[paramBase] * steepest * steepest));
                }
            }

            if (op == LayerOp.Warp)
            {
                Emit(op, NewChannels(outName, 3), mode: mode, firstOctave: first, octaves: octaves);
                return;
            }

            var mask = Masks(layer);
            var gradient = (mode & LayerMode.Gradient) != 0;

            if (!gradient && (mask != None || _channels.ContainsKey(outName)))
            {
                var sum = Temp();
                Emit(op, sum, gate: mask, warp: warp, mode: mode, firstOctave: first, octaves: octaves, paramBase: paramBase);
                AddInto(_channels.TryGetValue(outName, out var target) ? target : NewChannels(outName, 1), sum, mask);
                return;
            }

            if (mask != None || _channels.ContainsKey(outName))
                throw Error("a gradient field writes its own new channel, ungated");

            Emit(op, NewChannels(outName, gradient ? 4 : 1), warp: warp, mode: mode, firstOctave: first, octaves: octaves, paramBase: paramBase);
        }

        private void CurveLayer(JsonElement layer)
        {
            var src = Existing(Text(layer, "in") ?? throw Error("a curve needs 'in'"));
            var outName = Text(layer, "out") ?? "h";
            var knots = layer.GetProperty("knots");
            var count = knots.GetArrayLength();

            if (count is < 2 or > 8)
                throw Error("a curve takes 2..8 knots");

            var at = Push(count);
            var k = 0;

            foreach (var knot in knots.EnumerateArray())
                _params.Add((float)Resolve(knot[0], $"layers.{_layer}.knots.{k++}.x"));

            k = 0;

            foreach (var knot in knots.EnumerateArray())
                _params.Add((float)Resolve(knot[1], $"layers.{_layer}.knots.{k++}.y"));

            for (var i = 1; i < count; i++)
            {
                if (_params[at + 1 + i] <= _params[at + i])
                    throw Error("knot inputs must ascend");
            }

            var mode = (byte)((Flag(layer, "ease") ? LayerMode.CurveEase : 0) | (Text(layer, "op") == "add" ? LayerMode.CurveAdd : 0));
            var dst = _channels.TryGetValue(outName, out var c) ? c : NewChannels(outName, 1);

            if ((mode & LayerMode.CurveAdd) != 0 && !_written.Contains(dst))
                mode &= unchecked((byte)~LayerMode.CurveAdd);

            Emit(LayerOp.Curve, dst, src, mode: mode, paramBase: at);
        }

        private void TerraceLayer(JsonElement layer)
        {
            var levels = layer.GetProperty("levels");
            var count = Math.Clamp((int)Math.Round(Resolve(levels.GetProperty("count"), $"layers.{_layer}.levels.count")), 1, 16);
            var from = levels.GetProperty("fromM").GetSingle();
            var to = levels.GetProperty("toM").GetSingle();
            var jitter = levels.TryGetProperty("jitter", out var j) ? Math.Clamp(j.GetSingle(), 0f, 0.9f) : 0f;
            var band = (float)Num(layer, "riserBandM", 2.0);
            var beta = (float)Num(layer, "benchSlope", 0.05);

            if (to <= from || beta is < 0f or > 1f)
                throw Error("terrace needs fromM < toM and benchSlope in [0, 1]");

            var strata = Text(layer, "warp") is { } w ? Existing(w) : None;
            var strength = layer.TryGetProperty("strength", out var s) ? Gate(s) : None;
            var at = Push(count, beta, from);

            for (var k = 1; k < count; k++)
                _params.Add(from + (to - from) * (k + jitter * ((float)Unit($"layers.{_layer}.levels.{k}") - 0.5f)) / count);

            _params.Add(to);
            Emit(LayerOp.Terrace, PlanetKindProgram.Height, PlanetKindProgram.Height, strata, strength, paramBase: at, band: band);
        }

        private void CanyonLayer(JsonElement layer)
        {
            var field = Existing(Text(layer, "field") ?? throw Error("a canyon needs 'field'"));
            var source = _dst.IndexOf((byte)field);

            if (source < 0 || _op[source] != LayerOp.Fbm || (_mode[source] & LayerMode.Gradient) == 0)
                throw Error("a canyon's field must be an fbm with \"gradient\": true");

            var halfWidth = (float)Num(layer, "halfWidthM");
            var depth = (float)Num(layer, "depthM");
            var floor = Math.Clamp((float)Num(layer, "floor", 0.3), 0f, 0.9f);
            var benches = (float)Math.Round(Num(layer, "benches", 0.0));
            var riser = Math.Clamp((float)Num(layer, "riser", 0.5), 0.05f, 1f);
            var steepest = 0.0;

            for (var o = _firstOctave[source]; o < _firstOctave[source] + _octaveCount[source]; o++)
                steepest += _amplitude[o] * _frequency[o];

            var gate = layer.TryGetProperty("gate", out var g) ? Gate(g) : None;
            var at = Push(halfWidth, depth, floor, benches, 0f, 0f, (float)(steepest * Noise.GradientLipschitz));
            Emit(LayerOp.Canyon, PlanetKindProgram.Height, field, gate: gate, paramBase: at, band: riser);
        }

        /// <summary>Area share of one class grid at presence 1: mean bowl area over the cell's.</summary>
        private const double GridCoverage = Math.PI * 0.72 / (Stamps.CellRadii * Stamps.CellRadii);
        private const float SmallCrater = 500f;

        private void AddClass(LayerOp op, uint seed, StampClass cls, double cellArc)
        {
            var n = (int)Math.Floor(2.0 * Stamps.MinArcPerT * p.Radius / cellArc);

            if (n < 2)
                return;

            var extent = cls.Reach * cls.RadiusMax / (Stamps.MinArcPerT * p.Radius) * (n * 0.5);
            cls.Kind = op;
            cls.CellsPerSide = n;
            cls.Jitter = (float)Math.Max(0.5 - extent - 0.02, 0.0);
            cls.Rotation = (byte)((_classes.Count * 7 + 3) % Stamps.Rotations);
            cls.Seed = Noise.Mix(seed + (uint)_classes.Count * OctaveSalt);

            if (extent > 0.48)
                throw Error("a stamp class does not fit its cell");

            _classes.Add(cls);
        }

        private void StampLayer(JsonElement layer, LayerOp op)
        {
            var first = _classes.Count;
            var seed = (uint)SplitMix(p.KindSeed ^ Fnv(Encoding.UTF8.GetBytes($"layers.{_layer}.seed")));

            if (op == LayerOp.Craters)
            {
                var (rMin, rMax) = Pair(layer, "radiusM");
                var (ageMin, ageMax) = layer.TryGetProperty("age", out _) ? Pair(layer, "age") : (0f, 1f);
                var coverage = Num(layer, "coverage", 0.1);
                var complex = (float)Num(layer, "complexDiameterM", 1e9);
                var grids = Math.Clamp((int)Math.Ceiling(coverage / (2.0 * GridCoverage)), 1, 3);
                var presence = (float)Math.Min(coverage / (2.0 * grids * GridCoverage), 1.0);
                var steps = 0;

                for (var r = rMin; r * 1.2f < rMax; r *= 1.41421356f)
                    steps++;

                // Large to small: old basins first, young small craters on top.
                for (var k = steps - 1; k >= 0; k--)
                {
                    var lo = rMin;

                    for (var i = 0; i < k; i++)
                        lo *= 1.41421356f;

                    var hi = lo * 1.41421356f;
                    var deepest = Stamps.CraterDepth(hi, complex);

                    for (var g = 0; g < grids; g++)
                    {
                        AddClass(op, seed, new StampClass
                        {
                            RadiusMin = lo, RadiusMax = hi, Presence = presence, Reach = lo < SmallCrater ? Stamps.SmallReach : Stamps.CraterReach,
                            AgeMin = ageMin, AgeMax = ageMax, ComplexDiameter = complex,
                            DepthMax = deepest * (1f - 0.7f * ageMin), RimMax = Stamps.RimShare * deepest * (1f - 0.9f * ageMin),
                        }, Stamps.CellRadii * hi);
                    }
                }
            }
            else
            {
                var cell = Num(layer, "cellKm", 300.0) * 1000.0;
                var (rMin, rMax) = Pair(layer, "radiusKm");
                var (lambdaMin, lambdaMax) = Pair(layer, "lambdaM");
                var (ratioMin, ratioMax) = Pair(layer, "heightRatio");
                rMax = MathF.Min(rMax * 1000f, (float)(0.4 * cell));
                rMin = MathF.Min(rMin * 1000f, rMax);

                AddClass(op, seed, new StampClass
                {
                    RadiusMin = rMin, RadiusMax = rMax, Presence = (float)Num(layer, "presence", 0.3), Reach = 1f,
                    LambdaMin = lambdaMin, LambdaMax = lambdaMax, RatioMin = ratioMin, RatioMax = ratioMax, RimMax = lambdaMax * ratioMax,
                }, cell);
            }

            var mask = Masks(layer);
            var sum = Temp();
            var fresh = op == LayerOp.Craters && Text(layer, "freshOut") is { } name ? NewChannels(name, 1) : None;
            var at = Push(first, _classes.Count - first);

            if (op == LayerOp.Craters)
            {
                float deepest = 1f, highest = 1f, depths = 0f, rims = 0f;

                for (var k = first; k < _classes.Count; k++)
                {
                    deepest = MathF.Max(deepest, _classes[k].DepthMax);
                    highest = MathF.Max(highest, _classes[k].RimMax);
                    depths += _classes[k].DepthMax;
                    rims += _classes[k].RimMax;
                }

                // One crater is left alone; nested ones ease into 1.5 × the deepest (rims: the highest).
                float top = MathF.Max(rims, highest) + 1f, bottom = -MathF.Max(depths, deepest) - 1f;
                Push(highest, top, 1.5f * highest, (top - highest) / (0.5f * highest), -deepest, bottom, -1.5f * deepest, (-deepest - bottom) / (0.5f * deepest));
            }

            Emit(op, sum, b: fresh, paramBase: at);

            if (fresh != None)
                _written.Add(fresh);

            AddInto(Existing(Text(layer, "out") ?? "h"), sum, mask);
        }

        private void LegacyLayer(JsonElement layer)
        {
            PlanetParams.LegacyDraws(p.Seed, out var land, out var mountains);
            var sea = p.SeaHeight;
            var floor = p.HasSea ? sea + p.ShoreProtectHeight + 1f : float.NegativeInfinity;
            var at = Push(land, mountains, sea, p.MinHeight + 600f, floor, p.MinHeight, p.MaxHeight);
            Emit(LayerOp.Legacy, PlanetKindProgram.Height, Existing(Text(layer, "continent")!), Existing(Text(layer, "ridge")!), scale: Existing(Text(layer, "detail")!), paramBase: at);
        }

        public PlanetKindProgram Finish()
        {
            _layer = "(final)";

            if (!Compat && p.HasSea)
                Emit(LayerOp.Combine, PlanetKindProgram.Height, mode: LayerMode.AddConst, paramBase: Push(p.SeaHeight));

            var program = Build();
            Calibrate(program);

            if (Compat)
                return Seal(program);

            Span<float> lo = stackalloc float[program.ChannelCount];
            Span<float> hi = stackalloc float[program.ChannelCount];
            LayerOps.Bounds(program, p.Radius, 0.0, 0.0, 4.0 * p.Radius, 0, PlanetGenerator.MaxLod, lo, hi, program.LayerCount);
            float sumLo = lo[0], sumHi = hi[0];
            var range = p.MaxHeight - p.MinHeight;

            if (sumHi - sumLo > 2f * range)
                throw Error($"worst-case height sum {sumLo:F0} .. {sumHi:F0} m is over twice the relief range {p.MinHeight:F0} .. {p.MaxHeight:F0} m");

            var kneeHi = p.MaxHeight - (1f - KneeShare) * MathF.Max(MathF.Abs(p.MaxHeight), 0.25f * range);
            var kneeLo = p.MinHeight + (1f - KneeShare) * MathF.Max(MathF.Abs(p.MinHeight), 0.25f * range);
            float top = MathF.Max(sumHi + 1f, p.MaxHeight), bottom = MathF.Min(sumLo - 1f, p.MinHeight);
            var knee = Push(kneeHi, top, p.MaxHeight, (top - kneeHi) / (p.MaxHeight - kneeHi), kneeLo, bottom, p.MinHeight, (kneeLo - bottom) / (kneeLo - p.MinHeight));
            Emit(LayerOp.Curve, PlanetKindProgram.Height, PlanetKindProgram.Height, mode: LayerMode.CurveKnee, paramBase: knee);

            if (p.HasSea)
            {
                var continent = Existing(kind.Continent ?? throw Error("a sea needs a continent channel"));
                var clamp = Push(p.SeaHeight + p.ShoreProtectHeight + 1f, LayerOps.LegacyInlandStart);
                Emit(LayerOp.SeaClamp, PlanetKindProgram.Height, continent, paramBase: clamp);
            }

            var final = Build();
            program.OctaveMean.CopyTo(final.OctaveMean, 0);
            CopyCalibration(program, final);
            BuildLodTables(final);
            return Seal(final);
        }

        private PlanetKindProgram Build()
        {
            if (_channelCount > PlanetKindProgram.MaxChannels)
                throw Error($"{_channelCount} channels, over {PlanetKindProgram.MaxChannels}");

            var program = new PlanetKindProgram
            {
                KindId = kind.Id,
                Radius = p.Radius,
                AxisX = p.AxisX,
                AxisY = p.AxisY,
                AxisZ = p.AxisZ,
                MinHeight = p.MinHeight,
                MaxHeight = p.MaxHeight,
                SeaHeight = p.SeaHeight,
                HasSea = p.HasSea,
                Frequency = [.. _frequency],
                OffsetX = [.. _offsetX],
                OffsetY = [.. _offsetY],
                OffsetZ = [.. _offsetZ],
                Wavelength = [.. _wavelength],
                Amplitude = [.. _amplitude],
                OctaveMean = [.. _aux],
                Seed = [.. _seed],
                Op = [.. _op],
                Dst = [.. _dst],
                SrcA = [.. _srcA],
                SrcB = [.. _srcB],
                Gate = [.. _gate],
                Scale = [.. _scale],
                Warp = [.. _warp],
                Mode = [.. _mode],
                FirstOctave = [.. _firstOctave],
                OctaveCount = [.. _octaveCount],
                ParamBase = [.. _paramBase],
                Params = [.. _params],
                Classes = [.. _classes],
                LayerNames = [.. _names],
                LayerCount = _op.Count,
                ChannelCount = _channelCount,
                ContinentChannel = !Compat && p.HasSea && kind.Continent is { } c ? Existing(c) : -1,
            };

            BuildLodTables(program);
            return program;
        }

        private void BuildLodTables(PlanetKindProgram program)
        {
            var layers = program.LayerCount;
            program.OctaveEnd = new ushort[PlanetKindProgram.Lods * layers];
            program.CoarseEnd = new ushort[PlanetKindProgram.Lods * layers];
            program.CoarseSlot = new ushort[PlanetKindProgram.Lods * layers];
            program.RiserBand = new float[PlanetKindProgram.Lods * layers];
            program.LodWeight = new float[PlanetKindProgram.Lods * layers];
            program.RidgeMean = new float[PlanetKindProgram.Lods * layers];
            program.ClassWeight = new float[PlanetKindProgram.Lods * program.Classes.Length * 2];
            program.MaxCoarseSlots = 0;

            for (var lod = 0; lod < PlanetKindProgram.Lods; lod++)
            {
                var voxel = (float)(1 << lod);
                var slots = 0;

                for (var k = 0; k < program.Classes.Length; k++)
                {
                    ref readonly var cls = ref program.Classes[k];
                    var at = (lod * program.Classes.Length + k) * 2;

                    // Craters: full from 8 voxels of radius, the rim eased out by 4, the bowl by 2. Dunes: detail eased into its mean from 8 to 4 voxels of wavelength.
                    if (cls.Kind == LayerOp.Craters)
                    {
                        program.ClassWeight[at] = Math.Clamp((cls.RadiusMin / voxel - 2f) / 2f, 0f, 1f);
                        program.ClassWeight[at + 1] = Math.Clamp((cls.RadiusMin / voxel - 4f) / 4f, 0f, 1f);
                    }
                    else
                    {
                        program.ClassWeight[at] = Math.Clamp((cls.LambdaMin / voxel - 4f) / 4f, 0f, 1f);
                    }
                }

                for (var layer = 0; layer < layers; layer++)
                {
                    var at = program.At(lod, layer);
                    program.LodWeight[at] = 1f;

                    switch (program.Op[layer])
                    {
                        case LayerOp.Terrace:
                            program.RiserBand[at] = MathF.Max(_band[layer], _slope[layer] * voxel);
                            continue;
                        case LayerOp.Canyon:
                        {
                            var pb = program.ParamBase[layer];
                            float halfWidth = program.Params[pb], floor = program.Params[pb + 2], benches = MathF.Max(program.Params[pb + 3], 1f);
                            program.RiserBand[at] = Math.Clamp(MathF.Max(_band[layer], voxel * benches / (halfWidth * (1f - floor))), 0.05f, 1f);
                            program.LodWeight[at] = Math.Clamp(halfWidth / voxel - 1f, 0f, 1f);
                            continue;
                        }
                    }

                    if (!program.IsNoise(layer))
                        continue;

                    var fields = program.Fields(layer);
                    int first = program.FirstOctave[layer], count = program.OctaveCount[layer];
                    float min = program.MinVoxels(layer) * voxel, coarse = program.CoarseVoxelsOf(layer) * voxel;
                    int end = 0, coarseEnd = 0;
                    var mean = 0f;

                    for (var o = 0; o < count; o++)
                    {
                        var wavelength = program.Wavelength[first + o * fields];

                        if (wavelength >= min)
                            end++;
                        else
                            mean += program.OctaveMean[first + o];

                        if (wavelength >= coarse)
                            coarseEnd++;
                    }

                    program.OctaveEnd[at] = (ushort)end;
                    program.CoarseEnd[at] = (ushort)coarseEnd;
                    program.CoarseSlot[at] = (ushort)slots;
                    program.RidgeMean[at] = mean;
                    slots += program.SlotsOf(layer, coarseEnd);
                }

                program.CoarseSlots[lod] = slots;
                program.MaxCoarseSlots = Math.Max(program.MaxCoarseSlots, slots);
            }
        }

        /// <summary>
        /// Samples the program at seeded directions: the mean each ridged or billow octave contributes (what a LOD that
        /// skips it adds back), and per canyon the field's RMS tangential gradient (g_min is a third of it) and the mean
        /// of the wall profile (what the canyon fades to when a voxel outgrows it).
        /// </summary>
        private void Calibrate(PlanetKindProgram program)
        {
            var needed = false;

            for (var layer = 0; layer < program.LayerCount; layer++)
                needed |= program.Op[layer] is LayerOp.Canyon or LayerOp.Terrace || (program.Op[layer] is LayerOp.Ridged or LayerOp.Billow && (program.Mode[layer] & LayerMode.ShapedV1) == 0);

            if (!needed)
                return;

            const int n = CalibrationPoints;
            double[] x = new double[n], y = new double[n], z = new double[n];
            var state = SplitMix(p.KindSeed ^ 0xCA11B8A7EUL);

            for (var i = 0; i < n; i++)
            {
                double dx, dy, dz, l;

                do
                {
                    dx = Next(ref state) * 2.0 - 1.0;
                    dy = Next(ref state) * 2.0 - 1.0;
                    dz = Next(ref state) * 2.0 - 1.0;
                    l = dx * dx + dy * dy + dz * dz;
                }
                while (l is < 0.01 or > 1.0);

                l = p.Radius / Math.Sqrt(l);
                (x[i], y[i], z[i]) = (dx * l, dy * l, dz * l);
            }

            for (var layer = 0; layer < program.LayerCount; layer++)
            {
                if (program.Op[layer] is not (LayerOp.Ridged or LayerOp.Billow) || (program.Mode[layer] & LayerMode.ShapedV1) != 0)
                    continue;

                int first = program.FirstOctave[layer], count = program.OctaveCount[layer];
                var gain = program.Op[layer] == LayerOp.Ridged ? program.Params[program.ParamBase[layer]] : 0f;
                var sums = new double[count];

                for (var i = 0; i < n; i++)
                {
                    var weight = 1f;

                    for (var o = 0; o < count; o++)
                    {
                        var f = program.Frequency[first + o];
                        var v = Math.Clamp(Noise.Gradient3(x[i] * f + program.OffsetX[first + o], y[i] * f + program.OffsetY[first + o], z[i] * f + program.OffsetZ[first + o], program.Seed[first + o]), -1f, 1f);

                        if (program.Op[layer] == LayerOp.Billow)
                        {
                            sums[o] += program.Amplitude[first + o] * (2f * MathF.Abs(v) - 1f);
                            continue;
                        }

                        var k = 1f - MathF.Abs(v);
                        var s = k * k * weight;
                        sums[o] += program.Amplitude[first + o] * s;
                        weight = MathF.Min(s * gain, 1f);
                    }
                }

                for (var o = 0; o < count; o++)
                    program.OctaveMean[first + o] = (float)(sums[o] / n);
            }

            BuildLodTables(program);
            var rows = new float[program.Rows * n];
            var set = new RowSet { X = x, Y = y, Z = z, Ch = rows, Count = n, Stride = n, Channels = program.ChannelCount, W = n, H = 1, Direct = true };
            TerraceSlopes(program, ref set);
            BuildLodTables(program);
            set.Row(PlanetKindProgram.Height).Clear();
            LayerOps.Evaluate(program, 0, ref set);

            for (var layer = 0; layer < program.LayerCount; layer++)
            {
                if (program.Op[layer] != LayerOp.Canyon)
                    continue;

                var pb = program.ParamBase[layer];
                var src = program.SrcA[layer];
                ReadOnlySpan<float> field = set.Row(src), gx = set.Row(src + 1), gy = set.Row(src + 2), gz = set.Row(src + 3);
                var g = new float[n];
                var square = 0.0;

                for (var i = 0; i < n; i++)
                {
                    var radial = (float)((gx[i] * x[i] + gy[i] * y[i] + gz[i] * z[i]) / p.Radius);
                    g[i] = MathF.Sqrt(MathF.Max(gx[i] * gx[i] + gy[i] * gy[i] + gz[i] * gz[i] - radial * radial, 0f));
                    square += (double)g[i] * g[i];
                }

                var gMin = (float)(Math.Sqrt(square / n) / 3.0);
                var mean = 0.0;

                for (var i = 0; i < n; i++)
                    mean += LayerOps.CanyonProfile(MathF.Abs(field[i]) / MathF.Max(g[i], gMin) / program.Params[pb], program.Params[pb + 2], program.Params[pb + 3], _band[layer]);

                program.Params[pb + 4] = gMin;
                program.Params[pb + 5] = (float)(mean / n);
                _params[pb + 4] = program.Params[pb + 4];
                _params[pb + 5] = program.Params[pb + 5];
            }
        }

        /// <summary>
        /// A terrace riser must span a voxel on the ground, so its band in input height grows with the voxel by the
        /// input's RMS slope: measured here over a 64 m step at the calibration points, on the layers before the terrace.
        /// </summary>
        private void TerraceSlopes(PlanetKindProgram program, ref RowSet set)
        {
            const double step = 64.0;
            var n = set.Count;
            double[] x = new double[n], y = new double[n], z = new double[n];
            var input = new float[n];

            for (var layer = 0; layer < program.LayerCount; layer++)
            {
                if (program.Op[layer] != LayerOp.Terrace)
                    continue;

                for (var pass = 0; pass < 2; pass++)
                {
                    for (var i = 0; i < n; i++)
                    {
                        // A tangent step: the cross product with a fixed axis, or another where they are near parallel.
                        double px = set.X[i], py = set.Y[i], pz = set.Z[i];
                        double tx = Math.Abs(px) < 0.9 * p.Radius ? 0.0 : -pz, ty = Math.Abs(px) < 0.9 * p.Radius ? pz : 0.0, tz = Math.Abs(px) < 0.9 * p.Radius ? -py : px;
                        var scale = pass * step / Math.Sqrt(tx * tx + ty * ty + tz * tz);
                        (px, py, pz) = (px + tx * scale, py + ty * scale, pz + tz * scale);
                        var l = p.Radius / Math.Sqrt(px * px + py * py + pz * pz);
                        (x[i], y[i], z[i]) = (px * l, py * l, pz * l);
                    }

                    var moved = set with { X = x, Y = y, Z = z };
                    moved.Row(PlanetKindProgram.Height).Clear();
                    LayerOps.Evaluate(program, 0, ref moved, layer);
                    ReadOnlySpan<float> h = moved.Row(PlanetKindProgram.Height);
                    var strata = program.SrcB[layer] == None ? default : (ReadOnlySpan<float>)moved.Row(program.SrcB[layer]);

                    for (var i = 0; i < n; i++)
                    {
                        var v = strata.IsEmpty ? h[i] : h[i] + strata[i];
                        input[i] = pass == 0 ? v : v - input[i];
                    }
                }

                var square = 0.0;

                for (var i = 0; i < n; i++)
                    square += (double)input[i] * input[i];

                _slope[layer] = (float)(Math.Sqrt(square / n) / step);
            }
        }

        private static void CopyCalibration(PlanetKindProgram from, PlanetKindProgram to)
        {
            for (var layer = 0; layer < from.LayerCount; layer++)
            {
                if (from.Op[layer] != LayerOp.Canyon)
                    continue;

                var pb = from.ParamBase[layer];
                to.Params[pb + 4] = from.Params[pb + 4];
                to.Params[pb + 5] = from.Params[pb + 5];
            }
        }

        private static double Next(ref ulong state)
        {
            state = SplitMix(state);
            return (state >> 11) * (1.0 / (1UL << 53));
        }

        private static PlanetKindProgram Seal(PlanetKindProgram program)
        {
            var hash = Fnv(Encoding.UTF8.GetBytes(program.KindId));
            hash = Fnv(MemoryMarshal.AsBytes(program.Frequency.AsSpan()), hash);
            hash = Fnv(MemoryMarshal.AsBytes(program.OffsetX.AsSpan()), hash);
            hash = Fnv(MemoryMarshal.AsBytes(program.OffsetY.AsSpan()), hash);
            hash = Fnv(MemoryMarshal.AsBytes(program.OffsetZ.AsSpan()), hash);
            hash = Fnv(MemoryMarshal.AsBytes(program.Amplitude.AsSpan()), hash);
            hash = Fnv(MemoryMarshal.AsBytes(program.OctaveMean.AsSpan()), hash);
            hash = Fnv(MemoryMarshal.AsBytes(program.Seed.AsSpan()), hash);
            hash = Fnv(MemoryMarshal.AsBytes(program.Op.AsSpan()), hash);
            hash = Fnv(program.Dst, hash);
            hash = Fnv(program.SrcA, hash);
            hash = Fnv(program.SrcB, hash);
            hash = Fnv(program.Gate, hash);
            hash = Fnv(program.Scale, hash);
            hash = Fnv(program.Warp, hash);
            hash = Fnv(program.Mode, hash);
            hash = Fnv(MemoryMarshal.AsBytes(program.FirstOctave.AsSpan()), hash);
            hash = Fnv(MemoryMarshal.AsBytes(program.OctaveCount.AsSpan()), hash);
            hash = Fnv(MemoryMarshal.AsBytes(program.Params.AsSpan()), hash);
            hash = Fnv(MemoryMarshal.AsBytes(program.RiserBand.AsSpan()), hash);
            hash = Fnv(MemoryMarshal.AsBytes(program.LodWeight.AsSpan()), hash);
            hash = Fnv(MemoryMarshal.AsBytes(program.ClassWeight.AsSpan()), hash);

            foreach (ref readonly var c in program.Classes.AsSpan())
            {
                Span<float> fields = [(float)c.Kind, c.Rotation, c.CellsPerSide, c.Seed, c.RadiusMin, c.RadiusMax, c.Presence, c.Reach, c.Jitter, c.AgeMin, c.AgeMax,
                    c.ComplexDiameter, c.DepthMax, c.RimMax, c.LambdaMin, c.LambdaMax, c.RatioMin, c.RatioMax];
                hash = Fnv(MemoryMarshal.AsBytes(fields), hash);
            }

            Span<double> body = [program.Radius, program.AxisX, program.AxisY, program.AxisZ, program.MinHeight, program.MaxHeight, program.SeaHeight];
            program.ContentHash = Fnv(MemoryMarshal.AsBytes(body), hash);
            return program;
        }
    }
}
