﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace HedgeHog {
  public static class ReflectionCore {
    public static bool IsAnonymous(this object o) {
      return o.GetType().Name.Contains("AnonymousType");
    }
    public static void SetProperty<T>(this object o, string p, T v) {
      System.Reflection.PropertyInfo pi = o.GetType().GetProperty(p);
      if(pi != null)
        pi.SetValue(o, v, new object[] { });
      else {
        System.Reflection.FieldInfo fi = o.GetType().GetField(p);
        if(fi == null)
          throw new NotImplementedException("Property " + p + " is not implemented in " + o.GetType().FullName + ".");
        fi.SetValue(o, v);
      }
    }


    public static void SetProperty(this object o, string p, object v) {
      if(o == null)
        throw new NullReferenceException(new { o, p, v } + "");
      o.SetProperty(p, v, pi => pi.GetSetMethod() != null || pi.GetSetMethod(true) != null);
    }
    public static void SetProperty(this object o, string p, object v, Func<PropertyInfo, bool> propertyPredicate = null) {
      var convert = new Func<object, Type, object>((value, type) => {
        if(value != null) {
          Type tThis = Nullable.GetUnderlyingType(type);
          var isNullable = true;
          if(tThis == null) {
            tThis = type;
            isNullable = false;
          }
          if(tThis.IsEnum)
            try {
              return Enum.Parse(tThis, v + "", true);
            } catch(Exception exc) {
              throw new ArgumentException(new { property = p } + "", exc);
            }
          return string.IsNullOrWhiteSpace((v ?? "") + "") && isNullable ? null : Convert.ChangeType(v, tThis, null);
        }
        return value;
      });
      var t = o.GetType();
      var pi = t.GetProperty(p);
      if(pi == null) {
        pi = t.GetProperties().FirstOrDefault(prop => prop.GetCustomAttributes<DisplayNameAttribute>().Any(dn => dn.DisplayName == p));
      }
      if(propertyPredicate != null) {
        if(pi == null)
          throw new MissingMemberException(t.Name, p);
        if(!propertyPredicate(pi))
          return;
      }
      if(pi != null)
        pi.SetValue(o, v = convert(v, pi.PropertyType), new object[] { });
      else {
        System.Reflection.FieldInfo fi = o.GetType().GetField(p);
        if(fi == null)
          throw new MissingMemberException(t.Name, p);
        fi.SetValue(o, convert(v, fi.FieldType));
      }
    }

    public static string[] GetLambdas<TPropertySource>(params Expression<Func<TPropertySource, object>>[] expressions) {
      return expressions.Select(expression => GetLambda<TPropertySource>(expression)).ToArray();
    }
    public static string GetLambda<TPropertySource, TProperty>(Expression<Func<TPropertySource, TProperty>> expression) {
      var lambda = expression as LambdaExpression;
      MemberExpression memberExpression;
      if(lambda.Body is UnaryExpression) {
        var unaryExpression = lambda.Body as UnaryExpression;
        memberExpression = unaryExpression.Operand as MemberExpression;
      } else {
        memberExpression = lambda.Body as MemberExpression;
      }

      Debug.Assert(memberExpression != null, "Please provide a lambda expression like 'n => n.PropertyName'");

      if(memberExpression != null) {
        var propertyInfo = memberExpression.Member as PropertyInfo;

        return propertyInfo.Name;
      }

      return null;
    }
    public static string GetLambda<TPropertySource>(Expression<Func<TPropertySource, object>> expression) {
      var lambda = expression as LambdaExpression;
      MemberExpression memberExpression;
      if(lambda.Body is UnaryExpression) {
        var unaryExpression = lambda.Body as UnaryExpression;
        memberExpression = unaryExpression.Operand as MemberExpression;
      } else {
        memberExpression = lambda.Body as MemberExpression;
      }

      Debug.Assert(memberExpression != null, "Please provide a lambda expression like 'n => n.PropertyName'");

      if(memberExpression != null) {
        var propertyInfo = memberExpression.Member as PropertyInfo;

        return propertyInfo.Name;
      }

      return null;
    }

    public static string GetLambda(Expression<Func<object>> func) { return func.Name(); }
    public static string[] GetLambdas(params Expression<Func<object>>[] funcs) { return funcs.Names(); }
    public static string[] Names(this Expression<Func<object>>[] funcs) {
      var names = new List<string>();
      foreach(var e in funcs)
        names.Add(e.Name());
      return names.ToArray();
    }

    public static string Name(this Expression<Func<object>> propertyLamda) {
      var body = propertyLamda.Body as UnaryExpression;
      if(body == null) {
        return ((propertyLamda as LambdaExpression).Body as MemberExpression).Member.Name;
      } else {
        var operand = body.Operand as MemberExpression;
        var member = operand.Member;
        return member.Name;
      }
    }

    public static string Name(this LambdaExpression propertyExpression) {
      var body = propertyExpression.Body as MemberExpression;
      if(body == null)
        throw new ArgumentException("'propertyExpression' should be a member expression");

      // Extract the right part (after "=>")
      var vmExpression = body.Expression as ConstantExpression;
      if(vmExpression == null)
        throw new ArgumentException("'propertyExpression' body should be a constant expression");

      // Create a reference to the calling object to pass it as the sender
      LambdaExpression vmlambda = System.Linq.Expressions.Expression.Lambda(vmExpression);
      Delegate vmFunc = vmlambda.Compile();
      object vm = vmFunc.DynamicInvoke();
      return body.Member.Name;
    }

  }
}
