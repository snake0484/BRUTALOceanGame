using System.Text.Json;

namespace BrutalGame.Crafting;

/// <summary>
/// Parses and validates crafting data. It refuses impossible content rather than clamping it: recipes whose output could
/// exceed <c>QMax</c> or lower purity, inputs no stock can meet, loss-free loops, placeable materials that yield EM,
/// sources above the cap, unknown items, duplicate ids and unknown versions. All refusals are reported together.
/// </summary>
internal sealed class CraftingLoader
{
    private const string ItemsDir = "items/";
    private const string AdditivesDir = "em_additives/";
    private const string MaterialsDir = "materials/voxel/";
    private const string RecipesDir = "recipes/";
    private const int NoMax = int.MaxValue;

    private sealed record PendingRecipe(string File, string Key, JsonElement Json, string? OutputItem, bool Additive, bool Crude);

    private readonly List<(string Path, JsonElement Root)> _files = new();
    private readonly List<string> _errors = new();
    private readonly List<ItemDef> _items = new();
    private readonly List<(ItemDef Item, JsonElement Effect, JsonElement TradeOff)> _additiveJson = new();
    private readonly List<PendingRecipe> _pending = new();
    private readonly List<string> _families = new();
    private readonly List<string> _factorKeys = new();
    private readonly Dictionary<string, ItemDef> _byKey = new(StringComparer.Ordinal);

    private int _qMax = Fixed.One;
    private int _tickRateHz = 60;
    private double _amProductionJPerKg;
    private AdditiveRules _additives = new();

    public CraftingLoader(IEnumerable<(string Path, string Json)> files)
    {
        foreach (var (path, json) in files)
        {
            if (!Relevant(path))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                var root = doc.RootElement.Clone();

                if (root.ValueKind != JsonValueKind.Object)
                    Error(path, "root is not an object");
                else if (!root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != CraftingData.Version)
                    Error(path, $"unsupported or missing version (expected {CraftingData.Version})");
                else
                    _files.Add((path, root));
            }
            catch (JsonException e)
            {
                Error(path, "invalid JSON: " + e.Message);
            }
        }

        _files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
    }

    public CraftingData Build()
    {
        var exotic = Find(CraftingData.ExoticPath);
        var pricingJson = Find(CraftingData.PricingPath);

        if (exotic is null)
            Error(CraftingData.ExoticPath, "missing");

        if (pricingJson is null)
            Error(CraftingData.PricingPath, "missing");

        if (exotic is null || pricingJson is null)
            throw new CraftingDataException(_errors);

        var (purityBase, crystal, am) = ParseExotic(exotic.Value);
        var pricing = ParsePricing(pricingJson.Value);

        foreach (var (path, root) in _files)
        {
            if (path.StartsWith(ItemsDir, StringComparison.Ordinal))
                ParseItem(path, root, crystal);
            else if (path.StartsWith(AdditivesDir, StringComparison.Ordinal))
                ParseAdditive(path, root);
        }

        var data = AssignItems(purityBase, crystal, am, pricing);
        var materials = new List<MaterialDef>();
        var recipes = new List<RecipeDef>();

        foreach (var (path, root) in _files)
        {
            if (path.StartsWith(MaterialsDir, StringComparison.Ordinal))
                ParseMaterial(path, root, materials);
            else if (path.StartsWith(RecipesDir, StringComparison.Ordinal))
                _pending.Add(new PendingRecipe(path, Str(root, "id", path), root, null, false, false));
        }

        foreach (var p in _pending)
        {
            var recipe = ParseRecipe(p);

            if (recipe is not null)
                recipes.Add(recipe);
        }

        recipes.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        materials.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        CheckDuplicates(recipes.Select(r => r.Key), "recipe");
        CheckDuplicates(materials.Select(m => m.Key), "material");

        for (var i = 0; i < recipes.Count; i++)
            recipes[i].Id = new RecipeId((ushort)(i + 1));

        var gates = DeriveGates(purityBase, recipes);

        if (_errors.Count > 0)
            throw new CraftingDataException(_errors);

        return Finish(data, purityBase, gates, materials, recipes);
    }

    private static bool Relevant(string path) =>
        path == CraftingData.ExoticPath || path == CraftingData.PricingPath
        || path.StartsWith(ItemsDir, StringComparison.Ordinal) || path.StartsWith(AdditivesDir, StringComparison.Ordinal)
        || path.StartsWith(MaterialsDir, StringComparison.Ordinal) || path.StartsWith(RecipesDir, StringComparison.Ordinal);

    private JsonElement? Find(string path)
    {
        foreach (var (p, root) in _files)
        {
            if (p == path)
                return root;
        }

        return null;
    }

    private (PurityRules Purity, DecayRule Crystal, (double AmPerEm, double Energy, double Production, double Core) Am) ParseExotic(JsonElement e)
    {
        const string f = CraftingData.ExoticPath;
        _tickRateHz = (int)Num(e, "tickRateHz", f);
        _qMax = Fixed.Ppm(Num(e, "qMax", f));

        if (_tickRateHz <= 0)
            Error(f, "tickRateHz must be positive");

        if (_qMax <= 0 || _qMax >= Fixed.One)
            Error(f, "qMax must lie in (0, 1): complete purity is never reached");

        var names = new List<string>();
        var edges = new List<int>();

        if (e.TryGetProperty("bands", out var bands) && bands.ValueKind == JsonValueKind.Array)
        {
            foreach (var band in bands.EnumerateArray())
            {
                if (band.ValueKind != JsonValueKind.Array || band.GetArrayLength() != 2)
                {
                    Error(f, "a band is not [name, lowerEdge]");
                    continue;
                }

                names.Add(band[0].GetString() ?? "");
                edges.Add(Fixed.Ppm(band[1].GetDouble()));
            }
        }

        if (edges.Count == 0 || edges[0] != 0)
            Error(f, "bands must start at 0");

        for (var i = 1; i < edges.Count; i++)
        {
            if (edges[i] <= edges[i - 1])
                Error(f, "band edges must ascend");
        }

        if (edges.Count > 0 && edges[^1] >= _qMax)
            Error(f, "the top band starts at or above qMax");

        var bucket = Fixed.Ppm(Num(e, "mergeBucket", f));

        if (bucket <= 0)
            Error(f, "mergeBucket must be positive");

        var crystalJson = Obj(e, "crystal", f);
        var crystalBand = names.IndexOf("Crystal") is var ci and >= 0 ? edges[ci] : edges.Count > 0 ? edges[^1] : _qMax;
        var floor = Fixed.Ppm(Num(crystalJson, "floor", f));
        var rate = Fixed.Ppm(Num(crystalJson, "decayPerDay", f));
        var shock = Fixed.Ppm(Num(crystalJson, "shockLoss", f));

        if (floor <= 0 || floor >= crystalBand)
            Error(f, "crystal floor must lie below the Crystal band");

        if (rate < 0 || shock < 0)
            Error(f, "crystal decay and shock must not be negative");

        var add = Obj(e, "additives", f);
        _additives = new AdditiveRules
        {
            MaxKinds = (int)Num(add, "maxKinds", f),
            MaxMassFractionPpm = Fixed.Ppm(Num(add, "maxMassFraction", f)),
            FullActivePpm = Fixed.Ppm(Num(add, "fullActiveFraction", f)),
            PotencyBucketPpm = Fixed.Ppm(Num(add, "potencyBucket", f)),
            LabPotencyPpm = Fixed.Ppm(Num(add, "labPotency", f)),
            CrudeLabPotencyPpm = Fixed.Ppm(Num(add, "crudeLabPotency", f)),
            CrudeRateFactor = Num(add, "crudeRateFactor", f),
        };

        if (_additives.MaxKinds <= 0 || _additives.FullActivePpm <= 0 || _additives.PotencyBucketPpm <= 0 || _additives.CrudeRateFactor <= 0)
            Error(f, "additive rules must be positive");

        if (_additives.LabPotencyPpm >= Fixed.One || _additives.CrudeLabPotencyPpm >= Fixed.One)
            Error(f, "additive potency must stay below 1");

        var amPerEm = Num(e, "amPerEm", f);
        var energy = Num(e, "amEnergyJPerKg", f);
        _amProductionJPerKg = Num(e, "amProductionJPerKg", f);
        var core = Opt(e, "coreEfficiency", 0.4);

        if (_amProductionJPerKg <= 0 || energy <= 0 || core <= 0 || core > 1)
            Error(f, "antimatter energies must be positive and coreEfficiency in (0, 1]");
        else if (energy * core >= _amProductionJPerKg)
            Error(f, "antimatter round trip returns as much energy as it costs (free energy)");

        var gates = new List<int>();

        if (e.TryGetProperty("deviceGates", out var dg) && dg.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in dg.EnumerateArray())
                gates.Add(Fixed.Ppm(g.GetDouble()));
        }

        var purity = new PurityRules(_qMax, bucket, Math.Max(1, _additives.PotencyBucketPpm), names.ToArray(), edges.ToArray(), gates.ToArray());
        var ticksPerDay = Math.Max(1, _tickRateHz) * 86_400L;
        return (purity, new DecayRule(crystalBand, floor, rate, shock, ticksPerDay), (amPerEm, energy, _amProductionJPerKg, core));
    }

    private PricingRules ParsePricing(JsonElement e)
    {
        const string f = CraftingData.PricingPath;
        var retail = Obj(e, "retail", f);
        var nre = Obj(e, "nre", f);
        var buyback = Obj(e, "buyback", f);
        var p = new PricingRules
        {
            Wage = Num(e, "wage", f), CatalogScale = Num(e, "catalogScale", f), VendorMargin = Num(e, "vendorMargin", f),
            RetailStock = Num(retail, "stock", f), RetailPart = Num(retail, "part", f), RetailComponent = Num(retail, "component", f),
            YardMargin = Num(e, "yardMargin", f), JobLabour = Num(e, "jobLabour", f), JobMargin = Num(e, "jobMargin", f),
            RawMarkup = Num(e, "rawMarkup", f), MachineRate = Num(e, "machineRate", f), DesignStamp = Num(e, "designStamp", f),
            NrePart = Num(nre, "part", f), NreComponent = Num(nre, "component", f), NreShip = Num(nre, "ship", f),
            NrePerDistinct = Num(nre, "perDistinct", f), NrePerJoint = Num(nre, "perJoint", f),
            ToolingPerMaterial = Num(e, "toolingPerMaterial", f), PartLicence = Num(e, "partLicence", f),
            ComponentLicence = Num(e, "componentLicence", f), PlanLicencePerBuild = Num(e, "planLicencePerBuild", f),
            BuybackItemOfWholesale = Num(buyback, "itemOfWholesale", f), BuybackShipOfPremade = Num(buyback, "shipOfPremade", f),
            BuybackShipCertified = Num(buyback, "shipCertified", f),
        };

        if (p.Wage <= 0 || p.CatalogScale <= 0 || p.RawMarkup < 1 || p.DesignStamp <= p.PlanLicencePerBuild)
            Error(f, "pricing: wage and scale must be positive, rawMarkup ≥ 1, designStamp above planLicencePerBuild");

        if (p.BuybackItemOfWholesale >= 1 || p.BuybackShipOfPremade >= 1 || p.BuybackShipCertified >= 1)
            Error(f, "pricing: buyback must stay below the price (no produce-and-sell profit)");

        return p;
    }

    private void ParseItem(string f, JsonElement e, DecayRule crystal)
    {
        var key = Str(e, "id", f);
        var family = Str(e, "family", f);
        var item = MakeItem(f, key, OptStr(e, "name") ?? key, family, e, crystal);

        if (item is null)
            return;

        if (e.TryGetProperty("recipe", out var recipe) && recipe.ValueKind == JsonValueKind.Object)
            _pending.Add(new PendingRecipe(f, "make." + key, recipe, key, false, false));
    }

    private void ParseAdditive(string f, JsonElement e)
    {
        var key = Str(e, "id", f);
        var itemJson = Obj(e, "item", f);
        var item = MakeItem(f, key, OptStr(e, "name") ?? key, OptStr(itemJson, "family") ?? "additive", itemJson, default);

        if (item is null)
            return;

        if (item.Content != ContentKind.Active)
            Error(f, "an additive item must hold active content");

        _additiveJson.Add((item, e.TryGetProperty("effect", out var fx) ? fx : default, e.TryGetProperty("tradeOff", out var to) ? to : default));

        if (!e.TryGetProperty("recipe", out var recipe) || recipe.ValueKind != JsonValueKind.Object)
            return;

        _pending.Add(new PendingRecipe(f, "make." + key, recipe, key, true, false));

        if (OptBool(recipe, "crude"))
            _pending.Add(new PendingRecipe(f, "make." + key + ".crude", recipe, key, true, true));
    }

    private ItemDef? MakeItem(string f, string key, string name, string family, JsonElement e, DecayRule crystal)
    {
        var content = OptStr(e, "content") switch
        {
            "None" => ContentKind.None,
            "Exotic" => ContentKind.Exotic,
            "Active" => ContentKind.Active,
            "Antimatter" => ContentKind.Antimatter,
            null when family is "em" or "em_slurry" => ContentKind.Exotic,
            null when family == "additive" => ContentKind.Active,
            null when e.TryGetProperty("antimatterCapacityG", out _) => ContentKind.Antimatter,
            null => ContentKind.None,
            var other => Bad(f, $"item {key}: unknown content kind {other}"),
        };
        var density = (int)Num(e, "densityKgM3", f);

        if (density <= 0 || density > 30_000)
            Error(f, $"item {key}: density {density} kg/m³ out of range");

        var item = new ItemDef
        {
            Key = key, Name = name, Family = family, Content = content, DensityKgM3 = Math.Max(1, density),
            UnitMassMg = Fixed.Mg(Opt(e, "unitMassKg", 0)), CapacityMg = Fixed.Mg(Opt(e, "antimatterCapacityG", 0) / 1000),
            RefCostPerKg = Opt(e, "refCostPerKg", 0),
        };

        if (e.TryGetProperty("metastable", out var meta))
        {
            if (meta.ValueKind == JsonValueKind.String && meta.GetString() == "crystal")
            {
                if (content != ContentKind.Exotic)
                    Error(f, $"item {key}: only EM can be crystal-metastable");

                item.Decay = crystal;
            }
            else if (meta.ValueKind == JsonValueKind.Object)
            {
                var floor = Fixed.Ppm(Num(meta, "floorPotency", f));
                var rate = Fixed.Ppm(Num(meta, "decayPerDay", f));

                if (floor <= 0 || floor >= _qMax || rate <= 0)
                    Error(f, $"item {key}: metastable floor must lie in (0, qMax) and decay must be positive");

                item.Decay = new DecayRule(floor, floor, rate, Fixed.Ppm(Opt(meta, "shockLoss", 0)), crystal.TicksPerDay == 0 ? _tickRateHz * 86_400L : crystal.TicksPerDay);
            }
        }

        if (!_byKey.TryAdd(key, item))
        {
            Error(f, $"duplicate item id {key}");
            return null;
        }

        _items.Add(item);
        return item;
    }

    private CraftingData AssignItems(PurityRules purity, DecayRule crystal, (double AmPerEm, double Energy, double Production, double Core) am, PricingRules pricing)
    {
        _items.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

        if (_items.Count >= ushort.MaxValue)
            Error("items", "too many items");

        var byId = new ItemDef[_items.Count + 1];

        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            item.Id = new ItemId((ushort)(i + 1));
            byId[i + 1] = item;

            if (!_families.Contains(item.Family))
                _families.Add(item.Family);
        }

        _families.Sort(StringComparer.Ordinal);

        foreach (var item in _items)
            item.FamilyId = _families.IndexOf(item.Family);

        return new CraftingData
        {
            Purity = purity, Additives = _additives, Pricing = pricing, Crystal = crystal, TickRateHz = _tickRateHz,
            AmPerEm = am.AmPerEm, AmEnergyJPerKg = am.Energy, AmProductionJPerKg = am.Production, CoreEfficiency = am.Core,
            _items = byId, _itemList = _items.ToArray(), _itemsByKey = new Dictionary<string, ItemDef>(_byKey, StringComparer.Ordinal),
        };
    }

    private void ParseMaterial(string f, JsonElement e, List<MaterialDef> materials)
    {
        var key = Str(e, "id", f);
        var placeable = OptBool(e, "placeable");
        var yieldItem = ItemId.None;
        var purity = YieldPurity.None;
        int qMin = 0, qMax = 0;

        if (e.TryGetProperty("yields", out var y) && y.ValueKind == JsonValueKind.Object)
        {
            var item = Resolve(f, Str(y, "item", f));
            yieldItem = item?.Id ?? ItemId.None;
            purity = OptStr(y, "purityFrom") switch
            {
                "deposit" => YieldPurity.Deposit,
                "range" => YieldPurity.Range,
                _ => YieldPurity.None,
            };

            if (item?.Content == ContentKind.Exotic)
            {
                if (placeable)
                    Error(f, $"material {key}: a placeable material can never yield EM (refill-and-remine would print EM)");

                if (!y.TryGetProperty("purityRange", out var range) || range.ValueKind != JsonValueKind.Array || range.GetArrayLength() != 2)
                {
                    Error(f, $"material {key}: an EM yield needs purityRange [min, max]");
                }
                else
                {
                    qMin = Fixed.Ppm(range[0].GetDouble());
                    qMax = Fixed.Ppm(range[1].GetDouble());

                    if (qMin < 0 || qMin > qMax)
                        Error(f, $"material {key}: purityRange must ascend from 0 or more");

                    if (qMax > _qMax)
                        Error(f, $"material {key}: source purity {qMax} ppm above qMax");
                }
            }
        }

        var density = (int)Num(e, "densityKgM3", f);

        if (density <= 0)
            Error(f, $"material {key}: density must be positive");

        materials.Add(new MaterialDef
        {
            Key = key, Name = OptStr(e, "name") ?? key, DensityKgM3 = density, Hardness = Opt(e, "hardness", 0.5), Placeable = placeable,
            Yield = yieldItem, Purity = purity, PurityMinPpm = qMin, PurityMaxPpm = qMax,
        });
    }

    private RecipeDef? ParseRecipe(PendingRecipe p)
    {
        var kind = p.OutputItem is not null ? "Fixed" : OptStr(p.Json, "kind");

        return kind switch
        {
            "Enrich" => ParseEnrich(p),
            "Blend" => ParseBlend(p),
            "Fixed" => ParseFixed(p),
            "AmProduction" => ParseAmProduction(p),
            _ => BadRecipe(p, $"unknown kind {kind}"),
        };
    }

    private RecipeDef? ParseEnrich(PendingRecipe p)
    {
        var (f, e) = (p.File, p.Json);
        var input = Obj(e, "input", f);
        var family = FamilyOf(f, Str(input, "family", f));
        var r = new EnrichParams
        {
            FamilyId = family,
            AlphaPpm = (long)Math.Round(Num(e, "alpha", f) * Fixed.One),
            YieldPpm = Fixed.Ppm(Num(e, "yield", f)),
            LossPpm = Fixed.Ppm(Num(e, "loss", f)),
            MinInPpm = Fixed.Ppm(Opt(input, "minPurity", 0)),
            CapPpm = Fixed.Ppm(Num(e, "purityCap", f)),
            SpreadOutPpm = Fixed.Ppm(Opt(e, "spreadOut", 0)),
            ThroughputMgPerHour = Fixed.Mg(Num(e, "throughputKgPerHour", f)),
            AmNgPerKg = (long)Math.Round(Opt(e, "amGPerKg", 0) * 1e9),
        };

        if (e.TryGetProperty("consumes", out var consumes) && consumes.ValueKind == JsonValueKind.Array)
        {
            if (consumes.GetArrayLength() > 1)
                Error(f, $"recipe {p.Key}: at most one consumed solvent");

            foreach (var c in consumes.EnumerateArray())
            {
                r.Solvent = Resolve(f, Str(c, "item", f))?.Id ?? ItemId.None;
                r.SolventPpm = Fixed.Ppm(Num(c, "perKgInput", f));
            }
        }

        if (e.TryGetProperty("seed", out var seed) && seed.ValueKind == JsonValueKind.Object)
        {
            r.SeedMinPpm = Fixed.Ppm(Num(seed, "minPurity", f));
            r.SeedFracPpm = Fixed.Ppm(Num(seed, "minContentFraction", f));
            r.SeedUsePpm = Fixed.Ppm(Num(seed, "consumedFraction", f));

            if (r.SeedMinPpm > _qMax || r.SeedFracPpm <= 0 || r.SeedUsePpm <= 0 || r.SeedUsePpm > Fixed.One)
                Error(f, $"recipe {p.Key}: seed needs a purity at or below qMax and positive fractions");
        }

        var where = $"recipe {p.Key}";

        if (r.AlphaPpm <= Fixed.One)
            Error(f, $"{where}: alpha must exceed 1, or the recipe would not enrich");

        if (r.YieldPpm <= 0 || r.LossPpm <= 0 || r.YieldPpm + r.LossPpm > Fixed.One)
            Error(f, $"{where}: yield must be positive, loss positive (every loop lossy), and yield + loss at most 1");

        if (r.CapPpm > _qMax)
            Error(f, $"{where}: purityCap {r.CapPpm} ppm is above qMax {_qMax} ppm");

        if (r.MinInPpm < 0 || r.MinInPpm >= r.CapPpm)
            Error(f, $"{where}: minPurity must lie below purityCap");

        if (r.ThroughputMgPerHour <= 0)
            Error(f, $"{where}: throughput must be positive");

        if (r.SpreadOutPpm < 0 || r.SpreadOutPpm > Fixed.One / 2)
            Error(f, $"{where}: spreadOut must lie in [0, 0.5]");

        if (family >= 0 && !FamilyHolds(family, ContentKind.Exotic))
            Error(f, $"{where}: input family holds no exotic content");

        return new RecipeDef { Key = p.Key, Kind = RecipeKind.Enrich, Station = Str(e, "station", f), Crude = r.SpreadOutPpm > 0, PowerW = Opt(e, "powerW", 0), Enrich = r };
    }

    private RecipeDef? ParseBlend(PendingRecipe p)
    {
        var (f, e) = (p.File, p.Json);
        var b = new BlendParams { FamilyId = -1, MinPpm = 0, MaxPpm = NoMax };

        if (e.TryGetProperty("inputs", out var inputs) && inputs.ValueKind == JsonValueKind.Array)
        {
            foreach (var input in inputs.EnumerateArray())
            {
                if (input.TryGetProperty("family", out var fam))
                {
                    b.FamilyId = FamilyOf(f, fam.GetString() ?? "");
                    b.MinPpm = Fixed.Ppm(Opt(input, "minPurity", 0));
                    b.MaxPpm = input.TryGetProperty("maxPurity", out _) ? Fixed.Ppm(Num(input, "maxPurity", f)) : NoMax;
                }
                else
                {
                    b.Agent = Resolve(f, Str(input, "item", f))?.Id ?? ItemId.None;
                }
            }
        }

        b.AgentMinFractionPpm = Fixed.Ppm(Opt(e, "agentMinFraction", 0));
        b.TargetPpm = Fixed.Ppm(Num(e, "targetPurity", f));
        var output = Resolve(f, Str(e, "output", f));
        b.Output = output?.Id ?? ItemId.None;
        b.ThroughputMgPerHour = Fixed.Mg(Num(e, "throughputKgPerHour", f));
        b.FeePerKg = Opt(e, "feePerKg", 0);
        var where = $"recipe {p.Key}";

        if (b.FamilyId < 0 || b.Agent.IsNone)
            Error(f, $"{where}: a blend needs one family input and one agent item");

        if (b.TargetPpm <= 0 || b.TargetPpm > _qMax)
            Error(f, $"{where}: targetPurity must lie in (0, qMax]");

        if (b.MinPpm > b.MaxPpm || b.MinPpm > _qMax)
            Error(f, $"{where}: input purity range is empty or above qMax");

        if (b.AgentMinFractionPpm < 0 || b.AgentMinFractionPpm >= Fixed.One)
            Error(f, $"{where}: agentMinFraction must lie in [0, 1)");

        if (output is not null && output.Content != ContentKind.Exotic)
            Error(f, $"{where}: blend output must hold exotic content");

        if (b.ThroughputMgPerHour <= 0)
            Error(f, $"{where}: throughput must be positive");

        return new RecipeDef { Key = p.Key, Kind = RecipeKind.Blend, Station = Str(e, "station", f), PowerW = Opt(e, "powerW", 0), Blend = b };
    }

    private RecipeDef? ParseAmProduction(PendingRecipe p)
    {
        var (f, e) = (p.File, p.Json);
        var feed = Obj(e, "feed", f);
        var a = new AmProductionParams
        {
            JPerNg = (long)Math.Round(_amProductionJPerKg / 1e12),
            Feed = Resolve(f, Str(feed, "item", f))?.Id ?? ItemId.None,
            FeedPpm = Fixed.Ppm(Num(feed, "kgPerKg", f)),
        };

        if (a.JPerNg <= 0)
            Error(f, $"recipe {p.Key}: amProductionJPerKg must give at least 1 J per ng");

        if (a.FeedPpm < 0)
            Error(f, $"recipe {p.Key}: feed must not be negative");

        return new RecipeDef { Key = p.Key, Kind = RecipeKind.AmProduction, Station = OptStr(e, "station") ?? "fc.am_factory", Am = a };
    }

    private RecipeDef? ParseFixed(PendingRecipe p)
    {
        var (f, e) = (p.File, p.Json);
        var where = $"recipe {p.Key}";
        var inputs = new List<RecipeInput>();
        var outputs = new List<RecipeOutput>();

        if (e.TryGetProperty("inputs", out var ins) && ins.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in ins.EnumerateArray())
            {
                var input = ParseInput(f, where, i);

                if (input is not null)
                    inputs.Add(input.Value);
            }
        }

        var potency = p.Additive ? (p.Crude ? _additives.CrudeLabPotencyPpm : _additives.LabPotencyPpm) : 0;
        var outputKg = 0.0;

        if (e.TryGetProperty("outputs", out var outs) && outs.ValueKind == JsonValueKind.Array)
        {
            foreach (var o in outs.EnumerateArray())
            {
                var output = ParseOutput(f, where, o, potency);

                if (output is not null)
                {
                    outputs.Add(output.Value);
                    outputKg += output.Value.MassMg / (double)Fixed.MgPerKg;
                }
            }
        }
        else if (p.OutputItem is not null && Resolve(f, p.OutputItem) is { } item)
        {
            var mass = item.IsDiscrete ? item.UnitMassMg : Fixed.MgPerKg;
            outputs.Add(new RecipeOutput(item.Id, mass, item.Content == ContentKind.Active ? potency : 0));
            outputKg = mass / (double)Fixed.MgPerKg;
        }

        var seconds = e.TryGetProperty("timeSPerKg", out _) ? Num(e, "timeSPerKg", f) * outputKg : Num(e, "timeS", f);

        if (p.Crude)
            seconds /= _additives.CrudeRateFactor;

        if (outputs.Count == 0 || outputs.Count > ProductionQueue.MaxOutputs)
            Error(f, $"{where}: needs 1 to {ProductionQueue.MaxOutputs} outputs");

        if (!(seconds > 0))
            Error(f, $"{where}: time must be positive");

        foreach (var o in outputs)
        {
            var def = Item(o.Item);

            if (def.Content == ContentKind.Active && (o.ContentPpm <= 0 || o.ContentPpm > _qMax))
                Error(f, $"{where}: output potency {o.ContentPpm} ppm must lie in (0, qMax]");

            if (def.Content is ContentKind.Exotic or ContentKind.Antimatter)
                Error(f, $"{where}: a fixed recipe cannot output EM or AM content (it would bypass refining)");
        }

        return new RecipeDef
        {
            Key = p.Key, Kind = RecipeKind.Fixed, Station = p.Crude ? Str(e, "station", f) + ".crude" : Str(e, "station", f),
            Crude = p.Crude, PowerW = Opt(e, "powerW", 0), Inputs = inputs.ToArray(), Outputs = outputs.ToArray(),
            TicksPerUnit = (long)Math.Ceiling(seconds * _tickRateHz),
        };
    }

    private RecipeInput? ParseInput(string f, string where, JsonElement i)
    {
        if (i.TryGetProperty("family", out var fam))
        {
            var family = FamilyOf(f, fam.GetString() ?? "");
            var min = Fixed.Ppm(Opt(i, "minPurity", 0));
            var max = i.TryGetProperty("maxPurity", out _) ? Fixed.Ppm(Num(i, "maxPurity", f)) : NoMax;
            var content = Fixed.Mg(Num(i, "contentKg", f));

            if (min > _qMax || min > max)
                Error(f, $"{where}: no stock can meet input purity {min} ppm (above qMax or above its maximum)");

            if (content <= 0)
                Error(f, $"{where}: input content must be positive");

            if (family >= 0 && !FamilyHolds(family, ContentKind.Exotic))
                Error(f, $"{where}: input family {fam.GetString()} holds no exotic content");

            return new RecipeInput(InputKind.FamilyContent, ItemId.None, family, min, max, content);
        }

        var item = Resolve(f, Str(i, "item", f));

        if (item is null)
            return null;

        if (i.TryGetProperty("amG", out _))
        {
            if (item.Content != ContentKind.Antimatter)
                Error(f, $"{where}: amG drawn from {item.Key}, which holds no antimatter");

            return new RecipeInput(InputKind.Antimatter, item.Id, -1, 0, NoMax, Fixed.Mg(Num(i, "amG", f) / 1000));
        }

        if (i.TryGetProperty("count", out _))
        {
            var count = (long)Num(i, "count", f);

            if (!item.IsDiscrete || count <= 0)
                Error(f, $"{where}: count needs a discrete item and a positive count ({item.Key})");

            return new RecipeInput(InputKind.ItemCount, item.Id, -1, 0, NoMax, count);
        }

        var mass = Fixed.Mg(Num(i, "kg", f));

        if (item.IsDiscrete || mass <= 0)
            Error(f, $"{where}: kg needs a bulk item and a positive mass ({item.Key})");

        return new RecipeInput(InputKind.ItemMass, item.Id, -1, 0, NoMax, mass);
    }

    private RecipeOutput? ParseOutput(string f, string where, JsonElement o, int defaultPotency)
    {
        var item = Resolve(f, Str(o, "item", f));

        if (item is null)
            return null;

        var potency = o.TryGetProperty("potency", out _) ? Fixed.Ppm(Num(o, "potency", f)) : item.Content == ContentKind.Active ? defaultPotency : 0;

        if (o.TryGetProperty("count", out _))
        {
            var count = (long)Num(o, "count", f);

            if (!item.IsDiscrete || count <= 0)
                Error(f, $"{where}: output count needs a discrete item ({item.Key})");

            return new RecipeOutput(item.Id, count * item.UnitMassMg, potency);
        }

        var mass = Fixed.Mg(Num(o, "kg", f));

        if (item.IsDiscrete || mass <= 0)
            Error(f, $"{where}: output kg needs a bulk item and a positive mass ({item.Key})");

        return new RecipeOutput(item.Id, mass, potency);
    }

    private int[] DeriveGates(PurityRules purity, List<RecipeDef> recipes)
    {
        var gates = new SortedSet<int>();

        for (var i = 1; i < purity.BandEdgesPpm.Count; i++)
            gates.Add(purity.BandEdgesPpm[i]);

        foreach (var g in purity.GatesPpm)
            gates.Add(g);

        foreach (var r in recipes)
        {
            switch (r.Kind)
            {
                case RecipeKind.Enrich:
                    gates.Add(r.Enrich.MinInPpm);

                    if (r.Enrich.NeedsSeed)
                        gates.Add(r.Enrich.SeedMinPpm);

                    break;
                case RecipeKind.Blend:
                    gates.Add(r.Blend.MinPpm);
                    gates.Add(r.Blend.MaxPpm);
                    break;
                case RecipeKind.Fixed:
                    foreach (var input in r.Inputs)
                    {
                        if (input.Kind != InputKind.FamilyContent)
                            continue;

                        gates.Add(input.MinPpm);
                        gates.Add(input.MaxPpm);
                    }

                    break;
            }
        }

        gates.RemoveWhere(g => g <= 0 || g == NoMax);

        foreach (var g in gates)
        {
            if (g > _qMax)
                Error(CraftingData.ExoticPath, $"gate {g} ppm is above qMax");
        }

        return gates.ToArray();
    }

    private CraftingData Finish(CraftingData data, PurityRules purityBase, int[] gates, List<MaterialDef> materials, List<RecipeDef> recipes)
    {
        var purity = new PurityRules(purityBase.QMaxPpm, purityBase.BucketPpm, purityBase.PotencyBucketPpm,
            purityBase.BandNames.ToArray(), purityBase.BandEdgesPpm.ToArray(), gates);
        var additives = new List<AdditiveDef>();
        var byItem = new AdditiveDef?[data._items.Length];

        foreach (var (item, effect, tradeOff) in _additiveJson)
        {
            var def = new AdditiveDef { Key = item.Key, Item = item.Id, Effects = Factors(effect), TradeOffs = Factors(tradeOff) };
            additives.Add(def);
            byItem[item.Id.Value] = def;
        }

        additives.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        var result = new CraftingData
        {
            Purity = purity, Additives = data.Additives, Pricing = data.Pricing, Crystal = data.Crystal, TickRateHz = data.TickRateHz,
            AmPerEm = data.AmPerEm, AmEnergyJPerKg = data.AmEnergyJPerKg, AmProductionJPerKg = data.AmProductionJPerKg,
            CoreEfficiency = data.CoreEfficiency,
            _items = data._items, _itemList = data._itemList, _itemsByKey = data._itemsByKey,
            _materials = materials.ToArray(), _recipes = recipes.ToArray(), _additives = additives.ToArray(), _additiveByItem = byItem,
            _families = _families.ToArray(), _factorKeys = _factorKeys.ToArray(),
            _factorAdditive = _factorKeys.Select(k => k.EndsWith("Add", StringComparison.Ordinal)).ToArray(),
        };

        foreach (var r in recipes)
            result._recipesByKey[r.Key] = r;

        return result;
    }

    private FactorEntry[] Factors(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
            return [];

        var entries = new List<FactorEntry>();

        foreach (var p in e.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Number)
                continue;

            var key = _factorKeys.IndexOf(p.Name);

            if (key < 0)
            {
                key = _factorKeys.Count;
                _factorKeys.Add(p.Name);
            }

            entries.Add(new FactorEntry(key, p.Value.GetDouble(), p.Name.EndsWith("Add", StringComparison.Ordinal)));
        }

        return entries.ToArray();
    }

    private bool FamilyHolds(int family, ContentKind content) =>
        _items.Exists(i => i.FamilyId == family && i.Content == content);

    private int FamilyOf(string f, string family)
    {
        var id = _families.IndexOf(family);

        if (id < 0)
            Error(f, $"unknown item family {family}");

        return id;
    }

    private ItemDef? Resolve(string f, string key)
    {
        if (_byKey.TryGetValue(key, out var item))
            return item;

        Error(f, $"unknown item {key}");
        return null;
    }

    private ItemDef Item(ItemId id) => _items.Find(i => i.Id == id)!;

    private void CheckDuplicates(IEnumerable<string> keys, string what)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var key in keys)
        {
            if (!seen.Add(key))
                Error(what + "s", $"duplicate {what} id {key}");
        }
    }

    private RecipeDef? BadRecipe(PendingRecipe p, string message)
    {
        Error(p.File, $"recipe {p.Key}: {message}");
        return null;
    }

    private ContentKind Bad(string f, string message)
    {
        Error(f, message);
        return ContentKind.None;
    }

    private void Error(string file, string message) => _errors.Add($"{file}: {message}");

    private double Num(JsonElement o, string name, string file)
    {
        if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number)
            return v.GetDouble();

        Error(file, $"missing number '{name}'");
        return double.NaN;
    }

    private static double Opt(JsonElement o, string name, double fallback) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;

    private string Str(JsonElement o, string name, string file)
    {
        if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString()!;

        Error(file, $"missing string '{name}'");
        return "";
    }

    private static string? OptStr(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool OptBool(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private JsonElement Obj(JsonElement o, string name, string file)
    {
        if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object)
            return v;

        Error(file, $"missing object '{name}'");
        return default;
    }
}
