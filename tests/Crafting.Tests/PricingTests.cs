namespace BrutalGame.Crafting.Tests;

/// <summary>
/// ship-design-and-construction.md section 3 worked numbers: the Wren and a custom C-class recirculating drive.
/// Part rows match exactly. Route rows compose the primitives; the doc rounds intermediate values, so they are checked to
/// ±2 credits (the self-produced drive row is internally about 1.5 cr off in the doc; see docs/crafting.md).
/// </summary>
public class PricingTests
{
    private static readonly PricingRules P = TestData.Data.Pricing;

    private static readonly (string Name, int Qty, double M, double T, long Wholesale, long Retail, long Self)[] Parts =
    [
        ("chamber", 1, 300, 40, 469, 657, 545),
        ("emitter", 8, 34, 7, 63, 89, 74),
        ("diluter", 1, 70, 15, 133, 186, 156),
        ("separator", 1, 140, 25, 245, 343, 286),
        ("trap", 1, 60, 15, 122, 171, 144),
        ("reservoir", 1, 20, 4, 37, 51, 43),
        ("pump", 2, 22, 6, 47, 66, 55),
        ("valve", 5, 8, 2, 16, 23, 19),
        ("interlock", 1, 30, 10, 71, 100, 85),
    ];

    private const double StockWholesale = 152;
    private const double DriveMinutes = 75;
    private const double Consumables = 14;
    private const int Distinct = 13, Joints = 60;

    private static double Sum(Func<(string Name, int Qty, double M, double T, long W, long R, long S), double> f) => Parts.Sum(p => f(p));

    private static double DriveWholesaleParts => Sum(p => p.Qty * Pricing.Wholesale(P, p.M, p.T)) + StockWholesale;

    private static double DriveRetailParts => Sum(p => p.Qty * Pricing.Retail(P, Pricing.Wholesale(P, p.M, p.T), DesignLevel.Part))
        + Pricing.Retail(P, StockWholesale, DesignLevel.Stock);

    private static double CatalogDrive => Pricing.Wholesale(P, DriveWholesaleParts, DriveMinutes);

    private static double ComponentNre => Pricing.Nre(P, DesignLevel.Component, Distinct, Joints);

    [Fact]
    public void PartsTable_MatchesTheDoc()
    {
        foreach (var p in Parts)
        {
            var wholesale = Pricing.Wholesale(P, p.M, p.T);
            Assert.Equal(p.Wholesale, Pricing.Round(wholesale));
            Assert.Equal(p.Retail, Pricing.Round(Pricing.Retail(P, wholesale, DesignLevel.Part)));
            Assert.Equal(p.Self, Pricing.Round(Pricing.SelfProduce(P, p.M, p.T)));
        }

        Assert.Equal(1910, Pricing.Round(DriveWholesaleParts));
        Assert.Equal(2629, Pricing.Round(DriveRetailParts));
        Assert.Equal(2225, Pricing.Round(Sum(p => p.Qty * Pricing.SelfProduce(P, p.M, p.T)) + Pricing.Retail(P, StockWholesale, DesignLevel.Stock)));
        Assert.Equal(2255, Pricing.Round(CatalogDrive));
        Assert.Equal(2435, Pricing.Round(Pricing.Retail(P, CatalogDrive, DesignLevel.Component)));
        Assert.Equal(1212.5, ComponentNre, 6);
    }

    [Fact]
    public void DriveRoutes_MatchTheDoc()
    {
        var jobShop = Pricing.Stamped(P, Pricing.JobShop(P, DriveRetailParts, DriveMinutes));
        AssertRoute(jobShop, ComponentNre, 4449, 5662, 4692, 4510);

        var selfAssembled = Pricing.Stamped(P, Pricing.SelfAssemble(DriveRetailParts, Consumables));
        AssertRoute(selfAssembled, ComponentNre, 2802, 4015, 3045, 2863);

        var customParts = Sum(p => p.Qty * Pricing.Stamped(P, Pricing.JobShop(P, Pricing.RawMaterials(P, p.M), p.T)));
        var allCustom = Pricing.Stamped(P, Pricing.JobShop(P, customParts + Pricing.Retail(P, StockWholesale, DesignLevel.Stock), DriveMinutes));
        var allCustomOnce = ComponentNre + Sum(p => Pricing.Nre(P, DesignLevel.Part, 0, 4) + Pricing.Tooling(P, p.M));
        AssertRoute(allCustom, allCustomOnce, 5675, 7604, 6061, 5772);
        Assert.Equal(1929, Pricing.Round(allCustomOnce));

        var selfProduced = Pricing.Stamped(P, Sum(p => p.Qty * Pricing.SelfProduce(P, p.M, p.T))
            + Pricing.Retail(P, StockWholesale, DesignLevel.Stock) + Consumables);
        var selfOnce = ComponentNre + Sum(p => Pricing.PartLicence(P, Pricing.Wholesale(P, p.M, p.T)));
        AssertRoute(selfProduced, selfOnce, 2373, 3647, 2629, 2438);
        Assert.InRange(selfProduced / Pricing.Retail(P, CatalogDrive, DesignLevel.Component), 0.97, 0.98);
    }

    [Fact]
    public void ShipRoutes_KeepTheDocOrder_AtEveryFleetSize()
    {
        var catalogDriveRetail = Pricing.Retail(P, CatalogDrive, DesignLevel.Component);
        var hullStock = (9000 * 0.03 + 1500 * 0.18) * 1.08;
        var premade = Pricing.Premade(P, 1650 + CatalogDrive + hullStock + 280);
        var components = Pricing.Retail(P, 1650 + CatalogDrive, DesignLevel.Component);
        var stock = Pricing.Retail(P, hullStock, DesignLevel.Stock);
        const double shipConsumables = 40, shipNre = 975;

        var b = Pricing.PlanLicence(P, premade) + components + stock + shipConsumables;
        double CustomHull(double drive) => Pricing.Stamped(P, components - catalogDriveRetail + drive + stock + shipConsumables);
        var c = CustomHull(catalogDriveRetail);
        var jobDrive = Pricing.Stamped(P, Pricing.JobShop(P, DriveRetailParts, DriveMinutes));
        var selfDrive = Pricing.Stamped(P, DriveRetailParts + Consumables);
        var customParts = Parts.Sum(p => p.Qty * Pricing.Stamped(P, Pricing.JobShop(P, Pricing.RawMaterials(P, p.M), p.T)));
        var allCustomDrive = Pricing.Stamped(P, Pricing.JobShop(P, customParts + Pricing.Retail(P, StockWholesale, DesignLevel.Stock), DriveMinutes));
        var allCustomOnce = ComponentNre + Parts.Sum(p => Pricing.Nre(P, DesignLevel.Part, 0, 4) + Pricing.Tooling(P, p.M));

        Assert.Equal(5006, Pricing.Round(premade));
        Assert.InRange(Pricing.Round(b), 5147, 5151);
        AssertRoute(c, shipNre, null, 6167, 5387, 5241);
        AssertRoute(CustomHull(jobDrive), shipNre + ComponentNre, null, 9515, 7765, 7437);
        AssertRoute(CustomHull(selfDrive), shipNre + ComponentNre, null, 7770, 6020, 5692);
        AssertRoute(CustomHull(allCustomDrive), shipNre + allCustomOnce, null, 11531, 9208, 8772);

        foreach (var n in new[] { 1, 5, 20 })
        {
            var order = new[]
            {
                premade, b, Pricing.Average(c, shipNre, n), Pricing.Average(CustomHull(selfDrive), shipNre + ComponentNre, n),
                Pricing.Average(CustomHull(jobDrive), shipNre + ComponentNre, n), Pricing.Average(CustomHull(allCustomDrive), shipNre + allCustomOnce, n),
            };

            for (var i = 1; i < order.Length; i++)
                Assert.True(order[i] > order[i - 1], $"route {i} not dearer than route {i - 1} at N={n}");
        }

        Assert.True(Pricing.StampBeatsPlanLicence(P, premade, components + stock + shipConsumables));
        Assert.Equal(0.051, P.PlanLicencePerBuild * premade / (components + stock + shipConsumables), 3);
    }

    [Fact]
    public void Buyback_StaysBelowEveryProductionRoute()
    {
        foreach (var p in Parts)
        {
            var wholesale = Pricing.Wholesale(P, p.M, p.T);
            Assert.True(Pricing.SelfProduce(P, p.M, p.T) > Pricing.Buyback(P, wholesale), p.Name);
        }
    }

    private static void AssertRoute(double unit, double once, long? docUnit, long n1, long n5, long n20)
    {
        if (docUnit is { } u)
            Assert.InRange(Pricing.Round(unit), u - 2, u + 2);

        Assert.InRange(Pricing.Round(Pricing.Average(unit, once, 1)), n1 - 2, n1 + 2);
        Assert.InRange(Pricing.Round(Pricing.Average(unit, once, 5)), n5 - 2, n5 + 2);
        Assert.InRange(Pricing.Round(Pricing.Average(unit, once, 20)), n20 - 2, n20 + 2);
    }
}
