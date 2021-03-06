﻿using HedgeHog.Alice.Store;
using HedgeHog.Shared;
using Microsoft.AspNet.SignalR;
using Microsoft.Owin;
using Microsoft.Owin.Cors;
using Microsoft.Owin.FileSystems;
using Microsoft.Owin.StaticFiles;
using Owin;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Dynamic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using Westwind.Web.WebApi;
using HedgeHog.Bars;
using Microsoft.Owin.Security;
using Microsoft.AspNet.SignalR.Hubs;
using static HedgeHog.Core.JsonExtensions;
using TM_HEDGE_OLD = System.Nullable<(HedgeHog.Alice.Store.TradingMacro tm, string Pair, double HV, double HVP, double TradeRatio, double TradeAmount, double MMR, int Lot, double Pip, int Corr, bool IsBuy)>;
using TM_HEDGE = System.Nullable<(HedgeHog.Alice.Store.TradingMacro tm, string Pair, double HV, double HVP
  , (double All, double Up, double Down) TradeRatio
  , (double All, double Up, double Down) TradeRatioM1
  , (double All, double Up, double Down) TradeAmount
  , double MMR
  , (int All, int Up, int Down) Lot
  , (double All, double Up, double Down) Pip
  , bool IsBuy, bool IsPrime, double HVPR, double HVPM1R)>;
using IBApp;
using System.Diagnostics;
using IBApi;
using HedgeHog.DateTimeZone;

namespace HedgeHog.Alice.Client {
  public class StartUp {
    List<string> Pairs = new List<string>() { "usdjpy", "eurusd" };
    double IntOrDouble(double d, double max = 10) {
      return d.Abs() > max ? d.ToInt() : d.Round(1);
    }
    public void Configuration(IAppBuilder app) {
      Func<HttpListener> httpListener = () => (HttpListener)app.Properties["System.Net.HttpListener"];
      GlobalHost.Configuration.DefaultMessageBufferSize = 1;
      //GlobalHost.HubPipeline.AddModule(new MyHubPipelineModule());

      // Static content
      var fileSystem = new PhysicalFileSystem("./www");
      var fsOptions = new FileServerOptions {
        EnableDirectoryBrowsing = true,
        FileSystem = fileSystem
      };
      app.UseFileServer(fsOptions);

      // SignalR timer
      Func<IHubContext> myHub = () => GlobalHost.ConnectionManager.GetHubContext<MyHub>();
      var remoteControl = App.container.GetExport<RemoteControlModel>();
      IDisposable priceChanged = null;
      IDisposable tradesChanged = null;
      IDisposable lasrWwwErrorChanged = null;
      {
        var trader = App.container.GetExportedValue<TraderModel>();

        NewThreadScheduler.Default.Schedule(TimeSpan.FromSeconds(1), () => {
          priceChanged = trader.PriceChanged
          .Where(p => !Contract.FromCache(p.EventArgs.Price.Pair).Any(c => c.IsOption || c.IsCombo))
            .Select(x => x.EventArgs.Price.Pair.Replace("/", "").ToLower())
            //.Where(pair => Pairs.Contains(pair))
            .Subscribe(pair => {
              try {
                myHub().Clients.All.priceChanged(pair);
              } catch(Exception exc) {
                GalaSoft.MvvmLight.Messaging.Messenger.Default.Send<LogMessage>(new LogMessage(exc));
              }
            });
          tradesChanged =
            trader.TradeAdded.Select(x => x.EventArgs.Trade.Pair.Replace("/", "").ToLower())
            .Merge(
            trader.TradeRemoved.Select(x => TradesManagerStatic.WrapPair(x.EventArgs.MasterTrade.Pair)))
            .Delay(TimeSpan.FromSeconds(1))
            .Subscribe(pair => {
              try {
                myHub().Clients.All.tradesChanged(pair);
              } catch(Exception exc) {
                GalaSoft.MvvmLight.Messaging.Messenger.Default.Send<LogMessage>(new LogMessage(exc));
              }
            });
          lasrWwwErrorChanged = remoteControl.Value.ReplayArguments.LastWwwErrorObservable
          .Where(le => !string.IsNullOrWhiteSpace(le))
          .Subscribe(le => myHub().Clients.All.lastWwwErrorChanged(le));
        });
      }
      app.UseCors(CorsOptions.AllowAll);
      Action<RemoteControlModel> setAuthScheme = rc => {
        if(!rc.IsInVirtualTrading) {
          httpListener().AuthenticationSchemes = AuthenticationSchemes.IntegratedWindowsAuthentication;
        }
      };
      setAuthScheme?.Invoke(remoteControl.Value);
      app.Use((context, next) => {
        try {
          if(context == null)
            App.SetSignalRSubjectSubject(() => {
              //var tm = remoteControl.Value.TradingMacrosCopy.FirstOrDefault(t => t.PairPlain == Pair);
              try {
                GlobalHost.ConnectionManager.GetHubContext<MyHub>()
                  .Clients.All.addMessage();
              } catch(Exception exc) {
                GalaSoft.MvvmLight.Messaging.Messenger.Default.Send<LogMessage>(new LogMessage(exc));
              }
            });

          if(context.Request.Path.Value.ToLower().StartsWith("/hello")) {
            var key = context.Request.Path.Value.ToLower().Split('/').Last();
            var scheme = BasicAuthenticationFilter.AuthenticationSchemes = key == "hello"
            ? AuthenticationSchemes.IntegratedWindowsAuthentication
            : key == "off"
            ? AuthenticationSchemes.Anonymous
            : AuthenticationSchemes.Basic;
            httpListener().AuthenticationSchemes = scheme;
            return context.Response.WriteAsync(new { httpListener().AuthenticationSchemes } + "");
          }
          if(context.Request.Path.Value.ToLower().StartsWith("/logon")) {
            if(context.Authentication.User != null) {
              context.Response.Redirect("/who");
              return context.Response.WriteAsync(UserToString(httpListener, context.Authentication.User));
            }
            if(httpListener().AuthenticationSchemes == AuthenticationSchemes.Anonymous)
              return context.Response.WriteAsync(new { AuthenticationSchemes.Anonymous } + "");
            var host = context.Request.Uri.DnsSafeHost;
            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            context.Response.Headers.Add("WWW-Authenticate", new[] { string.Format("Basic realm=\"{0}\"", host) });
            return Task.FromResult("");
          }
          if(context.Request.Path.Value.ToLower().StartsWith("/logoff")) {
            if(httpListener().AuthenticationSchemes == AuthenticationSchemes.Anonymous) {
              context.Response.Redirect("/who");
              return context.Response.WriteAsync(new { AuthenticationSchemes.Anonymous } + "");
            }
            var host = context.Request.Uri.DnsSafeHost;
            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            context.Response.Headers.Add("WWW-Authenticate", new[] { string.Format("Basic realm=\"{0}\"", host) });
            return Task.FromResult("");
          }
          if(context.Request.Path.Value.ToLower().StartsWith("/who")) {
            var user = context.Authentication.User;
            return context.Response.WriteAsync(user == null
              ? "Anonymous"
              : UserToString(httpListener, user));
          }
          var path = context.Request.Path.Value.ToLower().Substring(1);
          var rc = remoteControl.Value;
          {
            var tm = rc.TradingMacrosCopy.FirstOrDefault(t => t.PairPlain == path);
            if(tm != null) {
              context.Response.ContentType = "image/png";
              return context.Response.WriteAsync(rc.GetCharter(tm).GetPng());
            }
          }
          {
            var tm = rc.TradingMacrosCopy.Skip(1).FirstOrDefault(t => t.PairPlain == path.Replace("2", ""));
            if(tm != null) {
              context.Response.ContentType = "image/png";
              return context.Response.WriteAsync(rc.GetCharter(tm).GetPng());
            }
          }
        } catch(Exception exc) {
          GalaSoft.MvvmLight.Messaging.Messenger.Default.Send<LogMessage>(new LogMessage(exc));
        }
        return next();
      });
      app.Map("/login2", map => {
        map.Run(ctx => {
          if(ctx.Authentication.User == null ||
          !ctx.Authentication.User.Identity.IsAuthenticated) {
            var authenticationProperties = new AuthenticationProperties();
            ctx.Authentication.Challenge(authenticationProperties, new[] { AuthenticationSchemes.Negotiate + "" });
          }
          return Task.FromResult("");
        });
      });
      //app.Use((context, next) => {
      //  return new GZipMiddleware(d => next()).Invoke(context.Environment);
      //});
      // SignalR
      try {
        GlobalHost.HubPipeline.AddModule(new MyHubPipelineModule());
        var hubConfiguration = new HubConfiguration();
        hubConfiguration.EnableDetailedErrors = true;
        app.MapSignalR(hubConfiguration);
      } catch(InvalidOperationException exc) {
        if(!exc.Message.StartsWith("Counter"))
          throw;
      }
    }

    private static string UserToString(Func<HttpListener> httpListener, System.Security.Claims.ClaimsPrincipal user) {
      return new { User = user.Identity.Name, user.Identity.AuthenticationType, httpListener().AuthenticationSchemes, IsTrader = user.IsInRole("Traders") }.ToJson();
    }
  }

  public class MyHubPipelineModule :HubPipelineModule {
    protected override void OnIncomingError(ExceptionContext exceptionContext, IHubIncomingInvokerContext invokerContext) {
      MethodDescriptor method = invokerContext.MethodDescriptor;
      var args = string.Join(", ", invokerContext.Args);
      var log = $"{method.Hub.Name}.{method.Name}({args}) threw the following uncaught exception: {exceptionContext.Error}";
      GalaSoft.MvvmLight.Messaging.Messenger.Default.Send(new LogMessage(log));

      base.OnIncomingError(exceptionContext, invokerContext);
    }
  }

  [BasicAuthenticationFilter]
  public class MyHub :Hub {
    class SendChartBuffer :AsyncBuffer<SendChartBuffer, Action> {
      protected override Action PushImpl(Action action) {
        return action;
      }
    }

    private const string ALL_COMBOS = "ALL COMBOS";
    Lazy<RemoteControlModel> remoteControl;
    Lazy<TraderModel> trader;
    static Exception Log { set { GalaSoft.MvvmLight.Messaging.Messenger.Default.Send<LogMessage>(new LogMessage(value)); } }
    static ISubject<Action> _AskRatesSubject;
    static ISubject<Action> AskRatesSubject { get { return _AskRatesSubject; } }
    static ISubject<Action> _AskRates2Subject;
    static ISubject<Action> AskRates2Subject { get { return _AskRates2Subject; } }
    static ISubject<Action> _combosSubject;
    static ISubject<Action> CombosSubject { get { return _combosSubject; } }
    static IDisposable CombosSubscribtion;
    //http://forex.timezoneconverter.com/?timezone=GMT&refresh=5
    static Dictionary<string, DateTimeOffset> _marketHours = new Dictionary<string, DateTimeOffset> {
    { "Frankfurt", DateTimeOffset.Parse("6:00 +0:00") } ,
    { "London", DateTimeOffset.Parse("07:00 +0:00") } ,
    { "New York", DateTimeOffset.Parse("12:00 +0:00") } ,
    { "Sydney", DateTimeOffset.Parse("22:00 +0:00") } ,
    { "Tokyo", DateTimeOffset.Parse("23:00 +0:00") }
    };
    static List<string> _marketHoursSet = new List<string>();
    static ISubject<Action> _CurrentCombos;
    static MyHub() {
      _AskRatesSubject = new Subject<Action>();
      _AskRatesSubject.InitBufferedObservable<Action>(exc => Log = exc);
      _AskRatesSubject.Subscribe(a => a());
      _AskRates2Subject = new Subject<Action>();
      _AskRates2Subject.InitBufferedObservable<Action>(exc => Log = exc);
      _AskRates2Subject.Subscribe(a => a());
      _CurrentCombos = new Subject<Action>();
      _CurrentCombos
        .ObserveOn(TaskPoolScheduler.Default)
        .Sample(TimeSpan.FromSeconds(0.5))
        .Subscribe(s => s(), exc => { });
    }
    static public void OnCurrentCombo(Action p) {
      _CurrentCombos.OnNext(p);
    }
    private AccountManager GetAccountManager() => ((IBWraper)trader?.Value?.TradesManager).AccountManager;
    public MyHub() {
      try {
        remoteControl = App.container.GetExport<RemoteControlModel>();
        trader = App.container.GetExport<TraderModel>();
        App.WwwMessageWarning.Do(wm => {
          Clients.All.message(wm);
        });
        App.WwwMessageWarning.Clear();
      } catch(ObjectDisposedException) { }
    }

    static DateTime _newsReadLastDate = DateTime.MinValue;
    static List<DateTimeOffset> _newsDates = new List<DateTimeOffset>();
    public object[] AskChangedPrice(string pair) {
      return (from tm0 in UseTradingMacro2(pair, 0, tm => tm)
              from tm1 in UseTradingMacro2(pair, 1, tm => tm)
              from tmTrader in tm0.TradingMacroTrader()
              from tmTrender in tm0.TradingMacroTrender().TakeLast(1)
              select new { tm0, tm1, tmTrader, tmTrender }
      ).Select(t => {
        var isVertual = t.tmTrader.IsInVirtualTrading;

        #region marketHours
        if(!t.tmTrader.IsInVirtualTrading)
          _marketHours
            .Where(mh => !_marketHoursSet.Contains(mh.Key))
            .Where(mh => (t.tmTrader.ServerTime.ToUniversalTime().TimeOfDay - mh.Value.TimeOfDay).TotalMinutes.Between(-15, 0))
            .Do(mh => Clients.All.marketIsOpening(new { mh.Key, mh.Value }))
            .ForEach(mh => _marketHoursSet.Add(mh.Key));
        _marketHours
          .Where(mh => (t.tmTrader.ServerTime.ToUniversalTime().TimeOfDay - mh.Value.TimeOfDay).TotalMinutes > 1)
          .ForEach(mh => _marketHoursSet.Remove(mh.Key));
        #endregion

        #region News
        var doNews = false;
        if(doNews && t.tmTrader.DoNews && DateTime.Now.Subtract(_newsReadLastDate).TotalMinutes > 1) {
          _newsReadLastDate = DateTime.Now;
          var now = DateTimeOffset.UtcNow;
          var then = now.AddMinutes(60);
          var news = ReadNewsFromDb()
            .Where(n => !_newsDates.Contains(n.Time))
            .Where(n => n.Time > now && n.Time < then)
            .ToArray();
          if(news.Any()) {
            _newsDates.AddRange(news.Select(n => n.Time));
            Clients.All.newsIsComming(news.Select(n => new { n.Time, n.Name }).ToArray());
          }
        }
        #endregion
        var timeFormat = (isVertual ? "MMM/d " : "") + "HH:mm:ss";
        var digits = t.tmTrader.Digits();
        Func<TradingMacro, string> timeFrame = tm => tm.BarPeriod > Bars.BarsPeriodType.t1
        ? (tm.RatesArray.Count * tm.BarPeriodInt).FromMinutes().TotalDays.Round(1) + ""
        : TimeSpan.FromMinutes(t.tm0.RatesDuration).ToString(@"h\:mm");
        var cgp = t.tmTrader.CurrentGrossInPips;
        var otgp = t.tmTrader.OpenTradesGross2InPips;
        var ht = trader.Value?.TradesManager.GetTrades().Any();
        return (object)new {
          time = t.tm0.ServerTime.ToString(timeFormat),
          prf = IntOrDouble(t.tmTrader.CurrentGrossInPipTotal, 3),
          otg = IntOrDouble(t.tmTrader.OpenTradesGross2InPips, 3),
          tps = t.tm0.TicksPerSecondAverage.Round(1),
          dur = timeFrame(t.tmTrader)
            + (t.tm1 != null ? "," + timeFrame(t.tm1) : ""),// + "|" + TimeSpan.FromMinutes(tm1.RatesDuration).ToString(@"h\:mm"),
          hgt = string.Join("/", new[] {
          t.tmTrender.RatesHeightInPips.ToInt()+"",
          t.tmTrader.BuySellHeightInPips.ToInt()+"",
          t.tmTrender.TLBlue.Angle.Abs().Round(1)+"°"
        }),
          rsdMin = t.tm0.RatesStDevMinInPips,
          rsdMin2 = t.tm1 == null ? 0 : t.tm1.RatesStDevMinInPips,
          S = remoteControl.Value.MasterModel.AccountModel.Equity.Round(0),
          price = t.tmTrader.CurrentPrice.YieldNotNull().Select(cp => new { ask = cp.Ask, bid = cp.Bid }).DefaultIfEmpty(new object()).Single(),
          tci = GetTradeConditionsInfo(t.tmTrader),
          wp = t.tmTrader.WaveHeightPower.Round(1),
          ip = remoteControl.Value.ReplayArguments.InPause ? 1 : 0,
          com = new { b = t.tmTrader.CenterOfMassBuy.Round(digits), s = t.tmTrader.CenterOfMassSell.Round(digits), dates = t.tmTrader.CenterOfMassDates ?? new DateTime[0] },
          com2 = new { b = t.tmTrader.CenterOfMassBuy2.Round(digits), s = t.tmTrader.CenterOfMassSell2.Round(digits), dates = t.tmTrader.CenterOfMass2Dates ?? new DateTime[0] },
          com3 = new { b = t.tmTrader.CenterOfMassBuy3.Round(digits), s = t.tmTrader.CenterOfMassSell3.Round(digits), dates = t.tmTrader.CenterOfMass3Dates ?? new DateTime[0] },
          com4 = new { b = t.tmTrader.CenterOfMassBuy4.Round(digits), s = t.tmTrader.CenterOfMassSell4.Round(digits), dates = t.tmTrader.CenterOfMass4Dates ?? new DateTime[0] },
          bth = t.tmTrader.BeforeHours.Select(tr => new { tr.upDown, tr.dates }).ToArray(),
          //afh2 = new[] { new { dates = t.tmTrader.ServerTime.Date.AddDays(-1).AddHours(16).With(d => new[] { d, d.AddHours(4) }) } },
          afh = GetBackDates(t.tmTrader.ServerTime.Date, 6)
            .Select(date => new { dates = date.AddHours(16).With(d => new[] { d, d.AddHours(4) }) })
            .ToArray(),
          tpls = t.tmTrader.GetTradeLevelsPreset().Select(e => e + "").ToArray(),
          tts = HasMinMaxTradeLevels(t.tmTrader) ? t.tmTrender.TradeTrends : "",
          tti = GetTradeTrendIndexImpl(t.tmTrader, t.tmTrender),
          ht
          //closed = trader.Value.ClosedTrades.OrderByDescending(t=>t.TimeClose).Take(3).Select(t => new { })
        };
      })
      .ToArray();
      DateTime[] GetBackDates(DateTime start, int daysBack) =>
        Enumerable.Range(0, 1000)
          .Select(d => start.AddDays(-d))
          .Where(d => !new[] { DayOfWeek.Saturday, DayOfWeek.Sunday }.Contains(d.DayOfWeek))
          .Take(daysBack)
          .ToArray();
    }

    private static bool HasMinMaxTradeLevels(TradingMacro tmTrader) {
      return (from tl in new[] { TradeLevelBy.PriceMax, TradeLevelBy.PriceMin }
              join bs in new[] { tmTrader.LevelBuyBy, tmTrader.LevelSellBy } on tl equals bs
              select bs).Count() == 2;
    }

    public bool IsInVirtual() {
      return remoteControl.Value.IsInVirtualTrading;
    }
    [BasicAuthenticationFilter]
    public bool TogglePause(string pair, int chartNum) {
      return UseTradingMacro2(pair, chartNum, tm => remoteControl.Value.IsInVirtualTrading
       ? remoteControl.Value.ToggleReplayPause()
       : tm.ToggleIsActive()
      ).DefaultIfEmpty().First();
    }
    public string ReadTitleRoot() { return trader.Value.TitleRoot; }

    #region TradeDirectionTriggers
    public string[] ReadTradeDirectionTriggers(string pair) {
      return UseTradingMacro2(pair, 0, tm => tm.TradeDirectionTriggersAllInfo((tc, name) => name)).Concat().ToArray();
    }
    public string[] GetTradeDirectionTriggers(string pair) {
      return UseTradingMacro2(pair, 0, tm => tm.TradeDirectionTriggersInfo((tc, name) => name)).Concat().ToArray();
    }
    [BasicAuthenticationFilter]
    public void SetTradeDirectionTriggers(string pair, string[] names) {
      UseTradingMacro(pair, tm => { tm.TradeDirectionTriggersSet(names); });
    }
    #endregion

    #region MMRs
    public void GetMMRs() {
      trader.Value.TradesManager.FetchMMRs();
    }
    public object[] LoadOffers() {
      return TraderModel.LoadOffers().Select(o => new { pair = o.Pair, mmrBuy = o.MMRLong.Round(3), mmrSell = o.MMRShort.Round(3) }).ToArray();
    }
    public object[] ReadOffers() {
      var equity = remoteControl.Value.MasterModel.AccountModel.Equity;
      return TradesManagerStatic.dbOffers.Select(o => new {
        pair = o.Pair,
        mmrBuy = o.MMRLong.Round(3),
        mmrSell = o.MMRShort.Round(3),
        pipBuy = TradeStats(o.Pair, true).SingleOrDefault(),
        pipSel = TradeStats(o.Pair, false).SingleOrDefault()
      }).ToArray();

      IEnumerable<string> TradeStats(string pair, bool isBuy) =>
        UseTraderMacro(pair, tm =>
        (tm, lot: tm.GetLotsToTrade(equity, TradesManagerStatic.GetMMR(tm.Pair, isBuy), 1)))
        .Select(t => (
          t.lot,
          t.tm,
          pip: TradesManagerStatic.PipAmount(t.tm.Pair, t.lot, (t.tm.CurrentPrice?.Average).GetValueOrDefault(), TradesManagerStatic.GetPointSize(t.tm.Pair))))
        .Select(t => (t.pip, h: t.tm.RatesHeightInPips * t.pip, hv: t.tm.HistoricalVolatility().SingleOrDefault()))
        .Select(t => $"{t.hv.AutoRound2(3)},{t.pip.AutoRound2("$", 3)},{t.h.ToString("c0")}");

    }
    public void UpdateMMRs(string pair, double mmrLong, double mmrShort) {
      pair = pair.ToUpper();
      GlobalStorage.UseForexMongo(c => c.Offer.Where(o => o.Pair == pair).ForEach(o => {
        o.MMRLong = mmrLong;
        o.MMRShort = mmrShort;
      }), true);
    }
    public void SaveOffers() {
      GlobalStorage.UseForexMongo(c =>
      (from o in c.Offer
       join dbo in TradesManagerStatic.dbOffers on o.Pair.ToLower() equals dbo.Pair.ToLower()
       select new { o, dbo }
      ).ForEach(x => {
        x.o.MMRLong = x.dbo.MMRLong;
        x.o.MMRShort = x.dbo.MMRShort;
      }), true);

    }

    public object ReadHedgingRatios(string pair) {
      try {
        //var xx = new[] { true, false }.SelectMany(isBuy => CalcHedgedPositions(pair, isBuy));
        var hbss = new[] { true, false }.SelectMany(isBuy => HedgeBuySell(pair, isBuy)).ToArray();
        var htms = GetHedgedTradingMacros(pair).ToArray();
        var canShort = htms.SelectMany(t => new[] { t.tm1, t.tm2 }).Select(tm => new { tm.Pair, IsShortable = tm.CurrentPrice?.IsShortable == true }).ToArray();
        var stats = (
          from tms in htms.Take(0)
          from corr in tms.tm1.TMCorrelation(tms.tm2)
          from slope1M1 in tms.tm1.TradingMacroM1(tm => tm.RatesArrayCoeffs.YieldIf(ra => ra.Any(), ra => ra.LineSlope() * 1000)).Concat()
          from slope2M1 in tms.tm2.TradingMacroM1(tm => tm.RatesArrayCoeffs.YieldIf(ra => ra.Any(), ra => corr * ra.LineSlope() * 1000)).Concat()
          where tms.tm1.RatesArrayCoeffs.Length == 2
          let slope1 = tms.tm1.RatesArrayCoeffs.LineSlope() * 1000
          let slope2 = corr * tms.tm2.RatesArrayCoeffs.LineSlope() * 1000
          select new {
            slopeRatio = (slope1 / slope2).AutoRound2(2),
            slope1 = slope1.AutoRound2(3),
            slope2 = slope2.AutoRound2(3),
            slpRatioM1 = (slope1M1 / slope2M1).AutoRound2(2),
            slope1M1 = slope1M1.AutoRound2(3),
            slope2M1 = slope2M1.AutoRound2(3)
          }).ToArray();
        return new {
          hrs = hbss
          .Select(x => x.Value)
          .Select(t => new {
            t.Pair,
            HV = t.HV.AutoRound2(3),
            HVP = t.HVP.AutoRound2(3),
            TradeRatio = t.TradeRatio.All,
            TradeRatio2 = t.TradeRatio.HedgedTriplet(t.IsBuy),
            TradeRatioM1 = t.TradeRatioM1.All,
            TradeRatioM12 = t.TradeRatioM1.HedgedTriplet(t.IsBuy),
            TradeAmount = t.TradeAmount.All.ToInt(),
            TradeAmount2 = t.TradeAmount.HedgedTradeAmount(t.IsBuy).ToInt(),
            MMR = t.MMR.AutoRound2(3),
            Lot = t.Lot.All,
            Lot2 = t.Lot.HedgedLot(t.IsBuy),
            //t.Corr,
            Pip = t.Pip.All.AutoRound2(2),
            Pip2 = t.Pip.HedgedPip(t.IsBuy).AutoRound2(2),
            t.IsBuy,
            IsPrime = (t.IsBuy || canShort.Any(cs => cs.Pair == t.Pair && cs.IsShortable)) && t.IsPrime,
            t.HVPR,
            t.HVPM1R
          }).ToArray(),
          stats,
          htvs = ReadHedgeVirtual(pair)
        };

      } catch(Exception exc) {
        GalaSoft.MvvmLight.Messaging.Messenger.Default.Send(new LogMessage(exc));
        return new { hrs = new object[0], stats = new object[0] };
      }
    }

    IEnumerable<TM_HEDGE> HedgeBuySell(string pair, bool isBuy) {
      return from hbs in UseTraderMacro(pair, tm => tm.HedgeBuySell(isBuy))
             from bs in hbs
             select bs;
    }
    IEnumerable<(TradingMacro tm1, TradingMacro tm2)> GetHedgedTradingMacros(string pair) {
      return from tm1 in UseTraderMacro(pair, tm => tm)
             from tm2 in tm1.TradingMacroHedged()
             select (tm1, tm2);
    }

    //static List<IList<Trade>> HedgeTradesVirtual = new List<IList<Trade>>();
    public void OpenHedgeVirtual(string pair, bool isBuy, DateTime time) {
      if(time.Kind == DateTimeKind.Unspecified)
        time = DateTime.SpecifyKind(time, DateTimeKind.Local);
      if(time.TimeOfDay == TimeSpan.Zero)
        time = trader.Value.TradesManager.ServerTime.AddSeconds(-1);
      var trm = trader.Value.TradesManager;
      TradingMacro.HedgeTradesVirtual.Add(HedgeBuySell(pair, isBuy)
       .Select(x => x.Value)
       .OrderByDescending(tm => tm.Pair == pair)
       .ToArray()
       .Select(t => {
         var rate = t.tm.FindRateByDate(time).Concat(t.tm.TradingMacroM1(tm => tm.FindRateByDate(time)).Concat()).First();
         var trade = Trade.Create(null, t.Pair, trm.GetPipSize(t.Pair), trm.GetBaseUnitSize(t.Pair), trader.Value.CommissionByTrade);
         trade.Buy = t.IsBuy;
         trade.IsBuy = t.IsBuy;
         trade.Time2 = rate.StartDate;
         trade.Time2Close = t.tm.ServerTime;
         trade.Open = t.IsBuy ? rate.AskHigh : rate.BidLow;
         trade.Close = t.IsBuy ? t.tm.CurrentPrice.Bid : t.tm.CurrentPrice.Ask;
         trade.Lots = t.tm.GetLotsToTrade(t.TradeAmount.HedgedTradeAmount(t.IsBuy), 1, 1);
         return trade;
       }).ToArray());
    }

    public object[] ReadHedgeVirtual(string pair) {
      var tradeInfo = MonoidsCore.ToFunc((Trade trade, double netPL2, double netSum) => new {
        trade.Pair,
        Pos = (trade.IsBuy ? 1 : -1) * trade.Lots,
        Net = netPL2.AutoRound2(1),
        Balance = netSum.ToInt(),
        Open = trade.Open.Round(TradesManagerStatic.GetDigits(trade.Pair)),
        Time = trade.Time.ToString("dd HH:mm:ss")
      });
      var tmh = (from t in GetHedgedTradingMacros(pair)
                 join hts in TradingMacro.HedgeTradesVirtual on new { pair1 = t.tm1.Pair, pair2 = t.tm2.Pair } equals new { pair1 = hts[0].Pair, pair2 = hts[1].Pair }
                 from ht in hts
                 from p in cp(ht).DefaultIfEmpty(double.NaN)
                 select tradeInfo(ht, ht.CalcNetPL2(p), hts.SelectMany(t => cp(t), (t, hp) => t.CalcNetPL2(hp)).Sum())
                ).ToArray();
      return tmh;

      double[] cp(Trade t) => trader.Value.TradesManager.TryGetPrice(t.Pair).Select(p => t.IsBuy ? p.Bid : p.Ask).ToArray();
    }
    public void ClearHedgeVirtualTrades(string pair) {
      TradingMacro.HedgeTradesVirtual.Clear();
    }
    public Trade[] GetHedgeVirtualTrades(string pair) {
      return (from t in GetHedgedTradingMacros(pair)
              join hts in TradingMacro.HedgeTradesVirtual on new { pair1 = t.tm1.Pair, pair2 = t.tm2.Pair } equals new { pair1 = hts[0].Pair, pair2 = hts[1].Pair }
              from ht in hts
              select (ht)
                ).ToArray();
    }
    [BasicAuthenticationFilter]
    public void OpenHedge(string pair, bool isBuy) => UseTraderMacro(pair, tm => tm.OpenHedgedTrades(isBuy, false, $"WWW {nameof(OpenHedge)}"));

    static double DaysTillExpiration2(DateTime expiration) => (expiration.InNewYork().AddHours(16) - DateTime.Now.InNewYork()).TotalDays.Max(1);
    static double DaysTillExpiration(DateTime expiration) => (expiration - DateTime.Now.Date).TotalDays + 1;
    public object[] ReadStraddles(string pair, int gap, int numOfCombos, int quantity, double? strikeLevel, int expDaysSkip, string optionTypeMap, string[] comboExits) =>
      UseTraderMacro(pair, tm => {
        int expirationDaysSkip = TradesManagerStatic.ExpirationDaysSkip(expDaysSkip);
        var am = GetAccountManager();
        string CacheKey(Contract c) => c.IsFuture ? c.LocalSymbol : c.Symbol;
        var underContracts = IBApi.Contract.FromCache(pair).ToArray();
        if(am != null) {
          Action straddles = () =>
            underContracts
              .ForEach(underContract => {
                am.CurrentStraddles(CacheKey(underContract), strikeLevel.GetValueOrDefault(double.NaN), expirationDaysSkip, numOfCombos, gap)
                .Select(ts => {
                  var cs = ts.Select(t => {
                    var d = DaysTillExpiration(t.combo.contract.Expiration);
                    return new {
                      i = t.instrument,
                      d = (t.combo.contract.IsFutureOption ? d.Round(1) + " " : ""),
                      t.bid,
                      t.ask,
                      avg = t.ask.Avg(t.bid),
                      time = t.time.ToString("HH:mm:ss"),
                      delta = t.deltaBid,
                      strike = t.strikeAvg,
                      perc = (t.bid / t.underPrice * 100),
                      strikeDelta = t.strikeAvg - t.underPrice,
                      be = new { t.breakEven.up, t.breakEven.dn },
                      isActive = false,
                      maxPlPerc = t.bid * quantity * t.combo.contract.ComboMultiplier / am.Account.Equity * 100 / d,
                      maxPL = t.bid * quantity * t.combo.contract.ComboMultiplier,
                      underPL = 0
                    };
                  });
                  cs = strikeLevel.HasValue && true
                  ? cs.OrderByDescending(x => x.strikeDelta)
                  : cs.OrderByDescending(x => x.delta);
                  return cs.Take(numOfCombos).OrderByDescending(x => x.strike).ToArray();
                })
                .Subscribe(b => base.Clients.Caller.butterflies(b));
              });
          const int NUB_OF_OPTIONS = 6;
          Action<string> currentOptions = (map) =>
            underContracts
              .ForEach(underContract
              => am.CurrentOptions(CacheKey(underContract), strikeLevel.GetValueOrDefault(double.NaN), expirationDaysSkip, (numOfCombos * 1.5).Ceiling()
              , c => map.Contains(c.Right))
              .Select(ts => {
                var underPosition = am.Positions.Where(p => p.contract.Instrument == underContract.Instrument).ToList().FirstOrDefault();
                var exp = ts.Select(t => new { dte = DaysTillExpiration(t.option.Expiration).Round(1), exp = t.option.LastTradeDateOrContractMonth }).Take(1).ToArray();
                var options = ts
                 .Select(t => {
                   var maxPL = t.deltaBid * quantity * t.option.ComboMultiplier;
                   var strikeDelta = t.strikeAvg - t.underPrice;
                   var underPL = (t.strikeAvg - underPosition.price) * quantity * underContract.ComboMultiplier + maxPL;
                   return new {
                     i = t.instrument,
                     t.bid,
                     t.ask,
                     avg = t.ask.Avg(t.bid),
                     time = t.time.ToString("HH:mm:ss"),
                     delta = t.deltaBid,
                     strike = t.strikeAvg,
                     perc = (t.ask.Avg(t.bid) / t.underPrice * 100),
                     strikeDelta,
                     be = new { t.breakEven.up, t.breakEven.dn },
                     isActive = false,
                     cp = t.option.Right,
                     maxPlPerc = t.deltaBid * quantity * t.option.ComboMultiplier / am.Account.Equity * 100 / exp[0].dte,
                     maxPL,
                     underPL
                   };
                 }).OrderBy(t => t.strikeDelta.Abs());

                var puts = options.Where(t => t.cp == "P" && t.strikeDelta < 5);
                var calls = options.Where(t => t.cp == "C" && t.strikeDelta > -5);
                return (exp, b: calls.OrderByDescending(x => x.strike).Concat(puts.OrderByDescending(x => x.strike)).ToArray());
              }
              //.ThenBy(t => t.i)
              )
             .Subscribe(t => {
               base.Clients.Caller.bullPuts(t.b);
               base.Clients.Caller.stockOptionsInfo(t.exp);
             }));
          Action currentBullPut = () =>
            underContracts
              .ForEach(symbol
              => am.CurrentBullPuts(CacheKey(symbol), strikeLevel.GetValueOrDefault(double.NaN), expirationDaysSkip, 3, gap)
              .Select(ts => ts
                .Select(t => new {
                  i = t.instrument,
                  t.bid,
                  t.ask,
                  avg = t.ask.Avg(t.bid),
                  time = t.time.ToString("HH:mm:ss"),
                  t.delta,
                  strike = t.strikeAvg,
                  perc = (t.ask.Avg(t.bid) / t.underPrice * 100),
                  strikeDelta = t.strikeAvg - t.underPrice,
                  be = new { t.breakEven.up, t.breakEven.dn },
                  isActive = false,
                  maxPlPerc = t.bid * quantity * t.combo.contract.ComboMultiplier / am.Account.Equity * 100
                  / DaysTillExpiration(t.combo.contract.Expiration),
                  maxPL = t.bid * quantity * t.combo.contract.ComboMultiplier
                })
              .OrderByDescending(t => t.delta)
              //.ThenBy(t => t.i)
              )
             .Subscribe(b => base.Clients.Caller.options(b)));

          Action openOrders = () =>
          am.UseOrderContracts(orderContracts =>
          (from oc in orderContracts
           where oc.isSubmitted
           select new { i = oc.contract.Instrument + " " + oc.contract.LastTradeDateOrContractMonth, id = oc.order.OrderId, f = oc.status.filled, r = oc.status.remaining, p = oc.order.LmtPrice }
          ).ToArray()
          ).ForEach(b => base.Clients.Caller.openOrders(b));

          if(!pair.IsNullOrWhiteSpace())
            OnCurrentCombo(() => {
              if(optionTypeMap.Contains("S"))
                straddles();
              currentOptions(optionTypeMap);
              openOrders();
              //currentBullPut();
            });
          //base.Clients.Caller.liveCombos(am.TradeStraddles().ToArray(x => new { combo = x.straddle, x.netPL, x.position }));
          am.ComboTrades(1)
            .ToArray()
            .Subscribe(cts => {
              try {
                var combos = cts
                .Where(ct => ct.position != 0)
                .OrderBy(ct => ct.contract.Legs().Count())
                .ThenBy(ct => ct.contract.IsOption)
                .ThenByDescending(ct => ct.contract.LastTradeDateOrContractMonth)
                .ToArray(x => {
                  var hasStrike = x.contract.IsOption || x.contract.IsCombo;
                  var delta = hasStrike ? x.strikeAvg - x.underPrice : x.closePrice.Abs() - x.openPrice.Abs();
                  return new {
                    combo = x.contract.Key
                      , netPL = x.pl
                      , x.position
                      , x.closePrice
                      , x.price.bid, x.price.ask
                      , x.close
                      , delta
                      , x.openPrice
                      , x.takeProfit, x.profit, x.orderId
                      , exit = 0, exitDelta = 0
                      , pmc = x.pmc.ToInt()
                      , color = !hasStrike ? "white"
                      : delta > 0 && x.contract.IsPut
                      ? "#ffd3d9"
                      : delta < 0 && x.contract.IsCall
                      ? "#ffd3d9"
                      : "chartreuse"
                  };
                });

                base.Clients.Caller.liveCombos(combos);

                (from ce in comboExits.Where(s => !s.IsNullOrWhiteSpace()).Select(s => s.Split(','))
                 join c in combos on ce[0] equals c.combo
                 //let exit = TryExit(ce[1])
                 let exitDelta = TryExitDelta(ce[2])
                 where (exitDelta != 0 && c.delta.Abs() < exitDelta)
                 select c
                 ).ForEach(combo => {
                   am?.CancelAllOrders("Closing combo by exitDelta");
                   CloseCombo(combo.combo);
                 });
              } catch(Exception exc) {
                Log = exc;
              }
            }, exc => {
              Log = exc;
            });
          if(false)
            base.Clients.Caller.orders(am.OrderContractsInternal
              .Select(oc =>
              new { order = oc.contract.Key + ":" + oc.order.Action, oc.status.status, filled = $"{oc.status.filled}<<{oc.status.remaining}" }
              ).OrderBy(oc => oc.order));
          double TryExitDelta(string s) => double.TryParse(s, out var i) ? i : int.MinValue;
        }
        return new { tm.TradingRatio, tm.OptionsDaysGap, Strategy = tm.Strategy + "" };
      });

    [BasicAuthenticationFilter]
    public void CancelOrder(int orderId) {
      GetAccountManager().UpdateOrder(orderId, 0);
    }
    [BasicAuthenticationFilter]
    public void UpdateCloseOrder(string instrument, int orderId, double? limit, double? profit) {
      if(limit.HasValue && profit.HasValue)
        throw new ArgumentException(new { limit, profit, error = "Only one can have value" } + "");
      if(!limit.HasValue && !profit.HasValue)
        throw new ArgumentException(new { limit, profit, error = "One must have a value" } + "");
      var am = GetAccountManager();
      am.ComboTrades(2)
        .Where(ct => ct.contract.Instrument == instrument)
        .Subscribe(trade => {
          try {
            if(limit.HasValue)
              am.OpenOrUpdateLimitOrder(trade.contract, trade.position, orderId, limit.Value);
            else {
              var p = profit.Value > 1 ? profit.Value
              : profit.Value >= 0.01 ? profit.Value
              : am.Account.Equity * profit.Value;
              am.OpenOrUpdateLimitOrderByProfit2(trade.contract.Instrument, trade.position, orderId, trade.open, p);
            }
            am.OpenOrderObservable
            .TakeUntil(DateTimeOffset.Now.AddSeconds(5))
            .Throttle(TimeSpan.FromSeconds(1))
            .Subscribe(_ => Clients.Caller.MustReadStraddles());
          } catch(Exception exc) {
            Log = exc;
          }
        });
    }
    [BasicAuthenticationFilter]
    public void OpenButterfly(string pair, string instrument, int quantity, bool useMarketPrice) {
      var am = ((IBWraper)trader.Value.TradesManager).AccountManager;
      if(IBApi.Contract.Contracts.TryGetValue(instrument, out var contract))
        UseTraderMacro(pair, tm =>
         tm.HistoricalVolatilityByPips().ForEach(hv
         => am.OpenLimitOrder(contract, quantity, hv, useMarketPrice, true)));
      else
        throw new Exception(new { instrument, not = "found" } + "");
    }
    [BasicAuthenticationFilter]
    public void OpenCoveredOption(string pair, int quantity, double? price) {
      Contract.Contracts.TryGetValue(pair, out var contract);
      if(contract == null)
        throw new Exception(new { pair, not = "found" } + "");
      GetAccountManager().OpenCoveredOption(contract, quantity, price.GetValueOrDefault());
    }
    [BasicAuthenticationFilter]
    public void CloseCombo(string instrument) {
      var am = GetAccountManager();
      if(am != null) {
        am.ComboTrades(1)
        .Where(ct => ct.contract.Key == instrument)
        .Select(s => (s.contract, s.position, s.orderId, s.closePrice))
        .Subscribe(c => {
          if(c.orderId != 0) {
            am.UseOrderContracts(orderContracts => orderContracts[c.orderId])
            .ForEach(och => {
              och.order.OrderType = "LMT";
              och.order.LmtPrice = c.closePrice;
              am.PlaceOrder(och.order, och.contract);
            });
          } else {
            am.CancelAllOrders("CloseCombo");
            am.OpenTrade(c.contract, -c.position, c.closePrice, 0.0, false, DateTime.MaxValue);
          }
        });
      }
    }
    [BasicAuthenticationFilter]
    public void CancelAllOrders() {
      var am = GetAccountManager();
      am?.CancelAllOrders(nameof(CancelAllOrders));
    }
    public object[] ReadContractsCache() {
      return Contract.Cache()
        .OrderBy(x => x.LastTradeDateOrContractMonth)
        .ThenBy(x => x.Strike)
        .ThenBy(x => x.IsPut)
        .Select(c => new { c.Instrument, c.ConId })
        .ToArray();
    }
    #endregion

    #region TradeConditions
    public string[] ReadTradingConditions(string pair) {
      return UseTradingMacro2(pair, 0, tm => tm.TradeConditionsAllInfo((tc, p, name) => name)).Concat().ToArray();
    }

    public string[] GetTradingConditions(string pair) {
      return UseTradingMacro2(pair, 0, tm => tm.TradeConditionsInfo((tc, p, name) => name)).Concat().ToArray();
    }
    [BasicAuthenticationFilter]
    public void SetTradingConditions(string pair, string[] names) {
      UseTradingMacro(pair, tm => { tm.TradeConditionsSet(names); });
    }
    public Dictionary<string, Dictionary<string, bool>> GetTradingConditionsInfo(string pair) {
      return UseTradingMacro(pair, tm => GetTradeConditionsInfo(tm));
    }

    private static Dictionary<string, Dictionary<string, bool>> GetTradeConditionsInfo(TradingMacro tm) {
      return tm.UseRates(ra => ra.IsEmpty() || !tm.IsRatesLengthStable)
        .DefaultIfEmpty(true)
        .SingleOrDefault()
        ? new Dictionary<string, Dictionary<string, bool>>()
        : tm.TradeConditionsInfo((d, p, t, c) => new { c, t, d = d() })
        .GroupBy(x => x.t)
        .Select(g => new { g.Key, d = g.ToDictionary(x => x.c, x => x.d.HasAny()) })
        .ToDictionary(x => x.Key + "", x => x.d);
    }
    #endregion

    #region TradeOpenActions
    public string[] ReadTradeOpenActions(string pair) {
      return UseTradingMacro(pair, tm => tm.TradeOpenActionsAllInfo((tc, name) => name).ToArray());
    }
    public string[] GetTradeOpenActions(string pair) {
      return UseTradingMacro(pair, tm => tm.TradeOpenActionsInfo((tc, name) => name).ToArray());
    }
    [BasicAuthenticationFilter]
    public void SetTradeOpenActions(string pair, string[] names) {
      UseTradingMacro(pair, tm => tm.TradeOpenActionsSet(names));
    }
    #endregion

    #region Strategies
    [BasicAuthenticationFilter]
    public void ClearStrategy(string pair) {
      UseTradingMacro(pair, tm => tm.CleanStrategyParameters());
    }
    public async Task<object[]> ReadStrategies(string pair) {
      return await UseTradingMacro(pair
        , async tm => (await RemoteControlModel.ReadStrategies(tm, (nick, name, content, uri, diff)
          => new { nick, name, uri, diff, isActive = diff.IsEmpty() })).ToArray()
        );
    }
    [BasicAuthenticationFilter]
    public void SaveStrategy(string pair, string nick) {
      if(string.IsNullOrWhiteSpace(nick))
        throw new ArgumentException("Is empty", "nick");
      UseTradingMacro(pair, 0, tm => RemoteControlModel.SaveStrategy(tm, nick));
    }
    public async Task RemoveStrategy(string name, bool permanent) {
      await RemoteControlModel.RemoveStrategy(name, permanent);
    }
    [BasicAuthenticationFilter]
    public async Task UpdateStrategy(string pair, string name) {
      await UseTradingMacro2(pair, 0, async tm => {
        tm.IsTradingActive = false;
        await RemoteControlModel.LoadStrategy(tm, name);
      }).WhenAllSequiential();
    }
    [BasicAuthenticationFilter]
    public async Task LoadStrategy(string pair, string strategyPath) {
      await UseTradingMacro2(pair, 0, async tm => {
        tm.IsTradingActive = false;
        await RemoteControlModel.LoadStrategy(tm, strategyPath);
      }).WhenAllSequiential();
    }
    [BasicAuthenticationFilter]
    public void SaveTradingMacros(string tradingMacroName) {
      if(string.IsNullOrWhiteSpace(tradingMacroName))
        throw new ArgumentException("Is empty", nameof(tradingMacroName));
      if(GlobalStorage.TradingMacroNames.Any(tm => tm.ToLower() == tradingMacroName.ToLower()))
        throw new ArgumentException($"Trading Macro[{tradingMacroName}] already exists");
      remoteControl.Value.SaveTradingMacros(tradingMacroName);
    }
    #endregion

    #region ReplayArguments
    public object ReadReplayArguments(string pair) {
      var ra = UseTradingMacro(pair, tm => new {
        remoteControl.Value.ReplayArguments.DateStart,
        isReplayOn = tm.IsInPlayback,
        remoteControl.Value.ReplayArguments.LastWwwError
      });
      remoteControl.Value.ReplayArguments.LastWwwError = "";
      return ra;
    }
    public object StartReplay(string pair, string startWhen) {
      TimeSpan ts;
      DateTime dateStart = TimeSpan.TryParse(startWhen, out ts)
        ? DateTime.Now.Subtract(ts)
        : DateTime.Parse(startWhen);
      remoteControl.Value.ReplayArguments.DateStart = dateStart;
      remoteControl.Value.ReplayArguments.IsWww = true;
      UseTradingMacro(pair, tm => remoteControl.Value.StartReplayCommand.Execute(tm));
      return ReadReplayArguments(pair);
    }
    public object StopReplay(string pair) {
      remoteControl.Value.ReplayArguments.MustStop = true;
      return ReadReplayArguments(pair);
    }
    #endregion
    public object GetWwwInfo(string pair, int chartNum) {
      return UseTradingMacro2(pair, chartNum, tm => tm.WwwInfo()).DefaultIfEmpty(new { }).First();
    }

    public void Send(string pair, string newInyterval) {
      try {
        App.SignalRInterval = int.Parse(newInyterval);
        App.ResetSignalR();
      } catch(Exception exc) {
        GalaSoft.MvvmLight.Messaging.Messenger.Default.Send<LogMessage>(new LogMessage(exc));
      }
    }
    public void CloseTrades(string pair) {
      try {
        GetTradingMacro(pair, 0)
          .AsParallel()
          .ForAll(tm => tm.CloseTrades("SignalR: CloseTrades"));
      } catch(Exception exc) {
        GalaSoft.MvvmLight.Messaging.Messenger.Default.Send<LogMessage>(new LogMessage(exc));
      }
    }
    public void CloseTradesAll(string pair) {
      try {
        trader.Value.TradesManager.GetTrades().ForEach(t => trader.Value.TradesManager.ClosePair(t.Pair));
        trader.Value.GrossToExitSoftReset();
        var tms = GetHedgedTradingMacros(pair).SelectMany(x => new[] { x.tm1, x.tm2 })
        .Distinct(tm => tm.Pair)
        .DefaultIfEmpty(GetTradingMacro(pair, 0).Single())
        .ToArray();
        tms.AsParallel().ForAll(tm => tm.CloseTrades($"SignalR: {nameof(CloseTradesAll)}"));
      } catch(Exception exc) {
        GalaSoft.MvvmLight.Messaging.Messenger.Default.Send<LogMessage>(new LogMessage(exc));
      }
    }
    public void MoveTradeLevel(string pair, bool isBuy, double pips) {
      try {
        GetTradingMacro(pair).ForEach(tm => tm.MoveBuySellLeve(isBuy, pips));
      } catch(Exception exc) {
        GalaSoft.MvvmLight.Messaging.Messenger.Default.Send<LogMessage>(new LogMessage(exc));
      }
    }
    public void SetTradeRate(string pair, bool isBuy, double price) {
      GetTradingMacro(pair).ForEach(tm => tm.SetTradeRate(isBuy, price));
    }
    public void ManualToggle(string pair) {
      try {
        GetTradingMacro(pair).ForEach(tm => tm.ResetSuppResesInManual());
      } catch(Exception exc) {
        GalaSoft.MvvmLight.Messaging.Messenger.Default.Send<LogMessage>(new LogMessage(exc));
      }
    }
    [BasicAuthenticationFilter]
    public void ToggleStartDate(string pair, int chartNumber) {
      UseTradingMacro(pair, chartNumber, tm => tm.ToggleCorridorStartDate());
    }
    [BasicAuthenticationFilter]
    public void SetStrategy(string pair, string strategy) {
      UseTradingMacro(pair, tm => tm.Strategy = EnumUtils.Parse<Strategies>(strategy));
    }
    [BasicAuthenticationFilter]
    public bool ToggleIsActive(string pair, int chartNumber) {
      return UseTradingMacro2(pair, chartNumber, tm => tm.ToggleIsActive()).DefaultIfEmpty().First();
    }
    [BasicAuthenticationFilter]
    public void FlipTradeLevels(string pair) {
      UseTradingMacro(pair, tm => tm.FlipTradeLevels());
    }
    [BasicAuthenticationFilter]
    public void WrapTradeInCorridor(string pair) {
      UseTradingMacro(pair, tm => tm.WrapTradeInCorridor());
    }
    [BasicAuthenticationFilter]
    public void Buy(string pair) {
      UseTradingMacro(pair, tm => tm.OpenTrade(true, tm.Trades.IsBuy(false).Lots() + tm.LotSizeByLossBuy, "web: buy with reverse"));
    }
    [BasicAuthenticationFilter]
    public void Sell(string pair) {
      UseTradingMacro(pair, tm => tm.OpenTrade(false, tm.Trades.IsBuy(true).Lots() + tm.LotSizeByLossSell, "web: sell with reverse"));
    }
    [BasicAuthenticationFilter]
    public void SetRsdTreshold(string pair, int chartNum, int pips) {
      UseTradingMacro(pair, chartNum, tm => tm.RatesStDevMinInPips = pips);
    }
    public object[] AskRates(int charterWidth, DateTimeOffset startDate, DateTimeOffset endDate, string pair, BarsPeriodType chartNum) {
      var a = UseTradingMacro(pair, tm => tm.BarPeriod == chartNum
        , tm => remoteControl.Value.ServeChart(charterWidth, startDate, endDate, tm));
      return a.SelectMany(x => x).ToArray();

      //var a = UseTradingMacro2(pair, chartNum
      //  , tm => Task.Run(() => remoteControl.Value.ServeChart(charterWidth, startDate, endDate, tm)));
      //var b = await Task.WhenAll(a);
      //return b.SelectMany(x => x).ToArray();
    }

    static int _sendChartBufferCounter = 0;
    static SendChartBuffer _sendChartBuffer = SendChartBuffer.Create();
    public void AskRates_(int charterWidth, DateTimeOffset startDate, DateTimeOffset endDate, string pair) {
      UseTradingMacro2(pair, 0, tm => {
        if(tm.IsActive)
          _sendChartBuffer.Push(() => {
            if(_sendChartBufferCounter > 1) {
              throw new Exception(new { _sendChartBufferCounter } + "");
            }
            _sendChartBufferCounter++;
            Clients.Caller.sendChart(pair, 0, remoteControl.Value.ServeChart(charterWidth, startDate, endDate, tm));
            _sendChartBufferCounter--;
          });
      });
    }

    [BasicAuthenticationFilter]
    public void SetPresetTradeLevels(string pair, TradeLevelsPreset presetLevels, object isBuy) {
      bool? b = isBuy == null ? null : (bool?)isBuy;
      UseTradingMacro(pair, tm => tm.IsTrader, tm => tm.SetTradeLevelsPreset(presetLevels, b));
    }
    void SetPresetTradeLevels(TradingMacro tm, TradeLevelsPreset presetLevels, object isBuy) {
      bool? b = isBuy == null ? null : (bool?)isBuy;
      tm.SetTradeLevelsPreset(presetLevels, b);
    }
    public TradeLevelsPreset[] GetPresetTradeLevels(string pair) {
      return UseTradingMacro(pair, tm => tm.GetTradeLevelsPreset().ToArray());
    }
    [BasicAuthenticationFilter]
    public void SetTradeLevel(string pair, bool isBuy, int level) {
      //var l = level.GetType() == typeof(string) ? (TradeLevelBy)Enum.Parse(typeof(TradeLevelBy), level+"") : (TradeLevelBy)level;
      UseTradingMacro(pair, tm => {
        if(isBuy) {
          tm.LevelBuyBy = (TradeLevelBy)level;
        } else {
          tm.LevelSellBy = (TradeLevelBy)level;
        }
      });
    }
    [BasicAuthenticationFilter]
    public void SetTradeTrendIndex(string pair, int index) {
      UseTradingMacro(pair, tm => tm.IsTrender, tm => {
        var trds = tm.TradingMacroTrader();
        var hasMM = trds.Any(trd => HasMinMaxTradeLevels(trd));
        if(!hasMM)
          trds.ForEach(trd => SetPresetTradeLevels(trd, TradeLevelsPreset.MinMax, null));
        var has = tm.TradeTrendsInt.Contains(index);
        tm.TradeTrends = has
        ? hasMM
        ? string.Join(",", tm.TradeTrendsInt.Where(i => i != index))
        : tm.TradeTrends
        : tm.TradeTrends + "," + index;
      });
    }
    [BasicAuthenticationFilter]
    public int[] GetTradeTrendIndex(string pair) {
      return UseTradingMacro(pair, tm => tm.IsTrender, tm => tm.TradingMacroTrader(trd => GetTradeTrendIndexImpl(trd, tm).Single())).TakeLast(1).SelectMany(i => i).ToArray();
    }

    private static int[] GetTradeTrendIndexImpl(TradingMacro tmTrader, TradingMacro tmTrender) {
      return tmTrader.GetTradeLevelsPreset()
            .Where(tpl => tpl == TradeLevelsPreset.MinMax)
            .SelectMany(_ => tmTrender.TradeTrendsInt.Take(1)).ToArray();
    }

    [BasicAuthenticationFilter]
    public void SetTradeCloseLevel(string pair, bool isBuy, int level) {
      UseTradingMacro(pair, tm => {
        if(isBuy) {
          tm.LevelBuyCloseBy = (TradeLevelBy)level;
        } else {
          tm.LevelSellCloseBy = (TradeLevelBy)level;
        }
      });
    }
    public object[] ReadNews() {
      return ReadNewsFromDb().ToArray(en => new { en.Time, en.Name, en.Level, en.Country });
    }

    private static DB.Event__News[] ReadNewsFromDb() {
      try {
        var date = DateTimeOffset.UtcNow.AddMinutes(-60);
        var countries = new[] { "USD", "US", "GBP", "GB", "EUR", "EMU", "JPY", "JP" };
        return GlobalStorage.UseForexContext(c => c.Event__News.Where(en => en.Time > date && countries.Contains(en.Country)).ToArray());
      } catch {
        return new DB.Event__News[0];
      }
    }
    public Dictionary<string, int> ReadEnum(string enumName) {
      return
        typeof(TradingMacro).Assembly.GetTypes()
        .Concat(typeof(HedgeHog.Bars.Rate).Assembly.GetTypes())
        .Where(t => t.Name.ToLower() == enumName.ToLower())
        .SelectMany(et => Enum.GetNames(et), (et, e) => new { name = e, value = (int)Enum.Parse(et, e, true) })
        .IfEmpty(() => { throw new Exception(new { enumName, message = "Not Found" } + ""); })
        .ToDictionary(t => t.name, t => t.value);
    }
    public Dictionary<string, int> ReadTradeLevelBys() {
      return Enum.GetValues(typeof(TradeLevelBy)).Cast<TradeLevelBy>().ToDictionary(t => t.ToString(), t => (int)t);
    }
    #region TradeSettings
    [BasicAuthenticationFilter]
    public ExpandoObject SaveTradeSettings(string pair, int chartNum, ExpandoObject ts) {
      return UseTradingMacro2(pair, chartNum, tm => {
        var props = (IDictionary<string, object>)ts;
        props.ForEach(kv => {
          tm.SetProperty(kv.Key, kv.Value, p => p.GetSetMethod() != null);
        });
        return ReadTradeSettings(pair, chartNum);
      }).DefaultIfEmpty(new { }.ToExpando()).First();
    }
    public ExpandoObject ReadTradeSettings(string pair, int chartNum) {
      return UseTradingMacro2(pair, chartNum, tm => {
        var e = (IDictionary<string, object>)new ExpandoObject();
        Func<object, object> convert = o => o != null && o.GetType().IsEnum ? o + "" : o;
        Func<PropertyInfo, string> dn = pi => pi.GetCustomAttributes<DisplayNameAttribute>().Select(a => a.DisplayName).DefaultIfEmpty(pi.Name).Single();
        tm.GetPropertiesByAttibute<WwwSettingAttribute>(_ => true)
          .Where(x => !x.Item1.Group.ToLower().StartsWith("hide"))
          .Select(x => new { x.Item1.Group, p = x.Item2, dn = dn(x.Item2) })
          .OrderBy(x => x.dn)
          .OrderBy(x => x.Group)
          .ForEach(x => e.Add(x.p.Name, new { v = convert(x.p.GetValue(tm)), g = x.Group, x.dn }));
        return e as ExpandoObject;
      }).DefaultIfEmpty(new { }.ToExpando()).First();
    }
    #endregion
    static string MakePair(string pair) { return TradesManagerStatic.IsCurrenncy(pair) ? pair.Substring(0, 3) + "/" + pair.Substring(3, 3) : pair; }
    bool IsEOW(DateTime date) => date.DayOfWeek == DayOfWeek.Friday && date.InNewYork().TimeOfDay > new TimeSpan(16, 0, 0);
    public Trade[] ReadClosedTrades(string pair) {
      try {
        var tms = GetTradingMacros(pair).Where(tm => tm.BarPeriod > BarsPeriodType.t1).Take(1).ToArray();
        var rc = remoteControl.Value;
        var trades = rc.GetClosedTrades("").Concat(rc.TradesManager.GetTrades()).ToArray();
        var tradesNew = (
          from tm in tms
          from trade in trades
          from dateMin in tm.RatesArray.Take(1).Select(r => r.StartDate)
          where trade.Time >= dateMin
          orderby trade.Time descending
          let rateOpen = tm.RatesArray.FuzzyFinder(trade.Time, (t, r1, r2) => t.Between(r1.StartDate, r2.StartDate)).Take(1)
          .IfEmpty(() => IsEOW(trade.Time) ? tm.RatesArray.TakeLast(1) : new Rate[0]).ToArray()
          let rateClose = tm.RatesArray.FuzzyFinder(trade.TimeClose, (t, r1, r2) => t.Between(r1.StartDate, r2.StartDate)).Take(1)
          .IfEmpty(() => IsEOW(trade.Time) ? tm.RatesArray.TakeLast(1) : new Rate[0]).ToArray().ToArray()
          where rateOpen.Any() && (trade.Kind != PositionBase.PositionKind.Closed || rateClose.Any())
          select (trade, rateOpen, rateClose)
         ).Select(t => {
           var trade = t.trade.Clone();
           t.rateOpen.Select(r => r.PriceAvg).ForEach(p => trade.Open = p);
           t.rateClose.Select(r => r.PriceAvg).ForEach(p => trade.Close = p);
           return trade;
         }).ToArray();
        return tradesNew;
      } catch(Exception exc) {
        GalaSoft.MvvmLight.Messaging.Messenger.Default.Send<LogMessage>(new LogMessage(exc));
        return new Trade[0];
      }
    }
    public object GetWaveRanges(string pair, int chartNum) {
      var value = MonoidsCore.ToFunc(0.0, false, (v, mx) => new { v, mx });
      var wrStats = UseTradingMacro(tm => true, pair, chartNum)
        .Select(tm => new { wrs = new[] { tm.WaveRangeAvg, tm.WaveRangeSum }, inPips = new Func<double, double>(d => tm.InPips(d)) })
        .SelectMany(x => x.wrs, (x, wr) => new {
          i = 0,
          ElliotIndex = value(0, false),
          Angle = value(wr.Angle, false),
          Minutes = value(wr.TotalMinutes, false),
          PPM = value(wr.PipsPerMinute, false),
          HSD = value(wr.HSDRatio, false),
          StDev = value(wr.StDev.Round(2), false),
          Distance = value(wr.Distance.Round(0), false),
          DistanceCma = value(wr.DistanceCma.Round(0), false),
          DistanceByRegression = value(wr.DistanceByRegression.Round(0), false),
          WorkByHeight = value(wr.WorkByHeight.Round(0), false),
          IsTail = false,
          IsFOk = false,
          IsDcOk = false,
          IsStats = true
        })
        .ToArray();
      var wrs = UseTradingMacro(tm => true, pair, chartNum)
        .SelectMany(tm => tm.WaveRangesWithTail, (tm, wr) => new { inPips = new Func<double, double>(d => tm.InPips(d)), wr, rs = tm.WaveRanges })
        .Select((x, i) => new {
          i,
          ElliotIndex = value((double)x.wr.ElliotIndex, false),
          Angle = value(x.wr.Angle.Round(0), x.wr.Angle.Abs() >= wrStats[0].Angle.v),//.ToString("###0.0"),
          Minutes = value(x.wr.TotalMinutes.ToInt(), x.wr.TotalMinutes >= wrStats[0].Minutes.v),//.ToString("###0.0"),
          PPM = value(x.wr.PipsPerMinute, x.wr.PipsPerMinute >= wrStats[0].PPM.v),//.ToString("###0.0"),
          HSD = value(x.wr.HSDRatio.Round(1), x.wr.HSDRatio >= wrStats[0].HSD.v),//.ToString("###0.0"),
          StDev = value(x.wr.StDev.Round(5), x.wr.StDev < wrStats[0].StDev.v),//.ToString("#0.00"),
          Distance = value(x.wr.Distance.Round(0), x.wr.Distance > wrStats[0].Distance.v),
          DistanceCma = value(x.wr.DistanceCma.Round(0), x.wr.Index(x.rs, wr => wr.DistanceCma) == 0),
          DistanceByRegression = value(x.wr.DistanceByRegression.Round(0), x.wr.Index(x.rs, wr => wr.DistanceByRegression) == 0),
          WorkByHeight = value(x.wr.WorkByHeight.Round(0), x.wr.Index(x.rs, wr => wr.WorkByHeight) == 0),
          x.wr.IsTail,
          IsFOk = x.wr.IsFatnessOk,
          IsDcOk = x.wr.IsDistanceCmaOk,
          IsStats = false
        })
        .ToList();
      #region Not Used
      Func<object, double> getProp = (o) =>
        o.GetType()
        .GetProperties()
        .Where(p => p.Name == "v")
        .Select(p => new Func<double>(() => (double)p.GetValue(o)))
        .DefaultIfEmpty(() => (double)o)
        .First()();
      var wra = wrs.Take(0).Select(wr0 => wr0.GetType()
        .GetProperties()
        .ToDictionary(p => p.Name, p => (object)value(
          wrs
          .Select(wr => getProp(p.GetValue(wr)).Abs())
          .DefaultIfEmpty(0)
          .ToArray()
          .AverageInRange(1, -1)
          .Average(),
          false
          )))
          .Do(wr => wr.Add("IsStats", true))
          .ToArray();
      #endregion
      //var wrStd = wrs.Take(1).Select(wr0 => wr0.GetType()
      //  .GetProperties()
      //  .ToDictionary(p => p.Name, p => {
      //    var dbls = wrs.Select(wr => getProp(p.GetValue(wr)).Abs()).ToArray();
      //    return value(dbls.StandardDeviation() / dbls.Height(), false);
      //  }));
      var res = wrs.Cast<object>()
        //.Concat(wra.Cast<object>())
        .Concat(wrStats.Cast<object>())
        /*.Concat(wrStd.Cast<object>())*/.ToArray();
      return res;
    }

    public object[] GetAccounting(string pair) {
      var row = MonoidsCore.ToFunc("", (object)null, (n, v) => new { n, v });
      var list = new[] { row("", 0) }.Take(0).ToList();
      if(remoteControl == null) return new object[0];
      var rc = remoteControl.Value;
      var am = rc.MasterModel.AccountModel;
      double CompInt(double profit) => am.Equity.CompoundInteres(profit, 252);
      list.Add(row("BalanceOrg", am.OriginalBalance.ToString("c0")));
      //list.Add(row("Balance", am.Balance.ToString("c0")));
      list.Add(row("Equity", am.Equity.ToString("c0")));
      var more = UseTraderMacro(pair, tm => {
        Trade[] trades = tm.Trades.Concat(remoteControl.Value.TradesManager.GetTrades(tm.PairHedge)).ToArray();
        var ht = tm.HaveTrades();
        var list2 = new[] { row("", 0) }.Take(0).ToList();
        if(trades.Any()) {
          var c = am.CurrentGross > 0 ? "lawngreen" : "lightpink";
          list2.Add(row("CurrentGross", new { v = am.CurrentGross.AutoRound2("$", 2) + (ht ? "/" + (am.ProfitPercent * 100).AutoRound2(2, "%") : ""), c }));

          if(tm.HaveHedgedTrades())
            list2.Add(row("CurrentLot", string.Join("/", trades.GroupBy(t => t.Pair).Select(g => g.Sum(t => t.Position)))));
          else if(ht)
            list2.Add(row("CurrentLot", tm.Trades.Lots() + (ht ? "/" + tm.PipAmount.AutoRound2("$", 2) : "")));
          list2.Add(row($"{pair} Gross", $"{trades.Gross().AutoRound2("$", 2)}@{tm.ServerTime.TimeOfDay.ToString(@"hh\:mm\:ss")}"));
        }
        list2.Add(row("Trades Gross", $"{rc.TradesManager.GetTrades().Gross().AutoRound2("$", 2)}@{tm.ServerTime.TimeOfDay.ToString(@"hh\:mm\:ss")}"));
        if(tm.LastTradeLoss < -1000000)
          list2.Add(row("Last Loss", tm.LastTradeLoss.AutoRound2("$", 2)));
        if(!ht) {
          list2.Add(row("PipAmountBuy", tm.PipAmountBuy.AutoRound2("$", 2) + "/" + (tm.PipAmountBuyPercent * 100).AutoRound2(2, "%")));
          list2.Add(row("PipAmountSell", tm.PipAmountSell.AutoRound2("$", 2) + "/" + (tm.PipAmountSellPercent * 100).AutoRound2(2, "%")));
        }
        if(!tm.HaveHedgedTrades()) {
          list2.Add(row("PipsToMC", (!ht ? 0 : am.PipsToMC).ToString("n0")));
          var lsb = (tm.IsCurrency ? (tm.LotSizeByLossBuy / 1000.0).Floor() + "K/" : tm.LotSizeByLossBuy + "/");
          var lss = (tm.IsCurrency ? (tm.LotSizeByLossSell / 1000.0).Floor() + "K/" : tm.LotSizeByLossSell + "/");
          var lsp = tm.LotSizePercent.ToString("p0");
          list2.Add(row("LotSizeBS", lsb + (lsb == lss ? "" : "" + lss) + lsp));
        }
        var pos = tm.PendingEntryOrders.Concat(tm.TradingMacroHedged(tmh => tmh.PendingEntryOrders).Concat()).ToArray();
        if(pos.Any())
          list2.Add(row("Pending", pos.Select(po => po.Key).ToArray().ToJson(false)));
        var oss = rc.TradesManager.GetOrderStatuses();
        if(oss.Any()) {
          var s = string.Join("<br/>", oss.Select(os => $"{os.status}:{os.filled}<{os.remaining}"));
          list2.Add(row("Orders", s));
        }
        if(tm.IsHedgedTrading)
          tm.MaxHedgeProfit?.ForEach(mhps => list2.Add(row("Hedge Profit", "$" + string.Join("/", mhps.Select(mhp => mhp.profit.AutoRound2(1))) + "/" +
            (CompInt(mhps.DefaultIfEmpty().Average(x => x.profit)) * 100).AutoRound2(3, "%")
            )));
        if(trader.Value.GrossToExitCalc() != 0) {
          var ca = CompInt(trader.Value.GrossToExitCalc().Abs());
          list2.Add(row("GrossToExit", $"${trader.Value.GrossToExitCalc().AutoRound2(1)}:{((ca) * 100).AutoRound2(3, "%")}"));
          list2.Add(row("grossToExitRaw", trader.Value.GrossToExit));
          list2.Add(row("profitByHedgeRatioDiff", trader.Value.ProfitByHedgeRatioDiff));
        }
        tm.CurrentPut?.ForEach(p =>
        list2.Add(row("Curr Put", $"{p.option.Key}{(p.option.IsFutureOption ? ":" + p.option.LastTradeDateOrContractMonth : "")}")));
        tm.CurrentStraddle?.ForEach(p => list2.Add(row("Curr Strdl", $"{p.combo.contract}")));
        return list2;
      }).Concat();
      return list.Concat(more).ToArray();
    }
    double IntOrDouble(double d, double max = 10) {
      return d.Abs() > max ? d.ToInt() : d.Round(1);
    }
    [BasicAuthenticationFilter]
    public void SetTradeCount(string pair, int tradeCount) {
      GetTradingMacro(pair, tm => tm.SetTradeCount(tradeCount));
    }
    [BasicAuthenticationFilter]
    public void SaveGrossToExit(double grossToExit) {
      trader.Value.GrossToExit = grossToExit;
    }
    [BasicAuthenticationFilter]
    public void SaveProfitByHedgeRatioDiff(double profitByRatioDiff) {
      trader.Value.ProfitByHedgeRatioDiff = profitByRatioDiff;
    }
    [BasicAuthenticationFilter]
    public void StopTrades(string pair) { SetCanTradeImpl(pair, false, null); }
    [BasicAuthenticationFilter]
    public void StartTrades(string pair, bool isBuy) { SetCanTrade(pair, true, isBuy); }
    void SetCanTradeImpl(string pair, bool canTrade, bool? isBuy) {
      GetTradingMacro(pair).ForEach(tm => {
        if(!isBuy.HasValue)
          tm.BuySellLevels.ForEach(sr => sr.InManual = canTrade);
        tm.SetCanTrade(canTrade, isBuy);
      });
    }
    [BasicAuthenticationFilter]
    public bool[] ToggleCanTrade(string pair, bool isBuy) {
      return UseTradingMacro(pair, tm => tm.ToggleCanTrade(isBuy)).ToArray();
    }
    [BasicAuthenticationFilter]
    public void SetHedgedPair(string pair, string pairHedge) => UseTraderMacro(pair, tm => {
      if(pair != pairHedge) tm.PairHedge = pairHedge;
    });
    public string ReadHedgedPair(string pair) => UseTraderMacro(pair, tm => tm.PairHedge).SingleOrDefault();

    public void SetCanTrade(string pair, bool canTrade, bool isBuy) {
      GetTradingMacro(pair).ForEach(tm => {
        tm.BuySellLevels.ForEach(sr => sr.InManual = canTrade);
        tm.SetCanTrade(canTrade, isBuy);
      });
    }

    public string[] ReadPairs() {
      return remoteControl?.Value?
        .TradingMacrosCopy
        .Where(tm => tm.IsActive && tm.IsTrader)
        .Select(tm => tm.PairPlain)
        .Concat(new[] { "" })
        .ToArray();
    }
    private void GetTradingMacro(string pair, Action<TradingMacro> action) {
      GetTradingMacro(pair)
        .ForEach(action);
    }
    private IEnumerable<TradingMacro> GetTradingMacro(string pair, int chartNum = 0) {
      return GetTradingMacros(pair)
        .Skip(chartNum)
        .Take(1);
      ;
    }

    private IEnumerable<TradingMacro> GetTradingMacros(string pair) {
      return GetTradingMacros()
        .Where(tm => tm.PairPlain == pair)
        .OrderBy(tm => tm.TradingGroup)
        .ThenBy(tm => tm.PairIndex)
        .AsEnumerable()
        ?? new TradingMacro[0];
    }
    private IEnumerable<TradingMacro> GetTradingMacros() {
      return remoteControl?.Value?
        .TradingMacrosCopy
        .Where(tm => tm.IsActive)
        .OrderBy(tm => tm.TradingGroup)
        .ThenBy(tm => tm.PairIndex)
        .AsEnumerable()
        ?? new TradingMacro[0];
    }

    IEnumerable<TradingMacro> UseTradingMacro(Func<TradingMacro, bool> predicate, string pair) {
      return UseTradingMacro(predicate, pair, 0);
    }
    IEnumerable<TradingMacro> UseTradingMacro(Func<TradingMacro, bool> predicate, string pair, int chartNum) {
      try {
        return GetTradingMacro(pair, chartNum).Where(predicate).Take(1);
      } catch(Exception exc) {
        GalaSoft.MvvmLight.Messaging.Messenger.Default.Send<LogMessage>(new LogMessage(exc));
        return new TradingMacro[0];
      }
    }
    void UseTradingMacro<T>(string pair, Action<TradingMacro> func) {
      UseTradingMacro2(pair, 0, func);
    }
    T UseTradingMacro<T>(string pair, Func<TradingMacro, T> func) {
      return UseTradingMacro2(pair, 0, func).First();
    }
    void UseTradingMacro(string pair, Action<TradingMacro> action) {
      UseTradingMacro(pair, 0, action);
    }
    void UseTraderMacro(string pair, Action<TradingMacro> action) {
      UseTradingMacro(pair, tm => tm.IsTrader, action);
    }
    void UseTradingMacro(string pair, Func<TradingMacro, bool> where, Action<TradingMacro> action) {
      GetTradingMacros(pair)
        .Where(where)
        .ForEach(action);
    }
    IEnumerable<T> UseTradingMacro<T>(string pair, Func<TradingMacro, bool> where, Func<TradingMacro, T> func) {
      return GetTradingMacros(pair)
        .Where(where)
        .Select(func);
    }
    void UseTradingMacro(string pair, int chartNum, Action<TradingMacro> action) {
      try {
        GetTradingMacro(pair, chartNum).ForEach(action);
      } catch(Exception exc) {
        GalaSoft.MvvmLight.Messaging.Messenger.Default.Send<LogMessage>(new LogMessage(exc));
      }
    }
    T UseTradingMacroOld<T>(string pair, int chartNum, Func<TradingMacro, T> func) {
      try {
        return GetTradingMacro(pair, chartNum).Select(func)
          .IfEmpty(() => { throw new Exception(new { TradingMacroNotFound = new { pair, chartNum } } + ""); }).First();
      } catch(Exception exc) {
        GalaSoft.MvvmLight.Messaging.Messenger.Default.Send<LogMessage>(new LogMessage(exc));
        return default;
      }
    }
    T[] UseTraderMacro<T>(string pair, Func<TradingMacro, T> func) {
      return UseTradingMacro2(pair, tm => tm.IsTrader, func);
    }
    T[] UseTradingMacro2<T>(string pair, Func<TradingMacro, bool> where, Func<TradingMacro, T> func) {
      try {
        return GetTradingMacros(pair).Where(where).Select(func).ToArray();
      } catch(Exception exc) {
        GalaSoft.MvvmLight.Messaging.Messenger.Default.Send<LogMessage>(new LogMessage(exc));
        return new T[0];
      }
    }
    IEnumerable<T> UseTradingMacro2<T>(string pair, int chartNum, Func<TradingMacro, T> func) {
      try {
        return GetTradingMacros(pair).Skip(chartNum).Take(1).Select(func);
      } catch(Exception exc) {
        GalaSoft.MvvmLight.Messaging.Messenger.Default.Send<LogMessage>(new LogMessage(exc));
        return new T[0];
      }
    }
    void UseTradingMacro2(string pair, int chartNum, Action<TradingMacro> func) {
      try {
        GetTradingMacro(pair, chartNum).ForEach(func);
      } catch(Exception exc) {
        GalaSoft.MvvmLight.Messaging.Messenger.Default.Send<LogMessage>(new LogMessage(exc));
      }
    }

  }
}
