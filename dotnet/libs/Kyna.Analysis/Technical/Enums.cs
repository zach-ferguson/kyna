﻿using System.ComponentModel;

namespace Kyna.Analysis.Technical;

public enum ChartInterval
{
    Daily = 0,
    Weekly,
    Monthly,
    Quarterly,
    Annually
}

public enum CandlestickColor
{
    None = 0,
    Light,
    Dark
}

public enum PricePoint
{
    MidPoint = 0,
    Open,
    High,
    Low,
    Close
}

[Flags]
public enum TrendSentiment
{
    None = 0,
    Neutral = 1 << 0,
    FullBear = 1 << 1,
    Bearish = 1 << 2,
    MildBear = 1 << 3,
    MildBull = 1 << 4,
    Bullish = 1 << 5,
    FullBull = 1 << 6
}

public enum ExtremeType
{
    High = 0,
    Low = 1
}

public enum SelectPatternName
{
    TallWhiteCandle = PatternName.TallWhiteCandle
}

public enum PatternName
{
    None = 0,
    [Description("Tall White Candle")]
    TallWhiteCandle,
    [Description("Bullish Engulfing")]
    BullishEngulfing,
    [Description("Bullish Engulfing With FollowThru")]
    BullishEngulfingWithFollowThru,
    [Description("Bullish Engulfing With Four Black Predecessors")]
    BullishEngulfingWithFourBlackPredecessors,
    [Description("Bullish Engulfing With Tall Candles")]
    BullishEngulfingWithTallCandles,
    [Description("Bearish Engulfing")]
    BearishEngulfing,
    [Description("Bearish Engulfing With FollowThru")]
    BearishEngulfingWithFollowThru,
    [Description("Bearish Engulfing With Tall Candles")]
    BearishEngulfingWithTallCandles,
    [Description("Bearish Engulfing With Four White Predecessors")]
    BearishEngulfingWithFourWhitePredecessors,
    [Description("Bullish Hammer")]
    BullishHammer,
    [Description("Bullish Hammer With FollowThru")]
    BullishHammerWithFollowThru,
    [Description("Bearish Hammer")]
    BearishHammer,
    [Description("Bearish Hammer With FollowThru")]
    BearishHammerWithFollowThru,
    [Description("Dark Cloud Cover")]
    DarkCloudCover,
    [Description("Dark Cloud Cover With FollowThru")]
    DarkCloudCoverWithFollowThru,
    [Description("Piercing Pattern")]
    PiercingPattern,
    [Description("Piercing Pattern With FollowThru")]
    PiercingPatternWithFollowThru,
    [Description("Morning Star")]
    MorningStar,
    [Description("Evening Star")]
    EveningStar,
    [Description("Morning Doji Star")]
    MorningDojiStar,
    [Description("Evening Doji Star")]
    EveningDojiStar,
    [Description("Shooting Star")]
    ShootingStar,
    [Description("Inverted Hammer")]
    InvertedHammer,
    [Description("Bullish Harami")]
    BullishHarami,
    [Description("Bearish Harami")]
    BearishHarami,
    [Description("Bullish Harami Cross")]
    BullishHaramiCross,
    [Description("Bearish Harami Cross")]
    BearishHaramiCross,
    [Description("Tweezer Top")]
    TweezerTop,
    [Description("Tweezer Bottom")]
    TweezerBottom,
    [Description("Bullish Belthold")]
    BullishBelthold,
    [Description("Bearish Belthold")]
    BearishBelthold,
    [Description("Upside Gap Two Crows")]
    UpsideGapTwoCrows,
    [Description("Three Black Crows")]
    ThreeBlackCrows,
    [Description("Three White Soliders")]
    ThreeWhiteSoliders,
    [Description("Bullish Counterattack")]
    BullishCounterattack,
    [Description("Bearish Counterattack")]
    BearishCounterattack
}