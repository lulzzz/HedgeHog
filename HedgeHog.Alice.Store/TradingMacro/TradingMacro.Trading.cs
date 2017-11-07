﻿using HedgeHog.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using TM_HEDGE = System.Nullable<(HedgeHog.Alice.Store.TradingMacro tm, string Pair, double HV, double HVP, double TradeRatio, double TradeAmount, double MMR, int Lot, double Pip, bool IsBuy, bool IsPrime, double HVPR, double HVPM1R)>;

namespace HedgeHog.Alice.Store {
  partial class TradingMacro {
    public IEnumerable<TM_HEDGE> HedgeBuySell(bool isBuy) {
      var equity = TradesManager.GetAccount().Equity * TradingRatio;
      var hbs = (from tmh in GetHedgedTradingMacros(Pair)
                 from corr in tmh.tm.TMCorrelation(tmh.tmh)
                 let t = new[] { (tmh.tm, isBuy), (tmh.tmh, corr > 0 ? !isBuy : isBuy) }
                 from x in Hedging.CalcTradeAmount(t, equity)
                 let lot = x.tm.GetLotsToTrade(x.tradeAmount, 1, 1)
                 select (
                 tm: x.tm,
                 Pair: x.tm.Pair,
                 HV: x.hv,
                 HVP: x.hvp,
                 TradeRatio: x.tradingRatio * 100,
                 TradeAmount: x.tradeAmount,
                 MMR: x.mmr,
                 Lot: lot,
                 Pip: x.tm.PipAmountByLot(lot),
                 IsBuy: x.buy,
                 IsPrime: x.tm.Pair.ToLower() == Pair.ToLower(),
                 HVPR: (x.hvpr * 100).AutoRound2(3),
                 HVPM1R: (x.hvpM1r * 100).AutoRound2(3)
                 ));
      return hbs.Select(t => new TM_HEDGE(t));
    }
    public void OpenHedgedTrades(bool isBuy, string reason) {
      HedgeBuySell(isBuy)
        .Select(x => x.Value)
        .OrderByDescending(tm => tm.Pair == Pair)
        .ToArray()
        .ForEach(t => {
          var lot = t.tm.Trades.IsBuy(!t.IsBuy).Sum(tr => tr.Lots);
          t.tm.OpenTrade(t.IsBuy, t.Lot + lot, reason + ": hedge open");
        });
    }

    public void OpenTrade(bool isBuy, int lot, string reason) {
      var key = lot - Trades.Lots(t => t.IsBuy != isBuy) > 0 ? OT : CT;
      CheckPendingAction(key, (pa) => {
        if(lot > 0) {
          pa();
          LogTradingAction(string.Format("{0}[{1}]: {2} {3} from {4} by [{5}]", Pair, BarPeriod, isBuy ? "Buying" : "Selling", lot, new StackFrame(3).GetMethod().Name, reason));
          TradesManager.OpenTrade(Pair, isBuy, lot, 0, 0, "", CurrentPrice);
        }
      });
    }

    public void CloseTrades(string reason) { CloseTrades(Trades.Lots(), reason); }
    private void CloseTrades(int lot, string reason) {
      if(!IsTrader || !Trades.Any() || HasPendingKey(CT))
        return;
      if(lot > 0)
        CheckPendingAction(CT, pa => {
          pa();
          LogTradingAction(string.Format("{0}[{1}]: Closing {2} from {3} in {4} from {5}]"
            , Pair, BarPeriod, lot, Trades.Lots(), new StackFrame(3).GetMethod().Name, reason));
          if(!TradesManager.ClosePair(Pair, Trades[0].IsBuy, lot))
            ReleasePendingAction(CT);
        });
    }
  }
}