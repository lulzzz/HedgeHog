﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HedgeHog {
  public static class MathCore {
    public static int Max(this int d1, int d2) {
      return Math.Max(d1, d2);
    }
    public static DateTime Max(this DateTime d1, DateTime d2) {
      return d1 >= d2 ? d1 : d2;
    }
    public static DateTime Min(this DateTime d1, DateTime d2) {
      return d1 <= d2 ? d1 : d2;
    }
    public static bool IsMin(this DateTime d) {
      return d == DateTime.MinValue;
    }
    public static bool IsMax(this DateTime d) {
      return d == DateTime.MaxValue;
    }
    public static DateTimeOffset IfMin(this DateTimeOffset d, DateTimeOffset d1) {
      return d == DateTimeOffset.MinValue ? d1 : d;
    }
    public static DateTime IfMin(this DateTime d, DateTime d1) {
      return d == DateTime.MinValue ? d1 : d;
    }
    public static DateTime IfMax(this DateTime d, DateTime d1) {
      return d == DateTime.MaxValue ? d1 : d;
    }

    #region TimeSpan
    public static TimeSpan Max(this IEnumerable<TimeSpan> spans) {
      var spanMin = TimeSpan.MinValue;
      foreach(var span in spans)
        if(spanMin < span)
          spanMin = span;
      return spanMin;
    }
    public static TimeSpan Average(this IEnumerable<TimeSpan> span) {
      return TimeSpan.FromMilliseconds(span.Average(s => s.TotalMilliseconds));
    }
    public static TimeSpan Multiply(this TimeSpan span, TimeSpan d) {
      return TimeSpan.FromMilliseconds(span.TotalMilliseconds * d.TotalMilliseconds);
    }
    public static TimeSpan Multiply(this TimeSpan span, double d) {
      return TimeSpan.FromMilliseconds(span.TotalMilliseconds * d);
    }
    public static TimeSpan Divide(this TimeSpan span, TimeSpan d) {
      return TimeSpan.FromMilliseconds(span.TotalMilliseconds / d.TotalMilliseconds);
    }
    public static double Ratio(this TimeSpan span, TimeSpan d) {
      return span.TotalMilliseconds / d.TotalMilliseconds;
    }
    public static TimeSpan Max(this TimeSpan span, TimeSpan d) {
      return span >= d ? span : d;
    }
    public static TimeSpan Min(this TimeSpan span, TimeSpan d) {
      return span <= d ? span : d;
    }
    #endregion

    public static double Div(this int v, int other) {
      return (v / (double)other);
    }
    public static double Div(this int v, double other) {
      return v / other;
    }
    public static int Sub(this int v, int other) {
      return v - other;
    }
    public static double Sub(this double v, double other) {
      return v - other;
    }
    public static double Div(this double v, double other) {
      return v / other;
    }

    public static double Avg(this double v, double other) {
      return (v + other) / 2;
    }
    public static int Avg(this int v, double other) {
      return ((v + other) / 2).ToInt();
    }
    public static double? Abs(this double? v) {
      return v.HasValue ? v.Value.Abs() : (double?)null;
    }
    public static double Abs(this double v) {
      return Math.Abs(v);
    }
    public static double Abs(this double v, double other) {
      return Math.Abs(v - other);
    }
    public static int Abs(this int v) {
      return Math.Abs(v);
    }
    public static int Abs(this int v, int other) {
      return Math.Abs(v - other);
    }
    public static int Sign(this int v) {
      return Math.Sign(v);
    }
    public static int Sign(this double v) {
      return Math.Sign(v);
    }
    public static int Sign(this double v, double other) {
      return Math.Sign(v - other);
    }
    public static int SignUp(this double v) {
      return Math.Sign(v) >= 0 ? 1 : -1;
    }
    public static int SignUp(this double v, double other) {
      return (v - other).SignUp();
    }
    public static int SignDown(this double v, double other) {
      var s = Math.Sign(v - other);
      return s > 0 ? 1 : -1;
    }
    public static double Max(this double? v, double? other) {
      return !v.HasValue ? other.GetValueOrDefault(double.NaN) : !other.HasValue ? v.GetValueOrDefault(double.NaN) : Math.Max(v.Value, other.Value);
    }
    public static double Max(this double v, double other) {
      return double.IsNaN(v) ? other : double.IsNaN(other) ? v : Math.Max(v, other);
    }
    public static double Max(this double v, params double[] other) {
      return other.Aggregate(v, (p, n) => p.Max(n));
    }
    public static int Max(this int v, params int[] other) {
      return other.Aggregate(v, (p, n) => p.Max(n));
    }
    public static double Min(this double? v, double? other) {
      return !v.HasValue ? other.GetValueOrDefault(double.NaN) : !other.HasValue ? v.GetValueOrDefault(double.NaN) : Math.Min(v.Value, other.Value);
    }
    public static double Min(this double v, double other) {
      return double.IsNaN(v) ? other : double.IsNaN(other) ? v : Math.Min(v, other);
    }
    public static double Min(this double v, params double[] other) {
      return other.Aggregate(v, (p, n) => p.Min(n));
    }
    public static int Min(this int v, int other) {
      return Math.Min(v, other);
    }
    public static int Min(this int v, params int[] other) {
      return other.Aggregate(v, (p, n) => p.Min(n));
    }

    public static int Floor(this double d) { return (int)Math.Floor(d); }
    public static int Floor(this double d, double other) { return (int)Math.Floor(d / other); }
    public static int Floor(this int d, double other) { return (int)Math.Floor(d / other); }
    public static int Ceiling(this double d) { return (int)Math.Ceiling(d); }
    public static int ToInt(this double d, bool useCeiling) {
      return (int)(useCeiling ? Math.Ceiling(d) : Math.Floor(d));
    }
    public static int ToInt(this double d) { return (int)Math.Round(d, 0); }
    public static double Percentage<T>(this int v, double other) {
      return other.Percentage(v);
    }
    /// <summary>
    /// (one - other) / Math.Max(one, other);
    /// </summary>
    /// <param name="v"></param>
    /// <param name="other"></param>
    /// <returns></returns>
    public static double Percentage(this double v, double other) {
      var max = Math.Max(Math.Abs(v), Math.Abs(other));
      var min = Math.Min(Math.Abs(v), Math.Abs(other));
      return (v - other).Abs() / max;
    }
    public static int ToPercent(this double d) { return (int)Math.Round(d * 100, 0); }
    public static int ToPercent(this double d, double other) { return (int)Math.Round(d.Percentage(other) * 100, 0); }

    #region Between
    public static bool Between(this int value, double d1, double d2) {
      return Math.Min(d1, d2) <= value && value <= Math.Max(d1, d2);
    }
    public static bool Between(this double value, double[] dd) {
      if(dd.Length != 2)
        throw new Exception(new { dd = new { dd.Length, Expected = 2 } } + "");
      return value.Between(dd[0], dd[1]);
    }
    public static bool Between(this double value, double d1, double d2) {
      return d1 < d2 ? d1 <= value && value <= d2 : d2 <= value && value <= d1;
    }
    public static bool Between(this DateTime value, DateTime[] d) {
      return value.Between(d[0],d[1]);
    }
    public static bool Between(this DateTime value, DateTime d1, DateTime d2) {
      return d1 <= d2 ? d1 <= value && value <= d2 : d2 <= value && value <= d1;
    }
    public static bool Between(this DateTime value, DateTimeOffset d1, DateTimeOffset d2) {
      return d1 <= d2 ? d1 <= value && value <= d2 : d2 <= value && value <= d1;
    }
    public static bool Between(this double value, Tuple<double, double> tuple) {
      return value.Between(tuple.Item1, tuple.Item2);
    }
    public static bool Between(this DateTimeOffset value, DateTimeOffset d1, DateTimeOffset d2) {
      return d1 <= d2 ? d1 <= value && value <= d2 : d2 <= value && value <= d1;
    }
    public static bool Between(this TimeSpan value, TimeSpan d1, TimeSpan d2) {
      return d1 < d2 ? d1 <= value && value <= d2 : d1 <= value || value <= d2;
    }
    #endregion
    public static double IntOrDouble(this double d, double max = 10) {
      return d.Abs() > max ? d.ToInt() : Math.Round(d, 1);
    }

    public enum RoundTo {
      Second, Minute, MinuteFloor, MinuteCieling, Hour, HourFloor, Day, DayFloor, Month, MonthEnd, Week
    }
    public static DateTimeOffset Round(this DateTimeOffset d, RoundTo rt) {
      DateTimeOffset dtRounded = new DateTimeOffset();
      switch(rt) {
        case RoundTo.Second:
          dtRounded = new DateTimeOffset(d.Year, d.Month, d.Day, d.Hour, d.Minute, d.Second, d.Offset);
          if(d.Millisecond >= 500)
            dtRounded = dtRounded.AddSeconds(1);
          break;
        case RoundTo.Minute:
          dtRounded = new DateTimeOffset(d.Year, d.Month, d.Day, d.Hour, d.Minute, 0, d.Offset);
          if(d.Second >= 30)
            dtRounded = dtRounded.AddMinutes(1);
          break;
        case RoundTo.MinuteFloor:
          dtRounded = new DateTimeOffset(d.Year, d.Month, d.Day, d.Hour, d.Minute, 0, d.Offset);
          break;
        case RoundTo.MinuteCieling:
          dtRounded = new DateTimeOffset(d.Year, d.Month, d.Day, d.Hour, d.Minute, 0, d.Offset);
          if(d.Second > 0)
            dtRounded = dtRounded.AddMinutes(1);
          break;
        case RoundTo.Hour:
          dtRounded = new DateTimeOffset(d.Year, d.Month, d.Day, d.Hour, 0, 0, d.Offset);
          if(d.Minute >= 30)
            dtRounded = dtRounded.AddHours(1);
          break;
        case RoundTo.HourFloor:
          dtRounded = new DateTimeOffset(d.Year, d.Month, d.Day, d.Hour, 0, 0, d.Offset);
          break;
        case RoundTo.DayFloor:
          dtRounded = new DateTimeOffset(d.Year, d.Month, d.Day, 0, 0, 0, d.Offset);
          break;
        case RoundTo.Day:
          dtRounded = new DateTimeOffset(d.Year, d.Month, d.Day, 0, 0, 0, d.Offset);
          if(d.Hour >= 12)
            dtRounded = dtRounded.AddDays(1);
          break;
        case RoundTo.Month:
          dtRounded = new DateTimeOffset(d.Year, d.Month, 1, 0, 0, 0, d.Offset);
          break;
        case RoundTo.MonthEnd:
          dtRounded = new DateTimeOffset(d.Year, d.Month, 1, 0, 0, 0, d.Offset).AddMonths(1).AddDays(-1);
          break;
        case RoundTo.Week:
          dtRounded = d.AddDays(-(int)d.DayOfWeek).Date;
          break;
      }
      return dtRounded;
    }
    public static DateTime SetKind(this DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Local);
    public static DateTime Round(this DateTime d, RoundTo rt) {
      DateTime dtRounded = new DateTime();
      switch(rt) {
        case RoundTo.Second:
          dtRounded = new DateTime(d.Year, d.Month, d.Day, d.Hour, d.Minute, d.Second);
          if(d.Millisecond >= 500)
            dtRounded = dtRounded.AddSeconds(1);
          break;
        case RoundTo.Minute:
          dtRounded = new DateTime(d.Year, d.Month, d.Day, d.Hour, d.Minute, 0);
          if(d.Second >= 30)
            dtRounded = dtRounded.AddMinutes(1);
          break;
        case RoundTo.MinuteFloor:
          dtRounded = new DateTime(d.Year, d.Month, d.Day, d.Hour, d.Minute, 0);
          break;
        case RoundTo.MinuteCieling:
          dtRounded = new DateTime(d.Year, d.Month, d.Day, d.Hour, d.Minute, 0);
          if(d.Second > 0)
            dtRounded = dtRounded.AddMinutes(1);
          break;
        case RoundTo.Hour:
          dtRounded = new DateTime(d.Year, d.Month, d.Day, d.Hour, 0, 0);
          if(d.Minute >= 30)
            dtRounded = dtRounded.AddHours(1);
          break;
        case RoundTo.HourFloor:
          dtRounded = new DateTime(d.Year, d.Month, d.Day, d.Hour, 0, 0);
          break;
        case RoundTo.DayFloor:
          dtRounded = new DateTime(d.Year, d.Month, d.Day, 0, 0, 0);
          break;
        case RoundTo.Day:
          dtRounded = new DateTime(d.Year, d.Month, d.Day, 0, 0, 0);
          if(d.Hour >= 12)
            dtRounded = dtRounded.AddDays(1);
          break;
        case RoundTo.Month:
          dtRounded = new DateTime(d.Year, d.Month, 1, 0, 0, 0);
          break;
        case RoundTo.MonthEnd:
          dtRounded = new DateTime(d.Year, d.Month, 1, 0, 0, 0).AddMonths(1).AddDays(-1);
          break;
        case RoundTo.Week:
          dtRounded = d.AddDays(-(int)d.DayOfWeek).Date;
          break;
      }
      return dtRounded;
    }
    public static DateTimeOffset Round(this DateTimeOffset dt) { return new DateTimeOffset(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0, dt.Offset); }
    public static DateTimeOffset Round_(this DateTimeOffset dt) { return dt.AddSeconds(-dt.Second).AddMilliseconds(-dt.Millisecond); }
    public static DateTimeOffset Round(this DateTimeOffset dt, int minutes) {
      dt = dt.Round();
      return dt.AddMinutes(dt.Minute / minutes * minutes - dt.Minute);
    }

    public static DateTime Round(this DateTime dt) { return DateTime.SpecifyKind(new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0), dt.Kind); }
    public static DateTime Round_(this DateTime dt) { return dt.AddSeconds(-dt.Second).AddMilliseconds(-dt.Millisecond); }
    public static DateTime Round(this DateTime dt, int period) {
      dt = dt.Round();
      return dt.AddMinutes(dt.Minute / period * period - dt.Minute);
    }
    public static DateTime GetNextWeekday(DayOfWeek day) => DateTime.Today.GetNextWeekday(day);
    public static DateTime GetNextWeekday(this DateTime start, DayOfWeek day) {
      // The (... + 7) % 7 ensures we end up with a value in the range [0, 6]
      int daysToAdd = ((int)day - (int)start.DayOfWeek + 7) % 7;
      return start.AddDays(daysToAdd);
    }
    /// <summary>
    /// Returns Slope from regression coeffisients array of two values
    /// </summary>
    /// <param name="coeffs"></param>
    /// <returns></returns>
    public static double LineSlope(this double[] coeffs) {
      if(coeffs?.Length != 2)
        throw new IndexOutOfRangeException(new { LineSlope = new { coeffs = new { coeffs?.Length } } } + "");
      return coeffs[1];
    }
    public static double LineValue(this double[] coeffs) {
      if(coeffs.Length != 2)
        throw new IndexOutOfRangeException();
      return coeffs[0];
    }
  }
}
