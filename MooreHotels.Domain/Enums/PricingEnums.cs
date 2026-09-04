namespace MooreHotels.Domain.Enums;

public enum RateAdjustmentType
{
    None,
    Percentage,
    FixedAmount
}

public enum DiscountType
{
    Percentage,
    FixedAmount
}

public enum PricingRuleKind
{
    Tax,
    Fee
}

public enum PricingRuleCalculation
{
    Percentage,
    FixedPerStay,
    FixedPerNight
}

public enum PricingLineType
{
    RoomNight,
    Discount,
    Tax,
    Fee
}
