using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using UnityEngine;
using static LoadingScreenModTest.Matcher;

namespace LoadingScreenModTest
{
    public sealed class PrefabLoader : DetourUtility<PrefabLoader>
    {
        readonly FieldInfo hasQueuedActionsField = typeof(LoadingManager).GetField("m_hasQueuedActions", BindingFlags.NonPublic | BindingFlags.Instance);
        readonly FieldInfo[] nameField     = new FieldInfo[NUM];
        readonly FieldInfo[] prefabsField  = new FieldInfo[NUM];
        readonly FieldInfo[] replacesField = new FieldInfo[NUM];
        readonly FieldInfo netPrefabsField;
        readonly HashSet<string>[] skippedPrefabs = new HashSet<string>[NUM];
        HashSet<string> simulationPrefabs; // just buildings
        HashSet<string> keptProps = new HashSet<string>(); // just props
        Matcher skipMatcher = Settings.settings.SkipMatcher, exceptMatcher = Settings.settings.ExceptMatcher;
        bool saveDeserialized;
        const string ROUTINE = "<InitializePrefabs>c__Iterator0";
        //StreamWriter[] w = new StreamWriter[NUM];

        private PrefabLoader()
        {
            try
            {
                int i = 0;

                foreach (Type type in new Type[] { typeof(BuildingCollection), typeof(VehicleCollection), typeof(PropCollection) })
                {
                    Type coroutine = type.GetNestedType(ROUTINE, BindingFlags.NonPublic);
                    nameField[i] = coroutine.GetField("name", BindingFlags.NonPublic | BindingFlags.Instance);
                    prefabsField[i] = coroutine.GetField("prefabs", BindingFlags.NonPublic | BindingFlags.Instance);
                    replacesField[i] = coroutine.GetField("replaces", BindingFlags.NonPublic | BindingFlags.Instance);
                    //w[i] = new StreamWriter(Util.GetFileName(type.Name, "txt"));
                    skippedPrefabs[i++] = new HashSet<string>();
                }

                netPrefabsField = typeof(NetCollection).GetNestedType(ROUTINE, BindingFlags.NonPublic).GetField("prefabs", BindingFlags.NonPublic | BindingFlags.Instance);
                init(typeof(LoadingManager), "QueueLoadingAction");
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
        }

        internal HashSet<string> SkippedProps => skippedPrefabs[PROPS];
        internal void SetSkippedPrefabs(HashSet<string>[] prefabs) => prefabs.CopyTo(skippedPrefabs, 0);

        internal override void Dispose()
        {
            base.Dispose();
            skipMatcher = exceptMatcher = null;
            simulationPrefabs?.Clear(); simulationPrefabs = null;
            LevelLoader.instance.SetSkippedPrefabs(skippedPrefabs);
            Array.Clear(skippedPrefabs, 0, skippedPrefabs.Length);

            //foreach (StreamWriter writer in w)
            //    writer.Dispose();

            //Array.Clear(w, 0, w.Length);
        }

        public static void QueueLoadingAction(LoadingManager lm, IEnumerator action)
        {
            Type type = action.GetType().DeclaringType;
            int index = -1;

            if (type == typeof(BuildingCollection))
                index = BUILDINGS;
            else if (type == typeof(VehicleCollection))
                index = VEHICLES;
            else if (type == typeof(PropCollection))
                index = PROPS;

            // This race condition with the simulation thread must be watched. It never occurs in my game, though.
            if (index >= 0 && !instance.saveDeserialized)
            {
                while (!LevelLoader.instance.IsSaveDeserialized())
                    Thread.Sleep(60);

                instance.saveDeserialized = true;
            }

            while (!Monitor.TryEnter(LevelLoader.instance.loadingLock, SimulationManager.SYNCHRONIZE_TIMEOUT))
                ;

            try
            {
                //if (index >= 0)
                //    instance.Desc(action, index);

                switch (index)
                {
                    case BUILDINGS:
                        instance.Skip<BuildingInfo>(action, UpdateBuildingPrefabs, UpdateBuildingCollection, index);
                        break;
                    case VEHICLES:
                        instance.Skip<VehicleInfo>(action, UpdateVehiclePrefabs, UpdateVehicleCollection, index);
                        break;
                    case PROPS:
                        instance.Skip<PropInfo>(action, UpdatePropPrefabs, UpdatePropCollection, index);
                        break;
                    default:
                        if (instance.skipMatcher.Has[PROPS] && type == typeof(NetCollection))
                            instance.RemoveSkippedFromNets(action);
                        break;
                }

                LevelLoader.instance.mainThreadQueue.Enqueue(action);

                if (LevelLoader.instance.mainThreadQueue.Count < 2)
                    instance.hasQueuedActionsField.SetValue(lm, true);
            }
            finally
            {
                Monitor.Exit(LevelLoader.instance.loadingLock);
            }
        }

        delegate void UpdatePrefabs(Array prefabs);
        delegate void UpdateCollection(string name, Array keptPrefabs, string[] replacedNames);

        void Skip<P>(IEnumerator action, UpdatePrefabs UpdateAll, UpdateCollection UpdateKept, int index) where P : PrefabInfo
        {
            try
            {
                P[] prefabs = prefabsField[index].GetValue(action) as P[];

                if (prefabs == null)
                {
                    prefabsField[index].SetValue(action, new P[0]);
                    return;
                }

                UpdateAll(prefabs);

                if (!skipMatcher.Has[index])
                    return;

                if (index == BUILDINGS)
                    LookupSimulationPrefabs();

                if (!(replacesField[index].GetValue(action) is string[] replaces))
                    replaces = new string[0];

                List<P> keptPrefabs = null; List<string> keptReplaces = null;

                for (int i = 0; i < prefabs.Length; i++)
                {
                    P info = prefabs[i];
                    string replace = i < replaces.Length ? replaces[i]?.Trim() : string.Empty;

                    if (Skip(info, replace, index))
                    {
                        AddToSkipped(info, replace, index);
                        LevelLoader.instance.skipCounts[index]++;

                        if (keptPrefabs == null)
                        {
                            keptPrefabs = prefabs.ToList(i);

                            if (i < replaces.Length)
                                keptReplaces = replaces.ToList(i);
                        }
                    }
                    else if (keptPrefabs != null)
                    {
                        keptPrefabs.Add(info);

                        if (keptReplaces != null)
                            keptReplaces.Add(replace);
                    }
                }

                if (keptPrefabs != null)
                {
                    P[] p = keptPrefabs.ToArray();
                    string[] r = null;
                    prefabsField[index].SetValue(action, p);

                    if (keptReplaces != null)
                    {
                        r = keptReplaces.ToArray();
                        replacesField[index].SetValue(action, r);
                    }

                    UpdateKept(nameField[index].GetValue(action) as string, p, r);
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
        }

        void AddToSkipped(PrefabInfo info, string replace, int index)
        {
            HashSet<string> skips = skippedPrefabs[index];
            skips.Add(info.name);

            if (!string.IsNullOrEmpty(replace))
                if (replace.IndexOf(',') != -1)
                {
                    string[] array = replace.Split(',');

                    for (int i = 0; i < array.Length; i++)
                        skips.Add(array[i].Trim());
                }
                else
                    skips.Add(replace);
        }

        static void UpdateBuildingPrefabs(Array prefabs)
        {
            if (instance.skipMatcher.Has[PROPS] && prefabs is BuildingInfo[] infos)
                for (int i = 0; i < infos.Length; i++)
                    instance.RemoveSkippedFromBuilding(infos[i]);
        }

        static void UpdateVehiclePrefabs(Array prefabs)
        {
            if (instance.skipMatcher.Has[VEHICLES] && prefabs is VehicleInfo[] infos)
                for (int i = 0; i < infos.Length; i++)
                    instance.RemoveSkippedFromVehicle(infos[i]);
        }

        static void UpdatePropPrefabs(Array prefabs)
        {
            if (instance.skipMatcher.Has[PROPS] && prefabs is PropInfo[] infos)
                for (int i = 0; i < infos.Length; i++)
                    instance.RemoveSkippedFromProp(infos[i]);
        }

        static void UpdateBuildingCollection(string name, Array keptPrefabs, string[] replacedNames)
        {
            BuildingCollection c = GameObject.Find(name)?.GetComponent<BuildingCollection>();

            if (c != null)
            {
                c.m_prefabs = keptPrefabs as BuildingInfo[];

                if (replacedNames != null)
                    c.m_replacedNames = replacedNames;
            }
        }

        static void UpdateVehicleCollection(string name, Array keptPrefabs, string[] replacedNames)
        {
            VehicleCollection c = GameObject.Find(name)?.GetComponent<VehicleCollection>();

            if (c != null)
            {
                c.m_prefabs = keptPrefabs as VehicleInfo[];

                if (replacedNames != null)
                    c.m_replacedNames = replacedNames;
            }
        }

        static void UpdatePropCollection(string name, Array keptPrefabs, string[] replacedNames)
        {
            PropCollection c = GameObject.Find(name)?.GetComponent<PropCollection>();

            if (c != null)
            {
                c.m_prefabs = keptPrefabs as PropInfo[];

                if (replacedNames != null)
                    c.m_replacedNames = replacedNames;
            }
        }

        bool Skip(PrefabInfo info, string replace, int index)
        {
            if (skipMatcher.Matches(info, index))
            {
                string name = info.name;

                if (index == BUILDINGS && IsSimulationPrefab(name, replace))
                {
                    Util.DebugPrint(name + " -> not skipped because used in city");
                    return false;
                }

                if (exceptMatcher.Matches(info, index))
                {
                    Util.DebugPrint(name + " -> not skipped because excepted");
                    return false;
                }

                Util.DebugPrint(name + " -> skipped");
                return true;
            }

            return false;
        }

        bool Skip(PrefabInfo info, int index) => skipMatcher.Matches(info, index) && !exceptMatcher.Matches(info, index);

        bool Skip(PropInfo info)
        {
            string name = info.name;

            if (keptProps.Contains(name))
                return false;

            if (skippedPrefabs[PROPS].Contains(name))
                return true;

            bool skip = Skip(info, PROPS);
            (skip ? skippedPrefabs[PROPS] : keptProps).Add(name);
            return skip;
        }

        /// <summary>
        /// Looks up the building prefabs used in the simulation.
        /// </summary>
        internal void LookupSimulationPrefabs()
        {
            if (simulationPrefabs == null)
            {
                simulationPrefabs = new HashSet<string>();

                try
                {
                    Building[] buffer = BuildingManager.instance.m_buildings.m_buffer;
                    int n = buffer.Length;

                    for (int i = 1; i < n; i++)
                        if (buffer[i].m_flags != Building.Flags.None)
                        {
                            string fullName = PrefabCollection<BuildingInfo>.PrefabName(buffer[i].m_infoIndex);

                            // Recognize prefabs.
                            if (!string.IsNullOrEmpty(fullName) && fullName.IndexOf('.') < 0)
                                simulationPrefabs.Add(fullName);
                        }
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogException(e);
                }
            }
        }

        internal bool AllPrefabsAvailable() => CustomDeserializer.AllAvailable<BuildingInfo>(simulationPrefabs, new HashSet<string>());

        bool IsSimulationPrefab(string name, string replace)
        {
            if (simulationPrefabs.Contains(name))
                return true;

            if (string.IsNullOrEmpty(replace))
                return false;

            if (replace.IndexOf(',') != -1)
            {
                string[] array = replace.Split(',');

                for (int i = 0; i < array.Length; i++)
                    if (simulationPrefabs.Contains(array[i].Trim()))
                        return true;

                return false;
            }
            else
                return simulationPrefabs.Contains(replace);
        }

        void RemoveSkippedFromBuilding(BuildingInfo info)
        {
            BuildingInfo.Prop[] props = info.m_props;

            if (props == null || props.Length == 0)
                return;

            try
            {
                //StreamWriter wp = new StreamWriter(Path.Combine(Util.GetSavePath(), "Building-props.txt"), true);
                //wp.WriteLine(info.name);
                List<BuildingInfo.Prop> keepThese = new List<BuildingInfo.Prop>(props.Length);
                bool skippedSome = false;

                for(int i = 0; i < props.Length; i++)
                {
                    BuildingInfo.Prop prop = props[i];

                    if (prop == null)
                        continue;
                    //if (prop.m_prop != null)
                    //    wp.WriteLine("  " + prop.m_prop.name);
                    if (prop.m_prop == null)
                        keepThese.Add(prop);
                    else if (Skip(prop.m_prop))
                    {
                        //Util.DebugPrint(prop.m_prop.name, "-> RemoveSkippedFromBuilding at", Profiling.Millis, "/", info.name);
                        prop.m_prop = prop.m_finalProp = null;
                        skippedSome = true;
                    }
                    else
                        keepThese.Add(prop);
                }

                if (skippedSome)
                {
                    info.m_props = keepThese.ToArray();

                    if (info.m_props.Length == 0)
                    {
                        // Less log clutter.
                        if (info.m_buildingAI is CommonBuildingAI cbai)
                            cbai.m_ignoreNoPropsWarning = true;
                        else if (info.GetComponent<BuildingAI>() is CommonBuildingAI cbai2)
                            cbai2.m_ignoreNoPropsWarning = true;
                    }
                }

                keepThese.Clear();
                //wp.Close();
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
        }

        void RemoveSkippedFromVehicle(VehicleInfo info)
        {
            VehicleInfo.VehicleTrailer[] trailers = info.m_trailers;

            if (trailers == null || trailers.Length == 0)
                return;

            try
            {
                //StreamWriter wp = new StreamWriter(Path.Combine(Util.GetSavePath(), "Vehicle-trailers.txt"), true);
                //wp.WriteLine(info.name);
                List<VehicleInfo.VehicleTrailer> keepThese = new List<VehicleInfo.VehicleTrailer>(trailers.Length);
                string prev = string.Empty;
                bool skippedSome = false, skip = false;

                for (int i = 0; i < trailers.Length; i++)
                {
                    VehicleInfo trailer = trailers[i].m_info;

                    if (trailer == null)
                        continue;

                    string name = trailer.name;
                    //wp.WriteLine("  " + name + "  " + trailers[i].m_probability.ToString().PadLeft(2));

                    if (prev != name)
                    {
                        skip = Skip(trailer, VEHICLES);
                        prev = name;
                    }

                    if (skip)
                    {
                        //Util.DebugPrint(name, "-> RemoveSkippedFromVehicle at", Profiling.Millis, "/", info.name);
                        trailers[i].m_info = null;
                        skippedSome = true;
                    }
                    else
                        keepThese.Add(trailers[i]);
                }

                if (skippedSome)
                    info.m_trailers = keepThese.Count > 0 ? keepThese.ToArray() : null;

                keepThese.Clear();
                //wp.Close();
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
        }

        void RemoveSkippedFromProp(PropInfo info)
        {
            PropInfo.Variation[] variations = info.m_variations;

            if (variations == null || variations.Length == 0)
                return;

            try
            {
                //StreamWriter wp = new StreamWriter(Path.Combine(Util.GetSavePath(), "Prop-variations.txt"), true);
                //wp.WriteLine(info.name);
                List<PropInfo.Variation> keepThese = new List<PropInfo.Variation>(variations.Length);
                bool skippedSome = false;

                for (int i = 0; i < variations.Length; i++)
                {
                    PropInfo prop = variations[i].m_prop;

                    if (prop == null)
                        continue;

                    //wp.WriteLine("  " + prop.name + "  " + variations[i].m_probability.ToString().PadLeft(2));

                    if (Skip(prop))
                    {
                        //Util.DebugPrint(prop.name, "-> RemoveSkippedFromProp at", Profiling.Millis, "/", info.name);
                        variations[i].m_prop = variations[i].m_finalProp = null;
                        skippedSome = true;
                    }
                    else
                        keepThese.Add(variations[i]);
                }

                if (skippedSome)
                    info.m_variations = keepThese.Count > 0 ? keepThese.ToArray() : null;

                keepThese.Clear();
                //wp.Close();
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
        }

        void RemoveSkippedFromNets(IEnumerator action)
        {
            try
            {
                NetInfo[] prefabs = netPrefabsField.GetValue(action) as NetInfo[];

                if (prefabs == null)
                {
                    netPrefabsField.SetValue(action, new NetInfo[0]);
                    return;
                }

                List<NetLaneProps.Prop> keepThese = new List<NetLaneProps.Prop>(16);
                //StreamWriter wp = new StreamWriter(Path.Combine(Util.GetSavePath(), "Net-props.txt"), true);

                foreach (NetInfo info in prefabs)
                {
                    //wp.WriteLine(info.name);

                    if (info.m_lanes == null)
                        continue;

                    for (int i = 0; i < info.m_lanes.Length; i++)
                    {
                        NetLaneProps laneProps = info.m_lanes[i].m_laneProps;

                        if (laneProps == null || laneProps.m_props == null)
                            continue;

                        //wp.WriteLine("  --");
                        bool skippedSome = false;

                        for (int j = 0; j < laneProps.m_props.Length; j++)
                        {
                            NetLaneProps.Prop prop = laneProps.m_props[j];

                            if (prop == null)
                                continue;
                            //if (prop.m_prop != null)
                            //    wp.WriteLine("  " + prop.m_prop.name);
                            if (prop.m_prop == null)
                                keepThese.Add(prop);
                            else if (Skip(prop.m_prop))
                            {
                                //Util.DebugPrint(prop.m_prop.name, "-> RemoveSkippedFromNets at", Profiling.Millis, "/", info.name);
                                prop.m_prop = prop.m_finalProp = null;
                                skippedSome = true;
                            }
                            else
                                keepThese.Add(prop);
                        }

                        if (skippedSome)
                            laneProps.m_props = keepThese.ToArray();

                        keepThese.Clear();
                    }
                }

                //wp.Close();
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
        }

        internal static IEnumerator RemoveSkippedFromSimulation()
        {
            if (instance == null)
                yield break;

            instance.RemoveSkippedFromSimulation<BuildingInfo>(BUILDINGS);
            yield return null;
            instance.RemoveSkippedFromSimulation<VehicleInfo>(VEHICLES);
            yield return null;
            instance.RemoveSkippedFromSimulation<PropInfo>(PROPS);
            yield return null;
        }

        void RemoveSkippedFromSimulation<P>(int index) where P : PrefabInfo
        {
            HashSet<string> skips = skippedPrefabs[index];

            if (skips == null || skips.Count == 0)
                return;

            object prefabLock = Util.GetStatic(typeof(PrefabCollection<P>), "m_prefabLock");

            while (!Monitor.TryEnter(prefabLock, SimulationManager.SYNCHRONIZE_TIMEOUT))
                ;

            try
            {
                FastList<PrefabCollection<P>.PrefabData> prefabs = (FastList<PrefabCollection<P>.PrefabData>)
                    Util.GetStatic(typeof(PrefabCollection<P>), "m_simulationPrefabs");

                int size = prefabs.m_size;
                var buffer = prefabs.m_buffer;

                for (int i = 0; i < size; i++)
                    if (buffer[i].m_name != null && skips.Contains(buffer[i].m_name))
                    {
                        buffer[i].m_name = CustomDeserializer.SKIP_PREFIX + (i + (index<<12));
                        buffer[i].m_refcount = 0;
                    }
            }
            finally
            {
                Monitor.Exit(prefabLock);
            }
        }

        internal static void RemoveSkippedFromStyle(DistrictStyle style)
        {
            HashSet<string> skips = instance?.skippedPrefabs[BUILDINGS];

            if (skips == null || skips.Count == 0)
                return;

            try
            {
                BuildingInfo[] inStyle = style.GetBuildingInfos();
                ((HashSet<BuildingInfo>) Util.Get(style, "m_Infos")).Clear();
                ((HashSet<int>) Util.Get(style, "m_AffectedServices")).Clear();

                foreach (BuildingInfo info in inStyle)
                    if (info != null && !skips.Contains(info.name))
                            style.Add(info);

                Array.Clear(inStyle, 0, inStyle.Length);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
        }

        internal static void UnloadSkipped()
        {
            if (instance == null)
                return;

            instance.keptProps.Clear(); instance.keptProps = null;
            instance.simulationPrefabs?.Clear(); instance.simulationPrefabs = null;
            int[] counts = LevelLoader.instance.skipCounts;

            if (counts[BUILDINGS] > 0)
                Util.DebugPrint("Skipped", counts[BUILDINGS], "building prefabs");
            if (counts[VEHICLES] > 0)
                Util.DebugPrint("Skipped", counts[VEHICLES], "vehicle prefabs");
            if (counts[PROPS] > 0)
                Util.DebugPrint("Skipped", counts[PROPS], "prop prefabs");

            try
            {
                Resources.UnloadUnusedAssets();
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
        }
/*
        void Desc(IEnumerator action, int index)
        {
            string s = "\n";
            string name = Util.Get(action, "name") as string;

            if (!string.IsNullOrEmpty(name))
                s = string.Concat(s, name);

            //var method = new StackFrame(2).GetMethod();
            //s = string.Concat(s, " - ", method.DeclaringType.FullName, " - ", Profiling.Millis, " - ", LevelLoader.GetSimProgress(), " - ",
            //    LevelLoader.instance.mainThreadQueue.Count);

            w[index].WriteLine(s);

            if (Util.Get(action, "prefabs") is Array prefabs && prefabs.Rank == 1)
                foreach (object o in prefabs)
                    if (o is PrefabInfo info)
                        Desc(info, index);
        }

        void Desc(PrefabInfo info, int index)
        {
            string s = ("  " + info.name).PadRight(65);
            int level = (int) info.GetClassLevel() + 1;
            string sub = info.GetSubService() == ItemClass.SubService.None ? "" : info.GetSubService() + " ";
            s = string.Concat(s, info.GetService() + " " + sub + "L" + level);

            if (info.GetWidth() > 0 || info.GetLength() > 0)
                s = string.Concat(s, " " + info.GetWidth() + "x" + info.GetLength());

            if (info is BuildingInfo bi)
                s = string.Concat(s, " " + bi.m_zoningMode);
            else if (info is PropInfo pi)
            {
                PropInfo.Variation[] variations = pi.m_variations;

                if (variations != null && variations.Length > 0)
                    s = string.Concat(s, " variations");
            }

            w[index].WriteLine(s);
        } */
    }
}
