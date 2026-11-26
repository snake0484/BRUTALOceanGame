namespace BrutalGame.Chemistry;

/// <summary>
/// A well-mixed ideal gas in a fixed volume: moles per species and total energy E = Σ nᵢ (cvᵢ T + hfᵢ), so a reaction
/// that turns formation enthalpy into heat conserves E and raises T by itself. Temperature and pressure are derived.
/// Also used as a fixed supply or ambient (<see cref="Fixed"/>): its state is never changed by flows.
/// </summary>
public sealed class Gas
{
    public const double R = 8.314462618;
    private const double MinMoles = 1e-12;

    private readonly ChemistryData _data;

    public Gas(ChemistryData data, double volumeM3)
    {
        _data = data;
        Volume = volumeM3;
        Moles = new double[data.SpeciesCount];
        Lit = new bool[data.Reactions.Count];
        Kindle = new double[data.Reactions.Count];
    }

    public double Volume;
    public readonly double[] Moles;
    public double Energy;

    /// <summary>Per reaction: burning in this volume.</summary>
    public readonly bool[] Lit;

    /// <summary>Per reaction: seconds left of a spark's flame kernel, during which a cold volume does not put the flame out.</summary>
    public readonly double[] Kindle;

    /// <summary>How long a spark's kernel keeps a reaction lit before the volume must hold it above extinction itself.</summary>
    public const double KindleSeconds = 0.02;

    /// <summary>A supply or ambient: flows never change it.</summary>
    public bool Fixed;

    public ChemistryData Data => _data;

    public double TotalMoles
    {
        get
        {
            var n = 0.0;

            foreach (var m in Moles)
                n += m;

            return n;
        }
    }

    /// <summary>Σ nᵢ cvᵢ (J/K).</summary>
    public double HeatCapacity
    {
        get
        {
            var c = 0.0;

            for (var i = 0; i < Moles.Length; i++)
                c += Moles[i] * _data.Species[i].Cv;

            return c;
        }
    }

    public double FormationEnergy
    {
        get
        {
            var h = 0.0;

            for (var i = 0; i < Moles.Length; i++)
                h += Moles[i] * _data.Species[i].FormationJ;

            return h;
        }
    }

    public double Temperature
    {
        get
        {
            var c = HeatCapacity;
            return c > MinMoles ? Math.Max((Energy - FormationEnergy) / c, 1.0) : 0.0;
        }
    }

    public double Pressure => Volume > 0 ? TotalMoles * R * Temperature / Volume : 0.0;

    public double Mass
    {
        get
        {
            var m = 0.0;

            for (var i = 0; i < Moles.Length; i++)
                m += Moles[i] * _data.Species[i].MolarMass;

            return m;
        }
    }

    /// <summary>Mean molar mass (kg/mol).</summary>
    public double MolarMass
    {
        get
        {
            var n = TotalMoles;
            return n > MinMoles ? Mass / n : 0.029;
        }
    }

    /// <summary>cp / cv of the mixture.</summary>
    public double Gamma
    {
        get
        {
            double cp = 0, cv = 0;

            for (var i = 0; i < Moles.Length; i++)
            {
                cp += Moles[i] * _data.Species[i].Cp;
                cv += Moles[i] * _data.Species[i].Cv;
            }

            return cv > MinMoles ? cp / cv : 1.4;
        }
    }

    public double MoleFraction(int species)
    {
        var n = TotalMoles;
        return n > MinMoles ? Moles[species] / n : 0.0;
    }

    /// <summary>Replaces the contents: <paramref name="mixture"/> at the given pressure and temperature.</summary>
    public void Fill(Mixture mixture, double pressure, double temperature)
    {
        var n = pressure * Volume / (R * temperature);

        for (var i = 0; i < Moles.Length; i++)
            Moles[i] = mixture.Fractions[i] * n;

        SetTemperature(temperature);
        Array.Clear(Lit);
        Array.Clear(Kindle);
    }

    public void SetTemperature(double temperature) => Energy = FormationEnergy + HeatCapacity * temperature;

    /// <summary>Molar enthalpy carried by a flow out of this gas (J/mol): Σ xᵢ (cpᵢ T + hfᵢ).</summary>
    public double MolarEnthalpy()
    {
        var n = TotalMoles;

        if (n <= MinMoles)
            return 0.0;

        var t = Temperature;
        var h = 0.0;

        for (var i = 0; i < Moles.Length; i++)
            h += Moles[i] * (_data.Species[i].Cp * t + _data.Species[i].FormationJ);

        return h / n;
    }

    /// <summary>
    /// Equivalence ratio of a reaction's first two reactants here: (n₀/n₁) / (ν₀/ν₁). 1 is stoichiometric, above is rich.
    /// </summary>
    public double Equivalence(Reaction r)
    {
        var a = r.Reactants[0];
        var b = r.Reactants[1];
        var nb = Moles[b.Species];

        if (nb <= MinMoles)
            return double.PositiveInfinity;

        return Moles[a.Species] / nb / (a.Coefficient / b.Coefficient);
    }

    public bool Flammable(Reaction r)
    {
        foreach (var t in r.Reactants)
        {
            if (Moles[t.Species] <= MinMoles)
                return false;
        }

        var phi = Equivalence(r);
        return phi >= r.EquivalenceMin && phi <= r.EquivalenceMax;
    }

    /// <summary>A spark: lights every reaction whose reactants are here in a flammable mix. True if something lit.</summary>
    public bool Spark()
    {
        var any = false;

        foreach (var r in _data.Reactions)
        {
            if (Flammable(r))
            {
                Lit[r.Index] = true;
                Kindle[r.Index] = KindleSeconds;
                any = true;
            }
        }

        return any;
    }

    /// <summary>
    /// Advances the reactions by <paramref name="dt"/> seconds. Lights any reaction whose mix is flammable and above its
    /// autoignition temperature, puts out those below extinction once a spark's kernel has passed, and burns the lit ones
    /// (extent capped by the limiting reactant, so a lit flame burns whatever reaches it). Energy is untouched: the heat appears through the formation enthalpies. Returns the heat released (J).
    /// </summary>
    public double React(double dt)
    {
        var heat = 0.0;

        foreach (var r in _data.Reactions)
        {
            var t = Temperature;

            if (!Lit[r.Index])
            {
                // Lighting needs the mix inside its flammable window; a lit flame then burns whatever reaches it.
                if (t < r.AutoignitionK || !Flammable(r))
                    continue;

                Lit[r.Index] = true;
            }
            else if (t < r.ExtinctionK && Kindle[r.Index] <= 0)
            {
                Lit[r.Index] = false;
                continue;
            }

            Kindle[r.Index] = Math.Max(0.0, Kindle[r.Index] - dt);
            var limit = LimitingExtent(r);
            var k = r.Rate * Math.Exp(-r.ActivationJ / (R * Math.Max(t, r.FlameK)));
            var extent = Math.Min(limit, limit * (1.0 - Math.Exp(-k * dt)));

            if (extent <= 0)
                continue;

            foreach (var term in r.Reactants)
                Moles[term.Species] = Math.Max(0.0, Moles[term.Species] - term.Coefficient * extent);

            foreach (var term in r.Products)
                Moles[term.Species] += term.Coefficient * extent;

            heat -= r.EnthalpyJ * extent;
        }

        return heat;
    }

    private double LimitingExtent(Reaction r)
    {
        var limit = double.MaxValue;

        foreach (var t in r.Reactants)
            limit = Math.Min(limit, Moles[t.Species] / t.Coefficient);

        return limit;
    }

    /// <summary>Moves <paramref name="moles"/> of this gas's mixture (with its enthalpy) into <paramref name="to"/>.</summary>
    public void Transfer(Gas to, double moles)
    {
        var n = TotalMoles;

        if (moles <= 0 || n <= MinMoles)
            return;

        moles = Math.Min(moles, n);
        var h = MolarEnthalpy() * moles;
        var fraction = moles / n;

        for (var i = 0; i < Moles.Length; i++)
        {
            var dn = Moles[i] * fraction;

            if (!to.Fixed)
                to.Moles[i] += dn;

            if (!Fixed)
                Moles[i] -= dn;
        }

        // A flow leaves with its enthalpy h = u + pv; the flow work makes the receiver warmer than the source.
        if (!to.Fixed)
            to.Energy += h;

        if (!Fixed)
            Energy -= h;
    }
}
