#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows;
using System.Windows.Input;

using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;

using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;

// Aliases to avoid ambiguity
using DWFactory  = SharpDX.DirectWrite.Factory;
using DWFontWeight = SharpDX.DirectWrite.FontWeight;
using DWFontStyle  = SharpDX.DirectWrite.FontStyle;

using System.Collections.Generic;                 // <-- List<>, IEnumerable<>
using System.Collections.Specialized;             // <-- INotifyCollectionChanged
using System.Reflection;                          // <-- BindingFlags
using System.Windows.Media;                       // <-- Brushes (WPF) VisualTreeHelper
using System.Xml.Serialization;
using System.Windows.Threading;   // DispatcherTimer

using System.Linq;
using NinjaTrader.Gui.NinjaScript;       // IndicatorRenderBase
using NinjaTrader.Core.FloatingPoint;
using System.Diagnostics;
using System.Threading; // (optional)
using System.IO;        // File-based settings persistence

using vgaBridge = NinjaTrader.NinjaScript.Indicators.vgaDROEBv3Bridge;
using vgaPadCommand = NinjaTrader.NinjaScript.Indicators.vgaPadCommand;

#endregion


namespace NinjaTrader.NinjaScript.Indicators.DimDim	
{
	// --- Minimal, safe reflection helper (no vendor internals) ---
	static class ReflectSafe
	{
	    // Prefer IndicatorBase types
	    public static bool IsIndicatorBase(Type t)
	        => t != null && typeof(IndicatorBase).IsAssignableFrom(t);
	
	    // Try to resolve a type by namespace + short name, optionally loading a dll
	    // Never invokes vendor methods; only Assembly.LoadFrom and Type.GetType
	    public static Type ResolveIndicatorType(string ns, string name, string dllPath = null)
	    {
	        if (string.IsNullOrEmpty(ns) || string.IsNullOrEmpty(name))
	            return null;
	
	        // 1) Already-loaded assemblies first (fast + safe)
	        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
	        {
	            var t = asm.GetType($"{ns}.{name}", throwOnError: false, ignoreCase: false);
	            if (IsIndicatorBase(t))
	                return t;
	        }
	
	        // 2) Fallback: explicit load if provided
	        if (!string.IsNullOrEmpty(dllPath))
	        {
	            try
	            {
	                var asm = Assembly.LoadFrom(dllPath);
	                // Prefer assembly-qualified name format: "Ns.Name, FullAssemblyName"
	                var t = Type.GetType($"{ns}.{name}, {asm.FullName}", throwOnError: false, ignoreCase: false)
	                        ?? asm.GetType($"{ns}.{name}", throwOnError: false, ignoreCase: false);
	                if (IsIndicatorBase(t))
	                    return t;
	            }
	            catch { /* swallow */ }
	        }
	
	        return null;
	    }
	
	    // Safely read Values[] from IndicatorBase
	    public static Series<double>[] SafeGetValues(IndicatorBase ib)
	    {
	        try { return ib?.Values; } catch { return null; }
	    }
	
	    // Safely read Plots array (exists on Indicator, not on pure IndicatorBase).
	    // We keep it reflection-only and only read 'Name' when present.
	    public static (int count, Func<int,string> nameAt) SafeGetPlots(IndicatorBase ib)
	    {
	        try
	        {
	            if (ib == null) return (0, _ => null);
	
	            var p = ib.GetType().GetProperty("Plots",
	                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
	            var plotsObj = p?.GetValue(ib);
	            if (plotsObj is System.Collections.IEnumerable en)
	            {
	                var list = new List<object>();
	                foreach (var it in en) list.Add(it);
	
	                string NameOf(object plot)
	                {
	                    if (plot == null) return null;
	                    try
	                    {
	                        var np = plot.GetType().GetProperty("Name",
	                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
	                        return np?.GetValue(plot) as string;
	                    }
	                    catch { return null; }
	                }
	
	                return (list.Count, i =>
	                {
	                    if (i < 0 || i >= list.Count) return null;
	                    return NameOf(list[i]) ?? $"Plot{i}";
	                });
	            }
	        }
	        catch { }
	        return (0, _ => null);
	    }
	}

	
    public class DDPlotReader : Indicator
    {
        private enum RedrawMode { Full, Throttled, Minimal, Disabled }

		private enum TradeState { Flat, InTrade, WaitingFlat }
		private TradeState thState = TradeState.Flat;
		
		public enum THEntryStyle { Pop, Drop, Market }

		// inside DDPlotReader class
		private enum Dest { None, TickHunter, DDATM, DDTP }
		private Dest lastDest = Dest.None;
		
		
		// Remember if we sent a Close (so we don't spam it)
		private int lastCloseBar = -1;

		// --- Debugging
		[NinjaScriptProperty, Display(Name="Debug Mode", GroupName="Debug", Order=0)]
		public bool DebugMode { get; set; } = true;
		
		// In DDPlotReader.cs (inside the class)
		[Browsable(false), XmlIgnore]
		public bool HasSelection => (selectedIndex >= 0 && selectedIndex < options.Count);
		
		[Browsable(false), XmlIgnore]
		public double SelectedValue => double.IsNaN(selectedLiveValue) ? double.NaN : selectedLiveValue;
		
		// === TickHunter Bridge integration ===
		[NinjaScriptProperty, Display(Name="Send to TickHunter", GroupName="TickHunter", Order=0)]
		public bool SendToTickHunter { get; set; } = false;
		
		[NinjaScriptProperty, Display(Name="Bridge Endpoint", GroupName="TickHunter", Order=1)]
		public string BridgeEndpoint { get; set; } = "default";

		#region VGA Bridge
		[NinjaScriptProperty, Display(Name="Send to vgaDROEBv3", GroupName="VGA Bridge", Order=0)]
		public bool SendToVgaBridge { get; set; } = false;

		[NinjaScriptProperty, Display(Name="VGA Bridge Endpoint", GroupName="VGA Bridge", Order=1)]
		public string VgaBridgeEndpoint { get; set; } = "vga_droeb";
		#endregion
		
		[NinjaScriptProperty, Display(Name="Fire Once Per Bar", GroupName="TickHunter", Order=2)]
		public bool FireOncePerBar { get; set; } = true;
		
		// === TickHunter throttle ===
		[NinjaScriptProperty, Display(Name="Min Bars Between Triggers", GroupName="TickHunter", Order=3)]
		public int MinBarsBetweenTriggers { get; set; } = 3;   // 0 = no spacing limit
		
		
		public THEntryStyle EntryMode { get; set; } = THEntryStyle.Drop;
		
		// === TickElectrifier - force faster redraws when in trade ===
		[NinjaScriptProperty, Display(Name="Electrifier When In Trade", GroupName="TickHunter", Order=4)]
		public bool ElectrifierWhenInTrade { get; set; } = false;
		
		[NinjaScriptProperty, Display(Name="Electrifier Update (ms)", GroupName="TickHunter", Order=5)]
		public int ElectrifierUpdateMs { get; set; } = 100;   // milliseconds between forced redraws
		
		// === Pending order management ===
		[NinjaScriptProperty, Display(Name="Pending Order Timeout (bars)", GroupName="TickHunter", Order=6)]
		public int PendingOrderTimeoutBars { get; set; } = 2;   // bars to wait before cancelling unfilled pending order
		
		// === DDATM integration ===
		[NinjaScriptProperty, Display(Name="Send to DDATM", GroupName="DDATM", Order=0)]
		public bool SendToDDATM { get; set; } = false;
		
		[NinjaScriptProperty, Display(Name="DDATM Endpoint", GroupName="DDATM", Order=1)]
		public string DDATMEndpoint { get; set; } = "ddatm-default";
		
		// Optional: use DDATM flat check to clear FSM (falls back to 'true' if query not available)
		[NinjaScriptProperty, Display(Name="Use DDATM Flat Check", GroupName="DDATM", Order=2)]
		public bool UseDDATMFlatCheck { get; set; } = false;

		// === DDTP integration ===
		[NinjaScriptProperty, Display(Name="Send to DDTP", GroupName="DDTP", Order=0)]
		public bool SendToDDTP { get; set; } = false;
		
		[NinjaScriptProperty, Display(Name="DDTP Endpoint", GroupName="DDTP", Order=1)]
		public string DDTPEndpoint { get; set; } = "ddtp-default";


		
		// runtime
		private int lastTriggerBar = -1;
	
		private int  lastSentSign   = 0;   // -1 bear, +1 bull, 0 neutral
		private int  lastSentOnBar  = -1;  // CurrentBar index when last command was sent
		private int  lastApiCallBar = -1;
		private bool lastVgaFibWasFive = false;
		private bool vgaFibInitialized = false;

		// ---- Catch-up state ----
		private int  pendingSign = 0;           // +1/-1 if a cross happened while endpoint wasn't ready
		private string pendingReason = null;
		private int  pendingSinceBar = -1;
		
		private bool endpointWasReady = false;  // tracks readiness rising edge
		private int  lastKnownSign    = 0;      // sign we last observed (for ready-edge logic)

		private bool pendingEntryActive = false;
		private int pendingEntryBar = -1;
		private int tradeConfirmedBar = -1;    // bar where we confirmed trade is filled (grace period)
		private bool tradeWasConfirmed = false; // true after bridge reports NOT flat (trade filled)

		private int lastSeriesCountAccepted = -1;
		private int lastIndexAccepted       = -1;
		
		// Per-bar hard blocker: after a POP is sent on bar N, no more sends while CurrentBar == N
		private int lastPopBar = -1;

		
		// --- Zero run tracking (per selected plot) ---
		private int zeroRun = 0;                         // consecutive aligned zeros
		private const int ZeroRunAccept = 3;             // require 3 aligned zeros to accept true zero

		
		// Tracks whether the selected source emitted a new sample on *this* primary bar
		private bool sawSourceUpdateThisBar = false;
		
		// Snapshot of the current primary bar’s time
		private DateTime currBarTime = DateTime.MinValue;

		
				// Track visited objects (by identity) to avoid cycles in deep search
		private readonly HashSet<int> dbgVisitedIds = new HashSet<int>();
		[ThreadStatic]
		private static HashSet<int> forceUpdateStack;
		private static int ObjId(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
		
		// Pretty name for types and values
		private static string TName(object o) => o == null ? "null" : (o.GetType().FullName ?? o.GetType().Name);
		
		// --- scan control / stability ---
		private readonly List<PlotOption> optionsStable = new List<PlotOption>();
		private volatile bool rebuildInProgress;
		private int backoffPow = 0;                    // 0..5 => exponential cooldown
		private long nextScanDueMs = 0;                // Environment.TickCount64 when scan allowed again
		private const int MinScanIntervalMs = 1500;    // normal steady-state throttle
		private const int MaxBackoffPow = 5;           // 2^5 = 32x
		private const int BaseBackoffMs = 1500;        // 1.5s * 2^pow
		
		// UI: refresh button next to dropdown
		private RectangleF refreshRect;                // clickable area for manual refresh
		private bool hoverRefresh = false;

				// --- HelloWin-style reliability state ---
		private double lastStableValue = double.NaN;   // last accepted, post-filters
		private double lastRawValue    = double.NaN;   // last raw read (pre-filters)
		private int    lastAcceptedIdx = -1;           // abs index we accepted
		private DateTime lastAcceptedTime = DateTime.MinValue;
		
		// Preserved sign from previous bar for cross detection (avoids false triggers on bar open)
		private int    prevBarSign = 0;                // sign of lastStableValue at end of prev bar
		
		private int deadFrames = 0;                    // consecutive frames with no valid read
		private int badZeroFrames = 0;                 // frames that produced suspicious 0
		
		// === Rolling tick buffer per bar - prevents signal carryover ===
		private int    tickBufferBar = -1;             // which bar this buffer belongs to
		private int    tickBufferCount = 0;            // how many ticks we've seen on this bar
		private double tickBufferFirstValue = double.NaN;  // first confirmed value this bar
		private double tickBufferLastValue = double.NaN;   // most recent confirmed value this bar
		private int    tickBufferFirstSign = 0;        // sign of first confirmed value
		private int    tickBufferLastSign = 0;         // sign of most recent confirmed value
		private bool   tickBufferHasConfirmedSignal = false;  // did we get a real signal this bar?
		private int    prevBarFinalSign = 0;           // final confirmed sign from previous bar (for cross detection)
		
		// === TickElectrifier - force faster chart updates when in trade ===
		private long   electrifierLastTF = 0;          // timestamp bucket for throttling
		private bool   electrifierPending = false;     // prevents overlapping async calls
		
		// Tunables (kept conservative)
		private const int   ZeroConfirmFrames = 2;     // require N confirmations to accept a raw 0
		private const int   MaxDeadFrames     = 3;     // self-heal catalog after this many dead frames
		private const double SpikeMultiplier  = 50.0;  // sudden spike invalidation multiplier
		private const int   StaleToleranceBars = 0;    // allow slight mismatch when checking time


		// rolling counters & last-read snapshot
		private int  dbgTick;                 // increments each OnBarUpdate
		private int  dbgLastRowsToDraw;
		private int  dbgSeriesCount;
		private int  dbgIndexUsed;            // which barsAgo we actually read
		private bool dbgIsValid;              // IsValidDataPointAt(idx)
		private double dbgValue;              // value we read
		private string dbgSel;                // selected label
		private string dbgNote;               // last note/warning
		
		// throttle prints so logs are useful but not spammy
		private int  dbgPrintEvery = 25;      // print every N updates when DebugMode

		// Scrolling window for the open dropdown
		private int listStart = 0;           // first visible option index when open
		private const int VisibleRows = 25;   // still render 3 rows; catalog holds all
		
		// Live value cache (read on data thread, shown in OnRender)
		private double selectedLiveValue = double.NaN;
		
				// --- Value box layout
		private float valBoxPad = 6f;
		private float valBoxGap = 8f;     // gap between dropdown and value box
		private TextFormat valTf;         // small font for value box
		private RedrawMode redrawMode = RedrawMode.Throttled;
		// Redraw throttling to limit chart invalidations (helps during replay)
		private readonly TimeSpan redrawThrottle = TimeSpan.FromMilliseconds(250);
		private DateTime nextRedrawTime = DateTime.MinValue;
		
		// Catalog retry machinery
		private DispatcherTimer catalogTimer;
		private int catalogRetries = 0;
		private const int MaxCatalogRetries = 20;   // ~10 seconds total with 500ms interval
		private readonly HashSet<Series<double>> seen = new HashSet<Series<double>>();
		private readonly HashSet<Indicator> seenIndicators = new HashSet<Indicator>();
		
        // -------- Layout
        private float boxX = 20f, boxY = 20f;
        private float boxW = 280f, boxH = 24f;
        private float itemH = 22f;
        private float pad   = 6f;

        [NinjaScriptProperty, Display(Name="Top-Left X", GroupName="Layout", Order=0)]
        public int TLX { get; set; } = 20;

        [NinjaScriptProperty, Display(Name="Top-Left Y", GroupName="Layout", Order=1)]
        public int TLY { get; set; } = 50;

        [NinjaScriptProperty, Display(Name="Width", GroupName="Layout", Order=2)]
        public int WidthPx { get; set; } = 280;

        [NinjaScriptProperty, Display(Name="Redraw Mode", Description="Controls how often the UI refreshes: Full=immediate, Throttled=limited updates, Minimal=only essential updates, Disabled=no visual updates", GroupName="Performance", Order=3)]
        public string RedrawModeStr { get; set; } = "Disabled";

		// --- Send toggle (left of dropdown)
		private bool sendEnabled;                           // runtime gate
		[NinjaScriptProperty, Display(Name="Send Enabled (default)", GroupName="Routing", Order=0)]
		public bool SendEnabledDefault { get; set; } = false;
		
		private SharpDX.RectangleF sendRect;                // clickable area
		private bool hoverSend = false;
		private float sendGap  = 8f;                        // gap between SEND and dropdown
		
		// --- Order mode toggle (next to SEND)
		private SharpDX.RectangleF orderModeRect;
		private bool hoverOrder = false;
		private float orderGap = 6f; // gap between SEND and order-mode button
		
		// --- Destination toggle (next to OrderMode) ---
		private SharpDX.RectangleF destModeRect;
		private bool hoverDest = false;
		private float destGap = 6f;   // gap between order-mode and destination button
		
		// runtime destination mode (derived from the flags)
		private enum DestMode { TickHunter, DDATM, DDTP }
		private DestMode destMode = DestMode.TickHunter;
		
		// Direction filter
		private enum DirFilter { Both, Longs, Shorts }
		private DirFilter dirFilter = DirFilter.Both;   // runtime


        // -------- Runtime State
        private bool isOpen = false;
        private int hoverIndex = -1;
        private bool mouseHooked = false;
        private readonly Dictionary<string, string> ellipsizeCache = new Dictionary<string, string>();
        private float lastEllipsizeWidth = 0f;

        // Device resources
        private DWFactory dwFactory;
        private TextFormat tf;
        private SharpDX.Direct2D1.SolidColorBrush brushBg, brushBorder, brushText, brushHover, brushOpenBg;
		
		// --- Direction filter toggle (next to Destination) ---
		private SharpDX.RectangleF dirFilterRect;
		private bool hoverDirFilter = false;
		private float dirGap = 6f;   // gap between destination and dir-filter
		

		private sealed class PlotOption
		{
		    public string Label;                 // "IndicatorName • PlotName"
		    public IndicatorBase Source;         // the actual indicator
		    public int PlotIndex;                // which Values[] slot
		    public override string ToString() => Label;
		}

        private readonly List<PlotOption> options = new List<PlotOption>();
        private int selectedIndex = -1;

        [Browsable(false)]
        [XmlIgnore]
        public string SelectedLabel => (selectedIndex >= 0 && selectedIndex < options.Count) ? options[selectedIndex].Label : "(no plot selected)";

        // Persisted property to remember last selected plot across sessions
        // Note: NinjaTrader doesn't persist [Browsable(false)] properties, so we use file-based storage
        [Browsable(false)]
        [XmlIgnore]
        public string SavedPlotLabel { get; set; } = "";
        
        // File-based persistence for plot selection (NT doesn't persist hidden properties)
        private string GetSettingsFilePath()
        {
            try
            {
                // Use NT documents folder + indicator name + instrument for uniqueness
                string docsFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string ntFolder = Path.Combine(docsFolder, "NinjaTrader 8", "Indicators", "DDPlotReader");
                if (!Directory.Exists(ntFolder))
                    Directory.CreateDirectory(ntFolder);
                
                // Create a unique filename per chart (instrument + bar type)
                string chartId = "default";
                try
                {
                    if (Instrument != null && BarsArray != null && BarsArray.Length > 0 && BarsArray[0] != null)
                        chartId = $"{Instrument.FullName}_{BarsArray[0].BarsPeriod}".Replace(" ", "_").Replace("/", "-");
                }
                catch { }
                
                return Path.Combine(ntFolder, $"selection_{chartId}.txt");
            }
            catch { return null; }
        }
        
        private void SavePlotSelection(string label)
        {
            try
            {
                string path = GetSettingsFilePath();
                if (string.IsNullOrEmpty(path)) return;
                File.WriteAllText(path, label ?? "");
                Print($"[DDPlotReader] Saved selection to file: '{label}'");
            }
            catch (Exception ex)
            {
                Print($"[DDPlotReader] Failed to save selection: {ex.Message}");
            }
        }
        
        private string LoadPlotSelection()
        {
            try
            {
                string path = GetSettingsFilePath();
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "";
                string label = File.ReadAllText(path).Trim();
                Print($"[DDPlotReader] Loaded selection from file: '{label}'");
                return label;
            }
            catch (Exception ex)
            {
                Print($"[DDPlotReader] Failed to load selection: {ex.Message}");
                return "";
            }
        }

        private Series<double> SelectedMirror;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                     = "DDPlotReader";
                IsOverlay                = true;
                IsSuspendedWhileInactive = false;
                Calculate                = Calculate.OnEachTick;

                // Needs System.Windows.Media.Brushes (WPF)
                AddPlot(Brushes.Transparent, "Selected");
				SendEnabledDefault = false;   // default OFF
				lastVgaFibWasFive = false;
				vgaFibInitialized = false;
				
            }
            else if (State == State.DataLoaded)
            {
                SelectedMirror = Values[0];
                EnsureMouseHook();
				sendEnabled = SendEnabledDefault;  // <- initialize runtime gate
				lastVgaFibWasFive = false;
				vgaFibInitialized = false;
				if (SendToVgaBridge)
				    EnsureVgaFibDefault();
				
				// decide initial destination mode from properties
				if (SendToDDTP && !SendToTickHunter && !SendToDDATM) destMode = DestMode.DDTP;
				else if (SendToDDATM && !SendToTickHunter)            destMode = DestMode.DDATM;
				else                                                  destMode = DestMode.TickHunter;
				
				ApplyDestModeFlags();
				
				// Load saved plot selection from file (NT doesn't persist hidden properties)
				SavedPlotLabel = LoadPlotSelection();
				Print($"[DDPlotReader] DataLoaded: SavedPlotLabel='{SavedPlotLabel}'");
				redrawMode = ParseRedrawMode(RedrawModeStr);
            }
			else if (State == State.Historical || State == State.Realtime)
			{
			    // Existing hooks
			    ChartControl?.Dispatcher.InvokeAsync(BuildCatalogSafe);
			    HookIndicatorCollectionChanged();
			
			    // NEW: rebuild once chart visual tree is loaded
			    try
			    {
			        if (ChartControl != null)
			        {
			            // Avoid multiple subscriptions
			            ChartControl.Loaded -= ChartControl_Loaded;
			            ChartControl.Loaded += ChartControl_Loaded;
			        }
			    } catch { }
			
			    // Start retry loop in case peers aren’t ready yet
			    StartCatalogTimer();
			}
			
			else if (State == State.Terminated || State == State.Finalized)
			{
			    try { if (ChartControl != null) ChartControl.Loaded -= ChartControl_Loaded; } catch { }
			    try
			    {
			        var p = ChartControl?.GetType().GetProperty("Indicators",
			            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			        if (p?.GetValue(ChartControl) is INotifyCollectionChanged list)
			            list.CollectionChanged -= IndicatorList_CollectionChanged;
			    } catch { }
			
			    UnhookMouse();
			    StopCatalogTimer();
			    DisposeDeviceResources();
				lastVgaFibWasFive = false;
				vgaFibInitialized = false;
			}


        }
		
		
		private bool PassesDirectionFilter(int dir)
		{
		    if (dir == 0) return false;
		    if (dirFilter == DirFilter.Longs  && dir < 0) return false;
		    if (dirFilter == DirFilter.Shorts && dir > 0) return false;
		    return true; // Both, or matching side
		}

		private RedrawMode ParseRedrawMode(string val)
		{
		    if (string.IsNullOrWhiteSpace(val)) return RedrawMode.Throttled;
		    var v = val.Trim();
		    if (v.Equals("Full", StringComparison.OrdinalIgnoreCase)) return RedrawMode.Full;
		    if (v.Equals("Throttled", StringComparison.OrdinalIgnoreCase)) return RedrawMode.Throttled;
		    if (v.Equals("Minimal", StringComparison.OrdinalIgnoreCase)) return RedrawMode.Minimal;
		    if (v.Equals("Disabled", StringComparison.OrdinalIgnoreCase)) return RedrawMode.Disabled;
		    return RedrawMode.Throttled;
		}

		
		private void ApplyDestModeFlags()
		{
		    if (destMode == DestMode.DDATM)
		    {
		        SendToDDATM       = true;
		        SendToTickHunter  = false;
		        SendToDDTP        = false;
		        TryActivateTickHunter(false);
		    }
		    else if (destMode == DestMode.DDTP)
		    {
		        SendToDDTP        = true;
		        SendToTickHunter  = false;
		        SendToDDATM       = false;
		        TryActivateTickHunter(false);
		    }
		    else // TickHunter
		    {
		        SendToTickHunter  = true;
		        SendToDDATM       = false;
		        SendToDDTP        = false;
		        TryActivateTickHunter(true);
		    }
		}
		
		private bool DDTPEndpointExists()
		{
		    try
		    {
		        var t = ResolveTypeAnyAssembly("NinjaTrader.NinjaScript.Indicators.DDTPBridge");
		        var mi = t?.GetMethod("Exists", BindingFlags.Public | BindingFlags.Static);
		        return mi != null && (bool)mi.Invoke(null, new object[] { DDTPEndpoint });
		    } catch { return false; }
		}
		

		// Entry: map +1/-1 to BuyUp / SellDown (DDTP conditional API)
		private bool TrySendDDTPEntry(int sign, string reason = "DDPlotReader")
		{
		    try
		    {
		        if (!SendToDDTP || string.IsNullOrWhiteSpace(DDTPEndpoint) || sign == 0) return false;
		
		        // Extra guard + helpful debug
		        if (!DDTPEndpointExists())
		        {
		            if (DebugMode) Dbg($"[DDPR] DDTP endpoint '{DDTPEndpoint}' not found (Exists=false) – not sending.");
		            return false;
		        }
		
		        var bridgeType = ResolveTypeAnyAssembly("NinjaTrader.NinjaScript.Indicators.DDTPBridge");
		        var cmdType    = ResolveTypeAnyAssembly("NinjaTrader.NinjaScript.Indicators.DimDim.DDTPCommand");
		        if (bridgeType == null || cmdType == null)
		        {
		            if (DebugMode) Dbg("[DDPR] DDTPBridge/DDTPCommand types not found – not sending.");
		            return false;
		        }
		
		        // ✅ NEW mapping:
		        var cmd = Enum.Parse(cmdType, sign > 0 ? "BuyUp" : "SellDown", ignoreCase: false);
		
		        // Prefer ACK (sync) if present, otherwise Send
		        var sendAck = bridgeType.GetMethod("SendAck", BindingFlags.Public | BindingFlags.Static)
		                  ?? bridgeType.GetMethod("Send",    BindingFlags.Public | BindingFlags.Static);
		        if (sendAck == null)
		        {
		            if (DebugMode) Dbg("[DDPR] DDTPBridge.Send/SendAck not found – not sending.");
		            return false;
		        }
		
		        bool ok = (bool)sendAck.Invoke(null, new object[] { DDTPEndpoint, cmd, reason, null });
		        if (DebugMode) Dbg($"[DDPR] DDTP {(sign > 0 ? "BuyUp" : "SellDown")} sent={ok} ep='{DDTPEndpoint}'");
		        return ok;
		    }
		    catch (Exception ex)
		    {
		        if (DebugMode) Dbg("[DDPR] TrySendDDTPEntry error: " + ex.Message);
		        return false;
		    }
		}


		
		// Optional: "close" for DDTP → use Cancel (last working)
		private bool TrySendDDTPCancel(string reason = "DDPlotReader")
		{
		    try
		    {
		        if (!SendToDDTP || !DDTPEndpointExists()) return false;
		
		        var bridgeType = ResolveTypeAnyAssembly("NinjaTrader.NinjaScript.Indicators.DDTPBridge");
		        var cmdType    = ResolveTypeAnyAssembly("NinjaTrader.NinjaScript.Indicators.DimDim.DDTPCommand");
		        if (bridgeType == null || cmdType == null) return false;
		
		        var cmd     = Enum.Parse(cmdType, "Cancel", ignoreCase:false);
		        var sendAck = bridgeType.GetMethod("SendAck", BindingFlags.Public | BindingFlags.Static)
		                  ?? bridgeType.GetMethod("Send",    BindingFlags.Public | BindingFlags.Static);
		        if (sendAck == null) return false;
		
		        bool ok = (bool)sendAck.Invoke(null, new object[] { DDTPEndpoint, cmd, reason, null });
		        if (DebugMode) Dbg($"[DDPR] DDTP Cancel sent={ok} ep='{DDTPEndpoint}'");
		        return ok;
		    }
		    catch { return false; }
		}


		
		// DDPlotReader.cs — single attempt; no retry; calls the API via the Bridge.
		private void TryActivateTickHunter(bool active)
		{
		    try
		    {
		        // Resolve: NinjaTrader.NinjaScript.Indicators.TickHunterBridge
		        var bridgeType = Type.GetType("NinjaTrader.NinjaScript.Indicators.TickHunterBridge, NinjaTrader.Core", false)
		                        ?? Type.GetType("NinjaTrader.NinjaScript.Indicators.TickHunterBridge", false);
		        if (bridgeType == null || string.IsNullOrWhiteSpace(BridgeEndpoint))
		            return;
		
		        // Calls into Bridge.SetActive(endpoint, active) which dispatches to UI
		        var setActiveMi = bridgeType.GetMethod("SetActive", BindingFlags.Public | BindingFlags.Static);
		        if (setActiveMi != null)
		            setActiveMi.Invoke(null, new object[] { BridgeEndpoint, active });
		    }
		    catch
		    {
		        // non-fatal; older builds may not have SetActive
		    }
		}

		
		private bool BridgeEndpointExists()
		{
		    try
		    {
		        var bridgeType = Type.GetType("NinjaTrader.NinjaScript.Indicators.TickHunterBridge, NinjaTrader.Core", false)
		                         ?? Type.GetType("NinjaTrader.NinjaScript.Indicators.TickHunterBridge", false);
		        if (bridgeType == null) return false;
		        var existsMi = bridgeType.GetMethod("Exists", BindingFlags.Public | BindingFlags.Static);
		        if (existsMi == null) return false;
		        return (bool)existsMi.Invoke(null, new object[] { BridgeEndpoint });
		    }
		    catch { return false; }
		}

		
		private bool TrySendTickHunterClose(string reason = "DDPlotReader")
		{
		    try {
		        var bridgeType = Type.GetType("NinjaTrader.NinjaScript.Indicators.TickHunterBridge, NinjaTrader.Core", false)
		                         ?? Type.GetType("NinjaTrader.NinjaScript.Indicators.TickHunterBridge", false);
		        var thCmdType  = Type.GetType("NinjaTrader.NinjaScript.Indicators.THCommand, NinjaTrader.Core", false)
		                      ?? Type.GetType("NinjaTrader.NinjaScript.Indicators.THCommand", false);
		        if (bridgeType == null || thCmdType == null) return false;
		        var cmdClose = Enum.Parse(thCmdType, "Close", ignoreCase:false);
		        var sendMi   = bridgeType.GetMethod("Send", BindingFlags.Public | BindingFlags.Static);
		        var disp     = ChartControl?.Dispatcher;
		        if (sendMi == null || disp == null) return false;
		        disp.InvokeAsync(() => { try { sendMi.Invoke(null, new object[]{ BridgeEndpoint, cmdClose, reason }); } catch { } });
		        return true;
		    } catch { return false; }
		}

		
		// Sends BuyPop for sign>0, SellPop for sign<0 via TickHunterBridge.Send(...)
		// Uses reflection so DDPlotReader does NOT require compiling against the Bridge assembly.
		private bool TrySendTickHunter(int sign, string reason = "DDPlotReader")
		{
		    try
		    {
		        if (!SendToTickHunter) return false;
		        if (string.IsNullOrWhiteSpace(BridgeEndpoint)) return false;
		        if (sign == 0) return false;
		
		        // Resolve type: NinjaTrader.NinjaScript.Indicators.TickHunterBridge
		        var bridgeType = Type.GetType("NinjaTrader.NinjaScript.Indicators.TickHunterBridge, NinjaTrader.Core", false)
		                         ?? Type.GetType("NinjaTrader.NinjaScript.Indicators.TickHunterBridge", false);
		        if (bridgeType == null) { Dbg("Bridge type not found"); return false; }
		
		        // Resolve enum: NinjaTrader.NinjaScript.Indicators.THCommand
		        var thCmdType = Type.GetType("NinjaTrader.NinjaScript.Indicators.THCommand, NinjaTrader.Core", false)
		                     ?? Type.GetType("NinjaTrader.NinjaScript.Indicators.THCommand", false);
		        if (thCmdType == null) { Dbg("THCommand enum not found"); return false; }
		
				// map EntryMode + sign -> command name (no probing)
				string cmdName;
				switch (EntryMode)
				{
				    case THEntryStyle.Market:
				        cmdName = sign > 0 ? "BuyMarket" : "SellMarket";
				        break;
				
				    case THEntryStyle.Pop:
				        cmdName = sign > 0 ? "BuyPop" : "SellPop";
				        break;
				
				    case THEntryStyle.Drop:
				    default:
				        cmdName = sign > 0 ? "BuyDrop" : "SellDrop";
				        break;
				}
				
				var cmdValue = Enum.Parse(thCmdType, cmdName, ignoreCase: false);

		
		        // static bool Send(string endpointName, THCommand cmd, string reason = "API")
		        var sendMi = bridgeType.GetMethod("Send",
		                        BindingFlags.Public | BindingFlags.Static);
		        if (sendMi == null) { Dbg("Bridge.Send not found"); return false; }
		
		        // Execute on UI thread (like a real button press)
		        var disp = ChartControl?.Dispatcher;
		        if (disp == null) return false;
		
		        disp.InvokeAsync(() =>
		        {
		            try
		            {
		                bool ok = (bool)sendMi.Invoke(null, new object[] { BridgeEndpoint, cmdValue, reason });
		                if (DebugMode) Dbg($"TickHunter {cmdName} sent to '{BridgeEndpoint}' ok={ok}");
		            }
		            catch (Exception ex)
		            {
		                if (DebugMode) Dbg("Bridge.Send invoke failed: " + ex.Message);
		            }
		        });
		
		        return true;
		    }
		    catch (Exception ex)
		    {
		        if (DebugMode) Dbg("TrySendTickHunter error: " + ex.Message);
		        return false;
		    }
		}

		private bool VgaBridgeEndpointExists()
		{
		    if (string.IsNullOrWhiteSpace(VgaBridgeEndpoint))
		        return false;
		    try { return vgaBridge.Exists(VgaBridgeEndpoint); }
		    catch { return false; }
		}

		private bool TrySendVgaFib(bool useFib5, string reason)
		{
		    if (!SendToVgaBridge)
		        return false;
		    if (string.IsNullOrWhiteSpace(VgaBridgeEndpoint))
		        return false;
		    if (!VgaBridgeEndpointExists())
		        return false;
		    var cmd = useFib5 ? vgaPadCommand.ActivateFib5 : vgaPadCommand.ActivateFib2;
		    return vgaBridge.Send(VgaBridgeEndpoint, cmd, reason);
		}

		private void EnsureVgaFibDefault()
		{
		    if (!SendToVgaBridge)
		        return;
		    if (vgaFibInitialized)
		        return;
		    if (TrySendVgaFib(false, "DDPlotReader default Fib2"))
		    {
		        lastVgaFibWasFive = false;
		        vgaFibInitialized = true;
		    }
		}

		private void UpdateVgaFibFromSignal(int currSign)
		{
		    if (!SendToVgaBridge)
		        return;

		    EnsureVgaFibDefault();

		    bool wantFib5 = currSign != 0;
		    if (vgaFibInitialized && lastVgaFibWasFive == wantFib5)
		        return;

		    string reason = wantFib5
		        ? $"DDPlotReader signal {currSign:+#;-#;0}"
		        : "DDPlotReader signal 0";

		    if (TrySendVgaFib(wantFib5, reason))
		    {
		        lastVgaFibWasFive = wantFib5;
		        vgaFibInitialized = true;
		    }
		}
		
		private static Type ResolveTypeAnyAssembly(string nameOrQualified)
		{
		    if (string.IsNullOrWhiteSpace(nameOrQualified))
		        return null;
		
		    // 0) If they gave "Ns.Type, Some.Assembly", also compute the short "Ns.Type"
		    string shortName = nameOrQualified;
		    int comma = nameOrQualified.IndexOf(',');
		    if (comma > 0)
		        shortName = nameOrQualified.Substring(0, comma).Trim();
		
		    // 1) Try as provided (works if assembly-qualified and that assembly is known)
		    var t = Type.GetType(nameOrQualified, throwOnError: false, ignoreCase: false);
		    if (t != null) return t;
		
		    // 2) Try the short full name with Type.GetType
		    t = Type.GetType(shortName, throwOnError: false, ignoreCase: false);
		    if (t != null) return t;
		
		    // 3) Scan all loaded assemblies, short name first (most likely to match)
		    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
		    {
		        try
		        {
		            t = asm.GetType(shortName, throwOnError: false, ignoreCase: false)
		             ?? asm.GetType(nameOrQualified, throwOnError: false, ignoreCase: false);
		            if (t != null) return t;
		        }
		        catch { /* ignore */ }
		    }
		
		    return null;
		}


		
		private bool DDATMEndpointExists()
		{
		    try
		    {
		        var t  = ResolveTypeAnyAssembly("NinjaTrader.NinjaScript.Strategies.DDATMBridge");
		        if (t == null) { if (DebugMode) Dbg("[DDPR] DDATMBridge type not found"); return false; }
		        var mi = t.GetMethod("Exists", BindingFlags.Public | BindingFlags.Static);
		        if (mi == null) { if (DebugMode) Dbg("[DDPR] DDATMBridge.Exists not found"); return false; }
		        bool ok = (bool)mi.Invoke(null, new object[] { DDATMEndpoint });
		        if (DebugMode) Dbg($"[DDPR] DDATMBridge.Exists('{DDATMEndpoint}') → {ok}");
		        return ok;
		    }
		    catch (Exception ex) { if (DebugMode) Dbg("[DDPR] Exists error: " + ex.Message); return false; }
		}

		
		private bool TrySendDDATMEntry(int sign, string reason = "DDPlotReader")
		{
		    try
		    {
		        if (!SendToDDATM || string.IsNullOrWhiteSpace(DDATMEndpoint) || sign == 0) return false;
		
		        var bridgeType = ResolveTypeAnyAssembly("NinjaTrader.NinjaScript.Strategies.DDATMBridge");
		        if (bridgeType == null) { if (DebugMode) Dbg("[DDPR] DDATMBridge type not found"); return false; }
		
		        var ddCmdType  = ResolveTypeAnyAssembly("NinjaTrader.NinjaScript.Strategies.DDATM+DDCommand");
		        if (ddCmdType == null) { if (DebugMode) Dbg("[DDPR] DDATM.DDCommand enum not found"); return false; }
		
		        var cmd        = Enum.Parse(ddCmdType, sign > 0 ? "Long" : "Short", ignoreCase: false);
		        var sendAckMi  = bridgeType.GetMethod("SendAck", BindingFlags.Public | BindingFlags.Static);
		        var sendMi     = bridgeType.GetMethod("Send",    BindingFlags.Public | BindingFlags.Static);
		
		        if (sendAckMi != null)
		        {
		            bool ack = (bool)sendAckMi.Invoke(null, new object[] { DDATMEndpoint, cmd, reason, null });
		            if (DebugMode) Dbg($"[DDPR] DDATM {(sign>0?"Long":"Short")} ACK={ack} ep='{DDATMEndpoint}'");
		            return ack;
		        }
		        if (sendMi == null) { if (DebugMode) Dbg("[DDPR] DDATMBridge.Send not found"); return false; }
		
		        bool ok = (bool)sendMi.Invoke(null, new object[] { DDATMEndpoint, cmd, reason, null });
		        if (DebugMode) Dbg($"[DDPR] DDATM {(sign>0?"Long":"Short")} sent={ok} ep='{DDATMEndpoint}'");
		        return ok;
		    }
		    catch (Exception ex) { if (DebugMode) Dbg("[DDPR] TrySendDDATMEntry error: " + ex.Message); return false; }
		}


		
		private bool TrySendDDATMClose(string reason = "DDPlotReader")
		{
		    try
		    {
		        if (!SendToDDATM) return false;
		        var bridgeType = ResolveTypeAnyAssembly("NinjaTrader.NinjaScript.Strategies.DDATMBridge");
		        var ddCmdType  = ResolveTypeAnyAssembly("NinjaTrader.NinjaScript.Strategies.DDATM+DDCommand");
		        if (bridgeType == null || ddCmdType == null) { if (DebugMode) Dbg("[DDPR] DDATMBridge/Command missing"); return false; }
		
		        var cmd   = Enum.Parse(ddCmdType, "Close", ignoreCase: false);
		        var send  = bridgeType.GetMethod("Send", BindingFlags.Public | BindingFlags.Static);
		        if (send == null) { if (DebugMode) Dbg("[DDPR] DDATMBridge.Send not found"); return false; }
		
		        bool ok = (bool)send.Invoke(null, new object[] { DDATMEndpoint, cmd, reason, null });
		        if (DebugMode) Dbg($"[DDPR] DDATM Close sent={ok} ep='{DDATMEndpoint}'");
		        return ok;
		    }
		    catch (Exception ex) { if (DebugMode) Dbg("[DDPR] TrySendDDATMClose error: " + ex.Message); return false; }
		}
		

		

		
		// Query "flat" from the bridge (uses reflection so this compiles even if TryQueryIsFlat
		// doesn't exist yet). If the query isn't available, we treat it as NOT flat unless we
		// have no active side locally.
		private bool IsFlatViaBridge()
		{
		    switch (lastDest)
		    {
				
				case Dest.DDTP:
    			try
    			{
    			    var t  = ResolveTypeAnyAssembly("NinjaTrader.NinjaScript.Indicators.DDTPBridge");
    			    var mi = t?.GetMethod("TryQueryIsFlat", BindingFlags.Public | BindingFlags.Static);
    			    if (mi != null)
    			    {
    			        object[] args = new object[] { DDTPEndpoint, false };
    			        bool ok = (bool)mi.Invoke(null, args);
    			        if (ok) return (bool)args[1];
    			    }
    			} catch { }
    			return true; // if no query, assume flat to avoid deadlocks

	
		        case Dest.TickHunter:
		            try
		            {
		                var bridgeType = Type.GetType("NinjaTrader.NinjaScript.Indicators.TickHunterBridge, NinjaTrader.Core", false)
		                                 ?? Type.GetType("NinjaTrader.NinjaScript.Indicators.TickHunterBridge", false);
		                var mi = bridgeType?.GetMethod("TryQueryIsFlat", BindingFlags.Public | BindingFlags.Static);
		                if (mi != null)
		                {
		                    object[] args = new object[] { BridgeEndpoint, false };
		                    bool ok = (bool)mi.Invoke(null, args);
		                    if (ok) return (bool)args[1];
		                }
		            } catch { }
		            // fallback for TH if query not available
		            return lastSentSign == 0;
		
		        case Dest.DDATM:
		            if (UseDDATMFlatCheck)     // consider defaulting this to true
		            {
		                try
		                {
		                    var t  = ResolveTypeAnyAssembly("NinjaTrader.NinjaScript.Strategies.DDATMBridge");
		                    var mi = t?.GetMethod("TryQueryIsAtmFlat", BindingFlags.Public | BindingFlags.Static);
		                    if (mi != null)
		                    {
		                        object[] args = new object[] { DDATMEndpoint, false };
		                        bool ok = (bool)mi.Invoke(null, args);
		                        if (ok) return (bool)args[1];
		                    }
		                } catch { }
		                // if we asked and can't query, assume flat to avoid deadlock
		                return true;
		            }
		            // if not using a query, we can only use the local latch
		            return lastSentSign == 0;
		
		        default:
		            // No prior destination (nothing “in trade”)
		            return true;
		    }
		}


		
		private void ResetAllTrackers(string reason)
		{
		    lastStableValue = double.NaN;
		    lastSentOnBar   = -1;
		    lastTriggerBar  = -1;
		    lastSentSign    = 0;
		    pendingSign     = 0; pendingReason = null; pendingSinceBar = -1;
		    thState         = TradeState.Flat;
		    lastDest        = Dest.None;            // <-- add this
		    tradeConfirmedBar = -1;
		    tradeWasConfirmed = false;
		    ClearPendingEntry();
		    if (DebugMode) Dbg($"[DDPR] RESET ({reason}) → state=Flat");
		}

		
		private bool AnyEndpointExists()
		{
		    bool th = SendToTickHunter && BridgeEndpointExists();
		    bool da = SendToDDATM      && DDATMEndpointExists();
		    bool tp = SendToDDTP       && DDTPEndpointExists();   // ← add this
		    return th || da || tp;                                // ← include tp
		}
		
		private bool TryEnterToDestination(int dir, string reason)
		{
		    if (thState != TradeState.Flat) return false;
		    if (!AnyEndpointExists())       return false;
			if (!sendEnabled) return false;       // <- SEND gate
			if (!PassesDirectionFilter(dir)) return false;
			if (!ApiCallAllowed())
			{
			    if (DebugMode) Dbg("[DDPR] ENTRY skipped: API already sent this bar");
			    return false;
			}

		
		    // // Choose destination deterministically (adjust order if you want DDATM-first)
		    // if (SendToTickHunter && BridgeEndpointExists())
		    // {
		    //     fired = TrySendTickHunter(dir, reason);
		    //     if (fired) destFired = Dest.TickHunter;
		    // }
		    // else if (SendToDDATM && DDATMEndpointExists())
		    // {
		    //     fired = TrySendDDATMEntry(dir, reason);
		    //     if (fired) destFired = Dest.DDATM;
		    // }
			
			bool fired = false;
			Dest destFired = Dest.None;
			
			if (destMode == DestMode.TickHunter)
			{
			    if (SendToTickHunter && BridgeEndpointExists())
			    {
			        fired = TrySendTickHunter(dir, reason);
			        if (fired) destFired = Dest.TickHunter;          // ← add
			    }
			    if (!fired && SendToDDATM && DDATMEndpointExists())
			    {
			        fired = TrySendDDATMEntry(dir, reason);
			        if (fired) destFired = Dest.DDATM;               // ← add
			    }
			    if (!fired && SendToDDTP && DDTPEndpointExists())
			    {
			        fired = TrySendDDTPEntry(dir, reason);
			        if (fired) destFired = Dest.DDTP; /* or Dest.DDTP if you add it to the enum above */ // ← pick one
			    }
			}
			else if (destMode == DestMode.DDATM)
			{
			    if (SendToDDATM && DDATMEndpointExists())
			    {
			        fired = TrySendDDATMEntry(dir, reason);
			        if (fired) destFired = Dest.DDATM;               // ← add
			    }
			    if (!fired && SendToTickHunter && BridgeEndpointExists())
			    {
			        fired = TrySendTickHunter(dir, reason);
			        if (fired) destFired = Dest.TickHunter;          // ← add
			    }
			    if (!fired && SendToDDTP && DDTPEndpointExists())
			    {
			        fired = TrySendDDTPEntry(dir, reason);
			        if (fired) destFired = Dest.DDTP; /* or Dest.DDTP */       // ← pick one
			    }
			}
			else // DDTP selected
			{
			    if (SendToDDTP && DDTPEndpointExists())
			    {
			        fired = TrySendDDTPEntry(dir, reason);
			        if (fired) destFired = Dest.DDTP; /* or Dest.DDTP */       // ← add
			    }
			    if (!fired && SendToDDATM && DDATMEndpointExists())
			    {
			        fired = TrySendDDATMEntry(dir, reason);
			        if (fired) destFired = Dest.DDATM;               // ← add
			    }
			    if (!fired && SendToTickHunter && BridgeEndpointExists())
			    {
			        fired = TrySendTickHunter(dir, reason);
			        if (fired) destFired = Dest.TickHunter;          // ← add
			    }
			}
			
			if (fired)
			{
			    thState        = TradeState.InTrade;
			    lastPopBar     = CurrentBar;
			    lastSentSign   = dir;
			    lastSentOnBar  = CurrentBar;
			    lastTriggerBar = CurrentBar;
			    lastDest       = destFired;                           // now actually has a value
			    MarkApiCall();
			    pendingEntryActive = true;
			    pendingEntryBar = GetPrimaryBarIndex();
			    if (DebugMode) Dbg($"[DDPR] ENTRY → state=InTrade dest={lastDest} dir={(dir>0?"+1":"-1")}");
			}

		    return fired;
		}

		
		// Request a "close/flat" at the chosen destination (with sensible fallbacks).
		// Returns true if any destination accepted the close request.
		private bool TryRequestCloseToDestination(string reason, bool force = false)
		{
		    try
		    {
		        // Gate: SEND toggle off = do nothing
		        if (!sendEnabled && !force)
		        {
		            if (DebugMode) Dbg("[DDPR] CLOSE skipped: sendEnabled=false");
		            return false;
		        }
		        if (!ApiCallAllowed())
		        {
		            if (!force)
		            {
		                if (DebugMode) Dbg("[DDPR] CLOSE skipped: API already sent this bar");
		                return false;
		            }
		        }
		
		        bool fired = false;
		
		        // Prefer closing where we last entered, if known.
		        // Otherwise, try the current destMode chosen by the user with fallbacks.
		        Dest preferred = lastDest;
		
		        // If we never recorded a dest (e.g., bootstrap or reload), pick based on current mode/flags.
		        if (preferred == Dest.None)
		        {
		            // Try selected destination first, then fallbacks based on availability
		            if (destMode == DestMode.TickHunter)
		                preferred = (SendToTickHunter && BridgeEndpointExists()) ? Dest.TickHunter
		                           : (SendToDDATM      && DDATMEndpointExists())  ? Dest.DDATM
		                           : (SendToDDTP       && DDTPEndpointExists())    ? Dest.DDTP
		                           : Dest.None;
		            else if (destMode == DestMode.DDATM)
		                preferred = (SendToDDATM      && DDATMEndpointExists())  ? Dest.DDATM
		                           : (SendToTickHunter && BridgeEndpointExists()) ? Dest.TickHunter
		                           : (SendToDDTP       && DDTPEndpointExists())   ? Dest.DDTP
		                           : Dest.None;
		            else // DDTP selected
		                preferred = (SendToDDTP       && DDTPEndpointExists())   ? Dest.DDTP
		                           : (SendToDDATM      && DDATMEndpointExists())  ? Dest.DDATM
		                           : (SendToTickHunter && BridgeEndpointExists()) ? Dest.TickHunter
		                           : Dest.None;
		        }
		
		        // 1) Try preferred
		        fired = CloseOn(preferred, reason);
		
		        // 2) If that failed, try remaining fallbacks (in a stable order)
		        if (!fired)
		        {
		            // Build an ordered list of other destinations to try
		            var fallbacks = new System.Collections.Generic.List<Dest>(3);
		            if (preferred != Dest.TickHunter) fallbacks.Add(Dest.TickHunter);
		            if (preferred != Dest.DDATM)      fallbacks.Add(Dest.DDATM);
		            if (preferred != Dest.DDTP)       fallbacks.Add(Dest.DDTP);
		
		            foreach (var d in fallbacks)
		            {
		                if (CloseOn(d, reason)) { fired = true; break; }
		            }
		        }
		
		        if (fired)
		        {
		            // Reset local state after a successful close request
		            thState        = TradeState.Flat;
		            lastSentSign   = 0;
		            lastDest       = Dest.None;
		            lastTriggerBar = CurrentBar;
		            lastSentOnBar  = CurrentBar;
		            MarkApiCall();
		            ClearPendingEntry();
		            if (DebugMode) Dbg("[DDPR] CLOSE → state=Flat (request sent)");
		        }
		
		        // Redraw UI for button states
		        RequestRedraw();
		        return fired;
		    }
		    catch (Exception ex)
		    {
		        if (DebugMode) Dbg("[DDPR] CLOSE exception: " + ex.Message);
		        return false;
		    }
		}
		
		// Helper: send close to a specific destination if available
		private bool CloseOn(Dest d, string reason)
		{
		    switch (d)
		    {
		        case Dest.TickHunter:
		            if (SendToTickHunter && BridgeEndpointExists())
		                return TrySendTickHunterClose(reason);
		            return false;
		
		        case Dest.DDATM:
		            if (SendToDDATM && DDATMEndpointExists())
		                return TrySendDDATMClose(reason);
		            return false;
		
		        case Dest.DDTP:
		            if (SendToDDTP && DDTPEndpointExists())
		                return TrySendDDTPCancel(reason); // DDTP close = Cancel
		            return false;
		
		        case Dest.None:
		        default:
		            return false;
		    }
		}




		
		private void ChartControl_Loaded(object sender, RoutedEventArgs e)
		{
		    // On load, try immediately and ensure retry loop is running
		    BuildCatalogSafe();
		    EnsureCatalogScheduled();
		}
		
		private bool CatalogHasRealPlots()
		{
		    for (int i = 0; i < options.Count; i++)
		        if (options[i] != null && options[i].Source != null)
		            return true;
		    return false;
		}
		
		private void StartCatalogTimer()
		{
		    StopCatalogTimer();
		    catalogRetries = 0;
		    catalogTimer = new DispatcherTimer(DispatcherPriority.Background)
		    {
		        Interval = TimeSpan.FromMilliseconds(500)
		    };
		    catalogTimer.Tick += (s, e) =>
		    {
		        BuildCatalogSafe();
		        catalogRetries++;
		        if (CatalogHasRealPlots() || catalogRetries >= MaxCatalogRetries)
		            StopCatalogTimer();
		    };
		    try { ChartControl?.Dispatcher?.InvokeAsync(() => catalogTimer.Start()); } catch { }
		}
		
		private void StopCatalogTimer()
		{
		    try { if (catalogTimer != null) { catalogTimer.Stop(); catalogTimer = null; } } catch { }
		}
		
		private void EnsureCatalogScheduled()
		{
		    if (!CatalogHasRealPlots() && catalogTimer == null)
		        StartCatalogTimer();
		}
		
		private void Dbg(string msg)
		{
		    if (!DebugMode) return;
		    try { Print($"[DropPlots/DBG] {msg}"); } catch { }
		}
		
		
		// Fallback: try direct indexer by converting absolute index -> barsAgo
		// Returns true if we could get a non-NaN finite number.
		private static bool TryDirectSeriesFallback(Series<double> s, int absIndex, out double val)
		{
		    val = 0.0;
		    try
		    {
		        if (s == null) return false;
		        int n = s.Count;
		        if (n <= 0 || absIndex < 0 || absIndex >= n) return false;
		        int barsAgo = (n - 1) - absIndex;
		        if (barsAgo < 0) return false;
		
		        double v = s[barsAgo]; // indexer path used by some vendors
		        if (double.IsNaN(v) || double.IsInfinity(v)) return false;
		        val = v;
		        return true;
		    }
		    catch { return false; }
		}
		
		
		private static void ForceIndicatorUpdate(IndicatorBase src)
		{
		    if (src == null)
		        return;
		
		    forceUpdateStack = forceUpdateStack ?? new HashSet<int>();
		    int id = ObjId(src);
		    if (!forceUpdateStack.Add(id))
		        return;
		
		    try
		    {
		        MethodInfo mi = null;
		        try
		        {
		            mi = src.GetType().GetMethod("Update",
		                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
		                null, Type.EmptyTypes, null);
		        }
		        catch { }
		
		        if (mi != null && mi.GetParameters().Length == 0)
		        {
		            try { mi.Invoke(src, null); } catch { }
		        }
		    }
		    catch { }
		    finally
		    {
		        forceUpdateStack.Remove(id);
		    }
		}
		
		// Try to read a bar time for snapshot-age check.
		// Best-effort only; swallow if src has no matching bars for that absolute index.
		private static bool TryGetSeriesTime(IndicatorBase src, int absIndex, out DateTime t)
		{
		    t = DateTime.MinValue;
		    try
		    {
		        var barsArray = src?.BarsArray;
		        if (barsArray == null || barsArray.Length == 0) return false;
		
		        // Prefer the primary series of the indicator’s own context
		        var bars = barsArray[0];
		        if (bars == null) return false;
		        if (absIndex < 0 || absIndex >= bars.Count) return false;
		
		        t = bars.GetTime(absIndex);
		        return true;
		    }
		    catch { return false; }
		}
		
		// Is the candidate time "fresh enough" with respect to our chart’s current bar?
		private bool IsFreshSnapshot(DateTime candidate, int /*absIndex*/ _)
		{
		    try
		    {
		        // Compare against our primary chart current bar time
		        DateTime chartCurr = (Times != null && Times[0] != null && CurrentBar >= 0) ? Times[0][0] : DateTime.MinValue;
		        if (chartCurr == DateTime.MinValue || candidate == DateTime.MinValue)
		            return true; // if we can’t verify, don’t block
		
		        // Accept only if candidate time <= current bar time (not from the future)
		        // and not older than the previous bar time if you want to be strict:
		        // DateTime prev = (CurrentBar >= 1) ? Times[0][1] : chartCurr;
		        // return candidate <= chartCurr && candidate >= prev;
		
		        return candidate <= chartCurr; // simple, strict enough for closed/live logic
		    }
		    catch { return true; }
		}



		// Safe fetch of a plot's Series<double> from an IndicatorBase
		private Series<double> SafeGetSeries(IndicatorBase src, int plotIndex)
		{
		    try
		    {
		        var vals = src?.Values;
		        if (vals != null && plotIndex >= 0 && plotIndex < vals.Length)
		            return vals[plotIndex];
		    }
		    catch { }
		    return null;
		}
		
		// Safe read at an absolute bars index (0..Count-1, where Count-1 is most recent)
		private static bool SafeTryGet(Series<double> s, int absIndex, out double val)
		{
		    val = 0.0;
		    if (s == null) return false;
		    int n = s.Count;
		    if (n <= 0 || absIndex < 0 || absIndex >= n) return false;
		
		    try
		    {
		        bool valid = true;
		        // Not all Series implement this; swallow if missing
		        try { valid = s.IsValidDataPoint(absIndex); } catch { /* ignore */ }
		        if (!valid) return false;
		
		        double v = s.GetValueAt(absIndex);
		        if (double.IsNaN(v) || double.IsInfinity(v)) return false;
		
		        val = v;
		        return true;
		    }
		    catch { return false; }
		}
		
		// Compare helpers (centralized tolerance)
		private static bool Eq(double a, double b) => MathExtentions.ApproxCompare(a, b) == 0;
		private static bool Ne(double a, double b) => MathExtentions.ApproxCompare(a, b) != 0;

		private static bool ApproxZero(double value) => MathExtentions.ApproxCompare(value, 0.0) == 0;
		private static bool ApproxPositive(double value) => MathExtentions.ApproxCompare(value, 0.0) > 0;
		private static bool ApproxNegative(double value) => MathExtentions.ApproxCompare(value, 0.0) < 0;
		
		// Cross detection against a scalar threshold (prev <= thr && curr > thr, etc.)
		private static bool CrossesAbove(double prev, double curr, double thr)
		    => prev <= thr && curr > thr;
		
		private static bool CrossesBelow(double prev, double curr, double thr)
		    => prev >= thr && curr < thr;
		
		// Historical scan on one plot series (no vendor internals, no instantiation)
		private static void FindMatches(
		    IndicatorBase indi,
		    int plotIndex,
		    double threshold,
		    List<int> outIndices,
		    bool isEq       = false,
		    bool isGt       = false,
		    bool isGe       = false,
		    bool isLt       = false,
		    bool isLe       = false,
		    bool isNe       = false,
		    bool crossAbove = false,
		    bool crossBelow = false)
		{
		    if (indi == null || indi.Values == null || outIndices == null) return;
		    var vals = indi.Values;
		    if (plotIndex < 0 || plotIndex >= vals.Length) return;
		
		    var s = vals[plotIndex];
		    if (s == null) return;
		
		    int n = s.Count;
		    if (n <= 0) return;
		
		    // bars index: 0..n-1 (n-1 == most recent)
		    for (int i = 0; i < n; i++)
		    {
		        // Only act on primary BIP=0 in callers, to mirror your existing semantics
		        if (!SafeTryGet(s, i, out double cur)) continue;
		
		        bool match = false;
		
		        if (isEq && Eq(cur, threshold)) match = true;
		        else if (isGt && cur > threshold) match = true;
		        else if (isGe && cur >= threshold) match = true;
		        else if (isLt && cur < threshold) match = true;
		        else if (isLe && cur <= threshold) match = true;
		        else if (isNe && Ne(cur, threshold)) match = true;
		
		        if (!match && (crossAbove || crossBelow) && i > 0)
		        {
		            if (SafeTryGet(s, i - 1, out double prev))
		            {
		                if (crossAbove && !crossBelow && CrossesAbove(prev, cur, threshold)) match = true;
		                else if (crossBelow && !crossAbove && CrossesBelow(prev, cur, threshold)) match = true;
		                else if (crossAbove && crossBelow &&
		                         (CrossesAbove(prev, cur, threshold) || CrossesBelow(prev, cur, threshold)))
		                    match = true;
		            }
		        }
		
		        if (match) outIndices.Add(i);
		    }
		}




		// Try reading using our CurrentBar offsets (Predator-style) with a validator:
		// If the source already wrote on the current bar, do NOT surface previous closed.
		// If it hasn't, return 0.0 (no carry) instead of the previous closed value.
		private bool TryCurrentBarClosedRead(IndicatorBase src, Series<double> s, out int absIndex, out double val)
		{
		    absIndex = -1; 
		    val = double.NaN;
		
		    try
		    {
		        if (src == null || s == null || Times == null || Times[0] == null || CurrentBar < 1)
		            return false;
		
		        // 1) Map OUR previous primary-bar time --> SOURCE index
		        DateTime prevTime = Times[0][1];
		        var barsArray = src.BarsArray;
		        int idxPrev = (barsArray != null && barsArray.Length > 0 && barsArray[0] != null)
		                        ? barsArray[0].GetBar(prevTime) : -1;
		        if (idxPrev < 0 || idxPrev >= s.Count)
		            return false;
		
		        if (!SafeTryGet(s, idxPrev, out double vPrev) && !TryDirectSeriesFallback(s, idxPrev, out vPrev))
		            return false;
		
		        // Optional time freshness on the previous bar snapshot
		        if (TryGetSeriesTime(src, idxPrev, out var tPrev) && !IsFreshSnapshot(tPrev, idxPrev))
		            return false;
		
		        // 2) Validator: if the source already has a sample for the *current* bar, refuse to return closed
		        int absLive = s.Count - 1;
		        if (absLive >= 0 && SafeTryGet(s, absLive, out double vLive))
		        {
		            if (TryGetSeriesTime(src, absLive, out var tLive) &&
		                currBarTime != DateTime.MinValue && tLive == currBarTime)
		            {
		                // A real current-bar sample exists → caller should use live path, not closed
		                sawSourceUpdateThisBar = true;   // if you keep this field
		                return false;                    // signal: don't use closed here
		            }
		        }
		
		        // 3) No current-bar sample: return 0.0 instead of the previous closed value (no carry)

				// if (ApproxZero(vPrev)) zeroRun++; else zeroRun = 0;
				// bool accept = !ApproxZero(vPrev) || zeroRun >= ZeroRunAccept || badZeroFrames >= ZeroConfirmFrames;
				// 
				// val = accept ? vPrev : 0.0;
				
				// REPLACE with hard no-carry:
				zeroRun = ApproxZero(vPrev) ? (zeroRun + 1) : 0;   // keep your zero-run book-keeping if you need it elsewhere
				val = 0.0;                                               // <- always 0.0 on fresh bar when no live exists

		        return true;             // success: caller can surface 0.0
		    }
		    catch
		    {
		        return false;
		    }
		}
		
		private readonly HashSet<int> zeroAllowedPlots = new HashSet<int>(); // fill by name→index mapping
		private bool AcceptZeroForThisBar(int plotIndex) => zeroAllowedPlots.Contains(plotIndex);


		private int TryGetBarIndex(IndicatorBase src, DateTime t)
		{
		    var bars = src?.BarsArray != null && src.BarsArray.Length > 0 ? src.BarsArray[0] : null;
		    if (bars == null) return -1;
		    int idx = bars.GetBar(t);
		    return (idx >= 0 && idx < bars.Count) ? idx : -1;
		}

		private int GetPrimaryBarIndex()
		{
		    try
		    {
		        if (CurrentBars != null && CurrentBars.Length > 0)
		            return CurrentBars[0];
		    }
		    catch { }
		    return CurrentBar;
		}

		private bool ApiCallAllowed()
		{
		    int bar = GetPrimaryBarIndex();
		    if (bar < 0) return false;
		    return bar != lastApiCallBar;
		}

		private void MarkApiCall()
		{
		    int bar = GetPrimaryBarIndex();
		    if (bar >= 0) lastApiCallBar = bar;
		}

		private void ClearPendingEntry()
		{
		    pendingEntryActive = false;
		    pendingEntryBar = -1;
		}

		// === Tick buffer management ===
		private void ResetTickBufferForNewBar(int newBarIndex)
		{
		    // Save the final sign from the previous bar before resetting
		    int oldFinalSign = prevBarFinalSign;
		    if (tickBufferHasConfirmedSignal)
		        prevBarFinalSign = tickBufferLastSign;
		    // else keep prevBarFinalSign unchanged (don't reset to 0 if we had no signal)
		    
		    if (DebugMode && State == State.Realtime)
		        Print($"[DDPlotReader] TickBuffer RESET bar={newBarIndex} prevBarFinalSign={prevBarFinalSign} (was {oldFinalSign}) hadSignal={tickBufferHasConfirmedSignal} lastSign={tickBufferLastSign}");
		    
		    tickBufferBar = newBarIndex;
		    tickBufferCount = 0;
		    tickBufferFirstValue = double.NaN;
		    tickBufferLastValue = double.NaN;
		    tickBufferFirstSign = 0;
		    tickBufferLastSign = 0;
		    tickBufferHasConfirmedSignal = false;
		}
		
		private void RecordTickValue(double value, int barIndex)
		{
		    // If we're on a different bar, reset first
		    if (barIndex != tickBufferBar)
		        ResetTickBufferForNewBar(barIndex);
		    
		    tickBufferCount++;
		    
		    // Only record finite, confirmed values
		    if (double.IsNaN(value) || double.IsInfinity(value))
		        return;
		    
		    int sign = ApproxPositive(value) ? +1 : (ApproxNegative(value) ? -1 : 0);
		    
		    // Record first confirmed value
		    if (!tickBufferHasConfirmedSignal)
		    {
		        tickBufferFirstValue = value;
		        tickBufferFirstSign = sign;
		        tickBufferHasConfirmedSignal = true;
		    }
		    
		    // Always update last value
		    tickBufferLastValue = value;
		    tickBufferLastSign = sign;
		}
		
		// Get the "previous" sign for cross detection:
		// - If no confirmed signal yet this bar, use the previous bar's final sign
		// - If we have confirmed signal(s) this bar, use the last recorded sign (from prior tick)
		// NOTE: This is called BEFORE RecordTickValue(), so tickBufferLastSign contains prev tick's sign
		private int GetPrevSignForCrossDetection()
		{
		    if (!tickBufferHasConfirmedSignal)
		        return prevBarFinalSign;  // no signal yet this bar, use prior bar's final
		    
		    // We've already recorded at least one tick this bar.
		    // tickBufferLastSign contains the sign from the most recent RecordTickValue() call,
		    // which is the PREVIOUS tick (since we call GetPrevSignForCrossDetection() before RecordTickValue())
		    return tickBufferLastSign;
		}

		bool TimeEqOrBefore(DateTime tLive, DateTime curr, int skewMs = 500)
		{
		    return tLive <= curr || Math.Abs((tLive - curr).TotalMilliseconds) <= skewMs;
		}

		private double ReadPlotValueLatestValid(IndicatorBase src, int plotIndex, int lookback,
		                                        out int indexUsed, out int seriesCount, out string note)
		{
		    indexUsed = -1;
		    seriesCount = -1;
		    note = "";
			int LiveTimeSkewMs = 500;
			bool PreferClosedOnNewBar = true;
		    ForceIndicatorUpdate(src);
		    var s = SafeGetSeries(src, plotIndex);
		    if (s == null) { note = "no Series for plot"; return double.NaN; }
		
		    seriesCount = s.Count;
		    if (seriesCount <= 0) { note = "empty series"; return double.NaN; }
			
			// 1) Closed-first when the bar is fresh (HelloWin UX), then upgrade to live if/when verified
			bool isFreshBar = IsFirstTickOfBar; // or your own fresh-bar flag
			int liveIdx     = s.Count - 1;
			
			// (A) On fresh bar: use validated closed immediately
			if (isFreshBar && TryCurrentBarClosedRead(src, s, out int idxClosed, out double vClosed))
			{
			    indexUsed  = idxClosed;
			    dbgIsValid = true;
			    note       = "path:CurrentBarClosed";
			    return vClosed;
			}
			
			// (B) Try time-mapped current-bar index first (most reliable)
			int curIdx = -1;
			if (currBarTime != DateTime.MinValue)
			    curIdx = TryGetBarIndex(src, currBarTime);
			
			if (curIdx >= 0 && (SafeTryGet(s, curIdx, out double vCur) || TryDirectSeriesFallback(s, curIdx, out vCur)))
			{
			    bool finite = !(double.IsNaN(vCur) || double.IsInfinity(vCur));
			    if (finite)
			    {
			        // confirm it's not a future sample; allow small skew
			        bool hasTime = TryGetSeriesTime(src, curIdx, out var tCur);
			        bool okTime =
						(hasTime  && tCur == currBarTime) ||
						(!hasTime && curIdx == s.Count - 1);  // only accept the very last slot if we can't time-check
			        bool advanced = (curIdx != lastIndexAccepted) || (s.Count != lastSeriesCountAccepted) || Ne(vCur, lastRawValue);

			        // Zero policy: allow only if explicitly whitelisted
			        bool nearZero = ApproxZero(vCur);
			        if ((okTime || advanced) && (!nearZero || AcceptZeroForThisBar(plotIndex)))
			        {
			            indexUsed  = curIdx;
			            dbgIsValid = true;
			            sawSourceUpdateThisBar = (hasTime && (tCur == currBarTime)) || advanced;
			            lastSeriesCountAccepted = s.Count;
			            lastIndexAccepted       = curIdx;
			            note = advanced ? "path:LiveMapped" : "path:LiveMapped(hold)";
			            return vCur;
			        }

			        if (!okTime && !advanced && curIdx < s.Count - 1)
			            note = "path:LiveMappedRejected(stale)";
			    }
			}
			
			 // (C) Fallback: vendor exposes no usable time; use latest slot but be strict
			 if (liveIdx >= 0 && (SafeTryGet(s, liveIdx, out double vLive) || TryDirectSeriesFallback(s, liveIdx, out vLive)))
			 {
			     bool finite   = !(double.IsNaN(vLive) || double.IsInfinity(vLive));
			     bool hasTime  = TryGetSeriesTime(src, liveIdx, out var tLive);
			     bool okTime   = hasTime ? TimeEqOrBefore(tLive, currBarTime, LiveTimeSkewMs) : false;
			     bool advanced = (liveIdx != lastIndexAccepted) || (s.Count != lastSeriesCountAccepted) || Ne(vLive, lastRawValue);
			     bool nonZero  = !ApproxZero(vLive);
			 
			     // accept if time-okay OR (no time but advanced & non-zero)
			     if (finite && (okTime || (!hasTime && advanced && nonZero)))
			     {
			         indexUsed  = liveIdx;
			         dbgIsValid = true;
			         sawSourceUpdateThisBar = (hasTime && (tLive == currBarTime)) || advanced;
			         lastSeriesCountAccepted = s.Count;
			         lastIndexAccepted       = liveIdx;
			         note = hasTime
			             ? (advanced ? "path:LiveTimeOK" : "path:LiveTimeHold")
			             : "path:LiveNoTimeAdvanced";
			         return vLive;
			     }
			 }
			
			// (D) If we got here on a fresh bar and still nothing live, give Closed another try
			if (!isFreshBar && TryCurrentBarClosedRead(src, s, out int idx2, out double v2))
			{
			    indexUsed  = idx2;
			    dbgIsValid = true;
			    note       = "path:CurrentBarClosedLate";
			    return v2;
			}


		    // 3) Historical scan within lookback for the newest finite, fresh point
		    int maxBack = Math.Min(lookback, s.Count);
		    for (int back = 1; back <= maxBack; back++)
		    {
		        int idx = s.Count - 1 - back;
		        if (idx < 0) break;
			
		        if (SafeTryGet(s, idx, out double v) || TryDirectSeriesFallback(s, idx, out v))
		        {
		            if (!double.IsInfinity(v))
		            {
		                bool fresh = !TryGetSeriesTime(src, idx, out var t) || IsFreshSnapshot(t, idx);
		                if (fresh)
		                {
		                    indexUsed = idx;
		                    dbgIsValid = true;
		                    note = "path:HistoricalScan";
		                    return v;
		                }
		            }
		        }
		    }
		
		    // 4) Nothing valid found
		    dbgIsValid = false;
		    note = (note == "" ? "" : (note + "; ")) + "no valid point within lookback";
		    return double.NaN;
		}






		private static long NowMs()
		{
		    return (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
		}
		
		private bool ScanAllowed()
		{
		    return NowMs() >= nextScanDueMs && !rebuildInProgress;
		}
		
		private void BumpNextScan(int ms)
		{
		    nextScanDueMs = NowMs() + Math.Max(250, ms);
		}
		
		private void BackoffAfterEmptyOrError()
		{
		    backoffPow = Math.Min(MaxBackoffPow, backoffPow + 1);
		    var delay = BaseBackoffMs << backoffPow;   // exponential
		    BumpNextScan(delay);
		    if (DebugMode) Dbg($"scan backoff: pow={backoffPow}, delay={delay}ms");
		}
		
		private void ResetBackoff()
		{
		    backoffPow = 0;
		    BumpNextScan(MinScanIntervalMs);
		}


		
		// Walk the WPF visual tree and collect *likely* indicator holders by name/shape.
		// No compile-time dependency on internal types (e.g., IndicatorRenderBase).
		private void CollectIndicatorHoldersFromVisualTree(DependencyObject root, HashSet<object> holders)
		{
		    if (root == null) return;
		
		    int count;
		    try { count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); }
		    catch { return; }
		
		    for (int i = 0; i < count; i++)
		    {
		        var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
		        if (child == null) continue;
		
		        try
		        {
		            var t = child.GetType();
		            var name = t.FullName ?? t.Name;
		
		            // Heuristics: objects whose type name indicates indicator containers
		            // (ChartIndicator, IndicatorRenderBase, IndicatorHost, etc.)
		            if (name.IndexOf("Indicator", StringComparison.OrdinalIgnoreCase) >= 0)
		                holders.Add(child);
		
		            // Sometimes the DataContext is the holder
		            var fe = child as System.Windows.FrameworkElement;
		            var dc = fe?.DataContext;
		            if (dc != null)
		            {
		                var dn = dc.GetType().FullName ?? dc.GetType().Name;
		                if (dn.IndexOf("Indicator", StringComparison.OrdinalIgnoreCase) >= 0)
		                    holders.Add(dc);
		            }
		        }
		        catch { /* ignore bad visual nodes */ }
		
		        // Recurse
		        CollectIndicatorHoldersFromVisualTree(child, holders);
		    }
		}



		private IEnumerable<object> SnapshotChartIndicators()
		{
		    var direct = new List<object>();
		    var indicators = ChartControl?.Indicators;
		    if (indicators == null)
		        return direct;
		
		    try
		    {
		        lock (indicators)
		        {
		            foreach (var obj in indicators)
		            {
		                if (obj != null)
		                    direct.Add(obj);
		            }
		        }
		    }
		    catch
		    {
		        // fall back to reflection path
		    }
		
		    return direct;
		}
		
		
        #region Catalog (DDPanel-style plot reader)
		private IEnumerable<object> GetIndicatorHolders()
		{
		    var bag = new HashSet<object>();
		
		    if (ChartControl == null)
		        return bag;
		
		    // Chart-level properties
		    foreach (var prop in new[] { "Indicators", "IndicatorsOnChart", "IndicatorCollection", "IndicatorRenderBases" })
		        foreach (var x in TryGetEnumerableProperty(ChartControl, prop) ?? System.Linq.Enumerable.Empty<object>())
		            bag.Add(x);
		
		    // Chart-level fields
		    foreach (var field in new[] { "_indicators", "_indicatorCollection", "indicatorRenderBases", "_indicatorRenderBases" })
		        foreach (var x in TryGetEnumerableField(ChartControl, field) ?? System.Linq.Enumerable.Empty<object>())
		            bag.Add(x);
		
		    // Panels (properties & fields)
		    IEnumerable<object> panels = null;
		    foreach (var pName in new[] { "ChartPanels", "Panels", "PanelsInternal" })
		        if ((panels = TryGetEnumerableProperty(ChartControl, pName)) != null) break;
		
		    if (panels == null)
		        foreach (var fName in new[] { "_chartPanels", "_panels", "_panelsInternal" })
		            if ((panels = TryGetEnumerableField(ChartControl, fName)) != null) break;
		
		    if (panels != null)
		    {
		        foreach (var panel in panels)
		        {
		            foreach (var prop in new[] { "Indicators", "IndicatorCollection", "IndicatorRenderBases", "Objects" })
		                foreach (var x in TryGetEnumerableProperty(panel, prop) ?? System.Linq.Enumerable.Empty<object>())
		                    bag.Add(x);
		
		            foreach (var field in new[] { "_indicators", "_indicatorCollection", "indicatorRenderBases", "_indicatorRenderBases", "_objects" })
		                foreach (var x in TryGetEnumerableField(panel, field) ?? System.Linq.Enumerable.Empty<object>())
		                    bag.Add(x);
		        }
		    }
		
		    // Visual tree fallback (pure reflection; no internal types)
		    try
		    {
		        var vt = new HashSet<object>();
		        CollectIndicatorHoldersFromVisualTree(ChartControl, vt);
		        foreach (var h in vt) bag.Add(h);
		    }
		    catch { }
		
		    return bag;
		}




        private static IEnumerable<object> TryGetEnumerableProperty(object obj, string propName)
        {
            if (obj == null || string.IsNullOrEmpty(propName))
                return null;

            try
            {
                var p = obj.GetType().GetProperty(propName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var v = p?.GetValue(obj) as System.Collections.IEnumerable;
                if (v == null) return null;

                var list = new List<object>();
                foreach (var item in v) list.Add(item);
                return list;
            }
            catch { return null; }
        }
		
		private Indicator FindIndicatorDeep(object obj, int depth, int maxDepth, string path)
		{
		    if (obj == null || depth > maxDepth) return null;
		
		    int id = ObjId(obj);
		    if (!dbgVisitedIds.Add(id)) return null;   // skip cycles
		
		    // Direct match
		    if (obj is Indicator di) return di;
		    if (obj is IndicatorBase dbi && dbi is Indicator di2) return di2;
		
		    var t = obj.GetType();
		
		    // 1) Properties
		    try
		    {
		        var props = t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		        foreach (var p in props)
		        {
		            object v = null;
		            try { v = p.GetValue(obj); } catch { continue; }
		            if (v == null) continue;
		
		            if (v is Indicator px) return px;
		            if (v is IndicatorBase pxb && pxb is Indicator pix) return pix;
		
		            // If it's an enumerable, walk children
		            if (v is System.Collections.IEnumerable en && !(v is string))
		            {
		                int idx = 0;
		                foreach (var item in en)
		                {
		                    var r = FindIndicatorDeep(item, depth + 1, maxDepth, path + $".{p.Name}[{idx}]");
		                    if (r != null) return r;
		                    idx++;
		                }
		            }
		            else
		            {
		                var r = FindIndicatorDeep(v, depth + 1, maxDepth, path + $".{p.Name}");
		                if (r != null) return r;
		            }
		        }
		    } catch { }
		
		    // 2) Fields
		    try
		    {
		        var fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		        foreach (var f in fields)
		        {
		            object v = null;
		            try { v = f.GetValue(obj); } catch { continue; }
		            if (v == null) continue;
		
		            if (v is Indicator fx) return fx;
		            if (v is IndicatorBase fxb && fxb is Indicator fix) return fix;
		
		            if (v is System.Collections.IEnumerable en && !(v is string))
		            {
		                int idx = 0;
		                foreach (var item in en)
		                {
		                    var r = FindIndicatorDeep(item, depth + 1, maxDepth, path + $".{f.Name}[{idx}]");
		                    if (r != null) return r;
		                    idx++;
		                }
		            }
		            else
		            {
		                var r = FindIndicatorDeep(v, depth + 1, maxDepth, path + $".{f.Name}");
		                if (r != null) return r;
		            }
		        }
		    } catch { }
		
		    return null;
		}
		
		private IndicatorBase AsIndicatorBase(object holder)
		{
		    if (holder is IndicatorBase ib) return ib;
		
		    // quick property/field probes (no vendor internals)
		    string[] tryNames = { "Indicator", "Core", "Source", "Owner", "StrategyIndicator", "NinjaScript", "InnerIndicator" };
		    var t = holder?.GetType();
		    if (t == null) return null;
		
		    foreach (var name in tryNames)
		    {
		        try
		        {
		            var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		            var pv = p?.GetValue(holder);
		            if (pv is IndicatorBase pBase) return pBase;
		        }
		        catch { }
		
		        try
		        {
		            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		            var fv = f?.GetValue(holder);
		            if (fv is IndicatorBase fBase) return fBase;
		        }
		        catch { }
		    }
		
		    // bounded deep walk you already use, but return IndicatorBase when seen
		    var found = FindIndicatorDeep(holder, 0, 3, "<root>");
		    return (found as IndicatorBase) ?? null;
		}

		
		// Keep labels inside the box
        private string Ellipsize(string s, float maxWidth)
        {
            if (string.IsNullOrEmpty(s)) return s;

            // Clear cache if width changed (e.g., resize)
            if (Math.Abs(maxWidth - lastEllipsizeWidth) > 1f)
            {
                ellipsizeCache.Clear();
                lastEllipsizeWidth = maxWidth;
            }

            // Cached?
            if (ellipsizeCache.TryGetValue(s, out var cached))
                return cached;

            // quick accept
            using (var tl = new TextLayout(dwFactory, s, tf, maxWidth, boxH))
                if (tl.Metrics.Width <= maxWidth)
                {
                    ellipsizeCache[s] = s;
                    return s;
                }

            const string ell = "…";
            int lo = 0, hi = s.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                string cand = s.Substring(0, mid) + ell;
                using (var tl = new TextLayout(dwFactory, cand, tf, maxWidth, boxH))
                {
                    if (tl.Metrics.Width <= maxWidth) lo = mid + 1; else hi = mid;
                }
            }
            int take = Math.Max(0, lo - 1);
            string result = (take <= 0) ? ell : s.Substring(0, take) + ell;
            ellipsizeCache[s] = result;
            return result;
        }


		private static IEnumerable<object> TryGetEnumerableField(object obj, string fieldName)
		{
		    if (obj == null || string.IsNullOrEmpty(fieldName))
		        return null;
		
		    try
		    {
		        var f = obj.GetType().GetField(fieldName,
		            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		        var v = f?.GetValue(obj) as System.Collections.IEnumerable;
		        if (v == null) return null;
		
		        var list = new List<object>();
		        foreach (var item in v) list.Add(item);
		        return list;
		    }
		    catch { return null; }
		}
		
		// Ensure mouse events are attached once ChartPanel actually exists
		private void EnsureMouseHook()
		{
		    if (mouseHooked || ChartPanel == null) return;
		    try
		    {
		        ChartPanel.PreviewMouseLeftButtonDown += OnMouseLeftButtonDown;
		        ChartPanel.PreviewMouseMove           += OnMouseMove;
		        ChartPanel.PreviewMouseLeftButtonUp   += OnMouseLeftButtonUp;
		        ChartPanel.PreviewMouseWheel          += OnMouseWheel;
		        mouseHooked = true;
		    }
		    catch { }
		}

		
		// Public entry: always marshal to UI thread synchronously to avoid transient empties
		
		private List<PlotOption> BuildCatalogCoreFromSnapshot(List<object> holdersSnap)
		{
		    var newOptions = new List<PlotOption>();
		    seen.Clear();
		
		    int holderCount = 0, indCount = 0;
		
			foreach (var holder in holdersSnap)
			{
			    holderCount++;
			
			    // Prefer IndicatorBase (covers both *.cs and *.dll indicators even if load contexts differ)
			    var ib = AsIndicatorBase(holder);
			    if (ib == null) continue;
			    if (ReferenceEquals(ib, this)) continue;
			
			    indCount++;
			
			    // Values[] is on IndicatorBase
			    var vals = ReflectSafe.SafeGetValues(ib);
			    if (vals == null || vals.Length == 0) continue;
			
			    // Try to read plot names reflectively; fallback to "Plot{j}"
			    var (plotCount, nameAt) = ReflectSafe.SafeGetPlots(ib);
			
			    int limit = vals.Length; // Values[] length is the hard cap we can actually read
			    for (int j = 0; j < limit; j++)
			    {
			        Series<double> s = null;
			        try { s = vals[j]; } catch { }
			        if (s == null) continue;
			
			        if (seen.Contains(s)) continue; // de-dupe across indicators
			
			        string plotName = (plotCount > 0 ? nameAt(j) : null) ?? $"Plot{j}";
			        string label    = $"{ib.Name} • {plotName}";
			
			        newOptions.Add(new PlotOption
			        {
			            Label     = label,
			            Source    = ib,   // store IndicatorBase
			            PlotIndex = j
			        });
			
			        seen.Add(s);
			    }
			}

		
		    if (DebugMode)
		        Dbg($"build: holders={holderCount}, indicators={indCount}, options(new)={newOptions.Count}");
		
		    return newOptions;
		}


		private void BuildCatalogSafe()
		{
		    if (ChartControl == null || rebuildInProgress) return;
		
		    // throttle
		    if (!ScanAllowed()) return;
		
		    rebuildInProgress = true;
		    try
		    {
		        // snapshot holders on UI thread
		        var holdersSnap = new List<object>();
		        var holderIds = new HashSet<int>();
		        Action<object> addHolder = obj =>
		        {
		            if (obj == null) return;
		            int id = ObjId(obj);
		            if (holderIds.Add(id))
		                holdersSnap.Add(obj);
		        };
		
		        try
		        {
		            foreach (var direct in SnapshotChartIndicators())
		                addHolder(direct);
		        }
		        catch { /* ignore */ }
		
		        try
		        {
		            foreach (var h in GetIndicatorHolders())
		                addHolder(h);
		        }
		        catch { /* ignore */ }
		
		        var newOptions = BuildCatalogCoreFromSnapshot(holdersSnap); // <- you already added earlier
		
                if (newOptions.Count > 0)
                {
                    options.Clear(); options.AddRange(newOptions);
                    optionsStable.Clear(); optionsStable.AddRange(newOptions);
                    ellipsizeCache.Clear(); // new catalog -> drop cached truncations
                    ResetBackoff();
                }
		        else
		        {
		            // keep last good catalog, don’t drop to zero
		            if (optionsStable.Count > 0)
		            {
		                options.Clear(); options.AddRange(optionsStable);
		            }
		            BackoffAfterEmptyOrError();
		        }
		
			// clamp selection / scroll - try to restore saved selection or default to none
			if (options.Count == 0)
			{
			    selectedIndex = -1;          // nothing to select yet
			}
			else
			{
			    // Try to restore from saved label
			    int restoredIdx = -1;
			    if (!string.IsNullOrEmpty(SavedPlotLabel))
			    {
			        restoredIdx = options.FindIndex(o => o.Label == SavedPlotLabel && o.Source != null);
			        Print($"[DDPlotReader] Restore attempt: SavedPlotLabel='{SavedPlotLabel}' foundIdx={restoredIdx} options.Count={options.Count}");
			    }
			    else
			    {
			        Print($"[DDPlotReader] SavedPlotLabel is empty, no restore attempted. options.Count={options.Count}");
			    }
			    
			    if (restoredIdx >= 0)
			    {
			        // Found saved selection, restore it
			        selectedIndex = restoredIdx;
			        Print($"[DDPlotReader] RESTORED selection to idx={restoredIdx} label='{options[restoredIdx].Label}'");
			    }
			    else if (selectedIndex >= 0 && selectedIndex < options.Count && options[selectedIndex].Source != null)
			    {
			        // Current selection is still valid, keep it
			        Print($"[DDPlotReader] Keeping current valid selection idx={selectedIndex}");
			    }
			    else
			    {
			        // No saved selection found or invalid, default to no selection
			        selectedIndex = -1;
			        Print($"[DDPlotReader] No saved/valid selection found, defaulting to none");
			    }
			
			    int maxStart = Math.Max(0, options.Count - VisibleRows);
			    listStart = Math.Max(0, Math.Min(listStart, maxStart));
			}
		
		        RequestRedraw();
		    }
		    catch (Exception ex)
		    {
		        if (DebugMode) Dbg("BuildCatalogSafe exception: " + ex.Message);
		        BackoffAfterEmptyOrError();
		    }
		    finally
		    {
		        rebuildInProgress = false;
		        // NEW
				if (State != State.Terminated)
				    BumpNextScan(MinScanIntervalMs);
		    }
		}


        private void HookIndicatorCollectionChanged()
        {
            try
            {
                var p = ChartControl?.GetType().GetProperty("Indicators",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var list = p?.GetValue(ChartControl) as INotifyCollectionChanged;
                if (list == null) return;

                list.CollectionChanged -= IndicatorList_CollectionChanged;
                list.CollectionChanged += IndicatorList_CollectionChanged;
            }
            catch { }
        }

        private void IndicatorList_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
             ChartControl?.Dispatcher.InvokeAsync(() =>
   			 {
   			     BuildCatalogSafe();
   			     if (!CatalogHasRealPlots())
   			         EnsureCatalogScheduled();
   			 });
        }
        #endregion

        #region Mouse

		private void UnhookMouse()
		{
		    try
		    {
		        if (mouseHooked && ChartPanel != null)
		        {
		            ChartPanel.PreviewMouseLeftButtonDown -= OnMouseLeftButtonDown;
		            ChartPanel.PreviewMouseMove           -= OnMouseMove;
		            ChartPanel.PreviewMouseLeftButtonUp   -= OnMouseLeftButtonUp;
		            ChartPanel.PreviewMouseWheel          -= OnMouseWheel;   // ← add this line
		        }
		    }
		    catch { }
		    finally { mouseHooked = false; }
		}
		
        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (ChartPanel == null) return;
            var pos = e.GetPosition(ChartPanel);
			
			// SEND toggle click
			if (sendRect.Contains(new SharpDX.Vector2((float)pos.X, (float)pos.Y)))
			{
			    sendEnabled = !sendEnabled;
			    RequestRedraw();
			    e.Handled = true;
			    return;
			}

            var dropRect = new Rect(TLX, TLY, WidthPx, (int)boxH);
            if (dropRect.Contains(pos))
            {
                isOpen = !isOpen;
				int maxStart = Math.Max(0, options.Count - VisibleRows);
    			listStart = Math.Max(0, Math.Min(maxStart, selectedIndex - VisibleRows / 2));
                hoverIndex = -1;
                RequestRedraw();
                e.Handled = true;
                return;
            }
			
			// ORDER MODE toggle click (cycle POP → DROP → MKT)
			// if (orderModeRect.Contains(new SharpDX.Vector2((float)pos.X, (float)pos.Y)))
			// {
			//     // Only meaningful for TickHunter (still allow cycling even if TH not enabled)
			//     EntryMode = EntryMode == THEntryStyle.Pop ? THEntryStyle.Drop
			//              : EntryMode == THEntryStyle.Drop ? THEntryStyle.Market
			//              : THEntryStyle.Pop;
			// 
			//     RequestRedraw();
			//     e.Handled = true;
			//     return;
			// }
			
			// ORDER MODE toggle click (cycle POP → DROP → MKT)
			if (orderModeRect.Contains(new SharpDX.Vector2((float)pos.X, (float)pos.Y)))
			{
			    if (destMode != DestMode.DDATM)    // disabled when ATM is selected
			    {
			        EntryMode = EntryMode == THEntryStyle.Pop ? THEntryStyle.Drop
			                 : EntryMode == THEntryStyle.Drop ? THEntryStyle.Market
			                 : THEntryStyle.Pop;
			        RequestRedraw();
			    }
			    e.Handled = true;
			    return;
			}
						
			
			// DESTINATION toggle click: TH → ATM → TP → TH
			if (destModeRect.Contains(new SharpDX.Vector2((float)pos.X, (float)pos.Y))) {
			    destMode = destMode == DestMode.TickHunter ? DestMode.DDATM
			            : destMode == DestMode.DDATM     ? DestMode.DDTP
			            : DestMode.TickHunter;
			    ApplyDestModeFlags();
			    RequestRedraw();
			    e.Handled = true;
			    return;
			}

			
			// DIR FILTER toggle click (BOTH → LONG → SHORT → BOTH)
			if (dirFilterRect.Contains(new SharpDX.Vector2((float)pos.X, (float)pos.Y)))
			{
			    dirFilter = dirFilter == DirFilter.Both  ? DirFilter.Longs
			             : dirFilter == DirFilter.Longs ? DirFilter.Shorts
			             : DirFilter.Both;
			    RequestRedraw();
			    e.Handled = true;
			    return;
			}



            if (isOpen)
            {

				int rowsAvailable = Math.Max(0, options.Count - listStart);
				int rowsToDraw    = Math.Min(VisibleRows, rowsAvailable);
				var listRect = new Rect(TLX, TLY + boxH + 2, WidthPx, itemH * rowsToDraw);
				
				if (listRect.Contains(pos))
				{
				    int row = (int)Math.Floor((pos.Y - listRect.Top) / itemH);
				    row = Math.Max(0, Math.Min(rowsToDraw - 1, row));
				    int idx = listStart + row;                             // map row → global
				if (idx >= 0 && idx < options.Count && options[idx].Source != null)
				{
				    selectedIndex = idx;
				    SavedPlotLabel = options[idx].Label;
				    SavePlotSelection(SavedPlotLabel);  // Persist to file
				
					selectedLiveValue = ReadPlotValueLatestValid(options[selectedIndex].Source,
					                                             options[selectedIndex].PlotIndex,
					                                             64,
					                                             out dbgIndexUsed, out dbgSeriesCount, out dbgNote);
					dbgSel = options[selectedIndex].Label;
					Dbg($"SELECT '{dbgSel}' usedAgo={dbgIndexUsed} value={(double.IsNaN(selectedLiveValue) ? "NaN" : selectedLiveValue.ToString("G6"))} note={dbgNote}");

				}
										
					isOpen = false;
					hoverIndex = -1;
					RequestRedraw();
					e.Handled = true;
				}
                else
                {
                    isOpen = false;
                    hoverIndex = -1;
                    RequestRedraw();
                }
            }
			
			if (refreshRect.Contains(new SharpDX.Vector2((float)pos.X, (float)pos.Y))) {
			    BuildCatalogSafe();
			    EnsureCatalogScheduled();
			    e.Handled = true;
			    return;
			}

        }
		
		private void OnMouseWheel(object sender, MouseWheelEventArgs e)
		{
		    // If the dropdown isn't open, ignore the wheel completely so the chart
		    // can use it (no plot cycling while closed).
		    if (!isOpen || ChartPanel == null || options.Count == 0)
		        return;
		
		    // When open, only scroll the visible list window.
		    var pos = e.GetPosition(ChartPanel);
		
		    // Compute the list rectangle using current paging
		    int rowsAvailable = Math.Max(0, options.Count - listStart);
		    int rowsToDraw    = Math.Min(VisibleRows, rowsAvailable);
		    var listRect      = new Rect(TLX, TLY + boxH + 2, WidthPx, itemH * rowsToDraw);
		
		    if (!listRect.Contains(pos))
		        return; // mouse not over the dropdown list; ignore
		
		    int dir = e.Delta > 0 ? -1 : 1; // wheel up = scroll up
		    int maxStart = Math.Max(0, options.Count - VisibleRows);
		    listStart = Math.Max(0, Math.Min(maxStart, listStart + dir));
		
		    RequestRedraw();
		    e.Handled = true; // consume so the chart doesn't also zoom/scroll
		}




        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!isOpen || ChartPanel == null) return;
						
   			 var pos = e.GetPosition(ChartPanel);
				 int rowsAvailable = Math.Max(0, options.Count - listStart);
				int rowsToDraw    = Math.Min(VisibleRows, rowsAvailable);
				var listRect = new Rect(TLX, TLY + boxH + 2, WidthPx, itemH * rowsToDraw);
			
			bool newHoverSend = sendRect.Contains(new SharpDX.Vector2((float)pos.X, (float)pos.Y));
			if (newHoverSend != hoverSend) { hoverSend = newHoverSend; RequestRedraw(); }
			
			// bool newHoverOrder = orderModeRect.Contains(new SharpDX.Vector2((float)pos.X, (float)pos.Y));
			// if (newHoverOrder != hoverOrder) { hoverOrder = newHoverOrder; RequestRedraw(); }

			
			bool newHoverRefresh = refreshRect.Contains(new SharpDX.Vector2((float)pos.X, (float)pos.Y));
			if (newHoverRefresh != hoverRefresh) { hoverRefresh = newHoverRefresh; RequestRedraw(); }
			
			bool newHoverDest = destModeRect.Contains(new SharpDX.Vector2((float)pos.X, (float)pos.Y));
			if (newHoverDest != hoverDest) { hoverDest = newHoverDest; RequestRedraw(); }
			
			bool orderActive = (destMode != DestMode.DDATM);
			bool newHoverOrder = orderActive && orderModeRect.Contains(new SharpDX.Vector2((float)pos.X, (float)pos.Y));
			if (newHoverOrder != hoverOrder) { hoverOrder = newHoverOrder; RequestRedraw(); }

			bool newHoverDir = dirFilterRect.Contains(new SharpDX.Vector2((float)pos.X, (float)pos.Y));
			if (newHoverDir != hoverDirFilter) { hoverDirFilter = newHoverDir; RequestRedraw(); }

			
            int newHover = -1;
            if (listRect.Contains(pos))
            {
                newHover = (int)Math.Floor((pos.Y - listRect.Top) / itemH);
                newHover = Math.Max(0, Math.Min(rowsToDraw - 1, newHover));
            }

            if (newHover != hoverIndex)
            {
                hoverIndex = newHover;
                RequestRedraw();
            }
        }

        private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e) { }
        #endregion

        #region Rendering
        public override void OnRenderTargetChanged()
        {
            DisposeDeviceResources();
            if (RenderTarget == null)
                return;

            dwFactory = new DWFactory();
            tf        = new TextFormat(dwFactory, "Segoe UI", DWFontWeight.Normal, DWFontStyle.Normal, 11f);
			
			if (valTf != null) { valTf.Dispose(); valTf = null; }
			valTf = new TextFormat(dwFactory, "Segoe UI", DWFontWeight.Normal, DWFontStyle.Normal, 11f);

			

            brushBg     = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new Color4(0.10f, 0.10f, 0.12f, 0.90f));
            brushOpenBg = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new Color4(0.12f, 0.12f, 0.16f, 0.95f));
            brushBorder = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new Color4(0.45f, 0.45f, 0.50f, 1.00f));
            brushText   = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new Color4(0.95f, 0.95f, 0.98f, 1.00f));
            brushHover  = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new Color4(0.30f, 0.45f, 0.80f, 0.25f));
        }

        private void DisposeDeviceResources()
        {
            Utilities.Dispose(ref brushBg);
            Utilities.Dispose(ref brushOpenBg);
            Utilities.Dispose(ref brushBorder);
            Utilities.Dispose(ref brushText);
            Utilities.Dispose(ref brushHover);
            Utilities.Dispose(ref tf);
            Utilities.Dispose(ref dwFactory);
        	Utilities.Dispose(ref valTf);
		}

		protected override void OnRender(ChartControl cc, ChartScale cs)
		{
		    // NEW: late hook guarantees clicks toggle isOpen
		    EnsureMouseHook();
		
		    boxX = TLX; boxY = TLY; boxW = WidthPx;
		    var rt = RenderTarget;
		    if (rt == null || tf == null) return;
			
			// --- SEND toggle BELOW the dropdown (slightly wider) ---
			float sendH = boxH;
			float sendW = boxH * 1.4f;              // ~40% wider than tall (tweak if needed)
			float sendX = boxX;                     // left-aligned with dropdown
			float sendY = boxY + boxH + sendGap;    // below
			
			sendRect = new SharpDX.RectangleF(sendX, sendY, sendW, sendH);

			
			// BG + border (green-ish when ON, dim when OFF)
			var onBg  = new Color4(0.10f, 0.35f, 0.10f, 0.95f);
			var offBg = new Color4(0.15f, 0.15f, 0.18f, 0.95f);
			using (var geoSend = new RoundedRectangleGeometry(rt.Factory, new RoundedRectangle { Rect = sendRect, RadiusX = 4f, RadiusY = 4f }))
			{
			    var fill = new SharpDX.Direct2D1.SolidColorBrush(rt, sendEnabled ? onBg : offBg);
			    rt.FillGeometry(geoSend, fill);
			    rt.DrawGeometry(geoSend, brushBorder, hoverSend ? 1.8f : 1.0f);
			    fill.Dispose();
			}
			
			// Label "SEND"
			using (var tlSend = new TextLayout(dwFactory, "SEND", tf, sendRect.Width, sendRect.Height))
			{
			    var tx = new SharpDX.Direct2D1.SolidColorBrush(rt, new Color4(0.95f,0.95f,0.98f,1f));
			    float txX = sendRect.Left + (sendRect.Width  - tlSend.Metrics.Width)  * 0.5f;
			    float txY = sendRect.Top  + (sendRect.Height - tlSend.Metrics.Height) * 0.5f;
			    rt.DrawTextLayout(new Vector2(txX, txY), tlSend, tx);
			    tx.Dispose();
			}
			
			// --- ORDER MODE toggle (POP / DROP / MKT) to the right of SEND ---
			float orderH = sendRect.Height;
			float orderW = sendRect.Height * 1.4f;
			float orderX = sendRect.Right + orderGap;
			float orderY = sendRect.Top;
			
			orderModeRect = new SharpDX.RectangleF(orderX, orderY, orderW, orderH);
			
			// Colors per mode
			Color4 bgPop      = new Color4(0.05f, 0.35f, 0.55f, 0.95f);
			Color4 bgDrop     = new Color4(0.55f, 0.15f, 0.55f, 0.95f);
			Color4 bgMarket   = new Color4(0.50f, 0.40f, 0.10f, 0.95f);
			Color4 bgDisabled = new Color4(0.20f, 0.20f, 0.22f, 0.75f);
			
			// If you have destMode from the TH/ATM toggle:
			bool disableOrderMode = (destMode != DestMode.TickHunter); // grey out in ATM or TP
			// If you don't have destMode, a safe alternative is:
			// bool disableOrderMode = SendToDDATM && !SendToTickHunter;
			
			bool thReady = SendToTickHunter && BridgeEndpointExists();
			
			var bg = bgDisabled;
			string label = "—";
			
			if (!disableOrderMode)
			{
			    switch (EntryMode)
			    {
			        case THEntryStyle.Pop:    bg = thReady ? bgPop    : bgDisabled; label = "POP";  break;
			        case THEntryStyle.Drop:   bg = thReady ? bgDrop   : bgDisabled; label = "DROP"; break;
			        case THEntryStyle.Market: bg = thReady ? bgMarket : bgDisabled; label = "MKT";  break;
			    }
			}
			else
			{
			    // Greyed out when DDATM is selected
			    switch (EntryMode)
			    {
			        case THEntryStyle.Pop:    label = "POP";  break;
			        case THEntryStyle.Drop:   label = "DROP"; break;
			        case THEntryStyle.Market: label = "MKT";  break;
			    }
			    bg = bgDisabled;
			}
			
			using (var geo = new RoundedRectangleGeometry(rt.Factory, new RoundedRectangle { Rect = orderModeRect, RadiusX = 4f, RadiusY = 4f }))
			{
			    using (var fill = new SharpDX.Direct2D1.SolidColorBrush(rt, bg))
			        rt.FillGeometry(geo, fill);
			
			    // Thin border, no hover emphasis when disabled
			    float borderW = (disableOrderMode ? 1.0f : (hoverOrder ? 1.8f : 1.0f));
			    rt.DrawGeometry(geo, brushBorder, borderW);
			}
			
			// Center the label
			using (var tl = new TextLayout(dwFactory, label, tf, orderModeRect.Width, orderModeRect.Height))
			using (var tx = new SharpDX.Direct2D1.SolidColorBrush(rt, new Color4(0.95f, 0.95f, 0.98f, 1f)))
			{
			    float txX = orderModeRect.Left + (orderModeRect.Width  - tl.Metrics.Width)  * 0.5f;
			    float txY = orderModeRect.Top  + (orderModeRect.Height - tl.Metrics.Height) * 0.5f;
			    rt.DrawTextLayout(new Vector2(txX, txY), tl, tx);
			}


		
			// --- DESTINATION toggle (TH / ATM) to the right of ORDER MODE ---
			float destH = orderModeRect.Height;
			// float destW = orderModeRect.Height * 1.6f; // a little wider for text
			float destW = Math.Max(destModeRect.Height*1.6f, 42f);
			float destX = orderModeRect.Right + destGap;
			float destY = orderModeRect.Top;
			
			destModeRect = new SharpDX.RectangleF(destX, destY, destW, destH);
			
			// Colors
			Color4 bgTH   = new Color4(0.15f, 0.45f, 0.15f, 0.95f); // green-ish for TH
			Color4 bgATM  = new Color4(0.10f, 0.35f, 0.60f, 0.95f); // blue-ish for DDATM
			Color4 bgTP = new Color4(0.35f, 0.30f, 0.10f, 0.95f); // warm amber/brownish for DDTP
			Color4 bgGray = new Color4(0.20f, 0.20f, 0.22f, 0.75f);
			
			string destLabel = destMode == DestMode.DDATM ? "ATM" : (destMode == DestMode.DDTP ? "TP" : "TH");
			Color4 destBg    = destMode == DestMode.DDATM ? bgATM : (destMode == DestMode.DDTP ? bgTP : bgTH);
			
			using (var geo = new RoundedRectangleGeometry(rt.Factory, new RoundedRectangle { Rect = destModeRect, RadiusX = 4f, RadiusY = 4f }))
			{
			    using (var fill = new SharpDX.Direct2D1.SolidColorBrush(rt, destBg))
			        rt.FillGeometry(geo, fill);
			    rt.DrawGeometry(geo, brushBorder, hoverDest ? 1.8f : 1.0f);
			}
			
			// Center the label
			using (var tl = new TextLayout(dwFactory, destLabel, tf, destModeRect.Width, destModeRect.Height))
			using (var tx = new SharpDX.Direct2D1.SolidColorBrush(rt, new Color4(0.95f,0.95f,0.98f,1f)))
			{
			    float txX = destModeRect.Left + (destModeRect.Width  - tl.Metrics.Width)  * 0.5f;
			    float txY = destModeRect.Top  + (destModeRect.Height - tl.Metrics.Height) * 0.5f;
			    rt.DrawTextLayout(new Vector2(txX, txY), tl, tx);
			}

			// --- DIR FILTER toggle (BOTH / LONG / SHORT) to the right of DEST ---
			float dfH = destModeRect.Height;
			float dfW = destModeRect.Height * 1.8f;
			float dfX = destModeRect.Right + dirGap;
			float dfY = destModeRect.Top;
			
			dirFilterRect = new SharpDX.RectangleF(dfX, dfY, dfW, dfH);
			
			// Colors
			Color4 bgBoth  = new Color4(0.25f, 0.25f, 0.30f, 0.95f); // neutral
			Color4 bgLong  = new Color4(0.10f, 0.45f, 0.15f, 0.95f); // green-ish
			Color4 bgShort = new Color4(0.55f, 0.15f, 0.15f, 0.95f); // red-ish
			
			Color4 dfBg; string dfLabel;
			switch (dirFilter)
			{
			    case DirFilter.Longs:  dfBg = bgLong;  dfLabel = "LONG";  break;
			    case DirFilter.Shorts: dfBg = bgShort; dfLabel = "SHORT"; break;
			    default:               dfBg = bgBoth;  dfLabel = "BOTH";  break;
			}
			
			using (var geo = new RoundedRectangleGeometry(rt.Factory, new RoundedRectangle { Rect = dirFilterRect, RadiusX = 4f, RadiusY = 4f }))
			{
			    using (var fill = new SharpDX.Direct2D1.SolidColorBrush(rt, dfBg))
			        rt.FillGeometry(geo, fill);
			    rt.DrawGeometry(geo, brushBorder, hoverDirFilter ? 1.8f : 1.0f);
			}
			
			// Center label
			using (var tl = new TextLayout(dwFactory, dfLabel, tf, dirFilterRect.Width, dirFilterRect.Height))
			using (var tx = new SharpDX.Direct2D1.SolidColorBrush(rt, new Color4(0.95f,0.95f,0.98f,1f)))
			{
			    float txX = dirFilterRect.Left + (dirFilterRect.Width  - tl.Metrics.Width)  * 0.5f;
			    float txY = dirFilterRect.Top  + (dirFilterRect.Height - tl.Metrics.Height) * 0.5f;
			    rt.DrawTextLayout(new Vector2(txX, txY), tl, tx);
			}

			
		    // Main box
		    var dropRect = new RectangleF(boxX, boxY, boxW, boxH);
		    using (var geo = new RoundedRectangleGeometry(rt.Factory, new RoundedRectangle { Rect = dropRect, RadiusX = 4f, RadiusY = 4f }))
		    {
		        rt.FillGeometry(geo, brushBg);
		        rt.DrawGeometry(geo, brushBorder, 1.0f);
		    }
		
		    // -------------------------
		    // Selected label (ellipsized)
		    // -------------------------
		    // reserve chevron width = boxH
		    float labelLeft  = boxX + pad;
		    float labelRight = boxX + boxW - pad - boxH;
		    float usableLblW = Math.Max(1, labelRight - labelLeft);
		    var   labelRect  = new RectangleF(labelLeft, boxY + 1f, labelRight, boxY + boxH - 1f);
		
		    string raw = CatalogHasRealPlots() ? SelectedLabel : "Loading plots…";
		    // sanitize hard breaks/tabs that could force a new line
		    if (!string.IsNullOrEmpty(raw))
		        raw = raw.Replace('\r',' ').Replace('\n',' ').Replace('\t',' ');
		
		    string text = Ellipsize(raw, usableLblW);
		    using (var layout = new TextLayout(dwFactory, text, tf, usableLblW, boxH))
		    {
		        layout.WordWrapping = WordWrapping.NoWrap;
		
		        // clip to ensure no visual spill
		        rt.PushAxisAlignedClip(labelRect, AntialiasMode.Aliased);
		        rt.DrawTextLayout(new Vector2(labelLeft, boxY + (boxH - tf.FontSize) * 0.5f - 1f), layout, brushText);
		        rt.PopAxisAlignedClip();
		    }
		
		    // Ensure we’re still retrying while we render
		    if (!CatalogHasRealPlots())
		        EnsureCatalogScheduled();
		
		    // Chevron
		    float cx = boxX + boxW - boxH * 0.5f;
		    float cy = boxY + boxH * 0.5f;
		    var p1 = new Vector2(cx - 6f, cy - (isOpen ? -3f : 3f));
		    var p2 = new Vector2(cx,       cy + (isOpen ? -3f : 3f));
		    var p3 = new Vector2(cx + 6f,  cy - (isOpen ? -3f : 3f));
		    rt.DrawLine(p1, p2, brushText, 1.25f);
		    rt.DrawLine(p2, p3, brushText, 1.25f);
		
		    // refresh button box (height same as dropdown, square)
		    float rSize = boxH;                             // same height as main box
		    refreshRect = new RectangleF(boxX + boxW + 6f, boxY, rSize, boxH);
		
		    // bg and border
		    using (var geoR = new RoundedRectangleGeometry(rt.Factory,
		             new RoundedRectangle { Rect = refreshRect, RadiusX = 4f, RadiusY = 4f }))
		    {
		        rt.FillGeometry(geoR, hoverRefresh ? brushHover : brushOpenBg);
		        rt.DrawGeometry(geoR, brushBorder, 1.0f);
		    }
		
		    // simple "↻" text glyph as refresh icon
		    using (var tlR = new TextLayout(dwFactory, "↻", tf, rSize, boxH))
		    {
		        rt.DrawTextLayout(
		            new Vector2(refreshRect.Left + (rSize - tf.FontSize) * 0.5f,
		                        refreshRect.Top  + (boxH  - tf.FontSize) * 0.5f - 1f),
		            tlR, brushText);
		    }
		
		    // ----------------------
		    // Value box for selection
		    // ----------------------
		    double selVal = selectedLiveValue;     // <-- draw from data-thread cache
		    string selLbl = SelectedLabel;
		
		    string valText = double.IsNaN(selVal)
		        ? "—"
		        : (Instrument != null ? Instrument.MasterInstrument.FormatPrice(selVal) : selVal.ToString("G5"));
		
		    // Compose "Label: value" (shorten label to keep box tidy)
		    string shortLbl = selLbl;
		    int dot = shortLbl.IndexOf('•');
		    if (dot > 0) shortLbl = shortLbl.Substring(dot + 1).Trim(); // keep just the plot name after the dot
		    string boxLine = $"{shortLbl}: {valText}";
		
		    // Measure & draw the small box just to the right of the dropdown
		    using (var lay = new TextLayout(dwFactory, boxLine, valTf, 300f, 32f))
		    {
		        float bx = boxX + boxW + valBoxGap;
		        float by = boxY;
		        float bw = lay.Metrics.Width + 2f * valBoxPad;
		        float bh = lay.Metrics.Height + 2f * valBoxPad;
		
		        // rounded rect background
		        using (var geo = new RoundedRectangleGeometry(rt.Factory, new RoundedRectangle
		        {
		            Rect = new RectangleF(bx, by, bw, bh),
		            RadiusX = 4f, RadiusY = 4f
		        }))
		        {
		            rt.FillGeometry(geo, brushOpenBg);     // reuse open-bg
		            rt.DrawGeometry(geo, brushBorder, 1f); // reuse border brush
		        }
		
		        // text
		        rt.DrawTextLayout(new Vector2(bx + valBoxPad, by + valBoxPad), lay, brushText);
		    }
		
		    if (DebugMode)
		    {
		        string dbgLine1 = $"sel: {Ellipsize(dbgSel ?? "—", Math.Max(40, boxW - 20))}";
		        string dbgLine2 = $"count={dbgSeriesCount} idx={dbgIndexUsed} valid={dbgIsValid}";
		        string dbgLine3 = $"val={(double.IsNaN(selectedLiveValue) ? "NaN" : selectedLiveValue.ToString("G6"))}";
		        string dbgLine4 = string.IsNullOrEmpty(dbgNote) ? "" : dbgNote;
		
		        string block = dbgLine1 + "\n" + dbgLine2 + "\n" + dbgLine3 + (string.IsNullOrEmpty(dbgLine4) ? "" : "\n" + dbgLine4);
		
		        using (var lay = new TextLayout(dwFactory, block, valTf, 360f, 100f))
		        {
		            float bx = boxX + boxW + valBoxGap;
		            float by = boxY + 28f; // under the value box
		            float bw = lay.Metrics.Width + 2f * valBoxPad;
		            float bh = lay.Metrics.Height + 2f * valBoxPad;
		
		            using (var geo = new RoundedRectangleGeometry(rt.Factory, new RoundedRectangle
		            {
		                Rect = new RectangleF(bx, by, bw, bh),
		                RadiusX = 4f, RadiusY = 4f
		            }))
		            {
		                // color: green if valid+value ok, else amber/red
		                var ok = dbgIsValid && !double.IsNaN(selectedLiveValue);
		                var mbg = ok ? new Color4(0.09f, 0.18f, 0.09f, 0.9f)
		                            : new Color4(0.22f, 0.16f, 0.10f, 0.92f);
		
		                using (var br = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, mbg))
		                    rt.FillGeometry(geo, br);
		
		                rt.DrawGeometry(geo, brushBorder, 1.0f);
		            }
		
		            rt.DrawTextLayout(new Vector2(bx + valBoxPad, by + valBoxPad), lay, brushText);
		        }
		    }
		
		    // Dropdown list
		    if (isOpen)
		    {
		        var listRect = new RectangleF(boxX, boxY + boxH + 2f, boxW, itemH * VisibleRows);
		
		        // How many rows are actually available to paint?
		        int rowsAvailable = Math.Max(0, options.Count - listStart);
		        int rowsToDraw    = Math.Min(VisibleRows, rowsAvailable);
		
		        // Resize the visual list rect to exactly what we will draw
		        var drawRect = new RectangleF(listRect.Left, listRect.Top, listRect.Width, itemH * rowsToDraw);
		
		        // Hints (only if overflow exists)
		        if (options.Count > VisibleRows)
		        {
		            if (listStart > 0)
		            {
		                var t1 = new Vector2(drawRect.Right - 12f, drawRect.Top + 6f);
		                var t2 = new Vector2(drawRect.Right - 6f,  drawRect.Top + 12f);
		                var t3 = new Vector2(drawRect.Right - 18f, drawRect.Top + 12f);
		                rt.DrawLine(t1, t2, brushText, 1.0f);
		                rt.DrawLine(t2, t3, brushText, 1.0f);
		            }
		            int maxStart = Math.Max(0, options.Count - VisibleRows);
		            if (listStart < maxStart)
		            {
		                var b1 = new Vector2(drawRect.Right - 18f, drawRect.Bottom - 12f);
		                var b2 = new Vector2(drawRect.Right - 6f,  drawRect.Bottom - 12f);
		                var b3 = new Vector2(drawRect.Right - 12f, drawRect.Bottom - 6f);
		                rt.DrawLine(b1, b2, brushText, 1.0f);
		                rt.DrawLine(b2, b3, brushText, 1.0f);
		            }
		        }
		
		        // Background & border for the actual height
		        using (var geo = new RoundedRectangleGeometry(rt.Factory, new RoundedRectangle { Rect = drawRect, RadiusX = 4f, RadiusY = 4f }))
		        {
		            rt.FillGeometry(geo, brushOpenBg);
		            rt.DrawGeometry(geo, brushBorder, 1.0f);
		        }
		
		        // Paint only real rows
		        for (int row = 0; row < rowsToDraw; row++)
		        {
		            int optIndex = listStart + row;
		            float iy = drawRect.Top + row * itemH;
		            var rowRect = new RectangleF(drawRect.Left, iy, drawRect.Width, itemH);
		
		            if (hoverIndex == row) rt.FillRectangle(rowRect, brushHover);
		
		            string rowText = options[optIndex].Label ?? "(null)";
		            rowText = rowText.Replace('\r',' ').Replace('\n',' ').Replace('\t',' ');
		            rowText = Ellipsize(rowText, Math.Max(1, drawRect.Width - 2 * pad)); // keep your width logic
		
		            using (var tl = new TextLayout(dwFactory, rowText, tf, Math.Max(1, drawRect.Width - 2 * pad), itemH))
		            {
		                tl.WordWrapping = WordWrapping.NoWrap;
		
		                // tight clip to row bounds minus padding
		                var clip = new RectangleF(rowRect.Left + pad, rowRect.Top + 1f, rowRect.Right - pad, rowRect.Bottom - 1f);
		                rt.PushAxisAlignedClip(clip, AntialiasMode.Aliased);
		
		                rt.DrawTextLayout(new Vector2(rowRect.Left + pad, iy + (itemH - tf.FontSize) * 0.5f - 1f), tl, brushText);
		
		                rt.PopAxisAlignedClip();
		            }
		        }
		    }
		}


        private void RequestRedraw()
        {
            try
            {
                var now = DateTime.UtcNow;

                // Check redraw mode with stepped millisecond intervals
                switch (redrawMode)
                {
                    case RedrawMode.Disabled:
                        return; // No redraws at all

                    case RedrawMode.Minimal:
                        // Only redraw when dropdown state changes or essential updates
                        if (!isOpen && now < nextRedrawTime)
                            return;
                        // Minimal mode: 100ms closed, 50ms open (minimum thresholds)
                        var minimalThrottle = isOpen ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromMilliseconds(100);
                        if (now < nextRedrawTime)
                            return;
                        nextRedrawTime = now + minimalThrottle;
                        break;

                    case RedrawMode.Full:
                        // Immediate redraws - no throttling
                        break;

                    case RedrawMode.Throttled:
                    default:
                        // Throttled mode: 250ms closed, 30ms open
                        var throttle = isOpen ? TimeSpan.FromMilliseconds(30) : TimeSpan.FromMilliseconds(250);
                        if (now < nextRedrawTime)
                            return;
                        nextRedrawTime = now + throttle;
                        break;
                }

                var cc = ChartControl;
                if (cc == null)
                    return;

                var disp = cc.Dispatcher ?? System.Windows.Application.Current?.Dispatcher;
                if (disp != null)
                {
                    disp.InvokeAsync(() =>
                    {
                        try { cc.InvalidateVisual(); } catch { }
                    });
                }
                else
                {
                    cc.InvalidateVisual();
                }
            }
            catch { }
        }
        #endregion
		
		protected override void OnBarUpdate()
		{
		    dbgTick++;
			
			bool isPrimary = (BarsInProgress == 0);
			bool isNewBar  = isPrimary && IsFirstTickOfBar;
			
			if (isPrimary && Times != null && Times[0] != null && CurrentBar >= 0)
			    currBarTime = Times[0][0];
		
		// === TickElectrifier: Force faster redraws when in trade ===
		// This increases signal reading accuracy by updating the chart more frequently
		if (ElectrifierWhenInTrade && State == State.Realtime && thState == TradeState.InTrade && ChartControl != null)
		{
		    if (!electrifierPending && ElectrifierUpdateMs > 0)
		    {
		        // Time bucket throttling: only fire once per time bucket
		        long timeBucket = DateTime.Now.Ticks / (10000L * ElectrifierUpdateMs);
		        if (timeBucket != electrifierLastTF)
		        {
		            electrifierLastTF = timeBucket;
		            electrifierPending = true;
		            ChartControl.Dispatcher.InvokeAsync(() =>
		            {
		                try { RequestRedraw(); } catch { }
		                electrifierPending = false;
		            });
		        }
		    }
		}
			
		if (isNewBar)
			{
			    // === TICK BUFFER: Reset for new bar ===
			    // This MUST happen before any other processing to prevent signal carryover
			    int newBarIdx = GetPrimaryBarIndex();
			    ResetTickBufferForNewBar(newBarIdx);
			    
			    // Preserve the sign from the prior bar BEFORE clearing lastStableValue
			    // This prevents false crosses when a persistent signal continues into a new bar
			    if (!double.IsNaN(lastStableValue))
			        prevBarSign = ApproxPositive(lastStableValue) ? +1 : (ApproxNegative(lastStableValue) ? -1 : 0);
			    // else keep prevBarSign as-is (don't reset to 0 if we had no valid read)
			    
			    badZeroFrames   = 0;
			    lastStableValue = double.NaN;

			    currBarTime = Times[0][0];
			    sawSourceUpdateThisBar = false;

			    // force-clear the visible/mirrored slot so strategies don't read old values
			    if (SelectedMirror != null)
			        SelectedMirror[0] = 0.0;

			    if (State == State.Realtime)
			    {
			        selectedLiveValue = double.NaN;
			        lastStableValue   = double.NaN;
			    }

			    if (pendingEntryActive)
			    {
			        int barIndex = GetPrimaryBarIndex();
			        if (barIndex > pendingEntryBar)
			        {
			            bool bridgeSaysFlat = true;
			            try { bridgeSaysFlat = IsFlatViaBridge(); }
			            catch { bridgeSaysFlat = true; }

			            if (!bridgeSaysFlat)
			            {
			                // Trade is confirmed filled - mark it and clear pending status
			                tradeWasConfirmed = true;
			                tradeConfirmedBar = barIndex;
			                ClearPendingEntry();
			                if (DebugMode) Print($"[DDPlotReader] Trade CONFIRMED on bar {barIndex}");
			            }
			            else
			            {
			                // Bridge says flat - but only cancel if we've waited long enough AND send is enabled
			                int barsSinceSent = barIndex - pendingEntryBar;
			                if (sendEnabled && barsSinceSent >= PendingOrderTimeoutBars)
			                {
			                    if (TryRequestCloseToDestination("DDPlotReader pending entry timeout", force: true))
			                    {
			                        ClearPendingEntry();
			                        if (DebugMode) Print($"[DDPlotReader] Pending entry CANCELLED after {barsSinceSent} bars (timeout={PendingOrderTimeoutBars})");
			                    }
			                }
			                else if (!sendEnabled && barsSinceSent >= PendingOrderTimeoutBars)
			                {
			                    // SEND is off - just clear the pending state without sending cancel
			                    ClearPendingEntry();
			                    if (DebugMode) Print($"[DDPlotReader] Pending entry CLEARED (SEND off) after {barsSinceSent} bars");
			                }
			            }
			        }
			    }
			}

			if (Bars.IsFirstBarOfSession)
    			lastTriggerBar = -1;

		
		    // --- Predator-style bar-closed reconfirmation (BIP0 only) -----------------
		    // On first tick of a NEW bar on the primary series, re-read the PREVIOUS bar
		    // from the selected source by time alignment and, if valid, correct our cache.
		    if (BarsInProgress == 0 && IsFirstTickOfBar && selectedIndex >= 0 && selectedIndex < options.Count)
		    {
				
				if (State == State.Realtime && IsFirstTickOfBar)
				{
				    // Don’t let live panel carry old numbers into a fresh bar
				    selectedLiveValue = double.NaN;               // live slot will wait for a truly live read
				    lastStableValue   = double.NaN;               // optional: if you don’t want intra-bar cache, clear it here
				}
				
		        var opt0 = options[selectedIndex];
		        var src0 = opt0?.Source;
		        var s0   = SafeGetSeries(src0, opt0?.PlotIndex ?? -1);
		
		        try
		        {
		            if (src0 != null && s0 != null && Times != null && Times[0] != null && CurrentBar >= 1)
		            {
		                // map our previous primary-bar time to the source Bars index
		                DateTime prevTime = Times[0][1];
		                var barsArray = src0.BarsArray;
		                int idxPrev = (barsArray != null && barsArray.Length > 0 && barsArray[0] != null)
		                                ? barsArray[0].GetBar(prevTime)
		                                : -1;
		
		                if (idxPrev >= 0 && idxPrev < s0.Count)
		                {
		                    // Try both paths (GetValueAt + indexer fallback)
		                    double closed;
		                    bool ok = SafeTryGet(s0, idxPrev, out closed) || TryDirectSeriesFallback(s0, idxPrev, out closed);
		
		                    if (ok && !double.IsNaN(closed) && !double.IsInfinity(closed))
		                    {
		                        // Accept zero at bar-close only if we've seen a recent aligned zero run
		                        bool acceptClosed = !ApproxZero(closed) || zeroRun >= ZeroRunAccept || badZeroFrames >= ZeroConfirmFrames;
		
		                        // Also reject stale snapshots if we can time-check the source
		                        if (acceptClosed && (!TryGetSeriesTime(src0, idxPrev, out var tPrev) || IsFreshSnapshot(tPrev, idxPrev)))
		                        {
		                            lastStableValue  = closed;
		                            lastAcceptedIdx  = idxPrev;
		                            lastRawValue     = closed;
		                            lastAcceptedTime = tPrev;
		
		                            // Optionally reflect the confirmed closed value in the mirror’s barsAgo=1 slot
		                            if (SelectedMirror != null && CurrentBar >= 1)
		                                SelectedMirror[1] = closed;
		                        }
		                    }
		                }
		            }
		        }
		        catch { /* no-op; reconfirmation is best-effort */ }
		    }
		    // --------------------------------------------------------------------------
		
		    dbgSel = SelectedLabel;
		    double raw = double.NaN; dbgSeriesCount = -1; dbgNote = null; dbgIndexUsed = -1;
		
		    // Keep catalog warm but not spammy
		    if (CurrentBar % 10 == 0 && ScanAllowed())
		        try { ChartControl?.Dispatcher?.InvokeAsync(BuildCatalogSafe); } catch { }
		
		    // Live read from selected plot
		    if (selectedIndex >= 0 && selectedIndex < options.Count)
		    {
		        var opt = options[selectedIndex];
		        if (opt?.Source != null && opt.PlotIndex >= 0)
		        {
		            raw = ReadPlotValueLatestValid(opt.Source, opt.PlotIndex, 256,
		                                           out dbgIndexUsed, out dbgSeriesCount, out dbgNote);
		        }
		        else dbgNote = "no Source/PlotIndex";
		    }
		    else dbgNote = "invalid selectedIndex";
		
		    // --- HelloWin-style reliability filters -----------------------------------
		    double candidate = raw;
		
		    // A) Ghost-zero debounce (frame-based)
			if (!double.IsNaN(candidate) && ApproxZero(candidate)) {
			    if (isNewBar) {
			        // Do not debounce on the first tick of a fresh bar
			        badZeroFrames = ZeroConfirmFrames;  // treat as confirmed so 0 passes immediately
			    } else {
			        badZeroFrames++;
			        if (badZeroFrames < ZeroConfirmFrames) {
			            dbgNote = (dbgNote == null ? "" : (dbgNote + "; ")) + $"zero-debounce({badZeroFrames}/{ZeroConfirmFrames})";
			            candidate = double.NaN; // hold off this frame
			        }
			    }
			} else {
			    badZeroFrames = 0;
			}
		
		    // B) Spike quarantine vs previous stable value
		    if (!double.IsNaN(candidate) && !double.IsNaN(lastStableValue))
		    {
		        double dv = Math.Abs(candidate - lastStableValue);
		        if (!ApproxZero(lastStableValue) && dv > Math.Abs(lastStableValue) * SpikeMultiplier)
		        {
		            dbgNote = (dbgNote == null ? "" : (dbgNote + "; ")) + "spike-quarantined";
		            candidate = double.NaN;
		        }
		    }
		
			
			// === TICK BUFFER: Get previous sign BEFORE recording this tick ===
			// This ensures we compare against the actual previous state, not the current value
			int prevSignFromBuffer = GetPrevSignForCrossDetection();
			
			// C) Accept or (maybe) fall back to lastStable; but on a NEW BAR force a fresh read
			if (!double.IsNaN(candidate))
			{
			    lastStableValue = candidate;
			    lastRawValue    = raw;
			    lastAcceptedIdx = dbgIndexUsed;
			
			    try
			    {
			        if (selectedIndex >= 0 && selectedIndex < options.Count && options[selectedIndex]?.Source != null)
			            TryGetSeriesTime(options[selectedIndex].Source, dbgIndexUsed, out lastAcceptedTime);
			    }
			    catch { }
			
			    if (!ApproxZero(candidate)) zeroRun = 0;
			    deadFrames = 0;
			    
			    // === TICK BUFFER: Record this confirmed value AFTER getting prev sign ===
			    RecordTickValue(candidate, GetPrimaryBarIndex());
			}
			else
			{
			    deadFrames++;
			
			    // IMPORTANT: on first tick of a NEW bar, DO NOT reuse lastStableValue.
			    // This forces the reader to refresh instead of holding old values.
				// Only revive lastStable if (a) not first tick, AND (b) we have seen a *live* sample this bar
				if (!isNewBar && sawSourceUpdateThisBar && !double.IsNaN(lastStableValue))
				{
				    candidate = lastStableValue;
				    dbgNote = (dbgNote == null ? "" : (dbgNote + "; ")) + "fallback:lastStable(afterUpdate)";
				}
				else
				{
				    // Keep it empty/zero until the source actually emits a value for this bar
				    dbgNote = (dbgNote == null ? "" : (dbgNote + "; ")) + "no-fallback:no-source-update";
				}
			
			    if (deadFrames >= MaxDeadFrames)
			    {
			        try
			        {
			            ChartControl?.Dispatcher?.InvokeAsync(() =>
			            {
			                BuildCatalogSafe();
			                EnsureCatalogScheduled();
			            });
			        }
			        catch { }
			        deadFrames = 0;
			    }
			}

		    // --------------------------------------------------------------------------
		
		    // Mirror out + live cache
		    selectedLiveValue = candidate;
		    // SelectedMirror[0] = double.IsNaN(candidate) ? 0.0 : candidate;
			SelectedMirror[0] = (!double.IsNaN(candidate) && !double.IsInfinity(candidate)) ? candidate : 0.0;
			
			try
			{
			    bool needsRealtimeSignals = State == State.Realtime && (SendToTickHunter || SendToDDATM || SendToVgaBridge);
			    if (needsRealtimeSignals)
			    {
			        bool   isFinite = !double.IsNaN(candidate) && !double.IsInfinity(candidate);
			        double curr     = isFinite ? candidate : 0.0;
			        int    currSign = ApproxPositive(curr) ? +1 : (ApproxNegative(curr) ? -1 : 0);

			        if (SendToVgaBridge)
			            UpdateVgaFibFromSignal(currSign);

			        if (SendToTickHunter || SendToDDATM)
			        {
			            // === TICK BUFFER: Use prevSignFromBuffer for cross detection ===
			            bool crossUp    = (prevSignFromBuffer <= 0) && (currSign > 0);
			            bool crossDown  = (prevSignFromBuffer >= 0) && (currSign < 0);
			            bool newCross   = crossUp || crossDown;
			        
			            // Require confirmed signal this bar
			            bool hasSignalThisBar = tickBufferHasConfirmedSignal;
			
			            int  primaryBar      = CurrentBars[0];
			            bool allowThisBar    = !FireOncePerBar || (primaryBar != lastSentOnBar);
			            bool spacingSatisfied= MinBarsBetweenTriggers <= 0
			                                   || lastTriggerBar < 0
			                                   || (CurrentBar - lastTriggerBar) >= MinBarsBetweenTriggers;
			
			            bool isFlat = true;
			            try { isFlat = IsFlatViaBridge(); } catch { isFlat = true; }
			        
			            if (pendingEntryActive && !isFlat)
			            {
			                tradeWasConfirmed = true;
			                tradeConfirmedBar = CurrentBar;
			                ClearPendingEntry();
			                if (DebugMode) Print($"[DDPlotReader] Trade CONFIRMED (intra-bar) on bar {CurrentBar}");
			            }
			        
			            bool shouldReset = !pendingEntryActive 
			                               && thState != TradeState.Flat 
			                               && isFlat
			                               && (!tradeWasConfirmed || (tradeConfirmedBar >= 0 && CurrentBar > tradeConfirmedBar + 1));
			            if (shouldReset)
			                ResetAllTrackers("Destination is FLAT");
			
			            switch (thState)
			            {
			                case TradeState.Flat:
			                {
			                    bool bootstrap = (lastTriggerBar < 0) && (currSign != 0);
			
			                    if (AnyEndpointExists()
			                        && spacingSatisfied && allowThisBar
			                        && hasSignalThisBar
			                        && (newCross || bootstrap))
			                    {
			                        int dir = newCross ? (crossUp ? +1 : -1) : currSign;
			                        string reason = newCross
			                            ? $"DDPlotReader '{SelectedLabel}' prevSign={prevSignFromBuffer:+#;-#;0} curr={curr:G6} bar={tickBufferBar} ticks={tickBufferCount} (cross)"
			                            : $"DDPlotReader '{SelectedLabel}' curr={curr:G6} bar={tickBufferBar} (bootstrap)";
									if (CurrentBar != lastPopBar)
			                   			TryEnterToDestination(dir, reason);
			                    }
			                    else if (DebugMode && (newCross || bootstrap))
			                    {
			                        string blocked = !hasSignalThisBar ? "noSignalThisBar" : 
			                                         !spacingSatisfied ? "spacingThrottle" : 
			                                         !allowThisBar ? "oncePerBar" : "noEndpoint";
			                        Print($"[DDPlotReader] BLOCKED {(newCross?"cross":"bootstrap")} bar={tickBufferBar} ticks={tickBufferCount} prevSign={prevSignFromBuffer} currSign={currSign} reason={blocked}");
			                    }
			                    break;
			                }
			
			                case TradeState.InTrade:
			                {
			                    if (hasSignalThisBar && newCross && (lastSentSign != 0) && (currSign != lastSentSign))
			                    {
			                        TryRequestCloseToDestination($"DDPlotReader opposite cross {lastSentSign:+#;-#;0}->{currSign:+#;-#;0}");
			                    }
			                    break;
			                }
			
			                case TradeState.WaitingFlat:
			                {
			                    break;
			                }
			            }
			
			            if (isFinite) lastStableValue = curr;
			        }
			    }
			}
			catch { /* non-fatal */ }



		    if (DebugMode && (dbgTick % dbgPrintEvery == 0 || !double.IsNaN(raw)))
		    {
		        double toPrint = double.IsNaN(candidate) ? double.NaN : candidate;
				if (toPrint!=0) {
		        	Dbg($"tick={dbgTick} sel='{dbgSel}' idxUsed={dbgIndexUsed} count={dbgSeriesCount} " +
		            $"val={(double.IsNaN(toPrint) ? "NaN" : toPrint.ToString("G6"))} note={dbgNote}");
				}
		    }
		
		    RequestRedraw();
		}

    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private DimDim.DDPlotReader[] cacheDDPlotReader;
		public DimDim.DDPlotReader DDPlotReader(bool debugMode, bool sendToTickHunter, string bridgeEndpoint, bool sendToVgaBridge, string vgaBridgeEndpoint, bool fireOncePerBar, int minBarsBetweenTriggers, bool electrifierWhenInTrade, int electrifierUpdateMs, int pendingOrderTimeoutBars, bool sendToDDATM, string dDATMEndpoint, bool useDDATMFlatCheck, bool sendToDDTP, string dDTPEndpoint, int tLX, int tLY, int widthPx, string redrawModeStr, bool sendEnabledDefault)
		{
			return DDPlotReader(Input, debugMode, sendToTickHunter, bridgeEndpoint, sendToVgaBridge, vgaBridgeEndpoint, fireOncePerBar, minBarsBetweenTriggers, electrifierWhenInTrade, electrifierUpdateMs, pendingOrderTimeoutBars, sendToDDATM, dDATMEndpoint, useDDATMFlatCheck, sendToDDTP, dDTPEndpoint, tLX, tLY, widthPx, redrawModeStr, sendEnabledDefault);
		}

		public DimDim.DDPlotReader DDPlotReader(ISeries<double> input, bool debugMode, bool sendToTickHunter, string bridgeEndpoint, bool sendToVgaBridge, string vgaBridgeEndpoint, bool fireOncePerBar, int minBarsBetweenTriggers, bool electrifierWhenInTrade, int electrifierUpdateMs, int pendingOrderTimeoutBars, bool sendToDDATM, string dDATMEndpoint, bool useDDATMFlatCheck, bool sendToDDTP, string dDTPEndpoint, int tLX, int tLY, int widthPx, string redrawModeStr, bool sendEnabledDefault)
		{
			if (cacheDDPlotReader != null)
				for (int idx = 0; idx < cacheDDPlotReader.Length; idx++)
					if (cacheDDPlotReader[idx] != null && cacheDDPlotReader[idx].DebugMode == debugMode && cacheDDPlotReader[idx].SendToTickHunter == sendToTickHunter && cacheDDPlotReader[idx].BridgeEndpoint == bridgeEndpoint && cacheDDPlotReader[idx].SendToVgaBridge == sendToVgaBridge && cacheDDPlotReader[idx].VgaBridgeEndpoint == vgaBridgeEndpoint && cacheDDPlotReader[idx].FireOncePerBar == fireOncePerBar && cacheDDPlotReader[idx].MinBarsBetweenTriggers == minBarsBetweenTriggers && cacheDDPlotReader[idx].ElectrifierWhenInTrade == electrifierWhenInTrade && cacheDDPlotReader[idx].ElectrifierUpdateMs == electrifierUpdateMs && cacheDDPlotReader[idx].PendingOrderTimeoutBars == pendingOrderTimeoutBars && cacheDDPlotReader[idx].SendToDDATM == sendToDDATM && cacheDDPlotReader[idx].DDATMEndpoint == dDATMEndpoint && cacheDDPlotReader[idx].UseDDATMFlatCheck == useDDATMFlatCheck && cacheDDPlotReader[idx].SendToDDTP == sendToDDTP && cacheDDPlotReader[idx].DDTPEndpoint == dDTPEndpoint && cacheDDPlotReader[idx].TLX == tLX && cacheDDPlotReader[idx].TLY == tLY && cacheDDPlotReader[idx].WidthPx == widthPx && cacheDDPlotReader[idx].RedrawModeStr == redrawModeStr && cacheDDPlotReader[idx].SendEnabledDefault == sendEnabledDefault && cacheDDPlotReader[idx].EqualsInput(input))
						return cacheDDPlotReader[idx];
			return CacheIndicator<DimDim.DDPlotReader>(new DimDim.DDPlotReader(){ DebugMode = debugMode, SendToTickHunter = sendToTickHunter, BridgeEndpoint = bridgeEndpoint, SendToVgaBridge = sendToVgaBridge, VgaBridgeEndpoint = vgaBridgeEndpoint, FireOncePerBar = fireOncePerBar, MinBarsBetweenTriggers = minBarsBetweenTriggers, ElectrifierWhenInTrade = electrifierWhenInTrade, ElectrifierUpdateMs = electrifierUpdateMs, PendingOrderTimeoutBars = pendingOrderTimeoutBars, SendToDDATM = sendToDDATM, DDATMEndpoint = dDATMEndpoint, UseDDATMFlatCheck = useDDATMFlatCheck, SendToDDTP = sendToDDTP, DDTPEndpoint = dDTPEndpoint, TLX = tLX, TLY = tLY, WidthPx = widthPx, RedrawModeStr = redrawModeStr, SendEnabledDefault = sendEnabledDefault }, input, ref cacheDDPlotReader);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.DimDim.DDPlotReader DDPlotReader(bool debugMode, bool sendToTickHunter, string bridgeEndpoint, bool sendToVgaBridge, string vgaBridgeEndpoint, bool fireOncePerBar, int minBarsBetweenTriggers, bool electrifierWhenInTrade, int electrifierUpdateMs, int pendingOrderTimeoutBars, bool sendToDDATM, string dDATMEndpoint, bool useDDATMFlatCheck, bool sendToDDTP, string dDTPEndpoint, int tLX, int tLY, int widthPx, string redrawModeStr, bool sendEnabledDefault)
		{
			return indicator.DDPlotReader(Input, debugMode, sendToTickHunter, bridgeEndpoint, sendToVgaBridge, vgaBridgeEndpoint, fireOncePerBar, minBarsBetweenTriggers, electrifierWhenInTrade, electrifierUpdateMs, pendingOrderTimeoutBars, sendToDDATM, dDATMEndpoint, useDDATMFlatCheck, sendToDDTP, dDTPEndpoint, tLX, tLY, widthPx, redrawModeStr, sendEnabledDefault);
		}

		public Indicators.DimDim.DDPlotReader DDPlotReader(ISeries<double> input , bool debugMode, bool sendToTickHunter, string bridgeEndpoint, bool sendToVgaBridge, string vgaBridgeEndpoint, bool fireOncePerBar, int minBarsBetweenTriggers, bool electrifierWhenInTrade, int electrifierUpdateMs, int pendingOrderTimeoutBars, bool sendToDDATM, string dDATMEndpoint, bool useDDATMFlatCheck, bool sendToDDTP, string dDTPEndpoint, int tLX, int tLY, int widthPx, string redrawModeStr, bool sendEnabledDefault)
		{
			return indicator.DDPlotReader(input, debugMode, sendToTickHunter, bridgeEndpoint, sendToVgaBridge, vgaBridgeEndpoint, fireOncePerBar, minBarsBetweenTriggers, electrifierWhenInTrade, electrifierUpdateMs, pendingOrderTimeoutBars, sendToDDATM, dDATMEndpoint, useDDATMFlatCheck, sendToDDTP, dDTPEndpoint, tLX, tLY, widthPx, redrawModeStr, sendEnabledDefault);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.DimDim.DDPlotReader DDPlotReader(bool debugMode, bool sendToTickHunter, string bridgeEndpoint, bool sendToVgaBridge, string vgaBridgeEndpoint, bool fireOncePerBar, int minBarsBetweenTriggers, bool electrifierWhenInTrade, int electrifierUpdateMs, int pendingOrderTimeoutBars, bool sendToDDATM, string dDATMEndpoint, bool useDDATMFlatCheck, bool sendToDDTP, string dDTPEndpoint, int tLX, int tLY, int widthPx, string redrawModeStr, bool sendEnabledDefault)
		{
			return indicator.DDPlotReader(Input, debugMode, sendToTickHunter, bridgeEndpoint, sendToVgaBridge, vgaBridgeEndpoint, fireOncePerBar, minBarsBetweenTriggers, electrifierWhenInTrade, electrifierUpdateMs, pendingOrderTimeoutBars, sendToDDATM, dDATMEndpoint, useDDATMFlatCheck, sendToDDTP, dDTPEndpoint, tLX, tLY, widthPx, redrawModeStr, sendEnabledDefault);
		}

		public Indicators.DimDim.DDPlotReader DDPlotReader(ISeries<double> input , bool debugMode, bool sendToTickHunter, string bridgeEndpoint, bool sendToVgaBridge, string vgaBridgeEndpoint, bool fireOncePerBar, int minBarsBetweenTriggers, bool electrifierWhenInTrade, int electrifierUpdateMs, int pendingOrderTimeoutBars, bool sendToDDATM, string dDATMEndpoint, bool useDDATMFlatCheck, bool sendToDDTP, string dDTPEndpoint, int tLX, int tLY, int widthPx, string redrawModeStr, bool sendEnabledDefault)
		{
			return indicator.DDPlotReader(input, debugMode, sendToTickHunter, bridgeEndpoint, sendToVgaBridge, vgaBridgeEndpoint, fireOncePerBar, minBarsBetweenTriggers, electrifierWhenInTrade, electrifierUpdateMs, pendingOrderTimeoutBars, sendToDDATM, dDATMEndpoint, useDDATMFlatCheck, sendToDDTP, dDTPEndpoint, tLX, tLY, widthPx, redrawModeStr, sendEnabledDefault);
		}
	}
}

#endregion
