using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace UELoader
{
    /// <summary>
    /// 相机跟随管理器：把游戏相机锁定到指定 Thing（任意 Thing，不限 Pawn），
    /// 每 LateUpdate 将相机 XZ 位置同步到目标 DrawPos，保持当前高度（缩放级别）不变。
    /// 目标被销毁或离开当前地图时自动解锁。
    /// 供 MCP 工具 camera_follow_thing 调用。
    /// </summary>
    public static class CameraFollowManager
    {
        static Thing followTarget;
        static int followThingId;           // 缓存 thingIDNumber（thing 被 GC 后仍可日志）
        static string followDefName;        // 缓存 defName
        static bool initialized;

        static CameraFollowBehaviour followComponent;

        // ---- 公开查询 ----

        public static bool IsFollowing => followTarget != null;

        /// <summary>初始化（ExplorerBootstrap.Initialize 末尾调用）。幂等。</summary>
        public static void Initialize()
        {
            if (initialized) return;
            initialized = true;
            try
            {
                var go = new GameObject("UELoader_CameraFollow");
                UnityEngine.Object.DontDestroyOnLoad(go);
                followComponent = go.AddComponent<CameraFollowBehaviour>();
                UEHttpLog.Message("[CameraFollow] 已初始化");
            }
            catch (Exception ex)
            {
                UEHttpLog.Error("[CameraFollow] Initialize failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>锁定相机到 thingId（当前地图内搜索）。</summary>
        public static Dictionary<string, object> Follow(int thingId)
        {
            try
            {
                Map map = Find.CurrentMap;
                if (map == null)
                    return Err("NO_MAP", "当前无地图");

                Thing thing = FindThing(map, thingId);
                if (thing == null)
                    return Err("THING_NOT_FOUND",
                        "thingId=" + thingId + " 未找到（当前地图 spawnedThings 中无匹配）");

                followTarget = thing;
                followThingId = thing.thingIDNumber;
                followDefName = thing.def?.defName ?? "?";

                UEHttpLog.Message("[CameraFollow] 锁定 thingId=" + thingId
                    + " (" + thing.LabelCap + ", def=" + followDefName + ")");

                return Ok("follow", "已锁定 " + thing.LabelCap
                    + " (thingId=" + thingId + ", def=" + followDefName + ")",
                    StatusData());
            }
            catch (Exception ex)
            {
                return Err("FOLLOW_FAILED", ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>手动解锁。</summary>
        public static Dictionary<string, object> Unfollow()
        {
            if (followTarget == null)
                return Ok("unfollow", "当前未锁定任何对象", StatusData());

            string label = followTarget.LabelCap;
            int id = followTarget.thingIDNumber;
            followTarget = null;

            UEHttpLog.Message("[CameraFollow] 手动解锁 thingId=" + id + " (" + label + ")");
            return Ok("unfollow", "已解锁 " + label + " (thingId=" + id + ")", StatusData());
        }

        /// <summary>状态查询。</summary>
        public static Dictionary<string, object> GetStatus()
        {
            return Ok("status", followTarget != null
                ? "锁定中: " + followTarget.LabelCap + " (thingId=" + followTarget.thingIDNumber + ")"
                : "未锁定", StatusData());
        }

        // ---- LateUpdate 调用（CameraFollowBehaviour 每帧调用） ----

        internal static void LateUpdateTick()
        {
            if (followTarget == null) return;

            try
            {
                // 自动解锁检查
                if (followTarget.Destroyed || !followTarget.Spawned)
                {
                    AutoUnlock("目标已销毁或离开地图 (thingId=" + followThingId
                        + ", def=" + followDefName + ")");
                    return;
                }

                // 目标可能换了地图（传送/ caravan），此时也解锁
                if (followTarget.Map != Find.CurrentMap)
                {
                    AutoUnlock("目标已切换地图 (thingId=" + followThingId + ")");
                    return;
                }

                // 同步相机 XZ 到目标 DrawPos（保持 Y 高度 = 缩放级别不变）
                Camera cam = Camera.main;
                if (cam == null) return;

                Vector3 drawPos = followTarget.DrawPos;
                Vector3 camPos = cam.transform.position;
                cam.transform.position = new Vector3(drawPos.x, camPos.y, drawPos.z);

                // 同步 CameraDriver 防止游戏相机控制器把镜头拉回去
                try
                {
                    CameraDriver driver = Find.CameraDriver;
                    if (driver != null)
                    {
                        // JumpToCurrentMapLoc 会把相机跳转到目标格子（RimWorld 标准 API）
                        driver.JumpToCurrentMapLoc(followTarget.Position);
                    }
                }
                catch { /* CameraDriver API 不可用时静默跳过 */ }
            }
            catch (Exception ex)
            {
                // LateUpdate 内异常不能抛出（会崩 Unity），静默记日志
                UEHttpLog.Warning("[CameraFollow] LateUpdate 异常: " + ex.GetType().Name + ": " + ex.Message);
                AutoUnlock("LateUpdate 异常，自动解锁");
            }
        }

        // ---- 内部实现 ----

        static void AutoUnlock(string reason)
        {
            followTarget = null;
            UEHttpLog.Message("[CameraFollow] 自动解锁: " + reason);
        }

        static Thing FindThing(Map map, int thingId)
        {
            // 优先遍历 spawnedThings（最快，通常百量级）
            foreach (Thing t in map.spawnedThings)
                if (t.thingIDNumber == thingId)
                    return t;
            // 回退 AllThings（含非地图生成的）
            try
            {
                foreach (Thing t in map.listerThings.AllThings)
                    if (t.thingIDNumber == thingId)
                        return t;
            }
            catch { }
            return null;
        }

        static Dictionary<string, object> StatusData()
        {
            var d = new Dictionary<string, object>();
            d["following"] = followTarget != null;
            if (followTarget != null)
            {
                d["thingId"] = followTarget.thingIDNumber;
                d["label"] = followTarget.LabelCap;
                d["defName"] = followTarget.def?.defName;
                d["spawned"] = followTarget.Spawned;
                d["destroyed"] = followTarget.Destroyed;
                try
                {
                    if (followTarget.Spawned)
                        d["position"] = followTarget.Position.ToString();
                }
                catch { }
            }
            return d;
        }

        static Dictionary<string, object> Ok(string mode, string message, Dictionary<string, object> data = null)
        {
            var r = new Dictionary<string, object>
            {
                { "success", true },
                { "mode", mode },
                { "message", message }
            };
            if (data != null) r["data"] = data;
            return r;
        }

        static Dictionary<string, object> Err(string errorCode, string error)
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
    /// 挂载到 DontDestroyOnLoad GameObject 的 MonoBehaviour，
    /// 在 LateUpdate 中调用 CameraFollowManager.LateUpdateTick()。
    /// 选择 LateUpdate 而非 Update：确保在游戏逻辑（Thing 位置更新）之后再移动相机，
    /// 避免一帧延迟导致画面抖动。
    /// </summary>
    public class CameraFollowBehaviour : MonoBehaviour
    {
        void LateUpdate()
        {
            CameraFollowManager.LateUpdateTick();
        }
    }
}
