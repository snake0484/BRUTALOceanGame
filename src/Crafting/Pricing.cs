namespace BrutalGame.Crafting;

public enum DesignLevel : byte
{
    Stock,
    Part,
    Component,
    Ship,
}

/// <summary>Pricing knobs from <c>data/economy/pricing.json</c> (ship-design-and-construction.md, section 3).</summary>
public sealed class PricingRules
{
    public double Wage { get; init; }
    public double CatalogScale { get; init; }
    public double VendorMargin { get; init; }
    public double RetailStock { get; init; }
    public double RetailPart { get; init; }
    public double RetailComponent { get; init; }
    public double YardMargin { get; init; }
    public double JobLabour { get; init; }
    public double JobMargin { get; init; }
    public double RawMarkup { get; init; }
    public double MachineRate { get; init; }
    public double DesignStamp { get; init; }
    public double NrePart { get; init; }
    public double NreComponent { get; init; }
    public double NreShip { get; init; }
    public double NrePerDistinct { get; init; }
    public double NrePerJoint { get; init; }
    public double ToolingPerMaterial { get; init; }
    public double PartLicence { get; init; }
    public double ComponentLicence { get; init; }
    public double PlanLicencePerBuild { get; init; }
    public double BuybackItemOfWholesale { get; init; }
    public double BuybackShipOfPremade { get; init; }
    public double BuybackShipCertified { get; init; }
}

/// <summary>
/// The four sourcing modes as pure functions, in credits (ship-design-and-construction.md, Sourcing modes and the rule).
/// <c>in</c> is the children's cost, <c>m</c> material cost, <c>t</c> labour or machine minutes. Every build of a redefined
/// design is multiplied by <c>1 + designStamp</c> at that level (<see cref="Stamped"/>), compounding per redefined level.
/// </summary>
public static class Pricing
{
    /// <summary>Catalog: <c>p_wh = (in + w·t) · S · (1 + μ_v)</c>.</summary>
    public static double Wholesale(PricingRules p, double inCost, double minutes) =>
        (inCost + p.Wage * minutes) * p.CatalogScale * (1 + p.VendorMargin);

    /// <summary>Retail: <c>p_wh · r_L</c>, the markup by level (ships sell at <see cref="Premade"/>).</summary>
    public static double Retail(PricingRules p, double wholesale, DesignLevel level) => wholesale * level switch
    {
        DesignLevel.Stock => p.RetailStock,
        DesignLevel.Part => p.RetailPart,
        DesignLevel.Component => p.RetailComponent,
        _ => 1 + p.YardMargin,
    };

    /// <summary>A premade ship: the yard buys inputs at wholesale and sells at a thin margin.</summary>
    public static double Premade(PricingRules p, double wholesaleInputs) => wholesaleInputs * (1 + p.YardMargin);

    /// <summary>Job shop: <c>(in_retail + J·w·t) · (1 + μ_j)</c>.</summary>
    public static double JobShop(PricingRules p, double inRetail, double minutes) =>
        (inRetail + p.JobLabour * p.Wage * minutes) * (1 + p.JobMargin);

    /// <summary>Self-assemble: <c>in_retail + consumables</c> (plus your time and NRE).</summary>
    public static double SelfAssemble(double inRetail, double consumables) => inRetail + consumables;

    /// <summary>Self-produce: <c>r_raw · m + M_rate · t</c> (plus your time and NRE).</summary>
    public static double SelfProduce(PricingRules p, double materialCost, double machineMinutes) =>
        p.RawMarkup * materialCost + p.MachineRate * machineMinutes;

    /// <summary>Raw materials bought on the market: <c>r_raw · m</c>.</summary>
    public static double RawMaterials(PricingRules p, double materialCost) => p.RawMarkup * materialCost;

    /// <summary>One build of a redefined design: <c>unit · (1 + designStamp)</c>.</summary>
    public static double Stamped(PricingRules p, double unit) => unit * (1 + p.DesignStamp);

    /// <summary>NRE, once per (owner, plan): <c>E_L · (1 + 0.25·u + 0.01·j)</c>; a revision counts only the diff.</summary>
    public static double Nre(PricingRules p, DesignLevel level, int distinctChildren, int joints) => level switch
    {
        DesignLevel.Part => p.NrePart,
        DesignLevel.Component => p.NreComponent,
        DesignLevel.Ship => p.NreShip,
        _ => 0,
    } * (1 + p.NrePerDistinct * distinctChildren + p.NrePerJoint * joints);

    /// <summary>Dies and fixtures per custom part design: <c>0.5 · m</c>.</summary>
    public static double Tooling(PricingRules p, double materialCost) => p.ToolingPerMaterial * materialCost;

    /// <summary>Right to self-produce a catalog part, once per part design.</summary>
    public static double PartLicence(PricingRules p, double wholesale) => p.PartLicence * wholesale;

    /// <summary>Right to self-assemble or self-produce a catalog component, per unit.</summary>
    public static double ComponentLicence(PricingRules p, double wholesale) => p.ComponentLicence * wholesale;

    /// <summary>Catalog ship plans, per build.</summary>
    public static double PlanLicence(PricingRules p, double premade) => p.PlanLicencePerBuild * premade;

    /// <summary>Average unit over <paramref name="n"/> builds: the stamp never amortises, the once-costs do.</summary>
    public static double Average(double unit, double once, int n) => unit + once / n;

    /// <summary>The most an NPC market pays.</summary>
    public static double Buyback(PricingRules p, double wholesale) => p.BuybackItemOfWholesale * wholesale;

    public static double ShipBuyback(PricingRules p, double premade, bool certified) =>
        premade * (certified ? p.BuybackShipCertified : p.BuybackShipOfPremade);

    /// <summary>The stamp must exceed <c>planLicence · A / X</c> so a like-for-like custom ship never undercuts the catalog plans.</summary>
    public static bool StampBeatsPlanLicence(PricingRules p, double premade, double likeForLikeUnit) =>
        p.DesignStamp > p.PlanLicencePerBuild * premade / likeForLikeUnit;

    /// <summary>Whole credits, half away from zero, as the docs' tables round.</summary>
    public static long Round(double credits) => (long)Math.Round(credits, MidpointRounding.AwayFromZero);
}
