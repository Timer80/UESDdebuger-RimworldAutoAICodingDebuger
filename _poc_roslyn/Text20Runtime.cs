// 实参级验证：带翅膀 pawn 的 FlightMappingDef 段序列 + Burst job 实参
// 由 _poc_roslyn 驱动在游戏进程内用 Roslyn 4.14 编译执行。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Verse;
using RimWorld;
using FlightPawn2Framework.PathPlan.FlightAStar;
using FlightPawn2Framework.Flight2Animation;
using FlightPawn2Framework.SkyLayer.FlightExecution;
// RimWorld 1.6 自带 Verse.AnimationDef，与 TEXT20 的同名类型冲突 → 显式别名
using WingAnimDef = FlightPawn2Framework.Flight2Animation.AnimationDef;

public static class Text20Runtime
{
    static string Fmt(object v) => v == null ? "null" : v.ToString();
    static string Names(IEnumerable<string> xs) => string.Join(",", xs.ToArray());

    public static string Probe()
    {
        var sb = new System.Text.StringBuilder();
        Map map = Find.CurrentMap;
        if (map == null) return "no current map";
        sb.Append("map=" + map.uniqueID + " size=" + map.Size.x + "x" + map.Size.z
                  + " burstAvailable=" + BurstProbe.IsAvailable() + "\n");

        Pawn winged = null;
        AnimationComp chosen = null;

        foreach (Pawn p in map.mapPawns.FreeColonists)
        {
            AnimationComp comp = null;
            try { comp = FlightSourceManager.GetActiveComp(p); } catch (Exception e) { sb.Append("  GetActiveComp EX " + e.GetType().Name + "\n"); }
            FlightPathTracker tr = p.TryGetFlightTracker(out var t) ? t : null;

            var apparelNames = new List<string>();
            if (p.apparel != null) foreach (var a in p.apparel.WornApparel) apparelNames.Add(a.def.defName);
            var hediffNames = new List<string>();
            foreach (var h in p.health.hediffSet.hediffs) hediffNames.Add(h.def.defName);
            var geneNames = new List<string>();
            if (p.genes != null) foreach (var g in p.genes.GenesListForReading) geneNames.Add(g.def.defName);

            sb.Append("pawn " + p.LabelShort + " pos=" + p.Position
                      + " apparel=[" + Names(apparelNames) + "]"
                      + " hediffs=[" + Names(hediffNames) + "]"
                      + " genes=[" + Names(geneNames) + "]\n");
            sb.Append("   animDef=" + (comp?.AnimationDef?.defName ?? "null")
                      + " tracker=" + (tr == null ? "null" : ("IsInAir=" + tr.IsInAir + " HasActivePath=" + tr.HasActivePath))
                      + "\n");
            if (tr != null)
            {
                sb.Append("   tracker.speed: CruiseSpeedOverride=" + (tr.CruiseSpeedOverride.HasValue ? tr.CruiseSpeedOverride.Value.ToString() : "null")
                          + " Multiplier=" + tr.CruiseSpeedMultiplier
                          + " SkyUsagePenaltyForPath=" + tr.SkyUsagePenaltyForPath
                          + " FuelSourceForTest=" + (tr.FuelSourceForTest == null ? "null" : tr.FuelSourceForTest.GetType().Name)
                          + "\n");
            }
            if (comp?.AnimationDef != null && winged == null) { winged = p; chosen = comp; }
        }

        if (winged == null) return sb.ToString() + "NO PAWN WITH ANIMATION DEF FOUND";

        sb.Append("\n=== WINGED PAWN: " + winged.LabelShort + " ===\n");
        WingAnimDef def = chosen.AnimationDef;
        var fc = def.comps?.OfType<FlightComp>().FirstOrDefault();
        var compList = def.comps == null ? new List<AnimationPlayComp>() : def.comps;
        sb.Append("animDef=" + def.defName + " comps=[" + Names(compList.Select(c => c.GetType().Name)) + "]\n");
        sb.Append("totalFrames=" + Fmt(def.GetType().GetField("totalFrames")?.GetValue(def))
                  + " drawSize=" + Fmt(def.GetType().GetField("drawSize")?.GetValue(def)) + "\n");
        if (fc == null) return sb.ToString() + "no FlightComp on anim def";

        // ---- 1) 三层 XML 原始链：FlightMappingDef -> SegmentDef -> SampleDef
        sb.Append("\n--- L3 FlightMappingDef -> L2 SegmentDef -> L1 SampleDef ---\n");
        int covered = 0;
        var cover = new HashSet<int>();
        foreach (FlightMappingDef m in fc.flightMapping ?? new List<FlightMappingDef>())
        {
            sb.Append("phase=" + m.phase + " tag=" + (m.tag ?? "-") + " segments=" + (m.segments?.Count ?? 0) + "\n");
            foreach (SegmentDef s in m.segments ?? new List<SegmentDef>())
            {
                SampleDef sc = s.samples;
                int rs = sc?.rangeStart ?? -1, re = sc?.rangeEnd ?? -1;
                int sf = s.startFrame >= 0 ? s.startFrame : rs;
                int ef = s.endFrame >= 0 ? s.endFrame : re;
                int ti = Math.Max(1, sc?.tickInterval ?? 1);
                int dur = (Math.Abs(ef - sf) + 1) * ti;
                sb.Append("   seg=" + s.defName + " sample=" + (sc?.defName ?? "null")
                          + " range=[" + rs + "," + re + "] window=[" + sf + "," + ef + "]"
                          + " mode=" + s.mode + " dir=" + s.direction + " tick=" + ti + " duration=" + dur + "t\n");
                if (sc != null) for (int f = Math.Min(sf, ef); f <= Math.Max(sf, ef); f++) cover.Add(f);
            }
        }
        covered = cover.Count;
        int maxFrame = cover.Count == 0 ? 0 : cover.Max();
        bool complete = cover.Count > 0 && cover.Min() == 0 && maxFrame == covered - 1;
        sb.Append("COVERAGE: frames=" + covered + " min=" + (cover.Count == 0 ? -1 : cover.Min()) + " max=" + maxFrame
                  + " contiguous0..N=" + complete + "\n");

        // ---- 2) 运行时段编排（BuildSections 是模组自己的构建入口，实参级）
        var sections = fc.BuildSections(chosen, out var fast);
        sb.Append("\n--- runtime sections (FlightComp.BuildSections) ---\n");
        sb.Append("defaultPhases=" + sections.Count + " fastCruisePhases=" + fast.Count + "\n");
        foreach (var kv in sections) sb.Append(DumpChain("  " + kv.Key, kv.Value));
        foreach (var kv in fast) sb.Append(DumpChain("  [fast]" + kv.Key, kv.Value));
        sb.Append("durations: Takeoff=" + fc.TakeoffTicks + " Landing=" + fc.LandingTicks
                  + " CruiseStart=" + fc.CruiseStartTicks + " CruiseEnd=" + fc.CruiseEndTicks
                  + " MinCruise=" + fc.MinCruiseTicks + " Falling=" + fc.FallingTicks
                  + " RoundTripVerticalCost=" + fc.RoundTripVerticalCost + "\n");
        sb.Append("GetRoundTripVerticalCost(pawn,def)=" + fc.GetRoundTripVerticalCost(winged, def)
                  + "  -> takelandCost = /4 = " + (fc.GetRoundTripVerticalCost(winged, def) / 4f) + "\n");

        // ---- 3) Burst job 实参（复刻 FlightPathInjector.SolveAndInject 的同一套推导）
        var cfg = new FlightAStarConfig();
        var trk = winged.GetFlightTracker();
        cfg.remainingFuel = FlightFuelBudgetResolver.ToPathCostBudget(trk?.FuelSourceForTest);
        cfg.skyUsagePenalty = trk?.SkyUsagePenaltyForPath ?? 0f;
        var provider = def.comps?.OfType<IFlightTakelandCostProvider>().FirstOrDefault();
        if (provider != null) cfg.takelandCost = provider.GetRoundTripVerticalCost(winged, def) / 4f;
        int derived = FlightPathInjector.ResolveNodeBudget(map.Size.x, map.Size.z, cfg.takelandCost, FlightAStarConfig.MaxNodeBudget);
        cfg.nodeBudget = derived;
        float groundSpeed = winged.GetStatValue(StatDefOf.MoveSpeed);
        float flightSpeed = trk != null && trk.CruiseSpeedOverride.HasValue
            ? MathF.Max(0.0001f, trk.CruiseSpeedOverride.Value) * MathF.Max(0.0001f, trk.CruiseSpeedMultiplier)
            : 0f;
        cfg.PawnFlightPowerCostPerCell = SpeedRatioCalculator.ComputeSpeedRatio(groundSpeed, flightSpeed);

        sb.Append("\n--- Burst job config (FlightAStarConfig.ToJobConfig = 真实入参结构体) ---\n");
        sb.Append("defaults: takelandCost=5 nodeBudget=200000 PPCPC=1\n");
        sb.Append("derived : takelandCost=" + cfg.takelandCost + " nodeBudget=" + derived
                  + " (ResolveNodeBudget(" + map.Size.x + "," + map.Size.z + "," + cfg.takelandCost + "," + FlightAStarConfig.MaxNodeBudget + "))"
                  + " PPCPC=" + cfg.PawnFlightPowerCostPerCell + "\n");
        sb.Append("speeds  : groundMoveSpeed=" + groundSpeed + " flightCruiseSpeed=" + flightSpeed + "\n");
        FlightJobConfig job = cfg.ToJobConfig();
        sb.Append("FlightJobConfig { PawnFlightPowerCostPerCell=" + job.PawnFlightPowerCostPerCell
                  + ", takelandCost=" + job.takelandCost + ", nodeBudget=" + job.nodeBudget
                  + ", heuristic=" + job.heuristic + ", heuristicWeight=" + job.heuristicWeight
                  + ", remainingFuel=" + job.remainingFuel + ", skyUsagePenalty=" + job.skyUsagePenalty + " }\n");

        // ---- 4) 真跑：用同一份 cfg 调 Burst/托管求解器
        var runner = new FlightPathJobRunner();
        IntVec3 from = winged.Position;
        IntVec3 destLong = new IntVec3(Math.Min(map.Size.x - 3, from.x + 40), 0, Math.Min(map.Size.z - 3, from.z + 40));
        IntVec3 destShort = new IntVec3(Math.Min(map.Size.x - 3, from.x + 6), 0, Math.Min(map.Size.z - 3, from.z + 6));
        sb.Append("\n--- live solve (FlightPathJobRunner.Solve, 同一份实参) ---\n");
        sb.Append(DumpSolve("long  ", runner, map, from, destLong, cfg));
        sb.Append(DumpSolve("short ", runner, map, from, destShort, cfg));

        // ---- 5) 运行时播放状态（反射进 FlightChainPlayer）
        sb.Append("\n--- live anim state ---\n");
        try
        {
            object drv = null;
            for (Type ty = chosen.GetType(); ty != null && drv == null; ty = ty.BaseType)
                foreach (var f in ty.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                    if (f.FieldType.Name.Contains("Driver")) { drv = f.GetValue(chosen); break; }
            if (drv == null) sb.Append("driver not found on " + chosen.GetType().Name + "\n");
            else
            {
                sb.Append("driver=" + drv.GetType().Name + "\n");
                foreach (var name in new[] { "currentPhase", "current", "currentChainIndex", "lastSegment", "pending" })
                {
                    var f = drv.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (f == null) continue;
                    object v = f.GetValue(drv);
                    if (v is AnimationSection sec)
                        sb.Append("  " + name + "=Section{frame=" + sec.CurrentFrame + " window=[" + sec.FrameStart + "," + sec.FrameEnd
                                  + "] tick=" + sec.TickInterval + " mode=" + sec.Mode + " duration=" + sec.DurationTicks
                                  + " completed=" + sec.IsCompleted + "}\n");
                    else
                        sb.Append("  " + name + "=" + Fmt(v) + "\n");
                }
            }
        }
        catch (Exception e) { sb.Append("anim state EX " + e.GetType().Name + ": " + e.Message + "\n"); }

        return sb.ToString();
    }

    static Pawn FindWinged()
    {
        Map map = Find.CurrentMap;
        if (map == null) return null;
        foreach (Pawn p in map.mapPawns.FreeColonists)
        {
            AnimationComp c = null;
            try { c = FlightSourceManager.GetActiveComp(p); } catch { }
            if (c?.AnimationDef != null) return p;
        }
        return null;
    }

    /// <summary>运行时播放状态（实参级）：状态槽 + 链播放器当前段。</summary>
    public static string Dump()
    {
        Pawn p = FindWinged();
        return p == null ? "no winged pawn" : DumpState(p);
    }

    /// <summary>往状态槽注入相位/段标记（模拟寻路写槽），可选暂停游戏以冻结 tracker 的每 tick 覆写。</summary>
    public static string Inject(string phaseName, string segName, bool pause)
    {
        Pawn p = FindWinged();
        if (p == null) return "no winged pawn";
        FlightPathTracker tr = p.GetFlightTracker();
        if (tr == null) return "no tracker";
        if (pause) Find.TickManager.Pause();

        var phase = (FlightPhase)Enum.Parse(typeof(FlightPhase), phaseName);
        tr.Phase = phase;
        if (!string.IsNullOrEmpty(segName))
        {
            var segType = tr.State.Segment.GetType();
            tr.State.Segment = (FlightSegment)Enum.Parse(segType, segName);
        }
        System.Threading.Thread.MemoryBarrier();
        return "injected Phase=" + phaseName + " Segment=" + (segName ?? "-") + " paused=" + Find.TickManager.Paused + "\n" + DumpState(p);
    }

    public static string Restore()
    {
        Pawn p = FindWinged();
        if (p == null) return "no winged pawn";
        FlightPathTracker tr = p.GetFlightTracker();
        if (tr != null)
        {
            tr.Phase = FlightPhase.Ground;
            tr.State.Segment = FlightSegment.None;
        }
        Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
        return "restored Phase=Ground Segment=None paused=" + Find.TickManager.Paused + "\n" + DumpState(p);
    }

    static string DumpState(Pawn p)
    {
        var sb = new System.Text.StringBuilder();
        FlightPathTracker tr = p.GetFlightTracker();
        AnimationComp c = FlightSourceManager.GetActiveComp(p);
        sb.Append("tick=" + Find.TickManager.TicksGame + " paused=" + Find.TickManager.Paused
                  + " pawn=" + p.LabelShort
                  + " slot(Phase=" + (tr == null ? "?" : tr.Phase.ToString()) + ", Segment=" + (tr == null ? "?" : tr.State.Segment.ToString())
                  + ", IsInAir=" + (tr == null ? "?" : tr.IsInAir.ToString()) + ")\n");
        if (c == null) return sb.ToString() + "  no anim comp\n";
        sb.Append("  compFrame=" + c.GetFrameIndex() + "/" + c.GetTotalFrames() + " facing=" + p.Rotation + "\n");
        if (c.Driver is FlightChainPlayer cp)
        {
            sb.Append("  driver=FlightChainPlayer currentPhase=" + GetField(cp, "currentPhase")
                      + " chainIndex=" + GetField(cp, "currentChainIndex")
                      + " lastSegment=" + GetField(cp, "lastSegment") + "\n");
            sb.Append("  current=" + SecText(GetField(cp, "current") as AnimationSection)
                      + " pending=" + SecText(GetField(cp, "pending") as AnimationSection) + "\n");
        }
        else sb.Append("  driver=" + (c.Driver == null ? "null" : c.Driver.GetType().Name) + "\n");
        return sb.ToString();
    }

    static object GetField(object o, string name)
    {
        for (Type t = o.GetType(); t != null; t = t.BaseType)
        {
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f != null) return f.GetValue(o);
        }
        return null;
    }

    static string SecText(AnimationSection s)
        => s == null ? "null"
           : ("Section{f" + s.CurrentFrame + " win[" + s.FrameStart + ".." + s.FrameEnd + "] tick" + s.TickInterval
              + " " + s.Mode + " dur=" + s.DurationTicks + "t completed=" + s.IsCompleted + "}");

    /// <summary>真起飞：走模组自己的飞行命令入口（Sky 终点路径注入）。</summary>
    public static string Fly(string pawnName, int dx, int dz)
    {
        Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
        Map map = Find.CurrentMap;
        if (map == null) return "no map";
        Pawn p = string.IsNullOrEmpty(pawnName)
            ? FindWinged()
            : map.mapPawns.FreeColonists.FirstOrDefault(x => x.LabelShort == pawnName);
        if (p == null) return "pawn not found: " + pawnName;
        IntVec3 dest = new IntVec3(Math.Min(map.Size.x - 3, Math.Max(1, p.Position.x + dx)), 0,
                                   Math.Min(map.Size.z - 3, Math.Max(1, p.Position.z + dz)));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        FlightCommandHandler.OrderFlySingle(p, dest);
        sw.Stop();
        return "OrderFlySingle(" + p.LabelShort + " " + p.Position + " -> " + dest + ") took "
               + sw.Elapsed.TotalMilliseconds.ToString("F1") + "ms\n" + DumpState(p);
    }

    /// <summary>落地：下地面 Goto 指令，观察 Landing 段。</summary>
    public static string Land(string pawnName, int dx, int dz)
    {
        Map map = Find.CurrentMap;
        if (map == null) return "no map";
        Pawn p = map.mapPawns.FreeColonists.FirstOrDefault(x => x.LabelShort == pawnName);
        if (p == null) return "pawn not found: " + pawnName;
        IntVec3 dest = new IntVec3(Math.Min(map.Size.x - 3, Math.Max(1, p.Position.x + dx)), 0,
                                   Math.Min(map.Size.z - 3, Math.Max(1, p.Position.z + dz)));
        p.jobs.TryTakeOrderedJob(new Verse.AI.Job(RimWorld.JobDefOf.Goto, dest), Verse.AI.JobTag.Misc);
        return "Goto " + p.Position + " -> " + dest + "\n" + DumpState(p);
    }

    /// <summary>空中 → 地面落点：注入 Sky→Ground 路径（触发 CruiseEnd/Landing 段并真正落地）。</summary>
    public static string FlyToGround(string pawnName, int dx, int dz)
    {
        Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
        Map map = Find.CurrentMap;
        if (map == null) return "no map";
        Pawn p = map.mapPawns.FreeColonists.FirstOrDefault(x => x.LabelShort == pawnName);
        if (p == null) return "pawn not found: " + pawnName;
        IntVec3 dest = new IntVec3(Math.Min(map.Size.x - 3, Math.Max(1, p.Position.x + dx)), 0,
                                   Math.Min(map.Size.z - 3, Math.Max(1, p.Position.z + dz)));
        var tr = p.GetFlightTracker();
        FlightLayer startLayer = (tr != null && tr.IsInAir) ? FlightLayer.Sky : FlightLayer.Ground;
        bool ok = FlightPathInjector.SolveAndInject(p, p.Position, dest, Verse.AI.PathEndMode.OnCell, startLayer, FlightLayer.Ground);
        return "SolveAndInject(" + startLayer + "->Ground at " + dest + ") ok=" + ok + "\n" + DumpState(p);
    }

    static string DumpChain(string label, AnimationSection[] arr)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(label + " -> " + arr.Length + " sections:");
        foreach (var s in arr)
            sb.Append(" [f" + s.FrameStart + ".." + s.FrameEnd + " tick" + s.TickInterval + " " + s.Mode + " dur=" + s.DurationTicks + "t]");
        sb.Append("\n");
        return sb.ToString();
    }

    static string DumpSolve(string tag, FlightPathJobRunner runner, Map map, IntVec3 from, IntVec3 dest, FlightAStarConfig cfg)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            FlightPathResult r = runner.Solve(map, from, dest, FlightLayer.Ground, FlightLayer.Ground, cfg);
            sw.Stop();
            if (!r.found)
                return "  " + tag + " " + from + "->" + dest + " FAILED reason=" + r.failReason
                       + " exceededBudget=" + r.exceededBudget + " nodesExpanded=" + r.nodesExpanded
                       + " (" + sw.Elapsed.TotalMilliseconds.ToString("F1") + "ms)\n";
            var p = r.path;
            int air = p.points.Count(pt => pt.yh != FlightLayers.Ground);
            return "  " + tag + " " + from + "->" + dest + " found len=" + p.points.Count
                   + " airPoints=" + air + " totalCost=" + p.totalCost + " flightCost=" + p.flightCost
                   + " nodesExpanded=" + p.nodesExpanded + " exceededBudget=" + p.exceededBudget
                   + " takeoff=" + p.takeoffCell + " landing=" + p.landingCell
                   + " (" + sw.Elapsed.TotalMilliseconds.ToString("F1") + "ms)\n";
        }
        catch (Exception e) { return "  " + tag + " EX " + e.GetType().Name + ": " + e.Message + "\n"; }
    }
}
