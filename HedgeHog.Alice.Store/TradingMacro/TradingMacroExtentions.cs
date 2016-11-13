﻿using System;
using System.Collections.Generic;
using System.Linq;
using HedgeHog.Bars;
using HedgeHog.Shared;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Diagnostics;
using System.Reflection;
using System.Collections.Specialized;
using System.Windows;
using HedgeHog.Alice.Store.Metadata;
using System.Linq.Expressions;
using System.Windows.Input;
using Gala = GalaSoft.MvvmLight.Command;
using System.Reactive.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using System.Web;
using System.Web.Caching;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Runtime.Caching;
using System.Reactive;
using System.Threading.Tasks.Dataflow;
using System.Collections.Concurrent;
using HedgeHog;
using HedgeHog.Shared.Messages;
using Hardcodet.Util.Dependencies;
using HedgeHog.UI;
using System.ComponentModel.Composition;
using ReactiveUI;
using HedgeHog.NewsCaster;
using System.Data.Entity.Core.Objects.DataClasses;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Runtime.Serialization;
using TL = HedgeHog.Bars.Rate.TrendLevels;
namespace HedgeHog.Alice.Store {
  [JsonObject(MemberSerialization.OptOut)]
  public partial class TradingMacro {
    List<SuppRes> _suppRes = new List<SuppRes>();
    public List<SuppRes> SuppRes {
      get {
        return _suppRes;
      }
    }

    #region Subjechiss
    static TimeSpan THROTTLE_INTERVAL = TimeSpan.FromSeconds(1);

    public void OnLoadRates(Action a = null) {
      _loadRatesAsyncBuffer.Push(() => LoadRates(a));
      //broadcastLoadRates.Post(u => LoadRates(a));
    }

    #region ScanCorridor Broadcast
    #endregion
    #endregion

    #region Events

    event EventHandler ShowChartEvent;
    public event EventHandler ShowChart {
      add {
        if(ShowChartEvent == null || !ShowChartEvent.GetInvocationList().Contains(value))
          ShowChartEvent += value;
      }
      remove {
        ShowChartEvent -= value;
      }
    }
    void RaiseShowChart() {
      if(ShowChartEvent != null)
        ShowChartEvent(this, EventArgs.Empty);
    }

    #endregion

    #region NeedChartSnaphot Event
    byte[] _lastChartSnapshot = null;
    public void SetChartSnapshot(byte[] image) { _lastChartSnapshot = image; }
    event EventHandler<EventArgs> NeedChartSnaphotEvent;
    public event EventHandler<EventArgs> NeedChartSnaphot {
      add {
        if(NeedChartSnaphotEvent == null || !NeedChartSnaphotEvent.GetInvocationList().Contains(value))
          NeedChartSnaphotEvent += value;
      }
      remove {
        NeedChartSnaphotEvent -= value;
      }
    }
    protected void RaiseNeedChartSnaphot() {
      if(NeedChartSnaphotEvent != null)
        NeedChartSnaphotEvent(this, new EventArgs());
    }
    #endregion


    #region Snapshot control
    SnapshotArguments _SnapshotArguments;

    public SnapshotArguments SnapshotArguments {
      get {
        if(_SnapshotArguments == null) {
          _SnapshotArguments = new SnapshotArguments();
          _SnapshotArguments.ShowSnapshot += (s, e) => ShowSnaphot(SnapshotArguments.DateStart, SnapshotArguments.DateEnd);
          _SnapshotArguments.AdvanceSnapshot += (s, e) => AdvanceSnapshot(SnapshotArguments.DateStart, SnapshotArguments.DateEnd, false);
          _SnapshotArguments.DescendSnapshot += (s, e) => AdvanceSnapshot(SnapshotArguments.DateStart, SnapshotArguments.DateEnd, true);
          _SnapshotArguments.MatchSnapshotRange += (s, e) => MatchSnapshotRange();
        }
        return _SnapshotArguments;
      }
    }
    void MatchSnapshotRange() {
      var dateStart = new DateTimeOffset(CorridorStartDate.GetValueOrDefault(CorridorStats.StartDate));
      var dateEnd = new DateTimeOffset(CorridorStopDate.IfMin(RatesArraySafe.LastBC().StartDate));
      var dateRangeStart = dateStart.AddDays(-1);
      var dateRangeEnd = dateStart.AddDays(1);
      Func<Rate, double> price = r => r.CrossesDensity;// r.PriceCMALast;
      var ratesSample = RatesArray.SkipWhile(r => r.StartDate2 < dateStart).TakeWhile(r => r.StartDate2 <= dateEnd).Select(price).ToArray();
      var interval = ratesSample.Count();
      var heightSample = ratesSample.Height();
      var hourStart = dateStart.AddHours(-1).UtcDateTime.Hour;
      var hourEnd = dateStart.AddHours(1).UtcDateTime.Hour;
      Func<DateTimeOffset, bool> isHourOk = d => hourEnd > hourStart ? d.UtcDateTime.Hour.Between(hourStart, hourEnd) : !d.UtcDateTime.Hour.Between(hourStart, hourEnd);
      Func<DateTimeOffset, bool> isDateOk = d => d.DayOfWeek == dateStart.DayOfWeek && isHourOk(d);
      var dateLoadStart = dateStart.AddYears(-1);
      var ratesHistory = GlobalStorage.GetRateFromDB(Pair, dateLoadStart.DateTime, int.MaxValue, BarPeriodInt);
      ratesHistory.Take(interval).ForEach(r => r.CrossesDensity = 0);
      ratesHistory.Cma(r => r.PriceAvg, PriceCmaLevels);
      Enumerable.Range(0, ratesHistory.Count() - interval).AsParallel().ForAll(i => {
        try {
          var range = new Rate[interval];
          ratesHistory.CopyTo(i, range, 0, interval);
          var cd = range.Select(_priceAvg).CrossesInMiddle(range.Select(GetPriceMA())).Count / (double)interval;
          range.LastBC().CrossesDensity = cd;
        } catch(Exception exc) {
          Debugger.Break();
        }
      });
      var priceHistory = ratesHistory.Select(price).ToList();
      var correlations = new ConcurrentDictionary<int, double>();
      Enumerable.Range(0, ratesHistory.Count() - interval).AsParallel().ForAll(i => {
        if(!ratesHistory[i].StartDate2.Between(dateRangeStart, dateRangeEnd)) {
          var range = new double[interval];
          priceHistory.CopyTo(i, range, 0, interval);
          if(isDateOk(ratesHistory[i].StartDate2) /*&& heightSample.Ratio(range.Height()) < 1.1*/)
            correlations.TryAdd(i, AlgLib.correlation.spearmanrankcorrelation(range, ratesSample, range.Length));
        }
      });
      var sorted = correlations.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
      Func<KeyValuePair<int, double>, KeyValuePair<int, double>, double, bool> compare = (d1, d2, d) => (d1.Key - d2.Key).Abs() <= d;
      var maxCorr1 = sorted.GroupBy(c => c, new ClosenessComparer<KeyValuePair<int, double>>(60, compare)).ToArray();
      var maxCorr2 = maxCorr1.Select(cg => cg.Key).ToArray();
      var avgIterations = 5;
      var maxCorr3 = maxCorr2.Take(avgIterations);
      maxCorr3.ForEach(kv => {
        var startDate = ratesHistory[kv.Key].StartDate2;
        try {
          var ds = startDate.Date + dateStart.TimeOfDay;
          var de = ds + (dateEnd - dateStart);
          GalaSoft.MvvmLight.Messaging.Messenger.Default.Send(
            new ShowSnapshotMatchMessage(ds, de/*, BarsCount*/, BarPeriodInt, kv.Value));
        } catch(Exception exc) {
          Log = new Exception(new { startDate } + "", exc);
        }
      });

    }
    void AdvanceSnapshot(DateTime? dateStart, DateTime? dateEnd, bool goBack = false) {
      var minutes = ((goBack ? -1 : 1) * BarsCountCalc * BarPeriodInt / 10).FromMinutes();
      if(SnapshotArguments.DateEnd != null)
        SnapshotArguments.DateEnd += minutes;
      else
        SnapshotArguments.DateStart += minutes;
      ShowSnaphot(SnapshotArguments.DateStart, SnapshotArguments.DateEnd);
    }
    IDisposable _scheduledSnapshot;
    void ShowSnaphot(DateTime? dateStart, DateTime? dateEnd) {
      var message = new List<string>();
      if(TradesManager == null)
        message.Add("TradesManager is null");
      if(dateStart == null && dateEnd == null)
        message.Add("SnapshotArguments.Date(Start and End) are null.");
      //if (dateStart != null && dateEnd != null) message.Add("SnapshotArguments.Date(Start or End) must be null.");
      if(message.Any()) {
        Log = new Exception(string.Join("\n", message));
        return;
      }
      RatesArray.Clear();
      UseRatesInternal(ri => ri.Clear());
      try {
        var rates = dateStart.HasValue && dateEnd.HasValue
          ? GlobalStorage.GetRateFromDBByDateRange(Pair, dateStart.Value, dateEnd.Value, BarPeriodInt).ToArray()
          : dateStart.HasValue
          ? GlobalStorage.GetRateFromDB(Pair, dateStart.Value, BarsCountCalc, BarPeriodInt).ToArray()
          : GlobalStorage.GetRateFromDBBackward(Pair, dateEnd.Value, BarsCountCalc, BarPeriodInt).ToArray();
        if(dateStart.HasValue && dateEnd.HasValue)
          _CorridorBarMinutes = rates.Count();
        UseRatesInternal(ri => {
          ri.AddRange(rates);
          while(ri.Count < BarsCountCalc)
            ri.Add(ri.LastBC());
          if(CorridorStartDate.HasValue && !CorridorStartDate.Value.Between(ri[0].StartDate, ri.LastBC().StartDate))
            CorridorStartDate = null;
        });
        Action doMatch = () => { };
        var doRunMatch = dateEnd.HasValue && !dateStart.HasValue && UseRatesInternal(ri => ri.LastBC()).Single().StartDate < dateEnd;
        if(doRunMatch) {
          UseRatesInternal(ri => {
            Enumerable.Range(0, CorridorDistanceRatio.ToInt()).ForEach(i => {
              ri.RemoveAt(0);
              var rate = ri.LastBC().Clone() as Rate;
              rate.StartDate2 = rate.StartDate2.AddMinutes(BarPeriodInt);
              ri.Add(rate);
            });
          });
          doMatch = MatchSnapshotRange;
          try {
            if(_scheduledSnapshot != null)
              _scheduledSnapshot.Dispose();
          } catch(Exception exc) { Log = exc; }
          _scheduledSnapshot = Scheduler.Default.Schedule(BarPeriodInt.FromMinutes(), () => {
            SnapshotArguments.RaiseShowSnapshot();
          });
        }
        Scheduler.Default.Schedule(() => {
          RaiseShowChart();
          doMatch();
        });
      } catch(Exception exc) {
        Log = exc;
      }
    }
    #endregion
    #region ctor
    [Import]
    static NewsCasterModel _newsCaster { get { return NewsCasterModel.Default; } }

    public TradingMacro() {
      this.ObservableForProperty(tm => tm.Pair, false, false)
        .Where(oc => !string.IsNullOrWhiteSpace(oc.Value) && !IsInVirtualTrading)
        .Throttle(1.FromSeconds())
        .ObserveOn(Application.Current.Dispatcher)
        .Subscribe(oc => {
          LoadActiveSettings();
          SubscribeToEntryOrderRelatedEvents();
        });
      _newsCaster.CountdownSubject
        .Where(nc => IsActive && Strategy != Strategies.None && nc.AutoTrade && nc.Countdown <= _newsCaster.AutoTradeOffset)
        .Subscribe(nc => {
          try {
            if(!RatesArray.Any())
              return;
            var height = CorridorStats.StDevByHeight;
            if(CurrentPrice.Average > MagnetPrice) {
              BuyLevel.Rate = MagnetPrice + height;
              SellLevel.Rate = MagnetPrice;
            } else {
              BuyLevel.Rate = MagnetPrice;
              SellLevel.Rate = MagnetPrice - height;
            }
            new[] { BuyLevel, SellLevel }.ForEach(sr => {
              sr.ResetPricePosition();
              sr.CanTrade = true;
              //sr.InManual = true;
            });
            DispatcherScheduler.Current.Schedule(5.FromSeconds(), () => nc.AutoTrade = false);
          } catch(Exception exc) { Log = exc; }
        });
      _waveShort = new WaveInfo(this);
      WaveShort.DistanceChanged += (s, e) => {
        OnPropertyChanged(() => WaveShortDistance);
        OnPropertyChanged(() => WaveShortDistanceInPips);
        _broadcastCorridorDateChanged();
      };
      //SuppRes.AssociationChanged += new CollectionChangeEventHandler(SuppRes_AssociationChanged);
      GalaSoft.MvvmLight.Messaging.Messenger.Default.Register<RequestPairForHistoryMessage>(this
        , a => {
          Debugger.Break();
          a.Pairs.Add(new Tuple<string, int>(this.Pair, this.BarPeriodInt));
        });
      GalaSoft.MvvmLight.Messaging.Messenger.Default.Register<CloseAllTradesMessage<TradingMacro>>(this, a => {
        if(a.Sender.YieldNotNull().Any(tm => tm.Pair == Pair))
          return;
        if(IsActive && TradesManager != null) {
          if(Trades.Any())
            CloseTrading("CloseAllTradesMessage sent by " + a.Sender.Pair);
          a.OnClose(this);
        }
      });
      GalaSoft.MvvmLight.Messaging.Messenger.Default.Register<TradeLineChangedMessage>(this, a => {
        if(a.Target == this && _strategyOnTradeLineChanged != null)
          _strategyOnTradeLineChanged(a);
      });
      GalaSoft.MvvmLight.Messaging.Messenger.Default.Register<ShowSnapshotMatchMessage>(this, m => {
        if(SnapshotArguments.IsTarget && !m.StopPropagation) {
          m.StopPropagation = true;
          SnapshotArguments.DateStart = m.DateStart;
          SnapshotArguments.DateEnd = null;
          SnapshotArguments.IsTarget = false;
          SnapshotArguments.Label = m.Correlation.ToString("n2");
          //if (BarsCount != m.BarCount) BarsCount = m.BarCount;
          if(BarPeriodInt != m.BarPeriod)
            BarPeriod = (BarsPeriodType)m.BarPeriod;
          UseRatesInternal(ri => ri.Clear());
          RatesArray.Clear();
          CorridorStartDate = null;
          ShowSnaphot(m.DateStart, m.DateEnd);
          Scheduler.Default.Schedule(1.FromSeconds(), () => {
            try {
              CorridorStartDate = m.DateStart;
              CorridorStopDate = DateTime.MinValue;// RatesArray.SkipWhile(r => r.StartDate < CorridorStartDate).Skip(m.DateEnd - 1).First().StartDate;
            } catch(Exception exc) {
              Log = exc;
            }
          });
          Scheduler.Default.Schedule(10.FromSeconds(), () => SnapshotArguments.IsTarget = true);
        }
      });
      //MessageBus.Current.Listen<AppExitMessage>().Subscribe(_ => SaveActiveSettings());
    }

    ~TradingMacro() {
      if(string.IsNullOrWhiteSpace(Pair))
        return;
      if(_TradesManager != null) {
        var fw = TradesManager as Order2GoAddIn.FXCoreWrapper;
        if(fw != null && fw.IsLoggedIn)
          fw.DeleteOrders(fw.GetEntryOrders(Pair, true));
      } else {
        Log = new Exception(new { _TradesManager } + "");
      }
    }
    #endregion

    #region SuppRes Event Handlers
    void SuppRes_AssociationChanged(object sender, CollectionChangeEventArgs e) {
      switch(e.Action) {
        case CollectionChangeAction.Add:
          ((Store.SuppRes)e.Element).CanTradeChanged += SuppRes_CanTradeChanged;
          ((Store.SuppRes)e.Element).RateChanged += SuppRes_RateChanged;
          ((Store.SuppRes)e.Element).RateChanging += SuppRes_RateChanging;
          ((Store.SuppRes)e.Element).Scan += SuppRes_Scan;
          ((Store.SuppRes)e.Element).SetLevelBy += SuppRes_SetLevelBy;
          ((Store.SuppRes)e.Element).IsActiveChanged += SuppRes_IsActiveChanged;
          ((Store.SuppRes)e.Element).EntryOrderIdChanged += SuppRes_EntryOrderIdChanged;
          break;
        case CollectionChangeAction.Refresh:
          ((IEnumerable<SuppRes>)sender).ToList()
            .ForEach(sr => {
              sr.CanTradeChanged += SuppRes_CanTradeChanged;
              sr.RateChanged += SuppRes_RateChanged;
              sr.RateChanging += SuppRes_RateChanging;
              sr.Scan += SuppRes_Scan;
              sr.SetLevelBy += SuppRes_SetLevelBy;
              sr.IsActiveChanged += SuppRes_IsActiveChanged;
              sr.EntryOrderIdChanged += SuppRes_EntryOrderIdChanged;
            });
          break;
        case CollectionChangeAction.Remove:
          ((Store.SuppRes)e.Element).CanTradeChanged -= SuppRes_CanTradeChanged;
          ((Store.SuppRes)e.Element).RateChanged -= SuppRes_RateChanged;
          ((Store.SuppRes)e.Element).RateChanging -= SuppRes_RateChanging;
          ((Store.SuppRes)e.Element).Scan -= SuppRes_Scan;
          ((Store.SuppRes)e.Element).SetLevelBy -= SuppRes_SetLevelBy;
          ((Store.SuppRes)e.Element).IsActiveChanged -= SuppRes_IsActiveChanged;
          ((Store.SuppRes)e.Element).EntryOrderIdChanged -= SuppRes_EntryOrderIdChanged;
          break;
      }
    }

    private void SuppRes_RateChanging(object sender, SuppRes.RateChangingEventArgs e) {
      var sr = (SuppRes)sender;
      if(sr.IsExitOnly || e.Prev == e.Next)
        return;
      var jump = e.Next.Abs(e.Prev);
      TradingMacroTrader()
        .Where(tm => jump / tm.RatesHeight > .15)
        .Take(1)
        .ForEach(_ => {
          sr.CanTrade = false;
          sr.TradesCount = 0;
          sr.ResetPricePosition();
        });
    }

    void SuppRes_SetLevelBy(object sender, EventArgs e) {
      SetLevelsBy(sender as SuppRes);
    }

    void SuppRes_Scan(object sender, EventArgs e) {
      if(IsInVirtualTrading)
        return;
      var sr = (sender as SuppRes);
      var rate = sr.Rate;
      var range = 15;
      var a = ScanCrosses(rate - PointSize * range, rate + PointSize * range);
      sr.Rate = a.OrderByDescending(t => t.Item1).First().Item2;
      RaiseShowChart();
    }

    #region ScanCrosses
    private List<Tuple<int, double>> ScanCrosses(double levelStart, double levelEnd, double stepInPips = 1) {
      return ScanCrosses(RatesArray, levelStart, levelEnd, stepInPips);
    }
    private List<Tuple<int, double>> ScanCrosses(IList<Rate> rates, double stepInPips = 1) {
      if(!rates.Any())
        return new List<Tuple<int, double>>();
      return ScanCrosses(rates, rates.Min(r => r.PriceAvg), rates.Max(r => r.PriceAvg), stepInPips);
    }
    private List<Tuple<int, double>> ScanCrosses(IList<Rate> rates, double levelStart, double levelEnd, double stepInPips = 1) {
      var step = PointSize * stepInPips;
      var steps = new List<double>();
      for(; levelStart <= levelEnd; levelStart += step)
        steps.Add(levelStart);
      return Partitioner.Create(steps).AsParallel().Select(s => new Tuple<int, double>(GetCrossesCount(rates, s), s)).ToList();
    }
    #endregion

    void SuppRes_EntryOrderIdChanged(object sender, SuppRes.EntryOrderIdEventArgs e) {
      var fw = GetFXWraper();
      if(!string.IsNullOrWhiteSpace(e.OldId) && fw != null)
        try {
          OnDeletingOrder(e.OldId);
          //fw.DeleteOrder(e.OldId);
        } catch(Exception exc) {
          Log = exc;
        }
    }

    void SuppRes_IsActiveChanged(object sender, EventArgs e) {
      try {
        var suppRes = (SuppRes)sender;
        var fw = GetFXWraper();
        if(fw != null && !suppRes.IsActive) {
          fw.GetEntryOrders(Pair, true).IsBuy(suppRes.IsBuy).ToList()
            .ForEach(o => fw.DeleteOrder(o.OrderID));
        }
      } catch(Exception exc) {
        Log = exc;
      }
    }

    void SuppRes_RateChanged(object sender, EventArgs e) {
      if(!IsInVirtualTrading)
        RaiseShowChart();
    }
    void SuppRes_CanTradeChanged(object sender, EventArgs e) {
      var sr = (SuppRes)sender;
      if(sr.CanTrade) {
        sr.RateCanTrade = sr.Rate;
        sr.DateCanTrade = ServerTime;
      }
    }
    #endregion

    public Guid SessionIdSuper { get; set; }
    static Guid _sessionId = Guid.NewGuid();
    public static Guid SessionId {
      get { return _sessionId; }
      set { _sessionId = value; }
    }
    public void ResetSessionId() { ResetSessionId(Guid.Empty); }
    public void ResetSessionId(Guid superId) {
      _sessionId = Guid.NewGuid();
      GlobalStorage.UseForexContext(c => {
        c.t_Session.Add(new DB.t_Session() { Uid = _sessionId, SuperUid = superId, Timestamp = DateTime.Now });
        c.SaveChanges();
      });
    }

    public string CompositeId { get { return Pair + "_" + PairIndex; } }

    public string CompositeName { get { return Pair + ":" + BarPeriod; } }

    void ReloadPairStats() {
      GlobalStorage.UseForexContext(f => {
        this.TimeFrameStats = f.s_GetBarStats(Pair, BarPeriodInt, BarsCountCalc, DateTime.Parse("2008-01-01")).ToArray();
      });
    }

    #region MonthStatistics
    class MonthStatistics : Models.ModelBase {
      #region MonthLow
      private DateTime _MonthLow;
      public DateTime MonthLow {
        get { return _MonthLow; }
        set {
          if(_MonthLow != value) {
            _MonthLow = value;
            _stats = null;
          }
        }
      }
      #endregion
      #region MonthHigh
      private DateTime _MonthHigh;
      public DateTime MonthHigh {
        get { return _MonthHigh; }
        set {
          if(_MonthHigh != value) {
            _MonthHigh = value;
            _stats = null;
          }
        }
      }
      #endregion
      #region Hour
      private int _Hour;
      public int Hour {
        get { return _Hour; }
        set {
          if(_Hour != value) {
            _Hour = value;
            _heightMin = double.NaN;
          }
        }
      }

      #endregion
      readonly IList<DB.s_GetBarStats_Result> _dbStats;
      public MonthStatistics(IList<DB.s_GetBarStats_Result> dbStats) {
        _dbStats = dbStats;
      }
      IList<DB.s_GetBarStats_Result> _stats;
      IList<DB.s_GetBarStats_Result> GetStats(DateTime date) {
        this.MonthLow = date.Round(MathExtensions.RoundTo.Day).AddDays(-3);
        this.MonthHigh = date.Round(MathExtensions.RoundTo.Day);
        return _stats ?? (_stats = _dbStats.Where(s => s.StopDateMonth.Value.Between(MonthLow, MonthHigh)).ToArray());
      }
      double _heightMin = double.NaN;
      public double GetHeightMin(DateTime date) {
        Hour = date.Hour;
        var hourHigh = (24 + Hour + 2) % 24;
        var hourLow = (24 + Hour - 2) % 24;
        Func<int, bool> compare = (hour) => {
          if(hourLow < hourHigh)
            return hour.Between(hourLow, hourHigh);
          return !hour.Between(hourLow, hourHigh);
        };
        if(double.IsNaN(_heightMin)) {
          var a = GetStats(date).Where(s => s.StopDateMonth < date.Date || s.StopDateHour < Hour);
          a = a.Where(s => compare(s.StopDateHour.Value));
          var b = a.Select(s => s.BarsHeightAvg.Value).DefaultIfEmpty(double.NaN).ToArray();
          var avg = b.Average();
          var stDev = b.StDev();
          var d = b.Where(v => v < avg + stDev).Average();
          _heightMin = d;// a.Select(s => s.BarsHeightAvg).DefaultIfEmpty(double.NaN).Average().Value;
        }
        return _heightMin;
      }
    }
    MonthStatistics _MonthStats;
    #endregion

    partial void OnPairChanged() {
      _inPips = null;
      _pointSize = double.NaN;
      _pipCost = double.NaN;
      _BaseUnitSize = 0;
      GlobalStorage.UseForexContext(f => {
        this._blackoutTimes = f.v_BlackoutTime.ToArray();
      });
      _pendingEntryOrders = new MemoryCache(Pair);
      OnPropertyChanged(TradingMacroMetadata.CompositeName);
    }
    partial void OnLimitBarChanged() { OnPropertyChanged(TradingMacroMetadata.CompositeName); }

    public bool IsBlackoutTime {
      get {
        var BlackoutHoursTimeframe = 0;
        if(BlackoutHoursTimeframe == 0)
          return false;
        var r = _blackoutTimes.Any(b => RateLast.StartDate.Between(b.Time.AddHours(-BlackoutHoursTimeframe), b.Time));
        return r;
        //return _blackoutTimes.Where(b => RateLast.StartDate.Between(b.TimeStart.Value.LocalDateTime, b.TimeStop.LocalDateTime)).Any();
      }
    }

    #region LotSize
    int _lotSize;
    public int LotSize {
      get { return _lotSize; }
      set {
        if(_lotSize == value)
          return;
        _lotSize = value;
        OnPropertyChanged("LotSize");
      }
    }

    private double _LotSizePercent;
    public double LotSizePercent {
      get { return _LotSizePercent; }
      set {
        if(_LotSizePercent != value) {
          _LotSizePercent = value;
          OnPropertyChanged("LotSizePercent");
        }
      }
    }

    private int _LotSizeByLossBuy;
    public int LotSizeByLossBuy {
      get { return _LotSizeByLossBuy; }
      set {
        if(_LotSizeByLossBuy != value) {
          _LotSizeByLossBuy = value;
          OnPropertyChanged("LotSizeByLossBuy");
          OnPropertyChanged("PipAmount");
        }
      }
    }
    private int _LotSizeByLossSell;
    public int LotSizeByLossSell {
      get { return _LotSizeByLossSell; }
      set {
        if(_LotSizeByLossSell != value) {
          _LotSizeByLossSell = value;
          OnPropertyChanged("LotSizeByLossSell");
        }
      }
    }
    int _currentLot;
    public int CurrentLot {
      get { return _currentLot; }
      set {
        if(_currentLot == value)
          return;
        _currentLot = value;
        OnPropertyChanged("CurrentLot");
      }
    }
    #endregion

    double TakeProfitInDollars { get { return TakeProfitPips * LotSize / 10000; } }
    private double _TakeProfitPips;
    public double TakeProfitPips {
      get { return _TakeProfitPips; }
      set {
        if(_TakeProfitPips != value) {
          if(!_useTakeProfitMin || value < _TakeProfitPips) {
            _TakeProfitPips = value;
            OnPropertyChanged("TakeProfitPips");
          }
        }
      }
    }
    #region Corridor Stats



    public IEnumerable<CorridorStatistics> GetCorridorStats() { return CorridorStatsArray.OrderBy(cs => cs.Iterations); }
    public CorridorStatistics GetCorridorStats(int iterations) {
      if(iterations <= 0)
        return CorridorStatsArray.OrderBy(c => c.Iterations).Take(-iterations + 1).Last();
      var cs = CorridorStatsArray.Where(c => c.Iterations == iterations).SingleOrDefault();
      if(cs == null) {
        GalaSoft.MvvmLight.Threading.DispatcherHelper.UIDispatcher
          .Invoke(new Action(() => {
            CorridorStatsArray.Add(new CorridorStatistics(this));
          }));
        CorridorStatsArray.Last().Iterations = iterations;
        return CorridorStatsArray.Last();
      }
      return cs;
    }

    partial void OnGannAnglesOffsetChanged() {
      if(RatesArraySafe.Count() > 0) {
        var rateLast = RatesArraySafe.Last();
        if(CorridorStats != null) {
          SetGannAngles();
          var slope = CorridorStats.Slope;
          Predicate<double> filter = ga => slope < 0 ? rateLast.PriceAvg > ga : rateLast.PriceAvg < ga;
          var index = GetGannAngleIndex(GannAngleActive);// GetGannIndex(rateLast, slope);
          if(index >= 0)
            GannAngleActive = index;
          //else
          //  Debugger.Break();
        }
      }
      OnPropertyChanged(Metadata.TradingMacroMetadata.GannAnglesOffset_);
    }

    private static int GetGannIndex(Rate rateLast, double slope) {
      var gann = slope > 0
        ? rateLast.GannPrices.Where(ga => rateLast.PriceAvg > ga).DefaultIfEmpty().Max()
        : rateLast.GannPrices.Where(ga => rateLast.PriceAvg < ga).DefaultIfEmpty().Min();
      var index = rateLast.GannPrices.ToList().IndexOf(gann);
      return index;
    }

    partial void OnGannAnglesChanged() {
      _gannAngles = GannAnglesList.FromString(GannAngles).Where(a => a.IsOn).Select(a => a.Value).ToList();
      OnPropertyChanged("GannAngles_");
      return;
      _gannAngles = GannAngles.Split(',')
        .Select(a => (double)System.Linq.Dynamic.DynamicExpression.ParseLambda(new ParameterExpression[0], typeof(double), a).Compile().DynamicInvoke())
        .ToList();
    }
    List<double> _gannAngles = new List<double>();
    public List<double> GannAnglesArray { get { return _gannAngles; } }

    public double Slope { get { return CorridorStats == null ? 0 : CorridorStats.Slope; } }
    public int GetGannAngleIndex(int indexOld) {
      return -1;
      var ratesForGann = ((IList<Rate>)SetGannAngles()).Reverse().ToList();
      if(Slope != 0 && ratesForGann.Count > 0) {
        var testList = new List<Tuple<Rate, Rate>>();
        ratesForGann.Aggregate((rp, rn) => {
          testList.Add(new Tuple<Rate, Rate>(rp, rn));
          return rn;
        });
        Func<Rate, Rate, int, bool> cross1 = (r1, r2, gannIndex) => {
          var gannLow = Math.Min(r1.GannPrices[gannIndex], r2.GannPrices[gannIndex]);
          var gannHigh = Math.Max(r1.GannPrices[gannIndex], r2.GannPrices[gannIndex]);
          var ask = Math.Max(r1.PriceHigh, r2.PriceHigh);
          var bid = Math.Min(r1.PriceLow, r2.PriceLow);
          return gannLow < ask && gannHigh > bid;
        };
        Func<Rate, Rate, int> cross2 = (rp, rn) =>
          rn.GannPrices.Select((gp, i) =>
            new { i, cross = cross1(rp, rn, i) })
            .Where(a => a.cross).DefaultIfEmpty(new { i = -1, cross = false }).Last().i;
        Predicate<Tuple<Rate, Rate>> cross3 = t => cross2(t.Item1, t.Item2) >= 0;
        var rateCross = testList.Find(cross3);
        if(rateCross != null && (_rateGannCurrentLast == null || _rateGannCurrentLast < rateCross.Item2)) {
          _rateGannCurrentLast = rateCross.Item2;
          if(rateCross != null)
            return cross2(rateCross.Item1, rateCross.Item2);
        }
        return indexOld;
      }
      return -1;
    }

    void cs_PropertyChanged(object sender, PropertyChangedEventArgs e) {
      var cs = (sender as CorridorStatistics);
      switch(e.PropertyName) {
        case Metadata.CorridorStatisticsMetadata.StartDate:
          if(!IsGannAnglesManual)
            SetGannAngleOffset(cs);
          break;
      }
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    private void SetGannAngleOffset(CorridorStatistics cs) {
      GannAnglesOffset = cs.Slope.Abs() / GannAngle1x1;
    }
    private ObservableCollection<CorridorStatistics> _CorridorStatsArray;
    public ObservableCollection<CorridorStatistics> CorridorStatsArray {
      get {
        if(_CorridorStatsArray == null) {
          _CorridorStatsArray = new ObservableCollection<CorridorStatistics>();
          _CorridorStatsArray.CollectionChanged += new System.Collections.Specialized.NotifyCollectionChangedEventHandler(CorridorStatsArray_CollectionChanged);
        }
        //  _CorridorStatsArray = new CorridorStatistics[] { new CorridorStatistics(this), new CorridorStatistics(this), new CorridorStatistics(this) };
        return _CorridorStatsArray;
      }
      set {
        if(_CorridorStatsArray != value) {
          _CorridorStatsArray = value;
          OnPropertyChanged("CorridorStatsArray");
        }
      }
    }

    void CorridorStatsArray_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e) {
      if(e.Action == NotifyCollectionChangedAction.Add)
        (e.NewItems[0] as CorridorStatistics).PropertyChanged += cs_PropertyChanged;
    }

    CorridorStatistics _corridorBig;
    public CorridorStatistics CorridorBig {
      get { return _corridorBig ?? new CorridorStatistics(); }
      set {
        if(_corridorBig == value)
          return;
        _corridorBig = value;
      }
    }



    public bool HasCorridor { get { return CorridorStats.IsCurrent; } }
    private CorridorStatistics _CorridorStats;
    public CorridorStatistics CorridorStats {
      get { return _CorridorStats ?? new CorridorStatistics(); }
      set {
        _CorridorStats = value;
        if(_CorridorStats != null) {
          _CorridorStats.PeriodsJumped += CorridorStats_PeriodsJumped;
        }


        if(value != null && RatesArray.Count > 0) {
          if(false && !IsGannAnglesManual)
            SetGannAngleOffset(value);
          UpdateTradingGannAngleIndex();
        }
        //}

        #region PropertyChanged
        OnPropertyChanged(TradingMacroMetadata.CorridorStats);
        OnPropertyChanged(TradingMacroMetadata.HasCorridor);
        #endregion
      }
    }

    void CorridorStats_PeriodsJumped(object sender, EventArgs e) {
      if(false && HasCorridor)
        ForceOpenTrade = CorridorStats.Slope < 0;
    }
    public void UpdateTradingGannAngleIndex() {
      if(CorridorStats == null)
        return;
      int newIndex = GetGannAngleIndex(GannAngleActive);
      if(true || newIndex > GannAngleActive)
        GannAngleActive = newIndex;
    }

    private int GetGannAngleIndex_() {
      var rateLast = RatesArraySafe.Last();
      Predicate<double> filter = ga => CorridorStats.Slope > 0 ? rateLast.PriceAvg < ga : rateLast.PriceAvg > ga;
      return rateLast.GannPrices.ToList().FindLastIndex(filter);
    }

    public List<Rate> SetGannAngles() {
      return new List<Rate>();
      if(true || CorridorStats == null)
        return new List<Rate>();
      RatesArraySafe.ToList().ForEach(r => Enumerable.Range(0, GannAnglesArray.Count).ToList()
        .ForEach(i => { if(r.GannPrices.Length > i) r.GannPrices[i] = 0; }));
      var ratesForGann = RatesArraySafe.SkipWhile(r => r.StartDate < this.GannAnglesAnchorDate.GetValueOrDefault(CorridorStats.StartDate)).ToList();
      var rateStart = this.GannAnglesAnchorDate.GetValueOrDefault(new Func<DateTime>(() => {
        var rateStop = Slope > 0 ? ratesForGann.OrderBy(r => r.PriceAvg).LastOrDefault() : ratesForGann.OrderBy(r => r.PriceAvg).FirstOrDefault();
        if(rateStop == null)
          return DateTime.MinValue;
        var ratesForStart = ratesForGann.Where(r => r < rateStop);
        if(ratesForStart.Count() == 0)
          ratesForStart = ratesForGann;
        return (CorridorStats.Slope > 0 ? ratesForStart.OrderBy(CorridorStats.priceLow).First() : ratesForStart.OrderBy(CorridorStats.priceHigh).Last()).StartDate;
      })());
      ratesForGann = ratesForGann.Where(r => r.StartDate >= rateStart).OrderBars().ToList();
      if(ratesForGann.Count == 0)
        return new List<Rate>();
      //var interseption = Slope > 0 ? Math.Min(ratesForGann[0].PriceAvg3, ratesForGann[0].PriceLow) : Math.Max(ratesForGann[0].PriceAvg2, ratesForGann[0].PriceHigh);
      var interseption = Slope > 0 ? ratesForGann[0].PriceLow : ratesForGann[0].PriceHigh;
      Enumerable.Range(0, ratesForGann.Count()).AsParallel().ForAll(i => {
        var rate = ratesForGann[i];
        if(rate.GannPrices.Length != GannAnglesArray.Count)
          rate.GannPrices = new double[GannAnglesArray.Count];
        for(var j = 0; j < GannAnglesArray.Count; j++) {
          double tangent = GannAnglesArray[j] * PointSize * GannAnglesOffset.GetValueOrDefault();
          var coeffs = new[] { interseption, Math.Sign(CorridorStats.Slope) * tangent };
          rate.GannPrices[j] = coeffs.RegressionValue(i);
        }
      });
      for(var i = 0; i < ratesForGann.Count(); i++) {
      }
      return ratesForGann;
    }
    public double GannAngle1x1 { get { return PointSize; } }
    private int _GannAngleActive = -1;
    /// <summary>
    /// Index of active Gann Angle
    /// </summary>
    public int GannAngleActive {
      get { return _GannAngleActive; }
      set { _GannAngleActive = value; }
    }
    double GannPriceForTrade() { return GannPriceForTrade(GetLastRateWithGannAngle()); }
    double GannPriceForTrade(Rate rateLast) {
      if(GannAngleActive >= 0 && rateLast.GannPrices.Length > GannAngleActive && GannAngleActive.Between(0, GannAnglesArray.Count - 1))
        return rateLast.GannPrices[GannAngleActive];
      return double.NaN;
    }

    //Dimok: Need to implement FindTrendAngle
    void FindTrendAngle(ICollection<Rate> rates) {


    }
    #endregion

    #region Correlations
    public double Correlation_P;
    public double Correlation_R;

    public double Correlation {
      get {
        return (Correlation_P + Correlation_R) / 2;
        return new double[] { Correlation_P, Correlation_R }.OrderBy(c => c.Abs()).First();
      }
    }

    public void SetCorrelation(string currency, double correlation) {
      if(Currency1 == currency)
        Correlation1 = correlation;
      if(Currency2 == currency)
        Correlation2 = correlation;
    }

    public string Currency1 { get { return (Pair + "").Split('/').DefaultIfEmpty("").ToArray()[0]; } }
    public string Currency2 { get { return (Pair + "").Split('/').Skip(1).DefaultIfEmpty("").ToArray()[0]; } }

    private double _Correlation1;
    public double Correlation1 {
      get { return _Correlation1; }
      set {
        if(_Correlation1 != value) {
          _Correlation1 = value;
          OnPropertyChanged("Correlation1");
        }
      }
    }

    private double _Correlation2;
    public double Correlation2 {
      get { return _Correlation2; }
      set {
        if(_Correlation2 != value) {
          _Correlation2 = value;
          OnPropertyChanged("Correlation2");
        }
      }
    }
    #endregion

    #region Overlap
    public int OverlapTotal { get { return Overlap.ToInt() + Overlap5; } }

    double _overlap;
    public double Overlap {
      get { return _overlap; }
      set {
        if(_overlap == value)
          return;
        _overlap = value;
        OnPropertyChanged("Overlap");
        OnPropertyChanged("OverlapTotal");
      }
    }

    int _overlap5;
    public int Overlap5 {
      get { return _overlap5; }
      set {
        if(_overlap5 == value)
          return;
        _overlap5 = value;
        OnPropertyChanged(TradingMacroMetadata.Overlap5);
        OnPropertyChanged(TradingMacroMetadata.OverlapTotal);
      }
    }
    #endregion

    #region TicksPerMinute
    public double TicksPerMinuteInstant { get { return PriceQueue.TickPerMinute(.25); } }
    public double TicksPerMinute { get { return PriceQueue.TickPerMinute(.5); } }
    void SetTicksPerSecondAverage(double tpc) { _ticksPerSecondAverage = tpc; }
    double _ticksPerSecondAverage = 0;
    public double TicksPerSecondAverage { get { return _ticksPerSecondAverage; } }

    int priceQueueCount = 600;
    public class TicksPerPeriod {
      Queue<Price> priceStackByPair = new Queue<Price>();
      int maxCount;
      public TicksPerPeriod(int maxCount) {
        this.maxCount = maxCount;
      }
      private IEnumerable<Price> GetQueue(double period) {
        lock(priceStackByPair) {
          if(period <= 1)
            period = (priceStackByPair.Count * period).ToInt();
          return priceStackByPair.Take(period.ToInt());
        }
      }
      public void Add(Price price, DateTime serverTime) {
        lock(priceStackByPair) {
          var queue = priceStackByPair;
          if((price.Time - serverTime).Duration() < TimeSpan.FromMinutes(1)) {
            if(queue.Count > maxCount)
              queue.Dequeue();
            queue.Enqueue(price);
          }
        }
      }
      public double TickPerMinute(double period) {
        return TickPerMinute(GetQueue(period));
      }

      public DateTime LastTickTime() {
        lock(priceStackByPair) {
          return priceStackByPair.Count == 0 ? DateTime.MaxValue : priceStackByPair.Max(p => p.Time);
        }
      }
      private static double TickPerMinute(IEnumerable<Price> queue) {
        if(queue.Count() < 10)
          return 10;
        var totalMinutes = (queue.Max(p => p.Time) - queue.Min(p => p.Time)).TotalMinutes;
        return queue.Count() / Math.Max(1, totalMinutes);
      }
      public double Speed(double period) {
        return Speed(GetQueue(period));
      }
      public static double Speed(IEnumerable<Price> queue) {
        if(queue.Count() < 2)
          return 0;
        var distance = 0.0;
        for(var i = 1; i < queue.Count(); i++)
          distance += (queue.ElementAt(i).Average - queue.ElementAt(i - 1).Average).Abs();
        var totalMinutes = (queue.Max(p => p.Time) - queue.Min(p => p.Time)).TotalMinutes;
        return totalMinutes == 0 ? 0 : distance / totalMinutes;
      }
    }

    TicksPerPeriod _PriceQueue;
    public TicksPerPeriod PriceQueue {
      get {
        if(_PriceQueue == null)
          _PriceQueue = new TicksPerPeriod(priceQueueCount);
        return _PriceQueue;
      }
    }
    public void TicksPerMinuteSet(Price price, DateTime serverTime) {
      PriceQueue.Add(price, serverTime);
      OnPropertyChanged(TradingMacroMetadata.TicksPerMinuteInstant);
      OnPropertyChanged(TradingMacroMetadata.TicksPerMinute);
      OnPropertyChanged(TradingMacroMetadata.PipsPerMinute);
      OnPropertyChanged(TradingMacroMetadata.PipsPerMinuteCmaFirst);
      OnPropertyChanged(TradingMacroMetadata.PipsPerMinuteCmaLast);
      OnPropertyChanged(TradingMacroMetadata.CurrentGross);
      OnPropertyChanged(TradingMacroMetadata.CurrentGrossInPips);
      OnPropertyChanged(TradingMacroMetadata.OpenTradesGross);
      OnPropertyChanged(TradingMacroMetadata.OpenTradesGross2);
      SyncSubject.OnNext(this);
    }
    #endregion

    public double PipsPerMinute { get { return InPips(PriceQueue.Speed(.25)); } }
    public double PipsPerMinuteCmaFirst { get { return InPips(PriceQueue.Speed(.5)); } }
    public double PipsPerMinuteCmaLast { get { return InPips(PriceQueue.Speed(1)); } }

    public double OpenTradesGross {
      get { return Trades.Gross() - (TradesManager == null ? 0 : TradesManager.CommissionByTrades(Trades)); }
    }
    public double OpenTradesGross2 { get { return OpenTradesGross - (TradesManager == null ? 0 : TradesManager.CommissionByTrades(Trades)); } }
    public double OpenTradesGross2InPips { get { return TradesManager.MoneyAndLotToPips(OpenTradesGross2, Trades.Lots(), Pair); } }

    partial void OnCurrentLossChanged() {
      if(!IsTrader && _CurrentLoss != 0)
        CurrentLoss = 0;
    }
    public double CurrentGross {
      get { return !IsTrader ? 0 : CurrentLoss + OpenTradesGross; }
    }

    public int CurrentGrossLot { get { return !IsTrader ? 0 : Trades.Select(t => t.Lots).DefaultIfEmpty(AllowedLotSizeCore()).Sum(); } }
    public double CurrentGrossInPips {
      get {
        return TradesManager == null ? double.NaN : TradesManager.MoneyAndLotToPips(CurrentGross, CurrentGrossLot, Pair);
      }
    }

    public double CurrentLossInPips {
      get { return TradesManager == null ? double.NaN : TradesManager.MoneyAndLotToPips(CurrentLoss, CurrentGrossLot, Pair); }
    }
    private double CurrentLossInPipTotal { get { return TradingStatistics.CurrentLossInPips; } }
    public double CurrentGrossInPipTotal {
      get {
        return _tradingStatistics.CurrentGrossInPips;
      }
    }

    public int PositionsBuy { get { return Trades.IsBuy(true).Length; } }
    public int PositionsSell { get { return Trades.IsBuy(false).Length; } }

    public double PipsPerPosition {
      get {
        var trades = Trades.ToArray();
        return trades.Length < 2 ? (trades.Length == 1 ? trades[0].PL : 0) : InPips(trades.Max(t => t.Open) - trades.Min(t => t.Open)) / (trades.Length - 1);
      }
    }

    private int _HistoricalGrossPL;
    public int HistoricalGrossPL {
      get { return _HistoricalGrossPL; }
      set {
        if(_HistoricalGrossPL != value) {
          _HistoricalGrossPL = value;
          OnPropertyChanged("HistoricalGrossPL");
        }
      }
    }

    struct TradeSignal {
      public double OpenPrice { get; set; }
      public double ClosePrice { get; set; }
      public bool IsActive { get; set; }
    }

    public double CalculateCloseProfitInPips() {
      return InPips(CalculateCloseProfit());
    }
    public double CalculateCloseProfit() {
      switch(Strategy) {
        default:
          return CalculateTakeProfit();
      }
    }
    public double CalculateCloseLossInPips() {
      return InPips(CalculateCloseLoss());
    }
    public double CalculateCloseLoss() {
      switch(Strategy) {
        default:
          return -CalculateTakeProfit();
      }
    }

    #region Last Rate
    private Rate GetLastRateWithGannAngle() {
      return GetLastRate(RatesArraySafe.SkipWhile(r => r.GannPrices.Length == 0).TakeWhile(r => r.GannPrices.Length > 0).ToList());
    }
    private Rate GetLastRate() { return GetLastRate(RatesArraySafe); }
    private Rate GetLastRate(ICollection<Rate> rates) {
      if(!rates.Any())
        return null;
      var rateLast = rates.Skip(rates.Count - 2)
        .LastOrDefault(LastRateFilter);
      return rateLast ?? rates.Last();
    }

    private bool LastRateFilter(Rate r) {
      return r.StartDate <= CurrentPrice.Time - TimeSpan.FromMinutes((int)BarPeriod);
    }
    #endregion

    #region Price Funcs

    static Func<Rate, double> gannPriceHigh = rate => rate.PriceAvg;
    static Func<Rate, double> gannPriceLow = rate => rate.PriceAvg;

    static Func<Rate, double> suppResPriceHigh = rate => rate.PriceHigh;
    static Func<Rate, double> suppResPriceLow = rate => rate.PriceLow;
    #endregion

    static object _tradesFromReportLock = new object();
    static List<Trade> _tradesFromReport;
    List<Trade> tradesFromReport {
      get {
        lock(_tradesFromReportLock) {
          if(_tradesFromReport == null)
            _tradesFromReport = GetFXWraper().GetTradesFromReport(DateTime.Now.AddDays(-7), DateTime.Now);
        }
        return _tradesFromReport;
      }
    }
    #region TradesManager 'n Stuff
    private IDisposable _priceChangedSubscribsion;
    public IDisposable PriceChangedSubscribsion {
      get { return _priceChangedSubscribsion; }
      set {
        if(_priceChangedSubscribsion == value)
          return;
        if(_priceChangedSubscribsion != null)
          _priceChangedSubscribsion.Dispose();
        _priceChangedSubscribsion = value;
      }
    }
    EventHandler<TradeEventArgs> TradeCloseHandler {
      get {
        return TradesManager_TradeClosed;
      }
    }

    EventHandler<TradeEventArgs> TradeAddedHandler {
      get {
        return TradesManager_TradeAddedGlobal;
      }
    }

    delegate double InPipsDelegate(string pair, double? price);
    InPipsDelegate _inPips;
    public double InPips(double? d) {
      if(_inPips == null && TradesManager != null)
        _inPips = TradesManager.InPips;
      return _inPips == null ? double.NaN : _inPips(Pair, d);
    }

    public int Digits() { return TradesManager == null ? 0 : TradesManager.GetDigits(Pair); }
    private const int RatesHeightMinimumOff = 0;
    IEnumerable<TradingMacro> _tradingMacros = new TradingMacro[0];
    Func<ITradesManager> _TradesManager = () => null;
    public ITradesManager TradesManager { get { return _TradesManager(); } }
    public void SubscribeToTradeClosedEVent(Func<ITradesManager> getTradesManager, IEnumerable<TradingMacro> tradingMacros) {
      _tradingMacros = tradingMacros;
      Action<Expression<Func<TradingMacro, bool>>> check = g => TradingMacrosByPair()
        .Scan(0, (t, tm) => t + (g.Compile()(tm) ? 1 : 0))
        .SkipWhile(c => c <= 1)
        .Take(1)
        .ForEach(c => { throw new Exception(Lib.GetLambda(g) + " is set in more then one TradingMacro"); });
      check(tm => tm.IsTrader);
      //check(tm => tm.IsTrender);
      TradingMacrosByPair()
        .Where(tm => tm.IsTrender)
        .IfEmpty(new Action(() => { throw new Exception("Pair " + Pair + " does not have any Trenders"); }));
      TradingMacrosByPair()
        .GroupBy(tm => tm.PairIndex)
        .Where(g => g.Count() > 1)
        .ForEach(g => { throw new Exception("PairIndex " + g.Key + " is user in more that one " + g.First().Pair); });
      _inPips = null;
      if(getTradesManager == null)
        if(Debugger.IsAttached)
          Debugger.Break();
        else
          Log = new Exception(new { getTradesManager } + "");
      this._TradesManager = () => getTradesManager();
      this.TradesManager.TradeClosed += TradeCloseHandler;
      this.TradesManager.TradeAdded += TradeAddedHandler;
      var fw = GetFXWraper();
      var digits = TradesManager.GetDigits(Pair);
      var a = Observable.FromEventPattern<EventHandler<PriceChangedEventArgs>
        , PriceChangedEventArgs>(h => h, h => TradesManager.PriceChanged += h, h => TradesManager.PriceChanged -= h)
        .Where(pce => pce.EventArgs.Pair == Pair)
        //.Sample((0.1).FromSeconds())
        //.DistinctUntilChanged(pce => pce.EventArgs.Price.Average.Round(digits))
        .Do(pce => {
          try {
            CurrentPrice = pce.EventArgs.Price;
            if(!TradesManager.IsInTest && !IsInPlayback)
              AddCurrentTick(pce.EventArgs.Price);
            TicksPerMinuteSet(pce.EventArgs.Price, ServerTime);
            OnPropertyChanged(TradingMacroMetadata.PipsPerPosition);
          } catch(Exception exc) { Log = exc; }
        });
      if(!IsInVirtualTrading)
        PriceChangedSubscribsion = a.SubscribeToLatestOnBGThread(pce => RunPriceChanged(pce.EventArgs, null), exc => MessageBox.Show(exc + ""), () => Log = new Exception(Pair + " got terminated."));
      else
        PriceChangedSubscribsion = a.Subscribe(pce => RunPriceChanged(pce.EventArgs, null), exc => MessageBox.Show(exc + ""), () => Log = new Exception(Pair + " got terminated."));

      if(fw != null && !IsInPlayback) {
        fw.CoreFX.LoggingOff += CoreFX_LoggingOffEvent;
        fw.OrderAdded += TradesManager_OrderAdded;
        fw.OrderChanged += TradesManager_OrderChanged;
        fw.OrderRemoved += TradesManager_OrderRemoved;
        if(isLoggedIn) {
          RunningBalance = tradesFromReport.ByPair(Pair).Sum(t => t.NetPL);
          CalcTakeProfitDistance();
        }
      }
      RaisePositionsChanged();
      Strategy = IsInVirtualTrading ? Strategies.UniversalA : Strategies.Universal;
      IsTradingActive = IsInVirtualTrading;
    }

    void TradesManager_OrderChanged(object sender, OrderEventArgs e) {
      if(!IsMyOrder(e.Order) || !e.Order.IsNetOrder)
        return;
      CalcTakeProfitDistance();
    }

    void CoreFX_LoggingOffEvent(object sender, Order2GoAddIn.LoggedInEventArgs e) {
      var fw = GetFXWraper();
      if(fw == null)
        return;
      fw.GetEntryOrders(Pair, true).ToList().ForEach(o => fw.DeleteOrder(o.OrderID, false));
    }

    void TradesManager_OrderAdded(object sender, OrderEventArgs e) {
      if(!IsMyOrder(e.Order))
        return;
      if(e.Order.IsEntryOrder) {
        EnsureActiveSuppReses();
        try {
          var key = "EO";
          ReleasePendingAction(key);
        } catch(Exception exc) {
          Log = exc;
        }
      }
      try {
        TakeProfitDistance = CalcTakeProfitDistance();
        var order = e.Order;
        var fw = GetFXWraper();
        if(fw != null && !order.IsNetOrder) {
          var orders = GetEntryOrders();
          orders.IsBuy(true).OrderBy(o => o.OrderID).Skip(1)
            .Concat(orders.IsBuy(false).OrderBy(o => o.OrderID).Skip(1))
            .ToList().ForEach(o => OnDeletingOrder(o.OrderID));
        }
      } catch(Exception exc) {
        Log = exc;
      }
    }

    void TradesManager_OrderRemoved(Order order) {
      if(!IsMyOrder(order))
        return;
      EnsureActiveSuppReses();
      SuppRes.Where(sr => sr.EntryOrderId == order.OrderID).ToList().ForEach(sr => sr.EntryOrderId = Store.SuppRes.RemovedOrderTag);
    }

    void TradesManager_TradeAddedGlobal(object sender, TradeEventArgs e) {
      try {
        if(!IsMyTrade(e.Trade))
          return;
        ReleasePendingAction("OT");
        EnsureActiveSuppReses();
        RaisePositionsChanged();
        if(_strategyExecuteOnTradeOpen != null)
          _strategyExecuteOnTradeOpen(e.Trade);
      } catch(Exception exc) {
        Log = exc;
      }
    }

    bool IsMyTrade(Trade trade) { return trade.Pair == Pair && IsTrader; }
    bool IsMyOrder(Order order) { return order.Pair == Pair && IsTrader; }
    public void UnSubscribeToTradeClosedEVent(ITradesManager tradesManager) {
      if(PriceChangedSubscribsion != null)
        PriceChangedSubscribsion.Dispose();
      PriceChangedSubscribsion = null;
      if(this.TradesManager != null) {
        this.TradesManager.TradeClosed -= TradeCloseHandler;
        this.TradesManager.TradeAdded -= TradeAddedHandler;
      }
      if(tradesManager != null) {
        tradesManager.TradeClosed -= TradeCloseHandler;
        tradesManager.TradeAdded -= TradeAddedHandler;
      }
    }
    void TradesManager_TradeClosed(object sender, TradeEventArgs e) {
      if(!IsMyTrade(e.Trade))
        return;
      if(HistoryMaximumLot > 0) {
        CurrentLot = Trades.Sum(t => t.Lots);
        CloseAtZero = false;
        EnsureActiveSuppReses();
        RaisePositionsChanged();
        ReleasePendingAction("CT");
        if(_strategyExecuteOnTradeClose != null)
          _strategyExecuteOnTradeClose(e.Trade);
      }
    }


    private void RaisePositionsChanged() {
      OnPropertyChanged("PositionsSell");
      OnPropertyChanged("PositionsBuy");
      OnPropertyChanged("PipsPerPosition");
      RaiseShowChart();
    }
    string[] sessionInfoCategories = new[] { categoryActive, categoryActiveYesNo, categorySession, categoryActiveFuncs };
    string _sessionInfo = "";
    public string SessionInfo {
      get {
        var separator = "\t";
        if(string.IsNullOrEmpty(_sessionInfo)) {
          var l = new List<string>();
          foreach(var p in GetType().GetProperties()) {
            var ca = p.GetCustomAttributes(typeof(CategoryAttribute), false).FirstOrDefault() as CategoryAttribute;
            if(ca != null && sessionInfoCategories.Contains(ca.Category)) {
              l.Add(p.Name + separator + p.GetValue(this, null));
            }
          }
          l.Add("TestFileName" + separator + TestFileName);
          _sessionInfo = string.Join(",", l);
        }
        return _sessionInfo;
      }
    }
    bool HasShutdownStarted { get { return ((bool)GalaSoft.MvvmLight.Threading.DispatcherHelper.UIDispatcher.Invoke(new Func<bool>(() => GalaSoft.MvvmLight.Threading.DispatcherHelper.UIDispatcher.HasShutdownStarted), new object[0])); } }

    public DateTime AddWorkingDays(DateTime start, int workingDates) {
      var sign = Math.Sign(workingDates);
      return Enumerable
          .Range(1, int.MaxValue)
          .Select(x => start.AddDays(x * sign))
          .Where(date => date.DayOfWeek != DayOfWeek.Saturday && date.DayOfWeek != DayOfWeek.Sunday)
          .Select((date, i) => new { date, i })
          .SkipWhile(a => a.i <= workingDates.Abs())
          .First().date;
    }
    class BarsBuffer {
      List<Rate> _buffer = new List<Rate>();
      private DateTime _lastDate;
      private Func<int> _barsCount;
      private string _pair;
      private int _barPeriod;
      public List<Rate> Start(string pair, DateTime dateStart, Func<int> barsCount, int barPeriod) {
        var bars = GlobalStorage.GetRateFromDB(pair, dateStart, barsCount() * 2, barPeriod);
        var rates = bars.Take(barsCount()).ToList();
        _pair = pair;
        _barPeriod = barPeriod;
        _barsCount = barsCount;
        _lastDate = bars.Last().StartDate;
        _buffer.AddRange(bars.Skip(barsCount()));
        return rates;
      }
      public Rate Next() {
        if(!_buffer.Any())
          return null;
        var rate = _buffer[0];
        _buffer.RemoveAt(0);
        return rate;
      }
      void GetSomeMore() {
        if(_buffer.Count >= _barsCount() / 2)
          return;
        NewThreadScheduler.Default.AsLongRunning().ScheduleLongRunning(_ => {
          var bars = GlobalStorage.GetRateFromDB(_pair, _lastDate.AddMinutes(_barPeriod), _barsCount() / 2, _barPeriod);
          _buffer.AddRange(bars);
          _lastDate = bars.CopyLast(1).Select(r => r.StartDate).DefaultIfEmpty(DateTime.MaxValue).Single();
        });
      }
    }

    static EventWaitHandle _waitHandle = new AutoResetEvent(false);
    public void Replay(ReplayArguments<TradingMacro> args) {
      if(!args.DateStart.HasValue) {
        Log = new ApplicationException("Start Date error.");
        return;
      }
      Func<IList<TradingMacro>> tms = () => args.TradingMacros;
      var trader = tms().Single(tm => tm.IsTrader);
      var traderStartDate = MonoidsCore.ToFunc(() => trader.UseRatesInternal(ri => ri.BackwardsIterator().Take(1).ToArray()).SelectMany(rates => rates));
      if(tms().Count(tm => tm.IsTrader) > 1)
        throw new Exception("Too many traders");
      if(tms().Count(tm => tm.IsTrader) == 0)
        throw new Exception("There is no traders");
      if(tms().Count(tm => tm.IsTrender) == 0)
        throw new Exception("There is no trenders");
      Action<RepayPauseMessage> pra = m => args.InPause = !args.InPause;
      Action<RepayBackMessage> sba = m => args.StepBack = true;
      Action<RepayForwardMessage> sfa = m => args.StepForward = true;
      var tc = new EventHandler<TradeEventArgs>((sender, e) => {
        GlobalStorage.UseForexContext(c => {
          try {
            var session = c.t_Session.Single(s => s.Uid == SessionId);
            session.MaximumLot = HistoryMaximumLot;
            session.MinimumGross = MinimumOriginalProfit;
            session.Profitability = Profitability;
            session.DateMin = e.Trade.TimeClose.IfMin(ServerTime);
            if(session.DateMin == null)
              session.DateMin = e.Trade.Time;
            c.SaveChanges();
          } catch(Exception exc) {
            Log = exc;
          }
        });
      });

      var isInitiator = args.Initiator == this;
      if(IsTrader)
        TradesManager.TradeClosed += tc;
      try {
        if(isInitiator) {
          GalaSoft.MvvmLight.Messaging.Messenger.Default.Register(this, pra);
          GalaSoft.MvvmLight.Messaging.Messenger.Default.Register(this, sba);
          GalaSoft.MvvmLight.Messaging.Messenger.Default.Register(this, sfa);
          args.MustStop = false;
        }
        if(!IsInVirtualTrading)
          UnSubscribeToTradeClosedEVent(TradesManager);
        SetPlayBackInfo(true, args.DateStart.GetValueOrDefault(), args.DelayInSeconds.FromSeconds());
        var dateStartDownload = AddWorkingDays(args.DateStart.Value, -(BarsCountCount() / 1440.0).Ceiling());
        var actionBlock = new ActionBlock<Action>(a => a());
        Action<Order2GoAddIn.FXCoreWrapper.RateLoadingCallbackArgs<Rate>> cb = callBackArgs => PriceHistory.SaveTickCallBack(BarPeriodInt, Pair, o => Log = new Exception(o + ""), actionBlock, callBackArgs);
        var fw = GetFXWraper();
        if(fw != null)
          PriceHistory.AddTicks(fw, BarPeriodInt, Pair, args.DateStart.GetValueOrDefault(DateTime.Now.AddMinutes(-BarsCountCount() * 2)), o => Log = new Exception(o + ""));
        //GetFXWraper().GetBarsBase<Rate>(Pair, BarPeriodInt, barsCountTotal, args.DateStart.GetValueOrDefault(TradesManagerStatic.FX_DATE_NOW), TradesManagerStatic.FX_DATE_NOW, new List<Rate>(), cb);
        var internalRateCount = BarsCountCount();
        var doGroupTick = BarPeriodCalc == BarsPeriodType.s1;
        Func<List<Rate>, List<Rate>> groupTicks = rates => doGroupTick ? GroupTicksToSeconds(rates) : rates;
        var _replayRates = GlobalStorage.GetRateFromDBBackwards<Rate>(Pair, args.DateStart.Value.ToUniversalTime(), BarsCountCount(), BarPeriodInt, groupTicks);
        _replayRates.CopyLast(1).Select(r => r.StartDate2)
          .ForEach(startDate => _replayRates.AddRange(groupTicks(GlobalStorage.GetRateFromDBForwards<Rate>(Pair, startDate, BarsCount, BarPeriodInt))));
        //var rateStart = rates.SkipWhile(r => r.StartDate < args.DateStart.Value).First();
        //var rateStartIndex = rates.IndexOf(rateStart);
        //var rateIndexStart = (rateStartIndex - BarsCount).Max(0);
        //rates.RemoveRange(0, rateIndexStart);
        var dateStop = args.DaysToTest > 0 ? args.DateStart.Value.AddDays(args.DaysToTest) : DateTime.MaxValue;
        if(args.DaysToTest > 0) {
          //rates = rates.Where(r => r.StartDate <= args.DateStart.Value.AddDays(args.MonthsToTest*30.5)).ToList();
          var avaibleDays = (_replayRates[0].StartDate - _replayRates.Last().StartDate).Duration().TotalDays;
          if(avaibleDays < args.DaysToTest) {
            //args.ResetSuperSession();
            //return;
            Log = new Exception("Total avalible days<" + avaibleDays + "> is less the DaysToTest<" + args.DaysToTest + ">");
          }
        }
        #region Init stuff
        _tradeEnterByCalc = new TradeCrossMethod[0];
        ResetBarsCountCalc();
        CorridorStats.Rates = null;
        UseRatesInternal(ri => ri.Clear());
        RateLast = null;
        WaveDistanceForTrade = double.NaN;
        WaveLength = 0;
        _waves = null;
        _sessionInfo = "";
        _isSelfStrategy = false;
        WaveShort.Reset();
        CloseAtZero = _trimAtZero = false;
        CurrentLoss = HistoryMaximumLot = 0;
        ResetMinimumGross();
        ForEachSuppRes(sr => {
          sr.CanTrade = false;
          sr.TradesCount = 0;
          sr.InManual = false;
          sr.CorridorDate = DateTime.MinValue;
        });
        if(CorridorStartDate != null)
          CorridorStartDate = null;
        if(CorridorStats != null)
          CorridorStats = null;
        WaveHigh = null;
        LastProfitStartDate = null;
        _waveRates.Clear();
        _strategyExecuteOnTradeClose = null;
        _strategyOnTradeLineChanged = null;
        MagnetPrice = double.NaN;
        var indexCurrent = 0;
        LastTrade = TradesManager.TradeFactory(Pair);
        FractalTimes = FractalTimes.Take(0);
        LineTimeMinFunc = null;
        if(_setVoltsSubscriber != null)
          _setVoltsSubscriber.Dispose();
        ResetTakeProfitManual();
        StDevByHeight = double.NaN;
        StDevByPriceAvg = double.NaN;
        LastTradeLossInPips = 0;
        LoadRatesStartDate2 = DateTimeOffset.MinValue;
        BarsCountLastDate = DateTime.MinValue;
        TradesManager.ResetClosedTrades(Pair);
        var closedSessionToLoad = TestClosedSession;
        _edgeDiffs = new double[0];
        if(!string.IsNullOrWhiteSpace(closedSessionToLoad)) {
          var vtm = (VirtualTradesManager)TradesManager;
          var sessionUid = Guid.Parse(closedSessionToLoad);
          var dbTrades = GlobalStorage.UseForexContext(c => c.t_Trade.Where(t => t.SessionId == sessionUid).ToArray());
          var trades = dbTrades.Select(trade => {
            var t = Trade.Create(trade.Pair, PointSize, CommissionByTrade);
            Func<DateTime, DateTime> convert = d => DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(d, HedgeHog.DateTimeZone.DateTimeZone.Eastern), DateTimeKind.Local);
            {
              t.Id = trade.Id;
              t.Buy = t.IsBuy = trade.Buy;
              t.PL = trade.PL;
              t.GrossPL = trade.GrossPL;
              t.Lots = trade.Lot.ToInt();
              t.SetTime(convert(trade.TimeOpen));
              t.TimeClose = convert(trade.TimeClose);
              t.Commission = trade.Commission;
              t.IsVirtual = trade.IsVirtual;
              t.Open = trade.PriceOpen;
              t.Close = trade.PriceClose;
              t.Kind = PositionBase.PositionKind.Closed;

            }
            return t;
          })
          .ToList();
          vtm.SetClosedTrades(trades);
          Log = new Exception("Loaded {1} trades from session {0}.".Formater(closedSessionToLoad, trades.Count));
        }
        _waitHandle.Set();
        TradingStatistics.OriginalProfit = 0;
        _macd2Rsd = double.NaN;
        MacdRsdAvg = double.NaN;
        IsRatesLengthStable = false;
        ResetTradeStrip = false;
        CenterOfMassBuy = CenterOfMassBuy2 = CenterOfMassSell = CenterOfMassSell2 = double.NaN;
        _SetVoltsByStd0 = new ConcurrentQueue<Tuple<DateTime, double>>();
        _SetVoltsByStd1 = new ConcurrentQueue<Tuple<DateTime, double>>();
        _SetVoltsByStd = new ConcurrentQueue<Tuple<DateTime, double>>();
        _voltsOk = true;
        _ratesStartDate = null;
        _mmaLastIsUp = null;
        _ratesArrayCoeffs = new double[0];
        #endregion
        var vm = (VirtualTradesManager)TradesManager;
        vm.SetInitialBalance(args.StartingBalance);
        if(!_replayRates.Any())
          throw new Exception("No rates were dowloaded fot Pair:{0}, Bars:{1}".Formater(Pair, BarPeriod));
        Rate ratePrev = null;
        bool noMoreDbRates = false;
        var isReplaying = false;
        var minutesOffset = BarPeriodInt * 0;
        while(!args.MustStop && indexCurrent < _replayRates.Count && Strategy != Strategies.None) {
          if(isReplaying && !IsTrader)
            if(!_waitHandle.WaitOne(1000)) {
              //Log = new Exception(new { PairIndex, WaitedFor = "1000 seconds. Recircles the loop now." } + "");
              continue;
            }
          Rate rate = null;
          DateTime replayDateMax = _replayRates.Last().StartDate;
          try {
            if(args.StepBack) {
              #region StepBack
              UseRatesInternal(ri => {
                if(ri.Last().StartDate > args.DateStart.Value) {
                  args.InPause = true;
                  rate = _replayRates.Previous(ri[0]);
                  if(rate != null)
                    ri.Insert(0, rate);
                  else
                    rate = ri[0];
                  ri.Remove(ri.Last());
                  RatesArraySafe.Count();
                  rate = ri.Last();
                  indexCurrent = _replayRates.IndexOf(rate);
                } else
                  rate = ri.Last();
              });
              #endregion
            } else {
              if(isReplaying && !IsTrader && tms().Count > 1) {
                var rateLast = UseRatesInternal(ri => ri.Last(), 15 * 1000);
                var dateMin = tms().First(tm => tm.IsTrader)
                  .UseRatesInternal(ri => ri.LastOrDefault())
                  .Where(r => r != null)
                  .Select(r => r.StartDate)
                  .SingleOrDefault();

                if(rateLast.IsEmpty() || rateLast.Single().StartDate > dateMin.AddMinutes(minutesOffset).Min(replayDateMax))
                  continue;
              }
              Func<bool> indexCurrentIsOver = () => indexCurrent >= _replayRates.Count - 1;
              if(!noMoreDbRates && (indexCurrent > _replayRates.Count - BarsCountCount() * .20 || indexCurrentIsOver())) {
                var moreRates = groupTicks(GlobalStorage.GetRateFromDBForwards<Rate>(Pair, _replayRates.Last().StartDate2.AddSeconds(1), BarsCountCount(), BarPeriodInt));
                if(moreRates.Count == 0)
                  noMoreDbRates = true;
                else {
                  _replayRates.AddRange(moreRates);
                  var maxCount = BarsCountCount() + moreRates.Count;
                  var slack = (_replayRates.Count - maxCount).Max(0);
                  _replayRates.RemoveRange(0, slack);
                  indexCurrent -= slack;
                }
              }
              if(indexCurrentIsOver())
                break;
              try {
                rate = _replayRates[indexCurrent + 1];
              } catch {
                Debugger.Break();
              }
              if(!UseRatesInternal(ri => {
                #region CloseTradesBeforeNews
                if(isReplaying && CloseTradesBeforeNews) {
                  var mi = _replayRates.Count - 1;
                  var ratesNext = Enumerable.Range(indexCurrent, 3).Where(i => i <= mi).Select(i => _replayRates[i]);
                  if(InPips(ratesNext.Select(r => r.AskHigh - r.BidLow).DefaultIfEmpty(0).Max()) > 40) {
                    if(Trades.Any())
                      BroadcastCloseAllTrades();
                    SuppRes.ForEach(sr => sr.CanTrade = false);
                    CloseTrades("Blackout");
                  }
                }
                #endregion
                if(rate != null)
                  if(ri.Count == 0 || rate > ri.LastBC()) {
                    if(isReplaying && !IsTrader && rate.StartDate > ServerTime.AddMinutes(minutesOffset).Min(replayDateMax))
                      return false;
                    if(!isReplaying && !IsTrader && traderStartDate().Any(r => rate.StartDate >= r.StartDate.AddMinutes(minutesOffset).Min(replayDateMax)))
                      return false;
                    ri.Add(rate);
                  } else if(args.StepBack) {
                    Debugger.Break();
                  }
                indexCurrent++;
                while(ri.Count > BarsCountCount()
                    && (!DoStreatchRates || (CorridorStats.Rates.Count == 0 || ri[0] < CorridorStats.Rates.LastBC())))
                  ri.RemoveAt(0);
                return true;
              })
              .DefaultIfEmpty(false)
              .Single())
                continue;
            }
            if(rate.StartDate > dateStop) {
              //if (CurrentGross > 0) {
              CloseTrades("Replay break due dateStop.");
              break;
              //}
            }
            if(UseRatesInternal(ri => ri.LastBC().StartDate < args.DateStart.Value).DefaultIfEmpty(true).Single()) {
              continue;
              //} else if (RatesArraySafe.LastBC().StartDate < args.DateStart.Value) {
              //  continue;
            } else if(!IsTrader && traderStartDate().Any(r => rate.StartDate > r.StartDate))
              continue;
            else {
              if(!isReplaying)
                Log = new Exception(new { Pair, PairIndex, isReplaying = "Start" } + "");
              isReplaying = true;
              if(UseRatesInternal(ri => ri.Last())
                .Do(rl => {
                  if(IsTrader)
                    TradesManager.SetServerTime(rl.StartDate);
                  LastRatePullTime = rl.StartDate;
                  LoadRatesStartDate2 = rl.StartDate2;
                }).IsEmpty())
                continue;
              //TradesManager.RaisePriceChanged(Pair, RateLast);
              var d = Stopwatch.StartNew();

              if(rate != null) {
                if(ratePrev == null || BarPeriod > BarsPeriodType.t1 || ratePrev.StartDate.Second != rate.StartDate.Second) {
                  if(!IsTrader && tms().First().BarPeriod == BarPeriod)
                    while(rate.StartDate.AddSeconds(1) < ServerTime && indexCurrent < _replayRates.Count)
                      UseRatesInternal(ri => ri.Add(rate = _replayRates[indexCurrent++]));
                  if(indexCurrent >= _replayRates.Count)
                    continue;
                  ratePrev = rate;
                  if(this.TradingMacrosByPair().First() == this && (Trades.Any() || IsTradingDay())) {
                    TradesManager.RaisePriceChanged(Pair, BarPeriodInt, new Price(Pair, rate));
                  }
                  ReplayEvents();
                  {
                    var a = TradesManager.GetAccount();
                    if(a.PipsToMC < 0) {
                      Log = new Exception("Equity Alert: " + TradesManager.GetAccount().Equity);
                      CloseTrades("Equity Alert: " + TradesManager.GetAccount().Equity);
                    }
                    if(MinimumOriginalProfit < TestMinimumBalancePerc) {
                      Log = new Exception("Minimum Balance Alert: " + MinimumOriginalProfit);
                      CloseTrades("Minimum Balance Alert: " + MinimumOriginalProfit);
                      args.MustStop = true;
                    }
                  }
                  if(RateLast != null)
                    Profitability = (args.GetOriginalBalance() - 50000) / (RateLast.StartDate - args.DateStart.Value).TotalDays * 30.5;
                  //if(DateTime.Now.Second % 5 == 0) Log = new Exception(("[{2}]{0}:{1:n1}ms" + Environment.NewLine + "{3}").Formater(MethodBase.GetCurrentMethod().Name, sw.ElapsedMilliseconds, Pair, string.Join(Environment.NewLine, swDict.Select(kv => "\t" + kv.Key + ":" + kv.Value))));
                }
              } else
                Log = new Exception("Replay:End");
              ReplayCancelationToken.ThrowIfCancellationRequested();
              Thread.Sleep((args.DelayInSeconds - d.Elapsed.TotalSeconds).Max(0).FromSeconds());
              Func<bool> inPause = () => args.InPause;
              if(inPause()) {
                args.StepBack = args.StepForward = false;
                Task.Factory.StartNew(() => {
                  while(inPause() && !args.StepBack && !args.StepForward && !args.MustStop)
                    Thread.Sleep(100);
                }).Wait();
              }
            }
          } finally {
            //args.NextTradingMacro();
            if(IsTrader)
              _waitHandle.Set();
          }
        }
        Log = new Exception("Replay for PairIndex:{0}[{1}] done.".Formater(PairIndex, BarPeriod));
      } catch(Exception exc) {
        Log = exc;
      } finally {
        try {
          if(IsTrader)
            _waitHandle.Set();
          ResetMinimumGross();
          args.TradingMacros.Remove(this);
          args.MustStop = true;
          args.SessionStats.ProfitToLossRatio = ProfitabilityRatio;
          TradesManager.CloseAllTrades();
          if(IsTrader)
            TradesManager.TradeClosed -= tc;
          if(isInitiator) {
            GalaSoft.MvvmLight.Messaging.Messenger.Default.Unregister(this, pra);
            GalaSoft.MvvmLight.Messaging.Messenger.Default.Unregister(this, sba);
            GalaSoft.MvvmLight.Messaging.Messenger.Default.Unregister(this, sfa);
          }
          SetPlayBackInfo(false, args.DateStart.GetValueOrDefault(), args.DelayInSeconds.FromSeconds());
          args.StepBack = args.StepBack = args.InPause = false;
          if(!IsInVirtualTrading) {
            UseRatesInternal(ri => ri.Clear());
            SubscribeToTradeClosedEVent(null, _tradingMacros);
            LoadRates();
          }
        } catch(Exception exc) {
          Log = exc;
          MessageBox.Show(exc.ToString(), "Replay");
        }
      }
    }

    public static List<TBar> GroupTicksToSeconds<TBar>(List<TBar> rates) where TBar : BarBase, new() {
      return GroupTicksToSeconds(rates, null);
    }
    public static List<TBar> GroupTicksToSeconds<TBar>(List<TBar> rates, Action<IList<TBar>, TBar> map) where TBar : BarBase, new() {
      return rates.GroupedDistinct(rate => rate.StartDate.AddMilliseconds(-rate.StartDate.Millisecond), (gt) => {
        var tBar = GroupToRate(gt);
        if(map != null)
          map(gt, tBar);
        return tBar;
      }).ToList();
    }

    public static TBar GroupToRate<TBar>(IList<TBar> gt, Action<IList<Rate>, TBar> map = null) where TBar : BarBase, new() {
      return new TBar() {
        StartDate2 = gt[0].StartDate2,
        AskOpen = gt.First().AskOpen,
        AskClose = gt.Last().AskClose,
        AskHigh = gt.Max(t => t.AskHigh),
        AskLow = gt.Min(t => t.AskLow),
        BidOpen = gt.First().BidOpen,
        BidClose = gt.Last().BidClose,
        BidHigh = gt.Max(t => t.BidHigh),
        BidLow = gt.Min(t => t.BidLow),
        PriceCMALast = gt.Average(r => r.PriceCMALast)
      };
    }

    private void ReplayEvents() {
      OnPropertyChanged(TradingMacroMetadata.CurrentGross);
      OnPropertyChanged(TradingMacroMetadata.CurrentGrossInPips);
      OnPropertyChanged(TradingMacroMetadata.OpenTradesGross);
    }


    #endregion

    #region TradesStatistics
    protected Dictionary<string, TradeStatistics> TradeStatisticsDictionary = new Dictionary<string, TradeStatistics>();
    public void SetTradesStatistics(Trade[] trades) {
      foreach(var trade in trades)
        SetTradeStatistics(trade);
    }
    static IEnumerable<double> PPMFromEnd(TradingMacro tm, double size) {
      return tm.UseRates(rates => {
        var rs = rates.GetRange(size);
        return new { rs, da = rs.Select(tm.GetPriceMA).Where(d => !d.IsNaN()).Distances().Average() };
      })
      .Select(x => x.da / x.rs.Last().StartDate.Subtract(x.rs[0].StartDate).Duration().TotalMinutes);
    }
    public TradeStatistics SetTradeStatistics(Trade trade) {
      if(!TradeStatisticsDictionary.ContainsKey(trade.Id))
        TradeStatisticsDictionary.Add(trade.Id, new TradeStatistics() {
          CorridorStDev = TLBlue.Angle.Abs(),
          CorridorStDevCma = RatesTimeSpan().FirstOrDefault().TotalMinutes,
          Values = new Dictionary<string, object> {
            { "Angle", TradingMacroTrender(tm=>tm.TLBlue.Angle.Abs()).SingleOrDefault() },
            { "Minutes", RatesTimeSpan().FirstOrDefault().TotalMinutes },
            { "PPM", TradingMacroTrender(tm=> PPMFromEnd(tm,-0.25),x=>x).SingleOrDefault() },
            { "PpmM1", TradingMacroM1(tm=>tm.WaveRangeAvg.PipsPerMinute).FirstOrDefault() },
            { "M1Angle", TradingMacroM1(tm=>tm.WaveRangeAvg.Angle.Abs()).FirstOrDefault() },
            //{ "Equinox", _wwwInfoEquinox },
            { "StDev", TradingMacroM1(tm=>tm.WaveRanges.Select(wr=>wr.StDev).FirstOrDefault()).SingleOrDefault() },
            { "StDevAvg", TradingMacroM1(tm=>tm.WaveRangeAvg.StDev).SingleOrDefault() }
          }
        });
      var ts = TradeStatisticsDictionary[trade.Id];
      if(false) {
        if(!trade.Buy && ts.Resistanse == 0 && HasCorridor)
          ts.Resistanse = CorridorRates.OrderBars().Max(CorridorStats.priceHigh);
        if(trade.Buy && ts.Support == 0 && HasCorridor)
          ts.Support = CorridorRates.OrderBars().Min(CorridorStats.priceLow);
      }
      return ts;
    }

    private IEnumerable<Rate> CorridorRates {
      get {
        return RatesArraySafe.Where(r => r.StartDate >= CorridorStats.StartDate);
      }
    }
    private IEnumerable<Rate> GannAngleRates {
      get {
        return RatesArraySafe.SkipWhile(r => r.GannPrice1x1 == 0);
      }
    }
    public TradeStatistics GetTradeStatistics(Trade trade) {
      return TradeStatisticsDictionary.ContainsKey(trade.Id) ? TradeStatisticsDictionary[trade.Id] : null;
    }
    #endregion

    int _PriceCmaDirection;

    public int PriceCmaDirection {
      get { return _PriceCmaDirection; }
      set { _PriceCmaDirection = value; }
    }

    private double _CorridorAngle;
    public double CorridorAngle {
      get { return _CorridorAngle; }
      set {
        if(PointSize != 0 && !value.IsNaN()) {
          var ca = value;
          if(Math.Sign(ca) != Math.Sign(_CorridorAngle) && _corridorDirectionChanged != null)
            _corridorDirectionChanged(this, EventArgs.Empty);
          _CorridorAngle = ca;
          OnPropertyChanged(TradingMacroMetadata.CorridorAngle);
        }
      }
    }
    event EventHandler _corridorDirectionChanged;

    #region SuppReses

    bool _checkAdjustSuppResCount=true;
    void AdjustSuppResCount() {
      if(SuppResLevelsCount < 1)
        throw new Exception("SuppResLevelsCount must be at least 1.");
      var raiseChart = false;
      foreach(var isSupport in new[] { false, true }) {
        while(SuppRes.Where(sr => sr.IsSupport == isSupport).Count() > SuppResLevelsCount) {
          RemoveSuppRes(SuppRes.Where(sr => sr.IsSupport == isSupport).Last());
          raiseChart = true;
        }
        while(SuppRes.Where(sr => sr.IsSupport == isSupport).Count() < SuppResLevelsCount) {
          AddSuppRes(RatesArray.Average(r => r.PriceAvg), isSupport);
          raiseChart = true;
        }
      }
      if(raiseChart)
        RaiseShowChart();
    }

    private bool IsEntityStateOk {
      get {
        return EntityState != System.Data.Entity.EntityState.Detached && EntityState != System.Data.Entity.EntityState.Deleted;
      }
    }
    const double suppResDefault = double.NaN;
    private int BarsCountCount() { return BarsCountMax; }// BarsCountMax < 1000 ? BarsCount * BarsCountMax : BarsCountMax; }

    public void SuppResResetAllTradeCounts(int tradesCount = 0) { SuppResResetTradeCounts(SuppRes, tradesCount); }
    public static void SuppResResetTradeCounts(IEnumerable<SuppRes> suppReses, double tradesCount = 0) {
      if(tradesCount < 0)
        suppReses.ToList().ForEach(sr => sr.TradesCount = Math.Max(Store.SuppRes.TradesCountMinimum, sr.TradesCount + tradesCount));
      else
        suppReses.ToList().ForEach(sr => sr.TradesCount = Math.Max(Store.SuppRes.TradesCountMinimum, tradesCount));
    }

    private Store.SuppRes SupportLow() {
      var a = Supports.OrderBy(s => s.Rate).FirstOrDefault();
      if(a == null)
        throw new Exception("There is no Support.");
      return a;
    }

    private Store.SuppRes SupportHigh() {
      var a = Supports.OrderBy(s => s.Rate).LastOrDefault();
      if(a == null)
        throw new Exception("There is no Support.");
      return a;
    }
    private Store.SuppRes[] Support0() {
      return SupportByPosition(0);
    }
    private Store.SuppRes[] Support1() {
      return SupportByPosition(1);
    }
    private Store.SuppRes[] SupportByPosition(int position) {
      if(SuppRes == null)
        throw new Exception(new { SuppRes } + "");
      return SuppRes.Where(sr => sr.IsSupport).Skip(position).Take(1).ToArray();
    }
    private Store.SuppRes[] SupportsNotCurrent() {
      return SuppResNotCurrent(Supports);
    }

    private Store.SuppRes ResistanceLow() {
      var a = Resistances.OrderBy(s => s.Rate).FirstOrDefault();
      if(a == null)
        throw new Exception("There is no Resistance.");
      return a;

    }

    private Store.SuppRes ResistanceHigh() {
      var a = Resistances.OrderBy(s => s.Rate).LastOrDefault();
      if(a == null)
        throw new Exception("There is no Restiance.");
      return a;
    }
    private Store.SuppRes[] Resistance0() {
      return ResistanceByPosition(0);
    }
    private Store.SuppRes[] Resistance1() {
      return ResistanceByPosition(1);
    }

    private Store.SuppRes[] ResistanceByPosition(int position) {
      if(SuppRes == null)
        throw new Exception(new { SuppRes } + "");
      return SuppRes.Where(sr => !sr.IsSupport).Skip(position).Take(1).ToArray();
    }
    private Store.SuppRes[] ResistancesNotCurrent() {
      return SuppResNotCurrent(Resistances);
    }
    private Store.SuppRes[] SuppResNotCurrent(SuppRes[] suppReses) {
      return suppReses.OrderBy(s => (s.Rate - CurrentPrice.Ask).Abs()).Skip(1).ToArray();
    }

    private SuppRes[] IndexSuppReses(SuppRes[] suppReses) {
      if(!IsActive)
        return suppReses;
      if(suppReses.Any(a => a.Index == 0)) {
        var index = 1;
        suppReses.OrderByDescending(a => a.Rate).ToList().ForEach(a => {
          a.Index = index++;
        });
        return suppReses;
        if(Trades.Length > 0) {
          var trade = Trades.OrderBy(t => t.Time).Last();
          var lots = (Trades.Sum(t => t.Lots) + LotSize) / LotSize;
          var lot = lots / 2;
          var rem = lots % 2;
          var tcBuy = lot + (trade.Buy ? rem : 0);
          var tcSell = lot + (!trade.Buy ? rem : 0);
          if(tcBuy > 0)
            SuppResResetTradeCounts(Resistances, tcBuy);
          if(tcSell > 0)
            SuppResResetTradeCounts(Supports, tcSell);
        }
      }
      return suppReses;
    }
    #endregion

    #region Supports/Resistances
    #region Add
    public SuppRes AddBuySellRate(double rate, bool isBuy) { return AddSuppRes(rate, !isBuy); }
    public SuppRes AddSuppRes(double rate, bool isSupport) {
      try {
        var srs = (isSupport ? Supports : Resistances);
        var index = srs.Select(a => a.Index).DefaultIfEmpty(0).Max() + 1;
        var sr = new SuppRes { Rate = rate, IsSupport = isSupport, TradingMacroID = UID, UID = Guid.NewGuid(), TradingMacro = this, Index = index, TradesCount = srs.Select(a => a.TradesCount).DefaultIfEmpty().Max() };
        SuppRes.Add(sr);
        SuppRes_AssociationChanged(SuppRes, new CollectionChangeEventArgs(CollectionChangeAction.Add, sr));
        //GlobalStorage.UseAliceContext(c => c.SuppRes.AddObject(sr));
        //GlobalStorage.UseAliceContext(c => c.SaveChanges());
        return sr;
      } catch(Exception exc) {
        Log = exc;
        return null;
      }
    }
    #endregion
    #region Update
    /// <summary>
    /// Used for manual (drag) position change
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="rateNew"></param>
    public SuppRes UpdateSuppRes(Guid uid, double rateNew) {
      var suppRes = SuppRes.ToArray().SingleOrDefault(sr => sr.UID == uid);
      if(suppRes == null)
        throw new InvalidOperationException("SuppRes UID:" + uid + " does not exist.");
      suppRes.Rate = rateNew;
      suppRes.InManual = true;
      return suppRes;
    }

    #endregion
    #region Remove
    public void RemoveSuppRes(Guid uid) {
      try {
        var suppRes = SuppRes.SingleOrDefault(sr => sr.UID == uid);
        RemoveSuppRes(suppRes);
      } catch(Exception exc) {
        Log = exc;
      }
    }

    private void RemoveSuppRes(Store.SuppRes suppRes) {
      if(suppRes != null) {
        SuppRes.Remove(suppRes);
        GlobalStorage.UseAliceContext(c => c.DeleteObject(suppRes));
      }
    }
    #endregion

    partial void OnSupportPriceStoreChanging(double? value) {
      //if (value.GetValueOrDefault() > 0)
      //  Application.Current.Dispatcher.BeginInvoke(new Action(() => {
      //    if (!SuppRes.Any(sr => sr.Rate == value.GetValueOrDefault()))
      //      AddSupport(value.GetValueOrDefault());
      //  }));
    }
    partial void OnResistancePriceStoreChanging(double? value) {
      //if (value.GetValueOrDefault() > 0)
      //  Application.Current.Dispatcher.BeginInvoke(new Action(() => {
      //    if (!SuppRes.Any(sr => sr.Rate == value.GetValueOrDefault()))
      //      AddResistance(value.GetValueOrDefault());
      //  }));
    }

    private static string GetSuppResRateErrorMessage(double rate) {
      return "Rate " + rate + " is not unique in SuppRes table";
    }
    object supportsLocker = new object();
    public SuppRes[] Supports {
      get {
        lock(supportsLocker) {
          return IndexSuppReses(SuppRes.Where(sr => sr.IsSupport).OrderBy(a => a.Rate).ToArray());
        }
      }
    }
    object resistancesLocker = new object();
    public SuppRes[] Resistances {
      get {
        lock(resistancesLocker)
          return IndexSuppReses(SuppRes.Where(sr => !sr.IsSupport).OrderBy(a => a.Rate).ToArray());
      }
    }


    public double[] SuppResPrices(bool isSupport) {
      return SuppRes.Where(sr => sr.IsSupport == isSupport).Select(sr => sr.Rate).ToArray();
    }
    public double[] SupportPrices { get { return Supports.Select(sr => sr.Rate).ToArray(); } }
    public double[] ResistancePrices { get { return Resistances.Select(sr => sr.Rate).ToArray(); } }
    #endregion

    #region CenterOfMass
    double _CenterOfMassSell = double.NaN;
    public double CenterOfMassSell {
      get { return _CenterOfMassSell; }
      private set { _CenterOfMassSell = value; }
    }
    double _CenterOfMassBuy = double.NaN;
    public double CenterOfMassBuy {
      get { return _CenterOfMassBuy; }
      private set { _CenterOfMassBuy = value; }
    }
    double _CenterOfMassSell2 = double.NaN;
    public double CenterOfMassSell2 {
      get { return _CenterOfMassSell2; }
      private set {
        CenterOfMassSell3 = _CenterOfMassSell2;
        _CenterOfMassSell2 = value;
      }
    }
    double _CenterOfMassBuy2 = double.NaN;
    public double CenterOfMassBuy2 {
      get { return _CenterOfMassBuy2; }
      private set {
        CenterOfMassBuy3 = _CenterOfMassBuy2;
        _CenterOfMassBuy2 = value;
      }
    }
    double _CenterOfMassSell3 = double.NaN;
    public double CenterOfMassSell3 {
      get { return _CenterOfMassSell3; }
      private set { _CenterOfMassSell3 = value; }
    }
    double _CenterOfMassBuy3 = double.NaN;
    public double CenterOfMassBuy3 {
      get { return _CenterOfMassBuy3; }
      private set { _CenterOfMassBuy3 = value; }
    }

    #endregion
    public double SuppResMinimumDistance { get { return CurrentPrice.Spread * 2; } }

    #region MagnetPrice
    private double CalcMagnetPrice(IList<Rate> rates = null) {
      return (rates ?? CorridorStats.Rates).Average(r => r.PriceAvg);
      //return (double)((rates ?? CorridorStats.Rates).Sum(r => (decimal)r.PriceAvg / (decimal)(r.Spread * r.Spread)) / (rates ?? CorridorStats.Rates).Sum(r => 1 / (decimal)(r.Spread * r.Spread)));
      return (double)((rates ?? CorridorStats.Rates).Sum(r => r.PriceAvg / r.Spread) / (rates ?? CorridorStats.Rates).Sum(r => 1 / r.Spread));
    }
    private void SetMagnetPrice(IList<Rate> rates = null) {
      try {
        //var rates = RatesArray.Where(r => r.Volume > 0 && r.Spread > 0 && r.PriceStdDev>0).ToList();
        //MagnetPrice = rates.Sum(r => r.PriceAvg / r.Volume) / rates.Sum(r => 1.0 / r.Volume);
        //MagnetPrice = rates.Sum(r => r.PriceAvg * r.PriceStdDev * r.PriceStdDev) / rates.Sum(r => r.PriceStdDev * r.PriceStdDev);
        MagnetPrice = CalcMagnetPrice(rates);
        //MagnetPrice = _levelCounts[0].Item2;
      } catch { }
    }
    private double _MagnetPrice = double.NaN;
    public double MagnetPrice {
      get { return _MagnetPrice; }
      set {
        if(_MagnetPrice != value) {
          _MagnetPrice = value;
          OnPropertyChanged("MagnetPrice");
        }
      }
    }
    private double _MagnetPricePosition;
    /// <summary>
    /// Position relatively mean
    /// </summary>
    public double MagnetPricePosition {
      get { return _MagnetPricePosition; }
      set {
        if(_MagnetPricePosition != value) {
          _MagnetPricePosition = value;
          OnPropertyChanged("MagnetPricePosition");
        }
      }
    }
    #endregion

    Rate _rateLast;
    public Rate RateLast {
      get { return _rateLast; }
      set {
        if(_rateLast != value) {
          _rateLast = value;
          OnPropertyChanged("RateLast");
        }
      }
    }

    Rate _ratePrev;
    public Rate RatePrev {
      get { return _ratePrev; }
      set {
        if(_ratePrev == value)
          return;
        _ratePrev = value;
        OnPropertyChanged("RatePrev");
      }
    }

    #region RatePrev1
    private Rate _RatePrev1;
    public Rate RatePrev1 {
      get { return _RatePrev1; }
      set {
        if(_RatePrev1 != value) {
          _RatePrev1 = value;
          OnPropertyChanged("RatePrev1");
        }
      }
    }

    #endregion
    //object _rateArrayLocker = new object();
    List<Rate> _rateArray_ = new List<Rate>();
    public List<Rate> RatesArray {
      get { return _rateArray_; }
      set { _rateArray_ = value == null ? new List<Rate>() : value; }
    }
    struct RatesArrayBag {
      public DateTime LastHour { get; set; }
      public double LastHeightMIn { get; set; }
    }
    RatesArrayBag _ratesArrayBag;
    double _distanceByMASD = double.NaN;
    public List<Rate> RatesArraySafe {
      get {
        try {
          if(!SnapshotArguments.IsTarget && UseRatesInternal(ri => ri.Count).DefaultIfEmpty(0).Single() < Math.Max(1, BarsCountCalc)) {
            //Log = new RatesAreNotReadyException();
            return new List<Rate>();
          }

          Stopwatch sw = Stopwatch.StartNew();
          var rateLast = UseRatesInternal(ri => ri.LastOrDefault());
          var rs = rateLast.Select(rl => rl.AskHigh - rl.BidLow);
          var rs2 = RateLast == null ? 0 : RateLast.AskHigh - RateLast.BidLow;
          if(!rateLast.IsEmpty() && (rateLast.Single() != RateLast || rs.Single() != rs2 || RatesArray == null || RatesArray.Count == 0)) {
            #region Quick Stuff
            UseRatesInternal(ri => {
              RateLast = ri.Last();
              RatePrev = ri[ri.Count - 2];
              RatePrev1 = ri[ri.Count - 3];
              UseRates(_ => RatesArray = ri.GetRange(BarsCountCalc).ToList());
              RatesDuration = RatesArray.Duration(r => r.StartDate).TotalMinutes.ToInt();
            });
            if(_checkAdjustSuppResCount) {
              _checkAdjustSuppResCount = false;
              AdjustSuppResCount();
            }
            IsAsleep = new[] { BuyLevel.InManual, SellLevel.InManual }.All(im => !im) &&
              Trades.Length == 0 &&
              IsInVirtualTrading &&
              TradeConditionsHaveAsleep();
            //if (IsAsleep)
            //ResetBarsCountCalc();
            RatesHeight = RatesArray.Height(r => r.AskHigh, r => r.BidLow, out _RatesMin, out _RatesMax);//CorridorStats.priceHigh, CorridorStats.priceLow);

            if(IsAsleep) {
              BarsCountCalc = BarsCount;
              RaiseShowChart();
              RunStrategy();
              OnScanCorridor(RatesArray, null, false);
              return RatesArray;
            }

            UseRates(rates => { SetMA(rates); return false; });

            OnSetBarsCountCalc();
            ScanOutsideEquinox();

            if(IsInVirtualTrading)
              Trades.ToList().ForEach(t => t.UpdateByPrice(TradesManager, CurrentPrice));
            #endregion
            if(BarPeriod > BarsPeriodType.t1)
              ScanForWaveRanges2(RatesArray);
            OnGeneralPurpose(() => {
              UseRates(rates => rates.ToList())
              .ForEach(rates => {
                if(rates.Count < BarsCount) {
                  Log = new Exception(new { BarsCount, Error = "Too low" } + "");
                  return;
                }
                if(VoltageFunction == Alice.VoltageFunction.DistanceMacd) {
                  SetVoltageByRHSD(rates);
                }
                SpreadForCorridor = rates.Spread();
                RatesHeightCma = rates.ToArray(r => r.PriceCMALast).Height(out _ratesHeightCmaMin, out _ratesHeightCmaMax);
                var leg = rates.Count.Div(10).ToInt().Max(1);
                PriceSpreadAverage = rates
                .Buffer(leg)
                .Where(b => b.Count > leg * .75)
                .Select(b => b.Max(r => r.PriceSpread))
                .Average();
                OnRatesArrayChaged();
                AdjustSuppResCount();
                var prices = RatesArray.ToArray(_priceAvg);
                _ratesArrayCoeffs = prices.Linear();
                StDevByPriceAvg = prices.StandardDeviation();
                StDevByHeight = prices.StDevByRegressoin(_ratesArrayCoeffs);
                switch(CorridorCalcMethod) {
                  case CorridorCalculationMethod.Height:
                  case CorridorCalculationMethod.MinMax:
                  case CorridorCalculationMethod.HeightUD:
                    RatesStDev = StDevByHeight;
                    break;
                  case CorridorCalculationMethod.PriceAverage:
                    RatesStDev = StDevByPriceAvg;
                    break;
                  case CorridorCalculationMethod.PowerMeanPower:
                    RatesStDev = StDevByPriceAvg.RootMeanPower(StDevByHeight, 100);
                    break;
                  case CorridorCalculationMethod.RootMeanSquare:
                    RatesStDev = StDevByPriceAvg.SquareMeanRoot(StDevByHeight);
                    break;
                  default:
                    throw new Exception(new { CorridorCalcMethod } + " is not supported.");
                }
                Angle = AngleFromTangent(_ratesArrayCoeffs.LineSlope(), () => CalcTicksPerSecond(rates));
                CalcBoilingerBand();
                //RatesArray.Select(GetPriceMA).ToArray().Regression(1, (coefs, line) => LineMA = line);
                OnPropertyChanged(() => RatesRsd);
              });
              RunStrategy();
            }, false);
            OnScanCorridor(RatesArray, () => {
              try {
                CorridorAngle = TLRed.Angle;
                TakeProfitPips = InPips(CalculateTakeProfit());
              } catch(Exception exc) { Log = exc; if(IsInVirtualTrading) Strategy = Strategies.None; throw; }
            }, IsInVirtualTrading);
            OnPropertyChanged(TradingMacroMetadata.TradingDistanceInPips);
            OnPropertyChanged(() => RatesStDevToRatesHeightRatio);
            OnPropertyChanged(() => SpreadForCorridorInPips);
            OnPropertyChanged(TradingMacroMetadata.TradingTimeState);
          }
          if(!IsInVirtualTrading && sw.Elapsed > TimeSpan.FromSeconds(5)) {
            //var s = string.Join(Environment.NewLine, timeSpanDict.Select(kv => " " + kv.Key + ":" + kv.Value));
            Log = new Exception("RatesArraySafe[{0}] took {1:n1} sec.".Formater(Pair, sw.Elapsed.TotalSeconds));
          }
          return RatesArray;
        } catch(Exception exc) {
          Log = exc;
          return RatesArray;
        }
      }
    }

    private void SetVoltageByDistanceMACD(List<Rate> rates) {
      rates.Reverse();
      var dist = BarPeriod != BarsPeriodType.none
        ? rates.Count > BarsCount
        ? new[] { rates }.Select(range => InPips(DistanceByMACD2(range, BarsCountCalc, null).LastOrDefault())).SingleOrDefault()
        : 0
        : this.TradingMacrosByPair()
        .Where(tm => tm.BarPeriod == BarsPeriodType.t1)
        .Select(tm => {
          var date = rates[0].StartDate;
          return tm.UseRates(ra => ra.SkipWhile(r => r.StartDate < date).Select(r => tm.GetVoltage(r)).DefaultIfEmpty(0).Average()).First();
        }).First();
      if(dist > 0) {
        rates.TakeWhile(r => GetVoltage(r).IsNaNOrZero()).ForEach(r => SetVoltage(r, dist));
        DistanceByMASD = rates.Average(GetVoltage);
      }
      rates.Reverse();
      var firstVolt = Lazy.Create(() => rates.SkipWhile(r => GetVoltage(r).IsNaN()).Take(1).ToArray(GetVoltage));
      rates.TakeWhile(r => GetVoltage(r).IsNaN()).ForEach(r => firstVolt.Value.ForEach(v => SetVoltage(r, v)));
    }
    private void SetVoltageByRHSD(List<Rate> rates) {
      if(CanTriggerTradeDirection()) {
        if(PairIndex == 1) {
          if(_macd2Rsd.IsNotNaN()) {
            rates.BackwardsIterator().TakeWhile(r => GetVoltage(r).IsNaN()).ForEach(r => SetVoltage(r, _macd2Rsd));
          }
        } else {
          var oneMinute = 1.FromMinutes();
          Func<Rate, double, bool> setVolts = (r, v) => { SetVoltage(r, v); return true; };
          (from tm in TradingMacroOther(tm => tm.BarPeriod == BarsPeriodType.t1)
           from lastRate in UseRates(ra => ra.Last())
           let lastDate = lastRate.StartDate
           from voltsAvg in tm.UseRates(ra => ra.BackwardsIterator()
            .TakeWhile(r => r.StartDate.Subtract(lastDate).Duration() < oneMinute)
            .Select(r => tm.GetVoltage(r))
            .Where(Lib.IsNotNaN)
            .DefaultIfEmpty(double.NaN)
            .Average())
            .Where(Lib.IsNotNaN)
           select rates.BackwardsIterator().TakeWhile(r => GetVoltage(r).IsNaN()).Do(r => SetVoltage(r, voltsAvg)).Count() +
                  rates.TakeWhile(r => GetVoltage(r).IsNaN()).Do(r => SetVoltage(r, voltsAvg)).Count()
           ).Count();
        }
        var avg = 0.0;

        var std = rates.Select(GetVoltage).Where(Lib.IsNotNaN).DefaultIfEmpty().StandardDeviation(out avg);
        MacdRsdAvg = std + avg;
        GetVoltageHigh = () => MacdRsdAvg;
        GetVoltageAverage = () => avg;
      }
    }
    CorridorStatistics ShowVoltsByStDev() {
      TLBlue.HStdRatio
      .ForEach(volt => SetVolts(volt, true));
      return null;
    }
    CorridorStatistics ShowVoltsByAvgLineRatio() {
      _setEdgeLinesAsyncBuffer.Push(() =>
        UseRates(rates => rates.ToArray(_priceAvg)).ForEach(rates => {
          SetAvgLines(rates);
          SetVots(InPips(AvgLineAvg), 2);
        }));
      return null;
    }

    private void SetVoltsByStd(double volt) {
      SetVoltsByStd(volt, TLBlue);
    }
    private void SetVoltsByStd(double volt, Rate.TrendLevels tls) {
      UseRates(rates => rates.Where(r => GetVoltage2(r).IsNaN()).ToList())
        .SelectMany(rates => rates).ForEach(r => SetVoltage2(r, volt));
      //UseRates(rates => rates.Select(GetVoltage2).Scan((v1, v2) => v1 == v2 ? v2 : double.NaN).TakeWhile(Lib.IsNotNaN).Count())
      //  .Where(dc => dc < RatesArray.Count - tls.Count)
      //  .ForEach(dc =>
      UseRates(rates => {
        return rates.GetRange(tls.Count).Select(GetVoltage2).StandardDeviation();
      })
      .ForEach(volts2 => {
        SetVolts(volts2, true);
        _setVoltsAveragesAsyncBuffer.Push(() => {
          _voltsPriceCorrelation = Lazy.Create(() => UseRates(rates => {
            var z = rates.Where(r => _priceAvg(r).IsNotNaN() && GetVoltage(r).IsNotNaN()).ToArray();
            double[] ps = z.ToArray(_priceAvg), vs = z.ToArray(GetVoltage);
            return new[] { alglib.pearsoncorr2(vs, ps), alglib.spearmancorr2(vs, ps) };
          }).SelectMany(c => c).ToArray());
        });
      });
    }
    private void SetVoltsByRStd(double volt) {
      UseRates(rates => rates.Where(r => GetVoltage2(r).IsNaN()).ToList())
        .SelectMany(rates => rates).ForEach(r => SetVoltage2(r, volt));
      UseRates(rates => rates.Select(GetVoltage2).RelativeStandardDeviation())
      .ForEach(volts2 => SetVolts(volts2, true));
    }

    Lazy<double[]> _voltsPriceCorrelation = new Lazy<double[]>(() => new[] { 0.0, 0.0 });
    class SetVoltsAveragesAsyncBuffer : AsyncBuffer<SetVoltsAveragesAsyncBuffer, Action> {
      public SetVoltsAveragesAsyncBuffer() : base() { }
      protected override Action PushImpl(Action context) {
        return context;
      }
    }
    SetVoltsAveragesAsyncBuffer _setVoltsAveragesAsyncBuffer = new SetVoltsAveragesAsyncBuffer();

    ConcurrentQueue<Tuple<DateTime, double>> _SetVoltsByStd0 = new ConcurrentQueue<Tuple<DateTime, double>>();
    ConcurrentQueue<Tuple<DateTime, double>> _SetVoltsByStd1 = new ConcurrentQueue<Tuple<DateTime, double>>();
    ConcurrentQueue<Tuple<DateTime, double>> _SetVoltsByStd = new ConcurrentQueue<Tuple<DateTime, double>>();
    private void SetVoltsByStd_Old(double volt, Rate.TrendLevels tls) {
      if(tls.IsEmpty)
        return;
      var startDate = RatesArray[RatesArray.Count - tls.Count].StartDate;
      var endDate = ServerTime;
      _SetVoltsByStd0.Enqueue(Tuple.Create(endDate, volt));
      var voltsCount = (tls.Count * 1.2).Min(_SetVoltsByStd0.Count).ToInt();
      var volts0 = _SetVoltsByStd0.ToList().GetRange(voltsCount).SkipWhile(t => t.Item1 < startDate).ToArray();
      if(volts0.Length > tls.Count / 10.0 || volts0.Length > 300) {
        var volts2 = volts0.StandardDeviation(t => t.Item2);
        var cma = CmaPeriodByRatesCount();
        _SetVoltsByStd.Enqueue(Tuple.Create(endDate, (_SetVoltsByStd.LastOrDefault() ?? Tuple.Create(DateTime.MaxValue, double.NaN)).Item2.Cma(cma, volts2)));
        UseRates(rates => rates.BackwardsIterator().TakeWhile(r => GetVoltage(r).IsNaN()).ToList())
          .ForEach(rates => {
            rates.Reverse();
            rates.Take(1).Select(r => r.StartDate).ForEach(sd =>
              rates.Zip(r => r.StartDate, _SetVoltsByStd.SkipWhile(t => t.Item1 <= sd), (r, t) => SetVoltage(r, t.Item2))
            );
          });

        UseRates(rates => {
          rates.BackwardsIterator().TakeWhile(r => GetVoltage(r).IsNaN()).TakeLast(1).Select(r => r.StartDate).ForEach(sd => {
            rates.Zip(r => r.StartDate, _SetVoltsByStd.SkipWhile(t => t.Item1 <= sd), (r, t) => SetVoltage(r, t.Item2));
          });
          return Unit.Default;
        });
        Action<DateTime, ConcurrentQueue<Tuple<DateTime, double>>> dequeByTime = (date, queue) => {
          do {
            Tuple<DateTime, double> t;
            queue.TryPeek(out t);
            if(t.Item1 >= date)
              break;
            queue.TryDequeue(out t);
          } while(true);
        };
        dequeByTime(RatesInternal[0].StartDate, _SetVoltsByStd0);
        dequeByTime(RatesInternal[0].StartDate, _SetVoltsByStd);
        Action a = () => {
          var sd = RatesArray[0].StartDate;
          var averageIterations = 2;
          var volts = _SetVoltsByStd.SkipWhile(t => t.Item1 < sd).ToArray(t => t.Item2);
          var voltageAvgLow = volts.AverageByIterations(-averageIterations).Average();
          GetVoltageAverage = () => voltageAvgLow;
          var voltageAvgHigh = volts.AverageByIterations(averageIterations).Average();
          GetVoltageHigh = () => voltageAvgHigh;
        };
        _setVoltsAveragesAsyncBuffer.Push(a);
      }
    }
    private void SetVoltsByStd_New(double volt, Rate.TrendLevels tls) {
      if(tls.IsEmpty)
        return;
      var startDate = RatesArray[RatesArray.Count - tls.Count].StartDate;
      var endDate = ServerTime;
      _SetVoltsByStd0.Enqueue(Tuple.Create(endDate, volt));
      var volts00 = _SetVoltsByStd0.SkipWhile(t => t.Item1 < startDate).ToArray();
      if(volts00.Length == _SetVoltsByStd0.Count) {
        _voltsOk = false;
        return;
      }
      _SetVoltsByStd1.Enqueue(Tuple.Create(endDate, volts00.StandardDeviation(t => t.Item2)));
      var voltsStds = _SetVoltsByStd1;
      var volts0 = voltsStds.SkipWhile(t => t.Item1 < startDate).ToArray();
      if(volts0.Length == voltsStds.Count) {
        _voltsOk = false;
        return;
      }
      var volts2 = volts0.StandardDeviation(t => t.Item2);
      var cma = CmaPeriodByRatesCount();
      _SetVoltsByStd.Enqueue(Tuple.Create(endDate, (_SetVoltsByStd.LastOrDefault() ?? Tuple.Create(DateTime.MaxValue, double.NaN)).Item2.Cma(cma, volts2)));

      UseRates(rates => rates.BackwardsIterator().TakeWhile(r => GetVoltage(r).IsNaN()).ToList())
        .ForEach(rates => {
          rates.Reverse();
          rates.Take(1).Select(r => r.StartDate).ForEach(sd => {
            var volts = _SetVoltsByStd.SkipWhile(t => t.Item1 <= sd).ToArray();
            rates.Zip(r => r.StartDate, volts, (r, t) => SetVoltage(r, t.Item2));
            _voltsOk = volts.Length < _SetVoltsByStd.Count;
          });
        });

      UseRates(rates => {
        rates.BackwardsIterator().TakeWhile(r => GetVoltage(r).IsNaN()).TakeLast(1).Select(r => r.StartDate).ForEach(sd => {
          rates.Zip(r => r.StartDate, _SetVoltsByStd.SkipWhile(t => t.Item1 <= sd), (r, t) => SetVoltage(r, t.Item2));
        });
        return Unit.Default;
      });
      Action<DateTime, ConcurrentQueue<Tuple<DateTime, double>>> dequeByTime = (date, queue) => {
        do {
          Tuple<DateTime, double> t;
          queue.TryPeek(out t);
          if(t.Item1 >= date)
            break;
          queue.TryDequeue(out t);
        } while(true);
      };
      dequeByTime(RatesInternal[0].StartDate, _SetVoltsByStd0);
      dequeByTime(RatesInternal[0].StartDate, _SetVoltsByStd);
      Action a = () => {
        var sd = RatesArray[0].StartDate;
        var averageIterations = 2;
        var volts = _SetVoltsByStd.SkipWhile(t => t.Item1 < sd).ToArray(t => t.Item2);
        var voltageAvgLow = volts.AverageByIterations(-averageIterations).Average();
        GetVoltageAverage = () => voltageAvgLow;
        var voltageAvgHigh = volts.AverageByIterations(averageIterations).Average();
        GetVoltageHigh = () => voltageAvgHigh;
      };
      _setVoltsAveragesAsyncBuffer.Push(a);
    }

    CorridorStatistics ShowVoltsByLGRatios() {
      if(CanTriggerTradeDirection()) {
        var perms = TrendLinesTrendsAll.Take(2).ToArray().Permutation().ToArray();
        var avg1s = perms
         .Select(c => c.Item1.PriceAvg1.Abs(c.Item2.PriceAvg1))
         .Average();
        var heights = perms
         .Select(c => c.Item1.StDev.Percentage(c.Item2.StDev))
         .Average();
        SetVolts(InPips(avg1s) * heights * 100, true);
      }
      return null;
    }
    void ShowVoltsByTrendsMins(IEnumerable<Rate.TrendLevels> tls, Action<double> doVolt) {
      if(CanTriggerTradeDirection()) {
        var perms = tls.ToArray().Permutation().ToArray();
        var avg1s = perms
         .Select(c => c.Item1.PriceAvg1.Abs(c.Item2.PriceAvg1))
         .Min();
        var heights = perms
         .Select(c => c.Item1.StDev.Abs(c.Item2.StDev))
         .Min();
        doVolt(/*InPips(avg1s) * */Math.Sqrt(InPips(heights)));
      }
    }
    void ShowVoltsByTrendsMax(IEnumerable<Rate.TrendLevels> tls, Action<double> doVolt) {
      if(CanTriggerTradeDirection()) {
        var perms = tls.ToArray().Permutation().ToArray();
        var heights = perms
         .Select(c => c.Item1.StDev.Abs(c.Item2.StDev))
         .Max();
        doVolt(Math.Sqrt(InPips(heights)));
      }
    }
    void ShowVoltsByHrStdTrendsRatios(IEnumerable<Rate.TrendLevels> tls, Action<double> doVolt) {
      if(CanTriggerTradeDirection()) {
        var perms = tls.ToArray().Permutation().ToArray();
        var avg1s = perms
         .Select(c => c.Item1.PriceAvg1.Abs(c.Item2.PriceAvg1))
         .StandardDeviation();
        var heights = perms
         .Select(c => c.Item1.StDev.Abs(c.Item2.StDev))
         .RelativeStandardDeviation();
        doVolt(InPips(avg1s) * heights * 100);
      }
    }
    void ShowVoltsByTrendsRatios3(IEnumerable<Rate.TrendLevels> tls, Action<double> doVolt) {
      if(CanTriggerTradeDirection()) {
        var perms = tls.ToArray().Permutation().ToArray();
        var avg1s = perms
         .Select(c => c.Item1.PriceAvg1.Abs(c.Item2.PriceAvg1))
         .StandardDeviation();
        var heights = perms
         .Select(c => c.Item1.StDev.Abs(c.Item2.StDev))
         .StandardDeviation();
        doVolt(InPips(avg1s) * InPips(heights));
      }
    }

    private IEnumerable<double> CutCmaCorners() {
      return (from rates in UseRates(rate => { var l = rate.Where(r => r.PriceCMALast.IsNotNaN()).ToList(); l.Reverse(); return l; })
              from zip in rates.Zip(rates.Skip(1), (r1, r2) => new { r1, r2 })
              select new {
                r = zip.r1,
                dir = new {
                  d1 = zip.r1.PriceAvg.Sign(zip.r1.PriceCMALast),
                  d2 = zip.r2.PriceAvg.Sign(zip.r2.PriceCMALast)
                }
              }
       ).SkipWhile(x => x.dir.d1 == x.dir.d2)
       .Select(x => x.r.PriceAvg - x.r.PriceCMALast);
    }
    private static List<Rate> CutCmaCorners(List<Rate> rates) {
      var start = CmaCornerIndex(rates);
      rates.Reverse();
      var end = CmaCornerIndex(rates);
      rates.Reverse();
      return rates.GetRange(start, rates.Count - start - end);
    }
    private static int CmaCornerIndex(List<Rate> rates) {
      return rates.Select(r => r.PriceAvg.SignUp(r.PriceCMALast))
        .Scan(0, (d, dir) => d == 0 ? dir : dir == d ? d : 0)
        .TakeWhile(d => d != 0)
        .Count();
    }
    private static List<double> CutCmaCorners(List<double> rates) {
      var start = CmaCornerIndex(rates);
      rates.Reverse();
      var end = CmaCornerIndex(rates);
      rates.Reverse();
      return rates.GetRange(start, rates.Count - start - end);
    }
    private static int CmaCornerIndex(List<double> rates) {
      return rates
        .Scan(0, (d, dir) => d == 0 ? dir.SignUp() : dir.SignUp() == d ? d : 0)
        .TakeWhile(d => d != 0)
        .Count();
    }

    double CalcTicksPerSecond(IList<Rate> rates) {
      if(BarPeriod != BarsPeriodType.t1)
        return 1;
      return rates.CalcTicksPerSecond();
    }
    int GetCrossesCount(IList<Rate> rates, double level) {
      return rates.Count(r => level.Between(r.BidLow, r.AskHigh));
    }
    double _streatchRatesMaxRatio = 1;

    [Category(categoryXXX)]
    public double StreatchRatesMaxRatio {
      get { return _streatchRatesMaxRatio; }
      set {
        _streatchRatesMaxRatio = value;
        OnPropertyChanged(() => StreatchRatesMaxRatio);
      }
    }
    public bool HasRates { get { return RatesArray.Count > 0; } }
    private List<Rate> GetRatesSafe(IList<Rate> ri) {
      Func<List<Rate>> a = () => {
        var barsCount = BarsCountCalc;
        var startDate = CorridorStartDate ?? (CorridorStats.Rates.Count > 0 ? CorridorStats.Rates.LastBC().StartDate : (DateTime?)null);
        var countByDate = startDate.HasValue && DoStreatchRates ? ri.Count(r => r.StartDate >= startDate).Min((barsCount * StreatchRatesMaxRatio).ToInt()) : 0;
        var countFinal = (countByDate * 1.05).Max(barsCount).ToInt().Min(ri.Count);
        return ri.ToList().GetRange(ri.Count - countFinal, countFinal);
        //return RatesInternal.Skip((RatesInternal.Count - (countByDate * 1).Max(BarsCount)).Max(0));
      };
      return _limitBarToRateProvider == (int)BarPeriod ? a() : ri.GetMinuteTicks((int)BarPeriod, false, false);
    }
    IEnumerable<Rate> GetRatesForStDev(IEnumerable<Rate> rates) {
      return rates.Reverse().Take(BarsCountCalc).Reverse();
    }
    ReactiveList<Rate> _Rates = new ReactiveList<Rate>();
    ReactiveList<Rate> RatesInternal {
      get {
        return _Rates;
      }
    }
    /// <summary>
    /// Returns instant deep copy of Rates
    /// </summary>
    /// <returns></returns>
    ICollection<Rate> RatesCopy() { return RatesArraySafe.Select(r => r.Clone() as Rate).ToList(); }

    public double InPoints(double d) {
      return TradesManager == null ? double.NaN : TradesManager.InPoints(Pair, d);
    }

    [ResetOnPair]
    double _pointSize = double.NaN;
    public double PointSize {
      get {
        if(double.IsNaN(_pointSize))
          _pointSize = TradesManager == null ? double.NaN : TradesManager.GetPipSize(Pair);
        return _pointSize;
      }
    }

    #region PipAmount
    public double PipAmount {
      get { return TradesManagerStatic.PipAmount(Pair, AllowedLotSizeCore(), TradesManager.RateForPipAmount(CurrentPrice.Ask, CurrentPrice.Bid), PointSize); }
    }

    #endregion
    public double PipAmountPercent {
      get { return PipAmount / Account.Balance; }
    }

    [ResetOnPair]
    double _pipCost = double.NaN;
    public double PipCost {
      get {
        if(double.IsNaN(_pipCost))
          _pipCost = TradesManager.GetPipCost(Pair);
        return _pipCost;
      }
    }

    double _HeightFib;

    public double HeightFib {
      get { return _HeightFib; }
      set {
        if(_HeightFib == value)
          return;
        _HeightFib = value;
        OnPropertyChanged("HeightFib");
      }
    }

    Trade _lastTrade;

    public Trade LastTrade {
      get { return _lastTrade ?? (_lastTrade = TradesManager.TradeFactory(Pair)); }
      set {
        if(value == null)
          return;
        _lastTrade = value;
        OnPropertyChanged("LastTrade");
        OnPropertyChanged("LastLotSize");
      }
    }

    #region LastTradeLossInPips
    private double _LastTradeLossInPips;
    [Category(categoryTrading)]
    [IsNotStrategy]
    public double LastTradeLossInPips {
      get { return _LastTradeLossInPips; }
      set {
        if(_LastTradeLossInPips != value) {
          _LastTradeLossInPips = value;
          OnPropertyChanged("LastTradeLossInPips");
        }
      }
    }

    #endregion
    #region UseLastLoss
    private bool _UseLastLoss;
    [WwwSetting(wwwSettingsTradingProfit)]
    [Category(categoryActiveYesNo)]
    public bool UseLastLoss {
      get { return _UseLastLoss; }
      set {
        if(_UseLastLoss != value) {
          if(value)
            IsTakeBack = false;
          _UseLastLoss = value;
          Log = new Exception(new { IsTakeBack } + "");
          OnPropertyChanged("UseLastLoss");
        }
      }
    }

    #endregion
    public int MaxLotSize {
      get {
        return MaxLotByTakeProfitRatio.ToInt() * LotSize;
      }
    }

    private double _Profitability;
    public double Profitability {
      get { return _Profitability; }
      set {
        if(_Profitability != value) {
          _Profitability = value;
          OnPropertyChanged(Metadata.TradingMacroMetadata.Profitability);
          OnPropertyChanged(Metadata.TradingMacroMetadata.ProfitabilityRatio);
        }
      }
    }

    public double ProfitabilityRatio {
      get { return Profitability / MinimumGross; }
    }

    private double _RunningBalance;
    public double RunningBalance {
      get { return _RunningBalance; }
      set {
        if(_RunningBalance != value) {
          _RunningBalance = value;
          OnPropertyChanged("RunningBalance");
        }
      }
    }
    void ResetMinimumGross() {
      MinimumGross = double.NaN;
      MinimumOriginalProfit = double.NaN;
    }
    private double _MinimumGross = double.NaN;
    public double MinimumGross {
      get { return _MinimumGross; }
      set {
        if(_MinimumGross.IsNaN() || value.IsNaN() || _MinimumGross > value) {
          _MinimumGross = value;
          OnPropertyChanged("MinimumGross");
        }
      }
    }
    #region MinimumOriginalProfit
    private double _MinimumOriginalProfit = double.NaN;
    public double MinimumOriginalProfit {
      get { return _MinimumOriginalProfit; }
      set {
        if(!value.IsNaN() && _MinimumOriginalProfit < value)
          return;
        {
          _MinimumOriginalProfit = value;
          OnPropertyChanged("MinimumOriginalProfit");
        }
      }
    }

    #endregion
    private int _HistoryMinimumPL;
    public int HistoryMinimumPL {
      get { return _HistoryMinimumPL; }
      set {
        if(_HistoryMinimumPL != value) {
          _HistoryMinimumPL = value;
          OnPropertyChanged("HistoryMinimumPL");
        }
      }
    }

    private int _HistoryMaximumLot;
    public int HistoryMaximumLot {
      get { return _HistoryMaximumLot; }
      set {
        if(_HistoryMaximumLot != value) {
          _HistoryMaximumLot = value;
          OnPropertyChanged("HistoryMaximumLot");
        }
      }
    }
    public int GetTradesGlobalCount() {
      return TradesManager.GetTrades().Select(t => t.Pair).Distinct().Count();
    }
    int _tradesCount = 0;
    public Trade[] Trades {
      get {
        Trade[] trades = TradesManager == null ? new Trade[0] : TradesManager.GetTrades(Pair);/* _trades.ToArray();*/
        if(_tradesCount != trades.Length) {
          OnTradesCountChanging(trades.Length, _tradesCount);
          _tradesCount = trades.Length;
        }
        return trades;
      }
      //set {
      //  _trades.Clear();
      //  _trades.AddRange(value);
      //  if (value.Length > 0) ResetLock();
      //}
    }

    private void OnTradesCountChanging(int countNew, int countOld) {
      //new Action(() => BarPeriod = countNew > 0 ? 1 : 5).BeginInvoke(a => { }, null);
    }


    private Strategies _Strategy;
    [Category(categorySession)]
    [WwwSetting]
    [Dnr]
    public Strategies Strategy {
      get {
        return _Strategy;
      }
      set {
        if(_Strategy != value) {
          _Strategy = value;
          OnPropertyChanged(TradingMacroMetadata.Strategy);
          Task.Run(() => {
            _broadcastCorridorDateChanged();
          });
        }
      }
    }
    private bool _ShowPopup;
    public bool ShowPopup {
      get { return _ShowPopup; }
      set {
        _ShowPopup = value;
        OnPropertyChanged(TradingMacroMetadata.ShowPopup);
      }
    }
    private string _PopupText;
    public string PopupText {
      get { return _PopupText; }
      set {
        if(_PopupText != value) {
          _PopupText = value;
          ShowPopup = value != "";
          OnPropertyChanged(TradingMacroMetadata.PopupText);
        }
      }
    }

    #region Spread
    private double CalcSpreadForCorridor(IList<Rate> rates, int iterations = 1) {
      try {
        return rates.Spread(iterations);
        var spreads = rates.Select(r => r.AskHigh - r.BidLow).ToList();
        if(spreads.Count == 0)
          return double.NaN;
        var spreadLow = spreads.AverageByIterations(iterations, true);
        var spreadHight = spreads.AverageByIterations(iterations, false);
        if(spreadLow.Count == 0 && spreadHight.Count == 0)
          return CalcSpreadForCorridor(rates, iterations - 1);
        var sa = spreads.Except(spreadLow.Concat(spreadHight)).DefaultIfEmpty(spreads.Average()).Average();
        var sstdev = 0;// spreads.StDev();
        return sa + sstdev;
      } catch(Exception exc) {
        Log = exc;
        return double.NaN;
      }
    }

    double SpreadForCorridor { get; set; }
    public double SpreadForCorridorInPips { get { return InPips(SpreadForCorridor); } }

    #endregion

    public double TradingDistanceInPips {
      get {
        try {
          return InPips(TradingDistance).Max(_tradingDistanceMax);
        } catch {
          return double.NaN;
        }
      }
    }
    double _tradingDistance = double.NaN;
    public double TradingDistance {
      get {
        if(!HasRates)
          return double.NaN;
        _tradingDistance = GetValueByTakeProfitFunction(TradingDistanceFunction, TradingDistanceX);
        if(false) {
          var td1 = AllowedLotSizeCore(TradingDistanceInPips)
            .ValueByPosition(LotSize, LotSize * MaxLotByTakeProfitRatio, _tradingDistance, RatesHeight / _ratesHeightAdjustmentForAls)
            .Min(RatesHeight / _ratesHeightAdjustmentForAls);
          if(td1 > 0) {
            if((td1 - _tradingDistance).Abs() / _tradingDistance > .1)
              _tradingDistance = td1;
          } else {
            td1 = _tradingDistance;
          }
        }
        return _tradingDistance;// td;
      }
    }

    Playback _Playback = new Playback();
    public void SetPlayBackInfo(bool play, DateTime startDate, TimeSpan delay) {
      _Playback.Play = play;
      _Playback.StartDate = startDate;
      _Playback.Delay = delay;
    }
    public bool IsInPlayback { get { return _Playback.Play || (SnapshotArguments.DateStart ?? SnapshotArguments.DateEnd) != null || SnapshotArguments.IsTarget; } }

    enum workers {
      LoadRates, ScanCorridor, RunPrice
    };
    Schedulers.BackgroundWorkerDispenser<workers> bgWorkers = new Schedulers.BackgroundWorkerDispenser<workers>();

    void AddCurrentTick(Price price) {
      UseRatesInternal(ri => {
        if(BarPeriod != BarsPeriodType.t1 && (!ri.Any() || !HasRates))
          return;
        var isTick = BarPeriod == BarsPeriodType.t1;
        if(BarPeriod == 0) {
          ri.Add(isTick ? new Tick(price, 0, false) : new Rate(price, false));
        } else {
          var lastDate = ri.Last().StartDate.Round();
          if(price.Time.Round() > lastDate) {
            ri.Add(isTick ? new Tick(price, 0, false) : new Rate(lastDate.AddMinutes((int)BarPeriod).ToUniversalTime(), price.Ask, price.Bid, false));
            OnLoadRates();
          } else
            ri.Last().AddTick(price.Time.Round().ToUniversalTime(), price.Ask, price.Bid);
        }
      });
    }


    public double RoundPrice(Rate rate) {
      return RoundPrice(rate.PriceAvg, 0);
    }
    public double RoundPrice(double price, int digitOffset = 0) {
      return TradesManager == null ? double.NaN : TradesManager.Round(Pair, price, digitOffset);
    }

    private bool _isPriceSpreadOk;
    public bool IsPriceSpreadOk {
      get {
        if(!_isPriceSpreadOk)
          Log = new Exception(new { _isPriceSpreadOk } + "");
        return _isPriceSpreadOk;
      }
      set {
        if(_isPriceSpreadOk == value)
          return;
        _isPriceSpreadOk = value;
        OnPropertyChanged(() => IsPriceSpreadOk);
      }
    }
    public void SetPriceSpreadOk() {
      IsPriceSpreadOk = this.CurrentPrice.Spread < this.PriceSpreadAverage * 3;
    }
    static TradingMacro() {
      Scheduler.Default.Schedule(5.FromSeconds(), () => {
        var dups = ((TrailingWaveMethod)0).HasDuplicates();
        if(dups.Any())
          GalaSoft.MvvmLight.Messaging.Messenger.Default.Send(new Exception(string.Join(Environment.NewLine, dups)));
      });
    }

    private Order2GoAddIn.FXCoreWrapper GetFXWraper(bool failTradesManager = true) {
      if(TradesManager == null)
        if(failTradesManager)
          FailTradesManager();
        else
          Log = new Exception("Request to TradesManager failed. TradesManager is null.");
      return TradesManager as Order2GoAddIn.FXCoreWrapper;
    }

    private static void FailTradesManager() {
      Debug.Fail("TradesManager is null", (new NullReferenceException()) + "");
    }

    public double CalcTakeProfitDistance(bool inPips = false) {
      if(Trades.Length == 0)
        return double.NaN;
      var fw = GetFXWraper();
      if(fw == null)
        return double.NaN;
      var netOrder = fw.GetNetLimitOrder(Trades.LastTrade());
      if(netOrder == null)
        return double.NaN;
      var netOpen = Trades.NetOpen();
      var ret = !netOrder.IsBuy ? netOrder.Rate - netOpen : netOpen - netOrder.Rate;
      return inPips ? InPips(ret) : ret;
    }

    bool? _magnetDirtection;
    DateTime? _corridorTradeDate;

    Action StrategyAction {
      get {
        switch((Strategy & ~Strategies.Auto)) {
          case Strategies.Hot:
            return StrategyEnterUniversal;
          case Strategies.Universal:
            return StrategyEnterUniversal;
          case Strategies.None:
            return () => { };
        }
        throw new NotSupportedException("Strategy " + Strategy + " is not supported.");
      }
    }
    bool _isSelfStrategy = false;
    void RunStrategy() {
      StrategyAction();
    }
    double? _buyPriceToLevelSign;
    double? _sellPriceToLevelSign;
    delegate double GetPriceLastForTradeLevelDelegate();
    delegate double GetPricePrevForTradeLevelDelegate();


    private void OpenTradeWithReverse(bool isBuy) {
      CheckPendingAction("OT", (pa) => {
        var lotClose = Trades.IsBuy(!isBuy).Lots();
        var lotOpen = AllowedLotSizeCore();
        var lot = lotClose + lotOpen;
        if(lot > 0) {
          pa();
          TradesManager.OpenTrade(Pair, isBuy, lot, 0, 0, "", null);
        }
      });
    }

    public void CalculatePriceLastAndPrev(out double priceLast, out double pricePrev) {
      priceLast = CalculateLastPrice(RatesArray.LastBC(), r => r.PriceAvg);
      pricePrev = RatePrev1.PriceAvg;
    }

    bool _useTakeProfitMin = false;
    Action<Trade> _strategyExecuteOnTradeClose;
    Action<Trade> _strategyExecuteOnTradeOpen;
    Action<TradeLineChangedMessage> _strategyOnTradeLineChanged;
    double _tradingDistanceMax = 0;

    private void TurnOffSuppRes(double level = double.NaN) {
      var rate = double.IsNaN(level) ? SuppRes.Average(sr => sr.Rate) : level;
      foreach(var sr in SuppRes)
        sr.RateEx = rate;
    }
    public void OpenTrade(bool isBuy, int lot, string reason) {
      var key = lot - Trades.Lots(t => t.IsBuy != isBuy) > 0 ? "OT" : "CT";
      CheckPendingAction(key, (pa) => {
        if(lot > 0) {
          pa();
          LogTradingAction(string.Format("{0}[{1}]: {2} {3} from {4} by [{5}]", Pair, BarPeriod, isBuy ? "Buying" : "Selling", lot, new StackFrame(3).GetMethod().Name, reason));
          TradesManager.OpenTrade(Pair, isBuy, lot, 0, 0, "", null);
        }
      });
    }

    public void TrimTrades(string reason) { CloseTrades(Trades.Lots() - LotSize, reason); }
    public void CloseTrades(string reason) { CloseTrades(Trades.Lots(), reason); }
    private void CloseTrades(int lot, string reason) {
      if(!Trades.Any())
        return;
      if(HasPendingKey("CT"))
        return;
      if(lot > 0)
        CheckPendingAction("CT", pa => {
          pa();
          LogTradingAction(string.Format("{0}[{1}]: Closing {2} from {3} in {4} from {5}]"
            , Pair, BarPeriod, lot, Trades.Lots(), new StackFrame(3).GetMethod().Name, reason));
          if(!TradesManager.ClosePair(Pair, Trades[0].IsBuy, lot))
            ReleasePendingAction("CT");
        });
    }

    #region GetEntryOrders
    private Order[] GetEntryOrders() {
      Order2GoAddIn.FXCoreWrapper fw = GetFXWraper(false);
      return fw == null ? new Order[0] : fw.GetEntryOrders(Pair);
    }
    private Order[] GetEntryOrders(bool isBuy) {
      return GetEntryOrders().IsBuy(isBuy);
    }
    #endregion

    Schedulers.TaskTimer _runPriceChangedTasker = new Schedulers.TaskTimer(100);
    Schedulers.TaskTimer _runPriceTasker = new Schedulers.TaskTimer(100);
    public void RunPriceChanged(PriceChangedEventArgs e, Action<TradingMacro> doAfterScanCorridor) {
      if(TradesManager != null) {
        try {
          RunPriceChangedTask(e, doAfterScanCorridor);
        } catch(Exception exc) {
          Log = exc;
        }
      }
    }

    private SuppRes[] EnsureActiveSuppReses() {
      return EnsureActiveSuppReses(true).Concat(EnsureActiveSuppReses(false)).OrderBy(sr => sr.Rate).ToArray();
    }
    private SuppRes[] EnsureActiveSuppReses(bool isBuy, bool doTrades = false) {
      var hasTrades = doTrades && HasTradesByDistance(Trades.IsBuy(isBuy));
      var isActiveCommon = true;// !IsCold && IsHotStrategy && HasCorridor;
      var isActiveByBuy = true;//rateLast == null ? true : !isBuy ? rateLast.PriceAvg < rateLast.PriceAvg1 : rateLast.PriceAvg > rateLast.PriceAvg1;
      SuppRes.IsBuy(isBuy).ToList().ForEach(sr => sr.IsActive = ForceOpenTrade.HasValue || !hasTrades && isActiveByBuy && isActiveCommon);
      return SuppRes.Active(isBuy);
    }

    private bool HasTradesByDistance(bool isBuy) {
      return HasTradesByDistance(Trades.IsBuy(isBuy));
    }
    private bool HasTradesByDistance(Trade[] trades) {
      if(MaximumPositions <= trades.Positions(LotSize))
        return true;
      var td = TradingDistanceInPips;//.Max(trades.DistanceMaximum());
      return CalculateTakeProfit() == 0 || double.IsNaN(TradingDistance) || HasTradesByDistanceDelegate(trades, td);
    }

    delegate bool HasTradesByDistanceCustom(Trade[] trades, double tradingDistanceInPips);
    HasTradesByDistanceCustom _hasTradesByDistanceDelegate;
    private HasTradesByDistanceCustom HasTradesByDistanceDelegate {
      get { return _hasTradesByDistanceDelegate ?? HasTradesByDistanceAndPL; }
      set { _hasTradesByDistanceDelegate = value; }
    }
    private bool HasTradesByDistanceAndPL(Trade[] trades, double tradeDistanceInPips) {
      return (trades.Any() && trades.Max(t => t.PL) > -(tradeDistanceInPips + PriceSpreadAverageInPips));
    }

    private bool HasTradesByDistanceAndCurrentGross(Trade[] trades, double tradeDistanceInPips) {
      return (trades.Any() && CurrentGrossInPips.Min(0).Abs() < (tradeDistanceInPips + PriceSpreadAverageInPips));
    }
    public void RunPriceChangedTask(PriceChangedEventArgs e, Action<TradingMacro> doAfterScanCorridor) {
      try {
        if(TradesManager == null)
          return;
        Price price = e.Price;
        #region LoadRates
        if(!TradesManager.IsInTest && !IsInPlayback
          && (!UseRatesInternal(ri => ri.Any()).DefaultIfEmpty(true).Single() || LastRatePullTime.AddMinutes(0.3.Max((double)BarPeriod / 2)) <= ServerTime)) {
          LastRatePullTime = ServerTime;
          OnLoadRates();
        }
        #endregion
        OnRunPriceBroadcast(e);
        if(doAfterScanCorridor != null)
          doAfterScanCorridor.BeginInvoke(this, ar => { }, null);
      } catch(Exception exc) {
        Log = exc;
      }
    }
    public double GetPriceMA(Rate rate) {
      var ma = GetPriceMA()(rate);
      if(ma <= 0) {
        var msg = "Price MA must be more than Zero!";
        Debug.Fail(msg);
        throw new InvalidDataException(msg);
      }
      return ma;
    }
    public double GetPriceMA2(Rate rate) {
      return GetPriceMA2()(rate);
    }
    public Func<Rate, double> GetPriceMA2() {
      return r => r.PriceRsiP;
    }
    public Func<Rate, double> GetPriceMA() {
      return GetPriceMA(MovingAverageType);
    }
    private static Func<Rate, double> GetPriceMA(MovingAverageType movingAverageType) {
      switch(movingAverageType) {
        case Store.MovingAverageType.FFT:
        case Store.MovingAverageType.FFT2:
        case Store.MovingAverageType.Cma:
          return r => r.PriceCMALast;
        default:
          throw new NotSupportedException(new { movingAverageType }.ToString());
      }
    }
    double _sqrt2 = 1.5;// Math.Sqrt(1.5);

    public double CmaPeriodByRatesCount() { return CmaPeriodByRatesCount(RatesArray.Count); }
    public double CmaPeriodByRatesCount(int count) {
      return PriceCmaLevels >= 1
        ? PriceCmaLevels
        : Math.Pow(count, PriceCmaLevels).ToInt();
    }
    #region SmaPasses
    private int _CmaPasses = 1;
    [Category(categoryActive)]
    [WwwSetting(wwwSettingsCorridorCMA)]
    public int CmaPasses {
      get { return _CmaPasses; }
      set {
        if(_CmaPasses != value) {
          if(value <= 0)
            Log = new Exception(new { CmaPasses = value, Error = "Must be > 0" } + "");
          else {
            _CmaPasses = value;
            OnPropertyChanged("CmaPasses");
          }
        }
      }
    }

    #endregion
    private void SetMA(IList<Rate> rates) {
      switch(MovingAverageType) {
        case Store.MovingAverageType.FFT:
          SetMAByFft(rates, _priceAvg, (rate, d) => rate.PriceCMALast = d, PriceCmaLevels.Div(10));
          break;
        case Store.MovingAverageType.FFT2:
          SetMAByFft2(RatesArray, _priceAvg, (rate, d) => rate.PriceCMALast = d, PriceCmaLevels.Div(10));
          break;
        case Store.MovingAverageType.Cma:
          SetCma(rates);
          break;
      }
    }

    private IList<double> GetCma(IList<Rate> rates, int? count = null, double? cmaPeriod = null, int? cmaPasses = null) {
      count = count ?? rates.Count;
      cmaPeriod = cmaPeriod ?? CmaPeriodByRatesCount(count.Value);
      cmaPasses = cmaPasses ?? CmaPasses;
      var cmas = rates.Cma(_priceAvg, cmaPeriod.Value);
      if(rates.Count != cmas.Count)
        throw new Exception("rates.Count != cmas.Count");
      for(var i = cmaPasses; i > 1; i--)
        cmas = cmas.Cma(cmaPeriod.Value);
      return cmas;
    }
    private IList<double> GetCma(IList<double> rates, int? count = null) {
      count = count ?? rates.Count;
      var cmaPeriod = CmaPeriodByRatesCount(count.Value);
      var cmas = rates.Cma(cmaPeriod);
      if(rates.Count != cmas.Count)
        throw new Exception("rates.Count != cmas.Count");
      for(var i = CmaPasses; i > 1; i--)
        cmas = cmas.Cma(cmaPeriod);
      return cmas;
    }
    private void SetCma(IList<Rate> rates) {
      // Set primary CMA
      var cmas = GetCma(rates);
      for(var i = 0; i < rates.Count; i++)
        rates[i].PriceCMALast = cmas[i];
      // Set secondary CMA
      if(CmaRatioForWaveLength > 0) {
        var cmas2 = GetCma2(cmas);
        for(var i = 0; i < rates.Count; i++)
          rates[i].PriceRsiP = cmas2[i];
      }
    }
    private IList<Tuple<Rate, double, double>> GetCmas(IList<Rate> rates, double period, int cmaPasses) {
      // Set primary CMA
      var cmas = GetCma(rates, (int?)null, period, cmaPasses);
      // Set secondary CMA
      return rates.Zip(cmas, Tuple.Create).Zip(GetCma2(cmas), (t, c) => Tuple.Create(t.Item1, t.Item2, c)).ToList();
    }

    private IList<double> GetCma2(IList<double> cmas, int? count = null, double? period = null) {
      count = count ?? cmas.Count;
      var cmaPeriod = period ?? CmaPeriodByRatesCount(count.Value);
      var cmas2 = cmas.Cma(cmaPeriod * CmaRatioForWaveLength);
      //for(var i = CmaRatioForWaveLength; i > 1; i--)
      //  cmas2 = cmas2.Cma(cmaPeriod);
      return cmas2;
    }

    private static void SetMAByFft(IList<Rate> rates, Func<Rate, double> getPrice, Action<Rate, double> setValue, double lastHarmonicRatioIndex) {
      rates.Zip(rates.ToArray(getPrice).Fft(lastHarmonicRatioIndex), (rate, d) => { setValue(rate, d); return 0; }).Count();
    }
    private static void SetMAByFft2(IList<Rate> rates, Func<Rate, double> getPrice, Action<Rate, double> setValue, double lastHarmonicRatioIndex) {
      rates.Zip(rates.ToArray(getPrice).Fft(lastHarmonicRatioIndex).Fft(lastHarmonicRatioIndex), (rate, d) => { setValue(rate, d); return 0; }).Count();
    }
    private IEnumerable<double> GetCma<T>(IEnumerable<T> rates, Func<T, double> value, double period) {
      return rates.Scan(double.NaN, (ma, r) => ma.Cma(period, value(r)))
              .Reverse()
              .Scan(double.NaN, (ma, d) => ma.Cma(period, d));
    }

    public void ScanCorridor(List<Rate> ratesForCorridor, Action callback = null) {
      try {
        if(!IsActive || !isLoggedIn || !HasRates /*|| !IsTradingHours(tm.Trades, rates.Last().StartDate)*/)
          return;
        var showChart = ratesForCorridor.Any();// CorridorStats == null || CorridorStats.Rates.Count == 0;
        #region Prepare Corridor
        Func<Rate, double> priceHigh = CorridorGetHighPrice();
        Func<Rate, double> priceLow = CorridorGetLowPrice();
        var crossedCorridor = GetScanCorridorFunction(ScanCorridorBy)(ratesForCorridor, priceHigh, priceLow);
        if(crossedCorridor.Rates.Count == 0)
          return;
        #endregion
        #region Update Corridor
        var csOld = CorridorStats;
        csOld.Init(crossedCorridor, PointSize);
        csOld.Spread = double.NaN;
        CorridorStats = csOld;
        CorridorStats.IsCurrent = true;// ok;// crossedCorridor != null;
        #endregion
        PopupText = "";
        if(showChart)
          RaiseShowChart();
        if(callback != null)
          callback();
      } catch(Exception exc) {
        Log = exc;
        //PopupText = exc.Message;
      }
      //Debug.WriteLine("{0}[{2}]:{1:n1}ms @ {3:mm:ss.fff}", MethodBase.GetCurrentMethod().Name, sw.ElapsedMilliseconds, Pair,DateTime.Now);
    }

    delegate CorridorStatistics ScanCorridorDelegate(List<Rate> ratesForCorridor, Func<Rate, double> priceHigh, Func<Rate, double> priceLow);

    Trade _tradeForProfit;
    private Trade TradeForCommissionCalculation() {
      var trade = _tradeForProfit ?? (_tradeForProfit = TradesManager.TradeFactory(Pair));
      trade.Lots = LotSizeByLossBuy.Max(LotSizeByLossSell);
      return trade;
    }
    double CommissionInPips() {
      IList<Trade> trades = Trades.DefaultIfEmpty(Trades.LastOrDefault() ?? TradeForCommissionCalculation()).ToList();
      var trade = trades.First();
      if(trades.Lots() == 0)
        return 0;
      var com = CommissionByTrade(trade);
      var rate = TradesManager.RateForPipAmount(CurrentPrice);
      return TradesManagerStatic.MoneyAndLotToPips(Pair, com, trades.Lots(), rate, PointSize);
    }
    public double CalculateTakeProfitInPips(double customRatio = double.NaN, bool dontAdjust = true) {
      return InPips(CalculateTakeProfit(customRatio, dontAdjust));
    }
    public double CalculateTakeProfit(double customRatio = double.NaN, bool dontAdjust = true) {
      var tp = GetValueByTakeProfitFunction(TakeProfitFunction, customRatio.IfNaN(TakeProfitXRatio));
      return (dontAdjust
        ? tp
        : tp.Max(PriceSpreadAverage.GetValueOrDefault(double.NaN) * 2)
        );// + InPoints(CommissionInPips());
    }
    double CalculateTradingDistance(double customRatio = double.NaN) {
      return GetValueByTakeProfitFunction(TradingDistanceFunction, customRatio.IfNaN(TradingDistanceX));
    }


    #region TakeProfitBSRatio
    private double _TakeProfitXRatio = 1;
    [WwwSetting(Group = wwwSettingsTrading)]
    [Description("TakeProfit = (BuyLevel-SellLevel)*X")]
    [Category(categoryActiveFuncs)]
    public double TakeProfitXRatio {
      get { return _TakeProfitXRatio; }
      set {
        if(_TakeProfitXRatio != value) {
          _TakeProfitXRatio = value.Max(0.1);
          OnPropertyChanged("TakeProfitXRatio");
          //if (value > 0) TakeProfitFunction = TradingMacroTakeProfitFunction.BuySellLevels_X;
        }
      }
    }

    #endregion

    #region RatesHeightXRatio
    private double _TradingDistanceX = 1;
    [WwwSetting(Group = wwwSettingsTradingConditions)]
    [Description("TradingDistance = RetasHeight * X")]
    [Category(categoryActiveFuncs)]
    public double TradingDistanceX {
      get { return _TradingDistanceX; }
      set {
        if(_TradingDistanceX != value) {
          _TradingDistanceX = value;
          OnPropertyChanged("TradingDistanceX");
        }
      }
    }

    #endregion

    #region IsTrader
    private bool _IsTrader;
    [Category(categoryTrading)]
    [WwwSetting]
    public bool IsTrader {
      get { return TradingMacrosByPair().Count() == 1 || _IsTrader; }
      set {
        if(_IsTrader != value) {
          _IsTrader = value;
          OnPropertyChanged("IsTrader");
          var tmo = TradingMacroOther();
          if(!value)
            tmo.Take(1).ForEach(tm => tm.IsTrader = true);
          else
            tmo.ForEach(tm => tm.IsTrader = false);
        }
      }
    }

    #endregion

    #region IsTrender
    private bool _IsTrender;
    [Category(categoryActiveYesNo)]
    [WwwSetting()]
    public bool IsTrender {
      get { return _IsTrender; }
      set {
        if(_IsTrender != value) {
          _IsTrender = value;
          //LevelBuyCloseBy = LevelSellCloseBy = TradeLevelBy.None;
          SuppRes.ForEach(sr => sr.ResetPricePosition());
          OnPropertyChanged("IsTrender");
          var tmo = TradingMacroOther();
          if(!value && !TradingMacrosByPair().Any(tm => tm.IsTrender))
            tmo.Take(1).DefaultIfEmpty(this)
              .ForEach(tm => tm.IsTrender = true);
          else if(value)
            tmo.ForEach(tm => tm.IsTrender = false);
        }
      }
    }

    #endregion

    double GetTradeLevel(SuppRes supRes) {
      return GetTradeLevel(supRes.IsBuy, supRes.Rate);
    }
    double GetTradeLevel(bool buy, double def) {
      return GetTradeLevel(buy, () => def);
    }
    double GetTradeLevel(bool buy, Func<double> def) {
      return TradeLevelFuncs[buy ? LevelBuyBy : LevelSellBy]().IfNaN(def());
    }

    class BolingerBanderAsyncBuffer : AsyncBuffer<BolingerBanderAsyncBuffer, Action> {
      protected override Action PushImpl(Action context) { return context; }
    }
    BolingerBanderAsyncBuffer _boilingerBanderAsyncAction = new BolingerBanderAsyncBuffer();

    Lazy<double[]> _boilingerStDev = Lazy.Create(()=> new double[0]);
    void CalcBoilingerBand() {
      _boilingerStDev = Lazy.Create(() => UseRates(rate => rate.Select(r => r.PriceCMALast.Abs(r.PriceAvg)).StandardDeviation()));
    }

    public static IEnumerable<double> GetLastRateCma(List<Rate> rate) {
      return rate.BackwardsIterator().Select(r => r.PriceCMALast).SkipWhile(double.IsNaN).Take(1);
    }

    double _bbRatio = 4;
    [Category(categoryActive)]
    //[WwwSetting]
    public double BbRatio {
      get {
        return _bbRatio;
      }

      set {
        _bbRatio = value;
      }
    }
    WaveSmoothBys _waveSmoothBy = WaveSmoothBys.MinDist;
    [Category(categoryActiveFuncs)]
    [WwwSetting(wwwSettingsCorridorFuncs)]
    public WaveSmoothBys WaveSmoothBy {
      get { return _waveSmoothBy; }
      set {
        _waveSmoothBy = value;
        OnPropertyChanged(() => WaveSmoothBy);
      }
    }
    static Dictionary<WaveSmoothBys, Func<WaveRange, double>> _waveSmoothFuncs;
    static Dictionary<WaveSmoothBys, Func<WaveRange, double>> WaveSmoothFuncs {
      get {
        return _waveSmoothFuncs ?? (_waveSmoothFuncs = new Dictionary<Store.WaveSmoothBys, Func<WaveRange, double>> {
          {WaveSmoothBys.Distance,wr=>wr.Distance },
          {WaveSmoothBys.Minutes,wr=>wr.TotalMinutes },
          {WaveSmoothBys.StDev,wr=>wr.StDev },
          {WaveSmoothBys.MinDist,wr=>wr.TotalMinutes*wr.Distance }
        });
      }
    }
    Func<WaveRange, double> WaveSmoothFunc() {
      return WaveSmoothFuncs[WaveSmoothBy];
    }
    private double TradeLevelByPA2(int takeCount) {
      return TrendLevelsSorted(tl => tl.PriceAvg2, (d1, d2) => d1 > d2, takeCount).Average();
    }

    private double TradeLevelByPA3(int takeCount) {
      return TrendLevelsSorted(tl => tl.PriceAvg3, (d1, d2) => d1 < d2, takeCount).Average();
    }
    Dictionary<TradeLevelBy, Func<double>> _TradeLevelFuncs;
    Dictionary<TradeLevelBy, Func<double>> TradeLevelFuncs {
      get {
        var tmt = TradingMacroOther(tm => !tm.IsAsleep && tm.IsTrender && !tm.TLRed.IsEmpty).OrderBy(tm => tm.PairIndex).DefaultIfEmpty(this);
        if(!IsTrader)
          throw new Exception(new { TradeLevelFuncs = new { IsTrader } } + "");
        Func<double> maxDefault = () => UseRates(rates => rates.Max(_priceAvg)).DefaultIfEmpty(double.NaN).Single();
        Func<double> minDefault = () => UseRates(rates => rates.Min(_priceAvg)).DefaultIfEmpty(double.NaN).Single();
        Func<Func<TradingMacro, double>, double> level = f => f(tmt.Where(tm => tm.IsTrader).DefaultIfEmpty(tmt.First()).First());
        Func<Func<TradingMacro, double>, double> levelMax = f => tmt.Select(tm => f(tm)).DefaultIfEmpty(RatesMax).Max().IfNaN(maxDefault);
        Func<Func<TradingMacro, double>, double> levelMin = f => tmt.Select(tm => f(tm)).DefaultIfEmpty(RatesMin).Min().IfNaN(minDefault);
        Func<IEnumerable<double>, int, IEnumerable<double>> comm = (ps, sign) => ps.Select(p => p + InPoints(CommissionInPips()) * sign);
        //Func<Func<TL, IEnumerable<double>>, int, IEnumerable<double>> commTL = (ps, sign) => ps().Select(p => p + InPoints(CommissionInPips()) * sign);
        if(_TradeLevelFuncs == null)
          _TradeLevelFuncs = new Dictionary<TradeLevelBy, Func<double>>
          {
          {TradeLevelBy.BoilingerUp,()=>level(tm=> {
            return _boilingerStDev.Value.SelectMany(bb=> GetLastRateCma(RatesArray).Select(cma=>cma+bb*BbRatio).DefaultIfEmpty(double.NaN)).Single();
          })},
          {TradeLevelBy.BoilingerDown,()=>level(tm=> {
            return _boilingerStDev.Value.SelectMany(bb=> GetLastRateCma(RatesArray).Select(cma=>cma-bb*BbRatio).DefaultIfEmpty(double.NaN)).Single();
          })},

            { TradeLevelBy.PriceCma,()=>level(tm=>tm.UseRates(GetLastRateCma).SelectMany(cma=>cma).DefaultIfEmpty(double.NaN).Single()) },
          {TradeLevelBy.PriceAvg1,()=>level(tm=>tm.TrendLines.Value[1].Trends.PriceAvg1)},
          {TradeLevelBy.BlueAvg1,()=>level(tm=>tm.TLBlue.PriceAvg1)},
          {TradeLevelBy.GreenAvg1,()=>level(tm=>tm.TLGreen.PriceAvg1)},
          {TradeLevelBy.LimeAvg1,()=>level(tm=>tm.TLLime.PriceAvg1)},

          {TradeLevelBy.PriceAvg1Max,()=> TradeTrendLines.Max(tl=>tl.PriceAvg1)},
          {TradeLevelBy.PriceAvg1Min,()=> TradeTrendLines.Min(tl=>tl.PriceAvg1)},

          {TradeLevelBy.Avg22,()=>levelMax(tm=>tm.TradeLevelByPA2(2)) },
          {TradeLevelBy.Avg23,()=>levelMin(tm=>tm.TradeLevelByPA3(2)) },

          {TradeLevelBy.PriceAvg2,()=> levelMax(tm=>tm.TLRed.PriceAvg2)},
          {TradeLevelBy.PriceAvg3,()=> levelMin(tm=>tm.TLRed.PriceAvg3)},

          {TradeLevelBy.PriceRB2,()=> levelMax(tm=>tm.TLRed.PriceAvg2.Max( tm.TLBlue.PriceAvg2))},
          {TradeLevelBy.PriceRB3,()=> levelMin(tm=>tm.TLRed.PriceAvg3.Min( tm.TLBlue.PriceAvg3))},

          { TradeLevelBy.PriceHigh,()=> levelMax(tm=>tm.TLBlue.PriceAvg2)},
          {TradeLevelBy.PriceLow,()=> levelMin(tm=>tm.TLBlue.PriceAvg3)},

          {TradeLevelBy.PriceLimeH,()=> levelMax(tm=>tm.TLLime.PriceAvg2)},
          {TradeLevelBy.PriceLimeL,()=> levelMin(tm=>tm.TLLime.PriceAvg3)},

          {TradeLevelBy.PricePlumH,()=> levelMax(tm=>tm.TLPlum.PriceAvg2)},
          {TradeLevelBy.PricePlumL,()=> levelMin(tm=>tm.TLPlum.PriceAvg3)},

          {TradeLevelBy.PriceHigh0,()=> levelMax(tm=>tm.TLGreen.PriceAvg2)},
          {TradeLevelBy.PriceLow0,()=> levelMin(tm=>tm.TLGreen.PriceAvg3)},


          {TradeLevelBy.PriceMax,()=> levelMax(TradeTrendsPriceMax)},
          {TradeLevelBy.PriceMin,()=> levelMin(TradeTrendsPriceMin)},

          { TradeLevelBy.GreenStripH,()=> CenterOfMassBuy.IfNaN(TradeLevelFuncs[TradeLevelBy.PriceMax]) },
          {TradeLevelBy.GreenStripL,()=> CenterOfMassSell.IfNaN(TradeLevelFuncs[TradeLevelBy.PriceMin]) },

          {TradeLevelBy.LimeMax,()=> levelMax(tm=> comm(tm.TLLime.PriceMax,1).DefaultIfEmpty(double.NaN).Single()) },
          {TradeLevelBy.LimeMin,()=> levelMin(tm=> comm(tm.TLLime.PriceMin,-1).DefaultIfEmpty(double.NaN).Single()) },

          {TradeLevelBy.GreenMax,()=> levelMax(tm=> comm(tm.TLGreen.PriceMax,1).DefaultIfEmpty(double.NaN).Single()) },
          {TradeLevelBy.GreenMin,()=> levelMin(tm=> comm(tm.TLGreen.PriceMin,-1).DefaultIfEmpty(double.NaN).Single()) },

          {TradeLevelBy.RedMax,()=> levelMax(tm=> comm(tm.TLRed.PriceMax,1).DefaultIfEmpty(double.NaN).Single()) },
          {TradeLevelBy.RedMin,()=> levelMin(tm=> comm(tm.TLRed.PriceMin,-1).DefaultIfEmpty(double.NaN).Single()) },

          {TradeLevelBy.PlumMax,()=> levelMax(tm=> comm(tm.TLPlum.PriceMax,1).DefaultIfEmpty(double.NaN).Single()) },
          {TradeLevelBy.PlumMin,()=> levelMin(tm=> comm(tm.TLPlum.PriceMin,-1).DefaultIfEmpty(double.NaN).Single()) },

          {TradeLevelBy.BlueMax,()=> levelMax(tm=> comm(tm.TLBlue.PriceMax,1).DefaultIfEmpty(double.NaN).Single()) },
          {TradeLevelBy.BlueMin,()=> levelMin(tm=> comm(tm.TLBlue.PriceMin,-1).DefaultIfEmpty(double.NaN).Single()) },

          {TradeLevelBy.None,()=>double.NaN}
          };
        return _TradeLevelFuncs;
      }
    }
    private double GetValueByTakeProfitFunction(TradingMacroTakeProfitFunction function, double xRatio) {
      var tp = double.NaN;
      Func<TradeLevelBy, TradeLevelBy, double> tradeLeveBy = (h, l) => (TradeLevelFuncs[h]() - TradeLevelFuncs[l]()) * xRatio;
      Func<Func<TradingMacro, double>, double> useTrender = f => TradingMacroTrender(f).DefaultIfEmpty(double.NaN).Single();
      switch(function) {
        case TradingMacroTakeProfitFunction.StDev:
          return useTrender(tm => tm.StDevByHeight * xRatio + tm.InPoints(tm.CommissionInPips()) * 2);
        case TradingMacroTakeProfitFunction.M1StDev:
          return TradingMacroM1(tm => InPoints(tm.WaveRangeAvg.StDev) * xRatio + tm.InPoints(tm.CommissionInPips()) * 2)
            .DefaultIfEmpty(StDevByHeight)
            .Single();
        case TradingMacroTakeProfitFunction.Lime:
          tp = tradeLeveBy(TradeLevelBy.PriceLimeH, TradeLevelBy.PriceLimeL);
          break;
        case TradingMacroTakeProfitFunction.LimeMM:
          tp = tradeLeveBy(TradeLevelBy.LimeMax, TradeLevelBy.LimeMin);
          break;
        case TradingMacroTakeProfitFunction.Green:
          tp = tradeLeveBy(TradeLevelBy.PriceHigh0, TradeLevelBy.PriceLow0);
          break;
        case TradingMacroTakeProfitFunction.GreenMM:
          tp = tradeLeveBy(TradeLevelBy.GreenMax, TradeLevelBy.GreenMin);
          break;
        case TradingMacroTakeProfitFunction.Greenish:
          var tpGreen = tradeLeveBy(TradeLevelBy.PriceHigh0, TradeLevelBy.PriceLow0);
          var tpLime = tradeLeveBy(TradeLevelBy.PriceLimeH, TradeLevelBy.PriceLimeL);
          tp = tpGreen.Min(tpLime);
          break;
        case TradingMacroTakeProfitFunction.Red:
          tp = tradeLeveBy(TradeLevelBy.PriceAvg2, TradeLevelBy.PriceAvg3);
          break;
        case TradingMacroTakeProfitFunction.RedMM:
          tp = tradeLeveBy(TradeLevelBy.RedMax, TradeLevelBy.RedMin);
          break;
        case TradingMacroTakeProfitFunction.Plum:
          tp = tradeLeveBy(TradeLevelBy.PricePlumH, TradeLevelBy.PricePlumL);
          break;
        case TradingMacroTakeProfitFunction.PlumMM:
          tp = tradeLeveBy(TradeLevelBy.PlumMax, TradeLevelBy.PlumMin);
          break;
        case TradingMacroTakeProfitFunction.Blue:
          tp = tradeLeveBy(TradeLevelBy.PriceHigh, TradeLevelBy.PriceLow);
          break;
        case TradingMacroTakeProfitFunction.BlueMM:
          tp = tradeLeveBy(TradeLevelBy.BlueMax, TradeLevelBy.BlueMin);
          break;
        case TradingMacroTakeProfitFunction.Pips:
          tp = InPoints(xRatio);
          break;
        #region RatesHeight
        case TradingMacroTakeProfitFunction.RatesHeight:
          tp = useTrender(tm => tm.RatesHeightCma * xRatio);
          break;
        #endregion
        #region BuySellLevels
        case TradingMacroTakeProfitFunction.BuySellLevels:
          tp = (BuyLevel.Rate - SellLevel.Rate).Abs() * xRatio;
          break;
        #endregion
        #region TradeHeight
        case TradingMacroTakeProfitFunction.TradeHeight:
          var xRate = MonoidsCore.ToFunc((List<Rate>)null, 0, (rates, sign) => new { rates, sign });
          Func<Func<Rate, bool>, IEnumerable<List<Rate>>> useRates = p => UseRates(rates => rates.Where(p).ToList());

          Func<double, Func<Rate, bool>> ratesBelow = open => r => r.PriceAvg <= open;
          Func<double, Func<Rate, bool>> ratesAbove = open => r => r.PriceAvg >= open;
          Func<Trade, Func<Rate, bool>> ratesAfterTrade = trade => trade.IsBuy ? ratesBelow(trade.Open) : ratesAbove(trade.Open);

          Func<Func<Rate, DateTime, bool>, DateTime, Func<Rate, bool>> ratesByTime = (p, t) => r => p(r, t);
          Func<DateTime, Func<Rate, bool>> ratesBefore = time => ratesByTime((r, t) => r.StartDate <= t, time);
          Func<DateTime, Func<Rate, bool>> ratesAfter = time => ratesByTime((r, t) => r.StartDate >= t, time);
          //Func<Trade, Func<Rate, bool>> ratesBeforeTrade = trade => trade.IsBuy ? ratesBelow(trade.Open) : ratesAbove(trade.Open);

          Func<Trade, double> heightBuySell = (trade) => {
            var before = useRates(ratesBefore(trade.Time)).Select(rates => rates.Height()).ToArray();
            var after = useRates(ratesAfter(trade.Time))
              .SelectMany(rates => rates.Where(ratesAfterTrade(trade)))
              .Height();
            return before
              .Where(Lib.IsNotNaN)
              .Select(b => b - (after - b).Max(0))
              .DefaultIfEmpty(-after)
              .Where(Lib.IsNotNaN)
              .DefaultIfEmpty(tp)
              .Single();
          };
          tp = Trades.TakeLast(1)
            .Select(trade => heightBuySell(trade) * xRatio)
            .DefaultIfEmpty(tp)
            .Single();
          break;
        #endregion
        default:
          throw new NotImplementedException(new { function, source = "GetValueByTakeProfitFunction" } + "");
      }
      return TakeProfitManual.Max(tp + PriceSpreadAverage.GetValueOrDefault(0) * 2 + InPoints(this.CommissionInPips()) * 2);
    }

    ScanCorridorDelegate GetScanCorridorFunction(ScanCorridorFunction function) {
      switch(function) {
        case ScanCorridorFunction.OneTwoThree:
          return ScanCorridorBy123;
        case ScanCorridorFunction.OneToFour:
          return ScanCorridorBy1234;
        case ScanCorridorFunction.OneToFive:
          return ScanCorridorBy12345;
        case ScanCorridorFunction.AllFive:
          return ScanCorridorByAll5;
        case ScanCorridorFunction.Fft:
          return ScanCorridorByFft;
        case ScanCorridorFunction.StDevSplits:
          return ScanCorridorBySplitHeights;
        case ScanCorridorFunction.StDevSplits3:
          return ScanCorridorBySplitHeights3;
      }
      throw new NotSupportedException(function + "");
    }

    public double CommissionByTrade(Trade trade) { return TradesManager.CommissionByTrade(trade); }

    public bool IsInVirtualTrading { get { return TradesManager is VirtualTradesManager; } }
    private bool CanTrade() {
      return IsInVirtualTrading || !IsInPlayback;
    }

    ITargetBlock<PriceChangedEventArgs> _runPriceBroadcast;
    public ITargetBlock<PriceChangedEventArgs> RunPriceBroadcast {
      get {
        if(_runPriceBroadcast == null) {
          Action<PriceChangedEventArgs> a = u => RunPrice(u, Trades);
          _runPriceBroadcast = a.CreateYieldingTargetBlock();
        }
        return _runPriceBroadcast;
      }
    }
    void OnRunPriceBroadcast(PriceChangedEventArgs pce) {
      RunPrice(pce, Trades);
      //if (TradesManager.IsInTest || IsInPlayback) {
      //  CurrentPrice = pce.Price;
      //  RunPrice(pce, Trades);
      //} else RunPriceBroadcast.SendAsync(pce);
    }

    #region GeneralPurpose Subject
    object _GeneralPurposeSubjectLocker = new object();
    ISubject<Action> _GeneralPurposeSubject;
    ISubject<Action> GeneralPurposeSubject {
      get {
        lock(_GeneralPurposeSubjectLocker)
          if(_GeneralPurposeSubject == null) {
            _GeneralPurposeSubject = new Subject<Action>();
            _GeneralPurposeSubject.SubscribeToLatestOnBGThread(exc => Log = exc, ThreadPriority.Normal);
            //.Latest().ToObservable(new EventLoopScheduler(ts => { return new Thread(ts) { IsBackground = true }; }))
            //.Subscribe(s => s(), exc => Log = exc);
          }
        return _GeneralPurposeSubject;
      }
    }
    void OnGeneralPurpose(Action p, bool useAsync) {
      if(useAsync)
        GeneralPurposeSubject.OnNext(p);
      else
        p();
    }
    #endregion

    #region News Subject
    object _NewsSubjectLocker = new object();
    ISubject<Action> _NewsSubject;
    ISubject<Action> NewsSubject {
      get {
        lock(_NewsSubjectLocker)
          if(_NewsSubject == null) {
            _NewsSubject = new Subject<Action>();
            _NewsSubject.SubscribeWithoutOverlap(a => a(), Scheduler.Default);
          }
        return _NewsSubject;
      }
    }
    void OnNews(Action p) {
      NewsSubject.OnNext(p);
    }
    #endregion

    IObservable<TradingMacro> _SyncObservable;
    public IObservable<TradingMacro> SyncObservable {
      get { return (_SyncObservable = _SyncObservable.InitBufferedObservable(ref _SyncSubject, exc => Log = exc)); }
    }
    ISubject<TradingMacro> _SyncSubject;
    ISubject<TradingMacro> SyncSubject {
      get { return (_SyncSubject = _SyncSubject.InitBufferedObservable(ref _SyncObservable, exc => Log = exc)); }
    }
    #region ScanCorridor Subject
    object _ScanCoridorSubjectLocker = new object();
    ISubject<Action> _ScanCoridorSubject;
    ISubject<Action> ScanCoridorSubject {
      get {
        lock(_ScanCoridorSubjectLocker)
          if(_ScanCoridorSubject == null) {
            _ScanCoridorSubject = new Subject<Action>();
            _ScanCoridorSubject.SubscribeToLatestOnBGThread(a => a(), exc => Log = exc, ThreadPriority.Highest);
          }
        return _ScanCoridorSubject;
      }
    }
    void OnScanCorridor(Action p) {
      ScanCoridorSubject.OnNext(p);
    }
    void OnScanCorridor(List<Rate> rates, Action callback, bool runSync) {
      if(runSync)
        ScanCorridor(rates, callback);
      else
        OnScanCorridor(() => ScanCorridor(rates, callback));
    }
    #endregion


    ReactiveList<NewsEvent> _newEventsCurrent = new ReactiveList<NewsEvent>();
    public ReactiveUI.ReactiveList<NewsEvent> NewEventsCurrent { get { return _newEventsCurrent; } }
    Queue<Price> _priceQueue = new Queue<Price>();
    private void RunPrice(PriceChangedEventArgs e, Trade[] trades) {
      Price price = e.Price;
      while(_priceQueue.Count > PriceCmaLevels.Max(5))
        _priceQueue.Dequeue();
      _priceQueue.Enqueue(price);
      Account account = e.Account;
      if(account.IsMarginCall && IsPrimaryMacro) {
        IsTradingActive = false;
        SuppRes.ForEach(sr => sr.CanTrade = false);
        CloseTrades("Margin Call.");
        BroadcastCloseAllTrades();
      }
      var timeSpanDict = new Dictionary<string, long>();
      try {
        CalcTakeProfitDistance();
        if(!price.IsReal)
          price = TradesManager.GetPrice(Pair);
        MinimumGross = CurrentGross;
        MinimumOriginalProfit = TradingStatistics.OriginalProfit;
        CurrentLossPercent = CurrentGross / account.Balance;
        BalanceOnStop = account.Balance + StopAmount.GetValueOrDefault();
        BalanceOnLimit = account.Balance + LimitAmount.GetValueOrDefault();
        //SetTradesStatistics(trades);
        if(DoNews && RatesArray.Any())
          OnNews(() => {
            if(!RatesArray.Any())
              return;
            var dateStart = RatesArray[0].StartDate;
            var dateEnd = RatesArray.LastBC().StartDate.AddHours(120);
            var newsEventsCurrent = NewsCasterModel.SavedNews.AsParallel().Where(ne => ne.Time.DateTime.Between(dateStart, dateEnd)).ToArray();
            NewEventsCurrent.Except(newsEventsCurrent).ToList().ForEach(ne => NewEventsCurrent.Remove(ne));
            NewEventsCurrent.AddRange(newsEventsCurrent.Except(NewEventsCurrent).ToArray());
          });
        SetLotSize();
        Stopwatch swLocal = Stopwatch.StartNew();
        if(!IsInVirtualTrading && swLocal.Elapsed > TimeSpan.FromSeconds(5)) {
          Log = new Exception("RunPrice({0}) took {1:n1} sec.".Formater(Pair, swLocal.Elapsed.TotalSeconds));
        }
        if(RatesArraySafe.Count == 0)
          RatesArray.Clear();
        timeSpanDict.Add("RatesArraySafe", swLocal.ElapsedMilliseconds);
      } catch(Exception exc) { Log = exc; }
      //Debug.WriteLine("RunPrice[{1}]:{0} ms", sw.Elapsed.TotalMilliseconds, pair);
    }

    #region LotSize
    int _BaseUnitSize = 0;
    public int BaseUnitSize { get { return _BaseUnitSize > 0 ? _BaseUnitSize : _BaseUnitSize = TradesManager.GetBaseUnitSize(Pair); } }
    Account _account = null;
    Account Account { get { return _account ?? (_account = TradesManager.GetAccount()); } }
    public void SetLotSize(Account account = null) {
      if(TradesManager == null)
        return;
      account = account ?? Account;
      if(account == null)
        return;
      Trade[] trades = Trades;
      try {
        TradingRatioByPMC.Yield()
          .Where(pmc => pmc)
          .Select(_ => CalcLotSizeByPMC(account))
          .Concat(TradingRatio.Yield()
            .Where(tr => tr > 0)
            .Select(tr => tr >= 1
              ? (tr * BaseUnitSize).ToInt()
              : TradesManagerStatic.GetLotstoTrade(account.Balance, TradesManager.Leverage(Pair), tr, BaseUnitSize)))
          .Concat(0.Yield())
          .Take(1)
          .ForEach(ls => {
            LotSize = ls;
            LotSizePercent = LotSize / account.Balance / TradesManager.Leverage(Pair);
            LotSizeByLossBuy = AllowedLotSizeCore();
            LotSizeByLossSell = AllowedLotSizeCore();
          });
        var stopAmount = 0.0;
        var limitAmount = 0.0;
        foreach(var trade in trades.ByPair(Pair)) {
          stopAmount += trade.StopAmount;
          limitAmount += trade.LimitAmount;
        }
        StopAmount = stopAmount;
        LimitAmount = limitAmount;
        OnPropertyChanged("PipAmount");
      } catch(Exception exc) { throw new SetLotSizeException("", exc); }
    }

    public int MaxPipsToPMC() {
      return InPips(
        Enumerable.Range(0, 1)
        .Where(_ => BuyLevel != null && SellLevel != null)
        .Select(_ => GetValueByTakeProfitFunction(TradingMacroTakeProfitFunction.BuySellLevels, 1))
        .Concat(new[] { GetValueByTakeProfitFunction(TradingDistanceFunction, TradingDistanceX) })
        .Max(pmc => pmc))
        .ToInt();
    }

    public int CalcLotSizeByPMC(Account account) {
      var tms = TradingStatistics.TradingMacros;
      return tms == null || !tms.Any() ? 0
        : TradesManagerStatic.LotToMarginCall(MaxPipsToPMC()
        , account.Equity / tms.Count(tm => tm.TradingRatioByPMC)
        , BaseUnitSize
        , GetPipCost()
        , TradesManager.GetOffer(Pair).MMR);
    }

    private double GetPipCost() {
      return TradesManagerStatic.PipCost(Pair, TradesManager.RateForPipAmount(CurrentPrice), BaseUnitSize, PointSize);
    }

    int LotSizeByLoss(ITradesManager tradesManager, double loss, int baseLotSize, double lotMultiplierInPips) {
      var bus = tradesManager.GetBaseUnitSize(Pair);
      return TradesManagerStatic.GetLotSize(-(loss / lotMultiplierInPips) * bus / TradesManager.GetPipCost(Pair), bus, true);
    }
    int LotSizeByLoss(double? lotMultiplierInPips = null) {
      var currentGross = this.TradingStatistics.CurrentGross;
      var lotSize = LotSizeByLoss(TradesManager, currentGross, LotSize,
        lotMultiplierInPips ?? (TradingDistanceInPips * 2));
      return lotMultiplierInPips.HasValue || lotSize <= MaxLotSize || !false ? lotSize.Min(MaxLotSize) : LotSizeByLoss(TradesManager, currentGross, LotSize, RatesHeightInPips / _ratesHeightAdjustmentForAls).Max(MaxLotSize);
    }

    int StrategyLotSizeByLossAndDistance(ICollection<Trade> trades) {
      var lotSize = trades.Select(t => t.Lots).DefaultIfEmpty(LotSizeByLoss()).Min()
        .Max(LotSize).Min((MaxLotByTakeProfitRatio * LotSize).ToInt());
      var lastLotSize = trades.Select(t => t.Lots).DefaultIfEmpty(lotSize).Max();
      var pl = trades.Select(t => (t.Close - t.Open).Abs()).OrderBy(d => d).LastOrDefault();
      var multilier = (Math.Floor(pl / CorridorStats.HeightUpDown) + 1).Min(MaxLotByTakeProfitRatio).ToInt();
      return (lotSize * multilier).Min(lastLotSize + lotSize);
    }

    private int LotSizeByDistance(ICollection<Trade> trades) {
      var pl = trades.Select(t => (t.Close - t.Open).Abs()).OrderBy(d => d).LastOrDefault();
      var multilier = (Math.Floor(pl / RatesHeight) + 1).Min(MaxLotByTakeProfitRatio).ToInt();
      return LotSize.Max(LotSize * multilier);
    }


    public int AllowedLotSizeCore(double? lotMultiplierInPips = null) {
      if(!HasRates)
        return 0;
      return LotSizeByLoss(lotMultiplierInPips).Max(LotSize);//.Min(MaxLotByTakeProfitRatio.ToInt() * LotSize);
    }
    #endregion

    #region Commands


    ICommand _GannAnglesResetCommand;
    public ICommand GannAnglesResetCommand {
      get {
        if(_GannAnglesResetCommand == null) {
          _GannAnglesResetCommand = new Gala.RelayCommand(GannAnglesReset, () => true);
        }

        return _GannAnglesResetCommand;
      }
    }
    void GannAnglesReset() {
      GannAnglesList.Reset();
    }


    ICommand _GannAnglesUnSelectAllCommand;
    public ICommand GannAnglesUnSelectAllCommand {
      get {
        if(_GannAnglesUnSelectAllCommand == null) {
          _GannAnglesUnSelectAllCommand = new Gala.RelayCommand(GannAnglesUnSelectAll, () => true);
        }

        return _GannAnglesUnSelectAllCommand;
      }
    }
    void GannAnglesUnSelectAll() {
      GannAnglesList.Angles.ToList().ForEach(a => a.IsOn = false);
    }


    ICommand _GannAnglesSelectAllCommand;
    public ICommand GannAnglesSelectAllCommand {
      get {
        if(_GannAnglesSelectAllCommand == null) {
          _GannAnglesSelectAllCommand = new Gala.RelayCommand(GannAnglesSelectAll, () => true);
        }
        return _GannAnglesSelectAllCommand;
      }
    }
    void GannAnglesSelectAll() {
      GannAnglesList.Angles.ToList().ForEach(a => a.IsOn = true);
    }

    #endregion


    #region PriceBars
    protected class PriceBarsDuplex {
      public PriceBar[] Long { get; set; }
      public PriceBar[] Short { get; set; }
      public PriceBar[] GetPriceBars(bool isLong) { return isLong ? Long : Short; }
    }
    protected PriceBarsDuplex PriceBars = new PriceBarsDuplex();
    protected void SetPriceBars(bool isLong, PriceBar[] priceBars) {
      if(isLong)
        PriceBars.Long = priceBars;
      else
        PriceBars.Short = priceBars;
    }
    protected PriceBar[] FetchPriceBars(int rowOffset, bool reversePower) {
      return FetchPriceBars(rowOffset, reversePower, DateTime.MinValue);
    }
    protected PriceBar[] FetchPriceBars(int rowOffset, bool reversePower, DateTime dateStart) {
      var isLong = dateStart == DateTime.MinValue;
      var rs = RatesArraySafe.Where(r => r.StartDate >= dateStart).GroupTicksToRates();
      var ratesForDensity = (reversePower ? rs.OrderBarsDescending() : rs.OrderBars()).ToList();
      SetPriceBars(isLong, ratesForDensity.GetPriceBars(TradesManager.GetPipSize(Pair), rowOffset));
      return GetPriceBars(isLong);
    }
    public PriceBar[] GetPriceBars(bool isLong) {
      return PriceBars.GetPriceBars(isLong) ?? new PriceBar[0];
    }
    #endregion


    private Exception _Log;
    public Exception Log {
      get { return _Log; }
      set {
        if(_Log != value) {
          _Log = value;
          OnPropertyChanged("Log");
        }
      }
    }

    bool isLoggedIn { get { return TradesManager != null && TradesManager.IsLoggedIn; } }

    int _limitBarToRateProvider {
      get {
        return (int)BarPeriod;// Enum.GetValues(typeof(BarsPeriodTypeFXCM)).Cast<int>().Where(i => i <= (int)BarPeriod).Max();
      }
    }

    object _innerRateArrayLocker = new object();
    public U[] UseRates<T, U>(Func<List<Rate>, IEnumerable<T>> func, Func<IEnumerable<T>, IEnumerable<U>> many, int timeoutInMilliseconds = 100, [CallerMemberName] string Caller = "", [CallerFilePath] string LastFile = "", [CallerLineNumber]int LineNumber = 0) {
      return UseRates(func, timeoutInMilliseconds, Caller, LastFile, LineNumber).SelectMany(many).ToArray();
    }
    public T[] UseRates<T>(Func<List<Rate>, T> func, int timeoutInMilliseconds = 100, [CallerMemberName] string Caller = "", [CallerFilePath] string LastFile = "", [CallerLineNumber]int LineNumber = 0) {
      var sw = new Stopwatch();
      if(!Monitor.TryEnter(_innerRateArrayLocker, timeoutInMilliseconds)) {
        var message = new { Pair, PairIndex, Method = "UseRates", Caller, timeoutInMilliseconds } + "";
        Log = new TimeoutException(message);
        return new T[0];
      }
      try {
        sw.Start();
        return new[] { func(RatesArray) };
      } catch(Exception exc) {
        Log = exc;
        return new T[0];
      } finally {
        Monitor.Exit(_innerRateArrayLocker);
        sw.Stop();
        if(sw.ElapsedMilliseconds > timeoutInMilliseconds) {
          var message = new { Pair, PairIndex, Method = "UseRates", Caller, LastFile = Path.GetFileNameWithoutExtension(LastFile), LastLine = LineNumber, ms = sw.ElapsedMilliseconds, timeOut = timeoutInMilliseconds } + "";
          Log = new TimeoutException(message);
        }
      }
    }
    object _innerRateLocker = new object();
    string _UseRatesInternalSource = string.Empty;
    public T[] UseRatesInternal<T>(Func<ReactiveList<Rate>, T> func, int timeoutInMilliseconds = 3000, [CallerMemberName] string Caller = "") {
      if(!Monitor.TryEnter(_innerRateLocker, timeoutInMilliseconds)) {
        var message = new { Pair, PairIndex, Method = "UseRatesInternal", Caller, timeoutInMilliseconds } + "";
        Log = new TimeoutException(message);
        return new T[0];
      }
      try {
        return new[] { func(_Rates) };
      } catch(Exception exc) {
        Log = exc;
        return new T[0];
      } finally {
        Monitor.Exit(_innerRateLocker);
      }
    }
    public void UseRatesInternal(Action<ReactiveList<Rate>> action) {
      Func<ReactiveList<Rate>, Unit> f = rates => { action(rates); return Unit.Default; };
      UseRatesInternal<Unit>(f).Count();
    }
    static object _loadRatesLoader = new object();
    public void LoadRates(Action before = null) {
      if(!IsActive || !isLoggedIn || TradesManager == null || TradesManager.IsInTest || IsInVirtualTrading || IsInPlayback)
        return;
      var noRates = UseRatesInternal(ri => ri.Count < 0);
      if(noRates.IsEmpty())
        return;
      if(noRates.Any(b => b)) {
        var dbRates = GlobalStorage.GetRateFromDBBackwards<Rate>(Pair, ServerTime.ToUniversalTime(), BarsCountCount(), BarPeriodInt, BarPeriodCalc == BarsPeriodType.s1 ? GroupTicksToSeconds : (Func<List<Rate>, List<Rate>>)null);
        if(UseRatesInternal(ri => { ri.AddRange(dbRates); return true; }).IsEmpty())
          return;
      }
      lock(_loadRatesLoader)
        try {
          {
            InfoTooltip = "Loading Rates";
            //Debug.WriteLine("LoadRates[{0}:{2}] @ {1:HH:mm:ss}", Pair, ServerTime, (BarsPeriodType)BarPeriod);
            var sw = Stopwatch.StartNew();
            if(before != null)
              before();
            var serverTime = ServerTime;
            var periodsBack = BarsCountCount();
            var useDefaultInterval = /*!DoStreatchRates || dontStreachRates ||*/  CorridorStats == null || CorridorStats.StartDate == DateTime.MinValue;
            var startDate = TradesManagerStatic.FX_DATE_NOW;
            if(!useDefaultInterval) {
              var intervalToAdd = Math.Max(5, _Rates.Count / 10);
              if(CorridorStartDate.HasValue)
                startDate = CorridorStartDate.Value;
              else if(CorridorStats == null)
                startDate = TradesManagerStatic.FX_DATE_NOW;
              else {
                startDate = CorridorStats.StartDate;//.AddMinutes(-(int)BarPeriod * intervalToAdd);
                UseRatesInternal(ri => ri.Count(r => r.StartDate >= startDate) + intervalToAdd)
                  .ForEach(periodsByStartDate => periodsBack = periodsBack.Max(periodsByStartDate));
              }
            }
            if(BarPeriod != BarsPeriodType.t1)
              UseRatesInternal(rl => {
                if(rl.Count != rl.Distinct().Count()) {
                  var ri = rl.Distinct().ToList();
                  rl.Clear();
                  rl.AddRange(ri);
                  Log = new Exception("[{0}]:Distinct count check point. New count:{1}".Formater(Pair, rl.Count));
                }
              });
            Func<Rate, bool> isHistory = r => r.IsHistory;
            Func<Rate, bool> isNotHistory = r => !isHistory(r);
            var ratesList = RatesInternal.BackwardsIterator().SkipWhile(isNotHistory).Take(1).ToList();
            startDate = ratesList.Select(r => r.StartDate).DefaultIfEmpty(startDate).First();
            if(startDate != TradesManagerStatic.FX_DATE_NOW && _Rates.Count > 10)
              periodsBack = 0;
            var groupTicks = BarPeriodCalc == BarsPeriodType.s1;
            LoadRatesImpl(GetFXWraper(), Pair, _limitBarToRateProvider, periodsBack, startDate, TradesManagerStatic.FX_DATE_NOW, ratesList, groupTicks);
            if(BarPeriod != BarsPeriodType.t1)
              ratesList.TakeLast(1).ForEach(r => {
                var rateLastDate = r.StartDate;
                var delay = ServerTime.Subtract(rateLastDate).Duration();
                var delayMax = 1.0.Max(BarPeriodInt).FromMinutes();
                if(delay > delayMax) {
                  if(delay > (delayMax + delayMax))
                    Log = new Exception("[{2}]Last rate time:{0} is far from ServerTime:{1}".Formater(rateLastDate, ServerTime, Pair));
                  ratesList.RemoveAt(ratesList.Count - 1);
                  LoadRatesImpl(GetFXWraper(), Pair, _limitBarToRateProvider, periodsBack, rateLastDate, TradesManagerStatic.FX_DATE_NOW, ratesList, groupTicks);
                }
              });
            {
              UseRatesInternal(ri => ri.BackwardsIterator().TakeWhile(isNotHistory).ToList()).ForEach(ratesLocal => {
                if(ratesLocal == null)
                  return;
                ratesLocal.Reverse();
                var ratesLocalCount = ratesLocal.Count;// RatesInternal.Reverse().TakeWhile(isNotHistory).Count();
                if(ratesList.Count > 0) {
                  var volts = ratesLocal.SkipWhile(r => GetVoltage(r).IsNaN()).Select(r => Tuple.Create(r.StartDate, GetVoltage(r)));
                  ratesList.Zip(r => r.StartDate, volts, (r, t) => SetVoltage(r, t.Item2));
                  UseRatesInternal(rl => {
                    LoadRatesStartDate2 = ratesList[0].StartDate2;
                    var sd1 = ratesList.Last().StartDate;
                    rl.RemoveRange(rl.Count - ratesLocalCount, ratesLocalCount);
                    rl.RemoveAll(r => r.StartDate2 >= LoadRatesStartDate2);
                    rl.AddRange(ratesList);
                    var rateTail = ratesLocal.SkipWhile(r => r.StartDate <= sd1).ToArray();
                    rl.AddRange(rateTail);
                    return;
                  });
                } else
                  Log = new Exception("No rates were loaded from server for " + new { Pair, BarPeriod });
              });
            }
            //if (BarPeriod == BarsPeriodType.t1)
            //  UseRatesInternal(ri => { ri.Sort(LambdaComparisson.Factory<Rate>((r1, r2) => r1.StartDate > r2.StartDate)); });
            if(sw.Elapsed > TimeSpan.FromSeconds(LoadRatesSecondsWarning))
              Log = new Exception("LoadRates[" + Pair + ":{1}] - {0:n1} sec".Formater(sw.Elapsed.TotalSeconds, (BarsPeriodType)BarPeriod));
            LastRatePullTime = ServerTime;
            UseRatesInternal(rl => new[] { rl.Count - BarsCountCount() }.Where(rc => rc > 0).ForEach(rc => rl.RemoveRange(0, rc)));
            if(LoadHistoryRealTime) {
              _addHistoryOrdersBuffer.Push(()
                => {
                  TradingMacrosByPair().ForEach(tm =>
                  PriceHistory.AddTicks(TradesManager as Order2GoAddIn.FXCoreWrapper
                    , tm.BarPeriodInt, Pair, serverTime.AddMonths(-1), obj => { if(DoLogSaveRates) Log = new Exception(obj + ""); }));
                });
            }
            //Scheduler.Default.Schedule(a);
            //{
            //  RatesArraySafe.SavePairCsv(Pair);
            //}
            //if (!HasCorridor) ScanCorridor();
          }
        } catch(Exception exc) {
          Log = exc;
        } finally {
          InfoTooltip = "";
        }
    }
    AddHistoryOrdersBuffer _addHistoryOrdersBuffer = AddHistoryOrdersBuffer.Create();
    class AddHistoryOrdersBuffer : AsyncBuffer<AddHistoryOrdersBuffer, Action> {
      protected override Action PushImpl(Action action) {
        return action;
      }
    }

    public DateTimeOffset LoadRatesStartDate2 { get; set; }
    #region Overrides
    [MethodImpl(MethodImplOptions.Synchronized)]
    void LoadRatesImpl(Order2GoAddIn.FXCoreWrapper fw, string pair, int periodMinutes, int periodsBack, DateTime startDate, DateTime endDate, List<Rate> ratesList, bool groupToSeconds) {
      Func<List<Rate>, List<Rate>> map = groupToSeconds ? TradingMacro.GroupTicksToSeconds<Rate> : (Func<List<Rate>, List<Rate>>)null;
      var fetchRates = ratesList.Count() == 0;
      if(ratesList.Count() == -1) {
        if(periodMinutes > 0)
          ratesList.AddRange(fw.GetBarsFromHistory(pair, periodMinutes, TradesManagerStatic.FX_DATE_NOW, endDate).Except(ratesList));
        else
          ratesList.AddRange(fw.GetTicks(pair, periodsBack, null).Except(ratesList));
      }
      //if (periodMinutes == 0) {
      //  var d = ratesList.OrderBarsDescending().TakeWhile(t => t.StartDate.Millisecond == 0)
      //    .Select(r => r.StartDate).DefaultIfEmpty(TradesManagerStatic.FX_DATE_NOW).Min();
      //  ratesList.RemoveAll(r => r.StartDate >= d);
      //}
      fw.GetBars(pair, periodMinutes, periodsBack, startDate, endDate, ratesList, z => {
        if(groupToSeconds)
          Log = new Exception(new { GetDars = new { z.Message } } + "");
      }, true, map);
    }

    class OnPropertyChangedDispatcher : BlockingConsumerBase<Tuple<TradingMacro, string>> {
      public OnPropertyChangedDispatcher() : base(t => t.Item1.OnPropertyChangedCore(t.Item2)) { }
      public void Add(TradingMacro tm, string propertyName) {
        Add(new Tuple<TradingMacro, string>(tm, propertyName), (t1, t2) => t1.Item1 == t2.Item1 && t1.Item2 == t2.Item2);
      }
    }
    static OnPropertyChangedDispatcher OnPropertyChangedQueue = new OnPropertyChangedDispatcher();

    protected ConcurrentDictionary<Expression<Func<object>>, string> _propertyExpressionDictionary = new ConcurrentDictionary<Expression<Func<object>>, string>();

    protected void OnPropertyChanged(Expression<Func<object>> property) {
      //var propertyString = _propertyExpressionDictionary.GetOrAdd(property, pe => Lib.GetLambda(property));
      OnPropertyChanged(Lib.GetLambda(property));
      //OnPropertyChanged(Lib.GetLambda(property));
    }
    protected override void OnPropertyChanged(string property) {
      base.OnPropertyChanged(property);
      OnPropertyChangedCore(property);
      //OnPropertyChangedQueue.Add(this, property);
    }

    int _broadcastCounter;
    BroadcastBlock<Action<int>> _broadcastCorridorDatesChange;
    BroadcastBlock<Action<int>> broadcastCorridorDatesChange {
      get {
        if(_broadcastCorridorDatesChange == null) {
          _broadcastCorridorDatesChange = DataFlowProcessors.SubscribeToBroadcastBlock(() => _broadcastCounter);
        }
        return _broadcastCorridorDatesChange;
      }
    }
    class LoadRateAsyncBuffer : AsyncBuffer<LoadRateAsyncBuffer, Action> {
      public LoadRateAsyncBuffer() : base() {

      }
      protected override Action PushImpl(Action context) {
        return context;
      }
    }
    LoadRateAsyncBuffer _loadRatesAsyncBuffer = new LoadRateAsyncBuffer();
    BroadcastBlock<Action<Unit>> _broadcastLoadRates;
    BroadcastBlock<Action<Unit>> broadcastLoadRates {
      get {
        if(_broadcastLoadRates == null) {
          _broadcastLoadRates = DataFlowProcessors.SubscribeToBroadcastBlock();
        }
        return _broadcastLoadRates;
      }
    }
    public void OnPropertyChangedCore(string property) {
      if(EntityState == System.Data.Entity.EntityState.Detached)
        return;
      //_propertyChangedTaskDispencer.RunOrEnqueue(property, () => {
      switch(property) {
        case TradingMacroMetadata.IsTradingActive:
          SuppRes.ToList().ForEach(sr => sr.ResetPricePosition());
          break;
        case TradingMacroMetadata.TradingDistanceFunction:
        case TradingMacroMetadata.CurrentLoss:
          _tradingDistanceMax = 0;
          SetLotSize();
          break;
        case TradingMacroMetadata.Pair:
          _pointSize = _pipCost = double.NaN;
          goto case TradingMacroMetadata.BarsCount;
        case TradingMacroMetadata.UsePrevHeight:
          ResetBarsCountCalc();
          goto case TradingMacroMetadata.BarsCount;
        case TradingMacroMetadata.VoltsFrameLength:
        case TradingMacroMetadata.CorridorDistanceRatio:
          CorridorStats = null;
          CorridorStartDate = null;
          goto case TradingMacroMetadata.TakeProfitFunction;
        case TradingMacroMetadata.BarsCount:
          if(!IsInVirtualTrading) {
            OnLoadRates(() => UseRatesInternal(ri => ri.Clear()));
          } else {
            var func = new[] {
              SetVoltage, SetVoltage2,
              (r, v) => r.VoltageLocal = v, (r, v) => r.VoltageLocal0 = new double[0], (r, v) => r.VoltageLocal2 = v, (r, v) => r.VoltageLocal3 = v,
              (r, v) => r.Distance = v };
            UseRatesInternal(ri => ri.ForEach(r => { func.ForEach(f => { f(r, double.NaN); }); }));
            new[] { _setVoltsSubscriber }.Where(a => a != null).ForEach(a => a.Dispose());
          }
          break;
        case TradingMacroMetadata.RatesInternal:
          RatesArraySafe.Count();
          break;
        case TradingMacroMetadata.Strategy:
        case TradingMacroMetadata.TrailingDistanceFunction:
          _strategyExecuteOnTradeClose = null;
          _strategyExecuteOnTradeOpen = null;
          CloseAtZero = false;
          _tradingDistanceMax = 0;
          goto case TradingMacroMetadata.TakeProfitFunction;
        case TradingMacroMetadata.TakeProfitFunction:
          if(RatesArray.Count > 0)
            OnScanCorridor(RatesArray, () => {
              RaiseShowChart();
              RunStrategy();
            }, true);
          break;
        case TradingMacroMetadata.CorridorCalcMethod:
        case TradingMacroMetadata.CorridorCrossHighLowMethod:
        case TradingMacroMetadata.CorridorCrossesCountMinimum:
        case TradingMacroMetadata.CorridorHighLowMethod:
        case TradingMacroMetadata.TradingAngleRange:
        case TradingMacroMetadata.StDevAverageLeewayRatio:
        case TradingMacroMetadata.StDevTresholdIterations:
        case TradingMacroMetadata.MovingAverageType:
        case TradingMacroMetadata.PriceCmaLevels:
          try {
            if(RatesArray.Any()) {
              RatesArray.Clear();
              RatesArraySafe.Count();
            }
          } catch(Exception exc) {
            Log = exc;
          }
          break;
        case TradingMacroMetadata.SuppResLevelsCount_:
          AdjustSuppResCount();
          break;
      }
      //}, exc => Log = exc);
    }

    double PriceSpreadAverageInPips { get { return InPips(PriceSpreadAverage); } }
    double? _priceSpreadAverage;
    public double? PriceSpreadAverage {
      get { return _priceSpreadAverage; }
      set {
        var spread = RoundPrice(value.Value, 1);
        if(_priceSpreadAverage == spread)
          return;
        _priceSpreadAverage = spread;
        OnPropertyChanged(() => PriceSpreadAverage);
        SetPriceSpreadOk();
      }
    }
    Strategies[] _exceptionStrategies = new[] { Strategies.Auto };
    partial void OnCorridorBarMinutesChanging(int value) {
      if(value == CorridorBarMinutes)
        return;
      if(!IsInVirtualTrading) {
        if(!_exceptionStrategies.Any(s => Strategy.HasFlag(s)))
          Strategy = Strategies.None;
        OnLoadRates();
      }
    }
    #endregion

    private Rate _rateGannCurrentLast;
    public Rate RateGannCurrentLast {
      get { return _rateGannCurrentLast; }
      set { _rateGannCurrentLast = value; }
    }
    private string _InfoTooltip;
    public string InfoTooltip {
      get { return _InfoTooltip; }
      set {
        _InfoTooltip = value;
        OnPropertyChanged(TradingMacroMetadata.InfoTooltip);
      }
    }

    private double _TakeProfitDistance;
    public double TakeProfitDistance {
      get { return _TakeProfitDistance; }
      set {
        if(_TakeProfitDistance != value) {
          _TakeProfitDistance = value;
          OnPropertyChanged(TradingMacroMetadata.TakeProfitDistance);
          OnPropertyChanged(TradingMacroMetadata.TakeProfitDistanceInPips);
        }
      }
    }
    public double TakeProfitDistanceInPips { get { return InPips(TakeProfitDistance); } }

    public double RatesStDevToRatesHeightRatio { get { return RatesHeight / RatesStDev; } }

    double _RatesHeight;
    public double RatesHeight {
      get { return _RatesHeight; }
      set {
        if(_RatesHeight == value)
          return;
        _RatesHeight = value;
        OnPropertyChanged(() => RatesHeightInPips);
      }
    }
    public double RatesHeightInPips { get { return InPips(RatesHeight); } }

    double _RatesStDevHourlyAvg;
    public double RatesStDevHourlyAvg {
      get { return _RatesStDevHourlyAvg; }
      set {
        if(_RatesStDevHourlyAvg == value)
          return;
        _RatesStDevHourlyAvg = value;
        OnPropertyChanged(() => RatesStDevHourlyAvgInPips);
      }
    }
    public double RatesStDevHourlyAvgInPips { get { return InPips(RatesStDevHourlyAvg); } }

    double _RatesStDevHourlyAvgNative;
    public double RatesStDevHourlyAvgNative {
      get { return _RatesStDevHourlyAvgNative; }
      set {
        if(_RatesStDevHourlyAvgNative == value)
          return;
        _RatesStDevHourlyAvgNative = value;
        OnPropertyChanged(() => RatesStDevHourlyAvgNativeInPips);
      }
    }
    public double RatesStDevHourlyAvgNativeInPips { get { return InPips(RatesStDevHourlyAvgNative); } }

    ConcurrentDictionary<string, Func<TradeDirections>> _canOpenTradeAutoConditions = new ConcurrentDictionary<string, Func<TradeDirections>>();
    private bool CanOpenTradeAuto(bool isBuy) {
      return isBuy && _canOpenTradeAutoConditions.Values.DefaultIfEmpty(() => TradeDirections.Down).All(v => v() == TradeDirections.Up) ||
        !isBuy && _canOpenTradeAutoConditions.Values.DefaultIfEmpty(() => TradeDirections.Up).All(v => v() == TradeDirections.Down);
    }
    public bool CanOpenTradeByDirection(bool isBuy) {
      return TradeDirection.IsAuto()
        ? CanOpenTradeAuto(isBuy)
        : isBuy
        ? TradeDirection.HasUp()
        : !isBuy
        ? TradeDirection.HasDown()
        : false;
    }

    public DateTime TouchDownDateTime { get; set; }

    private IList<WaveInfo> _waveRates = new List<WaveInfo>();
    private List<Tuple<int, double>> _levelCounts;
    public IList<WaveInfo> WaveRates {
      get { return _waveRates; }
      set { _waveRates = value; }
    }
    private double CorridorAngleFromTangent() {
      return CorridorStats.Coeffs == null ? double.NaN : -AngleFromTangent(CorridorStats.Coeffs.LineSlope(), () => CalcTicksPerSecond(CorridorStats.Rates));
    }

    private double AngleFromTangent(double tangent, Func<double> ticksPerSecond) {
      var barPeriod = BarPeriod != BarsPeriodType.t1
        ? BarPeriodInt
        : 1.0 / 60 * ticksPerSecond();
      return tangent.Angle(BarPeriodInt.Max(1), PointSize);
    }


    public double VolumeAverageLow { get; set; }

    private List<List<Rate>> _CorridorsRates = new List<List<Rate>>();
    bool IsReverseStrategy { get { return BuyLevel.Rate < SellLevel.Rate; } }

    public bool HasBuyLevel { get { return Resistances.Length > 0; } }
    public Store.SuppRes BuyLevel {
      get {
        if(!HasBuyLevel)
          throw new Exception("There are no Resistance levels.");
        return Resistance0().First();
      }
    }
    private Store.SuppRes[] BuyCloseSupResLevel() {
      var buyCloseLevel = Support1();
      buyCloseLevel.Where(sr => !sr.IsExitOnly).ForEach(sr => sr.IsExitOnly = true);
      return buyCloseLevel;
    }
    bool HasBuyCloseLevel { get { return BuyCloseSupResLevel().Any(); } }
    public Store.SuppRes BuyCloseLevel {
      get { return BuyCloseSupResLevel().First(); }
    }

    public bool HasSellLevel { get { return Supports.Length > 0; } }
    public Store.SuppRes SellLevel {
      get {
        if(!HasSellLevel)
          throw new Exception("There are no Support levels.");
        return Support0().First();
      }
    }

    bool HasSellCloseLevel { get { return SellCloseSupResLevel().Length > 0; } }
    public Store.SuppRes SellCloseLevel {
      get { return SellCloseSupResLevel()[0]; }
    }
    private IList<IList<Rate>> _waves;

    public IList<IList<Rate>> Waves {
      get { return _waves; }
      set { _waves = value; }
    }
    private IList<Rate> _WaveHigh;
    public IList<Rate> WaveHigh {
      get { return _WaveHigh; }
      set {
        if(_WaveHigh != value) {
          _WaveHigh = value;
          //_waveHighOnOff = new ValueWithOnOff<IList<Rate>>(value);
        }
      }
    }

    ValueWithOnOff<IList<Rate>> _waveHighOnOff;
    private List<IList<Rate>> _wavesBig;
    public List<IList<Rate>> WavesBig {
      get { return _wavesBig; }
      set { _wavesBig = value; }
    }

    private double _waveAverage;
    public double WaveAverage {
      get { return _waveAverage; }
      set {
        if(_waveAverage == value)
          return;
        _waveAverage = value;
        OnPropertyChanged("WaveAverage");
        OnPropertyChanged("WaveAverageInPips");
      }
    }

    public double WaveAverageInPips { get { return InPips(WaveAverage); } }

    class ValueWithOnOff<T> : Models.ModelBase {
      public T Value { get; set; }
      public bool IsOn { get; set; }
      public ValueWithOnOff(T value) {
        this.Value = value;
        this.TurnOn();
      }
      public void TurnOn() { IsOn = true; }
      public void TurnOff() { IsOn = false; }
    }
    private bool _isWaveOk;

    interface IWave<TBar> where TBar : BarBase {
      IList<TBar> Rates { get; set; }
      double RatesMax { get; }
      double RatesMin { get; }
    }
    public class WaveInfo : Models.ModelBase, IWave<Rate> {
      #region Distance
      public Rate DistanceRate { get; set; }
      double _Distance = double.NaN;

      public double Distance {
        get { return _Distance; }
        set {
          if(_Distance == value)
            return;
          _Distance = value;
          RaiseDistanceChanged();
        }
      }
      public bool HasDistance { get { return !double.IsNaN(Distance); } }
      public double ClearDistance() { return Distance = double.NaN; }
      public Rate SetRateByDistance(IList<Rate> rates) {
        if(!this.HasDistance)
          return null;
        return DistanceRate = rates.ReverseIfNot().SkipWhile(r => r.Distance < this.Distance).FirstOrDefault();
      }
      public void SetRatesByDistance(IList<Rate> rates, int countMinimum = 30) {
        if(!this.HasDistance)
          return;
        this.Rates = RatesByDistance(rates, Distance).ToArray();
        if(this.Rates.Count < countMinimum)
          this.Rates = rates.Take(countMinimum).ToArray();
      }
      public static Rate RateByDistance(IList<Rate> rates, double distance) {
        return rates.ReverseIfNot().SkipWhile(r => r.Distance < distance).First();
      }
      public static IEnumerable<Rate> RatesByDistance(IList<Rate> rates, double distance) {
        return rates.ReverseIfNot().TakeWhile(r => r.Distance <= distance);
      }
      #endregion


      public double RatesHeight { get { return _Rates == null ? double.NaN : RatesMax - RatesMin; } }
      public double RatesHeightInPips { get { return _tradingMacro.InPips(RatesHeight); } }

      #region RatesMax,RatesMinRatesStDev
      double _RatesMax = double.NaN;
      public double RatesMax {
        get {
          if(double.IsNaN(_RatesMax) && HasRates)
            _RatesMax = Rates.Max(_tradingMacro.CorridorPrice());
          return _RatesMax;
        }
        set {
          _RatesMax = value;
        }
      }
      double _RatesMin = double.NaN;
      public double RatesMin {
        get {
          if(double.IsNaN(_RatesMin) && HasRates)
            _RatesMin = Rates.Min(_tradingMacro.CorridorPrice());
          return _RatesMin;
        }
        set {
          _RatesMin = value;
        }
      }
      public double RatesStDevPrev { get; set; }
      private double _RatesStDev = double.NaN;
      public double RatesStDev {
        get {
          if(double.IsNaN(_RatesStDev) && HasRates && _Rates.Count > 1) {
            var corridor = Rates.ScanCorridorWithAngle(_tradingMacro.CorridorGetHighPrice(), _tradingMacro.CorridorGetLowPrice(), TimeSpan.Zero, _tradingMacro.PointSize, _tradingMacro.CorridorCalcMethod);
            //var stDevs = Rates.Shrink(_tradingMacro.CorridorPrice, 5).ToArray().ScanWaveWithAngle(v => v, _tradingMacro.PointSize, _tradingMacro.CorridorCalcMethod);
            _RatesStDev = corridor.StDev;
          }
          return _RatesStDev;
        }
        set {
          if(double.IsNaN(value))
            RatesStDevPrev = _RatesStDev;
          _RatesStDev = value;
        }
      }
      public double RatesStDevInPips { get { return _tradingMacro.InPips(RatesStDev); } }
      #endregion
      public WaveInfo(TradingMacro tradingMacro) {
        this._tradingMacro = tradingMacro;
      }
      public WaveInfo(TradingMacro tradingMacro, IList<Rate> rates)
        : this(tradingMacro) {
        this.Rates = rates;
      }

      public bool HasRates { get { return _Rates != null && _Rates.Any(); } }
      IList<Rate> _Rates;
      private TradingMacro _tradingMacro;
      public IList<Rate> ResetRates(IList<Rate> rates) {
        Rates = null;
        return Rates = rates;
      }
      public IList<Rate> Rates {
        get {
          if(_Rates == null || !_Rates.Any())
            throw new NullReferenceException();
          return _Rates;
        }
        set {
          var isUp = value == null || value.Count == 0 ? null : (bool?)(value.LastBC().PriceAvg < value[0].PriceAvg);
          if(value == null || !HasRates || isUp != IsUp || Rates.LastBC() == value.LastBC())
            _Rates = value;
          else {
            var newRates = value.TakeWhile(r => r != Rates[0]);
            _Rates = newRates.Concat(_Rates).ToArray();
          }
          RatesMax = double.NaN;
          RatesMin = double.NaN;
          RatesStDev = double.NaN;
          if(_Rates != null && _Rates.Any()) {
            StartDate = _Rates.LastBC().StartDate;
            IsUp = isUp;
          }
          RaisePropertyChanged("Rates");
          RaisePropertyChanged("RatesStDev");
          RaisePropertyChanged("RatesStDevInPips");
          RaisePropertyChanged("RatesHeightInPips");
        }
      }
      public void Reset() {
        ClearDistance();
        ClearEvents();
        Rates = null;
      }

      #region Events
      public void ClearEvents() {
        if(DistanceChangedEvent != null)
          foreach(var handler in DistanceChangedEvent.GetInvocationList().Cast<EventHandler<EventArgs>>())
            DistanceChangedEvent -= handler;
        if(StartDateChangedEvent != null)
          foreach(var handler in StartDateChangedEvent.GetInvocationList().Cast<EventHandler<NewOldEventArgs<DateTime>>>())
            StartDateChangedEvent -= handler;
        if(IsUpChangedEvent != null)
          foreach(var handler in IsUpChangedEvent.GetInvocationList().Cast<EventHandler<NewOldEventArgs<bool?>>>())
            IsUpChangedEvent -= handler;
      }
      #region DistanceChanged Event
      event EventHandler<EventArgs> DistanceChangedEvent;
      public event EventHandler<EventArgs> DistanceChanged {
        add {
          if(DistanceChangedEvent == null || !DistanceChangedEvent.GetInvocationList().Contains(value))
            DistanceChangedEvent += value;
        }
        remove {
          DistanceChangedEvent -= value;
        }
      }
      protected void RaiseDistanceChanged() {
        if(DistanceChangedEvent != null)
          DistanceChangedEvent(this, new EventArgs());
      }
      #endregion

      #region StartDateChanged Event
      event EventHandler<NewOldEventArgs<DateTime>> StartDateChangedEvent;
      public event EventHandler<NewOldEventArgs<DateTime>> StartDateChanged {
        add {
          if(StartDateChangedEvent == null || !StartDateChangedEvent.GetInvocationList().Contains(value))
            StartDateChangedEvent += value;
        }
        remove {
          StartDateChangedEvent -= value;
        }
      }
      protected void RaiseStartDateChanged(DateTime now, DateTime then) {
        if(StartDateChangedEvent != null)
          StartDateChangedEvent(this, new NewOldEventArgs<DateTime>(now, then));
      }
      #endregion

      #region IsUpChanged Event
      event EventHandler<NewOldEventArgs<bool?>> IsUpChangedEvent;
      public event EventHandler<NewOldEventArgs<bool?>> IsUpChanged {
        add {
          if(IsUpChangedEvent == null || !IsUpChangedEvent.GetInvocationList().Contains(value))
            IsUpChangedEvent += value;
        }
        remove {
          IsUpChangedEvent -= value;
        }
      }
      protected void RaiseIsUpChanged(bool? now, bool? then) {
        if(IsUpChangedEvent != null)
          IsUpChangedEvent(this, new NewOldEventArgs<bool?>(now, then));
      }
      #endregion

      #endregion

      public Rate Rate { get; set; }
      /// <summary>
      /// 1 - based
      /// </summary>
      public int Position { get; set; }
      public double Slope { get; set; }
      public double Direction { get { return Math.Sign(Slope); } }
      public WaveInfo(Rate rate, int position, double slope) {
        this.Rate = rate;
        this.Position = position;
        this.Slope = slope;
      }



      #region StartDate
      private DateTime _StartDate;
      public DateTime StartDate {
        get { return _StartDate; }
        set {
          if(_StartDate != value) {
            var then = _StartDate;
            _StartDate = value;
            RaisePropertyChanged("StartDate");
            RaiseStartDateChanged(_StartDate, then);
          }
        }
      }

      #endregion

      #region IsUp
      private Lazy<bool?> _getUp = new Lazy<bool?>();
      private bool? _IsUp;
      public bool? IsUp {
        get { return IsUpChangedEvent == null ? _getUp.Value : _IsUp; }
        set {
          if(_IsUp != value) {
            if(!HasRates || _Rates.Count < 2) {
              _IsUp = null;
              return;
            }
            _getUp = new Lazy<bool?>(() => HasRates ? _Rates.Select(r => r.PriceAvg).ToArray().LinearSlope() < 0 : (bool?)null, true);
            if(IsUpChangedEvent == null)
              return;
            else {
              throw new NotImplementedException("IsUp property of WaviInfo class must be tested with IsUpChangedEvent != null.");
              var isUp = _getUp.Value;
              if(_IsUp == isUp)
                return;
              _IsUp = isUp;
              RatesStDevPrev = _RatesStDev;
              RaisePropertyChanged("IsUp");
              RaiseIsUpChanged(_IsUp, !_IsUp);
            }
          }
        }
      }

      #endregion
    }

    private WaveInfo _waveTradeStart;
    public WaveInfo WaveTradeStart {
      get { return _waveTradeStart ?? (_waveTradeStart = new WaveInfo(this)); }
      set { _waveTradeStart = value; }
    }
    private WaveInfo _waveTradeStart1;
    public WaveInfo WaveTradeStart1 {
      get { return _waveTradeStart1 ?? (_waveTradeStart1 = new WaveInfo(this)); }
      set { _waveTradeStart1 = value; }
    }
    public List<List<Rate>> CorridorsRates {
      get { return _CorridorsRates; }
      set { _CorridorsRates = value; }
    }

    public Tuple<Rate, Rate, int> _waveLong { get; set; }

    private double _RatesMax = double.NaN;
    public double RatesMax {
      get { return _RatesMax; }
      set { _RatesMax = value; }
    }

    private double _RatesMin = double.NaN;
    public double RatesMin {
      get { return _RatesMin; }
      set { _RatesMin = value; }
    }
    private bool _isStrategyRunning;

    #region IsTradingActive
    private bool _IsTradingActive = false;

    private static TaskScheduler _currentDispatcher;

    #region FireOnNotIsTradingActive Subject
    object _FireOnNotIsTradingActiveSubjectLocker = new object();
    ISubject<Action> _FireOnNotIsTradingActiveSubject;
    ISubject<Action> FireOnNotIsTradingActiveSubject {
      get {
        lock(_FireOnNotIsTradingActiveSubjectLocker)
          if(_FireOnNotIsTradingActiveSubject == null) {
            _FireOnNotIsTradingActiveSubject = new Subject<Action>();
            _FireOnNotIsTradingActiveSubject
              .Where(_ => !IsTradingActive)
              .Delay(15.FromSeconds())
              .Where(_ => !IsTradingActive)
              .Subscribe(s => s(), exc => { });
          }
        return _FireOnNotIsTradingActiveSubject;
      }
    }
    void OnFireOnNotIsTradingActive(Action p) {
      FireOnNotIsTradingActiveSubject.OnNext(p);
    }
    #endregion
    #region MustStopTrading
    private bool _MustStopTrading;
    public bool MustStopTrading {
      get { return _MustStopTrading; }
      set {
        if(_MustStopTrading != value) {
          _MustStopTrading = value;
          OnPropertyChanged("MustStopTrading");
        }
      }
    }

    #endregion
    public bool IsTradingActive {
      get { return _IsTradingActive; }
      set {
        if(_IsTradingActive != value) {
          _IsTradingActive = value;
          SuppRes.ForEach(sr => sr.ResetPricePosition());
          OnPropertyChanged(() => IsTradingActive);
          if(value)
            MustStopTrading = false;
          else
            OnFireOnNotIsTradingActive(() => MustStopTrading = true);
        }
      }
    }

    #endregion

    #region WaveLength
    private int _WaveLength;
    public int WaveLength {
      get {
        if(_WaveLength == 0)
          throw new ArgumentOutOfRangeException("WaveLength");
        return _WaveLength;
      }
      set {
        if(_WaveLength != value) {
          _WaveLength = value;
          OnPropertyChanged("WaveLength");
        }
      }
    }
    #endregion
    public double WaveDistanceInPips { get { return InPips(WaveDistance); } }

    public void MakeGhosts() {
      var srExit = SuppRes.Where(sr => sr.IsExitOnly).ToList();
      if(!srExit.Any())
        throw new Exception("No ExitOnly levels was found.");
      var offset = BuyLevel.Rate.Abs(SellLevel.Rate) / 5;
      srExit.Single(sr => sr.IsSell).Rate = BuyLevel.Rate - offset;
      srExit.Single(sr => sr.IsBuy).Rate = SellLevel.Rate + offset;
      srExit.ForEach(sr => sr.IsGhost = true);
      SuppRes.ToList().ForEach(sr => sr.InManual = true);
    }

    IEnumerable<SuppRes> _suppResesForBulk() { return SuppRes.Where(sr => !sr.IsExitOnly || sr.InManual); }
    #region RatesRsd
    public double RatesRsd { get { return RatesHeight / StDevByPriceAvg; } }
    #endregion

    #region StDevByPriceAvg
    private double _StDevByPriceAvg = double.NaN;
    public double StDevByPriceAvg {
      get { return _StDevByPriceAvg; }
      set {
        if(_StDevByPriceAvg != value) {
          _StDevByPriceAvg = value;
          OnPropertyChanged("StDevByPriceAvg");
          OnPropertyChanged("StDevByPriceAvgInPips");
        }
      }
    }
    public double StDevByPriceAvgInPips { get { return InPips(StDevByPriceAvg); } }
    #endregion

    #region StDevByHeight
    private double _StDevByHeight = double.NaN;
    public double StDevByHeight {
      get { return _StDevByHeight; }
      set {
        if(_StDevByHeight != value) {
          _StDevByHeight = value;
          OnPropertyChanged("StDevByHeight");
          OnPropertyChanged("StDevByHeightInPips");
        }
      }
    }
    public double StDevByHeightInPips { get { return InPips(StDevByHeight); } }
    #endregion

    public double CorridorStDevSqrt { get { return Math.Pow(CorridorStats.StDevByPriceAvg * CorridorStats.StDevByHeight, .52); } }
    public double CorridorStDevSqrtInPips { get { return InPips(CorridorStDevSqrt); } }
    public double RatesStDevInPips { get { return InPips(RatesStDev); } }
    private double _RatesStDev;
    public double RatesStDev {
      get { return _RatesStDev; }
      set {
        if(_RatesStDev == value)
          return;
        _RatesStDev = value;
        OnPropertyChanged("RatesStDev");
        OnPropertyChanged("RatesStDevInPips");
      }
    }

    double _WaveDistanceForTrade = double.NaN;

    public double WaveDistanceForTrade {
      get {
        if(double.IsNaN(_WaveDistanceForTrade))
          throw new ArgumentOutOfRangeException("WaveDistanceForTrade");
        return _WaveDistanceForTrade;
      }
      set {
        if(_WaveDistanceForTrade == value)
          return;
        _WaveDistanceForTrade = value;
      }
    }
    double _WaveDistance;
    /// <summary>
    /// Distance for WaveShort
    /// </summary>
    public double WaveDistance {
      get { return _WaveDistance; }
      set {
        if(_WaveDistance == value)
          return;
        _WaveDistance = value;
        OnPropertyChanged(() => WaveDistance);
        OnPropertyChanged(() => WaveDistanceInPips);
        OnPropertyChanged(() => WaveShortDistanceInPips);
      }
    }
    public double WaveShortDistance { get { return WaveShort.Distance.IfNaN(WaveDistance); } }
    public double WaveShortDistanceInPips { get { return InPips(WaveShortDistance); } }

    double _RatesStDevAdjusted = double.NaN;
    private DB.v_BlackoutTime[] _blackoutTimes;

    private IList<DB.s_GetBarStats_Result> _TimeFrameStats;
    private double _ratesHeightAdjustmentForAls = 2;

    public IList<DB.s_GetBarStats_Result> TimeFrameStats {
      get { return _TimeFrameStats; }
      set {
        if(_TimeFrameStats == value)
          return;
        _TimeFrameStats = value;
        _MonthStats = new MonthStatistics(value);
      }
    }

    public double RatesStDevAdjusted {
      get { return _RatesStDevAdjusted; }
      set {
        if(_RatesStDevAdjusted == value)
          return;
        _RatesStDevAdjusted = value;
        OnPropertyChanged("RatesStDevAdjusted");
        OnPropertyChanged("RatesStDevAdjustedInPips");
      }
    }
    public double RatesStDevAdjustedInPips { get { return InPips(RatesStDevAdjusted); } }

    List<LambdaBinding> _tradingStateLambdaBindings = new List<LambdaBinding>();
    bool __tradingStateLambdaBinding;
    public bool TradingState {
      get {
        if(!__tradingStateLambdaBinding && HasBuyLevel && HasSellLevel) {
          __tradingStateLambdaBinding = true;
          try {
            _tradingStateLambdaBindings.AddRange(new[]{
              LambdaBinding.BindOneWay(() => BuyLevel.CanTradeEx, () => TradingState, (s) => false),
              LambdaBinding.BindOneWay(() => SellLevel.CanTradeEx, () => TradingState, (s) => false),
              LambdaBinding.BindOneWay(() => Strategy, () => TradingState, (s) => false),
              LambdaBinding.BindOneWay(() => IsTradingActive, () => TradingState, (s) => false)
            });
          } catch(Exception exc) {
            Log = exc;
          }
        }
        return Strategy != Strategies.None && IsTradingActive && (BuyLevel.CanTrade || SellLevel.CanTrade);
      }
      set {
        OnPropertyChanged(() => TradingState);
      }
    }

    List<LambdaBinding> _BuySellHeightLambdaBindings = new List<LambdaBinding>();
    bool __BuySellHeightLambdaBinding;
    public double BuySellHeight {
      get {
        if(!__BuySellHeightLambdaBinding && HasBuyLevel && HasSellLevel) {
          __BuySellHeightLambdaBinding = true;
          try {
            _BuySellHeightLambdaBindings.AddRange(new[]{
              LambdaBinding.BindOneWay(() => BuyLevel.Rate, () => BuySellHeight, (s) => double.NaN),
              LambdaBinding.BindOneWay(() => SellLevel.Rate, () => BuySellHeight, (s) => double.NaN)
            });
          } catch(Exception exc) {
            Log = exc;
          }
        }
        return !HasBuyLevel || !HasSellLevel ? 0 : BuyLevel.Rate.Abs(SellLevel.Rate);
      }
      set {
        OnPropertyChanged(() => BuySellHeight);
        OnPropertyChanged(() => BuySellHeightInPips);
      }
    }
    public double BuySellHeightInPips { get { return InPips(BuySellHeight); } }

    #region RatesStDevMin
    int _RatesStDevMinInPips = 10;
    [Category(categoryActive)]
    [DisplayName("Rates StDev Min")]
    [Description("RatesStDevMinInPips for BarsCountValc")]
    public int RatesStDevMinInPips {
      get { return _RatesStDevMinInPips; }
      set {
        if(_RatesStDevMinInPips != value) {
          _RatesStDevMinInPips = value;
          OnPropertyChanged("RatesStDevMinInPips");
          FreezeCorridorStartDate(true);
          ResetBarsCountLastDate();
        }
      }
    }
    #endregion

    #region RatesHeightMin
    private double _RatesHeightMin;
    private double[] _lineMA;
    public double[] LineMA {
      get { return _lineMA; }
      set { _lineMA = value; }
    }
    [Category(categoryActive)]
    [WwwSetting(wwwSettingsCorridorCMA)]
    public double RatesHeightMin {
      get { return _RatesHeightMin; }
      set {
        if(_RatesHeightMin != value) {
          _RatesHeightMin = value;
          OnPropertyChanged(nameof(RatesHeightMin));
        }
      }
    }
    #endregion

    #region RatesDistanceMin
    [Category(categoryCorridor)]
    private double _RatesDistanceMin = 1000;
    private double _ratesHeightCma;
    private double _ratesHeightCmaMax;
    private double _ratesHeightCmaMin;

    [Category(categoryActive)]
    [WwwSetting(wwwSettingsCorridorCMA)]
    public double RatesDistanceMin {
      get { return _RatesDistanceMin; }
      set {
        if(_RatesDistanceMin != value) {
          _RatesDistanceMin = value;
          OnPropertyChanged("RatesDistanceMin");
          ResetBarsCountLastDate();
        }
      }
    }

    private void ResetBarsCountLastDate() {
      BarsCountLastDate = DateTime.MinValue;
    }

    #endregion
    /// <summary>
    ///  In minutes
    /// </summary>
    int _ratesDuration;
    public int RatesDuration {
      get {
        return _ratesDuration;
      }
      set {
        _ratesDuration = value;
        RatesPipsPerMInute = InPips(RatesArray.Distances(_priceAvg).Last().Item2) / RatesDuration;
        OnPropertyChanged(() => RatesDuration);
      }
    }

    double RatesPipsPerMInute { get; set; }

    bool _isAsleep;
    private double[] _ratesArrayCoeffs = new double[0];

    public bool IsAsleep {
      get { return _isAsleep; }

      set {
        _isAsleep = value;
        if(value) {
          RatesLengthLatch = ScanCorridorLatch = true;
          BuySellLevels.ForEach(bs => bs.ResetPricePosition());
        }
      }
    }
    public bool RatesLengthLatch { get; set; }
    public bool ScanCorridorLatch { get; private set; }

    public double DistanceByMASD {
      get {
        return _distanceByMASD;
      }

      set {
        _distanceByMASD = value;
      }
    }

    public double RatesHeightCma {
      get {
        return _ratesHeightCma;
      }

      set {
        _ratesHeightCma = value;
      }
    }
  }
  public static class WaveInfoExtentions {
    public static Dictionary<CorridorCalculationMethod, double> ScanWaveWithAngle<T>(this IList<T> rates, Func<T, double> price, double pointSize, CorridorCalculationMethod corridorMethod) {
      return rates.ScanWaveWithAngle(price, price, price, pointSize, corridorMethod);
    }
    public static Dictionary<CorridorCalculationMethod, double> ScanWaveWithAngle<T>(this IList<T> rates, Func<T, double> price, Func<T, double> priceHigh, Func<T, double> priceLow, double pointSize, CorridorCalculationMethod corridorMethod) {
      try {
        #region Funcs
        double[] linePrices = new double[rates.Count()];
        Func<int, double> priceLine = index => linePrices[index];
        Action<int, double> lineSet = (index, d) => linePrices[index] = d;
        var coeffs = rates.SetRegressionPrice(price, lineSet);
        var sineOffset = Math.Sin(Math.PI / 2 - coeffs[1] / pointSize);
        Func<T, int, double> heightHigh = (rate, index) => (priceHigh(rate) - priceLine(index)) * sineOffset;
        Func<T, int, double> heightLow = (rate, index) => (priceLine(index) - priceLow(rate)) * sineOffset;
        #endregion
        #region Locals
        var lineLow = new LineInfo(new Rate[0], 0, 0);
        var lineHigh = new LineInfo(new Rate[0], 0, 0);
        #endregion

        var stDevDict = new Dictionary<CorridorCalculationMethod, double>();
        if(corridorMethod == CorridorCalculationMethod.Minimum || corridorMethod == CorridorCalculationMethod.Maximum) {
          stDevDict.Add(CorridorCalculationMethod.HeightUD, rates.Select(heightHigh).Union(rates.Select(heightLow)).ToList().StDevP());
          stDevDict.Add(CorridorCalculationMethod.Height, rates.Select((r, i) => heightHigh(r, i).Abs() + heightLow(r, i).Abs()).ToList().StDevP());
          if(corridorMethod == CorridorCalculationMethod.Minimum)
            stDevDict.Add(CorridorCalculationMethod.Price, rates.GetPriceForStats(price, priceLine, priceHigh, priceLow).ToList().StDevP());
          else
            stDevDict.Add(CorridorCalculationMethod.PriceAverage, rates.StDev(price));
        } else
          switch(corridorMethod) {
            case CorridorCalculationMethod.Minimum:
              stDevDict.Add(CorridorCalculationMethod.Minimum, stDevDict.Values.Min());
              break;
            case CorridorCalculationMethod.Maximum:
              stDevDict.Add(CorridorCalculationMethod.Maximum, stDevDict.Values.Max());
              break;
            case CorridorCalculationMethod.Height:
              stDevDict.Add(CorridorCalculationMethod.Height, rates.Select((r, i) => heightHigh(r, i).Abs() + heightLow(r, i).Abs()).ToList().StDevP());
              break;
            case CorridorCalculationMethod.HeightUD:
              stDevDict.Add(CorridorCalculationMethod.HeightUD, rates.Select(heightHigh).Union(rates.Select(heightLow)).ToList().StDevP());
              break;
            case CorridorCalculationMethod.Price:
              stDevDict.Add(CorridorCalculationMethod.Price, rates.GetPriceForStats(price, priceLine, priceHigh, priceLow).ToList().StDevP());
              break;
            default:
              throw new NotSupportedException(new { corridorMethod } + "");
          }
        stDevDict.Add(CorridorCalculationMethod.PriceAverage, rates.StDev(price));
        return stDevDict;
      } catch(Exception exc) {
        Debug.WriteLine(exc);
        throw;
      }
    }

  }
  public class ResetOnPairAttribute : Attribute {
  }
}
