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
using System.Globalization;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.Gui.Tools;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.NinjaScript;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    // -------- ATM TEMPLATE SELECTOR TYPE CONVERTER -----------
    public class AtmTemplateSelectorConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) => true;
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) => true;

        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
        {
            string atmFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "NinjaTrader 8", "templates", "AtmStrategy");
            List<string> templates = new List<string> { "" }; // Allow blank/default

            if (Directory.Exists(atmFolder))
            {
                foreach (var file in Directory.GetFiles(atmFolder, "*.xml"))
                    templates.Add(Path.GetFileNameWithoutExtension(file));
            }
            return new StandardValuesCollection(templates);
        }
    }
	
	    public static class DDATMBridge
    {
        private static readonly object _gate = new object();
        private static readonly Dictionary<string, WeakReference<DDATM>> Endpoints =
            new Dictionary<string, WeakReference<DDATM>>(StringComparer.OrdinalIgnoreCase);

        public static bool Register(string endpoint, DDATM instance)
        {
            if (string.IsNullOrWhiteSpace(endpoint) || instance == null) return false;
            lock (_gate)
            {
                Endpoints[endpoint] = new WeakReference<DDATM>(instance);
                return true;
            }
        }

        public static void Unregister(string endpoint, DDATM instance)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return;
            lock (_gate)
            {
                if (Endpoints.TryGetValue(endpoint, out var wr) && wr != null)
                {
                    if (!wr.TryGetTarget(out var existing) || existing == instance)
                        Endpoints.Remove(endpoint);
                }
            }
        }

        public static bool Exists(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return false;
            lock (_gate)
            {
                return Endpoints.TryGetValue(endpoint, out var wr) && wr != null && wr.TryGetTarget(out var _);
            }
        }

        public static bool Send(string endpoint, DDATM.DDCommand cmd, string reason = null, string arg = null)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return false;
            DDATM target = null;
            lock (_gate)
            {
                if (Endpoints.TryGetValue(endpoint, out var wr) && wr != null)
                    wr.TryGetTarget(out target);
            }
            if (target == null) return false;

            try
            {
                target.ApiExecute(cmd, reason, arg);
                return true;
            }
            catch { return false; }
        }

        public static int Broadcast(DDATM.DDCommand cmd, string reason = null, string arg = null)
        {
            int sent = 0;
            List<DDATM> targets = new List<DDATM>();
            lock (_gate)
            {
                foreach (var kv in Endpoints)
                    if (kv.Value != null && kv.Value.TryGetTarget(out var t) && t != null) targets.Add(t);
            }
            foreach (var t in targets)
            {
                try { t.ApiExecute(cmd, reason, arg); sent++; } catch { }
            }
            return sent;
        }
		
		public static bool SendAck(string endpoint, DDATM.DDCommand cmd, string reason = null, string arg = null)
		{
		    if (string.IsNullOrWhiteSpace(endpoint)) return false;
		    DDATM target = null;
		    lock (_gate)
		    {
		        if (Endpoints.TryGetValue(endpoint, out var wr) && wr != null)
		            wr.TryGetTarget(out target);
		    }
		    if (target == null) return false;
		
		    try
		    {
		        return target.ApiExecuteAck(cmd, reason, arg);
		    }
		    catch { return false; }
		}
		
		public static bool TryQueryIsAtmFlat(string endpoint, out bool isFlat)
		{
		    isFlat = true;
		    if (string.IsNullOrWhiteSpace(endpoint)) return false;
		    DDATM target = null;
		    lock (_gate)
		    {
		        if (Endpoints.TryGetValue(endpoint, out var wr) && wr != null)
		            wr.TryGetTarget(out target);
		    }
		    if (target == null) return false;
		
		    try
		    {
		        return target.TryQueryIsAtmFlat(out isFlat);
		    }
		    catch
		    {
		        isFlat = true;
		        return false;
		    }
		}

		
    }

    public class DDATM : Strategy
    {
	
		public enum TradeModeType { ATM, Managed }
		
		
		public enum DDCommand
		{
		    Long,          // enter long (ATM or unmanaged based on your TradeMode)
		    Short,         // enter short
		    Close,         // close active ATM(s)
		    Panic,         // panic close + disable
		    Enable,        // enable strategy button
		    Disable,       // disable strategy button
		    SetTemplate,   // optional: set ATM template by name
		}

		[NinjaScriptProperty]
		[Display(Name = "Trade Mode", Order = 800)]
		public TradeModeType TradeMode { get; set; } = TradeModeType.ATM;

		
		// ----------- GUI & PANEL SUPPORT FIELDS -----------
		private System.Windows.Controls.Button strategyBtn;
		private Button enableLongBtn, enableShortBtn;
		private System.Windows.Controls.Button longBtn, shortBtn, shortBtn2, closeBtn, panicBtn;
		private NinjaTrader.Gui.Chart.Chart chartWindow;
		private System.Windows.Controls.Grid chartTraderGrid, chartTraderButtonsGrid, lowerButtonsGrid;
		private bool panelActive;
		
		// PnL/status display
		private bool canTradeOK = true;
		private double strategySessionStartPnL = 0;
		private DateTime strategySessionDate = DateTime.MinValue;
		
		private double sessionMaxDrawdown = 0;
		private double sessionPeakPnL = 0;
		private double sessionTroughPnL = 0;
		private DateTime sessionDrawdownDate = DateTime.MinValue;

		// Panel display settings (add to properties section)
		[NinjaScriptProperty]
		[Display(GroupName = "STATUS PANEL", Name = "Show STATUS PANEL", Order = 0)]
		public bool showPnl { get; set; }
		
		[NinjaScriptProperty]
		[Display(Name="STATUS PANEL Position", Description = "PNL Position", Order = 1, GroupName = "STATUS PANEL")]
		public TextPosition PositionPnl { get; set; }
		
		[XmlIgnore()]
		[Display(Name = "STATUS PANEL Color", Order = 2, GroupName = "STATUS PANEL")]
		public Brush colorPnl { get; set; }
		
		// Serialize our Color object
		[Browsable(false)]
		public string colorPnlSerialize
		{
			get { return Serialize.BrushToString(colorPnl); }
			set { colorPnl = Serialize.StringToBrush(value); }
		}



        // ATM tracking
        private string longAtmStrategyId = string.Empty;
        private string longOrderId = string.Empty;
        private string shortAtmStrategyId = string.Empty;
        private string shortOrderId = string.Empty;
        private bool isLongAtmCreated;
        private bool isShortAtmCreated;

        // Session/bar counters for delay
        private int barsSinceSessionStart = 0;
        private DateTime lastBarDate = Core.Globals.MinDate;

        // Daily kill switch, unrealized/trailing
        private double peakUnrealized = 0;
        private double sessionStartPnL = 0;
        private DateTime lastSessionDate = Core.Globals.MinDate;
		
		private int[] trendSignalBuffer = new int[2];
		private double[] barPressureBuffer = new double[2];
		
		// At the top of your strategy
		private int consecutiveTrendBars = 0;
		
		private double lastAtmEntryPrice = 0;
		private DateTime lastAtmEntryTime = Core.Globals.MinDate;
		private bool lastAtmWasLong = false;
		private string lastAtmEntryOrderId = "";
		
		private double cachedExternalComposite = double.NaN;
		private double cachedChopComposite     = double.NaN;
		
		private double cumulativeAtmRealizedPnL = 0;
		private double dailyCumulativeAtmRealizedPnL = 0;
		private DateTime lastPnLUpdateDate = DateTime.MinValue;
		

		private double lastAtmUnrealizedPnL = 0; // new variable to persist unrealized PnL
		private List<double> realizedPnLPerTrade = new List<double>();
		private double lastTradeRealizedPnL = 0;
		
		private int lastEntryBar = -1;
		
		private int currentTrendDirection = 0; // 1 = long, -1 = short, 0 = neutral
		private int tradesThisTrend = 0;
		
		private string entrySignalLong1 = "Long1";
		private string entrySignalLong2 = "Long2";
		private string entrySignalLong3 = "Long3";
		private string entrySignalLong4 = "Long4";
		private string entrySignalLong5 = "Long5";
		private string entrySignalLong6 = "Long6";
		private string entrySignalShort1 = "Short1";
		private string entrySignalShort2 = "Short2";
		private string entrySignalShort3 = "Short3";
		private string entrySignalShort4 = "Short4";
		private string entrySignalShort5 = "Short5";
        private string entrySignalShort6 = "Short6";
		
		private static string DirStr(int s) => s == 1 ? "Long" : s == -1 ? "Short" : "Neutral";
		
		// --- CONFIG: tweak if you like ---
		private static readonly string[] LongSignals  = { "LongT1", "LongT2", "LongT3", "LongDom", "LongRun" };
		private static readonly string[] ShortSignals = { "ShortT1","ShortT2","ShortT3","ShortDom","ShortRun" };
		// 2-tick spacing for the first 3, then keep spacing for Domino and Runner
		private static readonly int[] EntryOffsetsTicks = { 1, 2, 3, 4, 5 }; // for Long: below price; for Short: above price
		// Geometric “log-like” weights (16,8,4,2,1) normalized to EntryQty
		private static readonly int[] QtyWeights = { 16, 8, 4, 2, 1 };

		
		// Gating counters
		private int barsSinceAnyEntry = int.MaxValue;
		private int barsSinceAnyExit  = int.MaxValue;
		private bool prevFlat = true;   // tracks flat/non-flat across bars
		
		// Render-side exit catcher
		private bool lastRenderFlat = true;
		
		// --- Bridge endpoint like TickHunter ---
		[NinjaScriptProperty, Display(Name="Bridge Endpoint", GroupName="API", Order=0)]
		public string BridgeEndpoint { get; set; } = "ddatm-default";
		
		// optional: keep HUD/reader but disable entries
		[NinjaScriptProperty, Display(Name="Disable External Entry Signals", GroupName="API", Order=1)]
		public bool DisableExternalEntrySignals { get; set; } = true;
		
		// Track if we’re registered
		private bool bridgeRegistered = false;

		
		[NinjaScriptProperty]
		[Display(Name = "Max Trades Per Trend Direction", Order = 105)]
		public int MaxTradesPerTrend { get; set; } = 3; // default 2 per trend direction
		
		
		
		
        // === STRATEGY PARAMETERS ===
        [NinjaScriptProperty]
        [Display(Name = "Enable Long", Order = 0)]
        public bool enableLong { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Enable Short", Order = 1)]
        public bool enableShort { get; set; }
		
		[NinjaScriptProperty]
		[Range(1, 6)] // Set max as you like (6 is plenty for most)
		[Display(Name = "Non-ATM: Number of Targets", Order = 910)]
		public int NonATM_NumTargets { get; set; } = 2;
		
		[NinjaScriptProperty]
		[Range(1, 100)]
		[Display(Name = "Non-ATM: Quantity", Order = 901)]
		public int NonATM_Quantity { get; set; } = 2;
		
		// [NinjaScriptProperty][Display(Name = "Non-ATM: Target 1 (ticks)", Order = 911)] public int NonATM_Target1Ticks { get; set; } = 10;
		// [NinjaScriptProperty][Display(Name = "Non-ATM: Target 2 (ticks)", Order = 912)] public int NonATM_Target2Ticks { get; set; } = 20;
		// [NinjaScriptProperty][Display(Name = "Non-ATM: Target 3 (ticks)", Order = 913)] public int NonATM_Target3Ticks { get; set; } = 30;
		// [NinjaScriptProperty][Display(Name = "Non-ATM: Target 4 (ticks)", Order = 914)] public int NonATM_Target4Ticks { get; set; } = 40;
		// [NinjaScriptProperty][Display(Name = "Non-ATM: Target 5 (ticks)", Order = 915)] public int NonATM_Target5Ticks { get; set; } = 50;
		// [NinjaScriptProperty][Display(Name = "Non-ATM: Target 6 (ticks)", Order = 916)] public int NonATM_Target6Ticks { get; set; } = 60;
		
		[NinjaScriptProperty][Display(Name = "Non-ATM: Target 1 (ticks)", Order = 911)] public int NonATM_Target1Ticks { get; set; } = 2;
		[NinjaScriptProperty][Display(Name = "Non-ATM: Target 2 (ticks)", Order = 912)] public int NonATM_Target2Ticks { get; set; } = 4;
		[NinjaScriptProperty][Display(Name = "Non-ATM: Target 3 (ticks)", Order = 913)] public int NonATM_Target3Ticks { get; set; } = 8;
		[NinjaScriptProperty][Display(Name = "Non-ATM: Target 4 (ticks)", Order = 914)] public int NonATM_Target4Ticks { get; set; } = 16;
		[NinjaScriptProperty][Display(Name = "Non-ATM: Target 5 (ticks)", Order = 915)] public int NonATM_Target5Ticks { get; set; } = 24;
		[NinjaScriptProperty][Display(Name = "Non-ATM: Target 6 (ticks)", Order = 916)] public int NonATM_Target6Ticks { get; set; } = 333;
		
		[NinjaScriptProperty]
		[Range(1, 100)]
		[Display(Name = "Non-ATM: Stop Loss (ticks)", Order = 902)]
		public int NonATM_StopLossTicks { get; set; } = 24; // prev 15

		[NinjaScriptProperty]
		[Range(1, 50)]
		[Display(Name = "Non-ATM: Break Even Trigger (ticks)", Order = 904)]
		public int NonATM_BreakEvenTriggerTicks { get; set; } = 10;
		
		[NinjaScriptProperty]
		[Range(0, 10)]
		[Display(Name = "Non-ATM: Break Even Buffer (ticks)", Order = 905)]
		public int NonATM_BreakEvenBufferTicks { get; set; } = 0;
		
		[NinjaScriptProperty]
		[Range(1, 50)]
		[Display(Name = "Non-ATM: Trailing Trigger (ticks)", Order = 906)]
		public int NonATM_TrailTriggerTicks { get; set; } = 14;
		
		[NinjaScriptProperty]
		[Range(1, 50)]
		[Display(Name = "Non-ATM: Trailing Step (ticks)", Order = 907)]
		public int NonATM_TrailStepTicks { get; set; } = 16;


        [TypeConverter(typeof(AtmTemplateSelectorConverter))]
        [NinjaScriptProperty]
        [Display(Name = "ATM Strategy Template", Order = 2)]
        public string AtmStrategyTemplate { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Skip Trade Start Time", Order = 3)]
        public DateTime SkipTradeStartTime { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Skip Trade End Time", Order = 4)]
        public DateTime SkipTradeEndTime { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Delay X Bars Auto ON", Order = 20)]
        public int DelayXBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Morning Safety (8:30am EST)", Order = 21)]
        public bool UseMorningSafety { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Afternoon Safety (4:00pm EST)", Order = 22)]
        public bool UseAfternoonSafety { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "OverFill Safety Cancel", Order = 23)]
        public bool UseOverfillCancel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Set Daily Kill Switch", Order = 24)]
        public bool UseDailyKillSwitch { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Kill Switch Profit Limit", Order = 25)]
        public double DailyProfitLimit { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Kill Switch Loss Limit", Order = 26)]
        public double DailyLossLimit { get; set; }
		
		[NinjaScriptProperty]
		[Display(Name = "Kill Switch Max Consecutive Win Amount", Order = 30)]
		public double MaxConsecutiveWinAmount { get; set; }
		
		[NinjaScriptProperty]
		[Display(Name = "Kill Switch Max Consecutive Losses", Order = 31)]
		public int MaxConsecutiveLosses { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Set 'UnRealized' Loss Per Trade", Order = 27)]
        public bool UseUnrealizedKillSwitch { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "'Unrealized' Loss Limit", Order = 28)]
        public double UnrealizedLossLimit { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trail Unrealized Loss Limit", Order = 29)]
        public double TrailingUnrealizedLossLimit { get; set; }
		
		[NinjaScriptProperty]
		[Display(Name = "Min Trend Bars Before Entry", Order = 99)]
		public int minTrendBars { get; set; } = 1;
		
		[NinjaScriptProperty]
		[Display(Name = "Breakout Lookback", Order = 101)]
		public int BreakoutLookback { get; set; } = 1;
		
		[NinjaScriptProperty]
		[Display(Name = "Breakout Min Range (ticks)", Order = 102)]
		public double BreakoutMinRange { get; set; } = 5.0;


		[NinjaScriptProperty]
		[Display(Name = "Enable Splunk Logging", Order = 200)]
		public bool EnableSplunkLogging { get; set; } = true;
		
		[NinjaScriptProperty]
		[Display(Name = "Enable Debug Output Print", Order = 201)]
		public bool EnableDebugPrint { get; set; } = true;
		
		[NinjaScriptProperty]
		[Display(Name = "DDMTTR Strict Consensus", Order = 98)]
		public bool DdmttrStrict { get; set; } = false;
		
		
		[NinjaScriptProperty]
		[Display(Name = "Use BuyDom/SellDom Entries", Order = 120)]
		public bool UseBuyDomEntries { get; set; } = true;
		
		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "Buy % Threshold", Order = 121)]
		public double BuyPctThreshold { get; set; } = 65.0;
		
		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "Sell % Threshold", Order = 122)]
		public double SellPctThreshold { get; set; } = 75.0;
		
		[NinjaScriptProperty]
		[Display(Name = "BSV3 Use Percentage Signal", Order = 123)]
		public bool Bsv3UsePercentageSignal { get; set; } = false;
		
		[NinjaScriptProperty]
		[Display(Name = "Include D/H/M in Extra", Order = 901, GroupName = "Logging")]
		public bool IncludeDhmBreakdown { get; set; } = true;
		
		[NinjaScriptProperty]
		[Display(Name = "Use Local Time For D/H/M", Order = 902, GroupName = "Logging")]
		public bool ExtraUseLocalTime { get; set; } = false;

		// --- Entry sizing ---
		[NinjaScriptProperty]
		[Range(1, 200)]
		[Display(Name = "Managed/Unmanaged: Entry Quantity", Order = 880)]
		public int EntryQty { get; set; } = 2;
		
		[NinjaScriptProperty]
		[Range(1, 6)]
		[Display(Name = "Managed: Number of Targets", Order = 881)]
		public int Managed_NumTargets { get; set; } = 2;
		
		// --- Bar Gating ---
		[NinjaScriptProperty]
		[Range(0, 1000)]
		[Display(Name = "Bars Since Entry", Order = 96)]
		public int BarsSinceEntry { get; set; } = 4;
		
		[NinjaScriptProperty]
		[Range(0, 1000)]
		[Display(Name = "Bars Since Exit", Order = 97)]
		public int BarsSinceExit { get; set; } = 4;

		
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "DDATM";
                Calculate = Calculate.OnEachTick;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsInstantiatedOnEachOptimizationIteration = false;

                enableLong = false;
                enableShort = false;
                AtmStrategyTemplate = ""; // Set in UI
                SkipTradeStartTime = DateTime.Parse("15:00", System.Globalization.CultureInfo.InvariantCulture);
                SkipTradeEndTime = DateTime.Parse("17:00", System.Globalization.CultureInfo.InvariantCulture);

                DelayXBars = 0;
                UseMorningSafety = true;
                UseAfternoonSafety = true;
                UseOverfillCancel = true;

                UseDailyKillSwitch = false;
                DailyProfitLimit = 25;
                DailyLossLimit = 99999;
                UseUnrealizedKillSwitch = false;
                UnrealizedLossLimit = 250;
                TrailingUnrealizedLossLimit = 0;
				MaxConsecutiveWinAmount = 25;   // example, set your default
    			MaxConsecutiveLosses = 5;        // example, set your default
			
    			showPnl = true;
    			PositionPnl = TextPosition.TopRight;
    			colorPnl = Brushes.White;
				
				BridgeEndpoint = "ddatm-default";
        		DisableExternalEntrySignals = true;
            }
			else if (State == State.Configure)
            {
				    IsUnmanaged = false;
			}
            else if (State == State.DataLoaded)
            {
	
				// Nop
            }
			else if (State == State.Realtime)
			{
				ChartControl.Dispatcher.InvokeAsync(() => CreateWPFControls());
			    ChartControl.Dispatcher.InvokeAsync(() => {
			        CreateWPFControls();
			    });
				
				NinjaTrader.NinjaScript.Strategies.DDATMBridge.Register(BridgeEndpoint, this);
        		bridgeRegistered = true;
				
				
			}
			else if (State == State.Terminated)
			{
				// Chart Trader Buttons Dispose (full code to be added next)
			    ChartControl?.Dispatcher.InvokeAsync(() => DisposeWPFControls());
				
			if (bridgeRegistered)
            	NinjaTrader.NinjaScript.Strategies.DDATMBridge.Unregister(BridgeEndpoint, this);
        		bridgeRegistered = false;
				
			}


        }
		
		// Directional gating in ONE place
		private bool CanEnterLongNow()  => canTradeOK && enableLong  && IsAtmFlat();
		private bool CanEnterShortNow() => canTradeOK && enableShort && IsAtmFlat();

		
		public void ApiExecute(DDCommand cmd, string reason = null, string arg = null)
		{
		    try
		    {
		        // Marshal to UI if needed; we’re touching NT UI/ATM from strategy thread is ok, but
		        // if you later touch WPF controls, keep Dispatcher usage.
		        switch (cmd)
		        {
		            case DDCommand.Long:
		                if (CanEnterLongNow()) EnterLongATM();   // you already have this UI handler wired to longBtn
		                break;
		
		            case DDCommand.Short:
		                if (CanEnterShortNow()) EnterShortATM();  // you already have this
		                break;
		
		            case DDCommand.Close:
		                KillAllATMs();                                   // you already have this helper
		                break;
		
		            case DDCommand.Panic:
		                KillAllATMs();
		                canTradeOK = false;
		                break;
		
		            case DDCommand.Enable:
		                canTradeOK = true;
		                break;
		
		            case DDCommand.Disable:
		                canTradeOK = false;
		                break;
		
		            case DDCommand.SetTemplate:
		                if (!string.IsNullOrWhiteSpace(arg))
		                    AtmStrategyTemplate = arg;                   // will be used on next entry
		                break;
		        }
		
		        // keep your existing GUI sync:
		        CleanupAtmStateAndButtons(); // updates buttons + reads ATM flatness etc. :contentReference[oaicite:2]{index=2}
		        UpdateStrategyButtonStyle();
		        if (EnableDebugPrint)
		            Print($"[DDATM.API] {cmd} reason='{reason}' arg='{arg}'");
		    }
		    catch (Exception ex)
		    {
		        Print($"[DDATM.API] error: {ex.Message}");
		    }
		}
		
		// Returns true ONLY if an action was actually taken (e.g., an entry placed, a close issued).
		public bool ApiExecuteAck(DDCommand cmd, string reason = null, string arg = null)
		{
		    try
		    {
		        switch (cmd)
		        {
		            case DDCommand.Long:
		                if (CanEnterLongNow())
		                {
		                    EnterLongATM();
		                    return true;
		                }
		                return false;
		
		            case DDCommand.Short:
		                if (CanEnterShortNow())
		                {
		                    EnterShortATM();
		                    return true;
		                }
		                return false;
		
		            case DDCommand.Close:
		                {
		                    bool hadAnything =
		                        (!string.IsNullOrEmpty(longAtmStrategyId) && GetAtmStrategyMarketPosition(longAtmStrategyId) != MarketPosition.Flat) ||
		                        (!string.IsNullOrEmpty(shortAtmStrategyId) && GetAtmStrategyMarketPosition(shortAtmStrategyId) != MarketPosition.Flat);
		                    if (hadAnything) KillAllATMs();
		                    return hadAnything;
		                }
		
		            case DDCommand.Panic:
		                {
		                    bool hadAnything =
		                        (!string.IsNullOrEmpty(longAtmStrategyId) && GetAtmStrategyMarketPosition(longAtmStrategyId) != MarketPosition.Flat) ||
		                        (!string.IsNullOrEmpty(shortAtmStrategyId) && GetAtmStrategyMarketPosition(shortAtmStrategyId) != MarketPosition.Flat);
		                    KillAllATMs();
		                    canTradeOK = false;
		                    return hadAnything;
		                }
		
		            case DDCommand.Enable:
		                {
		                    bool changed = !canTradeOK;
		                    canTradeOK = true;
		                    return changed;
		                }
		
		            case DDCommand.Disable:
		                {
		                    bool changed = canTradeOK;
		                    canTradeOK = false;
		                    return changed;
		                }
		
		            case DDCommand.SetTemplate:
		                if (!string.IsNullOrWhiteSpace(arg))
		                {
		                    AtmStrategyTemplate = arg;
		                    return true;
		                }
		                return false;
		        }
		    }
		    catch { }
		    return false;
		}
		
		// Bridge-friendly flatness query
		public bool TryQueryIsAtmFlat(out bool isFlat)
		{
		    try
		    {
		        isFlat = IsAtmFlat(); // your existing method
		        return true;
		    }
		    catch
		    {
		        isFlat = true;
		        return false;
		    }
		}

		
		private int GetTargetTicks(int i)
		{
		    switch(i) {
		        case 0: return NonATM_Target1Ticks;
		        case 1: return NonATM_Target2Ticks;
		        case 2: return NonATM_Target3Ticks;
		        case 3: return NonATM_Target4Ticks;
		        case 4: return NonATM_Target5Ticks;
		        case 5: return NonATM_Target6Ticks;
		        default: return NonATM_Target1Ticks; // fallback
		    }
		}


		private string GetAllPlotValuesForSplunk()
		{
		    var plots = new List<string>();
		
		    return string.Join(",", plots);
		}

		private string GetCurrentStatsSummary()
		{
		    var minTradePnLStr = realizedPnLPerTrade.Count > 0 ? GetMinTradePnL().ToString("0.##") : "N/A";
		    var maxTradePnLStr = realizedPnLPerTrade.Count > 0 ? GetMaxTradePnL().ToString("0.##") : "N/A";
		    var lastTradePnLStr = realizedPnLPerTrade.Count > 0 ? lastTradeRealizedPnL.ToString("0.##") : "N/A";
		    var winLossRatioStr = realizedPnLPerTrade.Count > 0 ? GetWinLossRatioStr() : "N/A";
		    var winRateStr = realizedPnLPerTrade.Count > 0 ? GetWinRateStr() : "N/A";
		    var totalTrades = GetTotalTradeCount();
		    var winTrades = GetWinningTradeCount();
		    var lossTrades = GetLosingTradeCount();
		
		    double monitorRealized = realizedPnLPerTrade.Count > 0 ? realizedPnLPerTrade.Sum() : 0;
		    double monitorUnrealized = !IsAtmFlat() ? lastAtmUnrealizedPnL : 0;
		    double accountRealized = Account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar);
		
		    return
		        $"Realized:{monitorRealized:0.##}|Unrealized:{monitorUnrealized:0.##}|AccountRealized:{accountRealized:0.##}|LastPnL:{lastTradePnLStr}|BestPnL:{maxTradePnLStr}|WorstPnL:{minTradePnLStr}|WinLoss:{winLossRatioStr}|WinRate:{winRateStr}|Trades:{totalTrades}|W:{winTrades}|L:{lossTrades}|Flat:{IsAtmFlat()}|Time:{DateTime.Now:HH:mm:ss}";
		}
		
		private string AppendDhm(string extra)
		{
		    DateTime t = ExtraUseLocalTime ? Time[0].ToLocalTime() : Time[0];
		    string dhm = $"D:{t:yyyy-MM-dd},H:{t:HH},M:{t:mm}";
		    return string.IsNullOrWhiteSpace(extra) ? dhm : $"{extra},{dhm}";
		}


		
		private void LogSplunkOrDebug(
		    string action,
		    string direction,
		    string eventType,
		    double entryExitPrice,
		    string extraDetails = "",
    		string atmId = ""
		)
		{
		    string plots = GetAllPlotValuesForSplunk();
		    string details = $"Price:{entryExitPrice}";
			if (!string.IsNullOrEmpty(atmId))
    			details += $",ATMID:{atmId}";
		    if (!string.IsNullOrEmpty(extraDetails))
		        details += $",{extraDetails}";
		
		    string stats = GetCurrentStatsSummary();
		    if (!string.IsNullOrEmpty(stats))
		        details += $",{stats}";
		
		    if (!string.IsNullOrEmpty(plots))
		        details += $",{plots}";
		
		    if (EnableSplunkLogging)
		    {
		        try
		        {
		            string[] logFields = new string[]
		            {
		                ((DateTimeOffset)Time[0]).ToUnixTimeSeconds().ToString(),
		                action,
		                "DDATM",
		                CurrentBar.ToString(),
		                Close[0].ToString(),
		                direction,
		                eventType,
		                details
		            };
		            NinjaTrader.NinjaScript.Indicators.SplunkLoggerIndicator.PrintToSplunkCsv(logFields);
		        }
		        catch (Exception ex)
		        {
		            Print("Splunk log error: " + ex.Message);
		        }
		    }
		    if (EnableDebugPrint)
		    {
		        Print($"[DEBUG][{Time[0]:HH:mm:ss}] {action} {direction} {eventType} {details}");
		    }
		}
		
		private void CleanupAtmState()
		{
		    if (!string.IsNullOrEmpty(longAtmStrategyId) && GetAtmStrategyMarketPosition(longAtmStrategyId) == MarketPosition.Flat)
		    {
		        double realized = GetAtmStrategyRealizedProfitLoss(longAtmStrategyId);
		        cumulativeAtmRealizedPnL += realized;
		        dailyCumulativeAtmRealizedPnL += realized;
		
		        lastTradeRealizedPnL = realized;
		        realizedPnLPerTrade.Add(realized);
				LogSplunkOrDebug("Exit", "Long", "ATM", Close[0], $"PnL:{lastTradeRealizedPnL:0.##},ATMID:{longAtmStrategyId}");
		        longAtmStrategyId = string.Empty;
		        longOrderId = string.Empty;
		        isLongAtmCreated = false;
		    }
		
		    if (!string.IsNullOrEmpty(shortAtmStrategyId) && GetAtmStrategyMarketPosition(shortAtmStrategyId) == MarketPosition.Flat)
		    {
		        double realized = GetAtmStrategyRealizedProfitLoss(shortAtmStrategyId);
		        cumulativeAtmRealizedPnL += realized;
		        dailyCumulativeAtmRealizedPnL += realized;
		
		        lastTradeRealizedPnL = realized;
		        realizedPnLPerTrade.Add(realized);
				LogSplunkOrDebug("Exit", "Short", "ATM", Close[0], $"PnL:{lastTradeRealizedPnL:0.##},ATMID:{shortAtmStrategyId}");
		        shortAtmStrategyId = string.Empty;
		        shortOrderId = string.Empty;
		        isShortAtmCreated = false;
		    }
		}


		private double GetMinTradePnL() => realizedPnLPerTrade.Count > 0 ? realizedPnLPerTrade.Min() : double.NaN;
		private double GetMaxTradePnL() => realizedPnLPerTrade.Count > 0 ? realizedPnLPerTrade.Max() : double.NaN;


		private int GetWinningTradeCount()
		{
		    return realizedPnLPerTrade.Count > 0 ? realizedPnLPerTrade.Count(pnl => pnl > 0) : 0;
		}
		
		private int GetLosingTradeCount()
		{
		    return realizedPnLPerTrade.Count > 0 ? realizedPnLPerTrade.Count(pnl => pnl < 0) : 0;
		}
		
		private string GetWinLossRatioStr()
		{
		    int wins = GetWinningTradeCount();
		    int losses = GetLosingTradeCount();
		    if (losses == 0) return wins > 0 ? "∞" : "N/A";
		    return ((double)wins / losses).ToString("0.##");
		}
		private string GetWinRateStr()
		{
		    int total = realizedPnLPerTrade.Count;
		    if (total == 0) return "N/A";
		    int wins = GetWinningTradeCount();
		    return ((double)wins / total * 100).ToString("0.#") + "%";
		}
		
		private int GetTotalTradeCount()
		{
		    return realizedPnLPerTrade != null ? realizedPnLPerTrade.Count : 0;
		}

		
		private double GetAveragePnL()
		{
		    return realizedPnLPerTrade.Count > 0 ? realizedPnLPerTrade.Average() : 0;
		}
		
		private double GetAverageWin()
		{
		    var winners = realizedPnLPerTrade.Where(pnl => pnl > 0).ToList();
		    return winners.Count > 0 ? winners.Average() : 0;
		}
		
		private double GetAverageLoss()
		{
		    var losers = realizedPnLPerTrade.Where(pnl => pnl < 0).ToList();
		    return losers.Count > 0 ? losers.Average() : 0;
		}
		
		private int GetMaxConsecutiveWins()
		{
		    int maxStreak = 0, currentStreak = 0;
		    foreach (var pnl in realizedPnLPerTrade)
		    {
		        if (pnl > 0)
		            maxStreak = ++currentStreak > maxStreak ? currentStreak : maxStreak;
		        else
		            currentStreak = 0;
		    }
		    return maxStreak;
		}
		
		private int GetMaxConsecutiveLosses()
		{
		    int maxStreak = 0, currentStreak = 0;
		    foreach (var pnl in realizedPnLPerTrade)
		    {
		        if (pnl < 0)
		            maxStreak = ++currentStreak > maxStreak ? currentStreak : maxStreak;
		        else
		            currentStreak = 0;
		    }
		    return maxStreak;
		}
		private double GetSharpeRatio()
		{
		    if (realizedPnLPerTrade.Count < 2) // Not enough trades
		        return 0;
		    double mean = realizedPnLPerTrade.Average();
		    double std = Math.Sqrt(realizedPnLPerTrade.Select(pnl => Math.Pow(pnl - mean, 2)).Sum() / (realizedPnLPerTrade.Count - 1));
		    if (std == 0)
		        return 0;
		    // Risk-free rate assumed zero, Sharpe = mean / std
		    return mean / std;
		}
		
		
		// Returns average PnL for *runs* of consecutive winning trades (amount per run, not per trade)
		private double GetAverageConsecutiveWinAmount()
		{
		    double sum = 0;
		    int count = 0;
		    double currentRunSum = 0;
		    foreach (var pnl in realizedPnLPerTrade)
		    {
		        if (pnl > 0)
		        {
		            currentRunSum += pnl;
		        }
		        else
		        {
		            if (currentRunSum > 0)
		            {
		                sum += currentRunSum;
		                count++;
		            }
		            currentRunSum = 0;
		        }
		    }
		    if (currentRunSum > 0)
		    {
		        sum += currentRunSum;
		        count++;
		    }
		    return count > 0 ? sum / count : 0;
		}
		
		// Returns average PnL for *runs* of consecutive losing trades (amount per run, not per trade)
		private double GetAverageConsecutiveLossAmount()
		{
		    double sum = 0;
		    int count = 0;
		    double currentRunSum = 0;
		    foreach (var pnl in realizedPnLPerTrade)
		    {
		        if (pnl < 0)
		        {
		            currentRunSum += pnl;
		        }
		        else
		        {
		            if (currentRunSum < 0)
		            {
		                sum += currentRunSum;
		                count++;
		            }
		            currentRunSum = 0;
		        }
		    }
		    if (currentRunSum < 0)
		    {
		        sum += currentRunSum;
		        count++;
		    }
		    return count > 0 ? sum / count : 0;
		}

		
		// Average count of trades per consecutive win streak
		private double GetAverageConsecutiveWinCount()
		{
		    int count = 0;
		    int streakLength = 0;
		    int streaks = 0;
		
		    foreach (var pnl in realizedPnLPerTrade)
		    {
		        if (pnl > 0)
		        {
		            streakLength++;
		        }
		        else
		        {
		            if (streakLength > 0)
		            {
		                count += streakLength;
		                streaks++;
		            }
		            streakLength = 0;
		        }
		    }
		    if (streakLength > 0)
		    {
		        count += streakLength;
		        streaks++;
		    }
		    return streaks > 0 ? (double)count / streaks : 0;
		}
		
		// Average count of trades per consecutive loss streak
		private double GetAverageConsecutiveLossCount()
		{
		    int count = 0;
		    int streakLength = 0;
		    int streaks = 0;
		
		    foreach (var pnl in realizedPnLPerTrade)
		    {
		        if (pnl < 0)
		        {
		            streakLength++;
		        }
		        else
		        {
		            if (streakLength > 0)
		            {
		                count += streakLength;
		                streaks++;
		            }
		            streakLength = 0;
		        }
		    }
		    if (streakLength > 0)
		    {
		        count += streakLength;
		        streaks++;
		    }
		    return streaks > 0 ? (double)count / streaks : 0;
		}



		
		private Grid FindChartGrid()
		{
		    Window chartWindow = System.Windows.Window.GetWindow(ChartControl);
		    if (chartWindow == null)
		        return null;
		
		    Grid mainGrid = chartWindow.Content as Grid;
		    if (mainGrid != null)
		        return mainGrid;
		
		    return FindVisualChildren<Grid>(chartWindow).FirstOrDefault();
		}
		
		private static IEnumerable<T> FindVisualChildren<T>(DependencyObject depObj) where T : DependencyObject
		{
		    if (depObj == null) yield break;
		
		    for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++)
		    {
		        DependencyObject child = VisualTreeHelper.GetChild(depObj, i);
		        if (child != null && child is T tChild)
		            yield return tChild;
		
		        foreach (T childOfChild in FindVisualChildren<T>(child))
		            yield return childOfChild;
		    }
		}
		
		private void OnEnableLongBtnClick(object sender, RoutedEventArgs e)
		{
		    enableLong = !enableLong;
		    UpdateEnableButtons();
		    Print("Enable Long: " + enableLong);
		}
		
		private void OnEnableShortBtnClick(object sender, RoutedEventArgs e)
		{
		    enableShort = !enableShort;
		    UpdateEnableButtons();
		    Print("Enable Short: " + enableShort);
		}
		
		private void UpdateEnableButtons()
		{
		    if (enableLongBtn != null)
		    {
		        enableLongBtn.Background = enableLong ? Brushes.LimeGreen : Brushes.Firebrick;
		        enableLongBtn.Foreground = Brushes.White;
		        enableLongBtn.Content = enableLong ? "Long Enabled" : "Long Disabled";
		    }
		    if (enableShortBtn != null)
		    {
		        enableShortBtn.Background = enableShort ? Brushes.LimeGreen : Brushes.Firebrick;
		        enableShortBtn.Foreground = Brushes.White;
		        enableShortBtn.Content = enableShort ? "Short Enabled" : "Short Disabled";
		    }
		}
		
		private void CleanupAtmStateAndButtons()
		{
		    CleanupAtmState();
		    UpdateEntryButtons();
		    UpdateEnableButtons();
		    UpdateStrategyButtonStyle();
		}

		private void UpdateEntryButtons()
		{
		    bool flatOK = IsAtmFlat();
		
		    if (longBtn != null)
		    {
		        bool canL = canTradeOK && enableLong && flatOK;
		        longBtn.IsEnabled  = canL;
		        longBtn.Background = canL ? Brushes.AliceBlue : Brushes.LightGray;
		    }
		    if (shortBtn != null)
		    {
		        bool canS = canTradeOK && enableShort && flatOK;
		        shortBtn.IsEnabled  = canS;
		        shortBtn.Background = canS ? Brushes.MistyRose : Brushes.LightGray;
		    }
		}

				
		private void CreateWPFControls()
		{
		    if (ChartControl == null || chartTraderGrid != null)
		        return;
		
		    chartWindow = Window.GetWindow(ChartControl) as NinjaTrader.Gui.Chart.Chart;
		    if (chartWindow == null) return;
		
		    chartTraderGrid = FindChartGrid();
		    if (chartTraderGrid == null) return;
		
			chartTraderButtonsGrid = new Grid { Margin = new Thickness(5, 5, 5, 5), HorizontalAlignment = HorizontalAlignment.Right };
			
			for (int i = 0; i < 7; i++)
			    chartTraderButtonsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			
			strategyBtn = new Button { Content = "Enabled", Margin = new Thickness(2), Height = 22, Width = 90, Background = Brushes.LimeGreen };
			enableLongBtn = new Button { Content = "Enable Long", Margin = new Thickness(2), Height = 22, Width = 80, Background = Brushes.LimeGreen };
			enableShortBtn = new Button { Content = "Enable Short", Margin = new Thickness(2), Height = 22, Width = 80, Background = Brushes.LimeGreen };
			longBtn = new Button { Content = "Force Long", Margin = new Thickness(2), Height = 22, Width = 85, Background = Brushes.AliceBlue };
			shortBtn = new Button { Content = "Force Short", Margin = new Thickness(2), Height = 22, Width = 85, Background = Brushes.MistyRose };
			closeBtn = new Button { Content = "Close", Margin = new Thickness(2), Height = 22, Width = 65, Background = Brushes.Gold };
			panicBtn = new Button { Content = "Panic", Margin = new Thickness(2), Height = 22, Width = 65, Background = Brushes.OrangeRed };
			
			// Add event handlers
			strategyBtn.Click += OnStrategyBtnClick;
			enableLongBtn.Click += OnEnableLongBtnClick;
			enableShortBtn.Click += OnEnableShortBtnClick;
			longBtn.Click += OnLongBtnClick;
			shortBtn.Click += OnShortBtnClick;
			closeBtn.Click += OnCloseBtnClick;
			panicBtn.Click += OnPanicBtnClick;
			
			chartTraderButtonsGrid.Children.Add(strategyBtn);
			chartTraderButtonsGrid.Children.Add(enableLongBtn);
			chartTraderButtonsGrid.Children.Add(enableShortBtn);
			chartTraderButtonsGrid.Children.Add(longBtn);
			chartTraderButtonsGrid.Children.Add(shortBtn);
			chartTraderButtonsGrid.Children.Add(closeBtn);
			chartTraderButtonsGrid.Children.Add(panicBtn);
			
			Grid.SetColumn(strategyBtn, 0);
			Grid.SetColumn(enableLongBtn, 1);
			Grid.SetColumn(enableShortBtn, 2);
			Grid.SetColumn(longBtn, 3);
			Grid.SetColumn(shortBtn, 4);
			Grid.SetColumn(closeBtn, 5);
			Grid.SetColumn(panicBtn, 6);
			
			chartTraderGrid.Children.Add(chartTraderButtonsGrid);

		}
		
		private void DisposeWPFControls()
		{
		    if (chartTraderButtonsGrid != null && chartTraderGrid != null)
		    {
		        chartTraderGrid.Children.Remove(chartTraderButtonsGrid);
		        chartTraderButtonsGrid = null;
		    }
		    if (strategyBtn != null) strategyBtn.Click -= OnStrategyBtnClick;
		    if (enableLongBtn != null) enableLongBtn.Click -= OnEnableLongBtnClick;
		    if (enableShortBtn != null) enableShortBtn.Click -= OnEnableShortBtnClick;
		    if (longBtn != null) longBtn.Click -= OnLongBtnClick;
		    if (shortBtn != null) shortBtn.Click -= OnShortBtnClick;
		    if (closeBtn != null) closeBtn.Click -= OnCloseBtnClick;
		    if (panicBtn != null) panicBtn.Click -= OnPanicBtnClick;

		
		    strategyBtn = null;
		    enableLongBtn = null;
		    enableShortBtn = null;
		    longBtn = null;
		    shortBtn = null;
		    closeBtn = null;
		    panicBtn = null;
		}



		private void OnStrategyBtnClick(object sender, RoutedEventArgs e)
		{
		    canTradeOK = !canTradeOK;
		    UpdateStrategyButtonStyle();
		    Print(canTradeOK ? "Strategy Enabled" : "Strategy Disabled");
		}
		
		private void OnLongBtnClick(object sender, RoutedEventArgs e)
		{
		    if (canTradeOK && IsAtmFlat())
		        EnterLongATM();
		}
		
		private void OnShortBtnClick(object sender, RoutedEventArgs e)
		{
		    if (canTradeOK && IsAtmFlat())
		        EnterShortATM();
		}
		
		private void OnCloseBtnClick(object sender, RoutedEventArgs e)
		{
		    KillAllATMs();
			barsSinceAnyExit = 0;
    		// Wait a moment for the ATM to close, then clean up state and update GUI
    		ChartControl.Dispatcher.InvokeAsync(async () => {
    		    await Task.Delay(200); // Short delay to ensure ATM has time to process
				CleanupAtmStateAndButtons();
    		    UpdateStrategyButtonStyle();
    		});
		}
		
		private void OnPanicBtnClick(object sender, RoutedEventArgs e)
		{
    		KillAllATMs();
			barsSinceAnyExit = 0;
    		canTradeOK = false;
    		// Wait a moment, then clean up state and update GUI
    		ChartControl.Dispatcher.InvokeAsync(async () => {
    		    await Task.Delay(200);
    		    CleanupAtmStateAndButtons();
    		    UpdateStrategyButtonStyle();
    		});
    		Print("PANIC! All positions closed and strategy disabled.");
		}

		private void UpdateStrategyButtonStyle()
		{
		    if (strategyBtn == null) return;
		    if (canTradeOK)
		    {
		        strategyBtn.Content = "Enabled";
		        strategyBtn.Background = Brushes.LimeGreen;
		    }
		    else
		    {
		        strategyBtn.Content = "Disabled";
		        strategyBtn.Background = Brushes.OrangeRed;
		    }
		}



		private double GetStrategyDailyPnl()
		{
		    if (strategySessionDate != Times[0][0].Date)
		    {
		        strategySessionDate = Times[0][0].Date;
		        strategySessionStartPnL = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
		    }
		    return SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - strategySessionStartPnL;
		}
		
        private bool HasTag(string tagBase)
		{
		    string tag = tagBase + CurrentBar;
		    return DrawObjects.Any(o => o.Tag == tag);
		}

        private int ToMinutes(DateTime dt) => dt.Hour * 60 + dt.Minute;

        private bool IsInSkipWindow()
        {
            int now = ToMinutes(Time[0]);
            int skipStart = ToMinutes(SkipTradeStartTime);
            int skipEnd = ToMinutes(SkipTradeEndTime);
            if (skipEnd > skipStart)
                return now >= skipStart && now < skipEnd;
            else // overnight
                return now >= skipStart || now < skipEnd;
        }

        private bool IsMorningSafety()
        {
            DateTime estTime = Time[0].AddHours(-5);
            return estTime.Hour == 8 && estTime.Minute >= 30 && estTime.Minute < 45;
        }
        private bool IsAfternoonSafety()
        {
            DateTime estTime = Time[0].AddHours(-5);
            return estTime.Hour >= 16;
        }

        private bool IsAtmFlat()
        {	
			
            return (string.IsNullOrEmpty(longAtmStrategyId) && string.IsNullOrEmpty(shortAtmStrategyId))
                || ((string.IsNullOrEmpty(longAtmStrategyId) || GetAtmStrategyMarketPosition(longAtmStrategyId) == MarketPosition.Flat)
                 && (string.IsNullOrEmpty(shortAtmStrategyId) || GetAtmStrategyMarketPosition(shortAtmStrategyId) == MarketPosition.Flat));
        }

        private double GetCumProfitBeforeSession()
        {
            if (lastSessionDate != Times[0][0].Date)
            {
                sessionStartPnL = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
                lastSessionDate = Times[0][0].Date;
            }
            return sessionStartPnL;
        }

        private void KillAllATMs()
        {
            if (!string.IsNullOrEmpty(longAtmStrategyId))
                AtmStrategyClose(longAtmStrategyId);
            if (!string.IsNullOrEmpty(shortAtmStrategyId))
                AtmStrategyClose(shortAtmStrategyId);
        }
		
		private void MarkExit(bool wasLong)
		{
		    double exitPrice = Close[0];
		    string exitTag = wasLong ? $"ExitLong_{CurrentBar}" : $"ExitShort_{CurrentBar}";
		    string label = wasLong ? "LExit " : "SExit ";
		    double yOffset = wasLong ? -2.0 * TickSize : 2.0 * TickSize;
		    Brush triBrush = wasLong ? Brushes.Orange : Brushes.OrangeRed;
		
		    // Draw triangle down for long exit, up for short exit
		    if (wasLong)
		    {
		        Draw.TriangleDown(this, exitTag, false, 0, exitPrice, triBrush);
		    }
		    else
		    {
		        Draw.TriangleUp(this, exitTag, false, 0, exitPrice, triBrush);
		    }
		
		    // Add the exit text
		    Draw.Text(this, exitTag + "_Text", false,
		        label + Instrument.MasterInstrument.FormatPrice(exitPrice, true),
		        0, exitPrice + yOffset, 0,
		        Brushes.White, new SimpleFont("Tahoma", 10), TextAlignment.Center,
		        Brushes.Transparent, Brushes.Transparent, 0);
		}


         private double GetOpenAtmUnrealizedPnL()
         {
             double unrealized = 0;
             if (!string.IsNullOrEmpty(longAtmStrategyId) && GetAtmStrategyMarketPosition(longAtmStrategyId) == MarketPosition.Long)
                 unrealized = GetAtmStrategyUnrealizedProfitLoss(longAtmStrategyId);
             else if (!string.IsNullOrEmpty(shortAtmStrategyId) && GetAtmStrategyMarketPosition(shortAtmStrategyId) == MarketPosition.Short)
                 unrealized = GetAtmStrategyUnrealizedProfitLoss(shortAtmStrategyId);
             return unrealized;
         }
		 
		
		private int GetMaxBarsRequired()
		{
		    int barsRequired = 0;
			
		    // ---- STRATEGY / SIGNALS (Aggressive profile) ----
		    barsRequired = Math.Max(barsRequired, 7);      // toPeriod
		
		    // -- Margin for edge-case lookbacks (e.g. some indicators may require 2 extra)
		    return barsRequired + 2;
		}
		
		
		// At class level or in a method:
		private int[] GetTargetTicksArray()
		{
		    // Example: user sets up to 4 targets; adjust as needed
		    var ticks = new List<int>();
		    if (NonATM_NumTargets >= 1) ticks.Add(NonATM_Target1Ticks);
		    if (NonATM_NumTargets >= 2) ticks.Add(NonATM_Target2Ticks);
		    if (NonATM_NumTargets >= 3) ticks.Add(NonATM_Target3Ticks);
		    if (NonATM_NumTargets >= 4) ticks.Add(NonATM_Target4Ticks);
		    // ...repeat for more targets if used
		    return ticks.ToArray();
		}


		
        protected override void OnBarUpdate()
        {

			 if (BarsInProgress != 0)
    		     return;
			 
			// Update daily strategy PnL memory if new day
    		GetStrategyDailyPnl(); // This keeps the session tracker up to date
			

			int barsRequired = GetMaxBarsRequired();

			if (CurrentBar < barsRequired)
			        return;

			if (lastPnLUpdateDate != Time[0].Date)
			{
			    dailyCumulativeAtmRealizedPnL = 0;
			    lastPnLUpdateDate = Time[0].Date;
			}

			
            // --- SESSION/BAR TRACKING ---
            if (lastBarDate != Times[0][0].Date)
            {
                barsSinceSessionStart = 0;
                lastBarDate = Times[0][0].Date;
                peakUnrealized = 0;
            }
            else
            {
                barsSinceSessionStart++;
            }
			
			// --- Bars-since counters ---
			if (barsSinceAnyEntry < int.MaxValue) barsSinceAnyEntry++;
			if (barsSinceAnyExit  < int.MaxValue) barsSinceAnyExit++;
			
			// Consider "flat" depending on mode (ATM uses IsAtmFlat; others use Position)
			bool nowFlat = (TradeMode == TradeModeType.ATM) ? IsAtmFlat() : (Position.MarketPosition == MarketPosition.Flat);
			
			// If we just *became* flat, mark an exit this bar
			if (!prevFlat && nowFlat)
			    barsSinceAnyExit = 0;
			
			prevFlat = nowFlat;

			// --- Max Daily Drawdown Calculation ---
			double todayPnL = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - GetCumProfitBeforeSession();
			
			if (Time[0].Date != sessionDrawdownDate) // New day/session
			{
			    sessionDrawdownDate = Time[0].Date;
			    sessionPeakPnL = todayPnL;
			    sessionTroughPnL = todayPnL;
			    sessionMaxDrawdown = 0;
			}
			else
			{
			    if (todayPnL > sessionPeakPnL)
			    {
			        sessionPeakPnL = todayPnL;
			        sessionTroughPnL = todayPnL; // Reset trough at new peak
			    }
			    if (todayPnL < sessionTroughPnL)
			    {
			        sessionTroughPnL = todayPnL;
			        double dd = sessionPeakPnL - sessionTroughPnL;
			        if (dd > sessionMaxDrawdown)
			            sessionMaxDrawdown = dd;
			    }
			}
			
        }


		
		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{

			
    		base.OnRender(chartControl, chartScale);
    		if (!showPnl) return;
            // Only update your cached values here!
			
           // cachedExternalComposite = GetExternalPlotValue("DDSignalAdv", 0, 0);
           // cachedChopComposite     = GetExternalPlotValue("ChopFilter", 0, 0);
			
			var minTradePnLStr = realizedPnLPerTrade.Count > 0 ? GetMinTradePnL().ToString("0.##") : "N/A";
			var maxTradePnLStr = realizedPnLPerTrade.Count > 0 ? GetMaxTradePnL().ToString("0.##") : "N/A";
			var lastTradePnLStr = realizedPnLPerTrade.Count > 0 ? lastTradeRealizedPnL.ToString("0.##") : "N/A";
			var winLossRatioStr = realizedPnLPerTrade.Count > 0 ? GetWinLossRatioStr() : "N/A";
			var winRateStr = realizedPnLPerTrade.Count > 0 ? GetWinRateStr() : "N/A";
			var totalTrades = GetTotalTradeCount();
			var winTrades = GetWinningTradeCount();
			var lossTrades = GetLosingTradeCount();
			
    		double atmRealized = cumulativeAtmRealizedPnL;
    		double dailyAtmPnL = dailyCumulativeAtmRealizedPnL;
			
    		double atmUnrealized = 0;
			
    		if (!string.IsNullOrEmpty(longAtmStrategyId))
    		    atmUnrealized += GetAtmStrategyUnrealizedProfitLoss(longAtmStrategyId);
    		if (!string.IsNullOrEmpty(shortAtmStrategyId))
    		    atmUnrealized += GetAtmStrategyUnrealizedProfitLoss(shortAtmStrategyId);
			
    		// persist unrealized if non-zero
    		if (atmUnrealized != 0)
    		    lastAtmUnrealizedPnL = atmUnrealized;
			
    		var accountRealized = Account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar);

			
			
		var lines = new List<string>
		{
		    $"ATM Realized PnL: {atmRealized:0.##}",
		    $"ATM Daily PnL: {dailyAtmPnL:0.##}",
		    $"ATM Unrealized PnL: {lastAtmUnrealizedPnL:0.##}",
		    $"Account Realized: {accountRealized:0.##}",
		    $"Last Trade PnL: {lastTradePnLStr}",
		    $"Best Trade PnL: {maxTradePnLStr}",
		    $"Worst Trade PnL: {minTradePnLStr}",
		    $"Win/Loss Ratio: {winLossRatioStr}",
		    $"Win Rate: {winRateStr}",
		    $"Avg PnL/Trade: {GetAveragePnL():0.##}",
		    $"Avg Win: {GetAverageWin():0.##} ",
		    $"Avg Loss: {GetAverageLoss():0.##}",
		    $"Consec Wins: {GetMaxConsecutiveWins()} Avg A {GetAverageConsecutiveWinAmount():0.##}",
		    $"Consec Losses: {GetMaxConsecutiveLosses()} Avg A {GetAverageConsecutiveLossAmount():0.##}",
			$"Avg Consec Win Trades: {GetAverageConsecutiveWinCount():0.##}",
    		$"Avg Consec Loss Trades: {GetAverageConsecutiveLossCount():0.##}",
		    $"Trades: {totalTrades} (W: {winTrades}, L: {lossTrades})",
		    $"ATMs Flat: {IsAtmFlat()}",
		    $"Longs: {enableLong} Shorts: {enableShort}",
			$"Drawdown:{sessionMaxDrawdown:0.##} Sharpe: {GetSharpeRatio():0.##}"
		    //$"Bar Time: {Time[0]:HH:mm:ss}"
		};

	        
	        float margin = 30; // from top/right
	        float boxPadding = 10;
	        float lineHeight = 22; // pixels
	        float width = 250;
	        float height = (lineHeight * lines.Count) + (boxPadding * 2);
	        
	        float x = ChartPanel.X + ChartPanel.W - width - margin;
	        float y = ChartPanel.Y + margin;
	        
	        var boxColor = new SharpDX.Color(30, 30, 30, 180); // R, G, B, A
	        var borderColor = SharpDX.Color.DodgerBlue;
	        var textColor = SharpDX.Color.White;
	        
	        var rect = new SharpDX.RectangleF(x, y, width, height);
	        
	        using (var bgBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, boxColor))
	        using (var borderBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, borderColor))
	        using (var textBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, textColor))
	        using (var format = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Consolas", 13))
	        {
	            RenderTarget.FillRectangle(rect, bgBrush);
	            RenderTarget.DrawRectangle(rect, borderBrush);
	        
	            for (int i = 0; i < lines.Count; i++)
	            {
	                float textY = y + boxPadding + (i * lineHeight);
	                RenderTarget.DrawText(
	                    lines[i],
	                    format,
	                    new SharpDX.RectangleF(x + boxPadding, textY, width - (boxPadding * 2), lineHeight),
	                    textBrush
	                );
	            }
	        }
		}




        private void EnterLongATM()
        {
			CleanupAtmState();
            if (!string.IsNullOrEmpty(longAtmStrategyId) || !string.IsNullOrEmpty(longOrderId))
                return; // already live
			if (State != State.Realtime) return; // Guard ATM methods
            isLongAtmCreated = false;
            longAtmStrategyId = GetAtmStrategyUniqueId();
            longOrderId = GetAtmStrategyUniqueId();

            AtmStrategyCreate(OrderAction.Buy, OrderType.Market, 0, 0, TimeInForce.Day, longOrderId, AtmStrategyTemplate, longAtmStrategyId,
                (errorCode, atmId) =>
                {
                    if (errorCode == ErrorCode.NoError && atmId == longAtmStrategyId)
                        isLongAtmCreated = true;
                });
			
			    lastAtmEntryPrice = Close[0];
    		lastAtmEntryTime = Time[0];
    		lastAtmWasLong = true;
    		lastAtmEntryOrderId = longOrderId;
			LogSplunkOrDebug("Entry", "Long", "ATM", lastAtmEntryPrice, $"ATMID:{longAtmStrategyId}");
    		Draw.TriangleUp(this, $"LEntry_{CurrentBar}", false, 0, lastAtmEntryPrice, Brushes.DarkGreen);
    		Draw.Text(this, $"EntryText_{CurrentBar}", false, "LEntry " + Instrument.MasterInstrument.FormatPrice(lastAtmEntryPrice, true),
    		    0, lastAtmEntryPrice - 2.0 * TickSize, 0, Brushes.Pink, new SimpleFont("Tahoma", 10), TextAlignment.Center, Brushes.Transparent, Brushes.Yellow, 0);
			barsSinceAnyEntry = 0;
        }
        private void EnterShortATM()
        {
			CleanupAtmState();
            if (!string.IsNullOrEmpty(shortAtmStrategyId) || !string.IsNullOrEmpty(shortOrderId))
                return; // already live
			if (State != State.Realtime) return; // Guard ATM methods
            isShortAtmCreated = false;
            shortAtmStrategyId = GetAtmStrategyUniqueId();
            shortOrderId = GetAtmStrategyUniqueId();

            AtmStrategyCreate(OrderAction.SellShort, OrderType.Market, 0, 0, TimeInForce.Day, shortOrderId, AtmStrategyTemplate, shortAtmStrategyId,
                (errorCode, atmId) =>
                {
                    if (errorCode == ErrorCode.NoError && atmId == shortAtmStrategyId)
                        isShortAtmCreated = true;
                });
			
			lastAtmEntryPrice = Close[0];
    		lastAtmEntryTime = Time[0];
    		lastAtmWasLong = false;
    		lastAtmEntryOrderId = shortOrderId;
			LogSplunkOrDebug("Entry", "Short", "ATM", lastAtmEntryPrice, $"ATMID:{shortAtmStrategyId}");
    		Draw.TriangleDown(this, $"SEntry_{CurrentBar}", false, 0, lastAtmEntryPrice, Brushes.Crimson);
    		Draw.Text(this, $"EntryText_{CurrentBar}", false, "SEntry " + Instrument.MasterInstrument.FormatPrice(lastAtmEntryPrice, true),
    		    0, lastAtmEntryPrice + 2.0 * TickSize, 0, Brushes.Pink, new SimpleFont("Tahoma", 12), TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
			barsSinceAnyEntry = 0;
   		}
	    private void EnterLongNonATM()
	    {
	         // Submit 2 contracts, each with separate signal name for two targets
	         EnterLong(16, entrySignalLong1);
	    	 SetStopLoss(entrySignalLong1, CalculationMode.Ticks, 8, false);
        	 SetProfitTarget(entrySignalLong1, CalculationMode.Ticks, 2);
	    	
	    	 EnterLong(8, entrySignalLong2);
	    	 SetStopLoss(entrySignalLong2, CalculationMode.Ticks, 8, false);
        	 SetProfitTarget(entrySignalLong2, CalculationMode.Ticks, 3);
	    	 
	    	 EnterLong(4, entrySignalLong3);
	    	 SetStopLoss(entrySignalLong3, CalculationMode.Ticks, 8, false);
        	 SetProfitTarget(entrySignalLong3, CalculationMode.Ticks, 4);
	    	 
			 EnterLong(2, entrySignalLong4);
	    	 SetStopLoss(entrySignalLong4, CalculationMode.Ticks, 8, false);
        	 SetProfitTarget(entrySignalLong4, CalculationMode.Ticks, 5);
			 
			 EnterLong(2, entrySignalLong6);
	    	 SetStopLoss(entrySignalLong6, CalculationMode.Ticks, 8, false);
        	 SetProfitTarget(entrySignalLong6, CalculationMode.Ticks, 6);
	    	 
			 barsSinceAnyEntry = 0;

	    
	    }
	    
	     private void EnterShortNonATM()
	     {
	         EnterShort(16, entrySignalShort1);
	     	 SetStopLoss(entrySignalShort1, CalculationMode.Ticks, 8, false);
         	 SetProfitTarget(entrySignalShort1, CalculationMode.Ticks, 2);
	     	
	     	 EnterShort(8, entrySignalShort2);
	     	 SetStopLoss(entrySignalShort2, CalculationMode.Ticks, 8, false);
         	 SetProfitTarget(entrySignalShort2, CalculationMode.Ticks, 3);
	     	 
	     	 EnterShort(4, entrySignalShort3);
	     	 SetStopLoss(entrySignalShort3, CalculationMode.Ticks, 8, false);
         	 SetProfitTarget(entrySignalShort3, CalculationMode.Ticks, 4);
	     	 
	     	 EnterShort(2, entrySignalShort4);
	     	 SetStopLoss(entrySignalShort4, CalculationMode.Ticks, 8, false);
         	 SetProfitTarget(entrySignalShort4, CalculationMode.Ticks, 5);
		 	 
		 	 EnterShort(2, entrySignalShort6);
	     	 SetStopLoss(entrySignalShort6, CalculationMode.Ticks, 8, false);
         	 SetProfitTarget(entrySignalShort6, CalculationMode.Ticks, 6);
			 barsSinceAnyEntry = 0;

	     }


    }
}
















































