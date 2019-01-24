using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Diagnostics;
using UnityEngine;

namespace LoadingScreenModTest
{
    public static class Profiling
    {
        static readonly Stopwatch stopWatch = new Stopwatch();
        internal const string FAILED = " (failed)", DUPLICATE = " (duplicate)", NOT_FOUND = " (missing)";

        internal static void Init()
        {
            Sink.builder.Length = 0;

            if (Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor)
                MemoryAPI.pfMax = MemoryAPI.wsMax = 0;
        }

        internal static void Start()
        {
            stopWatch.Reset();
            stopWatch.Start();
        }

        internal static void Stop()
        {
            Sink.builder.Length = 0; Sink.builder.Capacity = 0;
            stopWatch.Reset();
        }

        internal static int Millis => (int) stopWatch.ElapsedMilliseconds;

        internal static string TimeString(int millis)
        {
            int seconds = millis / 1000;
            return string.Concat((seconds / 60).ToString(), ":", (seconds % 60).ToString("00"));
        }
    }

    sealed class Sink
    {
        string name, last;
        readonly Queue<string> queue = new Queue<string>();
        readonly int len;
        internal string Name { set { name = value; } }
        internal string Last => last;
        string NameLoading => string.Concat(YELLOW, name, OFF);
        string NameIdle => string.Concat(GRAY, name, OFF);
        string NameFailed => string.Concat(RED, name, Profiling.FAILED, OFF);

        internal const string YELLOW = "<color #f0f000>", RED = "<color #f04040>", GRAY = "<color #c0c0c0>", ORANGE = "<color #f0a840>", CYAN = "<color #80e0f0>", OFF = "</color>";
        internal static readonly StringBuilder builder = new StringBuilder();

        internal Sink(string name, int len)
        {
            this.name = name;
            this.len = len;
        }

        internal void Clear()
        {
            queue.Clear();
            last = null;
        }

        internal void Add(string s)
        {
            if (s != last)
            {
                if (last != null && len > 1)
                {
                    if (queue.Count >= len - 1)
                        queue.Dequeue();

                    queue.Enqueue(last);
                }

                if (s[s.Length - 1] == ')')
                {
                    if (s.EndsWith(Profiling.NOT_FOUND))
                        s = string.Concat(ORANGE, s, OFF);
                    else if (s.EndsWith(Profiling.FAILED))
                        s = string.Concat(RED, s, OFF);
                    else if (s.EndsWith(Profiling.DUPLICATE))
                        s = string.Concat(CYAN, s, OFF);
                }

                last = s;
            }
        }

        internal string CreateText(bool isLoading, bool failed = false)
        {
            builder.AppendLine(isLoading ? NameLoading : failed ? NameFailed : NameIdle);

            foreach (string s in queue)
                builder.AppendLine(s);

            if (last != null)
                builder.Append(last);

            string ret = builder.ToString();
            builder.Length = 0;
            return ret;
        }
    }

    abstract class Source
    {
        protected internal abstract string CreateText();
    }

    class ProfilerSource : Source
    {
        protected readonly Sink sink;
        protected readonly FastList<LoadingProfiler.Event> events;
        protected int index;

        static FieldInfo EventsField => typeof(LoadingProfiler).GetField("m_events", BindingFlags.NonPublic | BindingFlags.Instance);
        internal static FastList<LoadingProfiler.Event> GetEvents(LoadingProfiler profiler) => (FastList<LoadingProfiler.Event>) EventsField.GetValue(profiler);

        protected bool IsLoading
        {
            get
            {
                int lst = events.m_size - 1;
                LoadingProfiler.Event[] buffer = events.m_buffer;
                return lst >= 0 && ((int) buffer[lst].m_type & 1) == 0; // true if the last one is begin or continue
            }
        }

        internal ProfilerSource(string name, int len, LoadingProfiler profiler) : this(profiler, new Sink(name, len)) { }

        internal ProfilerSource(LoadingProfiler profiler, Sink sink) : base()
        {
            this.sink = sink;
            this.events = GetEvents(profiler);
        }

        protected internal override string CreateText()
        {
            try
            {
                int i = index, len = events.m_size;

                if (i >= len)
                    return null;

                index = len;
                LoadingProfiler.Event[] buffer = events.m_buffer;

                for (; i < len; i++)
                    switch (buffer[i].m_type)
                    {
                        case LoadingProfiler.Type.BeginLoading:
                        case LoadingProfiler.Type.BeginSerialize:
                        case LoadingProfiler.Type.BeginDeserialize:
                        case LoadingProfiler.Type.BeginAfterDeserialize:
                            sink.Add(buffer[i].m_name);
                            break;
                    }

                return sink.CreateText(IsLoading);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
                return null;
            }
        }
    }

    internal sealed class SimpleProfilerSource : ProfilerSource
    {
        bool failed;

        internal SimpleProfilerSource(string name, LoadingProfiler profiler) : base(name, 1, profiler) { }

        protected internal override string CreateText()
        {
            try
            {
                if (failed)
                    return sink.CreateText(false, true);

                LoadingProfiler.Event[] buffer = events.m_buffer;

                for (int i = events.m_size - 1; i >= 0; i--)
                    switch (buffer[i].m_type)
                    {
                        case LoadingProfiler.Type.BeginLoading:
                        case LoadingProfiler.Type.BeginSerialize:
                        case LoadingProfiler.Type.BeginDeserialize:
                        case LoadingProfiler.Type.BeginAfterDeserialize:
                            if (i != index || IsLoading)
                            {
                                index = i;
                                sink.Add(buffer[i].m_name);
                                return sink.CreateText(true);
                            }
                            else
                            {
                                sink.Clear();
                                return sink.CreateText(false);
                            }
                    }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }

            return null;
        }

        internal void Failed(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                message = sink.Last;

                if (string.IsNullOrEmpty(message))
                    message = "Deserialize";
            }
            else if (message.Length > 80)
                message = message.Substring(0, 80);

            if (!message.StartsWith(Sink.RED))
                message = string.Concat(Sink.RED, message, Sink.OFF);

            sink.Clear();
            sink.Add(message);
            failed = true;
        }
    }

    internal sealed class LineSource : Source
    {
        readonly Sink sink;
        readonly Func<bool> IsLoading;

        internal LineSource(string name, int len, Func<bool> IsLoading) : this(new Sink(name, len), IsLoading) { }

        internal LineSource(Sink sink, Func<bool> IsLoading)
        {
            this.sink = sink;
            this.IsLoading = IsLoading;
        }

        protected internal override string CreateText() => sink.CreateText(IsLoading());
        internal void Add(string s) => sink.Add(s);
        internal void AddNotFound(string s) => Add(string.Concat(s, Profiling.NOT_FOUND));
        internal void AddFailed(string s) => Add(string.Concat(s, Profiling.FAILED));
        internal void AddDuplicate(string s) => Add(string.Concat(s, Profiling.DUPLICATE));
    }

    internal sealed class DualProfilerSource : Source
    {
        readonly Source scenes;
        readonly LineSource assets;
        readonly Sink sink;
        readonly string name;
        int state = 0;
        int failed, duplicate, notFound;

        internal DualProfilerSource(string name, int len) : base()
        {
            this.sink = new Sink(name, len);
            this.name = name;
            this.scenes = new ProfilerSource(LoadingManager.instance.m_loadingProfilerScenes, sink);
            this.assets = new LineSource(sink, () => true);
        }

        protected internal override string CreateText()
        {
            string ret = state == 1 ? assets.CreateText() : scenes.CreateText();

            if (state == 0 && LevelLoader.instance.assetsStarted)
                state = 1;
            else if (state == 1 && LevelLoader.instance.assetsFinished)
                state = 2;

            return ret;
        }

        internal void Add(string s) => assets.Add(s);
        internal void CustomAssetNotFound(string n) { notFound++; AdjustName(); assets.AddNotFound(n); }
        internal void CustomAssetFailed(string n) { failed++; AdjustName(); assets.AddFailed(n); }
        internal void CustomAssetDuplicate(string n) { duplicate++; AdjustName(); assets.AddDuplicate(n); }

        void AdjustName()
        {
            string s1 = failed == 0 ? String.Empty : string.Concat(failed.ToString(), " failed ");
            string s2 = notFound == 0 ? String.Empty : string.Concat(notFound.ToString(), " missing ");
            string s3 = duplicate == 0 ? String.Empty : string.Concat(duplicate.ToString(), " duplicates");
            sink.Name = name + " (" + s1 + s2 + s3 + ")";
        }
    }

    internal sealed class TimeSource : Source
    {
        protected internal override string CreateText() => Profiling.TimeString(Profiling.Millis);
    }

    internal sealed class MemorySource : Source
    {
        int systemMegas = SystemInfo.systemMemorySize, wsOrange, wsRed, pfOrange, pfRed;
        bool orange, red;

        internal MemorySource()
        {
            wsOrange =  92 * systemMegas >> 7;
            wsRed    = 106 * systemMegas >> 7;
            pfOrange = 107 * systemMegas >> 7;
            pfRed    = 124 * systemMegas >> 7;
        }

        protected internal override string CreateText()
        {
            try
            {
                MemoryAPI.GetUsage(out int pfMegas, out int wsMegas);
                string s = string.Concat((wsMegas / 1024f).ToString("F1"), " GB\n", (pfMegas / 1024f).ToString("F1"), " GB");
                orange |= wsMegas > wsOrange | pfMegas > pfOrange;
                red |= wsMegas > wsRed | pfMegas > pfRed;

                if (red)
                    return string.Concat(Sink.RED, s, Sink.OFF);
                else if (orange)
                    return string.Concat(Sink.ORANGE, s, Sink.OFF);
                else
                    return s;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}
