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

namespace HedgeHog.Alice.Store {
  class TradeConditionStartDateTriggerAttribute : Attribute {
  }
  class TradeConditionAsleepAttribute : Attribute {
  }
  class TradeConditionTurnOffAttribute : Attribute {
  }
  class TradeConditionUseCorridorAttribute : Attribute {
  }
  partial class TradingMacro {
    #region TradeOpenActions
    public delegate void TradeOpenAction(Trade trade);
    public TradeOpenAction FreezeOnTrade { get { return trade => FreezeCorridorStartDate(); } }
    public TradeOpenAction WrapOnTrade { get { return trade => WrapTradeInCorridor(); } }

    public TradeOpenAction Avg1ExitOnTrade {
      get {
        return trade => Task.Delay(100).ContinueWith(_ => SetCorridorExitImpl(trade, TradeLevelBy.Avg1Max, TradeLevelBy.Avg1Min));
      }
    }
    public TradeOpenAction Avg1GRBExitOnTrade {
      get {
        return trade => Task.Delay(100).ContinueWith(_ => SetCorridorExitImpl(trade, TradeLevelBy.Avg2GRBMax, TradeLevelBy.Avg3GRBMin));
      }
    }
    public TradeOpenAction GreenExitOnTrade {
      get {
        return trade => Task.Delay(100).ContinueWith(_ => SetCorridorExitImpl(trade, TradeLevelBy.PriceHigh0, TradeLevelBy.PriceLow0));
      }
    }

    public TradeOpenAction RedExitOnTrade {
      get {
        return trade => Task.Delay(100).ContinueWith(_ => SetCorridorExitImpl(trade, TradeLevelBy.PriceAvg2, TradeLevelBy.PriceAvg3));
      }
    }

    public TradeOpenAction BlueExitOnTrade {
      get {
        return trade => Task.Delay(100).ContinueWith(_ => SetCorridorExitImpl(trade, TradeLevelBy.PriceHigh, TradeLevelBy.PriceLow));
      }
    }

    TradeOpenAction SetCorridorExit(TradeLevelBy buy, TradeLevelBy sell) {
      return trade => Task.Delay(100).ContinueWith(_ => SetCorridorExitImpl(trade, buy, sell));
    }
    private void SetCorridorExitImpl(Trade trade, TradeLevelBy buy, TradeLevelBy sell) {
      if(trade.IsBuy) {
        var level = TradeLevelFuncs[buy]();
        if(InPips(level - trade.Open) > 5)
          LevelBuyCloseBy = buy;
      } else {
        var level = TradeLevelFuncs[TradeLevelBy.PriceLow0]();
        if(InPips(-level + trade.Open) > 5)
          LevelSellCloseBy = TradeLevelBy.PriceLow0;
      }
    }

    TradeOpenAction[] _tradeOpenActions = new TradeOpenAction[0];
    void OnTradeConditionsReset() { _tradeOpenActions = new TradeOpenAction[0]; }
    public TradeOpenAction[] GetTradeOpenActions() {
      return GetType().GetProperties()
        .Where(p => p.PropertyType == typeof(TradeOpenAction))
        .Select(p => p.GetValue(this))
        .Cast<TradeOpenAction>()
        .ToArray();
    }

    public TradeOpenAction[] TradeOpenActionsSet(IList<string> names) {
      return _tradeOpenActions = GetTradeOpenActions().Where(tc => names.Contains(ParseTradeConditionName(tc.Method))).ToArray();
    }
    public IEnumerable<T> TradeOpenActionsInfo<T>(Func<TradeOpenAction, string, T> map) {
      return TradeOpenActionsInfo(_tradeOpenActions, map);
    }
    public IEnumerable<T> TradeOpenActionsAllInfo<T>(Func<TradeOpenAction, string, T> map) {
      return TradeOpenActionsInfo(GetTradeOpenActions(), map);
    }
    public IEnumerable<T> TradeOpenActionsInfo<T>(IList<TradeOpenAction> tradeOpenActions, Func<TradeOpenAction, string, T> map) {
      return tradeOpenActions.Select(tc => map(tc, ParseTradeConditionName(tc.Method)));
    }
    [DisplayName("Trade Actions")]
    [Category(categoryActiveFuncs)]
    public string TradeOpenActionsSave {
      get { return string.Join(MULTI_VALUE_SEPARATOR, TradeOpenActionsInfo((tc, name) => name)); }
      set {
        TradeOpenActionsSet(value.Split(MULTI_VALUE_SEPARATOR[0]));
      }
    }
    #endregion

    #region TradeConditions
    public delegate TradeDirections TradeConditionDelegate();
    #region TradeDirection Helpers
    TradeDirections TradeDirectionBoth(bool ok) { return ok ? TradeDirections.Both : TradeDirections.None; }
    TradeConditionDelegate TradeDirectionEither(Func<bool> ok) { return () => ok() ? TradeDirections.Up : TradeDirections.Down; }
    TradeDirections IsTradeConditionOk(Func<TradingMacro, bool> tmPredicate, Func<TradingMacro, TradeDirections> condition) {
      return TradingMacroOther(tmPredicate).Take(1).Select(condition).DefaultIfEmpty(TradeDirections.Both).First();
    }
    #endregion

    double _tipRatioCurrent = double.NaN;

    int _bigWaveIndex = 0;
    [Category(categoryActive)]
    [WwwSetting(wwwSettingsTradingConditions)]
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
    public TradeConditionDelegate BigWaveOk {
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
    [TradeConditionAsleep]
    public Func<TradeDirections> FatWaveOk {
      get {
        return () => IsWaveOk((wr, tm) => wr.UID >= tm.WaveRangeAvg.UID, 0);
      }
    }

    [TradeConditionTurnOff]
    public TradeConditionDelegate TriplettOk {
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
          var isSell = BuyLevel.Rate >= _RatesMax;
          var isBuy = SellLevel.Rate <= _RatesMin;
          var tradeLevel = isSell ? SellLevel.Rate : BuyLevel.Rate;
          var extream = isSell ? _RatesMax.Max(BuyLevel.Rate) : _RatesMin.Min(SellLevel.Rate);
          _tipRatioCurrent = _ratesHeightCma / BuyLevel.Rate.Abs(SellLevel.Rate);// tradeLevel.Abs(extream);
          var isOutside = isSell || isBuy;
          return isOutside && IsTresholdAbsOk(_tipRatioCurrent, TipRatio)
            ? isSell
            ? TradeDirections.Down
            : TradeDirections.Up
            : TradeDirections.None;
        };
      }
    }

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


    #region WwwInfo
    public ExpandoObject WwwInfo() {
      return new ExpandoObject()
        .Merge(new { GRBRatio___ = TrendPrice1Ratio() })
        .Merge(new { GrnAngle_ = TrendLines1Trends.Angle.Round(1) })
        .Merge(new { RedAngle_ = TrendLinesTrends.Angle.Round(1) })
        .Merge(new { BlueAngle = TrendLines2Trends.Angle.Round(1) })
        .Merge(new { CmaDist__ = InPips(CmaMACD.Distances().Last()).Round(3) })
        .Merge(new { TipRatio_ = _tipRatioCurrent.Round(1) });
    }
    #endregion

    #region Angles
    TradeDirections TradeDirectionByAngleSign(double angle) {
      return angle > 0
        ? TradeDirections.Down
        : TradeDirections.Up;
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

    public TradeConditionDelegate GRBRatioOk {
      get {
        return () => IsTresholdAbsOk(TrendPrice1Ratio(), TrendAnglesPerc)
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
      return TrendLinesTrendsCrossJoin
      .Select(b => b[0].Angle.Percentage(b[1].Angle).ToPercent())
      .Max();
      //.CartesianProduct()
      //.Select(b=>b.ToList())
      //.Where(b=>b[0]!=b[1])
      //.Select(b=>b[0].Angle.Percentage(b[1].Angle).ToPercent())
      //.Min();
    }
    int TrendPrice1Ratio() {
      return GetTrendLinesCrossJoinValues(tls => tls[0].PriceAvg1.Abs(tls[1].PriceAvg1) / tls[1].Count)
      .Select(b => b[0].Percentage(b[1]).ToPercent())
      .Average().ToInt();
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

    private IEnumerable<TimeSpan> RatesTimeSpan() {
      return UseRates(rates => rates.Count == 0
            ? TimeSpan.Zero
            : rates.Last().StartDate - rates[0].StartDate);
    }

    [TradeConditionAsleep]
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
    public TradeConditionDelegate GreenOk {
      get {
        return () =>
          TrendLines1Trends.PriceAvg2 >= TrendLinesTrends.PriceAvg2.Max(TrendLines2Trends.PriceAvg2)
          ? TradeDirections.Down
          : TrendLines1Trends.PriceAvg3 <= TrendLinesTrends.PriceAvg3.Min(TrendLines2Trends.PriceAvg3)
          ? TradeDirections.Up
          : TradeDirections.None;
      }
    }
    public TradeConditionDelegate GreenExtOk {
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
      return _tradingMacros.Where(tm => tm.Pair == Pair);
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
    public static string ParseTradeConditionName(MethodInfo method) {
      return Regex.Match(method.Name, "<(.+)>").Groups[1].Value.Substring(4);

    }
    public Tuple<TradeConditionDelegate, PropertyInfo>[] TradeConditionsSet(IList<string> names) {
      return TradeConditions = GetTradeConditions().Where(tc => names.Contains(ParseTradeConditionName(tc.Item1.Method))).ToArray();
    }
    bool TradeConditionsHaveTurnOff() {
      return TradeConditionsInfo<TradeConditionTurnOffAttribute>().Any(d => d().HasNone());
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
      if(IsTrader && CanTriggerTradeDirection() && !HasTradeDirectionTriggers) {
        TradeConditionsEval().ForEach(eval => {
          var hasBuy = TradeDirection.HasUp() && eval.HasUp();
          var hasSell = TradeDirection.HasDown() && eval.HasDown();

          BuyLevel.CanTradeEx = IsTurnOnOnly && BuyLevel.CanTrade || hasBuy;
          SellLevel.CanTradeEx = IsTurnOnOnly && SellLevel.CanTrade || hasSell;

          if(BuyLevel.CanTrade && SellLevel.CanTrade && hasBuy != hasSell) {
            BuyLevel.CanTradeEx = hasBuy;
            SellLevel.CanTradeEx = hasSell;
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
      return tradeConditions.Select(tc => map(tc.Item1, tc.Item2, ParseTradeConditionName(tc.Item1.Method)));
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
