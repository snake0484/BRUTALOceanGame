namespace BrutalGame.Chemistry.Tests;

/// <summary>Reactions from data: nothing without a spark, conservation of atoms and energy, flammability limits, flow and nozzles.</summary>
public class ChemistryTests
{
    private static readonly ChemistryData Data = ChemistryData.Load(Path.Combine(AppContext.BaseDirectory, "data"));

    private static Gas Mix(double volume, double temperature, params (string Key, double Moles)[] contents)
    {
        var gas = new Gas(Data, volume);

        foreach (var (key, moles) in contents)
            gas.Moles[Data.IndexOf(key)] = moles;

        gas.SetTemperature(temperature);
        return gas;
    }

    private static Dictionary<string, double> Atoms(Gas gas)
    {
        var atoms = new Dictionary<string, double>();

        for (var i = 0; i < gas.Moles.Length; i++)
        {
            foreach (var (atom, n) in Data.Species[i].Atoms)
                atoms[atom] = atoms.GetValueOrDefault(atom) + n * gas.Moles[i];
        }

        return atoms;
    }

    [Fact]
    public void Reactions_BalanceAndReleaseHeat()
    {
        var methane = Data.Reactions.Single(r => r.Key == "rx.methane.oxygen");

        // CH4 + 2 O2 -> CO2 + 2 H2O(g): -802.3 kJ.
        Assert.Equal(-802.3e3, methane.EnthalpyJ, 0);
    }

    [Fact]
    public void FuelAndOxidiser_DoNothingWithoutASpark()
    {
        var gas = Mix(0.01, 300, ("gas.ch4", 1), ("gas.o2", 2));
        gas.React(1.0);

        Assert.Equal(1.0, gas.Moles[Data.IndexOf("gas.ch4")], 12);
        Assert.Equal(300, gas.Temperature, 6);
    }

    [Fact]
    public void Spark_BurnsAClosedVolume_ConservingAtomsAndEnergy()
    {
        var gas = Mix(0.01, 300, ("gas.ch4", 0.1), ("gas.o2", 0.2), ("gas.n2", 0.5));
        var atoms = Atoms(gas);
        var energy = gas.Energy;
        var p0 = gas.Pressure;

        Assert.True(gas.Spark());

        for (var i = 0; i < 200; i++)
            gas.React(1e-4);

        Assert.True(gas.Moles[Data.IndexOf("gas.ch4")] < 1e-6);
        Assert.Equal(0.1, gas.Moles[Data.IndexOf("gas.co2")], 5);
        Assert.Equal(energy, gas.Energy, 6);

        foreach (var (atom, n) in Atoms(gas))
            Assert.Equal(atoms[atom], n, 9);

        // 80.2 kJ into 0.8 mol of products and nitrogen: a flame-hot volume at several times the pressure.
        Assert.InRange(gas.Temperature, 2000, 4000);
        Assert.True(gas.Pressure > 6 * p0);
    }

    [Fact]
    public void Spark_DoesNotLightOutsideTheFlammableWindow()
    {
        var lean = Mix(0.01, 300, ("gas.ch4", 0.01), ("gas.o2", 1));
        Assert.False(lean.Spark());
        lean.React(0.1);
        Assert.Equal(0.01, lean.Moles[Data.IndexOf("gas.ch4")], 12);
    }

    [Fact]
    public void HotEnough_IgnitesWithoutASpark()
    {
        var gas = Mix(0.01, 1200, ("gas.h2", 0.2), ("gas.o2", 0.1));
        gas.React(0.01);
        Assert.True(gas.Moles[Data.IndexOf("gas.h2")] < 0.2);
    }

    [Fact]
    public void OneWayLink_BlocksBackflow()
    {
        var network = new FlowNetwork(Data);
        var high = network.Add("high", Mix(0.01, 300, ("gas.n2", 4)));
        var low = network.Add("low", Mix(0.01, 300, ("gas.n2", 1)));
        network.Connect(low, high, 1e-5, 0.8, oneWay: true);
        network.Step(0.1);

        Assert.Equal(4.0, network.Gases[high].TotalMoles, 9);
        Assert.Equal(1.0, network.Gases[low].TotalMoles, 9);

        network.Links[0].From = high;
        network.Links[0].To = low;
        network.Step(0.1);
        Assert.True(network.Gases[low].TotalMoles > 1.0);
        Assert.Equal(5.0, network.Gases[high].TotalMoles + network.Gases[low].TotalMoles, 9);
    }

    private static BellNozzle Nozzle(double initialDeg, double exitDeg, double length, double exitRadius = 0.1, double throatRadius = 0.03) => new()
    {
        ChamberRadius = Math.Max(0.06, throatRadius * 2),
        ThroatRadius = throatRadius,
        ExitRadius = exitRadius,
        ConvergingLength = 0.05,
        BellLength = length,
        InitialAngle = initialDeg * Math.PI / 180,
        ExitAngle = exitDeg * Math.PI / 180,
        ThroatCd = 1.0,
    };

    [Fact]
    public void Nozzle_MatchesIdealRocketTheoryInVacuum()
    {
        // A straight 0° exit removes divergence loss: F must equal C_F · p0 · A_t from the ideal rocket equations.
        const double gamma = 1.25, rs = 360.0, p0 = 5e6, t0 = 3000;
        var nozzle = Nozzle(25, 0.001, 0.4);
        var perf = nozzle.Perform(p0, t0, gamma, rs, 0.0);
        var eps = nozzle.ExpansionRatio;
        var me = GasDynamics.MachFromArea(eps, gamma, true);
        var pe = GasDynamics.PressureRatio(me, gamma);
        var cf = Math.Sqrt(2 * gamma * gamma / (gamma - 1) * Math.Pow(2 / (gamma + 1), (gamma + 1) / (gamma - 1)) * (1 - Math.Pow(pe, (gamma - 1) / gamma))) + pe * eps;

        Assert.True(perf.Choked);
        Assert.Equal(cf * p0 * nozzle.ThroatArea, perf.Thrust, perf.Thrust * 1e-3);
    }

    [Fact]
    public void BellShape_ChangesThrust()
    {
        const double gamma = 1.22, rs = 380.0, p0 = 3e6, t0 = 3200;
        var cone = Nozzle(15, 15, (0.1 - 0.03) / Math.Tan(15 * Math.PI / 180));
        var bell = Nozzle(30, 8, cone.BellLength * 0.8);

        Assert.True(cone.IsValid && bell.IsValid);
        var c = cone.Perform(p0, t0, gamma, rs, 0.0);
        var b = bell.Perform(p0, t0, gamma, rs, 0.0);

        // Same expansion, shorter: the bell turns the flow straighter at the lip and loses less to divergence.
        Assert.Equal(c.MassFlow, b.MassFlow, 9);
        Assert.True(b.Thrust > c.Thrust * 1.008);
        Assert.InRange(c.DivergenceEfficiency, 0.982, 0.984);
    }

    [Fact]
    public void OverExpandedBell_SeparatesAtSeaLevel()
    {
        const double gamma = 1.22, rs = 380.0, p0 = 1e6, t0 = 3200;
        var big = Nozzle(30, 8, 0.6, exitRadius: 0.25);
        var vacuum = big.Perform(p0, t0, gamma, rs, 0.0);
        var sea = big.Perform(p0, t0, gamma, rs, 101325);

        Assert.False(vacuum.Separated);
        Assert.True(sea.Separated);
        Assert.True(sea.Thrust < vacuum.Thrust);
        Assert.True(sea.Thrust > 0);
    }

    [Fact]
    public void Engine_IgnitesOnSparkAndMakesThrust()
    {
        var network = new FlowNetwork(Data);
        var fuel = network.Add("fuel", Mix(1, 300, ("gas.ch4", 1)));
        network.Gases[fuel].Fill(Data.Mixture("mix.methane")!, 2.0e6, 300);
        network.Gases[fuel].Fixed = true;
        var ox = network.Add("ox", new Gas(Data, 1));
        network.Gases[ox].Fill(Data.Mixture("mix.oxygen")!, 2.0e6, 300);
        network.Gases[ox].Fixed = true;
        var space = network.Add("space", new Gas(Data, 1) { Fixed = true });
        network.Ambient = space;
        var chamber = network.Add("chamber", new Gas(Data, 0.002));
        network.Connect(fuel, chamber, 3.0e-5, 0.7, oneWay: true);
        network.Connect(ox, chamber, 5.0e-5, 0.7, oneWay: true);
        network.Exhaust(chamber, space, Nozzle(30, 8, 0.08, exitRadius: 0.03, throatRadius: 0.006));

        for (var i = 0; i < 30; i++)
            network.Step(1.0 / 60);

        var cold = network.Thrust;
        Assert.True(network.Spark(chamber));

        for (var i = 0; i < 60; i++)
            network.Step(1.0 / 60);

        var gas = network.Gases[chamber];
        Assert.True(gas.Lit[0]);
        Assert.InRange(gas.Temperature, 1500, 4500);
        Assert.True(network.Thrust > 1.5 * cold, $"cold {cold} N, hot {network.Thrust} N");
        Assert.InRange(network.Nozzles[0].Last.Isp, 150, 450);
    }

    [Fact]
    public void Pipe_BurstsAboveItsRating()
    {
        var network = new FlowNetwork(Data);
        var supply = network.Add("supply", new Gas(Data, 1) { Fixed = true });
        network.Gases[supply].Fill(Data.Mixture("mix.air")!, 5e6, 300);
        var space = network.Add("space", new Gas(Data, 1) { Fixed = true });
        network.Ambient = space;
        var sink = network.Add("sink", new Gas(Data, 0.001));
        var pipe = network.Pipe("pipe", supply, sink, 0.01, 1.0, burstPressure: 2e6);

        for (var i = 0; i < 60; i++)
            network.Step(1.0 / 60);

        Assert.True(network.Burst[pipe]);
    }
}
