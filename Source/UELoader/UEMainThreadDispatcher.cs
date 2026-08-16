using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using Verse;

namespace UELoader
{
    /// <summary>主线程操作状态（P1-CS-1 方案 A）。</summary>
    public enum ActionState
    {
        Pending,   // 已入队等待主线程消费
        Running,   // 已出队、正在主线程执行
        Completed, // 正常完成且无异常
        TimedOut,  // 等待超时，动作仍在主线程执行、结果弃置
        Faulted    // 执行完成但 action 抛异常
    }

    /// <summary>
    /// 主线程调度器：把 UE API 调用（UI/补丁操作必须发生在游戏主线程）从 HTTP 线程投递到主线程执行，
    /// 通过 ManualResetEventSlim 等待结果（超时抛 TimeoutException）。
    /// 自 RimworldMCPDebug/MCPNotifier/MainThreadDispatcher.cs 精简移植。
    /// </summary>
    public class UEMainThreadDispatcher : MonoBehaviour
    {
        static volatile UEMainThreadDispatcher instance;
        static readonly object initLock = new object();
        static readonly Queue<Action> actions = new Queue<Action>();
        static readonly object queueLock = new object();
        static readonly List<Action> actionBuffer = new List<Action>();

        /// <summary>每帧钩子：供长任务分片（如 DebugAction 树后台预热）在每帧 Update 末尾执行。</summary>
        public static volatile Action OnFrame;

        /// <summary>调度器是否已就绪（实例已创建、Update 会消费队列）。</summary>
        public static bool IsReady
        {
            get { return instance != null; }
        }

        public static void EnsureInitialized()
        {
            if (instance != null) return;          // 快路径
            lock (initLock)                        // 双检锁（避免反复 new 无副作用 GameObject）
            {
                if (instance != null) return;
                var go = new GameObject("UELoader_MainThreadDispatcher");
                DontDestroyOnLoad(go);
                instance = go.AddComponent<UEMainThreadDispatcher>();
                Log.Message("[UEHttp] Main thread dispatcher initialized");
            }
        }

        public static void Enqueue(Action action)
        {
            lock (queueLock)
                actions.Enqueue(action);
        }

        /// <summary>在主线程执行并等待完成。超时抛 TimeoutException；action 抛异常时原样抛出。</summary>
        public static void ExecuteOnMainThread(Action action, int timeoutMs = 10000)
        {
            Exception exception = null;
            bool completed = false;

            Enqueue(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    exception = ex;
                }
                completed = true;
            });

            int waited = 0;
            while (!completed && waited < timeoutMs)
            {
                System.Threading.Thread.Sleep(10);
                waited += 10;
            }

            if (!completed)
                throw new TimeoutException("Main thread operation timed out");

            if (exception != null)
                throw exception;
        }

        /// <summary>同 ExecuteOnMainThread，但额外返回精确状态；超时置 TimedOut（不抛异常），Faulted 时仍抛原异常。</summary>
        public static ActionState ExecuteOnMainThreadWithState(Action action, int timeoutMs,
            out ActionState state)
        {
            Exception exception = null;
            int rawState = (int)ActionState.Pending;
            System.Action wrapper = () =>
            {
                Volatile.Write(ref rawState, (int)ActionState.Running);
                try { action(); }
                catch (Exception ex) { exception = ex; }
                Volatile.Write(ref rawState,
                    (int)(exception == null ? ActionState.Completed : ActionState.Faulted));
            };
            Enqueue(wrapper);

            int waited = 0;
            while (Volatile.Read(ref rawState) == (int)ActionState.Pending
                || Volatile.Read(ref rawState) == (int)ActionState.Running)
            {
                if (waited >= timeoutMs) break;
                System.Threading.Thread.Sleep(10);
                waited += 10;
            }

            int final = Volatile.Read(ref rawState);
            if (final == (int)ActionState.Pending || final == (int)ActionState.Running)
            {
                state = ActionState.TimedOut;
                UEHttpLog.Warning($"[UEHttp] 主线程操作超时({timeoutMs}ms)，仍在主线程执行，结果弃置");
                return ActionState.TimedOut;
            }
            if (final == (int)ActionState.Faulted)
            {
                state = ActionState.Faulted;
                throw exception; // 保持现有抛异常语义
            }
            state = ActionState.Completed;
            return ActionState.Completed;
        }

        void Update()
        {
            actionBuffer.Clear();
            lock (queueLock)
            {
                while (actions.Count > 0)
                    actionBuffer.Add(actions.Dequeue());
            }

            foreach (Action action in actionBuffer)
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.Error($"[UEHttp] Error executing main thread action: {ex}");
                }
            }

            if (OnFrame != null)
            {
                try
                {
                    OnFrame();
                }
                catch (Exception ex)
                {
                    Log.Error($"[UEHttp] Error in OnFrame hook: {ex}");
                }
            }
        }

        void OnDestroy()
        {
            instance = null;
        }
    }
}
