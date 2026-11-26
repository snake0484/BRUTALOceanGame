using System.Text.Json;

namespace BrutalGame.Chemistry;

/// <summary>An ideal gas: molar mass (kg/mol), constant cp (J/(mol·K)), formation enthalpy (J/mol), atom counts.</summary>
public sealed class Species
{
    public int Index;
    public string Key = "";
    public string Name = "";
    public double MolarMass;
    public double Cp;
    public double FormationJ;
    public Dictionary<string, int> Atoms = new();

    public double Cv => Cp - Gas.R;
}

/// <summary>A species and how many moles of it one unit of reaction extent takes or makes.</summary>
public readonly record struct Term(int Species, double Coefficient);

/// <summary>
/// A gas-phase reaction (data/chemistry/reactions.json). Heat comes from the species' formation enthalpies, so energy is
/// conserved by construction. It burns in a volume whose reactants are within the flammability window and which was lit:
/// by a spark, or by being above <see cref="AutoignitionK"/>. Once lit it holds while the mix stays flammable and the volume
/// stays above <see cref="ExtinctionK"/>. Rate (extent per second) = <see cref="Rate"/> · exp(−Ea / (R · max(T, FlameK)))
/// · moles of the limiting reactant per unit coefficient.
/// </summary>
public sealed class Reaction
{
    public int Index;
    public string Key = "";
    public string Name = "";
    public Term[] Reactants = [];
    public Term[] Products = [];
    public double EquivalenceMin;
    public double EquivalenceMax;
    public double AutoignitionK;
    public double ExtinctionK;
    public double FlameK;
    public double Rate;
    public double ActivationJ;

    /// <summary>Enthalpy change per unit extent (J; negative releases heat).</summary>
    public double EnthalpyJ;
}

/// <summary>A named mixture (mole fractions per species index).</summary>
public sealed class Mixture
{
    public string Key = "";
    public string Name = "";
    public double[] Fractions = [];
}

/// <summary>The chemistry tables, loaded once from <c>data/chemistry</c>. Read-only after loading: share it between threads.</summary>
public sealed class ChemistryData
{
    public const string Folder = "chemistry";

    public readonly List<Species> Species = new();
    public readonly List<Reaction> Reactions = new();
    public readonly List<Mixture> Mixtures = new();

    public int SpeciesCount => Species.Count;

    public int IndexOf(string key) => Species.FindIndex(s => s.Key == key);

    public Mixture? Mixture(string key) => Mixtures.Find(m => m.Key == key);

    public static ChemistryData Load(string dataFolder)
    {
        var folder = Path.Combine(dataFolder, Folder);
        return Parse(File.ReadAllText(Path.Combine(folder, "species.json")), File.ReadAllText(Path.Combine(folder, "reactions.json")), File.ReadAllText(Path.Combine(folder, "mixtures.json")));
    }

    public static ChemistryData Parse(string speciesJson, string reactionsJson, string mixturesJson)
    {
        var data = new ChemistryData();

        using (var doc = JsonDocument.Parse(speciesJson))
        {
            foreach (var e in doc.RootElement.GetProperty("species").EnumerateArray())
            {
                var s = new Species
                {
                    Index = data.Species.Count,
                    Key = e.GetProperty("id").GetString()!,
                    Name = e.GetProperty("name").GetString()!,
                    MolarMass = e.GetProperty("molarMassG").GetDouble() / 1000.0,
                    Cp = e.GetProperty("cpJmolK").GetDouble(),
                    FormationJ = e.GetProperty("hfKJmol").GetDouble() * 1000.0,
                };

                foreach (var atom in e.GetProperty("atoms").EnumerateObject())
                    s.Atoms[atom.Name] = atom.Value.GetInt32();

                if (s.MolarMass <= 0 || s.Cp <= Gas.R)
                    throw new InvalidDataException($"species {s.Key}: bad molar mass or cp");

                data.Species.Add(s);
            }
        }

        using (var doc = JsonDocument.Parse(reactionsJson))
        {
            foreach (var e in doc.RootElement.GetProperty("reactions").EnumerateArray())
            {
                var r = new Reaction
                {
                    Index = data.Reactions.Count,
                    Key = e.GetProperty("id").GetString()!,
                    Name = e.GetProperty("name").GetString()!,
                    Reactants = Terms(data, e.GetProperty("reactants")),
                    Products = Terms(data, e.GetProperty("products")),
                    EquivalenceMin = e.GetProperty("equivalenceMin").GetDouble(),
                    EquivalenceMax = e.GetProperty("equivalenceMax").GetDouble(),
                    AutoignitionK = e.GetProperty("autoignitionK").GetDouble(),
                    ExtinctionK = e.GetProperty("extinctionK").GetDouble(),
                    FlameK = e.GetProperty("flameK").GetDouble(),
                    Rate = e.GetProperty("rate").GetDouble(),
                    ActivationJ = e.GetProperty("activationKJmol").GetDouble() * 1000.0,
                };

                if (r.Reactants.Length < 2)
                    throw new InvalidDataException($"reaction {r.Key}: needs two reactants");

                foreach (var t in r.Products)
                    r.EnthalpyJ += t.Coefficient * data.Species[t.Species].FormationJ;

                foreach (var t in r.Reactants)
                    r.EnthalpyJ -= t.Coefficient * data.Species[t.Species].FormationJ;

                CheckAtoms(data, r);
                data.Reactions.Add(r);
            }
        }

        using (var doc = JsonDocument.Parse(mixturesJson))
        {
            foreach (var e in doc.RootElement.GetProperty("mixtures").EnumerateArray())
            {
                var m = new Mixture { Key = e.GetProperty("id").GetString()!, Name = e.GetProperty("name").GetString()!, Fractions = new double[data.Species.Count] };
                var sum = 0.0;

                foreach (var f in e.GetProperty("fractions").EnumerateObject())
                {
                    var i = data.IndexOf(f.Name);

                    if (i < 0)
                        throw new InvalidDataException($"mixture {m.Key}: unknown species {f.Name}");

                    m.Fractions[i] = f.Value.GetDouble();
                    sum += m.Fractions[i];
                }

                for (var i = 0; i < m.Fractions.Length; i++)
                    m.Fractions[i] /= sum;

                data.Mixtures.Add(m);
            }
        }

        return data;
    }

    private static Term[] Terms(ChemistryData data, JsonElement element)
    {
        var terms = new List<Term>();

        foreach (var p in element.EnumerateObject())
        {
            var i = data.IndexOf(p.Name);

            if (i < 0)
                throw new InvalidDataException($"unknown species {p.Name}");

            terms.Add(new Term(i, p.Value.GetDouble()));
        }

        return terms.ToArray();
    }

    /// <summary>Refuses a reaction whose atoms do not balance.</summary>
    private static void CheckAtoms(ChemistryData data, Reaction r)
    {
        var balance = new Dictionary<string, double>();

        foreach (var t in r.Reactants)
        {
            foreach (var (atom, n) in data.Species[t.Species].Atoms)
                balance[atom] = balance.GetValueOrDefault(atom) + n * t.Coefficient;
        }

        foreach (var t in r.Products)
        {
            foreach (var (atom, n) in data.Species[t.Species].Atoms)
                balance[atom] = balance.GetValueOrDefault(atom) - n * t.Coefficient;
        }

        foreach (var (atom, left) in balance)
        {
            if (Math.Abs(left) > 1e-9)
                throw new InvalidDataException($"reaction {r.Key}: {atom} does not balance ({left})");
        }
    }
}
