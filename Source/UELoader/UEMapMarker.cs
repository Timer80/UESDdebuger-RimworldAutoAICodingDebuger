using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace UELoader
{
    /// <summary>
    /// 「地图坐标光标」—— AI 报坐标时在地图上打的可见标记。
    ///
    /// 解决的痛点：AI 经常报「某坐标有敌人/有矿」，但玩家在地图上根本对不上号。
    /// 本工具在地图指定格画一个炼狱魔王炮（Diabolus 地狱球炮）风格的瞄准光标，
    /// 并在地面上直接写出坐标文字；玩家鼠标左键点一下光标即可消除。
    ///
    /// 实现要点：
    ///  1) 视觉：自研 shader（见 UECoordCursorShader 与 ShaderProject/），程序化生成准星 +
    ///     雷达扫描扇面 + 声纳脉冲环，颜色由 _Color 完全控制。
    ///     为什么不用游戏内置的 Mote_HellsphereCannon_Target：那个 shader 虽然声明了 _Color
    ///     却从不读取它，颜色烘焙在贴图里，**无法换色**（详见 UECoordCursorShader 注释）。
    ///  2) 绘制：自己的 MonoBehaviour 里 Graphics.DrawMesh，**不挂 map.components**。
    ///     MapComponent 会被 Map.ExposeData 一并 Scribe，运行期塞进去的类型会污染存档。
    ///  3) 点击消除：Harmony 前缀补丁 MapInterface.HandleMapClicks —— 这个时机在
    ///     WindowStack/主按钮之后、设计器/目标选择器之前，既能保证不误吞 UI 点击，
    ///     又能 return false 阻止这一次点击被当成普通地图操作。
    ///     （补丁装不上时自动降级为 OnGUI 里检测点击，只是不再拦截原操作。）
    /// </summary>
    public static class UEMapMarker
    {
        public const int MaxMarkers = 8;
        public const float DefaultSize = 8f;    // 与内置炼狱魔王炮指示器 drawSize(8,8) 一致
        public const float MinSize = 1f;
        public const float MaxSize = 40f;

        /// <summary>鼠标离光标中心多少像素内算「点中」。</summary>
        const float ClickPixelRadius = 14f;

        const float FadeInSeconds = 0.30f;
        const float FadeOutSeconds = 0.20f;

        // 实测回显 #FF4A1F：0.12f × 255 = 30.6 → 31 = 0x1F（旧注释与 MCP schema 文案里写的 1E 是错的）
        static readonly Color DefaultColor = new Color(1f, 0.29f, 0.12f, 1f);   // #FF4A1F 炼狱魔王同款橙红

        // ---------------------------------------------------------------- 数据模型

        sealed class Marker
        {
            public string id;
            public Map map;
            public IntVec3 cell;
            public Color color;
            public float size;
            public string label;
            public float ttlSeconds;
            public float age;
            public Material material;
            public bool fadingOut;
            public float fadeOutAge;
        }

        static readonly List<Marker> markers = new List<Marker>();
        static UEMapMarkerDriver driver;
        static bool clickPatchTried;
        static bool clickPatchInstalled;

        // ---------------------------------------------------------------- 对外 API
        // 全部必须在主线程调用（经 UEHttpHandler.RunOnMain 投递），因为会碰 UnityEngine 对象。

        public static Dictionary<string, object> Set(
            string id, int? x, int? z, string colorSpec, float? size, string label,
            float? ttlSeconds, int? mapId, bool keepExisting)
        {
            Map map;
            if (!TryResolveMap(mapId, out map))
                return Err("当前未进入地图（或无此 map_id）。请先加载存档/进入地图。", "MAP_NOT_LOADED");

            if (!x.HasValue || !z.HasValue)
                return Err("action=set 需要 x 与 z（目标格坐标）", "MISSING_PARAMETER");

            IntVec3 cell = new IntVec3(x.Value, 0, z.Value);
            if (!cell.InBounds(map))
                return Err($"坐标 ({cell.x}, {cell.z}) 超出地图范围（当前地图 {map.Size.x} x {map.Size.z}）", "OUT_OF_BOUNDS");

            Shader shader = UECoordCursorShader.Get();
            if (shader == null)
            {
                return Err("坐标光标 shader 未载入：" + (UECoordCursorShader.LoadError ?? "未知原因")
                    + "。请在 ShaderProject 里执行 Unity 菜单 UESDdebuger → Build Coord Cursor AssetBundle "
                    + "（或命令行批处理）重新打包。期望路径: " + UECoordCursorShader.ExpectedPaths(),
                    "SHADER_NOT_LOADED");
            }

            Color color;
            if (!TryParseColor(colorSpec, out color, out string colorError))
                return Err(colorError, "INVALID_COLOR");

            string markerId = string.IsNullOrEmpty(id) || id.Trim().Length == 0 ? "default" : id.Trim();

            if (!keepExisting)
                RemoveAll(m => m.id != markerId);          // 保留同名（随后会被替换）

            RemoveAll(m => m.id == markerId);              // 同 id 覆盖

            float finalSize = Mathf.Clamp(size ?? DefaultSize, MinSize, MaxSize);

            Material mat = CreateMaterial(shader, color);
            if (mat == null)
                return Err("创建 material 失败", "MATERIAL_FAILED");

            Marker marker = new Marker
            {
                id = markerId,
                map = map,
                cell = cell,
                color = color,
                size = finalSize,
                label = string.IsNullOrEmpty(label) ? null : label,
                ttlSeconds = Mathf.Max(0f, ttlSeconds ?? 0f),
                age = 0f,
                material = mat
            };
            markers.Add(marker);

            // 超出上限：丢掉最老的
            while (markers.Count > MaxMarkers)
                RemoveAt(0);

            EnsureDriver();

            return Ok(new Dictionary<string, object>
            {
                { "marker", Describe(marker) },
                { "count", markers.Count },
                { "shader", shader.name },
                { "hint", "玩家用鼠标左键点击该光标即可消除。"
                          + (marker.ttlSeconds > 0f
                              ? $"本光标将在 {marker.ttlSeconds:0.#} 秒后自动消失。"
                              : "本光标会一直保留直到被点击或显式 clear。") }
            });
        }

        public static Dictionary<string, object> Clear(string id, int? mapId)
        {
            Map map = null;
            if (mapId.HasValue && !TryResolveMap(mapId, out map))
                return Err("无此 map_id: " + mapId, "MAP_NOT_LOADED");

            string markerId = string.IsNullOrEmpty(id) ? null : id.Trim();

            if (markerId == null)
            {
                int n = map == null ? markers.Count : CountOn(map);
                RemoveAll(m => map == null || m.map == map);
                return Ok(new Dictionary<string, object>
                {
                    { "cleared", n },
                    { "count", markers.Count },
                    { "scope", map == null ? "all-maps" : ("map " + map.Index) }
                });
            }

            int removed = CountById(markerId);
            if (removed == 0)
                return Err("找不到坐标光标 id=" + markerId + "（用 action=list 看现有光标）", "MARKER_NOT_FOUND");

            RemoveAll(m => m.id == markerId);
            return Ok(new Dictionary<string, object>
            {
                { "cleared", removed },
                { "count", markers.Count },
                { "id", markerId }
            });
        }

        public static Dictionary<string, object> Recolor(string id, string colorSpec)
        {
            if (string.IsNullOrEmpty(id))
                return Err("action=recolor 需要 id", "MISSING_PARAMETER");

            Color color;
            if (!TryParseColor(colorSpec, out color, out string colorError))
                return Err(colorError, "INVALID_COLOR");

            Marker found = null;
            for (int i = 0; i < markers.Count; i++)
            {
                if (markers[i].id == id.Trim()) { found = markers[i]; break; }
            }
            if (found == null)
                return Err("找不到坐标光标 id=" + id, "MARKER_NOT_FOUND");

            found.color = color;
            if (found.material != null)
                found.material.SetColor("_Color", new Color(color.r, color.g, color.b, 1f));

            return Ok(new Dictionary<string, object>
            {
                { "marker", Describe(found) },
                { "count", markers.Count }
            });
        }

        public static Dictionary<string, object> List()
        {
            var list = new List<object>();
            for (int i = 0; i < markers.Count; i++)
                list.Add(Describe(markers[i]));

            var data = new Dictionary<string, object>
            {
                { "count", markers.Count },
                { "max", MaxMarkers },
                { "markers", list },
                { "shaderReady", UECoordCursorShader.Get() != null }
            };
            if (list.Count == 0)
                data["hint"] = "当前没有坐标光标。用 action=set 并给出 x/z 就能在地图上打一个。";
            return Ok(data);
        }

        // ---------------------------------------------------------------- 每帧绘制

        internal static void DrawAll()
        {
            if (markers.Count == 0)
                return;

            Map current = null;
            bool drawMap = false;
            try
            {
                current = Find.CurrentMap;
                drawMap = WorldRendererUtility.DrawingMap;
            }
            catch { }

            float dt = Time.deltaTime;
            if (dt < 0f) dt = 0f;
            if (dt > 0.5f) dt = 0.5f;   // 掉帧/断点恢复时不跳变

            for (int i = markers.Count - 1; i >= 0; i--)
            {
                Marker m = markers[i];

                // 地图已卸载（换存档/回到主菜单）→ 丢弃
                if (m.map == null || !IsLiveMap(m.map))
                {
                    RemoveAt(i);
                    continue;
                }

                m.age += dt;
                if (m.fadingOut)
                {
                    m.fadeOutAge += dt;
                    if (m.fadeOutAge >= FadeOutSeconds)
                    {
                        RemoveAt(i);
                        continue;
                    }
                }
                else if (m.ttlSeconds > 0f && m.age >= m.ttlSeconds)
                {
                    BeginFadeOut(m);
                }

                if (!drawMap || current == null || m.map != current)
                    continue;

                DrawOne(m, ComputeAlpha(m));
            }
        }

        static void DrawOne(Marker m, float alpha)
        {
            if (m.material == null || m.material.shader == null)
                return;

            try
            {
                m.material.SetFloat("_Alpha", alpha);

                Matrix4x4 matrix = default(Matrix4x4);
                matrix.SetTRS(
                    m.cell.ToVector3ShiftedWithAltitude(AltitudeLayer.MoteOverhead),
                    Quaternion.identity,
                    new Vector3(m.size, 1f, m.size));

                Graphics.DrawMesh(MeshPool.plane10, matrix, m.material, 0);
            }
            catch (Exception ex)
            {
                UEHttpLog.Warning("[UEMapMarker] 绘制失败: " + ex.Message);
            }
        }

        static float ComputeAlpha(Marker m)
        {
            float a = 1f;
            if (m.age < FadeInSeconds && FadeInSeconds > 0f)
                a = Mathf.Clamp01(m.age / FadeInSeconds);
            if (m.fadingOut)
                a = Mathf.Min(a, Mathf.Clamp01(1f - m.fadeOutAge / FadeOutSeconds));
            return a;
        }

        // ---------------------------------------------------------------- 地面文字

        internal static void HandleGUI()
        {
            if (markers.Count == 0)
                return;

            Map current;
            try
            {
                if (!WorldRendererUtility.DrawingMap || Find.Camera == null)
                    return;
                current = Find.CurrentMap;
            }
            catch { return; }
            if (current == null)
                return;

            for (int i = markers.Count - 1; i >= 0; i--)
            {
                Marker m = markers[i];
                if (m.map != current || m.fadingOut)
                    continue;

                try { DrawLabel(m); }
                catch { }
            }

            // Harmony 补丁装不上时，退回在 OnGUI 里检测点击（代价：那一次点击仍会被游戏当普通地图点击处理）
            if (!clickPatchInstalled)
                TryConsumeMapClick();
        }

        static void DrawLabel(Marker m)
        {
            string labelLine = string.IsNullOrEmpty(m.label) ? null : m.label;
            string coordLine = m.cell.x + ", " + m.cell.z;
            string hintLine = m.ttlSeconds > 0f
                ? "点击消除 · 剩余 " + Mathf.Max(0f, m.ttlSeconds - m.age).ToString("0.#") + "s"
                : "点击消除";

            Vector3 world = m.cell.ToVector3ShiftedWithAltitude(AltitudeLayer.MoteOverhead);
            Vector3 sp = Find.Camera.WorldToScreenPoint(world);
            if (sp.z < 0f)
                return;   // 相机背后

            // sp 是原始像素坐标（原点左下）；先按 UIScale 归一，再翻转 y 到 GUI 坐标，
            // 与 GenMapUI.DrawText 的算法完全一致。
            Vector2 p = new Vector2(sp.x, sp.y) / Prefs.UIScale;
            p.y = (float)UI.screenHeight - p.y;

            // 文字块整体抬到**光圈外沿之上**：把「光标中心 + 半个边长」也投影一次，
            // 得到光圈在屏幕上的像素半径。这样任何缩放倍率下文字都不会压住准星中心
            // —— 那正是用来指示目标格的部分，被字挡住就白做了。
            float ringPx = 0f;
            try
            {
                Vector3 edge = Find.Camera.WorldToScreenPoint(
                    world + new Vector3(m.size * 0.5f, 0f, 0f));
                ringPx = Mathf.Abs(edge.x - sp.x) / Prefs.UIScale;
            }
            catch { }
            float anchorLift = Mathf.Clamp(ringPx, 22f, 400f) + 6f;

            const float pad = 8f;
            const float gap = 2f;

            // ---- 量尺寸：主行（标题 + 坐标）用 Medium，提示行用 Tiny ----
            var mainLines = new List<string>(2);
            if (labelLine != null) mainLines.Add(labelLine);
            mainLines.Add(coordLine);

            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Medium;

            int n = mainLines.Count;
            var mainHeights = new float[n];
            float mainH = 0f;
            float mainW = 0f;
            for (int i = 0; i < n; i++)
            {
                mainHeights[i] = Text.CalcHeight(mainLines[i], 600f);
                mainH += mainHeights[i] + (i > 0 ? gap : 0f);
                mainW = Mathf.Max(mainW, Text.CalcSize(mainLines[i]).x);
            }

            Text.Font = GameFont.Tiny;
            float hintH = Text.CalcHeight(hintLine, 600f);
            float hintW = Text.CalcSize(hintLine).x;

            float blockW = Mathf.Max(mainW, hintW) + pad * 2f;
            float blockH = mainH + gap + hintH;
            float top = p.y - anchorLift - blockH;

            // 完全在屏幕外就跳过
            if (top > (float)UI.screenHeight || top + blockH < 0f)
                return;

            // ---- 逐行绘制（黑描边 + 呼吸），从锚点向上堆叠 ----
            Color oldColor = GUI.color;
            Text.Anchor = TextAnchor.UpperCenter;

            float y = top;
            Text.Font = GameFont.Medium;
            for (int i = 0; i < n; i++)
            {
                Rect r = new Rect(p.x - blockW * 0.5f, y, blockW, mainHeights[i]);
                DrawOutlined(r, mainLines[i], m.color);
                y += mainHeights[i] + gap;
            }

            Text.Font = GameFont.Tiny;
            {
                Rect r = new Rect(p.x - blockW * 0.5f, y, blockW, hintH);
                DrawOutlined(r, hintLine, new Color(1f, 1f, 1f, 0.8f));
            }

            GUI.color = oldColor;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        /// <summary>黑描边 + 呼吸着色的文字：任何地形底色上都读得清。</summary>
        static void DrawOutlined(Rect r, string text, Color color)
        {
            GUI.color = new Color(0f, 0f, 0f, 0.85f);
            Widgets.Label(new Rect(r.x - 1f, r.y, r.width, r.height), text);
            Widgets.Label(new Rect(r.x + 1f, r.y, r.width, r.height), text);
            Widgets.Label(new Rect(r.x, r.y - 1f, r.width, r.height), text);
            Widgets.Label(new Rect(r.x, r.y + 1f, r.width, r.height), text);

            GUI.color = color;
            Widgets.Label(r, text);
        }

        // ---------------------------------------------------------------- 点击消除

        /// <summary>
        /// MapInterface.HandleMapClicks 前缀：命中光标则开始淡出并 return false，
        /// 阻止这一次点击落进设计器/目标选择器。
        /// </summary>
        internal static bool HandleMapClicks_Prefix()
        {
            return !TryConsumeMapClick();
        }

        internal static bool TryConsumeMapClick()
        {
            if (markers.Count == 0)
                return false;

            try
            {
                Event ev = Event.current;
                if (ev == null || ev.type != EventType.MouseDown || ev.button != 0)
                    return false;
                if (!WorldRendererUtility.DrawingMap)
                    return false;

                Map current = Find.CurrentMap;
                if (current == null || Find.Camera == null)
                    return false;

                // 点在 UI 窗口上时不算
                if (Find.WindowStack != null
                    && Find.WindowStack.GetWindowAt(UI.MousePositionOnUIInverted) != null)
                    return false;

                Vector2 mouse = Input.mousePosition;
                if (mouse.x < 0f || mouse.y < 0f
                    || mouse.x > Screen.width || mouse.y > Screen.height)
                    return false;

                Marker hit = null;
                float best = float.MaxValue;
                for (int i = 0; i < markers.Count; i++)
                {
                    Marker m = markers[i];
                    if (m.map != current || m.fadingOut)
                        continue;

                    Vector3 sp = Find.Camera.WorldToScreenPoint(
                        m.cell.ToVector3ShiftedWithAltitude(AltitudeLayer.MoteOverhead));
                    if (sp.z < 0f)
                        continue;

                    float d = Vector2.Distance(mouse, new Vector2(sp.x, sp.y));
                    if (d <= ClickPixelRadius && d < best)
                    {
                        best = d;
                        hit = m;
                    }
                }

                if (hit == null)
                    return false;

                BeginFadeOut(hit);
                ev.Use();

                try
                {
                    Messages.Message(
                        "已消除坐标光标 " + (hit.id == "default" ? "" : hit.id + " ")
                        + "(" + hit.cell.x + ", " + hit.cell.z + ")",
                        MessageTypeDefOf.SilentInput, false);
                }
                catch { }

                return true;
            }
            catch (Exception ex)
            {
                UEHttpLog.Warning("[UEMapMarker] 点击检测失败: " + ex.Message);
                return false;
            }
        }

        // ---------------------------------------------------------------- 驱动 / 补丁

        static void EnsureDriver()
        {
            if (driver == null)
            {
                try
                {
                    var go = new GameObject("UELoader_MapMarker");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    driver = go.AddComponent<UEMapMarkerDriver>();
                }
                catch (Exception ex)
                {
                    UEHttpLog.Warning("[UEMapMarker] 创建绘制驱动失败: " + ex.Message);
                }
            }

            InstallClickPatch();
        }

        static void InstallClickPatch()
        {
            if (clickPatchTried)
                return;
            clickPatchTried = true;

            try
            {
                var harmony = new Harmony("UESDdebuger.mapmarker");
                MethodInfo target = AccessTools.Method(typeof(MapInterface), "HandleMapClicks");
                if (target == null)
                {
                    UEHttpLog.Warning("[UEMapMarker] 未找到 MapInterface.HandleMapClicks，"
                        + "点击消除降级为 OnGUI 检测（那一次点击仍会被游戏当普通地图点击处理）");
                    return;
                }

                MethodInfo prefix = AccessTools.Method(typeof(UEMapMarker), nameof(HandleMapClicks_Prefix));
                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                clickPatchInstalled = true;
                UEHttpLog.Info("[UEMapMarker] 已挂载 MapInterface.HandleMapClicks 前缀补丁（左键点击光标即消除）");
            }
            catch (Exception ex)
            {
                UEHttpLog.Warning("[UEMapMarker] 安装点击补丁失败: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- 颜色

        static readonly Dictionary<string, Color> NamedColors =
            new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
            {
                { "red",     new Color(1.00f, 0.15f, 0.12f) },
                { "orange",  new Color(1.00f, 0.45f, 0.08f) },
                { "gold",    new Color(1.00f, 0.75f, 0.15f) },
                { "yellow",  new Color(1.00f, 0.95f, 0.25f) },
                { "lime",    new Color(0.60f, 1.00f, 0.25f) },
                { "green",   new Color(0.20f, 0.95f, 0.35f) },
                { "teal",    new Color(0.15f, 0.90f, 0.75f) },
                { "cyan",    new Color(0.25f, 0.90f, 1.00f) },
                { "blue",    new Color(0.30f, 0.55f, 1.00f) },
                { "purple",  new Color(0.65f, 0.35f, 1.00f) },
                { "magenta", new Color(1.00f, 0.25f, 0.85f) },
                { "pink",    new Color(1.00f, 0.55f, 0.75f) },
                { "white",   new Color(1.00f, 1.00f, 1.00f) },
                { "black",   new Color(0.10f, 0.10f, 0.10f) },
                { "gray",    new Color(0.65f, 0.65f, 0.65f) },
                { "grey",    new Color(0.65f, 0.65f, 0.65f) },
            };

        static bool TryParseColor(string spec, out Color color, out string error)
        {
            color = DefaultColor;
            error = null;

            if (string.IsNullOrEmpty(spec) || spec.Trim().Length == 0)
                return true;   // 用默认色

            string s = spec.Trim();

            Color named;
            if (NamedColors.TryGetValue(s.Replace(" ", "").Replace("_", "").Replace("-", ""), out named))
            {
                color = named;
                return true;
            }

            string hex = s.StartsWith("#") ? s.Substring(1) : s;
            if (IsHex(hex))
            {
                if (hex.Length == 3)
                {
                    hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
                }
                if (hex.Length == 6 || hex.Length == 8)
                {
                    int r = int.Parse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    int g = int.Parse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    int b = int.Parse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    color = new Color(r / 255f, g / 255f, b / 255f);
                    return true;
                }
            }

            if (s.IndexOf(',') >= 0)
            {
                string[] parts = s.Split(',');
                if (parts.Length >= 3)
                {
                    float r, g, b;
                    if (float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out r)
                        && float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out g)
                        && float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out b))
                    {
                        // 允许 0-255 或 0-1 两种写法
                        if (r > 1f || g > 1f || b > 1f) { r /= 255f; g /= 255f; b /= 255f; }
                        color = new Color(Mathf.Clamp01(r), Mathf.Clamp01(g), Mathf.Clamp01(b));
                        return true;
                    }
                }
            }

            error = "无法识别颜色 \"" + spec + "\"。可用：英文名（red/orange/gold/yellow/lime/green/teal/cyan/blue/"
                    + "purple/magenta/pink/white/gray）或 #RRGGBB（也接受 RRGGBB / #RGB）或 \"r,g,b\"（0-255 或 0-1）";
            return false;
        }

        static bool IsHex(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok) return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- 内部工具

        static Material CreateMaterial(Shader shader, Color color)
        {
            try
            {
                var mat = new Material(shader);
                SetColorProp(mat, "_Color", new Color(color.r, color.g, color.b, 1f));
                SetFloatProp(mat, "_Alpha", 1f);
                SetFloatProp(mat, "_Intensity", 1.1f);
                SetFloatProp(mat, "_RingWidth", 0.022f);
                return mat;
            }
            catch (Exception ex)
            {
                UEHttpLog.Warning("[UEMapMarker] new Material 失败: " + ex.Message);
                return null;
            }
        }

        static void SetColorProp(Material m, string name, Color v)
        {
            if (m.HasProperty(name)) m.SetColor(name, v);
        }

        static void SetFloatProp(Material m, string name, float v)
        {
            if (m.HasProperty(name)) m.SetFloat(name, v);
        }

        static bool TryResolveMap(int? mapId, out Map map)
        {
            map = null;
            try
            {
                if (!mapId.HasValue)
                {
                    map = Find.CurrentMap;
                    return map != null;
                }
                List<Map> maps = Find.Maps;
                if (maps == null) return false;
                for (int i = 0; i < maps.Count; i++)
                {
                    if (maps[i] != null && maps[i].Index == mapId.Value)
                    {
                        map = maps[i];
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        static bool IsLiveMap(Map m)
        {
            try
            {
                List<Map> maps = Find.Maps;
                return maps != null && maps.Contains(m);
            }
            catch { return false; }
        }

        static void BeginFadeOut(Marker m)
        {
            if (m.fadingOut) return;
            m.fadingOut = true;
            m.fadeOutAge = 0f;
        }

        static int CountById(string id)
        {
            int n = 0;
            for (int i = 0; i < markers.Count; i++)
                if (markers[i].id == id) n++;
            return n;
        }

        static int CountOn(Map map)
        {
            int n = 0;
            for (int i = 0; i < markers.Count; i++)
                if (markers[i].map == map) n++;
            return n;
        }

        static void RemoveAll(Predicate<Marker> pred)
        {
            for (int i = markers.Count - 1; i >= 0; i--)
            {
                if (pred(markers[i]))
                    RemoveAt(i);
            }
        }

        static void RemoveAt(int index)
        {
            Marker m = markers[index];
            markers.RemoveAt(index);
            if (m.material != null)
            {
                try { UnityEngine.Object.Destroy(m.material); } catch { }
                m.material = null;
            }
        }

        static Dictionary<string, object> Describe(Marker m)
        {
            var d = new Dictionary<string, object>
            {
                { "id", m.id },
                { "mapId", (m.map != null) ? (object)m.map.Index : null },
                { "x", m.cell.x },
                { "z", m.cell.z },
                { "color", "#" + ColorUtility.ToHtmlStringRGB(m.color) },
                { "size", m.size },
                { "label", m.label },
                { "ttlSeconds", m.ttlSeconds },
                { "ageSeconds", (float)Math.Round(m.age, 2) },
                { "dismissing", m.fadingOut }
            };
            if (m.ttlSeconds > 0f)
                d["remainingSeconds"] = (float)Math.Round(Mathf.Max(0f, m.ttlSeconds - m.age), 2);
            return d;
        }

        static Dictionary<string, object> Ok(Dictionary<string, object> data)
        {
            return new Dictionary<string, object> { { "success", true }, { "data", data } };
        }

        static Dictionary<string, object> Err(string error, string errorCode)
        {
            return new Dictionary<string, object>
            {
                { "success", false },
                { "error", error },
                { "errorCode", errorCode }
            };
        }
    }

    /// <summary>
    /// 坐标光标的绘制驱动。独立 GameObject + DontDestroyOnLoad，
    /// 与 UEMainThreadDispatcher 同款生命周期；**不**进 map.components（避免存档写入未知类型）。
    /// </summary>
    public class UEMapMarkerDriver : MonoBehaviour
    {
        void Update()
        {
            UEMapMarker.DrawAll();
        }

        void OnGUI()
        {
            UEMapMarker.HandleGUI();
        }
    }
}
