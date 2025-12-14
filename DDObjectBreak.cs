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
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows;
using System.Windows.Input;

using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.Data;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;

using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;

// Aliases
using DWFactory = SharpDX.DirectWrite.Factory;
using DWFontStyle = SharpDX.DirectWrite.FontStyle;
using DWFontWeight = SharpDX.DirectWrite.FontWeight;
#endregion


    /// <summary>
    /// Direction filter options for breakout signals
    /// </summary>
    public enum DirFilter { Both, Longs, Shorts, Candle }

    /// <summary>
    /// UI redraw mode options for performance control
    /// </summary>
    public enum RedrawMode { Full, Throttled, Minimal, Disabled }


namespace NinjaTrader.NinjaScript.Indicators.DimDim
{

    /// <summary>
    /// Watches recent chart drawing objects and emits +1 / -1 when price
    /// breaks above/below the selected object (within a configurable lookback).
    /// UI is a lightweight dropdown styled after DDPlotReader with a direction
    /// toggle (Long / Short / Both).
    /// </summary>
    public class DDObjectBreak : Indicator
    {

        // --- Parameters ---
        [NinjaScriptProperty, Display(Name = "Lookback Bars", GroupName = "Behavior", Order = 0)]
        [Range(1, 10000)]
        public int LookbackBars { get; set; } = 100;

        [NinjaScriptProperty, Display(Name = "Fire Once Per Bar", GroupName = "Behavior", Order = 1)]
        public bool FireOncePerBar { get; set; } = true;

        [NinjaScriptProperty, Display(Name = "Fire In Historical", GroupName = "Behavior", Order = 2)]
        public bool FireInHistorical { get; set; } = false;

        [NinjaScriptProperty, Display(Name = "Debug", GroupName = "Behavior", Order = 3)]
        public bool DebugMode { get; set; } = false;

        [NinjaScriptProperty, Display(Name = "Offset (ticks)", GroupName = "Behavior", Order = 4)]
        [Range(0, 1000)]
        public int OffsetTicks { get; set; } = 0;

        [NinjaScriptProperty, Display(Name = "Max Distance (ticks)", GroupName = "Behavior", Order = 5)]
        [Range(1, 1000)]
        public int MaxDistanceTicks { get; set; } = 10;  // Reduced from 50 - tighter proximity required

        [NinjaScriptProperty, Display(Name = "Top-Left X", GroupName = "Layout", Order = 10)]
        public int TLX { get; set; } = 20;

        [NinjaScriptProperty, Display(Name = "Top-Left Y", GroupName = "Layout", Order = 11)]
        public int TLY { get; set; } = 40;

        [NinjaScriptProperty, Display(Name = "Width", GroupName = "Layout", Order = 12)]
        public int WidthPx { get; set; } = 220;

        [NinjaScriptProperty, Display(Name = "Visible Rows", GroupName = "Layout", Order = 13)]
        [Range(3, 30)]
        public int VisibleRows { get; set; } = 12;

        [NinjaScriptProperty, Display(Name = "Longs Enabled (default)", GroupName = "Routing", Order = 20)]
        public bool LongsEnabledDefault { get; set; } = true;

        [NinjaScriptProperty, Display(Name = "Shorts Enabled (default)", GroupName = "Routing", Order = 21)]
        public bool ShortsEnabledDefault { get; set; } = true;

        [NinjaScriptProperty, Display(Name = "Direction Filter Default", Description = "Initial button state", GroupName = "Routing", Order = 22)]
        public DirFilter DirFilterDefault { get; set; } = DirFilter.Candle;

        [NinjaScriptProperty, Display(Name = "Invert Mode Default", Description = "Start with REV mode enabled", GroupName = "Routing", Order = 23)]
        public bool InvertModeDefault { get; set; } = false;

        [NinjaScriptProperty, Display(Name = "Draw Signal Arrows", GroupName = "Visuals", Order = 30)]
        public bool DrawSignalArrows { get; set; } = false;

        [NinjaScriptProperty, Display(Name = "Arrow Offset (ticks)", GroupName = "Visuals", Order = 31)]
        [Range(0, 1000)]
        public int ArrowOffsetTicks { get; set; } = 2;

        // [NinjaScriptProperty, Display(Name = "Redraw Mode", Description = "Controls how often the UI refreshes", GroupName = "Performance", Order = 32)]
        public RedrawMode RedrawModeDefault { get; set; } = RedrawMode.Minimal;

        // --- Persistence: stores the last selected object tag across reloads ---
        [NinjaScriptProperty]
        [Display(Name = "Selected Object Tag", Description = "Tag of the object to monitor for breakouts. Saved across reloads.", GroupName = "Object Selection", Order = 0)]
        public string PersistedSelectedTag { get; set; } = "";

        // --- Runtime state ---
        private readonly List<string> tags = new List<string>();
        private readonly HashSet<string> tagSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string selectedTag;  // null by default - no initial selection
        private bool selectionRestored = false;  // track if we've attempted to restore
        private DirFilter dirFilter = DirFilter.Candle;
        private readonly Dictionary<string, int> lastSideByTag = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private int lastSignalBar = -1;
        private DateTime nextGroupScan = DateTime.MinValue;
        private readonly TimeSpan groupScanInterval = TimeSpan.FromMilliseconds(100);  // Fast refresh for object moves
        private readonly Dictionary<string, double> lastLevelByTag = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private bool inForceCheck = false;
        
        // Frozen signal state - holds signal for current bar once detected
        private int frozenSignal = 0;
        private int frozenSignalBar = -1;
        private double frozenSignalLevel = double.NaN; // Store the breakout level for arrow drawing
        
        /// <summary>
        /// Resets the frozen signal state (called on object/button change)
        /// </summary>
        private void ResetFrozenSignal(bool force = false)
        {
            // Keep the frozen signal for the remainder of the current bar unless explicitly forced
            if (!force && frozenSignal != 0 && frozenSignalBar == CurrentBar)
                return;
            frozenSignal = 0;
            frozenSignalBar = -1;
            frozenSignalLevel = double.NaN;
        }

        // UI
        private bool isOpen;
        private int hoverIndex = -1;
        private int listStart = 0;
        private float boxH = 24f;
        private float itemH = 20f;
        private float pad = 6f;
        private RectangleF dropdownRect;
        private RectangleF dirFilterRect;
        private RectangleF invertRect;
        private bool hoverDir;
        private bool hoverInvert;
        private bool hoverDrop;

        private DWFactory dwFactory;
        private TextFormat tf;
        private SolidColorBrush brushBg, brushBorder, brushText, brushHover, brushOpenBg;

        private bool mouseHooked;

        // Cached paint coords
        private float boxX, boxY, boxW;
        private RectangleF dbgRect;
        private string dbgLine = "";
        private int lastMatchCount = 0;
        private string lastResolvedTag = null;
        private bool invertMode = false;   // when true, signals are flipped
        private RedrawMode redrawMode = RedrawMode.Throttled;
        // Redraw throttling to avoid excessive UI invalidations (especially in replay)
        private readonly TimeSpan redrawThrottle = TimeSpan.FromMilliseconds(250);
        private DateTime nextRedrawTime = DateTime.MinValue;
        
        // Debug state - updated every tick
        private double dbgLevel = double.NaN;
        private double dbgPrice = double.NaN;
        private int dbgSide = 0;
        private int dbgPrevSide = 0;
        private bool dbgFoundObject = false;
        private int dbgObjectCount = 0;
        
        // Ellipsize cache to avoid expensive TextLayout creation on every render
        private readonly Dictionary<string, string> ellipsizeCache = new Dictionary<string, string>();
        private float lastEllipsizeWidth = 0f;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "DDObjectBreak";
                IsOverlay = false;            // render in its own panel
                Calculate = Calculate.OnEachTick;
                IsSuspendedWhileInactive = false;

                AddPlot(System.Windows.Media.Brushes.Transparent, "BreakSignal");
            }
            else if (State == State.Configure)
            {
                dirFilter = DirFilter.Candle;
            }
            else if (State == State.DataLoaded)
            {
                // runtime toggles derive from defaults
                ApplyDirDefaults();
                invertMode = InvertModeDefault;
                
                // Restore selection from persisted value (even if object not yet on chart)
                if (!string.IsNullOrEmpty(PersistedSelectedTag))
                {
                    selectedTag = PersistedSelectedTag;
                    selectionRestored = true;
                }
                else
                {
                    selectedTag = null;
                    selectionRestored = false;
                }
            }
            else if (State == State.Terminated || State == State.Finalized)
            {
                UnhookMouse();
                DisposeDeviceResources();
            }
        }

        /// <summary>
        /// Forces an immediate condition check on selection change (runs on the data thread).
        /// </summary>
        private void ForceImmediateCheck()
        {
            if (inForceCheck)
                return;
            try
            {
                inForceCheck = true;
                if (BarsArray == null || BarsArray.Length == 0 || CurrentBar < 0)
                    return;
                OnBarUpdate();
            }
            catch
            {
                // ignore
            }
            finally { inForceCheck = false; }
        }


        private void ApplyDirDefaults()
        {
            // Use enum properties directly
            dirFilter = DirFilterDefault;
            if (dirFilter == DirFilter.Candle && !(LongsEnabledDefault && ShortsEnabledDefault))
            {
                if (LongsEnabledDefault && !ShortsEnabledDefault) dirFilter = DirFilter.Longs;
                else if (!LongsEnabledDefault && ShortsEnabledDefault) dirFilter = DirFilter.Shorts;
                else if (!LongsEnabledDefault && !ShortsEnabledDefault) dirFilter = DirFilter.Candle;
            }
            redrawMode = RedrawModeDefault;
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0)
                return;
            if (CurrentBar < 1)
            {
                Values[0][0] = 0;
                return;
            }
            if (!FireInHistorical && State != State.Realtime)
            {
                Values[0][0] = 0;
                return;
            }

            // Refresh object catalog on the data thread (cheap)
            if (!isOpen && DateTime.UtcNow >= nextGroupScan)
            {
                RebuildObjects();
                nextGroupScan = DateTime.UtcNow + groupScanInterval;
            }

            int signal = 0;
            
            // Check if we have a frozen signal from earlier this bar
            if (frozenSignalBar == CurrentBar && frozenSignal != 0)
            {
                // Use the frozen signal for the rest of this bar
                Values[0][0] = frozenSignal;
                
                // Still update debug display
                dbgLine = $"FROZEN {(frozenSignal > 0 ? "UP" : "DN")} bar={CurrentBar}";
                RequestRedraw();
                return;
            }
            
            // Reset frozen signal on new bar
            if (frozenSignalBar != CurrentBar)
            {
                frozenSignal = 0;
                frozenSignalBar = CurrentBar;
            }
            
            // Reset debug state each tick
            dbgFoundObject = false;
            dbgLevel = double.NaN;
            dbgPrice = Close[0];
            dbgSide = 0;
            dbgPrevSide = 0;

            // Only count objects when dropdown is closed (expensive operation)
            if (!isOpen)
            {
                dbgObjectCount = 0;
                foreach (DrawingTool t in DrawObjects)
                    if (t != null) dbgObjectCount++;
            }

            // Allow prefix matching when the exact tag is not in the catalog (helps historical + wildcard)
            string activeTag = selectedTag;
            if (!string.IsNullOrEmpty(selectedTag) && tagSet.Contains(selectedTag))
            {
                activeTag = selectedTag; // exact match
            }
            // else use selectedTag for prefix matching

            // Count matching objects for the selected tag (exact or prefix)
            int matchCount = 0;
            bool hasPricedMatch = false;
            double nearestDistance = double.MaxValue;
            string nearestTag = null;
            foreach (DrawingTool tool in DrawObjects)
            {
                if (tool == null || string.IsNullOrWhiteSpace(tool.Tag)) continue;
                string tag = tool.Tag;

                // Check if this tool matches our selection (exact or prefix)
                bool matches = false;
                if (!string.IsNullOrEmpty(activeTag) && tagSet.Contains(activeTag))
                    matches = string.Equals(tag, activeTag, StringComparison.OrdinalIgnoreCase);
                else if (!string.IsNullOrEmpty(selectedTag))
                    matches = tag.StartsWith(selectedTag, StringComparison.OrdinalIgnoreCase);

                if (matches)
                {
                    matchCount++;
                    // Try to get the price to calculate distance (fallback to last known level to avoid flicker)
                    double level;
                    bool gotLevel = TryGetAnchorPrice(tool, out level);
                    if (!gotLevel && lastLevelByTag.TryGetValue(tag, out double cachedLevel))
                    {
                        level = cachedLevel;
                        gotLevel = true;
                    }

                    if (gotLevel)
                    {
                        hasPricedMatch = true;
                        double dist = Math.Abs(Close[0] - level);
                        if (dist < nearestDistance)
                        {
                            nearestDistance = dist;
                            nearestTag = tag;
                        }
                    }
                }
            }

            // Update debug with match info
            if (matchCount > 0)
            {
                if (!hasPricedMatch)
                {
                    dbgLine = $"m={matchCount} (no level)";
                }
                else
                {
                    double distTicks = nearestDistance / TickSize;
                    string distStr = distTicks > MaxDistanceTicks ? $"TOO FAR nearest={nearestTag} dist={distTicks:F0}t" : $"nearest={nearestTag} dist={distTicks:F0}t";
                    string multiStr = matchCount > 1 ? " (multiple)" : "";
                    dbgLine = $"m={matchCount}{multiStr} {distStr}";
                }
            }
            else
            {
                dbgLine = $"NO MATCH tag={activeTag ?? selectedTag ?? "none"} obj={dbgObjectCount} m=0";
            }

            // Never trigger signal when no objects are available
            if (tags.Count == 0)
            {
                dbgLine = "NO OBJECTS";
                ResetFrozenSignal();
                Values[0][0] = 0;
                RequestRedraw();
                return;
            }

            lastMatchCount = matchCount;
            lastResolvedTag = activeTag;

            // Store current best level for arrow drawing (may be different from frozen level)
            double currentBestLevel = double.NaN;

            if (!string.IsNullOrEmpty(activeTag))
            {
                double offset = Math.Abs(OffsetTicks) * TickSize;
                double currPrice = Close[0];
                int evalMatchCount = 0;

                // Track best candidate (nearest within range)
                bool foundWithPrice = false;
                bool foundWithinRange = false;
                double bestDistance = double.MaxValue;
                double bestLevel = double.NaN;
                string bestTag = null;
                string bestToolType = null;
                int bestPrevSide = 0;
                int bestCurrSide = 0;
                bool bestBarTouches = false;
                string bestCandleStr = "";

                double maxDist = MaxDistanceTicks * TickSize;

                // Evaluate all matching objects and pick the closest within max distance
                foreach (DrawingTool tool in DrawObjects)
                {
                    if (tool == null)
                        continue;
                    var tag = tool.Tag;
                    if (string.IsNullOrWhiteSpace(tag))
                        continue;

                    // Check if this tool matches our selection (exact or prefix)
                    bool matches = false;
                    if (!string.IsNullOrEmpty(activeTag) && tagSet.Contains(activeTag))
                        matches = string.Equals(tag, activeTag, StringComparison.OrdinalIgnoreCase);
                    else if (!string.IsNullOrEmpty(selectedTag))
                        matches = tag.StartsWith(selectedTag, StringComparison.OrdinalIgnoreCase);

                    if (!matches)
                        continue;

                    evalMatchCount++;
                    // Get the tool type for debugging
                    string toolType = tool.GetType().Name;

                    double level;
                    bool gotLevel = TryGetAnchorPrice(tool, out level);
                    if (!gotLevel && lastLevelByTag.TryGetValue(tag, out double cachedLevel))
                    {
                        level = cachedLevel;
                        gotLevel = true;
                    }

                    if (!gotLevel)
                    {
                        // continue scanning others
                        continue;
                    }

                    foundWithPrice = true;

                    // Detect if the object was moved - if level changed, reset side tracking
                    double lastLevel;
                    bool objectMoved = false;
                    if (lastLevelByTag.TryGetValue(tag, out lastLevel))
                    {
                        double levelChange = Math.Abs(level - lastLevel);
                        if (levelChange > TickSize * 2)  // More than 2 ticks change = object moved
                        {
                            objectMoved = true;
                            lastSideByTag.Remove(tag);  // Reset side tracking for this object
                            if (DebugMode)
                                Print($"[DDObjectBreak] Object moved: {tag} from {lastLevel:F2} to {level:F2}");
                        }
                    }
                    lastLevelByTag[tag] = level;  // Always update the last known level

                    double distance = Math.Abs(currPrice - level);
                    bool withinRange = distance <= maxDist;

                    int currSide = SideFor(currPrice, level, offset);
                    int prevSide;
                    if (!lastSideByTag.TryGetValue(tag, out prevSide) || objectMoved)
                    {
                        prevSide = currSide;
                    }
                    lastSideByTag[tag] = currSide; // keep tracking for all matches

                    bool barTouchesLevel = (High[0] >= level && Low[0] <= level);
                    string candleStr = dirFilter == DirFilter.Candle
                        ? (Close[0] > Open[0] ? "G" : Close[0] < Open[0] ? "R" : "D")
                        : "";

                    // Prefer the nearest within range; if none within range, keep the nearest overall
                    bool isBetter = false;
                    if (withinRange)
                    {
                        if (!foundWithinRange || distance < bestDistance)
                        {
                            isBetter = true;
                            foundWithinRange = true;
                        }
                    }
                    else if (!foundWithinRange && distance < bestDistance)
                    {
                        isBetter = true;
                    }

                    if (isBetter)
                    {
                        bestDistance = distance;
                        bestLevel = level;
                        bestTag = tag;
                        bestToolType = toolType;
                        bestPrevSide = prevSide;
                        bestCurrSide = currSide;
                        bestBarTouches = barTouchesLevel;
                        bestCandleStr = candleStr;
                    }
                }

                lastMatchCount = evalMatchCount;
                lastResolvedTag = activeTag;

                if (!foundWithPrice)
                {
                    dbgLine = $"NO MATCH tag={activeTag ?? selectedTag} m={evalMatchCount}";
                    ResetFrozenSignal();
                    signal = 0;
                }
                else
                {
                    dbgFoundObject = true;
                    dbgLevel = bestLevel;
                    dbgSide = bestCurrSide;
                    dbgPrevSide = bestPrevSide;

                    // Base debug line showing nearest candidate and match count
                    string baseLine = $"best={bestTag ?? activeTag} lvl={bestLevel:F2} dist={bestDistance / TickSize:F0}t m={evalMatchCount}" +
                                      (foundWithinRange ? " in" : " out");
                    dbgLine = baseLine;

                    // Determine allowed direction (manual or candle-based)
                    bool allowLong = dirFilter == DirFilter.Both || dirFilter == DirFilter.Longs;
                    bool allowShort = dirFilter == DirFilter.Both || dirFilter == DirFilter.Shorts;
                    if (dirFilter == DirFilter.Candle)
                    {
                        if (Close[0] > Open[0]) { allowLong = true; allowShort = false; }
                        else if (Close[0] < Open[0]) { allowLong = false; allowShort = true; }
                        else { allowLong = false; allowShort = false; }
                    }

                    bool crossUp = (bestPrevSide <= 0 && bestCurrSide > 0) && bestBarTouches;
                    bool crossDn = (bestPrevSide >= 0 && bestCurrSide < 0) && bestBarTouches;

                    if (DebugMode)
                    {
                        string touchStr = bestBarTouches ? "T" : "NT";
                        dbgLine = $"lvl={bestLevel:F2} px={Close[0]:F2} ps={bestPrevSide} cs={bestCurrSide} {touchStr} {bestCandleStr} m={evalMatchCount}";
                    }

                    if (crossUp && allowLong)
                    {
                        signal = 1;
                        currentBestLevel = bestLevel; // Store current breakout level for arrow drawing
                        frozenSignalLevel = bestLevel; // Store breakout level for arrow drawing
                        dbgLine = $"BREAK UP! lvl={bestLevel:F2} px={Close[0]:F2} {bestCandleStr} m={evalMatchCount}";
                        if (DebugMode)
                            Print($"[DDObjectBreak] BREAK UP tag={bestTag} level={bestLevel:F4} price={Close[0]:F4} candle={bestCandleStr} bar={CurrentBar} m={evalMatchCount}");
                    }
                    else if (crossDn && allowShort)
                    {
                        signal = -1;
                        currentBestLevel = bestLevel; // Store current breakout level for arrow drawing
                        frozenSignalLevel = bestLevel; // Store breakout level for arrow drawing
                        dbgLine = $"BREAK DN! lvl={bestLevel:F2} px={Close[0]:F2} {bestCandleStr} m={evalMatchCount}";
                        if (DebugMode)
                            Print($"[DDObjectBreak] BREAK DOWN tag={bestTag} level={bestLevel:F4} price={Close[0]:F4} candle={bestCandleStr} bar={CurrentBar} m={evalMatchCount}");
                    }
                    else if (!foundWithinRange)
                    {
                        // all matches are too far
                        dbgLine = $"TOO FAR nearest lvl={bestLevel:F2} px={Close[0]:F2} dist={bestDistance / TickSize:F0}t m={evalMatchCount}";
                    }
                }
            }
            else
            {
                dbgLine = $"NO SEL obj={dbgObjectCount}";
                // No selection - reset frozen signal
                ResetFrozenSignal();
                signal = 0;
            }
            
            // Apply inversion if enabled
            if (invertMode && signal != 0)
            {
                signal = -signal;
                dbgLine = $"INV {dbgLine}".Trim();
            }

            // Request chart redraw to update debug box
            RequestRedraw();

            // Only apply FireOncePerBar if we haven't already frozen a signal on this bar
            if (FireOncePerBar && CurrentBar == lastSignalBar && frozenSignalBar != CurrentBar)
                signal = 0;

            // Optionally draw arrows on the price panel when a signal fires
            if (DrawSignalArrows && signal != 0)
            {
                // Use frozen level if available (for frozen signals), otherwise use current bestLevel
                double arrowLevel = !double.IsNaN(frozenSignalLevel) ? frozenSignalLevel : currentBestLevel;
                DrawEntryMarker(signal > 0, 0, arrowLevel);
            }

            // Freeze the signal for the rest of this bar
            if (signal != 0)
            {
                frozenSignal = signal;
                frozenSignalBar = CurrentBar;
                lastSignalBar = CurrentBar;
            }
            
            Values[0][0] = signal;
        }

        private int SideFor(double price, double level, double offset)
        {
            double up = level + offset;
            double dn = level - offset;
            if (price > up) return 1;
            if (price < dn) return -1;
            return 0;
        }

        private bool TryGetAnchorPrice(DrawingTool tool, out double price)
        {
            price = double.NaN;
            try
            {
                var anchors = tool?.Anchors;
                if (anchors == null)
                    return false;

                var alist = anchors.ToList();
                if (alist.Count == 0)
                    return false;

                string tname = tool.GetType().Name;
                double currPrice = Close[0];

                // --- HorizontalLine: simplest case, just use the price ---
                if (tname.Equals("HorizontalLine", StringComparison.OrdinalIgnoreCase))
                {
                    var a = alist.FirstOrDefault(x => x != null && IsFinite(x.Price));
                    if (a != null)
                    {
                        price = a.Price;
                        return true;
                    }
                }

                // --- Line, TrendLine, Ray, ArrowLine: project to current bar ---
                if (tname.Equals("Line", StringComparison.OrdinalIgnoreCase) ||
                    tname.Equals("TrendLine", StringComparison.OrdinalIgnoreCase) ||
                    tname.Equals("Ray", StringComparison.OrdinalIgnoreCase) ||
                    tname.Equals("ArrowLine", StringComparison.OrdinalIgnoreCase))
                {
                    if (alist.Count >= 2 && Bars != null)
                    {
                        if (ProjectLinePrice(alist[0], alist[1], out double proj))
                        {
                            price = proj;
                            return true;
                        }
                    }
                }

                // --- Channel / TrendChannel: use both boundaries, pick nearer ---
                if (tname.IndexOf("Channel", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (alist.Count >= 3 && Bars != null)
                    {
                        if (ProjectLinePrice(alist[0], alist[1], out double pMain))
                        {
                            // For channel, parallel line uses anchor[2]
                            if (alist.Count > 2 && ProjectLinePrice(alist[2], alist.Count > 3 ? alist[3] : alist[1], out double pParallel))
                            {
                                price = Math.Abs(currPrice - pMain) <= Math.Abs(currPrice - pParallel) ? pMain : pParallel;
                            }
                            else
                            {
                                price = pMain;
                            }
                            return true;
                        }
                    }
                }

                // --- Rectangle / Range: use upper/lower boundary nearest to price ---
                // DDTP-style: Only consider if current time is within the rectangle's time bounds
                if (tname.IndexOf("Rectangle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tname.IndexOf("Range", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (alist.Count >= 2)
                    {
                        var t0 = alist[0];
                        var t1 = alist[1];
                        if (t0 != null && t1 != null && IsFinite(t0.Price) && IsFinite(t1.Price))
                        {
                            // Check time bounds - only valid if current bar is within the rectangle
                            DateTime minTime = t0.Time <= t1.Time ? t0.Time : t1.Time;
                            DateTime maxTime = t0.Time >= t1.Time ? t0.Time : t1.Time;
                            DateTime currTime = Times != null && Times[0] != null ? Times[0][0] : DateTime.MinValue;
                            
                            if (currTime < minTime || currTime > maxTime)
                            {
                                // Current time outside rectangle bounds - skip
                                return false;
                            }
                            
                            double top = Math.Max(t0.Price, t1.Price);
                            double bot = Math.Min(t0.Price, t1.Price);
                            price = Math.Abs(currPrice - top) <= Math.Abs(currPrice - bot) ? top : bot;
                            return true;
                        }
                    }
                }

                // --- Fibonacci: pick nearest level ---
                if (tname.IndexOf("Fibonacci", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (alist.Count >= 2)
                    {
                        var a0 = alist[0];
                        var a1 = alist[1];
                        if (a0 != null && a1 != null && IsFinite(a0.Price) && IsFinite(a1.Price))
                        {
                            double p0 = a0.Price;
                            double p1 = a1.Price;
                            double span = p1 - p0;
                            double[] ratios = { 0.0, 0.236, 0.382, 0.5, 0.618, 0.786, 1.0 };
                            double best = double.NaN;
                            double bestDiff = double.MaxValue;
                            foreach (var r in ratios)
                            {
                                double lvl = p0 + span * r;
                                double d = Math.Abs(currPrice - lvl);
                                if (d < bestDiff) { bestDiff = d; best = lvl; }
                            }
                            if (IsFinite(best))
                            {
                                price = best;
                                return true;
                            }
                        }
                    }
                }

                // --- Generic 2-anchor line projection ---
                if (alist.Count >= 2 && Bars != null)
                {
                    if (ProjectLinePrice(alist[0], alist[1], out double proj))
                    {
                        price = proj;
                        return true;
                    }
                }

                // --- Fallback: use first valid anchor price (for single-anchor objects) ---
                var first = alist.FirstOrDefault(a => a != null && IsFinite(a.Price));
                if (first != null)
                {
                    price = first.Price;
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private bool ProjectLinePrice(ChartAnchor a0, ChartAnchor a1, out double proj)
        {
            proj = double.NaN;
            try
            {
                if (a0 == null || a1 == null || Bars == null)
                    return false;
                if (!IsFinite(a0.Price) || !IsFinite(a1.Price))
                    return false;
                
                // Get bar indices for the anchors
                int b0 = Bars.GetBar(a0.Time);
                int b1 = Bars.GetBar(a1.Time);
                if (b0 < 0 || b1 < 0 || b0 == int.MaxValue || b1 == int.MaxValue)
                    return false;
                
                // Ensure b0 < b1 (swap if needed)
                if (b0 > b1)
                {
                    var temp = b0; b0 = b1; b1 = temp;
                    var tempA = a0; a0 = a1; a1 = tempA;
                }
                
                int bc = CurrentBar;
                
                // DDTP-style: Don't extrapolate beyond the line's extent
                // Only project if current bar is within the line's range (or slightly beyond)
                // Allow small extension (10 bars) past the line end for practical use
                const int maxExtension = 10;
                if (bc > b1 + maxExtension)
                {
                    // Current bar is too far beyond line end - don't extrapolate
                    return false;
                }
                
                // Calculate projected price
                double slope = (b1 != b0) ? (a1.Price - a0.Price) / (double)(b1 - b0) : 0.0;
                proj = a0.Price + slope * (bc - b0);
                return IsFinite(proj);
            }
            catch { return false; }
        }

        private static bool IsFinite(double v) => !(double.IsNaN(v) || double.IsInfinity(v));

        // Draw a small entry marker as a Draw object instead of a plot marker
        private void DrawEntryMarker(bool isLong, int barsAgo, double price)
        {
            // one object per bar+side, so we don't spam tags
            string side = isLong ? "LONG" : "SHORT";
            int barIndex = CurrentBar - barsAgo;
            string tag = string.Format("{0}.{1}.{2}", "DDObjectBreak", side, barIndex);

            if (isLong)
                Draw.ArrowUp(this, tag, false, barsAgo, price, System.Windows.Media.Brushes.Aqua);
            else
                Draw.ArrowDown(this, tag, false, barsAgo, price, System.Windows.Media.Brushes.Red);
        }

        private bool IsRecent(DrawingTool tool, int currBar)
        {
            try
            {
                if (tool == null)
                    return false;
                    
                string typeName = tool.GetType().Name;
                
                // HorizontalLine objects span ALL bars - always include them
                // This covers GoldenSetupV4 levels and similar horizontal line indicators
                if (typeName.Equals("HorizontalLine", StringComparison.OrdinalIgnoreCase))
                    return true;
                
                // Also always include objects with common indicator prefixes
                string tag = tool.Tag;
                if (!string.IsNullOrEmpty(tag))
                {
                    // GoldenSetupV4 uses "Level" prefix for its horizontal lines
                    if (tag.StartsWith("Level", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                
                var anchors = tool.Anchors;
                if (anchors == null)
                    return false;
                foreach (var a in anchors)
                {
                    if (a == null) continue;
                    int bar = Bars.GetBar(a.Time);
                    if (bar < 0 || bar == int.MaxValue)
                        return true; // if we cannot map, do not exclude
                    if (currBar - bar <= LookbackBars)
                        return true;
                }
                return false;
            }
            catch { return false; }
        }

        private void RebuildObjects()
        {
            try
            {
                bool open = isOpen;
                int prevListStart = listStart;
                string prevSelected = selectedTag;
                
                // Remember previous tag count and set for change detection
                int prevTagCount = tags.Count;
                var prevTagSet = new HashSet<string>(tagSet, StringComparer.OrdinalIgnoreCase);

                tagSet.Clear();
                tags.Clear();

                int currBar = CurrentBar;
                foreach (DrawingTool tool in DrawObjects)
                {
                    var tag = tool?.Tag;
                    if (string.IsNullOrWhiteSpace(tag))
                        continue;

                    if (!IsRecent(tool, currBar))
                        continue;

                    if (tagSet.Add(tag))
                        tags.Add(tag);
                }

                tags.Sort(StringComparer.OrdinalIgnoreCase);
                
                // Check if the object list changed (added, removed, or different objects)
                bool objectsChanged = (tags.Count != prevTagCount) || !tagSet.SetEquals(prevTagSet);
                if (objectsChanged)
                {
                    // Reset state, but keep any frozen signal alive for this bar to avoid flicker
                    ResetFrozenSignal();
                    lastSideByTag.Clear();  // Clear side tracking so crosses are re-evaluated
                    ellipsizeCache.Clear(); // Clear text cache for new object names
                    if (DebugMode)
                        Print($"[DDObjectBreak] Objects changed: {prevTagCount} -> {tags.Count}, signal reset");
                }

                // Attempt to restore persisted selection on first rebuild
                // Always restore even if object not on chart - it may appear later
                if (!selectionRestored)
                {
                    selectionRestored = true;
                    if (!string.IsNullOrEmpty(PersistedSelectedTag))
                    {
                        // Keep the user-entered/persisted text; matching is resolved at runtime
                        selectedTag = PersistedSelectedTag;

                        if (DebugMode)
                            Print($"[DDObjectBreak] Restored persisted selection: {selectedTag} (exists on chart: {tagSet.Contains(PersistedSelectedTag)})");
                    }
                    // else: leave selectedTag as null (no default selection)
                }

                if (open)
                {
                    // Preserve selection and scroll while dropdown is open
                    if (!string.IsNullOrEmpty(prevSelected) && tagSet.Contains(prevSelected))
                        selectedTag = prevSelected;
                    // Don't auto-select first item - keep null if nothing was selected

                    int maxStart = Math.Max(0, tags.Count - VisibleRows);
                    listStart = Math.Max(0, Math.Min(maxStart, prevListStart));
                }
                else
                {
                    // Keep selection even if object disappears - it may reappear
                    // Don't clear selectedTag or PersistedSelectedTag when object not on chart

                    int maxStart = Math.Max(0, tags.Count - VisibleRows);
                    listStart = Math.Max(0, Math.Min(maxStart, listStart));
                }
                // After a rebuild (e.g., new objects), re-evaluate immediately
                // Skip when dropdown is open to avoid UI lag
                if (!open)
                    ForceImmediateCheck();
            }
            catch
            {
                // ignore catalog failures; will retry next tick
            }
        }

        #region UI: mouse + rendering
        private void EnsureMouseHook()
        {
            if (mouseHooked || ChartPanel == null)
                return;
            try
            {
                ChartPanel.PreviewMouseLeftButtonDown += OnMouseLeftButtonDown;
                ChartPanel.PreviewMouseMove += OnMouseMove;
                ChartPanel.PreviewMouseLeftButtonUp += OnMouseLeftButtonUp;
                ChartPanel.PreviewMouseWheel += OnMouseWheel;
                mouseHooked = true;
            }
            catch { }
        }

        private void UnhookMouse()
        {
            if (!mouseHooked || ChartPanel == null)
                return;
            try
            {
                ChartPanel.PreviewMouseLeftButtonDown -= OnMouseLeftButtonDown;
                ChartPanel.PreviewMouseMove -= OnMouseMove;
                ChartPanel.PreviewMouseLeftButtonUp -= OnMouseLeftButtonUp;
                ChartPanel.PreviewMouseWheel -= OnMouseWheel;
            }
            catch { }
            mouseHooked = false;
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (ChartPanel == null)
                return;
            var pos = e.GetPosition(ChartPanel);
            var p = new Vector2((float)pos.X, (float)pos.Y);

            if (dropdownRect.Contains(p))
            {
                isOpen = !isOpen;
                hoverIndex = -1;
                RequestRedraw();
                e.Handled = true;
                return;
            }

            if (dirFilterRect.Contains(p))
            {
                dirFilter = dirFilter == DirFilter.Both   ? DirFilter.Longs
                           : dirFilter == DirFilter.Longs  ? DirFilter.Shorts
                           : dirFilter == DirFilter.Shorts ? DirFilter.Candle
                           : DirFilter.Both;
                ResetFrozenSignal(force: true);  // Unfreeze on button interaction
                ForceImmediateCheck();
                RequestRedraw();
                e.Handled = true;
                return;
            }

            if (invertRect.Contains(p))
            {
                invertMode = !invertMode;
                ResetFrozenSignal(force: true);  // Unfreeze on mode change
                ForceImmediateCheck();
                RequestRedraw();
                e.Handled = true;
                return;
            }

            if (isOpen)
            {
                int rows = Math.Min(VisibleRows, tags.Count - listStart);
                var listRect = new Rect(dropdownRect.Left, dropdownRect.Bottom + 2,
                                        dropdownRect.Width, itemH * rows);
                if (listRect.Contains(pos))
                {
                    int row = (int)Math.Floor((pos.Y - listRect.Top) / itemH);
                    row = Math.Max(0, Math.Min(rows - 1, row));
                    int idx = listStart + row;
                    if (idx >= 0 && idx < tags.Count)
                    {
                        selectedTag = tags[idx];
                        PersistedSelectedTag = selectedTag ?? "";  // Save for persistence
                        isOpen = false;
                        hoverIndex = -1;
                        ResetFrozenSignal(force: true);  // Unfreeze on object change
                        // Re-evaluate immediately on selection change
                        ForceImmediateCheck();
                        RequestRedraw();
                        e.Handled = true;
                        return;
                    }
                }
                else
                {
                    // click outside closes list
                    isOpen = false;
                    hoverIndex = -1;
                    RequestRedraw();
                }
            }
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!isOpen || ChartPanel == null)
                return;

            var pos = e.GetPosition(ChartPanel);
            var p = new Vector2((float)pos.X, (float)pos.Y);
            bool newHoverDrop = dropdownRect.Contains(p);
            if (newHoverDrop != hoverDrop)
            {
                hoverDrop = newHoverDrop;
                RequestRedraw();
            }

            bool newHoverDir = dirFilterRect.Contains(p);
            if (newHoverDir != hoverDir)
            {
                hoverDir = newHoverDir;
                RequestRedraw();
            }

            bool newHoverInvert = invertRect.Contains(p);
            if (newHoverInvert != hoverInvert)
            {
                hoverInvert = newHoverInvert;
                RequestRedraw();
            }

            int rows = Math.Min(VisibleRows, tags.Count - listStart);
            var listRect = new Rect(dropdownRect.Left, dropdownRect.Bottom + 2,
                                    dropdownRect.Width, itemH * rows);
            int newHover = -1;
            if (listRect.Contains(pos))
            {
                newHover = (int)Math.Floor((pos.Y - listRect.Top) / itemH);
                newHover = Math.Max(0, Math.Min(rows - 1, newHover));
            }
            if (newHover != hoverIndex)
            {
                hoverIndex = newHover;
                RequestRedraw();
            }
        }

        private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e) { }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!isOpen || ChartPanel == null || tags.Count == 0)
                return;

            var pos = e.GetPosition(ChartPanel);
            int rows = Math.Min(VisibleRows, tags.Count - listStart);
            var listRect = new Rect(dropdownRect.Left, dropdownRect.Bottom + 2,
                                    dropdownRect.Width, itemH * rows);
            if (!listRect.Contains(pos))
                return;

            int dir = e.Delta > 0 ? -1 : 1;
            int maxStart = Math.Max(0, tags.Count - VisibleRows);
            listStart = Math.Max(0, Math.Min(maxStart, listStart + dir));
            RequestRedraw();
            e.Handled = true;
        }

        public override void OnRenderTargetChanged()
        {
            DisposeDeviceResources();
            if (RenderTarget == null)
                return;

            dwFactory = new DWFactory();
            tf = new TextFormat(dwFactory, "Segoe UI", DWFontWeight.Normal, DWFontStyle.Normal, 11f);

            brushBg = new SolidColorBrush(RenderTarget, new Color4(0.10f, 0.10f, 0.12f, 0.92f));
            brushOpenBg = new SolidColorBrush(RenderTarget, new Color4(0.12f, 0.12f, 0.16f, 0.95f));
            brushBorder = new SolidColorBrush(RenderTarget, new Color4(0.45f, 0.45f, 0.50f, 1.00f));
            brushText = new SolidColorBrush(RenderTarget, new Color4(0.95f, 0.95f, 0.98f, 1.00f));
            brushHover = new SolidColorBrush(RenderTarget, new Color4(0.30f, 0.45f, 0.80f, 0.28f));
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
        }

        protected override void OnRender(ChartControl cc, ChartScale cs)
        {
            EnsureMouseHook();
            if (RenderTarget == null || tf == null)
                return;

            // Keep catalog warm while painting (but avoid refreshing while open to prevent scroll jump)
            if (!isOpen && DateTime.UtcNow >= nextGroupScan)
            {
                RebuildObjects();
                nextGroupScan = DateTime.UtcNow + groupScanInterval;
            }

            boxX = TLX;
            boxY = TLY;
            boxW = WidthPx;

            dropdownRect = new RectangleF(boxX, boxY, boxW, boxH);
            dirFilterRect = new RectangleF(dropdownRect.Right + 8f, boxY, 60f, boxH);
            invertRect = new RectangleF(dirFilterRect.Right + 8f, boxY, 70f, boxH);
            dbgRect = new RectangleF(invertRect.Right + 8f, boxY, 280f, boxH);

            DrawDropdown();
            DrawDirFilter();
            DrawInvertButton();
            DrawDebugBox();

            if (isOpen)
                DrawList();
        }

        private void DrawDropdown()
        {
            var rt = RenderTarget;

            using (var geo = new RoundedRectangleGeometry(rt.Factory,
                     new RoundedRectangle { Rect = dropdownRect, RadiusX = 4f, RadiusY = 4f }))
            {
                rt.FillGeometry(geo, brushBg);
                rt.DrawGeometry(geo, brushBorder, hoverDrop ? 1.6f : 1.0f);
            }

            string label = !string.IsNullOrEmpty(selectedTag)
                ? selectedTag + (lastMatchCount > 1 ? " (multiple)" : "")
                : (tags.Count > 0 ? "Select object…" : "No objects");
            label = Ellipsize(label, dropdownRect.Width - boxH);

            using (var tl = new TextLayout(dwFactory, label, tf, dropdownRect.Width, dropdownRect.Height))
            {
                tl.WordWrapping = WordWrapping.NoWrap;
                float tx = dropdownRect.Left + pad;
                float ty = dropdownRect.Top + (dropdownRect.Height - tl.Metrics.Height) * 0.5f;
                rt.DrawTextLayout(new Vector2(tx, ty), tl, brushText);
            }

            // chevron
            float cx = dropdownRect.Right - boxH * 0.5f;
            float cy = dropdownRect.Top + boxH * 0.5f;
            var p1 = new Vector2(cx - 6f, cy - (isOpen ? -3f : 3f));
            var p2 = new Vector2(cx, cy + (isOpen ? -3f : 3f));
            var p3 = new Vector2(cx + 6f, cy - (isOpen ? -3f : 3f));
            rt.DrawLine(p1, p2, brushText, 1.3f);
            rt.DrawLine(p2, p3, brushText, 1.3f);
        }

        private void DrawDirFilter()
        {
            var rt = RenderTarget;
            Color4 bg = dirFilter == DirFilter.Both   ? new Color4(0.25f, 0.25f, 0.30f, 0.95f)
                       : dirFilter == DirFilter.Longs ? new Color4(0.10f, 0.45f, 0.15f, 0.95f)
                       : dirFilter == DirFilter.Shorts? new Color4(0.55f, 0.15f, 0.15f, 0.95f)
                       : new Color4(0.18f, 0.18f, 0.35f, 0.95f); // Candle
            string text = dirFilter == DirFilter.Both   ? "BOTH"
                          : dirFilter == DirFilter.Longs  ? "LONG"
                          : dirFilter == DirFilter.Shorts ? "SHORT"
                          : "CAND";

            using (var geo = new RoundedRectangleGeometry(rt.Factory,
                     new RoundedRectangle { Rect = dirFilterRect, RadiusX = 4f, RadiusY = 4f }))
            {
                using (var fill = new SolidColorBrush(rt, bg))
                    rt.FillGeometry(geo, fill);
                rt.DrawGeometry(geo, brushBorder, hoverDir ? 1.6f : 1.0f);
            }

            using (var tl = new TextLayout(dwFactory, text, tf, dirFilterRect.Width, dirFilterRect.Height))
            {
                tl.WordWrapping = WordWrapping.NoWrap;
                float tx = dirFilterRect.Left + (dirFilterRect.Width - tl.Metrics.Width) * 0.5f;
                float ty = dirFilterRect.Top + (dirFilterRect.Height - tl.Metrics.Height) * 0.5f;
                rt.DrawTextLayout(new Vector2(tx, ty), tl, brushText);
            }
        }

        private void DrawInvertButton()
        {
            var rt = RenderTarget;
            Color4 bg = invertMode
                ? new Color4(0.50f, 0.20f, 0.20f, 0.95f)  // reddish when reversing
                : new Color4(0.20f, 0.35f, 0.20f, 0.95f); // greenish when normal
            string text = invertMode ? "REV" : "NORM";

            using (var geo = new RoundedRectangleGeometry(rt.Factory,
                     new RoundedRectangle { Rect = invertRect, RadiusX = 4f, RadiusY = 4f }))
            {
                using (var fill = new SolidColorBrush(rt, bg))
                    rt.FillGeometry(geo, fill);
                rt.DrawGeometry(geo, brushBorder, hoverInvert ? 1.6f : 1.0f);
            }

            using (var tl = new TextLayout(dwFactory, text, tf, invertRect.Width, invertRect.Height))
            {
                tl.WordWrapping = WordWrapping.NoWrap;
                float tx = invertRect.Left + (invertRect.Width - tl.Metrics.Width) * 0.5f;
                float ty = invertRect.Top + (invertRect.Height - tl.Metrics.Height) * 0.5f;
                rt.DrawTextLayout(new Vector2(tx, ty), tl, brushText);
            }
        }

        private void DrawDebugBox()
        {
            var rt = RenderTarget;
            
            // Build status text
            string text;
            if (DebugMode && !string.IsNullOrEmpty(dbgLine))
            {
                text = dbgLine;
            }
            else if (dbgFoundObject && !double.IsNaN(dbgLevel))
            {
                text = $"lvl={dbgLevel:F2} px={dbgPrice:F2} s={dbgSide}";
            }
            else if (!string.IsNullOrEmpty(selectedTag))
            {
                text = $"[{selectedTag}] obj={dbgObjectCount} found={dbgFoundObject}";
            }
            else
            {
                text = $"obj={dbgObjectCount} (no selection)";
            }
            
            // Choose color based on state
            Color4 bgColor;
            if (dbgLine != null && (dbgLine.Contains("UP") && (dbgLine.Contains("BREAK") || dbgLine.Contains("FROZEN"))))
                bgColor = new Color4(0.10f, 0.45f, 0.15f, 0.95f); // green for UP breakout
            else if (dbgLine != null && (dbgLine.Contains("DN") && (dbgLine.Contains("BREAK") || dbgLine.Contains("FROZEN"))))
                bgColor = new Color4(0.55f, 0.15f, 0.15f, 0.95f); // red for DN breakout
            else if (dbgFoundObject && !double.IsNaN(dbgLevel))
                bgColor = new Color4(0.12f, 0.20f, 0.30f, 0.95f); // blue when tracking
            else
                bgColor = new Color4(0.15f, 0.15f, 0.18f, 0.95f); // gray default
            
            using (var geo = new RoundedRectangleGeometry(rt.Factory,
                     new RoundedRectangle { Rect = dbgRect, RadiusX = 4f, RadiusY = 4f }))
            {
                using (var fill = new SolidColorBrush(rt, bgColor))
                    rt.FillGeometry(geo, fill);
                rt.DrawGeometry(geo, brushBorder, 1.0f);
            }

            using (var tl = new TextLayout(dwFactory, text, tf, dbgRect.Width - 2 * pad, dbgRect.Height))
            {
                tl.WordWrapping = WordWrapping.NoWrap;
                float tx = dbgRect.Left + pad;
                float ty = dbgRect.Top + (dbgRect.Height - tl.Metrics.Height) * 0.5f;
                rt.DrawTextLayout(new Vector2(tx, ty), tl, brushText);
            }
        }

        private void DrawList()
        {
            if (tags.Count == 0)
                return;

            var rt = RenderTarget;
            int rows = Math.Min(VisibleRows, tags.Count - listStart);
            var listRect = new RectangleF(dropdownRect.Left, dropdownRect.Bottom + 2,
                                          dropdownRect.Width, itemH * rows);

            using (var geo = new RoundedRectangleGeometry(rt.Factory,
                     new RoundedRectangle { Rect = listRect, RadiusX = 4f, RadiusY = 4f }))
            {
                rt.FillGeometry(geo, brushOpenBg);
                rt.DrawGeometry(geo, brushBorder, 1.0f);
            }

            for (int row = 0; row < rows; row++)
            {
                int idx = listStart + row;
                float y = listRect.Top + row * itemH;
                var rowRect = new RectangleF(listRect.Left, y, listRect.Width, itemH);
                if (row == hoverIndex)
                    rt.FillRectangle(rowRect, brushHover);

                string text = Ellipsize(tags[idx], listRect.Width - 2 * pad);
                using (var tl = new TextLayout(dwFactory, text, tf, listRect.Width, itemH))
                {
                    tl.WordWrapping = WordWrapping.NoWrap;
                    float tx = rowRect.Left + pad;
                    float ty = rowRect.Top + (itemH - tl.Metrics.Height) * 0.5f;
                    rt.DrawTextLayout(new Vector2(tx, ty), tl, brushText);
                }
            }
        }

        private string Ellipsize(string s, float maxW)
        {
            if (string.IsNullOrEmpty(s))
                return s;
            
            // Clear cache if width changed (e.g., window resize)
            if (Math.Abs(maxW - lastEllipsizeWidth) > 1f)
            {
                ellipsizeCache.Clear();
                lastEllipsizeWidth = maxW;
            }
            
            // Return cached result if available
            if (ellipsizeCache.TryGetValue(s, out string cached))
                return cached;
            
            // Check if string fits without truncation
            using (var tl = new TextLayout(dwFactory, s, tf, maxW, boxH))
            {
                if (tl.Metrics.Width <= maxW)
                {
                    ellipsizeCache[s] = s;
                    return s;
                }
            }

            const string ell = "…";
            int lo = 0, hi = s.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                string cand = s.Substring(0, mid) + ell;
                using (var tl = new TextLayout(dwFactory, cand, tf, maxW, boxH))
                {
                    if (tl.Metrics.Width <= maxW) lo = mid + 1; else hi = mid;
                }
            }
            int take = Math.Max(0, lo - 1);
            string result = (take <= 0) ? ell : s.Substring(0, take) + ell;
            ellipsizeCache[s] = result;
            return result;
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
    }
}


#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private DimDim.DDObjectBreak[] cacheDDObjectBreak;
		public DimDim.DDObjectBreak DDObjectBreak(int lookbackBars, bool fireOncePerBar, bool fireInHistorical, bool debugMode, int offsetTicks, int maxDistanceTicks, int tLX, int tLY, int widthPx, int visibleRows, bool longsEnabledDefault, bool shortsEnabledDefault, DirFilter dirFilterDefault, bool invertModeDefault, bool drawSignalArrows, int arrowOffsetTicks, string persistedSelectedTag)
		{
			return DDObjectBreak(Input, lookbackBars, fireOncePerBar, fireInHistorical, debugMode, offsetTicks, maxDistanceTicks, tLX, tLY, widthPx, visibleRows, longsEnabledDefault, shortsEnabledDefault, dirFilterDefault, invertModeDefault, drawSignalArrows, arrowOffsetTicks, persistedSelectedTag);
		}

		public DimDim.DDObjectBreak DDObjectBreak(ISeries<double> input, int lookbackBars, bool fireOncePerBar, bool fireInHistorical, bool debugMode, int offsetTicks, int maxDistanceTicks, int tLX, int tLY, int widthPx, int visibleRows, bool longsEnabledDefault, bool shortsEnabledDefault, DirFilter dirFilterDefault, bool invertModeDefault, bool drawSignalArrows, int arrowOffsetTicks, string persistedSelectedTag)
		{
			if (cacheDDObjectBreak != null)
				for (int idx = 0; idx < cacheDDObjectBreak.Length; idx++)
					if (cacheDDObjectBreak[idx] != null && cacheDDObjectBreak[idx].LookbackBars == lookbackBars && cacheDDObjectBreak[idx].FireOncePerBar == fireOncePerBar && cacheDDObjectBreak[idx].FireInHistorical == fireInHistorical && cacheDDObjectBreak[idx].DebugMode == debugMode && cacheDDObjectBreak[idx].OffsetTicks == offsetTicks && cacheDDObjectBreak[idx].MaxDistanceTicks == maxDistanceTicks && cacheDDObjectBreak[idx].TLX == tLX && cacheDDObjectBreak[idx].TLY == tLY && cacheDDObjectBreak[idx].WidthPx == widthPx && cacheDDObjectBreak[idx].VisibleRows == visibleRows && cacheDDObjectBreak[idx].LongsEnabledDefault == longsEnabledDefault && cacheDDObjectBreak[idx].ShortsEnabledDefault == shortsEnabledDefault && cacheDDObjectBreak[idx].DirFilterDefault == dirFilterDefault && cacheDDObjectBreak[idx].InvertModeDefault == invertModeDefault && cacheDDObjectBreak[idx].DrawSignalArrows == drawSignalArrows && cacheDDObjectBreak[idx].ArrowOffsetTicks == arrowOffsetTicks && cacheDDObjectBreak[idx].PersistedSelectedTag == persistedSelectedTag && cacheDDObjectBreak[idx].EqualsInput(input))
						return cacheDDObjectBreak[idx];
			return CacheIndicator<DimDim.DDObjectBreak>(new DimDim.DDObjectBreak(){ LookbackBars = lookbackBars, FireOncePerBar = fireOncePerBar, FireInHistorical = fireInHistorical, DebugMode = debugMode, OffsetTicks = offsetTicks, MaxDistanceTicks = maxDistanceTicks, TLX = tLX, TLY = tLY, WidthPx = widthPx, VisibleRows = visibleRows, LongsEnabledDefault = longsEnabledDefault, ShortsEnabledDefault = shortsEnabledDefault, DirFilterDefault = dirFilterDefault, InvertModeDefault = invertModeDefault, DrawSignalArrows = drawSignalArrows, ArrowOffsetTicks = arrowOffsetTicks, PersistedSelectedTag = persistedSelectedTag }, input, ref cacheDDObjectBreak);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.DimDim.DDObjectBreak DDObjectBreak(int lookbackBars, bool fireOncePerBar, bool fireInHistorical, bool debugMode, int offsetTicks, int maxDistanceTicks, int tLX, int tLY, int widthPx, int visibleRows, bool longsEnabledDefault, bool shortsEnabledDefault, DirFilter dirFilterDefault, bool invertModeDefault, bool drawSignalArrows, int arrowOffsetTicks, string persistedSelectedTag)
		{
			return indicator.DDObjectBreak(Input, lookbackBars, fireOncePerBar, fireInHistorical, debugMode, offsetTicks, maxDistanceTicks, tLX, tLY, widthPx, visibleRows, longsEnabledDefault, shortsEnabledDefault, dirFilterDefault, invertModeDefault, drawSignalArrows, arrowOffsetTicks, persistedSelectedTag);
		}

		public Indicators.DimDim.DDObjectBreak DDObjectBreak(ISeries<double> input , int lookbackBars, bool fireOncePerBar, bool fireInHistorical, bool debugMode, int offsetTicks, int maxDistanceTicks, int tLX, int tLY, int widthPx, int visibleRows, bool longsEnabledDefault, bool shortsEnabledDefault, DirFilter dirFilterDefault, bool invertModeDefault, bool drawSignalArrows, int arrowOffsetTicks, string persistedSelectedTag)
		{
			return indicator.DDObjectBreak(input, lookbackBars, fireOncePerBar, fireInHistorical, debugMode, offsetTicks, maxDistanceTicks, tLX, tLY, widthPx, visibleRows, longsEnabledDefault, shortsEnabledDefault, dirFilterDefault, invertModeDefault, drawSignalArrows, arrowOffsetTicks, persistedSelectedTag);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.DimDim.DDObjectBreak DDObjectBreak(int lookbackBars, bool fireOncePerBar, bool fireInHistorical, bool debugMode, int offsetTicks, int maxDistanceTicks, int tLX, int tLY, int widthPx, int visibleRows, bool longsEnabledDefault, bool shortsEnabledDefault, DirFilter dirFilterDefault, bool invertModeDefault, bool drawSignalArrows, int arrowOffsetTicks, string persistedSelectedTag)
		{
			return indicator.DDObjectBreak(Input, lookbackBars, fireOncePerBar, fireInHistorical, debugMode, offsetTicks, maxDistanceTicks, tLX, tLY, widthPx, visibleRows, longsEnabledDefault, shortsEnabledDefault, dirFilterDefault, invertModeDefault, drawSignalArrows, arrowOffsetTicks, persistedSelectedTag);
		}

		public Indicators.DimDim.DDObjectBreak DDObjectBreak(ISeries<double> input , int lookbackBars, bool fireOncePerBar, bool fireInHistorical, bool debugMode, int offsetTicks, int maxDistanceTicks, int tLX, int tLY, int widthPx, int visibleRows, bool longsEnabledDefault, bool shortsEnabledDefault, DirFilter dirFilterDefault, bool invertModeDefault, bool drawSignalArrows, int arrowOffsetTicks, string persistedSelectedTag)
		{
			return indicator.DDObjectBreak(input, lookbackBars, fireOncePerBar, fireInHistorical, debugMode, offsetTicks, maxDistanceTicks, tLX, tLY, widthPx, visibleRows, longsEnabledDefault, shortsEnabledDefault, dirFilterDefault, invertModeDefault, drawSignalArrows, arrowOffsetTicks, persistedSelectedTag);
		}
	}
}

#endregion


