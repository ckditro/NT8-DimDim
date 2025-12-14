/*
This file is part of a NinjaTrader indicator.

Copyright (C) 2025 DimDim

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

#region Using declarations
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
	// === Command surface for API access ===
	public enum ABPCommand
	{
		// Entry commands (market order with bracket)
		BuyMarket,
		SellMarket,
		
		// Entry commands (limit order with auto-bracket on fill)
		BuyLimit,
		SellLimit,
		
		// Management
		Cancel,
		FlattenAll,
		Breakeven1,
		Breakeven2,
		Half,
		Double,
		Bracket,
		AddStop,
		Naked,
		Split,
		PricePlus,
		EntryPlus
	}
	
	/// <summary>
	/// Bridge indicator for cross-indicator communication with AlightenButtonPanelV0001.
	/// Matches the TickHunterBridge pattern.
	/// </summary>
	public class ABPBridge : Indicator
	{
		// ========== GLOBAL BUS ==========
		private static readonly ConcurrentDictionary<string, WeakReference<AlightenButtonPanelV0001>> Endpoints
			= new ConcurrentDictionary<string, WeakReference<AlightenButtonPanelV0001>>(StringComparer.OrdinalIgnoreCase);
		
		/// <summary>
		/// Register an ABP panel with a named endpoint
		/// </summary>
		public static void Register(string endpointName, AlightenButtonPanelV0001 panel)
		{
			if (string.IsNullOrWhiteSpace(endpointName) || panel == null) return;
			
			var wr = new WeakReference<AlightenButtonPanelV0001>(panel);
			Endpoints[endpointName] = wr;
			
			try { panel.Print($"[ABPBridge] REGISTERED endpoint '{endpointName}'"); } catch { }
		}
		
		/// <summary>
		/// Unregister an endpoint
		/// </summary>
		public static void Unregister(string endpointName)
		{
			if (string.IsNullOrWhiteSpace(endpointName)) return;
			Endpoints.TryRemove(endpointName, out _);
		}
		
		/// <summary>
		/// Check if an endpoint exists and is still alive
		/// </summary>
		public static bool Exists(string endpointName)
		{
			if (string.IsNullOrWhiteSpace(endpointName)) return false;
			
			if (!Endpoints.TryGetValue(endpointName, out var wr) || wr == null)
				return false;
			
			if (!wr.TryGetTarget(out var panel) || panel == null)
			{
				Endpoints.TryRemove(endpointName, out _);
				return false;
			}
			
			return panel.ChartControl != null;
		}
		
		/// <summary>
		/// Send a command to a named endpoint (async, non-blocking)
		/// </summary>
		public static bool Send(string endpointName, ABPCommand cmd, string reason = "API", string arg = null)
		{
			if (string.IsNullOrWhiteSpace(endpointName)) return false;
			
			if (!Endpoints.TryGetValue(endpointName, out var wr) || wr == null)
				return false;
			
			if (!wr.TryGetTarget(out var panel) || panel == null)
			{
				Endpoints.TryRemove(endpointName, out _);
				return false;
			}
			
			var disp = panel.ChartControl?.Dispatcher;
			if (disp == null) return false;
			
			disp.InvokeAsync(() =>
			{
				try
				{
					panel.ApiExecute(cmd, reason, arg);
				}
				catch (Exception ex)
				{
					try { panel.Print($"[ABPBridge] Send exception: {ex.Message}"); } catch { }
				}
			}, DispatcherPriority.Normal);
			
			return true;
		}
		
		/// <summary>
		/// Send a command and acknowledge (logs to Output)
		/// </summary>
		public static bool SendAck(string endpointName, ABPCommand cmd, string reason = null, string arg = null)
		{
			if (string.IsNullOrWhiteSpace(endpointName)) return false;
			
			if (!Endpoints.TryGetValue(endpointName, out var wr) || wr == null)
			{
				NinjaTrader.Code.Output.Process($"[ABPBridge] SendAck: endpoint '{endpointName}' not found", PrintTo.OutputTab1);
				return false;
			}
			
			if (!wr.TryGetTarget(out var panel) || panel == null)
			{
				Endpoints.TryRemove(endpointName, out _);
				NinjaTrader.Code.Output.Process($"[ABPBridge] SendAck: endpoint '{endpointName}' expired", PrintTo.OutputTab1);
				return false;
			}
			
			try
			{
				panel.Print($"[ABPBridge] SendAck: {cmd} to '{endpointName}' arg={arg ?? "null"}");
				panel.ApiExecute(cmd, reason, arg);
				return true;
			}
			catch (Exception ex)
			{
				try { panel.Print($"[ABPBridge] SendAck exception: {ex.Message}"); } catch { }
				return false;
			}
		}
		
		/// <summary>
		/// Synchronous send with timeout
		/// </summary>
		public static bool SendSync(string endpointName, ABPCommand cmd, string reason = "API", string arg = null, int timeoutMs = 1500)
		{
			if (string.IsNullOrWhiteSpace(endpointName)) return false;
			
			if (!Endpoints.TryGetValue(endpointName, out var wr) || wr == null) return false;
			if (!wr.TryGetTarget(out var panel) || panel == null) return false;
			
			var disp = panel.ChartControl?.Dispatcher;
			if (disp == null) return false;
			
			bool ok = false;
			Exception execErr = null;
			
			void DoCall()
			{
				try
				{
					panel.ApiExecute(cmd, reason, arg);
					ok = true;
				}
				catch (Exception ex)
				{
					execErr = ex;
				}
			}
			
			try
			{
				if (disp.CheckAccess())
				{
					DoCall();
				}
				else
				{
					disp.Invoke(DispatcherPriority.Normal, new Action(DoCall));
				}
			}
			catch (Exception ex)
			{
				execErr = ex;
			}
			
			if (!ok && execErr != null)
				NinjaTrader.Code.Output.Process($"[ABPBridge] SendSync failed: {execErr.Message}", PrintTo.OutputTab1);
			
			return ok;
		}
		
		/// <summary>
		/// Broadcast a command to all registered endpoints
		/// </summary>
		public static int Broadcast(ABPCommand cmd, string reason = "API", string arg = null)
		{
			int fired = 0;
			foreach (var kvp in Endpoints.ToArray())
			{
				var wr = kvp.Value;
				if (wr != null && wr.TryGetTarget(out var panel) && panel?.ChartControl?.Dispatcher != null)
				{
					panel.ChartControl.Dispatcher.InvokeAsync(() =>
					{
						try { panel.ApiExecute(cmd, reason, arg); } catch { }
					}, DispatcherPriority.Normal);
					fired++;
				}
				else
				{
					Endpoints.TryRemove(kvp.Key, out _);
				}
			}
			return fired;
		}
		
		/// <summary>
		/// Query if the endpoint is flat (no open position)
		/// </summary>
		public static bool TryQueryIsFlat(string endpointName, out bool isFlat)
		{
			isFlat = true;
			try
			{
				if (string.IsNullOrWhiteSpace(endpointName)) return false;
				if (!Endpoints.TryGetValue(endpointName, out var wr) || wr == null) return false;
				if (!wr.TryGetTarget(out var panel) || panel == null) return false;
				
				isFlat = panel.ApiIsFlat();
				return true;
			}
			catch { return false; }
		}
		
		/// <summary>
		/// Query if the endpoint has working orders
		/// </summary>
		public static bool TryQueryHasWorking(string endpointName, out bool hasWorking)
		{
			hasWorking = false;
			try
			{
				if (string.IsNullOrWhiteSpace(endpointName)) return false;
				if (!Endpoints.TryGetValue(endpointName, out var wr) || wr == null) return false;
				if (!wr.TryGetTarget(out var panel) || panel == null) return false;
				
				hasWorking = panel.ApiHasWorkingOrders();
				return true;
			}
			catch { return false; }
		}
		
		// ========== INSTANCE (bind to ABP on THIS chart) ==========
		
		[NinjaScriptProperty]
		[Display(Name = "Endpoint Name", GroupName = "Bridge", Order = 0)]
		public string EndpointName { get; set; } = "abp-default";
		
		[NinjaScriptProperty]
		[Display(Name = "Auto Bind (find ABP on this chart)", GroupName = "Bridge", Order = 1)]
		public bool AutoBind { get; set; } = true;
		
		private bool isBound;
		private WeakReference<AlightenButtonPanelV0001> myABPRef;
		
		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Name                     = "ABPBridge";
				Description              = "Pairs with AlightenButtonPanelV0001 on THIS chart and exposes a cross-chart command endpoint.";
				Calculate                = Calculate.OnBarClose;
				IsOverlay                = true;
				IsSuspendedWhileInactive = true;
			}
			else if (State == State.DataLoaded)
			{
				if (AutoBind && !isBound)
					TryBindToLocalABP();
			}
			else if (State == State.Terminated)
			{
				Unbind();
			}
		}
		
		protected override void OnBarUpdate()
		{
			// Retry binding until it succeeds
			if (AutoBind && !isBound)
				TryBindToLocalABP();
		}
		
		private void TryBindToLocalABP()
		{
			try
			{
				var cc = ChartControl;
				if (cc == null || string.IsNullOrWhiteSpace(EndpointName))
					return;
				
				// Find ABP instance on THIS chart
				var abp = cc.Indicators?.OfType<AlightenButtonPanelV0001>().FirstOrDefault();
				if (abp == null)
					return;
				
				myABPRef = new WeakReference<AlightenButtonPanelV0001>(abp);
				Endpoints[EndpointName] = myABPRef;
				isBound = true;
				Print($"[ABPBridge] Bound to '{EndpointName}'");
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
					&& myABPRef != null
					&& ReferenceEquals(wr, myABPRef))
				{
					Endpoints.TryRemove(EndpointName, out _);
				}
			}
			catch { }
			finally
			{
				isBound = false;
				myABPRef = null;
			}
		}
		
		/// <summary>
		/// Convenience: send command to this instance's endpoint
		/// </summary>
		public bool SendToMe(ABPCommand cmd, string reason = "API", string arg = null)
			=> !string.IsNullOrWhiteSpace(EndpointName) && Send(EndpointName, cmd, reason, arg);
		
		/// <summary>
		/// Convenience: broadcast to all ABP endpoints
		/// </summary>
		public int BroadcastAll(ABPCommand cmd, string reason = "API", string arg = null)
			=> Broadcast(cmd, reason, arg);
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private ABPBridge[] cacheABPBridge;
		public ABPBridge ABPBridge(string endpointName, bool autoBind)
		{
			return ABPBridge(Input, endpointName, autoBind);
		}

		public ABPBridge ABPBridge(ISeries<double> input, string endpointName, bool autoBind)
		{
			if (cacheABPBridge != null)
				for (int idx = 0; idx < cacheABPBridge.Length; idx++)
					if (cacheABPBridge[idx] != null && cacheABPBridge[idx].EndpointName == endpointName && cacheABPBridge[idx].AutoBind == autoBind && cacheABPBridge[idx].EqualsInput(input))
						return cacheABPBridge[idx];
			return CacheIndicator<ABPBridge>(new ABPBridge(){ EndpointName = endpointName, AutoBind = autoBind }, input, ref cacheABPBridge);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.ABPBridge ABPBridge(string endpointName, bool autoBind)
		{
			return indicator.ABPBridge(Input, endpointName, autoBind);
		}

		public Indicators.ABPBridge ABPBridge(ISeries<double> input , string endpointName, bool autoBind)
		{
			return indicator.ABPBridge(input, endpointName, autoBind);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.ABPBridge ABPBridge(string endpointName, bool autoBind)
		{
			return indicator.ABPBridge(Input, endpointName, autoBind);
		}

		public Indicators.ABPBridge ABPBridge(ISeries<double> input , string endpointName, bool autoBind)
		{
			return indicator.ABPBridge(input, endpointName, autoBind);
		}
	}
}

#endregion
