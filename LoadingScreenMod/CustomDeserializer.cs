using System;
using System.Collections.Generic;
using ColossalFramework.Packaging;
using UnityEngine;

namespace LoadingScreenMod
{
    internal sealed class CustomDeserializer : Instance<CustomDeserializer>
    {
        internal const string SKIP_PREFIX = "lsm___";
        Package.Asset[] assets;
        Dictionary<string, object> packages = new Dictionary<string, object>(256);
        HashSet<string> skippedProps;
        readonly bool loadUsed = Settings.settings.loadUsed, reportUsed = Settings.settings.reportAssets & Settings.settings.loadUsed;
        bool skipProps = Settings.settings.skipPrefabs;

        static Package.Asset[] Assets
        {
            get
            {
                if (instance.assets == null)
                    instance.assets = FilterAssets(Package.AssetType.Object);

                return instance.assets;
            }
        }

        static HashSet<string> SkippedProps
        {
            get
            {
                if (instance.skippedProps == null)
                {
                    instance.skippedProps = PrefabLoader.instance?.SkippedProps;

                    if (instance.skippedProps == null || instance.skippedProps.Count == 0)
                    {
                        instance.skipProps = false;
                        instance.skippedProps = new HashSet<string>();
                    }
                }

                return instance.skippedProps;
            }
        }

        private CustomDeserializer() { }

        internal void Dispose()
        {
            Fetch<BuildingInfo>.Dispose(); Fetch<PropInfo>.Dispose(); Fetch<TreeInfo>.Dispose(); Fetch<VehicleInfo>.Dispose();
            Fetch<CitizenInfo>.Dispose(); Fetch<NetInfo>.Dispose();
            assets = null; packages.Clear(); packages = null; skippedProps = null; instance = null;
        }

        internal static object CustomDeserialize(Package p, Type t, PackageReader r)
        {
            // Props and trees in buildings and parks.
            if (t == typeof(BuildingInfo.Prop))
            {
                string propName = r.ReadString();
                string treeName = r.ReadString();
                PropInfo pi = GetProp(propName);        // old name format (without package name) is possible
                TreeInfo ti = Get<TreeInfo>(treeName);  // old name format (without package name) is possible

                if (instance.reportUsed)
                {
                    if (!string.IsNullOrEmpty(propName))
                        AddRef(pi, propName, CustomAssetMetaData.Type.Prop);

                    if (!string.IsNullOrEmpty(treeName))
                        AddRef(ti, treeName, CustomAssetMetaData.Type.Tree);
                }

                return new BuildingInfo.Prop
                {
                    m_prop = pi,
                    m_tree = ti,
                    m_position = r.ReadVector3(),
                    m_angle = r.ReadSingle(),
                    m_probability = r.ReadInt32(),
                    m_fixedHeight = r.ReadBoolean()
                };
            }

            // Paths (nets) in buildings.
            if (t == typeof(BuildingInfo.PathInfo))
            {
                string fullName = r.ReadString();
                NetInfo ni = Get<NetInfo>(fullName);

                if (instance.reportUsed && !string.IsNullOrEmpty(fullName))
                    AddRef(ni, fullName, CustomAssetMetaData.Type.Road);

                BuildingInfo.PathInfo path = new BuildingInfo.PathInfo();
                path.m_netInfo = ni;
                path.m_nodes = r.ReadVector3Array();
                path.m_curveTargets = r.ReadVector3Array();
                path.m_invertSegments = r.ReadBoolean();
                path.m_maxSnapDistance = r.ReadSingle();

                if (p.version >= 5)
                {
                    path.m_forbidLaneConnection = r.ReadBooleanArray();
                    path.m_trafficLights = (BuildingInfo.TrafficLights[]) (object) r.ReadInt32Array();
                    path.m_yieldSigns = r.ReadBooleanArray();
                }

                return path;
            }

            if (t == typeof(Package.Asset))
                return r.ReadAsset(p);

            // It seems that trailers are listed in the save game so this is not necessary. Better to be safe however
            // because a missing trailer reference is fatal for the simulation thread.
            if (t == typeof(VehicleInfo.VehicleTrailer))
            {
                string name = r.ReadString();
                string fullName = p.packageName + "." + name;
                VehicleInfo vi = Get<VehicleInfo>(p, fullName, name, false);

                VehicleInfo.VehicleTrailer trailer;
                trailer.m_info = vi;
                trailer.m_probability = r.ReadInt32();
                trailer.m_invertProbability = r.ReadInt32();
                return trailer;
            }

            if (t == typeof(NetInfo.Lane))
            {
                return new NetInfo.Lane
                {
                    m_position = r.ReadSingle(),
                    m_width = r.ReadSingle(),
                    m_verticalOffset = r.ReadSingle(),
                    m_stopOffset = r.ReadSingle(),
                    m_speedLimit = r.ReadSingle(),
                    m_direction = (NetInfo.Direction) r.ReadInt32(),
                    m_laneType = (NetInfo.LaneType) r.ReadInt32(),
                    m_vehicleType = (VehicleInfo.VehicleType) r.ReadInt32(),
                    m_stopType = (VehicleInfo.VehicleType) r.ReadInt32(),
                    m_laneProps = GetNetLaneProps(p, r),
                    m_allowConnect = r.ReadBoolean(),
                    m_useTerrainHeight = r.ReadBoolean(),
                    m_centerPlatform = r.ReadBoolean(),
                    m_elevated = r.ReadBoolean()
                };
            }

            if (t == typeof(NetInfo.Segment))
            {
                NetInfo.Segment segment = new NetInfo.Segment();
                string checksum = r.ReadString();
                segment.m_mesh = string.IsNullOrEmpty(checksum) ? null : Sharing.instance.GetMesh(checksum, p, true);
                checksum = r.ReadString();
                segment.m_material = string.IsNullOrEmpty(checksum) ? null : Sharing.instance.GetMaterial(checksum, p, true);
                checksum = r.ReadString();
                segment.m_lodMesh = string.IsNullOrEmpty(checksum) ? null : Sharing.instance.GetMesh(checksum, p, false);
                checksum = r.ReadString();
                segment.m_lodMaterial = string.IsNullOrEmpty(checksum) ? null : Sharing.instance.GetMaterial(checksum, p, false);
                segment.m_forwardRequired = (NetSegment.Flags) r.ReadInt32();
                segment.m_forwardForbidden = (NetSegment.Flags) r.ReadInt32();
                segment.m_backwardRequired = (NetSegment.Flags) r.ReadInt32();
                segment.m_backwardForbidden = (NetSegment.Flags) r.ReadInt32();
                segment.m_emptyTransparent = r.ReadBoolean();
                segment.m_disableBendNodes = r.ReadBoolean();
                return segment;
            }

            if (t == typeof(NetInfo.Node))
            {
                NetInfo.Node node = new NetInfo.Node();
                string checksum = r.ReadString();
                node.m_mesh = string.IsNullOrEmpty(checksum) ? null : Sharing.instance.GetMesh(checksum, p, true);
                checksum = r.ReadString();
                node.m_material = string.IsNullOrEmpty(checksum) ? null : Sharing.instance.GetMaterial(checksum, p, true);
                checksum = r.ReadString();
                node.m_lodMesh = string.IsNullOrEmpty(checksum) ? null : Sharing.instance.GetMesh(checksum, p, false);
                checksum = r.ReadString();
                node.m_lodMaterial = string.IsNullOrEmpty(checksum) ? null : Sharing.instance.GetMaterial(checksum, p, false);
                node.m_flagsRequired = (NetNode.Flags) r.ReadInt32();
                node.m_flagsForbidden = (NetNode.Flags) r.ReadInt32();
                node.m_connectGroup = (NetInfo.ConnectGroup) r.ReadInt32();
                node.m_directConnect = r.ReadBoolean();
                node.m_emptyTransparent = r.ReadBoolean();
                return node;
            }

            if (t == typeof(NetInfo))
            {
                string name = r.ReadString();
                CustomAssetMetaData.Type type = AssetLoader.instance.GetMetaType(AssetLoader.instance.Current);

                if (type == CustomAssetMetaData.Type.Road || type == CustomAssetMetaData.Type.RoadElevation)
                    return Get<NetInfo>(p, name); // elevations, bridges, slopes, tunnels in nets
                else
                    return Get<NetInfo>(name); // train lines, metro lines in buildings (stations)
            }

            if (t == typeof(BuildingInfo))
            {
                string name = r.ReadString();
                CustomAssetMetaData.Type type = AssetLoader.instance.GetMetaType(AssetLoader.instance.Current);

                if (type == CustomAssetMetaData.Type.Road || type == CustomAssetMetaData.Type.RoadElevation)
                    return Get<BuildingInfo>(p, name); // pillars in nets
                else
                    return Get<BuildingInfo>(name); // do these exist?
            }

            // Sub-buildings in buildings.
            if (t == typeof(BuildingInfo.SubInfo))
            {
                string name = r.ReadString();
                string fullName = p.packageName + "." + name;
                BuildingInfo bi = null;

                if (fullName == AssetLoader.instance.Current.fullName || name == AssetLoader.instance.Current.fullName)
                    Util.DebugPrint("Warning:", fullName, "wants to be a sub-building for itself");
                else
                    bi = Get<BuildingInfo>(p, fullName, name, true);

                BuildingInfo.SubInfo subInfo = new BuildingInfo.SubInfo();
                subInfo.m_buildingInfo = bi;
                subInfo.m_position = r.ReadVector3();
                subInfo.m_angle = r.ReadSingle();
                subInfo.m_fixedHeight = r.ReadBoolean();
                return subInfo;
            }

            // Prop variations in props.
            if (t == typeof(PropInfo.Variation))
            {
                string name = r.ReadString();
                string fullName = p.packageName + "." + name;
                PropInfo pi = null;

                if (fullName == AssetLoader.instance.Current.fullName)
                    Util.DebugPrint("Warning:", fullName, "wants to be a prop variation for itself");
                else
                    pi = Get<PropInfo>(p, fullName, name, false);

                return new PropInfo.Variation
                {
                    m_prop = pi,
                    m_probability = r.ReadInt32()
                };
            }

            // Tree variations in trees.
            if (t == typeof(TreeInfo.Variation))
            {
                string name = r.ReadString();
                string fullName = p.packageName + "." + name;
                TreeInfo ti = null;

                if (fullName == AssetLoader.instance.Current.fullName)
                    Util.DebugPrint("Warning:", fullName, "wants to be a tree variation for itself");
                else
                    ti = Get<TreeInfo>(p, fullName, name, false);

                return new TreeInfo.Variation
                {
                    m_tree = ti,
                    m_probability = r.ReadInt32()
                };
            }

            if (t == typeof(VehicleInfo.MeshInfo))
            {
                VehicleInfo.MeshInfo meshinfo = new VehicleInfo.MeshInfo();
                string checksum = r.ReadString();

                if (!string.IsNullOrEmpty(checksum))
                {
                    Package.Asset asset = p.FindByChecksum(checksum);
                    GameObject go = AssetDeserializer.Instantiate(asset) as GameObject;
                    meshinfo.m_subInfo = go.GetComponent<VehicleInfoBase>();
                    go.SetActive(false);

                    if (meshinfo.m_subInfo.m_lodObject != null)
                        meshinfo.m_subInfo.m_lodObject.SetActive(false);
                }
                else
                    meshinfo.m_subInfo = null;

                meshinfo.m_vehicleFlagsForbidden = (Vehicle.Flags) r.ReadInt32();
                meshinfo.m_vehicleFlagsRequired = (Vehicle.Flags) r.ReadInt32();
                meshinfo.m_parkedFlagsForbidden = (VehicleParked.Flags) r.ReadInt32();
                meshinfo.m_parkedFlagsRequired = (VehicleParked.Flags) r.ReadInt32();
                return meshinfo;
            }

            return PackageHelper.CustomDeserialize(p, t, r);
        }

        static NetLaneProps GetNetLaneProps(Package p, PackageReader r)
        {
            int count = r.ReadInt32();
            NetLaneProps laneProps = ScriptableObject.CreateInstance<NetLaneProps>();
            laneProps.m_props = new NetLaneProps.Prop[count];

            for (int i = 0; i < count; i++)
                laneProps.m_props[i] = GetNetLaneProp(p, r);

            return laneProps;
        }

        static NetLaneProps.Prop GetNetLaneProp(Package p, PackageReader r)
        {
            string propName, treeName;

            NetLaneProps.Prop o = new NetLaneProps.Prop
            {
                m_flagsRequired = (NetLane.Flags) r.ReadInt32(),
                m_flagsForbidden = (NetLane.Flags) r.ReadInt32(),
                m_startFlagsRequired = (NetNode.Flags) r.ReadInt32(),
                m_startFlagsForbidden = (NetNode.Flags) r.ReadInt32(),
                m_endFlagsRequired = (NetNode.Flags) r.ReadInt32(),
                m_endFlagsForbidden = (NetNode.Flags) r.ReadInt32(),
                m_colorMode = (NetLaneProps.ColorMode) r.ReadInt32(),
                m_prop = GetProp(propName = r.ReadString()),
                m_tree = Get<TreeInfo>(treeName = r.ReadString()),
                m_position = r.ReadVector3(),
                m_angle = r.ReadSingle(),
                m_segmentOffset = r.ReadSingle(),
                m_repeatDistance = r.ReadSingle(),
                m_minLength = r.ReadSingle(),
                m_cornerAngle = r.ReadSingle(),
                m_probability = r.ReadInt32()
            };

            if (instance.reportUsed)
            {
                if (!string.IsNullOrEmpty(propName))
                    AddRef(o.m_prop, propName, CustomAssetMetaData.Type.Prop);

                if (!string.IsNullOrEmpty(treeName))
                    AddRef(o.m_tree, treeName, CustomAssetMetaData.Type.Tree);
            }

            return o;
        }

        static PropInfo GetProp(string fullName)
        {
            if (string.IsNullOrEmpty(fullName) || instance.skipProps && SkippedProps.Contains(fullName))
                return null;
            else
                return Get<PropInfo>(fullName);
        }

        // Works with (fullName = asset name), too.
        static T Get<T>(string fullName) where T : PrefabInfo
        {
            if (string.IsNullOrEmpty(fullName))
                return null;

            T info = FindLoaded<T>(fullName);

            if (info == null && Load(ref fullName, FindAsset(fullName)))
                info = FindLoaded<T>(fullName);

            return info;
        }

        // For nets and pillars, the reference can be to a custom asset (dotted) or a built-in asset.
        static T Get<T>(Package package, string name) where T : PrefabInfo
        {
            if (string.IsNullOrEmpty(name))
                return null;

            string stripName = PackageHelper.StripName(name);
            T info = FindLoaded<T>(package.packageName + "." + stripName);

            if (info == null)
            {
                Package.Asset data = package.Find(stripName);

                if (data != null)
                {
                    string fullName = data.fullName;

                    if (Load(ref fullName, data))
                        info = FindLoaded<T>(fullName);
                }
                else
                    info = Get<T>(name);
            }

            return info;
        }

        // For sub-buildings, name may be package.assetname.
        static T Get<T>(Package package, string fullName, string name, bool tryName) where T : PrefabInfo
        {
            T info = FindLoaded<T>(fullName);

            if (tryName && info == null)
                info = FindLoaded<T>(name);

            if (info == null)
            {
                Package.Asset data = package.Find(name);

                if (tryName && data == null)
                    data = FindAsset(name); // yes, name

                if (data != null)
                    fullName = data.fullName;
                else if (name.IndexOf('.') >= 0)
                    fullName = name;

                if (Load(ref fullName, data))
                    info = FindLoaded<T>(fullName);
            }

            return info;
        }

        // Optimized version.
        internal static T FindLoaded<T>(string fullName, bool tryName = true) where T : PrefabInfo
        {
            if (string.IsNullOrEmpty(fullName))
                return null;

            Dictionary<string, PrefabCollection<T>.PrefabData> prefabDict = Fetch<T>.PrefabDict;

            if (prefabDict.TryGetValue(fullName, out PrefabCollection<T>.PrefabData prefabData))
                return prefabData.m_prefab;

            // Old-style (early 2015) custom asset full name?
            if (tryName && fullName.IndexOf('.') < 0 && !LevelLoader.instance.HasFailed(fullName))
            {
                Package.Asset[] a = Assets;

                for (int i = 0; i < a.Length; i++)
                    if (fullName == a[i].name && prefabDict.TryGetValue(a[i].package.packageName + "." + fullName, out prefabData))
                        return prefabData.m_prefab;
            }

            return null;
        }

        /// <summary>
        /// Given packagename.assetname, find the asset. Works with (fullName = asset name), too.
        /// </summary>
        internal static Package.Asset FindAsset(string fullName)
        {
            // Fast fail.
            if (LevelLoader.instance.HasFailed(fullName))
                return null;

            int j = fullName.IndexOf('.');

            if (j >= 0)
            {
                string name = fullName.Substring(j + 1);

                if (instance.packages.TryGetValue(fullName.Substring(0, j), out object obj))
                    if (obj is Package p)
                        return p.Find(name);
                    else
                    {
                        List<Package> list = obj as List<Package>;
                        Package.Asset asset;

                        for (int i = 0; i < list.Count; i++)
                            if ((asset = list[i].Find(name)) != null)
                                return asset;
                    }
            }
            else
            {
                Package.Asset[] a = Assets;

                // We also try the old (early 2015) naming that does not contain the package name. FindLoaded does this, too.
                for (int i = 0; i < a.Length; i++)
                    if (fullName == a[i].name)
                        return a[i];
            }

            return null;
        }

        static bool Load(ref string fullName, Package.Asset data)
        {
            if (instance.loadUsed)
                if (data != null)
                    try
                    {
                        fullName = data.fullName;

                        // There is at least one asset (411236307) on the workshop that wants to include itself. Asset Editor quite
                        // certainly no longer accepts that but in the early days, it was possible.
                        if (fullName != AssetLoader.instance.Current.fullName && !LevelLoader.instance.HasFailed(fullName))
                        {
                            if (instance.reportUsed)
                                AssetReport.instance.AddPackage(data.package);

                            AssetLoader.instance.LoadImpl(data);
                            return true;
                        }
                    }
                    catch (Exception e)
                    {
                        AssetLoader.instance.AssetFailed(data, data.package, e);
                    }
                else
                    AssetLoader.instance.NotFound(fullName);
            else
                LevelLoader.instance.AddFailed(fullName);

            return false;
        }

        static void AddRef(PrefabInfo info, string fullName, CustomAssetMetaData.Type type)
        {
            if (info == null)
            {
                if (type == CustomAssetMetaData.Type.Prop && instance.skipProps && SkippedProps.Contains(fullName))
                    return;

                // The referenced asset is missing.
                Package.Asset container = FindContainer();

                if (container != null)
                    AssetReport.instance.AddReference(container, fullName, type);
            }
            else if (info.m_isCustomContent)
            {
                string r = info.name;
                Package.Asset container = FindContainer();

                if (!string.IsNullOrEmpty(r) && container != null)
                {
                    string packageName = container.package.packageName;
                    int i = r.IndexOf('.');
                    string r2;

                    if (i >= 0 && (i != packageName.Length || !r.StartsWith(packageName)) && (r2 = FindMain(r)) != null)
                        AssetReport.instance.AddReference(container, r2, type);
                }
            }
        }

        static Package.Asset FindContainer()
        {
            Package.Asset container = AssetLoader.instance.Current;

            if (AssetReport.instance.IsKnown(container))
                return container;

            return KnownMainAssetRef(container.package);
        }

        static string FindMain(string fullName)
        {
            if (AssetReport.instance.IsKnown(fullName))
                return fullName;

            Package.Asset asset = FindAsset(fullName);

            if (asset != null)
                return KnownMainAssetRef(asset.package)?.fullName;

            return null;
        }

        static Package.Asset KnownMainAssetRef(Package p)
        {
            Package.Asset mainAssetRef = AssetLoader.FindMainAssetRef(p);
            return !string.IsNullOrEmpty(mainAssetRef?.fullName) && AssetReport.instance.IsKnown(mainAssetRef) ?
                mainAssetRef : null;
        }

        // Optimized version for other mods.
        static string ResolveCustomAssetName(string fullName)
        {
            // Old (early 2015) name?
            if (fullName.IndexOf('.') < 0 && !fullName.StartsWith(SKIP_PREFIX) && !LevelLoader.instance.HasFailed(fullName))
            {
                Package.Asset[] a = Assets;

                for (int i = 0; i < a.Length; i++)
                    if (fullName == a[i].name)
                        return a[i].package.packageName + "." + fullName;
            }

            return fullName;
        }

        static Package.Asset[] FilterAssets(Package.AssetType assetType)
        {
            List<Package.Asset> enabled = new List<Package.Asset>(64), notEnabled = new List<Package.Asset>(64);

            try
            {
                foreach (Package.Asset asset in PackageManager.FilterAssets(assetType))
                    if (asset != null)
                        if (asset.isEnabled)
                            enabled.Add(asset);
                        else
                            notEnabled.Add(asset);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }

            // Why enabled assets first? Because in duplicate name situations, I want the enabled one to get through.
            Package.Asset[] ret = new Package.Asset[enabled.Count + notEnabled.Count];
            enabled.CopyTo(ret);
            notEnabled.CopyTo(ret, enabled.Count);
            enabled.Clear(); notEnabled.Clear();
            return ret;
        }

        internal void AddPackage(Package p)
        {
            string pname = p.packageName;

            if (string.IsNullOrEmpty(pname))
            {
                Util.DebugPrint(p.packagePath, " Error : no package name");
                return;
            }

            if (packages.TryGetValue(pname, out object obj))
            {
                if (obj is List<Package> list)
                    list.Add(p);
                else
                    packages[pname] = new List<Package>(4) { obj as Package, p };
            }
            else
                packages.Add(pname, p);
        }

        internal bool HasPackages(string packageName) => packages.ContainsKey(packageName);

        internal List<Package> GetPackages(string packageName)
        {
            if (packages.TryGetValue(packageName, out object obj))
                if (obj is Package p)
                    return new List<Package>(1) { p };
                else
                    return obj as List<Package>;
            else
                return null;
        }

        internal static bool AllAvailable<P>(HashSet<string> fullNames, HashSet<string> ignore) where P : PrefabInfo
        {
            foreach (string fullName in fullNames)
                if (!ignore.Contains(fullName) && FindLoaded<P>(fullName, tryName:false) == null)
                {
                    Util.DebugPrint("Not available:", fullName);
                    return false;
                }

            return true;
        }
    }

    static class Fetch<T> where T : PrefabInfo
    {
        static Dictionary<string, PrefabCollection<T>.PrefabData> prefabDict;

        internal static Dictionary<string, PrefabCollection<T>.PrefabData> PrefabDict
        {
            get
            {
                if (prefabDict == null)
                    prefabDict = (Dictionary<string, PrefabCollection<T>.PrefabData>) Util.GetStatic(typeof(PrefabCollection<T>), "m_prefabDict");

                return prefabDict;
            }
        }

        internal static void Dispose() => prefabDict = null;
    }
}
