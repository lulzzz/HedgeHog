﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HedgeHog;
using HedgeHog.Bars;
using HedgeHog.Shared;
using static HedgeHog.Shared.TradesManagerStatic;
namespace IBApp {
  public class IBWraper : HedgeHog.Shared.ITradesManager {
    private readonly IBClientCore _ibClient;

    public IBWraper(ICoreFX coreFx, Func<Trade, double> commissionByTrade) {
      CommissionByTrade = commissionByTrade;

      CoreFX = coreFx;
      _ibClient = (IBClientCore)CoreFX;
      _ibClient.PriceChanged += OnPriceChanged;
      ;
      _ibClient.CommissionByTrade = commissionByTrade;
    }

    private void OnPriceChanged(object sender, PriceChangedEventArgs e) {
      var price = e.Price;
      RaisePriceChanged(price.Pair, price);
    }

    #region ITradesManager - Implemented

    #region Methods
    public int GetBaseUnitSize(string pair) => TradesManagerStatic.IsCurrenncy(pair) ? 1000 : 1;
    public double Leverage(string pair) => 1;
    public Trade TradeFactory(string pair) => Trade.Create(this, pair, GetPipSize(pair), GetBaseUnitSize(pair), CommissionByTrade);

    public double InPips(string pair, double? price) => price.GetValueOrDefault() / GetPipSize(pair);
    public double RateForPipAmount(Price price) { return price.Ask.Avg(price.Bid); }
    public double RateForPipAmount(double ask, double bid) { return ask.Avg(bid); }
    TBar ToRate<TBar>(DateTime date, double open, double high, double low, double close, int volume, int count) where TBar : Rate, new() {
      return Rate.Create<TBar>(date, high, low, true);
    }
    public void GetBarsBase<TBar>(string pair
      , int period
      , int periodsBack
      , DateTime startDate
      , DateTime endDate
      , List<TBar> ticks
      , Func<List<TBar>, List<TBar>> map
      , Action<RateLoadingCallbackArgs<TBar>> callBack = null
      ) where TBar : Rate, new() {

      var isFx = pair.Contains("/");
      var contract = isFx ? ContractSamples.FxContract(pair) : ContractSamples.Commodity(pair);
      var isDone = false;
      Func<DateTime, DateTime> fxDate = d => d == FX_DATE_NOW ? new DateTime(DateTime.Now.Ticks, DateTimeKind.Local) : d;
      endDate = fxDate(endDate);
      startDate = fxDate(startDate);
      new HistoryLoader<TBar>(_ibClient, contract, periodsBack, endDate.Max(startDate), (endDate - startDate).Duration(), period == 0 ? TimeUnit.S : TimeUnit.M, period == 0 ? BarSize._1_secs : BarSize._1_min,
        ToRate<TBar>,
         list => { ticks.AddRange(list); ticks.Sort(); isDone = true; },
         list => callBack(new RateLoadingCallbackArgs<TBar>(new { HistoryLoader = new { StartDate = list.FirstOrDefault()?.StartDate, EndDate = list.LastOrDefault()?.StartDate } } + "", list)),
         exc => {
           isDone = !(exc is SoftException);
           RaiseError(exc);
         });
      while(!isDone)
        Thread.Sleep(300);
      return;
    }
    public void GetBars(string pair, int periodMinutes, int periodsBack, DateTime startDate, DateTime endDate, List<Rate> ratesList, bool doTrim, Func<List<Rate>, List<Rate>> map) {
      throw new NotImplementedException();
    }

    public void GetBars(string pair, int Period, int periodsBack, DateTime StartDate, DateTime EndDate, List<Rate> Bars, Action<RateLoadingCallbackArgs<Rate>> callBack, bool doTrim, Func<List<Rate>, List<Rate>> map) {
      GetBarsBase(pair, Period, periodsBack, StartDate, EndDate, Bars, map, callBack);
    }
    public Account GetAccount() {
      try {
        return _ibClient.AccountManager?.Account;
      } catch(Exception exc) {
        RaiseError(exc);
        return null;
      }
    }
    public Trade[] GetTrades() => _ibClient.AccountManager?.GetTrades() ?? new Trade[0];
    public Trade[] GetTrades(string pair) => GetTrades().Where(t => t.Pair.WrapPair() == pair.WrapPair()).ToArray();
    public Trade[] GetTradesInternal(string Pair) => GetTrades(Pair);

    #endregion

    #region Error Event
    event EventHandler<ErrorEventArgs> ErrorEvent;
    public event EventHandler<ErrorEventArgs>  Error {
      add {
        if (ErrorEvent == null || !ErrorEvent.GetInvocationList().Contains(value))
          ErrorEvent += value;
      }
      remove {
        ErrorEvent -= value;
      }
    }
    protected void RaiseError(Exception exc) {
      if(ErrorEvent != null)
        ErrorEvent(this, new ErrorEventArgs(exc));
    }
    #endregion

    #region PriceChangedEvent

    event EventHandler<PriceChangedEventArgs> PriceChangedEvent;
    public event EventHandler<PriceChangedEventArgs> PriceChanged {
      add {
        if (PriceChangedEvent == null || !PriceChangedEvent.GetInvocationList().Contains(value))
          PriceChangedEvent += value;
      }
      remove {
        PriceChangedEvent -= value;
      }
    }
    void RaisePriceChanged(string pair, Price price, Account account, Trade[] trades) {
      var e = new PriceChangedEventArgs(pair, price, account, trades);
      PriceChangedEvent?.Invoke(this, e);
    }

    public void RaisePriceChanged(string pair, Price price) {
      RaisePriceChanged(pair, price, GetAccount(), GetTrades());
    }
    #endregion


    #region Properties
    public bool HasTicks => false;
    public bool IsLoggedIn => _ibClient.IsLoggedIn;
    public DateTime ServerTime {
      get {
        return DateTime.Now + _ibClient._serverTimeOffset;
      }
    }
    #endregion
    #endregion

    #region ITradesManager
    public Func<Trade, double> CommissionByTrade { get; private set; }

    public ICoreFX CoreFX { get; set; }

    public bool IsHedged {
      get {
        throw new NotImplementedException();
      }
    }

    public bool IsInTest { get; set; }

    public double PipsToMarginCall {
      get {
        throw new NotImplementedException();
      }
    }

    public event EventHandler<OrderEventArgs> OrderAdded;
    public event EventHandler<OrderEventArgs> OrderChanged;
    public event OrderRemovedEventHandler OrderRemoved;
    public event EventHandler<RequestEventArgs> RequestFailed;
    public event EventHandler<TradeEventArgs> TradeAdded;
    public event EventHandler<TradeEventArgs> TradeClosed;
    public event TradeRemovedEventHandler TradeRemoved;

    public void ChangeEntryOrderLot(string orderId, int lot) {
      throw new NotImplementedException();
    }

    public void ChangeEntryOrderPeggedLimit(string orderId, double rate) {
      throw new NotImplementedException();
    }

    public void ChangeEntryOrderPeggedStop(string orderId, double rate) {
      throw new NotImplementedException();
    }

    public void ChangeEntryOrderRate(string orderId, double rate) {
      throw new NotImplementedException();
    }

    public void ChangeOrderAmount(string orderId, int lot) {
      throw new NotImplementedException();
    }

    public void ChangeOrderRate(Order order, double rate) {
      throw new NotImplementedException();
    }

    public void CloseAllTrades() {
      throw new NotImplementedException();
    }

    public bool ClosePair(string pair) {
      throw new NotImplementedException();
    }

    public bool ClosePair(string pair, bool isBuy) {
      throw new NotImplementedException();
    }

    public bool ClosePair(string pair, bool isBuy, int lot) {
      throw new NotImplementedException();
    }

    public void CloseTrade(Trade trade) {
      throw new NotImplementedException();
    }

    public bool CloseTrade(Trade trade, int lot, Price price) {
      throw new NotImplementedException();
    }

    public void CloseTradeAsync(Trade trade) {
      throw new NotImplementedException();
    }

    public void CloseTradesAsync(Trade[] trades) {
      throw new NotImplementedException();
    }

    public string CreateEntryOrder(string pair, bool isBuy, int amount, double rate, double stop, double limit) {
      throw new NotImplementedException();
    }

    public void DeleteEntryOrderLimit(string orderId) {
      throw new NotImplementedException();
    }

    public void DeleteEntryOrderStop(string orderId) {
      throw new NotImplementedException();
    }

    public bool DeleteOrder(string orderId) {
      throw new NotImplementedException();
    }

    public void DeleteOrders(string pair) {
      RaiseError(new NotImplementedException(nameof(DeleteOrders)));
    }

    public PendingOrder FixCreateLimit(string tradeId, double limit, string remark) {
      throw new NotImplementedException();
    }

    public object FixOrderClose(string tradeId) {
      throw new NotImplementedException();
    }

    public object FixOrderClose(string tradeId, int mode, Price price, int lot) {
      throw new NotImplementedException();
    }

    public object[] FixOrdersClose(params string[] tradeIds) {
      throw new NotImplementedException();
    }

    public void FixOrderSetLimit(string tradeId, double takeProfit, string remark) {
      throw new NotImplementedException();
    }

    public void FixOrderSetStop(string tradeId, double stopLoss, string remark) {
      throw new NotImplementedException();
    }

    public IList<Rate> GetBarsFromHistory(string pair, int periodMinutes, DateTime dateTime, DateTime endDate) {
      throw new NotImplementedException();
    }


    void RaiseNotImplemented(string NotImplementedException) {
      //RaiseError(new NotImplementedException(new { NotImplementedException } + ""));
    }
    public Trade[] GetClosedTrades(string pair) {
      RaiseNotImplemented(nameof(GetClosedTrades));
      return new Trade[0];
    }

    public int GetDigits(string pair) => TradesManagerStatic.GetDigits(pair);

    public Trade GetLastTrade(string pair) {
      RaiseNotImplemented(nameof(GetLastTrade));
      return null;
    }

    public Order GetNetLimitOrder(Trade trade, bool getFromInternal = false) {
      RaiseNotImplemented(nameof(GetNetLimitOrder));
      return null;
    }

    public Order[] GetOrders(string pair) {
      RaiseNotImplemented(nameof(GetOrders));
      return new Order[0];
    }

    public double GetNetOrderRate(string pair, bool isStop, bool getFromInternal = false) {
      throw new NotImplementedException();
    }

    public Offer GetOffer(string pair) {
      throw new NotImplementedException();
    }

    public Offer[] GetOffers() {
      throw new NotImplementedException();
    }


    //public double GetPipCost(string pair) {
    //  throw new NotImplementedException();
    //}

    public double GetPipSize(string pair) => TradesManagerStatic.GetPointSize(pair);

    public Price GetPrice(string pair) {
      return _ibClient.GetPrice(pair);
    }

    public Tick[] GetTicks(string pair, int periodsBack, Func<List<Tick>, List<Tick>> map) {
      throw new NotImplementedException();
    }

    public double InPoints(string pair, double? price) { return InPoins(this, pair, price); }

    public PendingOrder OpenTrade(string Pair, bool isBuy, int lot, double takeProfit, double stopLoss, double rate, string comment) {
      throw new NotImplementedException();
    }

    public PendingOrder OpenTrade(string pair, bool buy, int lots, double takeProfit, double stopLoss, string remark, Price price) {
      throw new NotImplementedException();
    }

    public void RefreshOrders() {
      throw new NotImplementedException();
    }

    public void ResetClosedTrades(string pair) {
      throw new NotImplementedException();
    }

    public double Round(string pair, double value, int digitOffset = 0) { return Math.Round(value, GetDigits(pair) + digitOffset); }

    public void SetServerTime(DateTime serverTime) {
      throw new NotImplementedException();
    }
    #endregion
  }
}