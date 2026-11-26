namespace BrutalGame.Chemistry;

/// <summary>A flow path between two gases: an orifice of an area and discharge coefficient, optionally one way.</summary>
public sealed class FlowLink
{
    public int From;
    public int To;
    public double Area;
    public double Cd = 0.8;

    /// <summary>Flow only from <see cref="From"/> to <see cref="To"/> (an injector, a check valve).</summary>
    public bool OneWay;

    /// <summary>Last mass flow, kg/s, positive From → To.</summary>
    public double MassFlow;
}

/// <summary>A nozzle exhausting a gas into another (usually the fixed ambient), with its last performance.</summary>
public sealed class NozzleLink
{
    public int Chamber;
    public int Ambient;
    public BellNozzle Nozzle = new();
    public NozzlePerformance Last;
}

/// <summary>
/// Gases joined by orifices, pipes and nozzles, stepped in small explicit substeps: every link's flow from its two
/// pressures (compressible orifice equation), moles and enthalpy moved upstream to downstream, then each gas reacts.
/// Reactions are only what the chemistry data says happens when reactants meet: a combustion chamber is a volume where
/// fuel and oxidiser meet and a spark lights them. Fixed gases are supplies and the ambient. A gas with a burst pressure
/// (a pipe) vents to the ambient once exceeded. Deterministic: the same inputs give the same results.
/// </summary>
public sealed class FlowNetwork
{
    public const double MinStep = 1e-7;
    public const double MaxStep = 1e-3;
    public const int MaxSubsteps = 20000;
    private const double MaxFraction = 0.05;

    public readonly ChemistryData Data;
    public readonly List<Gas> Gases = new();
    public readonly List<string> Names = new();
    public readonly List<FlowLink> Links = new();
    public readonly List<NozzleLink> Nozzles = new();

    /// <summary>Per gas: pressure (Pa) above which it bursts and vents to <see cref="Ambient"/>; 0 = never.</summary>
    public readonly List<double> BurstPressure = new();

    public readonly List<bool> Burst = new();

    /// <summary>Per gas: the hole a burst opens (m²), the pipe's bore.</summary>
    public readonly List<double> BurstArea = new();
    private double[] _rates = [];

    public FlowNetwork(ChemistryData data) => Data = data;

    /// <summary>The gas that burst pipes vent to; −1 for none.</summary>
    public int Ambient = -1;

    public double Time { get; private set; }

    public int LastSubsteps { get; private set; }

    /// <summary>Total nozzle thrust averaged over the last step (N).</summary>
    public double Thrust { get; private set; }

    /// <summary>Heat released by reactions over the last step, per second (W).</summary>
    public double HeatRate { get; private set; }

    public int Add(string name, Gas gas, double burstPressure = 0, double burstArea = 0)
    {
        Gases.Add(gas);
        Names.Add(name);
        BurstPressure.Add(burstPressure);
        Burst.Add(false);
        BurstArea.Add(burstArea);
        return Gases.Count - 1;
    }

    public FlowLink Connect(int from, int to, double area, double cd, bool oneWay)
    {
        var link = new FlowLink { From = from, To = to, Area = area, Cd = cd, OneWay = oneWay };
        Links.Add(link);
        return link;
    }

    /// <summary>
    /// A pipe: its bore as a gas of its own volume, joined at both ends by its bore area with half the friction loss each
    /// (discharge coefficient 1 / √(1 + f·L/D), f from <paramref name="friction"/>). Returns the pipe's gas.
    /// </summary>
    public int Pipe(string name, int from, int to, double bore, double length, double burstPressure, double friction = 0.02)
    {
        var area = Math.PI * bore * bore * 0.25;
        var gas = new Gas(Data, Math.Max(area * length, 1e-9));
        var node = Add(name, gas, burstPressure, area);
        var cd = 1.0 / Math.Sqrt(1.0 + friction * 0.5 * length / Math.Max(bore, 1e-6));
        Connect(from, node, area, cd, oneWay: false);
        Connect(node, to, area, cd, oneWay: false);
        return node;
    }

    public NozzleLink Exhaust(int chamber, int ambient, BellNozzle nozzle)
    {
        var link = new NozzleLink { Chamber = chamber, Ambient = ambient, Nozzle = nozzle };
        Nozzles.Add(link);
        return link;
    }

    /// <summary>A spark in gas <paramref name="gas"/>: lights whatever its contents can burn.</summary>
    public bool Spark(int gas) => Gases[gas].Spark();

    /// <summary>Advances by <paramref name="dt"/> seconds in as many substeps as stability needs (capped).</summary>
    public void Step(double dt)
    {
        if (_rates.Length < Links.Count + Nozzles.Count)
            _rates = new double[Links.Count + Nozzles.Count];

        var remaining = dt;
        var substeps = 0;
        var impulse = 0.0;
        var heat = 0.0;

        while (remaining > 1e-12 && substeps < MaxSubsteps)
        {
            Rates();
            var h = Math.Min(remaining, StableStep());
            Apply(h);

            for (var i = 0; i < Gases.Count; i++)
            {
                var gas = Gases[i];

                if (gas.Fixed)
                    continue;

                heat += gas.React(h);
                CheckBurst(i);
            }

            foreach (var n in Nozzles)
                impulse += n.Last.Thrust * h;

            remaining -= h;
            substeps++;
        }

        var done = dt - Math.Max(remaining, 0.0);
        Thrust = done > 0 ? impulse / done : 0.0;
        HeatRate = done > 0 ? heat / done : 0.0;
        Time += done;
        LastSubsteps = substeps;
    }

    /// <summary>Mole rates (mol/s) of every link and nozzle at the current state; nozzle performance refreshed.</summary>
    private void Rates()
    {
        for (var i = 0; i < Links.Count; i++)
        {
            var link = Links[i];
            Gas a = Gases[link.From], b = Gases[link.To];
            double pa = a.Pressure, pb = b.Pressure;
            var forward = pa >= pb;

            if (link.OneWay && !forward)
            {
                link.MassFlow = 0;
                _rates[i] = 0;
                continue;
            }

            var up = forward ? a : b;
            var mdot = GasDynamics.MassFlow(link.Cd, link.Area, Math.Max(pa, pb), up.Temperature, up.Gamma, Gas.R / up.MolarMass, Math.Min(pa, pb));
            link.MassFlow = forward ? mdot : -mdot;
            _rates[i] = (forward ? mdot : -mdot) / up.MolarMass;
        }

        for (var j = 0; j < Nozzles.Count; j++)
        {
            var n = Nozzles[j];
            var chamber = Gases[n.Chamber];
            n.Last = n.Nozzle.Perform(chamber.Pressure, chamber.Temperature, chamber.Gamma, Gas.R / chamber.MolarMass, Gases[n.Ambient].Pressure);
            _rates[Links.Count + j] = n.Last.MassFlow / chamber.MolarMass;
        }
    }

    /// <summary>The longest step in which no gas loses or gains more than a few percent of its moles.</summary>
    private double StableStep()
    {
        var step = MaxStep;

        for (var g = 0; g < Gases.Count; g++)
        {
            var gas = Gases[g];

            if (gas.Fixed)
                continue;

            var flux = 0.0;

            for (var i = 0; i < Links.Count; i++)
            {
                if (Links[i].From == g || Links[i].To == g)
                    flux += Math.Abs(_rates[i]);
            }

            for (var j = 0; j < Nozzles.Count; j++)
            {
                if (Nozzles[j].Chamber == g)
                    flux += Math.Abs(_rates[Links.Count + j]);
            }

            if (flux <= 0)
                continue;

            // An empty volume being filled is limited by what fills it: a few percent of a full one at the source pressure.
            var n = Math.Max(gas.TotalMoles, 1e-9);
            step = Math.Min(step, MaxFraction * n / flux);
        }

        return Math.Max(step, MinStep);
    }

    private void Apply(double h)
    {
        for (var i = 0; i < Links.Count; i++)
        {
            var link = Links[i];
            var moles = _rates[i] * h;

            if (moles > 0)
                Gases[link.From].Transfer(Gases[link.To], moles);
            else if (moles < 0)
                Gases[link.To].Transfer(Gases[link.From], -moles);
        }

        for (var j = 0; j < Nozzles.Count; j++)
        {
            var n = Nozzles[j];
            Gases[n.Chamber].Transfer(Gases[n.Ambient], _rates[Links.Count + j] * h);
        }
    }

    private void CheckBurst(int g)
    {
        if (Burst[g] || BurstPressure[g] <= 0 || Ambient < 0 || Gases[g].Pressure <= BurstPressure[g])
            return;

        // Burst: the gas now leaks to the ambient through a hole the size of the pipe's bore.
        Burst[g] = true;
        Connect(g, Ambient, BurstArea[g], 0.6, oneWay: false);
        _rates = new double[Links.Count + Nozzles.Count];
    }
}
