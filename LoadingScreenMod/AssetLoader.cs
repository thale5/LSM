using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ColossalFramework;
using ColossalFramework.Packaging;
using ColossalFramework.PlatformServices;
using ColossalFramework.Plugins;
using ColossalFramework.UI;
using ICities;
using UnityEngine;
using static AssetDataWrapper;
using static ColossalFramework.Plugins.PluginManager;

namespace LoadingScreenModTest
{
    /// <summary>
    /// LoadCustomContent coroutine from LoadingManager.
    /// </summary>
    public sealed class AssetLoader : Instance<AssetLoader>
    {
        HashSet<string> loadedIntersections = new HashSet<string>(), dontSpawnNormally = new HashSet<string>();
        HashSet<string>[] queuedLoads;
        int[] loadQueueIndex = { 5, 1, 0, 7, 7, 5, 5, 1, 0, 3, 3, 3 };
        Dictionary<string, SomeMetaData> metaDatas = new Dictionary<string, SomeMetaData>(128);
        Dictionary<string, CustomAssetMetaData> citizenMetaDatas = new Dictionary<string, CustomAssetMetaData>();
        Dictionary<string, List<Package.Asset>> suspects = new Dictionary<string, List<Package.Asset>>(4);
        Dictionary<string, PluginInfo> plugins;

        internal readonly Stack<Package.Asset> stack = new Stack<Package.Asset>(4); // the asset loading stack
        internal int beginMillis, lastMillis, assetCount;
        readonly bool reportAssets = Settings.settings.reportAssets;

        internal const int yieldInterval = 350;
        float progress;
        internal bool IsIntersection(string fullName) => loadedIntersections.Contains(fullName);
        internal Package.Asset Current => stack.Count > 0 ? stack.Peek() : null;

        private AssetLoader()
        {
            HashSet<string> queuedBuildings = new HashSet<string>(), queuedProps = new HashSet<string>(), queuedTrees = new HashSet<string>(),
                queuedVehicles = new HashSet<string>(), queuedCitizens = new HashSet<string>(), queuedNets = new HashSet<string>();

            queuedLoads = new HashSet<string>[] { queuedBuildings, queuedProps, queuedTrees, queuedVehicles, queuedVehicles, queuedBuildings, queuedBuildings,
                queuedProps, queuedCitizens, queuedNets, queuedNets, queuedBuildings };
        }

        public void Setup()
        {
            Sharing.Create();

            if (reportAssets)
                AssetReport.Create();
        }

        public void Dispose()
        {
            if (reportAssets)
            {
                AssetReport.instance.SaveStats();
                AssetReport.instance.Dispose();
            }

            UsedAssets.instance?.Dispose();
            Sharing.instance?.Dispose();
            loadedIntersections.Clear(); dontSpawnNormally.Clear(); metaDatas.Clear(); citizenMetaDatas.Clear();
            loadedIntersections = null; dontSpawnNormally = null; metaDatas = null; citizenMetaDatas = null;
            plugins = null; loadQueueIndex = null; instance = null;
        }

        void Report()
        {
            if (Settings.settings.loadUsed)
                UsedAssets.instance.ReportMissingAssets();

            if (reportAssets)
                AssetReport.instance.Save(Sharing.instance.texhit, Sharing.instance.mathit, Sharing.instance.meshit);

            Sharing.instance.Dispose();
        }

        public IEnumerator LoadCustomContent()
        {
            LoadingManager.instance.m_loadingProfilerMain.BeginLoading("LoadCustomContent");
            LoadingManager.instance.m_loadingProfilerCustomContent.Reset();
            LoadingManager.instance.m_loadingProfilerCustomAsset.Reset();
            LoadingManager.instance.m_loadingProfilerCustomContent.BeginLoading("District Styles");
            LoadingManager.instance.m_loadingProfilerCustomAsset.PauseLoading();
            LevelLoader.instance.assetsStarted = true;

            int i, j;
            DistrictStyle districtStyle;
            DistrictStyleMetaData districtStyleMetaData;
            List<DistrictStyle> districtStyles = new List<DistrictStyle>();
            HashSet<string> styleBuildings = new HashSet<string>();
            FastList<DistrictStyleMetaData> districtStyleMetaDatas = new FastList<DistrictStyleMetaData>();
            FastList<Package> districtStylePackages = new FastList<Package>();
            Package.Asset europeanStyles = PackageManager.FindAssetByName("System." + DistrictStyle.kEuropeanStyleName);

            if (europeanStyles != null && europeanStyles.isEnabled)
            {
                districtStyle = new DistrictStyle(DistrictStyle.kEuropeanStyleName, true);
                Util.InvokeVoid(LoadingManager.instance, "AddChildrenToBuiltinStyle", GameObject.Find("European Style new"), districtStyle, false);
                Util.InvokeVoid(LoadingManager.instance, "AddChildrenToBuiltinStyle", GameObject.Find("European Style others"), districtStyle, true);

                if (Settings.settings.SkipPrefabs)
                    PrefabLoader.RemoveSkippedFromStyle(districtStyle);

                districtStyles.Add(districtStyle);
            }

            if ((bool) typeof(LoadingManager).GetMethod("DLC", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(LoadingManager.instance, new object[] { 715190u }))
            {
                Package.Asset asset = PackageManager.FindAssetByName("System." + DistrictStyle.kEuropeanSuburbiaStyleName);

                if (asset != null && asset.isEnabled)
                {
                    districtStyle = new DistrictStyle(DistrictStyle.kEuropeanSuburbiaStyleName, true);
                    Util.InvokeVoid(LoadingManager.instance, "AddChildrenToBuiltinStyle", GameObject.Find("Modder Pack 3"), districtStyle, false);

                    if (Settings.settings.SkipPrefabs)
                        PrefabLoader.RemoveSkippedFromStyle(districtStyle);

                    districtStyles.Add(districtStyle);
                }
            }

            if (Settings.settings.SkipPrefabs)
                PrefabLoader.UnloadSkipped();

            foreach (Package.Asset asset in PackageManager.FilterAssets(UserAssetType.DistrictStyleMetaData))
            {
                try
                {
                    if (asset != null && asset.isEnabled)
                    {
                        districtStyleMetaData = asset.Instantiate<DistrictStyleMetaData>();

                        if (districtStyleMetaData != null && !districtStyleMetaData.builtin)
                        {
                            districtStyleMetaDatas.Add(districtStyleMetaData);
                            districtStylePackages.Add(asset.package);

                            if (districtStyleMetaData.assets != null)
                                for (i = 0; i < districtStyleMetaData.assets.Length; i++)
                                    styleBuildings.Add(districtStyleMetaData.assets[i]);
                        }
                    }
                }
                catch (Exception ex)
                {
                    CODebugBase<LogChannel>.Warn(LogChannel.Modding, string.Concat(new object[] {ex.GetType(), ": Loading custom district style failed[", asset, "]\n", ex.Message}));
                }
            }

            LoadingManager.instance.m_loadingProfilerCustomAsset.ContinueLoading();
            LoadingManager.instance.m_loadingProfilerCustomContent.EndLoading();

            if (Settings.settings.loadUsed)
                UsedAssets.Create();

            lastMillis = Profiling.Millis;
            LoadingScreen.instance.DualSource.Add("Custom Assets");
            LoadingManager.instance.m_loadingProfilerCustomContent.BeginLoading("Calculating asset load order");
            Util.DebugPrint("GetLoadQueue", Profiling.Millis);
            Package.Asset[] queue = GetLoadQueue(styleBuildings);
            Util.DebugPrint("LoadQueue", queue.Length, Profiling.Millis);
            LoadingManager.instance.m_loadingProfilerCustomContent.EndLoading();
            plugins = (Dictionary<string, PluginInfo>) Util.Get(Singleton<PluginManager>.instance, "m_Plugins");
            //PrintPlugins();

            LoadingManager.instance.m_loadingProfilerCustomContent.BeginLoading("Loading Custom Assets");
            Sharing.instance.Start(queue);
            beginMillis = Profiling.Millis;

            for (i = 0; i < queue.Length; i++)
            {
                Package.Asset assetRef = queue[i];
                Console.WriteLine(string.Concat("[LSMT] ", i, ": ", Profiling.Millis, " ", assetCount, " ", Sharing.instance.currentCount, " ",
                    assetRef.fullName, Sharing.instance.ThreadStatus));

                if ((i & 63) == 0)
                    PrintMem();

                Sharing.instance.WaitForWorkers();

                try
                {
                    stack.Clear();
                    LoadImpl(assetRef);
                }
                catch (Exception e)
                {
                    AssetFailed(assetRef, assetRef.package, e);
                }

                Sharing.instance.ManageLoadQueue(i);

                if (Profiling.Millis - lastMillis > yieldInterval)
                {
                    lastMillis = Profiling.Millis;
                    progress = 0.15f + (i + 1) * 0.7f / queue.Length;
                    LoadingScreen.instance.SetProgress(progress, progress, assetCount, assetCount - i - 1 + queue.Length, beginMillis, lastMillis);
                    yield return null;
                }
            }

            lastMillis = Profiling.Millis;
            LoadingScreen.instance.SetProgress(0.85f, 1f, assetCount, assetCount, beginMillis, lastMillis);
            LoadingManager.instance.m_loadingProfilerCustomContent.EndLoading();
            Util.DebugPrint("Custom assets loaded in", lastMillis - beginMillis);
            PrintMem();
            queue = null;
            stack.Clear();
            Report();

            LoadingManager.instance.m_loadingProfilerCustomContent.BeginLoading("Finalizing District Styles");
            LoadingManager.instance.m_loadingProfilerCustomAsset.PauseLoading();

            for (i = 0; i < districtStyleMetaDatas.m_size; i++)
            {
                try
                {
                    districtStyleMetaData = districtStyleMetaDatas.m_buffer[i];
                    districtStyle = new DistrictStyle(districtStyleMetaData.name, false);

                    if (districtStylePackages.m_buffer[i].GetPublishedFileID() != PublishedFileId.invalid)
                        districtStyle.PackageName = districtStylePackages.m_buffer[i].packageName;

                    if (districtStyleMetaData.assets != null)
                    {
                        for(j = 0; j < districtStyleMetaData.assets.Length; j++)
                        {
                            BuildingInfo bi = CustomDeserializer.FindLoaded<BuildingInfo>(districtStyleMetaData.assets[j] + "_Data");

                            if (bi != null)
                            {
                                districtStyle.Add(bi);

                                if (districtStyleMetaData.builtin) // this is always false
                                    bi.m_dontSpawnNormally = !districtStyleMetaData.assetRef.isEnabled;
                            }
                            else
                                CODebugBase<LogChannel>.Warn(LogChannel.Modding, "Warning: Missing asset (" + districtStyleMetaData.assets[j] + ") in style " + districtStyleMetaData.name);
                        }

                        districtStyles.Add(districtStyle);
                    }
                }
                catch (Exception ex)
                {
                    CODebugBase<LogChannel>.Warn(LogChannel.Modding, ex.GetType() + ": Loading district style failed\n" + ex.Message);
                }
            }

            Singleton<DistrictManager>.instance.m_Styles = districtStyles.ToArray();

            if (Singleton<BuildingManager>.exists)
                Singleton<BuildingManager>.instance.InitializeStyleArray(districtStyles.Count);

            LoadingManager.instance.m_loadingProfilerCustomAsset.ContinueLoading();
            LoadingManager.instance.m_loadingProfilerCustomContent.EndLoading();
            LoadingManager.instance.m_loadingProfilerMain.EndLoading();
            LevelLoader.instance.assetsFinished = true;
        }

        internal static void PrintMem()
        {
            string s = "[LSMT] Mem ";

            try
            {
                if (Application.platform == RuntimePlatform.WindowsPlayer)
                {
                    MemoryAPI.GetUsage(out int pfMegas, out int wsMegas);
                    s += string.Concat(wsMegas.ToString(), " ", pfMegas.ToString(), " ");
                }

                s = string.Concat(s, GC.CollectionCount(0).ToString());

                if (Sharing.HasInstance)
                    s += string.Concat(" ", Sharing.instance.Misses.ToString(), " ", Sharing.instance.WorkersAhead.ToString());
            }
            catch (Exception)
            {
            }

            Console.WriteLine(s);
        }

        internal void LoadImpl(Package.Asset assetRef)
        {
            try
            {
                stack.Push(assetRef);
                LoadingManager.instance.m_loadingProfilerCustomAsset.BeginLoading(ShortName(assetRef.name));
                CustomAssetMetaData.Type type = GetMetaType(assetRef);

                GameObject go = AssetDeserializer.Instantiate(assetRef) as GameObject;
                string packageName = assetRef.package.packageName;
                string fullName = type < CustomAssetMetaData.Type.RoadElevation ? packageName + "." + go.name : PillarOrElevationName(packageName, go.name);
                go.name = fullName;
                go.SetActive(false);
                PrefabInfo info = go.GetComponent<PrefabInfo>();
                info.m_isCustomContent = true;

                if (info.m_Atlas != null && !string.IsNullOrEmpty(info.m_InfoTooltipThumbnail) && info.m_Atlas[info.m_InfoTooltipThumbnail] != null)
                    info.m_InfoTooltipAtlas = info.m_Atlas;

                PropInfo pi;
                TreeInfo ti;
                BuildingInfo bi;
                VehicleInfo vi;
                CitizenInfo ci;
                NetInfo ni;

                if ((pi = go.GetComponent<PropInfo>()) != null)
                {
                    if (pi.m_lodObject != null)
                        pi.m_lodObject.SetActive(false);

                    Initialize(pi);
                }
                else if ((ti = go.GetComponent<TreeInfo>()) != null)
                    Initialize(ti);
                else if ((bi = go.GetComponent<BuildingInfo>()) != null)
                {
                    if (bi.m_lodObject != null)
                        bi.m_lodObject.SetActive(false);

                    bi.m_dontSpawnNormally = dontSpawnNormally.Remove(fullName);
                    Initialize(bi);

                    if (bi.GetAI() is IntersectionAI)
                        loadedIntersections.Add(fullName);
                }
                else if ((vi = go.GetComponent<VehicleInfo>()) != null)
                {
                    if (vi.m_lodObject != null)
                        vi.m_lodObject.SetActive(false);

                    Initialize(vi);
                }
                else if ((ci = go.GetComponent<CitizenInfo>()) != null)
                {
                    if (ci.m_lodObject != null)
                        ci.m_lodObject.SetActive(false);

                    if (ci.InitializeCustomPrefab(citizenMetaDatas[assetRef.fullName]))
                    {
                        citizenMetaDatas.Remove(assetRef.fullName);
                        ci.gameObject.SetActive(true);
                        Initialize(ci);
                    }
                    else
                    {
                        info = null;
                        CODebugBase<LogChannel>.Warn(LogChannel.Modding, "Custom citizen [" + assetRef.fullName + "] template not available in selected theme. Asset not added in game.");
                    }
                }
                else if ((ni = go.GetComponent<NetInfo>()) != null)
                    Initialize(ni);
                else
                    info = null;

                if (info != null)
                {
                    string path = Path.GetDirectoryName(assetRef.package.packagePath);

                    if (!string.IsNullOrEmpty(path) && plugins.TryGetValue(path, out PluginInfo plugin))
                    {
                        IAssetDataExtension[] extensions = plugin.GetInstances<IAssetDataExtension>();

                        if (extensions.Length > 0)
                        {
                            UserAssetData uad = GetUserAssetData(assetRef, out string name);

                            if (uad != null)
                            {
                                for (int i = 0; i < extensions.Length; i++)
                                {
                                    try
                                    {
                                        extensions[i].OnAssetLoaded(name, info, uad.Data);
                                    }
                                    catch (Exception e)
                                    {
                                        ModException ex = new ModException("The Mod " + plugin.ToString() + " has caused an error when loading " + fullName, e);
                                        UIView.ForwardException(ex);
                                        Debug.LogException(ex);
                                    }
                                }
                            }
                            else
                                Util.DebugPrint("UserAssetData is null for", fullName);
                        }
                    }
                }
            }
            finally
            {
                stack.Pop();
                assetCount++;
                LoadingManager.instance.m_loadingProfilerCustomAsset.EndLoading();
            }
        }

        void Initialize<T>(T info) where T : PrefabInfo
        {
            string fullName = info.name;
            string brokenAssets = LoadingManager.instance.m_brokenAssets;
            PrefabCollection<T>.InitializePrefabs("Custom Assets", info, null);
            LoadingManager.instance.m_brokenAssets = brokenAssets;

            if (CustomDeserializer.FindLoaded<T>(fullName, tryName:false) == null)
                throw new Exception(string.Concat(typeof(T).Name, " ", fullName, " failed"));
        }

        int PackageComparison(Package a, Package b)
        {
            int d = string.Compare(a.packageName, b.packageName);

            if (d == 0)
            {
                bool a1 = IsEnabled(a), b1 = IsEnabled(b);
                return a1 == b1 ? 0 : a1 ? -1 : 1;
            }

            return d;
        }

        Package.Asset[] GetLoadQueue(HashSet<string> styleBuildings)
        {
            Package.AssetType[] customAssets = { UserAssetType.CustomAssetMetaData };
            Package[] packages = { };

            try
            {
                packages = PackageManager.allPackages.Where(p => p.FilterAssets(customAssets).Any()).ToArray();
                Array.Sort(packages, PackageComparison);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }

            List<Package.Asset> assets = new List<Package.Asset>(8);
            List<CustomAssetMetaData> metas = new List<CustomAssetMetaData>(8);

            // Why this load order? By having related and identical assets close to each other, we get more loader cache hits (of meshes and textures)
            // in Sharing. We also get faster disk reads.
            // [0] propvar and prop, tree, citizen  [1] prop      [2] pillar and elevation and road  [3] road
            // [4] sub-building and building        [5] building  [6] trailer and vehicle            [7] vehicle
            List<Package.Asset>[] queues = { new List<Package.Asset>(32), new List<Package.Asset>(64), new List<Package.Asset>(4),  new List<Package.Asset>(4),
                                             new List<Package.Asset>(16), new List<Package.Asset>(64), new List<Package.Asset>(32), new List<Package.Asset>(32) };

            Util.DebugPrint("Sorted at", Profiling.Millis);
            SteamHelper.DLC_BitMask notMask = ~SteamHelper.GetOwnedDLCMask();
            bool loadEnabled = Settings.settings.loadEnabled, loadUsed = Settings.settings.loadUsed;
            //PrintPackages(packages);

            foreach (Package p in packages)
            {
                CustomAssetMetaData meta = null;

                try
                {
                    assets.Clear();
                    assets.AddRange(p.FilterAssets(customAssets));
                    int count = assets.Count;

                    CustomDeserializer.instance.AddPackage(p);
                    bool enabled = loadEnabled && IsEnabled(p);

                    if (count == 1) // the common case
                    {
                        bool want = enabled || styleBuildings.Contains(assets[0].fullName);

                        // Fast exit.
                        if (!want && !(loadUsed && UsedAssets.instance.GotPackage(p.packageName)))
                            continue;

                        meta = AssetDeserializer.Instantiate(assets[0]) as CustomAssetMetaData;
                        bool used = loadUsed && UsedAssets.instance.IsUsed(meta);

                        if (used || want && (AssetImporterAssetTemplate.GetAssetDLCMask(meta) & notMask) == 0)
                        {
                            if (reportAssets)
                                AssetReport.instance.AddPackage(p, meta, want, used);

                            CustomAssetMetaData.Type type = meta.type;
                            int offset = type == CustomAssetMetaData.Type.Trailer || type == CustomAssetMetaData.Type.SubBuilding ||
                                type == CustomAssetMetaData.Type.PropVariation || type >= CustomAssetMetaData.Type.RoadElevation ? -1 : 0;

                            AddToQueue(queues, meta, type, offset, !(enabled | used));
                        }
                    }
                    else
                    {
                        bool want = enabled;

                        // Fast exit.
                        if (!want)
                        {
                            for (int i = 0; i < count; i++)
                                want = want || styleBuildings.Contains(assets[i].fullName);

                            if (!want && !(loadUsed && UsedAssets.instance.GotPackage(p.packageName)))
                                continue;
                        }

                        metas.Clear();
                        bool used = false;

                        for (int i = 0; i < count; i++)
                        {
                            meta = AssetDeserializer.Instantiate(assets[i]) as CustomAssetMetaData;
                            metas.Add(meta);
                            want = want && (AssetImporterAssetTemplate.GetAssetDLCMask(meta) & notMask) == 0;
                            used = used || loadUsed && UsedAssets.instance.IsUsed(meta);
                        }

                        if (want | used)
                        {
                            metas.Sort((a, b) => b.type - a.type); // prop variation, sub-building, trailer, elevation, pillar before main asset
                            CustomAssetMetaData lastmeta = metas[count - 1];

                            if (reportAssets)
                                AssetReport.instance.AddPackage(p, lastmeta, want, used);

                            CustomAssetMetaData.Type type = metas[0].type;
                            int offset = type == CustomAssetMetaData.Type.Trailer || type == CustomAssetMetaData.Type.SubBuilding ||
                                type == CustomAssetMetaData.Type.PropVariation || type >= CustomAssetMetaData.Type.RoadElevation ? -1 : 0;
                            bool treeVariations = lastmeta.type == CustomAssetMetaData.Type.Tree, dontSpawn = !(enabled | used);

                            for (int i = 0; i < count; i++)
                            {
                                CustomAssetMetaData m = metas[i];
                                CustomAssetMetaData.Type t = m.type;

                                if (treeVariations && t == CustomAssetMetaData.Type.PropVariation)
                                    t = CustomAssetMetaData.Type.Tree;

                                AddToQueue(queues, m, t, offset, dontSpawn);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    AssetFailed(meta?.assetRef, p, e);
                }
            }

            CheckSuspects();

            for (int i = 0; i < queuedLoads.Length; i++)
            {
                queuedLoads[i].Clear();
                queuedLoads[i] = null;
            }

            queuedLoads = null;
            Package.Asset[] queue = new Package.Asset[queues.Sum(lst => lst.Count)];

            for (int i = 0, k = 0; i < queues.Length; i++)
            {
                queues[i].CopyTo(queue, k);
                k += queues[i].Count;
                queues[i].Clear(); queues[i] = null;
            }

            return queue;
        }

        void AddToQueue(List<Package.Asset>[] queues, CustomAssetMetaData meta, CustomAssetMetaData.Type type, int offset, bool dontSpawn)
        {
            Package.Asset assetRef = meta.assetRef;

            if (assetRef == null)
            {
                Util.DebugPrint(meta.name, " Error : NULL asset");
                return;
            }

            if (assetRef.fullName == null)
                Util.DebugPrint(meta.name, " Warning : NULL asset name");

            Package package = assetRef.package;
            string fullName = type < CustomAssetMetaData.Type.RoadElevation ? assetRef.fullName : PillarOrElevationName(package.packageName, assetRef.name);

            if (!IsDuplicate(assetRef, type, queues))
            {
                int index = Math.Max(loadQueueIndex[(int) type] + offset, 0);
                queues[index].Add(assetRef);
                metaDatas[assetRef.fullName] = new SomeMetaData(meta.userDataRef, meta.name, type);

                if (dontSpawn)
                    dontSpawnNormally.Add(fullName);

                if (type == CustomAssetMetaData.Type.Citizen)
                    citizenMetaDatas[fullName] = meta;
            }
        }

        bool IsDuplicate(Package.Asset assetRef, CustomAssetMetaData.Type type, List<Package.Asset>[] queues)
        {
            string fullName = assetRef.fullName;

            if (!queuedLoads[(int) type].Add(fullName))
            {
                if (suspects.TryGetValue(fullName, out List<Package.Asset> assets))
                    assets.Add(assetRef);
                else
                {
                    int index = loadQueueIndex[(int) type];
                    int[] indices = index > 0 ? new int[] { index, index - 1 } : new int[] { index };

                    foreach (int i in indices)
                        foreach (Package.Asset a in queues[i])
                            if (fullName == a.fullName)
                            {
                                suspects.Add(fullName, new List<Package.Asset>(2) { a, assetRef });
                                return true;
                            }

                    suspects.Add(fullName, new List<Package.Asset>(2) { assetRef });
                }

                return true;
            }

            return false;
        }

        void CheckSuspects()
        {
            foreach (KeyValuePair<string, List<Package.Asset>> kvp in suspects)
            {
                List<Package.Asset> assets = kvp.Value;
                int n = assets.Select(a => a.checksum).Distinct().Count();

                if (n > 1)
                    Duplicate(kvp.Key, assets);
            }

            suspects.Clear(); suspects = null;
        }

        static bool IsEnabled(Package package)
        {
            Package.Asset mainAsset = package.Find(package.packageMainAsset);
            return mainAsset?.isEnabled ?? true;
        }

        internal CustomAssetMetaData.Type GetMetaType(Package.Asset assetRef)
        {
            if (metaDatas.TryGetValue(assetRef.fullName, out SomeMetaData some))
                return some.type;

            ReadMetaData(assetRef.package);

            if (metaDatas.TryGetValue(assetRef.fullName, out some))
                return some.type;

            Util.DebugPrint("!Cannot resolve metatype for", assetRef.fullName);
            return CustomAssetMetaData.Type.Unknown;
        }

        UserAssetData GetUserAssetData(Package.Asset assetRef, out string name)
        {
            if (!metaDatas.TryGetValue(assetRef.fullName, out SomeMetaData some))
            {
                ReadMetaData(assetRef.package);
                metaDatas.TryGetValue(assetRef.fullName, out some);
            }

            if (some.userDataRef != null)
            {
                try
                {
                    UserAssetData uad = AssetDeserializer.Instantiate(some.userDataRef) as UserAssetData;

                    if (uad == null)
                        uad = new UserAssetData();

                    name = some.name;
                    return uad;
                }
                catch (Exception)
                {
                    Util.DebugPrint("!Cannot resolve UserAssetData for", assetRef.fullName);
                }
            }

            name = string.Empty;
            return null;
        }

        void ReadMetaData(Package package)
        {
            foreach (Package.Asset asset in package.FilterAssets(UserAssetType.CustomAssetMetaData))
            {
                try
                {
                    CustomAssetMetaData meta = AssetDeserializer.Instantiate(asset) as CustomAssetMetaData;
                    metaDatas[meta.assetRef.fullName] = new SomeMetaData(meta.userDataRef, meta.name, meta.type);
                }
                catch (Exception)
                {
                    Util.DebugPrint("!Cannot read metadata from", package.packageName);
                }
            }
        }

        internal static Package.Asset FindMainAssetRef(Package p) => p.FilterAssets(Package.AssetType.Object).LastOrDefault(a => a.name.EndsWith("_Data"));
        internal static string PillarOrElevationName(string packageName, string fullName) => packageName + "." + PackageHelper.StripName(fullName);
        internal static string ShortName(string name_Data) => name_Data.Length > 5 && name_Data.EndsWith("_Data") ? name_Data.Substring(0, name_Data.Length - 5) : name_Data;

        internal static string ShortAssetName(string fullName_Data)
        {
            int j = fullName_Data.IndexOf('.');

            if (j >= 0 && j < fullName_Data.Length - 1)
                fullName_Data = fullName_Data.Substring(j + 1);

            return ShortName(fullName_Data);
        }

        internal void AssetFailed(Package.Asset assetRef, Package p, Exception e)
        {
            string fullName = assetRef?.fullName;

            if (fullName == null)
            {
                assetRef = FindMainAssetRef(p);
                fullName = assetRef?.fullName;
            }

            if (fullName != null && LevelLoader.instance.AddFailed(fullName))
            {
                if (reportAssets)
                    AssetReport.instance.AssetFailed(assetRef);

                Util.DebugPrint("Asset failed:", fullName);
                DualProfilerSource profiler = LoadingScreen.instance.DualSource;
                profiler?.CustomAssetFailed(ShortAssetName(fullName));
            }

            if (e != null)
                UnityEngine.Debug.LogException(e);
        }

        internal void NotFound(string fullName)
        {
            if (fullName != null && LevelLoader.instance.AddFailed(fullName))
            {
                Util.DebugPrint("Asset missing:", fullName);
                DualProfilerSource profiler = LoadingScreen.instance.DualSource;
                profiler?.CustomAssetNotFound(ShortAssetName(fullName));
            }
        }

        internal void Duplicate(string fullName, List<Package.Asset> assets)
        {
            if (reportAssets)
                AssetReport.instance.Duplicate(assets);

            Util.DebugPrint("Duplicate name", fullName);
            DualProfilerSource profiler = LoadingScreen.instance.DualSource;
            profiler?.CustomAssetDuplicate(ShortAssetName(fullName));
        }

        //void PrintPlugins()
        //{
        //    foreach (KeyValuePair<string, PluginInfo> plugin in plugins)
        //    {
        //        Util.DebugPrint("Plugin ", plugin.Value.name);
        //        Util.DebugPrint("  path", plugin.Key);
        //        Util.DebugPrint("  id", plugin.Value.publishedFileID);
        //        Util.DebugPrint("  assemblies", plugin.Value.assemblyCount);
        //        Util.DebugPrint("  asset data extensions", plugin.Value.GetInstances<IAssetDataExtension>().Length);
        //    }
        //}

        //static void PrintPackages(Package[] packages)
        //{
        //    foreach (Package p in packages)
        //    {
        //        Trace.Pr(p.packageName, "\t\t", p.packagePath, "   ", p.version);

        //        foreach (Package.Asset a in p)
        //            Trace.Pr(a.isMainAsset ? " *" : "  ", a.fullName.PadRight(116), a.checksum, a.type.ToString().PadRight(19),
        //                a.offset.ToString().PadLeft(8), a.size.ToString().PadLeft(8));
        //    }
        //}

        //internal static void PrintProfilers()
        //{
        //    LoadingProfiler[] pp = { LoadingManager.instance.m_loadingProfilerMain, LoadingManager.instance.m_loadingProfilerScenes,
        //            LoadingManager.instance.m_loadingProfilerSimulation, LoadingManager.instance.m_loadingProfilerCustomContent };
        //    string[] names = { "Main:", "Scenes:", "Simulation:", "Custom Content:" };
        //    int i = 0;

        //    using (StreamWriter w = new StreamWriter(Util.GetFileName("Profilers", "txt")))
        //        foreach (LoadingProfiler p in pp)
        //        {
        //            w.WriteLine(); w.WriteLine(names[i++]);
        //            FastList<LoadingProfiler.Event> events = ProfilerSource.GetEvents(p);

        //            foreach (LoadingProfiler.Event e in events)
        //                w.WriteLine((e.m_name ?? "").PadRight(36) + e.m_time.ToString().PadLeft(8) + "   " + e.m_type);
        //        }
        //}
    }

    struct SomeMetaData
    {
        internal Package.Asset userDataRef;
        internal string name;
        internal CustomAssetMetaData.Type type;

        internal SomeMetaData(Package.Asset u, string n, CustomAssetMetaData.Type t)
        {
            userDataRef = u;
            name = n;
            type = t;
        }
    }
}
