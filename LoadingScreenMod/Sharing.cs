using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ColossalFramework.Importers;
using ColossalFramework.Packaging;
using UnityEngine;

namespace LoadingScreenMod
{
    internal sealed class Sharing : Instance<Sharing>
    {
        const int cacheDepth = 3;
        const int dataHistory = cacheDepth * 50;
        const int dataMax = 4 * dataHistory;
        const int evictLimit = dataMax / 6;
        const int materialMax = 3 * evictLimit / 4;
        const int removeTarget = 15;
        ConcurrentCounter loadAhead = new ConcurrentCounter(0, 0, cacheDepth), mtAhead = new ConcurrentCounter(0, 0, cacheDepth);
        internal void WaitForWorkers() => mtAhead.Decrement();
        internal int WorkersAhead => loadAhead.Value + mtAhead.Value;
        static bool Supports(Package.AssetType type) => type <= Package.UnityTypeEnd && type >= Package.UnityTypeStart;

        // The assets to load, ordered for maximum performance.
        volatile Package.Asset[] assetsQueue;
        object mutex = new object();

        // Asset checksum to asset data.
        LinkedHashMap<string, KeyValuePair<int, object>> data = new LinkedHashMap<string, KeyValuePair<int, object>>(dataHistory + 80);
        internal int currentCount;
        int maxCount, removedCount;

        // Meshes and textures from LoadWorker to MTWorker.
        ConcurrentQueue<KeyValuePair<Package.Asset, byte[]>> mtQueue = new ConcurrentQueue<KeyValuePair<Package.Asset, byte[]>>(48);

        // These are local to LoadWorker.
        List<Package.Asset> loadList = new List<Package.Asset>(32);
        Dictionary<string, byte[]> loadMap = new Dictionary<string, byte[]>(32);

        // Worker threads.
        Thread loadWorkerThread, mtWorkerThread;

        internal string ThreadStatus
        {
            get
            {
                bool b1 = loadWorkerThread.IsAlive, b2 = mtWorkerThread.IsAlive;
                return b1 & b2 ? string.Empty : string.Concat(" ", b1.ToString(), " ", b2.ToString());
            }
        }

        void LoadPackage(Package package, int index)
        {
            lock (mutex)
            {
                int total = data.Count, matCount = 0;
                int materialLimit = Mathf.Min(materialMax, dataMax - total - 20);

                foreach (Package.Asset asset in package)
                {
                    string name = asset.name, checksum = asset.checksum;
                    Package.AssetType type = asset.type;

                    if (!Supports(type) || name.EndsWith("_SteamPreview") || name.EndsWith("_Snapshot") || name == "UserAssetData")
                        continue;

                    // Some workshop assets contain hundreds of materials. Probably by mistake.
                    if (type == Package.AssetType.Texture && (texturesMain.ContainsKey(checksum) || texturesLod.ContainsKey(checksum)) ||
                        type == Package.AssetType.StaticMesh && meshes.ContainsKey(checksum) ||
                        type == Package.AssetType.Material && (materialsMain.ContainsKey(checksum) || materialsLod.ContainsKey(checksum) || ++matCount > materialLimit))
                        continue;

                    if (data.ContainsKey(checksum))
                        data.Reinsert(checksum);
                    else if (total < dataMax)
                    {
                        loadList.Add(asset);
                        total++;
                    }
                }
            }

            loadList.Sort((a, b) => (int) (a.offset - b.offset));

            using (FileStream fs = File.OpenRead(package.packagePath))
                for (int i = 0; i < loadList.Count; i++)
                {
                    Package.Asset asset = loadList[i];
                    byte[] bytes = LoadAsset(fs, asset);
                    loadMap[asset.checksum] = bytes;

                    if (asset.type == Package.AssetType.Texture || asset.type == Package.AssetType.StaticMesh)
                        mtQueue.Enqueue(new KeyValuePair<Package.Asset, byte[]>(asset, bytes));
                }

            lock (mutex)
            {
                foreach (var kvp in loadMap)
                    if (!data.ContainsKey(kvp.Key)) // this check is necessary
                        data.Add(kvp.Key, new KeyValuePair<int, object>(index, kvp.Value));
            }
        }

        byte[] LoadAsset(FileStream fs, Package.Asset asset)
        {
            int remaining = asset.size;

            if (remaining > 222444000 || remaining < 0)
                throw new IOException("Asset " + asset.fullName + " size: " + remaining);

            fs.Position = asset.offset;
            byte[] bytes = new byte[remaining];
            int got = 0;

            while (remaining > 0)
            {
                int n = fs.Read(bytes, got, remaining);

                if (n == 0)
                    throw new IOException("Unexpected end of file: " + asset.fullName);

                got += n; remaining -= n;
            }

            return bytes;
        }

        int Forward(Package p, Package.Asset[] q, int index)
        {
            while(++index < q.Length && ReferenceEquals(p, q[index].package))
                ;

            return index - 1;
        }

        void LoadWorker()
        {
            Thread.CurrentThread.Name = "LoadWorker";
            Package.Asset[] q = assetsQueue;
            Package prevPackage = null;

            for (int index = 0; index < q.Length; index++)
            {
                Package p = q[index].package;

                if (!ReferenceEquals(p, prevPackage))
                    try
                    {
                        LoadPackage(p, Forward(p, q, index));
                    }
                    catch (Exception e)
                    {
                        Util.DebugPrint("LoadWorker:", e.Message);
                    }

                mtQueue.Enqueue(default(KeyValuePair<Package.Asset, byte[]>)); // end-of-asset marker
                loadList.Clear(); loadMap.Clear();
                prevPackage = p;
                loadAhead.Increment();
            }

            mtQueue.SetCompleted();
            loadList = null; loadMap = null; assetsQueue = null;
        }

        void MTWorker()
        {
            Thread.CurrentThread.Name = "MTWorker A";
            KeyValuePair<Package.Asset, byte[]> elem;
            int index = 0;

            while (mtQueue.Dequeue(out elem))
            {
                try
                {
                    if (elem.Key == null)
                    {
                        mtAhead.Increment();
                        loadAhead.Decrement();
                        index++;
                    }
                    else if (elem.Key.type == Package.AssetType.Texture)
                        DeserializeTextObj(elem.Key, elem.Value, index);
                    else if (elem.Key.type == Package.AssetType.StaticMesh)
                        DeserializeMeshObj(elem.Key, elem.Value, index);
                }
                catch (Exception e)
                {
                    Util.DebugPrint("MTWorker:", e.Message);
                }
            }
        }

        void DeserializeMeshObj(Package.Asset asset, byte[] bytes, int index)
        {
            MeshObj mo;

            using (MemStream stream = new MemStream(bytes, 0))
            using (MemReader reader = new MemReader(stream))
            {
                if (DeserializeHeader(reader) != typeof(Mesh))
                    throw new IOException("Asset " + asset.fullName + " should be Mesh");

                string name = reader.ReadString();
                Vector3[] vertices = reader.ReadVector3Array();
                Color[] colors = reader.ReadColorArray();
                Vector2[] uv = reader.ReadVector2Array();
                Vector3[] normals = reader.ReadVector3Array();
                Vector4[] tangents = reader.ReadVector4Array();
                BoneWeight[] boneWeights = reader.ReadBoneWeightsArray();
                Matrix4x4[] bindposes = reader.ReadMatrix4x4Array();
                int count = reader.ReadInt32();
                int[][] triangles = new int[count][];

                for (int i = 0; i < count; i++)
                    triangles[i] = reader.ReadInt32Array();

                mo = new MeshObj { name = name, vertices = vertices, colors = colors, uv = uv, normals = normals,
                                   tangents = tangents, boneWeights = boneWeights, bindposes = bindposes, triangles = triangles };
            }

            lock (mutex)
            {
                data[asset.checksum] = new KeyValuePair<int, object>(index, mo);
            }
        }

        void DeserializeTextObj(Package.Asset asset, byte[] bytes, int index)
        {
            TextObj to;

            using (MemStream stream = new MemStream(bytes, 0))
            using (MemReader reader = new MemReader(stream))
            {
                Type t = DeserializeHeader(reader);

                if (t != typeof(Texture2D) && t != typeof(Image))
                    throw new IOException("Asset " + asset.fullName + " should be Texture2D or Image");

                string name = reader.ReadString();
                bool linear = reader.ReadBoolean();
                int anisoLevel = asset.package.version >= 6 ? reader.ReadInt32() : 1;
                int count = reader.ReadInt32();
                Image image = new Image(reader.ReadBytes(count));
                byte[] pix = image.GetAllPixels();

                to = new TextObj { name = name, pixels = pix, width = image.width, height = image.height, anisoLevel = anisoLevel,
                                   format = image.format, mipmap = image.mipmapCount > 1, linear = linear };

                // image.Clear(); TODO test
                image = null;
            }

            lock (mutex)
            {
                data[asset.checksum] = new KeyValuePair<int, object>(index, to);
            }
        }

        static Type DeserializeHeader(MemReader reader)
        {
            if (reader.ReadBoolean())
                return null;

            return Type.GetType(reader.ReadString());
        }

        internal void ManageLoadQueue(int index)
        {
            lock (mutex)
            {
                currentCount = data.Count;
                maxCount = Mathf.Max(currentCount, maxCount);
                int target = Mathf.Min(removeTarget - removedCount, currentCount - dataHistory);
                target = Mathf.Max(currentCount - dataMax + evictLimit, target);
                removedCount = 0;

                for (int count = 0; count < target; count++)
                {
                    string checksum = data.EldestKey;
                    KeyValuePair<int, object> kvp = data.RemoveEldest();

                    if (kvp.Key > index)
                    {
                        data.Add(checksum, kvp);
                        break;
                    }
                }
            }
        }

        internal Stream GetStream(Package.Asset asset)
        {
            string checksum = asset.checksum;
            KeyValuePair<int, object> kvp;

            lock (mutex)
            {
                if (data.TryGetValue(checksum, out kvp) && asset.size > 32768)
                {
                    data.Remove(checksum);
                    removedCount++;
                }
            }

            byte[] bytes = kvp.Value as byte[];

            if (bytes != null)
                return new MemStream(bytes, 0);

            //Util.DebugPrint("        GetStream miss:", asset.fullName, asset.package.packagePath, checksum);
            return asset.GetStream();
        }

        internal Mesh GetMesh(string checksum, Package package, bool isMain)
        {
            Mesh mesh;
            KeyValuePair<int, object> kvp;

            lock (mutex)
            {
                if (meshes.TryGetValue(checksum, out mesh))
                {
                    meshit++;
                    return mesh;
                }

                data.TryGetValue(checksum, out kvp);
            }

            MeshObj mo = kvp.Value as MeshObj;
            byte[] bytes;

            if (mo != null)
            {
                mesh = new Mesh();
                mesh.name = mo.name;
                mesh.vertices = mo.vertices;
                mesh.colors = mo.colors;
                mesh.uv = mo.uv;
                mesh.normals = mo.normals;
                mesh.tangents = mo.tangents;
                mesh.boneWeights = mo.boneWeights;
                mesh.bindposes = mo.bindposes;

                for (int i = 0; i < mo.triangles.Length; i++)
                    mesh.SetTriangles(mo.triangles[i], i);

                mespre++;
            }
            else if ((bytes = kvp.Value as byte[]) != null)
            {
                mesh = AssetDeserializer.Instantiate(package, bytes, isMain) as Mesh;
                mespre++;
            }
            else
            {
                Package.Asset asset = package.FindByChecksum(checksum);
                mesh = AssetDeserializer.Instantiate(asset, isMain) as Mesh;
                mesload++;
            }

            if (shareMeshes)
                lock (mutex)
                {
                    meshes[checksum] = mesh;

                    if (data.Remove(checksum))
                        removedCount++;
                }

            return mesh;
        }

        internal Texture2D GetTexture(string checksum, Package package, bool isMain)
        {
            Texture2D texture2D;
            KeyValuePair<int, object> kvp;

            lock (mutex)
            {
                if (isMain && texturesMain.TryGetValue(checksum, out texture2D))
                {
                    texhit++;
                    return texture2D;
                }
                else if (!isMain && (texturesLod.TryGetValue(checksum, out texture2D) || texturesMain.TryGetValue(checksum, out texture2D)))
                {
                    texpre++;
                    return UnityEngine.Object.Instantiate(texture2D);
                }

                data.TryGetValue(checksum, out kvp);
            }

            TextObj to = kvp.Value as TextObj;
            byte[] bytes;

            if (to != null)
            {
                texture2D = new Texture2D(to.width, to.height, to.format, to.mipmap, to.linear);
                texture2D.LoadRawTextureData(to.pixels);
                texture2D.Apply();
                texture2D.name = to.name;
                texture2D.anisoLevel = to.anisoLevel;
                texpre++;
            }
            else if ((bytes = kvp.Value as byte[]) != null)
            {
                texture2D = AssetDeserializer.Instantiate(package, bytes, isMain) as Texture2D;
                texpre++;
            }
            else
            {
                Package.Asset asset = package.FindByChecksum(checksum);
                texture2D = AssetDeserializer.Instantiate(asset, isMain) as Texture2D;
                texload++;
            }

            if (shareTextures)
                lock (mutex)
                {
                    if (isMain)
                        texturesMain[checksum] = texture2D;
                    else
                        texturesLod[checksum] = texture2D;

                    if (data.Remove(checksum))
                        removedCount++;
                }

            return texture2D;
        }

        internal Material GetMaterial(string checksum, Package package, bool isMain)
        {
            MaterialData mat;
            KeyValuePair<int, object> kvp;

            lock (mutex)
            {
                if (isMain && materialsMain.TryGetValue(checksum, out mat))
                {
                    mathit++;
                    texhit += mat.textureCount;
                    return mat.material;
                }
                else if (!isMain && (materialsLod.TryGetValue(checksum, out mat) || materialsMain.TryGetValue(checksum, out mat)))
                {
                    matpre++;
                    return new Material(mat.material);
                }

                data.TryGetValue(checksum, out kvp);
            }

            byte[] bytes = kvp.Value as byte[];

            if (bytes != null)
            {
                mat = (MaterialData) AssetDeserializer.Instantiate(package, bytes, isMain);
                matpre++;
            }
            else
            {
                Package.Asset asset = package.FindByChecksum(checksum);
                mat = (MaterialData) AssetDeserializer.Instantiate(asset, isMain);
                matload++;
            }

            if (shareMaterials)
                lock (mutex)
                {
                    if (isMain)
                        materialsMain[checksum] = mat;
                    else
                        materialsLod[checksum] = mat;

                    if (data.Remove(checksum))
                        removedCount++;
                }

            return mat.material;
        }

        internal PackageReader GetReader(Stream stream)
        {
            MemStream ms = stream as MemStream;
            return ms != null ? new MemReader(ms) : new PackageReader(stream);
        }

        // Texture / material / mesh sharing begins here.
        internal int texhit, mathit, meshit;
        int texpre, texload, matpre, matload, mespre, mesload;
        internal int Misses => texload + matload + mesload;
        Dictionary<string, Texture2D> texturesMain = new Dictionary<string, Texture2D>(128);
        Dictionary<string, Texture2D> texturesLod = new Dictionary<string, Texture2D>(128);
        Dictionary<string, MaterialData> materialsMain = new Dictionary<string, MaterialData>(64);
        Dictionary<string, MaterialData> materialsLod = new Dictionary<string, MaterialData>(64);
        Dictionary<string, Mesh> meshes = new Dictionary<string, Mesh>(128);
        bool shareTextures, shareMaterials, shareMeshes;

        private Sharing() { }

        internal void Dispose()
        {
            Util.DebugPrint("Textures / Materials / Meshes shared:", texhit, "/", mathit, "/", meshit, "pre-loaded:", texpre, "/", matpre, "/", mespre,
                            "loaded:", texload, "/", matload, "/", mesload);

            Util.DebugPrint("Max cache", maxCount);

            lock (mutex)
            {
                data.Clear();
                data = null;
                loadWorkerThread = null; mtWorkerThread = null;
                texturesMain.Clear(); texturesLod.Clear(); materialsMain.Clear(); materialsLod.Clear();  meshes.Clear();
                texturesMain = null; texturesLod = null; materialsMain = null; materialsLod = null; meshes = null; instance = null;
            }
        }

        internal void Start(Package.Asset[] queue)
        {
            assetsQueue = queue;
            shareTextures = Settings.settings.shareTextures;
            shareMaterials = Settings.settings.shareMaterials;
            shareMeshes = Settings.settings.shareMeshes;

            (loadWorkerThread = new Thread(LoadWorker)).Start();
            (mtWorkerThread = new Thread(MTWorker)).Start();
        }
    }

    sealed class MeshObj
    {
        internal string name;
        internal Vector3[] vertices;
        internal Color[] colors;
        internal Vector2[] uv;
        internal Vector3[] normals;
        internal Vector4[] tangents;
        internal BoneWeight[] boneWeights;
        internal Matrix4x4[] bindposes;
        internal int[][] triangles;
    }

    sealed class TextObj
    {
        internal string name;
        internal byte[] pixels;
        internal int width;
        internal int height;
        internal int anisoLevel;
        internal TextureFormat format;
        internal bool mipmap;
        internal bool linear;
    }

    // Critical fixes for loading performance.
    internal sealed class Fixes : DetourUtility<Fixes>
    {
        private Fixes()
        {
            init(typeof(BuildConfig), "ResolveCustomAssetName", typeof(CustomDeserializer), "ResolveCustomAssetName");
            init(typeof(PackageReader), "ReadByteArray", typeof(MemReader), "DreadByteArray");
        }
    }
}
