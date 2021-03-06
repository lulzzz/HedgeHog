﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HedgeHog.DB;
using HedgeHog.Bars;
using System.Diagnostics;
using Order2GoAddIn;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using System.Reactive;
using HedgeHog.Shared;
using HedgeHog.Shared.Messages;
using EntityFramework.BulkInsert.Extensions;
using System.Runtime.CompilerServices;

namespace HedgeHog.Alice.Store {
  public static class PriceHistory {
    public static void LoadBars(ITradesManager fw, string pairToLoad, Action<object> progressCallback = null) {
      var pairsToLoad = new RequestPairForHistoryMessage();
      GalaSoft.MvvmLight.Messaging.Messenger.Default.Send(pairsToLoad);
      foreach(var pair in pairsToLoad.Pairs)
        AddTicks(fw, pair.Item2, pair.Item1, DateTime.Now.AddYears(-1), progressCallback);
    }
    //[MethodImpl(MethodImplOptions.Synchronized)]
    public static void AddTicks(ITradesManager fw, int period, string pair, DateTime dateStart, Action<object> progressCallback) {
      try {
        #region callback
        ActionBlock<Action> saveTickActionBlock = new ActionBlock<Action>(a => a());
        Action<RateLoadingCallbackArgs<Rate>> showProgress = (args) => {
          SaveTickCallBack(period, pair, progressCallback, saveTickActionBlock, args);
          args.IsProcessed = true;
        };
        #endregion

        var offset = TimeSpan.FromMinutes(period);
        using(var context = new ForexEntities()) {
          if(dateStart > DateTime.MinValue) {
            var dateMin = new DateTime(context.t_Bar.Where(b => b.Pair == pair && b.Period == period).Min(b => (DateTimeOffset?)b.StartDate).GetValueOrDefault().DateTime.Ticks, DateTimeKind.Utc);
            if(dateMin.IsMin())
              dateMin = DateTime.Now;
            var dateEnd = dateMin.Subtract(offset);
            if(dateStart < dateMin)
              fw.GetBarsBase(pair, period, 0, dateStart, dateEnd, new List<Rate>(), null, showProgress);
          }
          var q = context.t_Bar.Where(b => b.Pair == pair && b.Period == period).Select(b => b.StartDate).DefaultIfEmpty().Max();
          if(dateStart == DateTime.MinValue && q == DateTimeOffset.MinValue)
            throw new Exception("dateStart must be provided there is no bars in database.");
          var p = period == 0 ? 1 / 60.0 : period;
          dateStart = q.LocalDateTime.Add(p.FromMinutes());
        }
        fw.GetBarsBase(pair, period, 0, dateStart.Max(DateTime.Now.AddYears(-1)), DateTime.Now, new List<Rate>(), null, showProgress);
      } catch(Exception exc) {
        GalaSoft.MvvmLight.Messaging.Messenger.Default.Send<LogMessage>(new LogMessage(exc));
      }
    }

    public static void SaveTickCallBack(int period, string pair, Action<object> progressCallback, ActionBlock<Action> saveTickActionBlock, RateLoadingCallbackArgs<Rate> args) {
      if(progressCallback != null)
        progressCallback(args.Message);
      else
        Debug.WriteLine("{0}", args.Message);
      var context = new ForexEntities();
      //context.Configuration.AutoDetectChangesEnabled = false;
      context.Configuration.ValidateOnSaveEnabled = false;
      Action a = () =>
        args.NewRates.Distinct().Do(t => {
          context.t_Bar.Add(FillBar(period, pair, context.t_Bar.Create(), t));
        }).TakeLast(1)
        .ForEach(_ => {
          try {
            context.SaveConcurrent();
          } catch(Exception exc) {
            progressCallback?.Invoke(exc);
          }
          context.Dispose();
          args.NewRates.Clear();
        });
      a();
      //saveTickActionBlock.Post(a);
    }
    private static t_Bar FillBar(int period, string pair, t_Bar bar, Rate t) {
      bar.Pair = pair;
      bar.Period = period;
      bar.StartDate = t.StartDate2;
      bar.AskHigh = (float)t.AskHigh;
      bar.AskLow = (float)t.AskLow;
      bar.AskOpen = (float)t.AskOpen;
      bar.AskClose = (float)t.AskClose;
      bar.BidHigh = (float)t.BidHigh;
      bar.BidLow = (float)t.BidLow;
      bar.BidOpen = (float)t.BidOpen;
      bar.BidClose = (float)t.BidClose;
      bar.Volume = t.Volume;
      bar.Row = new[] { t }.OfType<Tick>().Select(tick => tick.Row).DefaultIfEmpty().Single();
      return bar;
    }
  }
}
