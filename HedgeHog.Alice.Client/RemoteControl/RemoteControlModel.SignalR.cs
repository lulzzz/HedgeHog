﻿using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HedgeHog.Alice.Store;
using HedgeHog.Bars;
using HedgeHog.Shared;

namespace HedgeHog.Alice.Client {
  partial class RemoteControlModel {
    public static async Task<IEnumerable<T>> ReadStrategies<T>(TradingMacro tm, Func<string, string, Uri, string[], T> map) {
      var localMap = MonoidsCore.ToFunc("", "", "", (Uri)null, 0, (name, description, content, uri, index) => new { name, description, content, uri, index });
      var strategies = await Cloud.GitHub.GistStrategies(localMap);
      var activeSettings = Lib.ReadParametersToString(tm.GetActiveSettings());
      return (from strategy in strategies
              where strategy.index == 0
              let diffs = TradingMacro.ActiveSettingsDiff(strategy.content, activeSettings).ToDictionary()
              let diff = diffs.Select(kv => new { diff = kv.Key + "= " + kv.Value[0] + " {" + kv.Value[1] + "}", lev = Lib.LevenshteinDistance(kv.Value[0], kv.Value[1]) }).ToArray()
              orderby diff.Length, diff.Sum(x => x.lev), strategy.name
              select map(strategy.name, strategy.description, strategy.uri, diff.Select(x => x.diff).ToArray())
               );
      //Func<IDictionary<string,string>, IDictionary<string, string>> joinDicts = (d)
      //(from strategy in strategies
      // from content in strategy.content
      // join )
      //Func<string, string> name = s =>
      //  Regex.Matches(s, @"\{(.+)\}").Cast<Match>().SelectMany(m => m.Groups.Cast<Group>()
      //  .Skip(1).Take(1)
      //  .Select(g => g.Value))
      //  .DefaultIfEmpty(s).First();
      //return Directory.GetFiles(StrategiesPath())
      //  .OrderBy(file => file)
      //  .Select(file => map(name(Path.GetFileNameWithoutExtension(file)), Path.GetFileNameWithoutExtension(file), file));
    }

    //private static string StrategiesPath(string pathEnd = "") {
    //  return Path.Combine(Directory.GetCurrentDirectory(), "..", "Strategies", pathEnd);
    //}

    public static async Task SaveStrategy(TradingMacro tm, string nick) {
      await tm.SaveActiveSettings(nick, TradingMacro.ActiveSettingsStore.Gist);
    }
    public static async Task RemoveStrategy(string name) {
      await Cloud.GitHub.GistStrategyDeleteByName(name);
      //File.Delete(path);
    }
    public static async Task UpdateStrategy(TradingMacro tm, string nick) {
      await tm.SaveActiveSettings(nick, TradingMacro.ActiveSettingsStore.Gist);
      //File.Delete(path);
    }
    public static async Task LoadStrategy(TradingMacro tm, string strategy) {
      await tm.LoadActiveSettings(strategy, TradingMacro.ActiveSettingsStore.Gist);
    }

    public object ServeChart(int chartWidth, DateTimeOffset dateStart, DateTimeOffset dateEnd, TradingMacro tm) {
      var digits = tm.Digits();
      if(dateEnd > tm.LoadRatesStartDate2)
        dateEnd = tm.LoadRatesStartDate2;
      else
        dateEnd = dateEnd.AddMinutes(-tm.BarPeriodInt.Min(2));
      string pair = tm.Pair;
      Func<Rate, double> rateHL = rate => (rate.PriceAvg >= rate.PriceCMALast ? rate.PriceHigh : rate.PriceLow).Round(digits);
      #region map
      var lastVolt = tm.UseRates(rates => rates.BackwardsIterator().Select(tm.GetVoltage).SkipWhile(v => v.IsNaNOrZero()).FirstOrDefault()).FirstOrDefault();
      var map = MonoidsCore.ToFunc((Rate)null, rate => new {
        d = rate.StartDate2,
        c = rateHL(rate),
        v = tm.GetVoltage(rate).IfNaNOrZero(lastVolt),
        m = rate.PriceCMALast.Round(digits)
      });
      #endregion

      if(tm.RatesArray.Count == 0 || tm.IsTrader && tm.BuyLevel == null)
        return new { rates = new int[0] };

      var tmTrader = GetTradingMacros(tm.Pair).Where(t => t.IsTrader).DefaultIfEmpty(tm).Single();
      var tpsAvg = tmTrader.MacdRsdAvg;


      var ratesForChart = tm.UseRates(rates => rates.Where(r => r.StartDate2 >= dateEnd/* && !tm.GetVoltage(r).IsNaNOrZero()*/).ToList()).FirstOrDefault();
      if(ratesForChart == null)
        return new { };
      var ratesForChart2 = tm.UseRates(rates => rates.Where(r => r.StartDate2 < dateStart/* && !tm.GetVoltage(r).IsNaNOrZero()*/).ToList()).FirstOrDefault();
      if(ratesForChart2 == null)
        return new { };

      double cmaPeriod = tm.CmaPeriodByRatesCount();
      if(tm.BarPeriod == BarsPeriodType.t1) {
        Action<IList<Rate>, Rate> volts = (gr, r) => tm.SetVoltage(r, gr.Select(tm.GetVoltage).Where(v => v.IsNotNaN()).DefaultIfEmpty(0).Average());
        cmaPeriod /= tm.TicksPerSecondAverage;
        if(ratesForChart.Count > 1)
          ratesForChart = TradingMacro.GroupTicksToSeconds(ratesForChart, volts).ToList();
        if(ratesForChart2.Count > 1)
          ratesForChart2 = TradingMacro.GroupTicksToSeconds(ratesForChart2, volts).ToList();
      }
      var getRates = MonoidsCore.ToFunc((IList<Rate>)null, rates3 => rates3.Select(map).ToList());
      var tradeLevels = !tmTrader.HasBuyLevel ? new object { } : new {
        buy = tmTrader.BuyLevel.Rate.Round(digits),
        buyClose = tmTrader.BuyCloseLevel.Rate.Round(digits),
        canBuy = tmTrader.BuyLevel.CanTrade,
        manualBuy = tmTrader.BuyLevel.InManual,
        buyCount = tmTrader.BuyLevel.TradesCount,
        sell = tmTrader.SellLevel.Rate.Round(digits),
        sellClose = tmTrader.SellCloseLevel.Rate.Round(digits),
        canSell = tmTrader.SellLevel.CanTrade,
        manualSell = tmTrader.SellLevel.InManual,
        sellCount = tmTrader.SellLevel.TradesCount,
      };
      /*
      if (tm.IsAsleep) {
        var o = new object();
        var a = new object[0];
        return new {
          rates = getRates(ratesForChart),
          rates2 = getRates(ratesForChart2),
          ratesCount = tm.RatesArray.Count,
          dateStart = tm.RatesArray[0].StartDate2,
          trendLines = o,
          trendLines2 = o,
          trendLines1 = o,
          isTradingActive = tm.IsTradingActive,
          tradeLevels = o,
          trades = a,
          askBid = o,
          hasStartDate = tm.CorridorStartDate.HasValue,
          cmp = cmaPeriod,
          tpsAvg = 0,
          isTrader = tm.IsTrader,
          canBuy = false,
          canSell = false,
          waveLines = a
        };
      }
      */
      var trends = tm.TrendLines.Value.ToList();
      var trendLines = tm.UseRates(rates => new {
        dates = rates.Count > 0
        ? new DateTimeOffset[]{
          tm.BarPeriod == BarsPeriodType.m1
          ? rates.Last().StartDate2.AddMinutes(-(tm.CorridorStats.Rates.Count - 1))
          : trends[0].StartDate2,
          rates.Last().StartDate2}
        : new DateTimeOffset[0],
        close1 = trends.ToArray(t => t.Trends.PriceAvg1.Round(digits)),
        close2 = trends.ToArray(t => t.Trends.PriceAvg2.Round(digits)),
        close3 = trends.ToArray(t => t.Trends.PriceAvg3.Round(digits)),
        close21 = trends.ToArray(t => t.Trends.PriceAvg21.Round(digits)),
        close31 = trends.ToArray(t => t.Trends.PriceAvg31.Round(digits))
      })
      .SingleOrDefault();
      var ratesLastStartDate2 = tm.RatesArray.Last().StartDate2;
      var trends2 = tm.TrendLines2.Value.ToList();
      var trendLines2 = new {
        dates = trends2.Count == 0
        ? new DateTimeOffset[0]
        : new DateTimeOffset[]{
          tm.BarPeriod == BarsPeriodType.m1
          ? ratesLastStartDate2.AddMinutes(-(tm.CorridorLengthBlue-1))
          : trends2[0].StartDate2,
          ratesLastStartDate2},
        close2 = trends2.ToArray(t => t.Trends.PriceAvg2.Round(digits)),
        close3 = trends2.ToArray(t => t.Trends.PriceAvg3.Round(digits)),
      };
      var trends1 = tm.TrendLines1.Value.ToList();
      var trendLines1 = new {
        dates = trends1.Count == 0
        ? new DateTimeOffset[0]
        : new DateTimeOffset[]{
          tm.BarPeriod == BarsPeriodType.m1
          ? ratesLastStartDate2.AddMinutes(-(tm.CorridorLengthGreen-1))
          : trends1[0].StartDate2,
          ratesLastStartDate2},
        close2 = trends1.ToArray(t => t.Trends.PriceAvg2.Round(digits)),
        close3 = trends1.ToArray(t => t.Trends.PriceAvg3.Round(digits)),
      };
      var waveLines = tm.WaveRangesWithTail
        .ToArray(wr => new {
          dates = new[] { wr.StartDate, wr.EndDate },
          isept = new[] { wr.InterseptStart, wr.InterseptEnd },
          bold = wr.ElliotIndex > 0
        });
      var tmg = TradesManager;
      var trades0 = tmg.GetTrades(pair);
      Func<bool, Trade[]> getTrades = isBuy => trades0.Where(t => t.IsBuy == isBuy).ToArray();
      var trades = new ExpandoObject();
      var tradeFoo = MonoidsCore.ToFunc(false, isBuy => new { o = getTrades(isBuy).NetOpen(), t = getTrades(isBuy).Max(t => t.Time) });
      getTrades(true).Take(1).ForEach(_ => trades.Add(new { buy = tradeFoo(true) }));
      getTrades(false).Take(1).ForEach(_ => trades.Add(new { sell = tradeFoo(false) }));
      var price = tmg.GetPrice(pair);
      var askBid = new { ask = price.Ask.Round(digits), bid = price.Bid.Round(digits) };
      var ret = tm.UseRates(ratesArray => ratesArray.Take(1).ToArray()).ToArray(_ => new {
        rates = getRates(ratesForChart),
        rates2 = getRates(ratesForChart2),
        ratesCount = tm.RatesArray.Count,
        dateStart = tm.RatesArray[0].StartDate2,
        trendLines,
        trendLines2,
        trendLines1,
        isTradingActive = tm.IsTradingActive,
        tradeLevels = tradeLevels,
        trades,
        askBid,
        hasStartDate = tm.CorridorStartDate.HasValue,
        cmp = cmaPeriod,
        tpsAvg,
        isTrader = tm.IsTrader,
        canBuy = tmTrader.CanOpenTradeByDirection(true),
        canSell = tmTrader.CanOpenTradeByDirection(false),
        waveLines
      });
      return ret;
    }
  }
}