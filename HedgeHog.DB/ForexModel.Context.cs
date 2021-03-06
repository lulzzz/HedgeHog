﻿//------------------------------------------------------------------------------
// <auto-generated>
//     This code was generated from a template.
//
//     Manual changes to this file may cause unexpected behavior in your application.
//     Manual changes to this file will be overwritten if the code is regenerated.
// </auto-generated>
//------------------------------------------------------------------------------

namespace HedgeHog.DB
{
    using System;
    using System.Data.Entity;
    using System.Data.Entity.Infrastructure;
    using System.Data.Entity.Core.Objects;
    using System.Linq;
    
    public partial class ForexEntities : DbContext
    {
        public ForexEntities()
            : base("name=ForexEntities")
        {
        }
    
        protected override void OnModelCreating(DbModelBuilder modelBuilder)
        {
            throw new UnintentionalCodeFirstException();
        }
    
        public virtual DbSet<v_Pair> v_Pair { get; set; }
        public virtual DbSet<t_Offer> t_Offer { get; set; }
        public virtual DbSet<t_Stat> t_Stat { get; set; }
        public virtual DbSet<t_BarHeight> t_BarHeight { get; set; }
        public virtual DbSet<t_Report> t_Report { get; set; }
        public virtual DbSet<v_BlackoutTime> v_BlackoutTime { get; set; }
        public virtual DbSet<t_Session> t_Session { get; set; }
        public virtual DbSet<MonthlyStat__Stats> MonthlyStat__Stats { get; set; }
        public virtual DbSet<Event__News> Event__News { get; set; }
        public virtual DbSet<EventLevel__News> EventLevel__News { get; set; }
        public virtual DbSet<t_BarExtender> t_BarExtender { get; set; }
        public virtual DbSet<t_Bar> t_Bar { get; set; }
        public virtual DbSet<t_TradeValue> t_TradeValue { get; set; }
        public virtual DbSet<SP500> SP500 { get; set; }
        public virtual DbSet<v_TradeSession> v_TradeSession { get; set; }
        public virtual DbSet<t_Trade> t_Trade { get; set; }
    
        public virtual ObjectResult<Nullable<float>> GetCorridor(string pair, Nullable<byte> period, Nullable<System.DateTime> date, Nullable<int> spreadPeriod)
        {
            var pairParameter = pair != null ?
                new ObjectParameter("Pair", pair) :
                new ObjectParameter("Pair", typeof(string));
    
            var periodParameter = period.HasValue ?
                new ObjectParameter("Period", period) :
                new ObjectParameter("Period", typeof(byte));
    
            var dateParameter = date.HasValue ?
                new ObjectParameter("Date", date) :
                new ObjectParameter("Date", typeof(System.DateTime));
    
            var spreadPeriodParameter = spreadPeriod.HasValue ?
                new ObjectParameter("SpreadPeriod", spreadPeriod) :
                new ObjectParameter("SpreadPeriod", typeof(int));
    
            return ((IObjectContextAdapter)this).ObjectContext.ExecuteFunction<Nullable<float>>("GetCorridor", pairParameter, periodParameter, dateParameter, spreadPeriodParameter);
        }
    
        public virtual ObjectResult<GetCorridorAverage_Result> GetCorridorAverage(string pair, Nullable<byte> period, Nullable<System.DateTime> corridorDate, Nullable<int> corridorPeriods, Nullable<int> barMinutes)
        {
            var pairParameter = pair != null ?
                new ObjectParameter("Pair", pair) :
                new ObjectParameter("Pair", typeof(string));
    
            var periodParameter = period.HasValue ?
                new ObjectParameter("Period", period) :
                new ObjectParameter("Period", typeof(byte));
    
            var corridorDateParameter = corridorDate.HasValue ?
                new ObjectParameter("CorridorDate", corridorDate) :
                new ObjectParameter("CorridorDate", typeof(System.DateTime));
    
            var corridorPeriodsParameter = corridorPeriods.HasValue ?
                new ObjectParameter("CorridorPeriods", corridorPeriods) :
                new ObjectParameter("CorridorPeriods", typeof(int));
    
            var barMinutesParameter = barMinutes.HasValue ?
                new ObjectParameter("BarMinutes", barMinutes) :
                new ObjectParameter("BarMinutes", typeof(int));
    
            return ((IObjectContextAdapter)this).ObjectContext.ExecuteFunction<GetCorridorAverage_Result>("GetCorridorAverage", pairParameter, periodParameter, corridorDateParameter, corridorPeriodsParameter, barMinutesParameter);
        }
    
        public virtual ObjectResult<BarsByMinutes_Result> BarsByMinutes(string pair, Nullable<byte> period, Nullable<System.DateTime> dateEnd, Nullable<int> barMinutes, Nullable<int> barsCount)
        {
            var pairParameter = pair != null ?
                new ObjectParameter("Pair", pair) :
                new ObjectParameter("Pair", typeof(string));
    
            var periodParameter = period.HasValue ?
                new ObjectParameter("Period", period) :
                new ObjectParameter("Period", typeof(byte));
    
            var dateEndParameter = dateEnd.HasValue ?
                new ObjectParameter("DateEnd", dateEnd) :
                new ObjectParameter("DateEnd", typeof(System.DateTime));
    
            var barMinutesParameter = barMinutes.HasValue ?
                new ObjectParameter("BarMinutes", barMinutes) :
                new ObjectParameter("BarMinutes", typeof(int));
    
            var barsCountParameter = barsCount.HasValue ?
                new ObjectParameter("BarsCount", barsCount) :
                new ObjectParameter("BarsCount", typeof(int));
    
            return ((IObjectContextAdapter)this).ObjectContext.ExecuteFunction<BarsByMinutes_Result>("BarsByMinutes", pairParameter, periodParameter, dateEndParameter, barMinutesParameter, barsCountParameter);
        }
    
        public virtual ObjectResult<s_GetBarStats_Result> s_GetBarStats(string pair, Nullable<int> period, Nullable<int> length, Nullable<System.DateTime> startDate)
        {
            var pairParameter = pair != null ?
                new ObjectParameter("Pair", pair) :
                new ObjectParameter("Pair", typeof(string));
    
            var periodParameter = period.HasValue ?
                new ObjectParameter("Period", period) :
                new ObjectParameter("Period", typeof(int));
    
            var lengthParameter = length.HasValue ?
                new ObjectParameter("Length", length) :
                new ObjectParameter("Length", typeof(int));
    
            var startDateParameter = startDate.HasValue ?
                new ObjectParameter("StartDate", startDate) :
                new ObjectParameter("StartDate", typeof(System.DateTime));
    
            return ((IObjectContextAdapter)this).ObjectContext.ExecuteFunction<s_GetBarStats_Result>("s_GetBarStats", pairParameter, periodParameter, lengthParameter, startDateParameter);
        }
    
        public virtual ObjectResult<sGetStats_Result> sGetStats(Nullable<System.DateTime> dateStart, Nullable<int> frameInPeriods, Nullable<int> weeks)
        {
            var dateStartParameter = dateStart.HasValue ?
                new ObjectParameter("DateStart", dateStart) :
                new ObjectParameter("DateStart", typeof(System.DateTime));
    
            var frameInPeriodsParameter = frameInPeriods.HasValue ?
                new ObjectParameter("FrameInPeriods", frameInPeriods) :
                new ObjectParameter("FrameInPeriods", typeof(int));
    
            var weeksParameter = weeks.HasValue ?
                new ObjectParameter("Weeks", weeks) :
                new ObjectParameter("Weeks", typeof(int));
    
            return ((IObjectContextAdapter)this).ObjectContext.ExecuteFunction<sGetStats_Result>("sGetStats", dateStartParameter, frameInPeriodsParameter, weeksParameter);
        }
    }
}
