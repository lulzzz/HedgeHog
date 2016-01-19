﻿using HedgeHog.Bars;
using HedgeHog.Shared;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Dynamic;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace HedgeHog.Alice.Store {
  partial class TradingMacro {

    #region TradeConditions
    public delegate TradeDirections TradeConditionDelegate();
    public delegate TradeDirections TradeConditionDelegateHide();
    #region Trade Condition Helers
    bool IsCurrentPriceInside(params double[] levels) {
      return new[] { CurrentEnterPrice(true), CurrentEnterPrice(false) }.All(cp => cp.Between(levels[0], levels[1]));
    }
    private bool IsCurrentPriceInsideTradeLevels {
      get {
        return new[] { CurrentEnterPrice(true), CurrentEnterPrice(false) }.All(cp => cp.Between(SellLevel.Rate, BuyLevel.Rate));
      }
    }
    private bool IsCurrentPriceInsideTradeLevels2 {
      get {
        Func<double, double[], bool> isIn = (v, levels) => {
          var h = levels.Height() / 4;
          return v.Between(levels.Min() + h, levels.Max() - h);
        };
        return new[] { CurrentEnterPrice(true), CurrentEnterPrice(false) }.All(cp => isIn(cp, new[] { SellLevel.Rate, BuyLevel.Rate }));
      }
    }
    private bool IsCurrentPriceInsideBlueStrip { get { return IsCurrentPriceInside(CenterOfMassSell, CenterOfMassBuy); } }
    #endregion
    #region TradeDirection Helpers
    TradeDirections TradeDirectionBoth(bool ok) { return ok ? TradeDirections.Both : TradeDirections.None; }
    TradeConditionDelegate TradeDirectionEither(Func<bool> ok) { return () => ok() ? TradeDirections.Up : TradeDirections.Down; }
    TradeDirections IsTradeConditionOk(Func<TradingMacro, bool> tmPredicate, Func<TradingMacro, TradeDirections> condition) {
      return TradingMacroOther(tmPredicate).Take(1).Select(condition).DefaultIfEmpty(TradeDirections.Both).First();
    }
    #endregion

    double _tipRatioCurrent = double.NaN;

    #region Wave Conditions
    int _bigWaveIndex = 0;
    [Category(categoryTrading)]
    [WwwSetting]
    public int BigWaveIndex {
      get {
        return _bigWaveIndex;
      }

      set {
        if(value < 0)
          throw new Exception("BigWaveIndex must bre >= 0");
        _bigWaveIndex = value;
        OnPropertyChanged(() => BigWaveIndex);
      }
    }

    [TradeConditionTurnOff]
    public TradeConditionDelegateHide BigWaveOk {
      get {
        var ors = new Func<WaveRange, double>[] { wr => wr.DistanceCma };
        Func<WaveRange, TradingMacro, bool> predicate = (wr, tm) =>
          //wr.Angle.Abs().ToInt() >= tm.WaveRangeAvg.Angle.ToInt() && 
          ors.Any(or => or(wr) >= or(tm.WaveRangeAvg));
        return () => {
          var twoWavess = TradingMacroOther().SelectMany(tm => tm.WaveRanges)
            .Take(BigWaveIndex + 1)
            .Buffer(BigWaveIndex + 1)
            .Where(b => b.SkipLast(1).DefaultIfEmpty(b.Last()).Max(w => w.DistanceCma) <= b.Last().DistanceCma);
          var x = twoWavess.Select(_ => TradeDirections.Both).DefaultIfEmpty(TradeDirections.None).Single();
          return IsWaveOk(predicate, BigWaveIndex) & x;
        };
      }
    }

    [TradeConditionTurnOff]
    public TradeConditionDelegateHide TriplettOk {
      get {
        Func<WaveRange, TradingMacro, bool> isBig = (wr, tm) => wr.DistanceCma > tm.WaveRangeAvg.DistanceCma / 2;
        Func<WaveRange[], TradingMacro, bool> areBig = (wrs, tm) => wrs.Sum(wr => wr.DistanceCma) > tm.WaveRangeAvg.DistanceCma * 2;
        Func<WaveRange, TradingMacro, bool> isSmall = (wr, tm) => wr.DistanceCma < tm.WaveRangeAvg.DistanceCma / 3;
        Func<TradingMacro, IEnumerable<WaveRange>> bigUns = tm => tm.WaveRanges
          .Take(3)
          .Select((wr, i) => new { wr, i })
          .Where(x => x.i != 1)
          .Select(x => x.wr);
        Func<TradingMacro, WaveRange> smallOne = tm => tm.WaveRanges[1];

        return () =>
          TradingMacroOther()
          .Where(tm => tm.WaveRanges.Count > 2)
          .Select(tm =>
            areBig(bigUns(tm).ToArray(), tm) && bigUns(tm).All(wr => isBig(wr, tm)) && isSmall(smallOne(tm), tm)
            ? this.TradeDirectionByAngleSign(tm.WaveRanges[0].Angle)
            : TradeDirections.None
          )
          .DefaultIfEmpty(TradeDirections.None)
          .Single();
      }
    }

    private TradeDirections IsWaveOk(Func<WaveRange, TradingMacro, bool> predicate, int index) {
      return TradingMacroOther()
      .Take(1)
      .SelectMany(tm => tm.WaveRanges
      .SkipWhile(wr => wr.IsEmpty)
      .Skip(index)
      .Take(1)
      .Where(wr => predicate(wr, tm))
      .Select(wr => wr.Slope > 0 ? TradeDirections.Down : TradeDirections.Up)
      )
      .DefaultIfEmpty(TradeDirections.None)
      .Single();
    }
    private TradeDirections IsWaveOk2(Func<WaveRange, TradingMacro, bool> predicate, int index) {
      return TradingMacroOther()
      .Take(1)
      .SelectMany(tm => tm.WaveRanges
      .SkipWhile(wr => wr.IsEmpty)
      .Skip(index)
      .Take(1)
      .Where(wr => predicate(wr, tm))
      .Select(wr => TradeDirections.Both)
      )
      .DefaultIfEmpty(TradeDirections.None)
      .Single();
    }
    #endregion

    #region Trade Corridor and Directions conditions
    #region Tip COnditions

    [TradeConditionTurnOff]
    public TradeConditionDelegate TipOk {
      get {
        return () => TrendLines2Trends
          .YieldIf(p => !p.IsEmpty, p => p.Slope.SignUp())
          .Select(ss => {
            var tradeLevel = ss > 0 ? SellLevel.Rate : BuyLevel.Rate;
            var extream = ss > 0 ? _RatesMax : _RatesMin;
            var tip = (extream - tradeLevel).Abs();
            _tipRatioCurrent = RatesHeight / tip;
            return !IsTresholdAbsOk(_tipRatioCurrent, TipRatio)
              ? TradeDirections.None
              : ss > 0
              ? TradeDirections.Down
              : TradeDirections.Up;
          })
          .DefaultIfEmpty(TradeDirections.None)
          .Single();
      }
    }
    [TradeConditionTurnOff]
    public TradeConditionDelegate Tip2Ok {
      get {
        return () => {
          _tipRatioCurrent = _ratesHeightCma / BuyLevel.Rate.Abs(SellLevel.Rate);
          var td = IsTresholdAbsOk(_tipRatioCurrent, TipRatio)
            ? TradeDirections.Both
            : TradeDirections.None;
          var bsl = new[] { BuyLevel, SellLevel };
          if(/*bsl.Any(sr => sr.CanTrade) && */Trades.Length == 0)
            SetTradeCorridorToMinHeight();
          return td;
        };
      }
    }
    public void SetTradeCorridorToMinHeight() {
      if(CanTriggerTradeDirection()) {
        var bsl = new[] { BuyLevel, SellLevel };
        var buy = GetTradeLevel(true, double.NaN);
        var sell = GetTradeLevel(false, double.NaN);
        var zip = bsl.Zip(new[] { buy, sell }, (sr, bs) => new { sr, bs });
        zip.Where(x => !x.sr.InManual)
          .ForEach(x => {
            x.sr.InManual = true;
            x.sr.ResetPricePosition();
            x.sr.Rate = x.bs;
          });
        var bsHeight = BuyLevel.Rate.Abs(SellLevel.Rate);
        var tlHeight = buy.Abs(sell);
        var tlAvg = buy.Avg(sell);
        //var currentPrice = new[] { CurrentEnterPrice(true), CurrentEnterPrice(false) };
        var canSetLevel = (!tlAvg.Between(SellLevel.Rate, BuyLevel.Rate) || tlHeight < bsHeight);
        if(canSetLevel)
          lock (_rateLocker) {
            zip.ForEach(x => {
              var rate = x.bs;
              var rateJump = InPips(rate.Abs(x.sr.Rate));
              var reset = rateJump > 1;
              if(reset)
                x.sr.ResetPricePosition();
              x.sr.Rate = rate;
              if(reset)
                x.sr.ResetPricePosition();
            });
          }
      }
    }

    public TradeConditionDelegate Tip3Ok {
      get {
        return () => {
          var bsl = new[] { BuyLevel, SellLevel };
          if(!HaveTrades())
            SetTradeCorridorToMinHeight2();
          _tipRatioCurrent = _ratesHeightCma / BuyLevel.Rate.Abs(SellLevel.Rate);
          return TradeDirectionByBool(IsCurrentPriceInsideTradeLevels && IsTresholdAbsOk(_tipRatioCurrent, TipRatio));
        };
      }
    }

    #region Properties
    [Category(categoryCorridor)]
    [WwwSetting]
    public bool ResetTradeStrip {
      get { return false; }
      set {
        if(value)
          CenterOfMassSell = CenterOfMassBuy = CenterOfMassSell2 = CenterOfMassBuy2 = double.NaN;
      }
    }
    double _tradeStripJumpRatio = 1.333333;
    [Category(categoryActive)]
    [WwwSetting]
    public double TradeStripJumpRatio {
      get { return _tradeStripJumpRatio; }
      set {
        if(_tradeStripJumpRatio == value)
          return;
        _tradeStripJumpRatio = value;
        OnPropertyChanged(() => TradeStripJumpRatio);
      }
    }
    #endregion
    [TradeConditionSetCorridor]
    public TradeConditionDelegate TradeSlideOk {
      get {
        int sell = 1, buy = 0;
        Func<Rate.TrendLevels> baseTL = () => TrendLines2Trends;
        var getTradeLevels = MonoidsCore.ToFunc(() => {
          var h = GetTradeLevel(true, double.NaN).Abs(GetTradeLevel(false, double.NaN)) / 2;
          var mean = baseTL().PriceAvg1;
          return new[] { mean + h, mean - h };
        });
        var getTradeLevels2 = MonoidsCore.ToFunc((Rate.TrendLevels)null, tl => {
          var sign = tl.Slope;
          var baseLevel = tl.PriceAvg1;
          var buyLevel = sign > 0 ? GetTradeLevel(true, double.NaN) : baseLevel;
          var sellLevel = sign < 0 ? GetTradeLevel(false, double.NaN) : baseLevel;
          return new[] { buyLevel, sellLevel };
        });
        return () => {
          SetTradeStrip();
          var tradeLevels = getTradeLevels2(baseTL());
          var blueLevles = GetTradeLevelsToTradeStrip();
          _tipRatioCurrent = TradeLevelsEdgeRatio(tradeLevels).Min(_ratesHeightCma / tradeLevels.Height());
          var tipRatioOk = IsTresholdAbsOk(_tipRatioCurrent, TipRatio);
          if(!tipRatioOk)
            BuySellLevelsForEach(sr => sr.CanTradeEx = false);
          CanTrade_TurnOffByAngle(baseTL());
          var td = TradeDirectionByBool(tipRatioOk);
          if(td.HasAny()) {
            BuyLevel.RateEx = tradeLevels[buy];
            SellLevel.RateEx = tradeLevels[sell];
          }
          return td;
        };
      }
    }

    private void CanTrade_TurnOffByAngle(Rate.TrendLevels tl) {
      if(tl.Slope < 0 && SellLevel.CanTrade)
        SellLevel.CanTrade = false;
      if(tl.Slope > 0 && BuyLevel.CanTrade)
        BuyLevel.CanTrade = false;
    }

    [TradeConditionSetCorridor]
    public TradeConditionDelegate TradeStripOk {
      get {
        return () => {
          SetTradeStrip();
          var canSetStrip = true;// Trades.IsEmpty();
          var tradeLevles = GetTradeLevelsToTradeStrip();
          _tipRatioCurrent = TradeLevelsEdgeRatio(tradeLevles).Min(_ratesHeightCma / tradeLevles.Height());
          var tipRatioOk = IsTresholdAbsOk(_tipRatioCurrent, TipRatio);
          if(!tipRatioOk)
            BuySellLevelsForEach(sr => sr.CanTradeEx = false);
          var td = TradeDirectionByBool(IsCurrentPriceInsideBlueStrip && tipRatioOk);
          if(canSetStrip && td.HasAny())
            SetTradeLevelsToTradeStrip();
          return td;
        };
      }
    }
    private double TradeLevelsEdgeRatio(double[] tradeLevles) {
      return TrendLines2Trends
        .YieldIf(p => !p.IsEmpty, p => p.Slope.SignUp())
        .Select(ss => {
          var tradeLevel = ss > 0 ? tradeLevles.Min() : tradeLevles.Max();
          var extream = ss > 0 ? _RatesMax : _RatesMin;
          var tip = extream.Abs(tradeLevel);
          return RatesHeight / tip;
        })
        .DefaultIfEmpty(double.NaN)
        .Single();
    }
    #endregion

    #region After Tip
    public TradeConditionDelegate IsInOk {
      get {
        return () => TradeDirectionByBool(IsCurrentPriceInsideTradeLevels);
      }
    }
    public TradeConditionDelegate IsIn2Ok {
      get {
        return () => TradeDirectionByBool(IsCurrentPriceInsideTradeLevels2);
      }
    }
    [TradeConditionTurnOff]
    public TradeConditionDelegate ToStripOk {
      get {
        #region Locals
        Action setGreenStrip = () => {
          if(CenterOfMassSell2.IsNaN() & !TrendLines2Trends.IsEmpty) {
            var ofset = TrendLines2.Value[0].Trends.PriceAvg1 - TrendLines2.Value[1].Trends.PriceAvg1;
            CenterOfMassBuy2 = CenterOfMassBuy + ofset;
            CenterOfMassSell2 = CenterOfMassSell + ofset;
          }
        };
        var maxMin = MonoidsCore.ToFunc(0.0, 0.0, (max, min) => new { max, min });
        var maxMinFunc = MonoidsCore.ToFunc(() => new[] { maxMin(CenterOfMassBuy, CenterOfMassSell), maxMin(CenterOfMassBuy2, CenterOfMassSell2) });
        var isUp = MonoidsCore.ToFunc(0.0, maxMin(0, 0), maxMin(0, 0), (cp, mm, mm2) => {
          return mm2.min < mm.max || cp > mm.max
          ? TradeDirections.None
          : TradeDirections.Up;
        });
        var hasNaN = MonoidsCore.ToFunc(() => new[] { CenterOfMassBuy, CenterOfMassSell, CenterOfMassBuy2, CenterOfMassSell2 }.Any(double.IsNaN));
        var isDown = MonoidsCore.ToFunc(0.0, maxMin(0, 0), maxMin(0, 0), (cp, mm, mm2) => {
          return mm2.max > mm.min || cp < mm.min
          ? TradeDirections.None
          : TradeDirections.Down;
        });
        var conds = new[] { isUp, isDown };
        #endregion
        return () => {
          setGreenStrip();
          var maxMins = maxMinFunc();
          var cp = CurrentPrice.Average;
          var evals = conds.SelectMany(ud => new[] { ud(cp, maxMins[0], maxMins[1]), ud(cp, maxMins[1], maxMins[0]) });
          try {
            return hasNaN() ? TradeDirections.None : evals.SingleOrDefault(eval => eval.HasAny());
          } catch(Exception exc) {
            Log = exc;
            throw;
          }
        };
      }
    }

    [TradeConditionTurnOff]
    public TradeConditionDelegate CorlOk {
      get {
        return () => TradeDirectionByBool(TradingMacroOther().Any(tm => IsFathessOk(tm)));
      }
    }
    [TradeConditionTurnOff]
    public TradeConditionDelegate WvLenOk {
      get {
        return () => TradeDirectionByBool(TradingMacroOther().Any(tm => IsDistanceCmaOk(tm)));
      }
    }

    [TradeConditionTurnOff]
    public TradeConditionDelegate AnglesOk {
      get {
        Func<Func<Rate.TrendLevels, bool>, Func<Rate.TrendLevels, Rate.TrendLevels, bool>, TradeDirections, TradeDirections> aok = (signPredicate, slopeCompare, success) => {
          var cUp = TrendLinesTrendsAll.OrderByDescending(tl => tl.Count)
            .Where(signPredicate)
            .Scan((tlp, tln) => slopeCompare(tlp, tln) ? tln : Rate.TrendLevels.Empty)
            .TakeWhile(tl => !tl.IsEmpty)
            .Count();
          return cUp == TrendLinesTrendsAll.Length - 1 ? success : TradeDirections.None;
        };
        var up = MonoidsCore.ToFunc(() => aok(tl => tl.Slope > 0, (s1, s2) => s1.Slope < s2.Slope, TradeDirections.Down));
        var down = MonoidsCore.ToFunc(() => aok(tl => tl.Slope < 0, (s1, s2) => s1.Slope > s2.Slope, TradeDirections.Up));
        return () => up() | down();
      }
    }

    bool IsFathessOk(TradingMacro tm) { return tm.WaveRanges.Take(1).Any(wr => Angle.Sign() == wr.Slope.Sign() &&  wr.IsFatnessOk); }
    bool IsDistanceCmaOk(TradingMacro tm) { return tm.WaveRanges.Take(1).Any(wr 
      => TrendLines2Trends.Slope.Sign() == wr.Slope.Sign() && wr.DistanceCma>=StDevByPriceAvgInPips); }

    #endregion

    public TradeConditionDelegate TrdCorChgOk {
      get {
        return () => {
          return TradeDirectionByBool(BuySellLevels.HasTradeCorridorChanged());
        };
      }
    }
    #region Helpers
    public SuppRes[] BuySellLevels { get { return new[] { BuyLevel, SellLevel }; } }
    void BuySellLevelsForEach(Action<SuppRes> action) {
      BuySellLevels.ForEach(action);
    }
    void BuySellLevelsForEach(Func<SuppRes, bool> predicate, Action<SuppRes> action) {
      BuySellLevels.Where(predicate).ForEach(action);
    }
    [MethodImpl(MethodImplOptions.Synchronized)]
    public double SetTradeStrip() {
      if(CanTriggerTradeDirection() && !TradeConditionsTradeStrip().Any(tc => tc.HasNone())) {
        var tlbuy = GetTradeLevel(true, double.NaN);//TradeLevelFuncs[TradeLevelBy.PriceMax]();
        var tlSell = GetTradeLevel(false, double.NaN);//TradeLevelFuncs[TradeLevelBy.PriceMin]();
        var bsHeight = CenterOfMassBuy.Abs(CenterOfMassSell).IfNaN(double.MaxValue);
        var tlHeight = tlbuy.Abs(tlSell);
        //var tlAvg = tlbuy.Avg(tlSell);
        double tlMax = tlbuy.Max(tlSell), tlMin = tlSell.Min(tlbuy);
        double comMax = CenterOfMassSell.Max(CenterOfMassBuy), comMin = CenterOfMassSell.Min(CenterOfMassBuy);
        var tlJumped = (tlMin > comMax || tlMax < comMin)
          && tlHeight.Div(bsHeight) <= TradeStripJumpRatio;
        if(tlJumped) {
          CenterOfMassBuy2 = CenterOfMassBuy;
          CenterOfMassSell2 = CenterOfMassSell;
          BuySellLevelsForEach(sr => sr.CanTradeEx = false);
        }
        var canSetLevel = (bsHeight.IsNaN() || tlJumped || tlHeight < bsHeight);
        if(canSetLevel) {
          CenterOfMassBuy = tlbuy;
          CenterOfMassSell = tlSell;
        }
        return CenterOfMassBuy.Abs(CenterOfMassSell);
      }
      return double.NaN;
    }

    private void SetTradeLevelsToTradeStrip() {
      BuyLevel.RateEx = CenterOfMassBuy.IfNaN(GetTradeLevel(true, BuyLevel.Rate));
      SellLevel.RateEx = CenterOfMassSell.IfNaN(GetTradeLevel(false, SellLevel.Rate));
    }
    private double[] GetTradeLevelsToTradeStrip() {
      return new[] { CenterOfMassBuy, CenterOfMassSell };
    }

    public void SetTradeCorridorToMinHeight2() {
      if(CanTriggerTradeDirection()) {
        var bsl = new[] { BuyLevel, SellLevel };
        var buy = GetTradeLevel(true, double.NaN);
        var sell = GetTradeLevel(false, double.NaN);
        var zip = bsl.Zip(new[] { buy, sell }, (sr, bs) => new { sr, bs });
        zip.Where(x => !x.sr.InManual)
          .ForEach(x => {
            x.sr.InManual = true;
            x.sr.ResetPricePosition();
            x.sr.Rate = x.bs;
          });
        var bsHeight = BuyLevel.Rate.Abs(SellLevel.Rate);
        var tlHeight = buy.Abs(sell);
        var tlAvg = buy.Avg(sell);
        var tlJumped = !tlAvg.Between(SellLevel.Rate, BuyLevel.Rate) && tlHeight.Div(bsHeight) > 1.5;
        if(tlJumped)
          bsl.ForEach(sr => sr.CanTrade = false);
        var canSetLevel = (tlJumped || tlHeight < bsHeight);
        if(canSetLevel)
          lock (_rateLocker) {
            zip.ForEach(x => {
              var rate = x.bs;
              var rateJump = InPips(rate.Abs(x.sr.Rate));
              var reset = rateJump > 1;
              if(reset)
                x.sr.ResetPricePosition();
              x.sr.Rate = rate;
              if(reset)
                x.sr.ResetPricePosition();
            });
          }
      }
    }

    public void SetTradeCorridorToHignLow() {
      var angle = TrendLines2Trends.Angle.Sign() + TrendLinesTrends.Angle.Sign();
      if(angle != 0) {
        var bsl = new[] { BuyLevel, SellLevel };
        var buy = GetTradeLevel(true, double.NaN);
        var sell = GetTradeLevel(false, double.NaN);
        var zip = bsl.Zip(new[] { buy, sell }, (sr, bs) => new { sr, bs });
        var tlAvg = buy.Avg(sell);
        var canSetLevel = (!tlAvg.Between(SellLevel.Rate, BuyLevel.Rate));
        if(zip.Where(x => !x.sr.InManual || canSetLevel)
            .Do(x => {
              x.sr.InManual = true;
              x.sr.ResetPricePosition();
              x.sr.Rate = x.bs;
            }).Count() == 0) {
          lock (_rateLocker) {
            var hasTipOk = TradeConditionsHave(TipOk);
            zip.ForEach(x => {
              var rate = angle > 0 ? x.bs.Max(x.sr.Rate) : x.bs.Min(x.sr.Rate);
              var rateJump = InPips(rate.Abs(x.sr.Rate));
              var reset = rateJump > 1;
              if(reset)
                x.sr.ResetPricePosition();
              if(x.sr.Rate != rate) {
                x.sr.Rate = rate;
              }
              if(reset)
                x.sr.ResetPricePosition();
            });
          }
        }
      }
    }
    #endregion
    #endregion

    double _tipRatio = 4;
    [Category(categoryActive)]
    [WwwSetting(wwwSettingsTradingConditions)]
    public double TipRatio {
      get { return _tipRatio; }
      set {
        _tipRatio = value;
        OnPropertyChanged(() => TipRatio);
      }
    }
    double _rhsdRatio = 0.4;
    [Category(categoryActive)]
    [WwwSetting(wwwSettingsTradingConditions)]
    public double RhSDRatio {
      get {
        return _rhsdRatio;
      }

      set {
        _rhsdRatio = value;
        OnPropertyChanged(() => RhSDRatio);
      }
    }
    [TradeConditionTurnOff]
    public TradeConditionDelegate RhSDAvgOk {
      get { return () => TradeDirectionByBool(_macd2Rsd >= MacdRsdAvg); }
    }

    public Func<TradeDirections> CmaRsdOk {
      get {
        return () => TradeDirectionByBool(IsTresholdAbsOk(MacdRsdAvg, MacdRsdAvgLevel));
      }
    }

    #region WwwInfo
    public object WwwInfo() {
      return new {
        GRBHRatio = TrendHeighRatio(),
        GRBRatio_ = TrendHeighRatio(TrendLines2Trends),
        TipRatio_ = _tipRatioCurrent.Round(1),
        GrnAngle_ = TrendLines1Trends.Angle.Round(1),
        RedAngle_ = TrendLinesTrends.Angle.Round(1),
        BlueAngle = TrendLines2Trends.Angle.Round(1)
      };
      // RhSDAvg__ = _macd2Rsd.Round(1) })
      // CmaDist__ = InPips(CmaMACD.Distances().Last()).Round(3) })
    }
    #endregion

    #region Angles
    TradeDirections TradeDirectionByTradeLevels(double buyLevel, double sellLevel) {
      return buyLevel.Avg(sellLevel).PositionRatio(_RatesMin, _RatesMax).ToPercent() > 50
        ? TradeDirections.Down
        : TradeDirections.Up;
    }
    TradeDirections TradeDirectionByAngleSign(double angle) {
      return angle > 0
        ? TradeDirections.Down
        : TradeDirections.Up;
    }
    TradeDirections TradeDirectionByBool(bool value) {
      return value
        ? TradeDirections.Both
        : TradeDirections.None;
    }
    TradeDirections TradeDirectionByAngleCondition(Rate.TrendLevels tls, double tradingAngleRange) {
      return IsTresholdAbsOk(tls.Angle, tradingAngleRange)
        ? tradingAngleRange > 0
        ? TradeDirectionByAngleSign(tls.Angle)
        : TradeDirections.Both
        : TradeDirections.None;
    }
    [TradeCondition(TradeConditionAttribute.Types.And)]
    public TradeConditionDelegate GreenAngOk { get { return () => TradeDirectionByAngleCondition(TrendLines1Trends, TrendAngleGreen); } }
    [TradeCondition(TradeConditionAttribute.Types.And)]
    public TradeConditionDelegate RedAngOk { get { return () => TradeDirectionByAngleCondition(TrendLinesTrends, TrendAngleRed); } }
    [TradeConditionTurnOff]
    public TradeConditionDelegate BlueAngOk {
      get {
        return () => TrendAngleBlue1.IsNaN()
        ? TradeDirectionByAngleCondition(TrendLines2Trends, TrendAngleBlue0)
        : TrendLines2Trends.Angle.Abs().Between(TrendAngleBlue0, TrendAngleBlue1)
        ? TradeDirectionByAngleSign(TrendLines2Trends.Angle)
        : TradeDirections.None;
      }
    }

    [TradeConditionTradeStrip]
    public TradeConditionDelegate GRBRatioOk {
      get {
        return () => TradeDirectionByBool(IsTresholdAbsOk(TrendHeighRatio(TrendLines2Trends), TrendAnglesPerc));
      }
    }
    [TradeConditionTradeStrip]
    public TradeConditionDelegate GRBHRatioOk {
      get {
        return () => IsTresholdAbsOk(TrendHeighRatio(), TrendHeightPerc)
          ? TradeDirections.Both
          : TradeDirections.None;
      }
    }
    public Func<TradeDirections> AngRBRatioOk {
      get {
        return () => IsTresholdAbsOk(TrendLines2Trends.Angle.Percentage(TrendLinesTrends.Angle).ToPercent(), TrendAnglesPerc)
          ? TradeDirections.Both
          : TradeDirections.None;
      }
    }
    int TrendAnglesRatio() {
      Func<IList<Rate>, double> spread = tls => tls[1].Trends.Angle;
      return BlueBasedRatio(spread).ToPercent();
    }
    int TrendPrice1Ratio() {
      Func<IList<Rate>, double> spread = tls => tls[1].Trends.Angle;
      var blueSpread = spread(TrendLines2.Value);
      var redSpread = spread(TrendLines.Value);
      var greenSpread = spread(TrendLines1.Value);
      return BlueBasedRatio(blueSpread, redSpread, greenSpread).ToPercent();
    }

    public static double BlueBasedRatio(double blue, double red, double green) {
      return new[] { blue.Percentage(red), blue.Percentage(green) }.Average().Abs();
    }
    public double BlueBasedRatio(Func<IList<Rate>, double> spread) {
      var blue = spread(TrendLines2.Value);
      var red = spread(TrendLines.Value);
      var green = spread(TrendLines1.Value);
      return new[] { blue.Percentage(red), blue.Percentage(green) }.Average().Abs();
    }

    int TrendHeighRatio() {
      Func<IList<Rate>, double> spread = tls => tls.Take(1).Select(tl => tl.Trends.PriceAvg2 - tl.Trends.PriceAvg3).DefaultIfEmpty(double.NaN).Single();
      return BlueBasedRatio(spread).ToPercent();
    }
    int TrendHeighRatio(Rate.TrendLevels baseLevel) {
      if(baseLevel.IsEmpty)
        return 200;
      Func<IList<Rate>, double[]> spread = tls => tls.TakeLast(1).Select(tl =>
        new[] { tl.Trends.PriceAvg1.Abs(baseLevel.PriceAvg2), baseLevel.PriceAvg3.Abs(tl.Trends.PriceAvg1) }
        )
        .DefaultIfEmpty(new double [0])
        .Single();
      var blue = baseLevel.PriceAvg2 - baseLevel.PriceAvg1;
      var greenLevels = spread(TrendLines1.Value).Select(gl => gl.Percentage(blue)).Max();
      var redLevels = spread(TrendLines.Value).Select(gl=>gl.Percentage(blue)).Max();
      return greenLevels.Avg(redLevels).ToPercent();
    }
    #endregion

    #region TimeFrameOk
    bool TestTimeFrame() {
      return RatesTimeSpan()
      .Where(ts => ts != TimeSpan.Zero)
      .Select(ratesSpan => TimeFrameTresholdTimeSpan2 > TimeSpan.Zero
        ? ratesSpan <= TimeFrameTresholdTimeSpan && ratesSpan >= TimeFrameTresholdTimeSpan2
        : IsTresholdAbsOk(ratesSpan, TimeFrameTresholdTimeSpan)
        )
        .DefaultIfEmpty()
        .Any(b => b);
    }

    static readonly Calendar callendar = CultureInfo.GetCultureInfo("en-US").Calendar;
    TimeSpan _RatesTimeSpanCache = TimeSpan.Zero;
    DateTime[] _RateForTimeSpanCache = new DateTime[0];
    private IEnumerable<TimeSpan> RatesTimeSpan() {
      return UseRates(rates => rates.Count == 0
        ? TimeSpan.Zero
        : RatesTimeSpan(rates));// rates.Last().StartDate - rates[0].StartDate);
    }

    private TimeSpan RatesTimeSpan(IList<Rate> rates) {
      var ratesLast = new[] { rates[0].StartDate, rates.Last().StartDate };
      if((from rl in ratesLast join ch in _RateForTimeSpanCache on rl equals ch select rl).Count() == 2)
        return _RatesTimeSpanCache;
      _RateForTimeSpanCache = ratesLast;
      var periodMin = BarPeriodInt.Max(1);
      return _RatesTimeSpanCache = rates
        .Pairwise((r1, r2) => r1.StartDate.Subtract(r2.StartDate).Duration())
        .Where(ts => ts.TotalMinutes <= periodMin)
        .Sum(ts => ts.TotalMinutes)
        .FromMinutes();
    }

    //[TradeConditionAsleep]
    [TradeConditionTurnOff]
    public TradeConditionDelegate TimeFrameOk {
      get {
        return () => TestTimeFrame()
        ? TradeDirections.Both
        : TradeDirections.None;
        ;
      }
    }
    #endregion

    #region Outsides
    [Description("'Green' corridor is outside the 'Red' and 'Blue' ones")]
    public Func<TradeDirections> GreenOk {
      get {
        return () =>
          TrendLines1Trends.PriceAvg2 >= TrendLinesTrends.PriceAvg2.Max(TrendLines2Trends.PriceAvg2)
          ? TradeDirections.Down
          : TrendLines1Trends.PriceAvg3 <= TrendLinesTrends.PriceAvg3.Min(TrendLines2Trends.PriceAvg3)
          ? TradeDirections.Up
          : TradeDirections.None;
      }
    }
    public Func<TradeDirections> GreenExtOk {
      get {
        return () =>
          TrendLines1Trends.PriceAvg2 >= TrendLinesTrends.PriceAvg21.Max(TrendLines2Trends.PriceAvg2)
          ? TradeDirections.Up
          : TrendLines1Trends.PriceAvg3 <= TrendLinesTrends.PriceAvg31.Min(TrendLines2Trends.PriceAvg3)
          ? TradeDirections.Down
          : TradeDirections.None;
      }
    }
    [TradeConditionAsleep]
    public TradeConditionDelegate OutsideAnyOk {
      get { return () => Outside1Ok() | OutsideOk() | Outside2Ok(); }
    }
    TradeDirections TradeDirectionByAll(params TradeDirections[] tradeDirections) {
      return tradeDirections
        .Where(td => td.HasAny())
        .Buffer(3)
        .Where(b => b.Count == 3 && b.Distinct().Count() == 1)
        .Select(b => b[2])
        .DefaultIfEmpty(TradeDirections.None)
        .Single();
    }
    [TradeConditionAsleep]
    public TradeConditionDelegate OutsideAllOk {
      [TradeCondition(TradeConditionAttribute.Types.Or)]
      get {
        return () =>
          IsCurrentPriceOutsideCorridor(MySelfNext, tm => tm.TrendLinesTrends, tl => tl.PriceAvg31, tl => tl.PriceAvg21, IsReverseStrategy).HasAny()
          ? TradeDirections.None
          : TradeDirectionByAll(Outside1Ok(), OutsideOk(), Outside2Ok());
      }
    }
    private bool IsOuside(TradeDirections td) { return td == TradeDirections.Up || td == TradeDirections.Down; }
    [TradeConditionAsleep]
    public TradeConditionDelegate OutsideExtOk {
      get {
        return () => TradeDirectionByAll(
          IsCurrentPriceOutsideCorridor(MySelfNext, tm => tm.TrendLinesTrends, tl => tl.PriceAvg31, tl => tl.PriceAvg21, IsReverseStrategy),
          Outside1Ok(),
          Outside2Ok());
      }
    }
    TradeDirections IsCurrentPriceOutsideCorridorSelf(Func<TradingMacro, Rate.TrendLevels> trendLevels, Func<Rate.TrendLevels, double> min, Func<Rate.TrendLevels, double> max) {
      return IsCurrentPriceOutsideCorridor(MySelf, trendLevels, min, max, IsReverseStrategy);
    }

    [TradeConditionAsleep]
    [TradeCondition(TradeConditionAttribute.Types.Or)]
    public TradeConditionDelegate OutsideOk {
      get { return () => IsCurrentPriceOutsideCorridor(MySelfNext, tm => tm.TrendLinesTrends); }
    }
    [TradeConditionAsleep]
    [TradeCondition(TradeConditionAttribute.Types.Or)]
    public TradeConditionDelegate Outside1Ok {
      get { return () => IsCurrentPriceOutsideCorridor(MySelfNext, tm => tm.TrendLines1Trends); }
    }
    [TradeConditionAsleep]
    [TradeCondition(TradeConditionAttribute.Types.Or)]
    public TradeConditionDelegate Outside2Ok {
      get { return () => IsCurrentPriceOutsideCorridor(MySelfNext, tm => tm.TrendLines2Trends); }
    }
    #region Helpers
    TradeDirections IsCurrentPriceOutsideCorridor(
      Func<TradingMacro, bool> tmPredicate,
      Func<TradingMacro, Rate.TrendLevels> trendLevels,
      Func<Rate.TrendLevels, double> min,
      Func<Rate.TrendLevels, double> max,
      bool ReverseStrategy
      ) {
      Func<TradeDirections> onBelow = () => TradeDirections.Up;
      Func<TradeDirections> onAbove = () => TradeDirections.Down;
      return TradingMacroOther(tmPredicate)
        .Select(tm => trendLevels(tm))
        .Select(tls =>
          CurrentPrice.Average < min(tls) ? onBelow()
          : CurrentPrice.Average > max(tls) ? onAbove()
          : TradeDirections.None)
        .DefaultIfEmpty(TradeDirections.None)
        .First();
    }
    TradeDirections IsCurrentPriceOutsideCorridorSelf(Func<TradingMacro, Rate.TrendLevels> trendLevels) {
      return IsCurrentPriceOutsideCorridor(MySelf, trendLevels);
    }
    TradeDirections IsCurrentPriceOutsideCorridor(Func<TradingMacro, bool> tmPredicate, Func<TradingMacro, Rate.TrendLevels> trendLevels) {
      return IsCurrentPriceOutsideCorridor(tmPredicate, trendLevels, tl => tl.PriceAvg3, tl => tl.PriceAvg2, InPips(BuyLevel.Rate - SellLevel.Rate) > 0);
    }
    TradeDirections IsCurrentPriceOutsideCorridor2Self(Func<TradingMacro, Rate.TrendLevels> trendLevels) {
      return IsCurrentPriceOutsideCorridor2(MySelf, trendLevels);
    }
    TradeDirections IsCurrentPriceOutsideCorridor2(Func<TradingMacro, bool> tmPredicate, Func<TradingMacro, Rate.TrendLevels> trendLevels) {
      return IsCurrentPriceOutsideCorridor(tmPredicate, trendLevels, tl => tl.PriceAvg32, tl => tl.PriceAvg22, IsReverseStrategy);
    }
    private bool IsOutsideOk(Func<TradingMacro, bool> tmPredicate, string canTradeKey, Func<TradingMacro, Rate.TrendLevels> trendLevels) {
      var td = IsCurrentPriceOutsideCorridor(tmPredicate, trendLevels);
      var ok = IsOuside(td);
      if(ok && !_canOpenTradeAutoConditions.ContainsKey(canTradeKey))
        _canOpenTradeAutoConditions.TryAdd(canTradeKey, () => td);
      if((!ok || TradeDirection != TradeDirections.Auto) && _canOpenTradeAutoConditions.ContainsKey(canTradeKey)) {
        Func<TradeDirections> f;
        _canOpenTradeAutoConditions.TryRemove(canTradeKey, out f);
      }
      return ok;
    }
    #endregion

    #endregion

    #region TradingMacros
    private IEnumerable<TradingMacro> TradingMacroOther(Func<TradingMacro, bool> predicate) {
      return TradingMacrosByPair().Where(predicate);
    }
    private IEnumerable<TradingMacro> TradingMacroOther() {
      return TradingMacrosByPair().Where(tm => tm != this);
    }
    private IEnumerable<TradingMacro> TradingMacrosByPair() {
      return _tradingMacros.Where(tm => tm.Pair == Pair).OrderBy(tm => PairIndex);
    }
    #endregion

    #region TradeConditions
    public Tuple<TradeConditionDelegate, PropertyInfo>[] _TradeConditions = new Tuple<TradeConditionDelegate, PropertyInfo>[0];
    public Tuple<TradeConditionDelegate, PropertyInfo>[] TradeConditions {
      get { return _TradeConditions; }
      set {
        _TradeConditions = value;
        OnPropertyChanged("TradeConditions");
      }
    }
    bool HasTradeConditions { get { return TradeConditions.Any(); } }
    void TradeConditionsReset() { TradeConditions = new Tuple<TradeConditionDelegate, PropertyInfo>[0]; }
    public T[] GetTradeConditions<A, T>(Func<A, bool> attrPredicate, Func<TradeConditionDelegate, PropertyInfo, TradeConditionAttribute.Types, T> map) where A : Attribute {
      return (from x in this.GetPropertiesByTypeAndAttribute(() => (TradeConditionDelegate)null, attrPredicate, (v, p) => new { v, p })
              let a = x.p.GetCustomAttributes<TradeConditionAttribute>()
              .DefaultIfEmpty(new TradeConditionAttribute(TradeConditionAttribute.Types.And)).First()
              select map(x.v, x.p, a.Type))
              .ToArray();
    }
    public Tuple<TradeConditionDelegate, PropertyInfo>[] GetTradeConditions(Func<PropertyInfo, bool> predicate = null) {
      return GetType().GetProperties()
        .Where(p => p.PropertyType == typeof(TradeConditionDelegate))
        .Where(p => predicate == null || predicate(p))
        .Select(p => Tuple.Create((TradeConditionDelegate)p.GetValue(this), p))
        .ToArray();

      //return new[] { WideOk, TpsOk, AngleOk, Angle0Ok };
    }
    public static string ParseTradeConditionNameFromMethod(MethodInfo method) {
      return Regex.Match(method.Name, "<(.+)>").Groups[1].Value.Substring(4);

    }
    public static string ParseTradeConditionToNick(string tradeConditionFullName) {
      return Regex.Replace(tradeConditionFullName, "ok$", "", RegexOptions.IgnoreCase);
    }
    public Tuple<TradeConditionDelegate, PropertyInfo>[] TradeConditionsSet(IList<string> names) {
      return TradeConditions = GetTradeConditions().Where(tc => names.Contains(ParseTradeConditionNameFromMethod(tc.Item1.Method))).ToArray();
    }
    bool TradeConditionsHaveTurnOff() {
      return TradeConditionsInfo<TradeConditionTurnOffAttribute>().Any(d => d().HasNone());
    }
    bool TradeConditionsHaveSetCorridor() {
      return TradeConditionsInfo<TradeConditionSetCorridorAttribute>().Any();
    }
    IEnumerable<TradeDirections> TradeConditionsTradeStrip() {
      return TradeConditionsInfo<TradeConditionTradeStripAttribute>().Select(d => d());
    }
    bool TradeConditionsHaveAsleep() {
      return TradeConditionsInfo<TradeConditionAsleepAttribute>().Any(d => d().HasNone()) || !IsTradingDay();
    }
    bool TradeConditionsHave(TradeConditionDelegate td) {
      return TradeConditionsInfo().Any(d => d == td);
    }
    public IEnumerable<TradeConditionDelegate> TradeConditionsInfo<A>() where A : Attribute {
      return TradeConditionsInfo(new Func<Attribute, bool>(a => a.GetType() == typeof(A)), (d, p, ta, s) => d);
    }
    public IEnumerable<T> TradeConditionsInfo<T>(Func<TradeConditionDelegate, PropertyInfo, TradeConditionAttribute.Types, string, T> map) {
      return TradeConditionsInfo((Func<Attribute, bool>)null, map);
    }
    public IEnumerable<T> TradeConditionsInfo<A, T>(Func<A, bool> attrPredicate, Func<TradeConditionDelegate, PropertyInfo, TradeConditionAttribute.Types, string, T> map) where A : Attribute {
      return from tc in TradeConditionsInfo((d, p, s) => new { d, p, s })
             where attrPredicate == null || tc.p.GetCustomAttributes().OfType<A>().Count(attrPredicate) > 0
             from tca in tc.p.GetCustomAttributes<TradeConditionAttribute>().DefaultIfEmpty(new TradeConditionAttribute(TradeConditionAttribute.Types.And))
             select map(tc.d, tc.p, tca.Type, tc.s);
    }
    public void TradeConditionsTrigger() {
      if(IsTrader && CanTriggerTradeDirection() && !HaveTrades() /*&& !HasTradeDirectionTriggers*/) {
        TradeConditionsEval().ForEach(eval => {
          var hasBuy = TradeDirection.HasUp() && eval.HasUp();
          var hasSell = TradeDirection.HasDown() && eval.HasDown();
          var canBuy = IsTurnOnOnly && BuyLevel.CanTrade || hasBuy;
          var canSell = IsTurnOnOnly && SellLevel.CanTrade || hasSell;
          var canBuyOrSell = canBuy || canSell;
          var canTradeBuy = canBuy || (canBuyOrSell && TradeCountMax > 0);
          var canTradeSell = canSell || (canBuyOrSell && TradeCountMax > 0);

          Action<SuppRes, bool> updateCanTrade = (sr, ct) => {
            if(sr.CanTrade != ct) {
              if(sr.CanTrade = ct) {
                var tradesCount = sr.IsSell && eval.HasDown() || sr.IsBuy && eval.HasUp() ? 0 : 1;
                sr.TradesCount = tradesCount + TradeCountStart;
              }
            }
          };

          updateCanTrade(BuyLevel, canTradeBuy);
          updateCanTrade(SellLevel, canTradeSell);
          var isPriceIn = new[] { CurrentEnterPrice(false), this.CurrentEnterPrice(true) }.All(cp => cp.Between(SellLevel.Rate, BuyLevel.Rate));

          if(BuyLevel.CanTrade && SellLevel.CanTrade && canTradeBuy != canTradeSell) {
            BuyLevel.CanTrade = hasBuy && isPriceIn;
            SellLevel.CanTrade = hasSell && isPriceIn;
          }
        });
      }
    }
    bool _isTurnOnOnly = false;
    [Category(categoryActiveYesNo)]
    [WwwSetting(wwwSettingsTradingOther)]
    public bool IsTurnOnOnly {
      get {
        return _isTurnOnOnly;
      }
      set {
        _isTurnOnOnly = value;
      }
    }

    public IEnumerable<TradeDirections> TradeConditionsEvalStartDate() {
      if(!IsTrader)
        return new TradeDirections[0];
      return (from d in TradeConditionsInfo<TradeConditionStartDateTriggerAttribute>()
              let b = d()
              group b by b into gb
              select gb.Key
              )
              .DefaultIfEmpty()
              .OrderBy(b => b)
              .Take(1)
              .Where(b => b.HasAny());
    }
    public IEnumerableCore.Singleable<TradeDirections> TradeConditionsEval() {
      if(!IsTrader)
        return new TradeDirections[0].AsSingleable();
      return (from tc in TradeConditionsInfo((d, p, t, s) => new { d, t, s })
              group tc by tc.t into gtci
              let and = gtci.Select(g => g.d()).ToArray()
              let c = gtci.Key == TradeConditionAttribute.Types.And
              ? and.Aggregate(TradeDirections.Both, (a, td) => a & td)
              : and.Aggregate(TradeDirections.None, (a, td) => a | td)
              select c
              )
              .Scan(TradeDirections.Both, (a, td) => a & td)
              .TakeLast(1)
              .Select(td => td & TradeDirection)
              .AsSingleable();
    }
    public IEnumerable<TradeConditionDelegate> TradeConditionsInfo() {
      return TradeConditionsInfo(TradeConditions, (d, p, s) => d);
    }
    public IEnumerable<T> TradeConditionsInfo<T>(Func<TradeConditionDelegate, PropertyInfo, string, T> map) {
      return TradeConditionsInfo(TradeConditions, map);
    }
    public IEnumerable<T> TradeConditionsAllInfo<T>(Func<TradeConditionDelegate, PropertyInfo, string, T> map) {
      return TradeConditionsInfo(GetTradeConditions(), map);
    }
    public IEnumerable<T> TradeConditionsInfo<T>(IList<Tuple<TradeConditionDelegate, PropertyInfo>> tradeConditions, Func<TradeConditionDelegate, PropertyInfo, string, T> map) {
      return tradeConditions.Select(tc => map(tc.Item1, tc.Item2, ParseTradeConditionNameFromMethod(tc.Item1.Method)));
    }
    [DisplayName("Trade Conditions")]
    [Category(categoryActiveFuncs)]
    public string TradeConditionsSave {
      get { return string.Join(MULTI_VALUE_SEPARATOR, TradeConditionsInfo((tc, pi, name) => name)); }
      set {
        TradeConditionsSet(value.Split(MULTI_VALUE_SEPARATOR[0]));
      }
    }

    #endregion

    #endregion
  }
}
