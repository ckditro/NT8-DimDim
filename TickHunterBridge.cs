/*
MIT License

Copyright (c) 2025 DimDim

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, subject to the following conditions...

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND.
*/

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Windows.Threading;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Indicators.TickHunterTA;

#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class TickHunterBridge : Indicator
    {
        // ========== GLOBAL BUS ==========
        private static readonly ConcurrentDictionary<string, WeakReference<TickHunter>> Endpoints
            = new ConcurrentDictionary<string, WeakReference<TickHunter>>(StringComparer.OrdinalIgnoreCase);
		
		
		public static bool SetActive(string endpointName, bool active)
		{
		    try
		    {
		        if (string.IsNullOrWhiteSpace(endpointName))
		            return false;
		
		        if (!Endpoints.TryGetValue(endpointName, out var wr) || wr == null)
		            return false;
		
		        if (!wr.TryGetTarget(out var th) || th == null)
		        {
		            // prune dead endpoint
		            Endpoints.TryRemove(endpointName, out _);
		            return false;
		        }
		
		        // Dispatcher can be null during reloads
		        var disp = th.ChartControl?.Dispatcher ?? System.Windows.Application.Current?.Dispatcher;
		        if (disp == null || disp.HasShutdownStarted || disp.HasShutdownFinished)
		            return false;
		
		        void DoCallSafe()
		        {
		            try
		            {
		                var t = th.GetType();
		
		                // 1) Explicit API if available
		                var mi = t.GetMethod("ApiSetActive", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
		                      ?? t.GetMethod("SetActive",   System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
		
		                if (mi != null)
		                {
		                    mi.Invoke(th, new object[] { active });
		                    return;
		                }
		
		                // 2) Writable property fallback
		                var pi = t.GetProperty("Active",   System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
		                      ?? t.GetProperty("IsActive", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
		
		                if (pi != null && pi.CanWrite)
		                {
		                    pi.SetValue(th, active);
		                    return;
		                }
		
		                // No API available: just no-op (do NOT throw inside dispatcher)
		            }
		            catch
		            {
		                // Swallow – chart may be reloading/disposing; we want a clean bail
		            }
		        }
		
		        // Non-blocking; safe during reloads
		        disp.InvokeAsync(DoCallSafe, DispatcherPriority.Normal);
		        return true;
		    }
		    catch (ObjectDisposedException) { return false; }
		    catch (InvalidOperationException) { return false; } // dispatcher shutting down
		    catch { return false; }
		}



        public static bool Send(string endpointName, THCommand cmd, string reason = "API")
        {
            if (string.IsNullOrWhiteSpace(endpointName))
                return false;

            if (!Endpoints.TryGetValue(endpointName, out var wr) || wr == null)
                return false;

            if (!wr.TryGetTarget(out var th) || th == null)
                return false;

            var disp = th.ChartControl?.Dispatcher;
            if (disp == null)
                return false;

            // Map enum by name to avoid hard dependency issues
            var thCmd = MapToTickHunterCommand(cmd);
            if (thCmd == null) return false;

            disp.InvokeAsync(() => th.ApiExecute(thCmd.Value, reason), DispatcherPriority.Normal);
            return true;
        }

 
		// bool ok = TickHunterBridge.SendSync(endpoint, THCommand.BuyPop, "reason");
		
		public static bool SendSync(string endpointName, THCommand cmd, string reason = "API", int timeoutMs = 1500)
		{
		    if (string.IsNullOrWhiteSpace(endpointName)) return false;
		
		    // 1) Resolve endpoint → TickHunter instance
		    if (!Endpoints.TryGetValue(endpointName, out var wr) || wr == null) return false;
		    if (!wr.TryGetTarget(out var th) || th == null) return false;
		
		    // 2) Get chart dispatcher
		    var disp = th.ChartControl?.Dispatcher;
		    if (disp == null) return false;
		
		    // 3) Map bridge command → TickHunter's internal command enum
		    // If your bridge already has MapToTickHunterCommand, use it.
		    // Otherwise this fallback assumes the enum names match.
		    int? thCmd = null;
		    try
		    {
		        // Prefer existing mapper if present
		        var mapMi = typeof(TickHunterBridge).GetMethod("MapToTickHunterCommand",
		                      System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
		        if (mapMi != null)
		        {
		            thCmd = (int?)mapMi.Invoke(null, new object[] { cmd });
		        }
		        else
		        {
		            // Fallback: same-name mapping
		            var thCmdType = th.GetType().Assembly.GetType("NinjaTrader.NinjaScript.Indicators.TickHunter+THCommand");
		            if (thCmdType == null) return false;
		            object parsed = Enum.Parse(thCmdType, cmd.ToString(), ignoreCase: false);
		            thCmd = (int)Convert.ChangeType(parsed, typeof(int));
		        }
		    }
		    catch { return false; }
		
		    if (thCmd == null) return false;
		
		    // 4) Execute on UI thread (synchronously)
		    bool ok = false;
		    Exception execErr = null;
		
		    void DoCall()
		    {
		        try
		        {
		            // ApiExecute(int cmd, string reason) – matching the internal signature
		            var apiMi = th.GetType().GetMethod("ApiExecute",
		                         System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
		            if (apiMi == null)
		                throw new MissingMethodException("TickHunter.ApiExecute not found");
		
		            apiMi.Invoke(th, new object[] { thCmd.Value, reason });
		            ok = true;
		        }
		        catch (Exception ex)
		        {
		            execErr = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null
		                      ? tie.InnerException : ex;
		        }
		    }
		
		    try
		    {
		        if (disp.CheckAccess())
		        {
		            // Already on UI thread
		            DoCall();
		        }
		        else
		        {
		            // Invoke with timeout to avoid hangs
		            disp.Invoke(DispatcherPriority.Normal, new Action(DoCall));
		            // If you want a hard timeout, use this overload (requires .NET with timeout signature):
		            // disp.Invoke(DispatcherPriority.Normal, TimeSpan.FromMilliseconds(timeoutMs), new Action(DoCall));
		        }
		    }
		    catch (Exception ex)
		    {
		        execErr = ex;
		    }
		
		    // Optional: log failures to NT Output
		    if (!ok && execErr != null)
		        NinjaTrader.Code.Output.Process($"[TH-Bridge] SendSync failed: {execErr.Message}", PrintTo.OutputTab1);
		
		    return ok;
		}

		

        public static int Broadcast(THCommand cmd, string reason = "API")
        {
            int fired = 0;
            foreach (var kvp in Endpoints.ToArray())
            {
                var wr = kvp.Value;
                if (wr != null && wr.TryGetTarget(out var th) && th?.ChartControl?.Dispatcher != null)
                {
                    var thCmd = MapToTickHunterCommand(cmd);
                    if (thCmd == null) continue;

                    th.ChartControl.Dispatcher.InvokeAsync(() => th.ApiExecute(thCmd.Value, reason), DispatcherPriority.Normal);
                    fired++;
                }
                else
                {
                    Endpoints.TryRemove(kvp.Key, out _); // cleanup dead
                }
            }
            return fired;
        }

        public static bool Exists(string endpointName)
        {
            if (!Endpoints.TryGetValue(endpointName, out var wr) || wr == null)
                return false;
            return wr.TryGetTarget(out var th) && th != null && th.ChartControl != null;
        }

        private static TickHunterTA.THCommand? MapToTickHunterCommand(THCommand cmd)
        {
            try
            {
                var name = Enum.GetName(typeof(THCommand), cmd);
                if (name == null) return null;
                return (TickHunterTA.THCommand)Enum.Parse(typeof(TickHunterTA.THCommand), name, ignoreCase: false);
            }
            catch { return null; }
        }

        // ========== INSTANCE (bind to TH on THIS chart) ==========
        [NinjaScriptProperty]
        [Display(Name = "Endpoint Name", GroupName = "Bridge", Order = 0)]
        public string EndpointName { get; set; } = "default";

        [NinjaScriptProperty]
        [Display(Name = "Auto Bind (find TickHunter on this chart)", GroupName = "Bridge", Order = 1)]
        public bool AutoBind { get; set; } = true;

        private bool isBound;
        private WeakReference<TickHunter> myTHRef;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                     = "TickHunterBridge";
                Description              = "Pairs with TickHunter on THIS chart and exposes a cross-chart command endpoint.";
                Calculate                = Calculate.OnBarClose;
                IsOverlay                = true;
                IsSuspendedWhileInactive = true;
            }
            else if (State == State.DataLoaded)
            {
                // Try an early bind (ChartControl may still be null here; we guard inside)
                if (AutoBind && !isBound)
                    TryBindToLocalTickHunter();
            }
            else if (State == State.Terminated)
            {
                Unbind();
            }
        }

        protected override void OnBarUpdate()
        {
            // Retry binding until it succeeds (handles late ChartControl availability)
            if (AutoBind && !isBound)
                TryBindToLocalTickHunter();
        }

        private void TryBindToLocalTickHunter()
        {
            try
            {
                var cc = ChartControl;
                if (cc == null || string.IsNullOrWhiteSpace(EndpointName))
                    return;

                // find TickHunter instance on THIS chart
                var th = cc.Indicators?.OfType<TickHunter>().FirstOrDefault();
                if (th == null)
                    return;

                myTHRef = new WeakReference<TickHunter>(th);
                Endpoints[EndpointName] = myTHRef;
                isBound = true;
                // Optional: Print($"TickHunterBridge bound to '{EndpointName}'");
            }
            catch
            {
                // swallow; safe to retry next bar
            }
        }

        private void Unbind()
        {
            try
            {
                if (!isBound || string.IsNullOrWhiteSpace(EndpointName))
                    return;

                if (Endpoints.TryGetValue(EndpointName, out var wr)
                    && wr != null
                    && myTHRef != null
                    && ReferenceEquals(wr, myTHRef))
                {
                    Endpoints.TryRemove(EndpointName, out _);
                }
            }
            catch { }
            finally
            {
                isBound = false;
                myTHRef = null;
            }
        }

        // convenience
        public bool SendToMe(THCommand cmd, string reason = "API")
            => !string.IsNullOrWhiteSpace(EndpointName) && Send(EndpointName, cmd, reason);

        public int BroadcastAll(THCommand cmd, string reason = "API")
            => Broadcast(cmd, reason);
		
		// Query status from a named endpoint (non-throwing).
		public static bool TryQueryIsFlat(string endpointName, out bool isFlat)
		{
		    isFlat = true;
		    try
		    {
		        if (string.IsNullOrWhiteSpace(endpointName)) return false;
		        if (!Endpoints.TryGetValue(endpointName, out var wr) || wr == null) return false;
		        if (!wr.TryGetTarget(out var th) || th == null) return false;
		
		        // Direct, synchronous read. TH method is lightweight.
		        isFlat = th.ApiIsFlat();
		        return true;
		    }
		    catch { return false; }
		}
		
		// Optional: market position
		public static bool TryQueryMarketPosition(string endpointName, out NinjaTrader.Cbi.MarketPosition mp)
		{
		    mp = NinjaTrader.Cbi.MarketPosition.Flat;
		    try
		    {
		        if (string.IsNullOrWhiteSpace(endpointName)) return false;
		        if (!Endpoints.TryGetValue(endpointName, out var wr) || wr == null) return false;
		        if (!wr.TryGetTarget(out var th) || th == null) return false;
		
		        mp = th.ApiMarketPosition();
		        return true;
		    }
		    catch { return false; }
		}

    }

    // Mirror enum (names must match TickHunterTA.THCommand)
    public enum THCommand
    {
        TPPlus,
        BEPlus,
        SLPlus,
        BuyMarket,
        SellMarket,
        BuyPop,
        SellPop,
        BuyDrop,
        SellDrop,
        Close
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private TickHunterBridge[] cacheTickHunterBridge;
		public TickHunterBridge TickHunterBridge(string endpointName, bool autoBind)
		{
			return TickHunterBridge(Input, endpointName, autoBind);
		}

		public TickHunterBridge TickHunterBridge(ISeries<double> input, string endpointName, bool autoBind)
		{
			if (cacheTickHunterBridge != null)
				for (int idx = 0; idx < cacheTickHunterBridge.Length; idx++)
					if (cacheTickHunterBridge[idx] != null && cacheTickHunterBridge[idx].EndpointName == endpointName && cacheTickHunterBridge[idx].AutoBind == autoBind && cacheTickHunterBridge[idx].EqualsInput(input))
						return cacheTickHunterBridge[idx];
			return CacheIndicator<TickHunterBridge>(new TickHunterBridge(){ EndpointName = endpointName, AutoBind = autoBind }, input, ref cacheTickHunterBridge);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.TickHunterBridge TickHunterBridge(string endpointName, bool autoBind)
		{
			return indicator.TickHunterBridge(Input, endpointName, autoBind);
		}

		public Indicators.TickHunterBridge TickHunterBridge(ISeries<double> input , string endpointName, bool autoBind)
		{
			return indicator.TickHunterBridge(input, endpointName, autoBind);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.TickHunterBridge TickHunterBridge(string endpointName, bool autoBind)
		{
			return indicator.TickHunterBridge(Input, endpointName, autoBind);
		}

		public Indicators.TickHunterBridge TickHunterBridge(ISeries<double> input , string endpointName, bool autoBind)
		{
			return indicator.TickHunterBridge(input, endpointName, autoBind);
		}
	}
}

#endregion

