using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace UELoader
{
    /// <summary>
    /// 主线程调度器：把 UE API 调用（UI/补丁操作必须发生在游戏主线程）从 HTTP 线程投递到主线程执行，
    /// 通过 ManualResetEventSlim 等待结果（超时抛 TimeoutException）。
    /// 自 RimworldMCPDebug/MCPNotifier/MainThreadDispatcher.cs 精简移植。
    /// </summary>
    public class UEMainThreadDispatcher : MonoBehaviour
    {
        static UEMainThreadDispatcher instance;
        static readonly Queue<Action> actions = new Queue<Action>();
        static readonly object queueLock = new object();
        static readonly List<Action> actionBuffer = new List<Action>();

        /// <summary>每帧钩子：供长任务分片（如 DebugAction 树后台预热）在每帧 Update 末尾执行。</summary>
        public static Action OnFrame;

        /// <summary>调度器是否已就绪（实例已创建、Update 会消费队列）。</summary>
        public static bool IsReady
        {
            get { return instance != null; }
        }

        public static void EnsureInitialized()
        {
            if (instance != null)
                return;

            var go = new GameObject("UELoader_MainThreadDispatcher");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<UEMainThreadDispatcher>();
            Log.Message("[UEHttp] Main thread dispatcher initialized");
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
