using ColossalFramework.Importers;
using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using ColossalFramework.Packaging;
using ColossalFramework;

namespace LoadingScreenModTest
{
    internal sealed class AssetDeserializer
    {
        readonly Package package;
        readonly PackageReader reader;
        bool isMain;

        public static object Instantiate(Package.Asset asset, bool isMain = true)
        {
            using (Stream stream = Sharing.instance.GetStream(asset))
            using (PackageReader reader = Sharing.instance.GetReader(stream))
            {
                return new AssetDeserializer(asset.package, reader, isMain).Deserialize();
            }
        }

        internal static object Instantiate(Package package, byte[] bytes, bool isMain)
        {
            using (MemStream stream = new MemStream(bytes, 0))
            using (PackageReader reader = new MemReader(stream))
            {
                return new AssetDeserializer(package, reader, isMain).Deserialize();
            }
        }

        internal AssetDeserializer(Package package, PackageReader reader, bool isMain)
        {
            this.package = package;
            this.reader = reader;
            this.isMain = isMain;
        }

        internal object Deserialize()
        {
            Type type;

            if (!DeserializeHeader(out type))
                return null;

            if (type == typeof(GameObject))
                return DeserializeGameObject();
            if (type == typeof(Mesh))
                return DeserializeMesh();
            if (type == typeof(Material))
                return DeserializeMaterial();
            if (type == typeof(Texture2D) || type == typeof(Image))
                return DeserializeTexture();
            if (typeof(ScriptableObject).IsAssignableFrom(type))
                return DeserializeScriptableObject(type);

            return DeserializeObject(type);
        }

        object DeserializeSingleObject(Type type, Type expectedType)
        {
            object obj = CustomDeserializer.CustomDeserialize(package, type, reader);

            if (obj != null)
                return obj;
            if (typeof(ScriptableObject).IsAssignableFrom(type))
                return Instantiate(FindAsset(reader.ReadString()), isMain);
            if (typeof(GameObject).IsAssignableFrom(type))
                return Instantiate(FindAsset(reader.ReadString()), isMain);

            try
            {
                if (package.version < 3 && expectedType != null && expectedType == typeof(Package.Asset))
                    return reader.ReadUnityType(expectedType);

                return reader.ReadUnityType(type, package);
            }
            catch (MissingMethodException)
            {
                Util.DebugPrint("Unsupported type for deserialization:", type.Name);
                return null;
            }
        }

        UnityEngine.Object DeserializeScriptableObject(Type type)
        {
            object obj = CustomDeserializer.CustomDeserialize(package, type, reader);

            if (obj != null)
                return (UnityEngine.Object) obj;

            ScriptableObject so = ScriptableObject.CreateInstance(type);
            so.name = reader.ReadString();
            DeserializeFields(so, type, false);
            return so;
        }

        void DeserializeFields(object obj, Type type, bool resolveMember)
        {
            int count = reader.ReadInt32();

            for (int i = 0; i < count; i++)
            {
                if (DeserializeHeader(out Type t, out string name))
                {
                    FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    if (field == null && resolveMember)
                        field = type.GetField(ResolveLegacyMember(t, type, name), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    object value;

                    if (t.IsArray)
                    {
                        int n = reader.ReadInt32();
                        Type elementType = t.GetElementType();

                        // Make the common case fast, avoid boxing.
                        if (elementType == typeof(float))
                        {
                            float[] array = new float[n]; value = array;

                            for (int j = 0; j < n; j++)
                                array[j] = reader.ReadSingle();
                        }
                        else if (elementType == typeof(Vector2))
                        {
                            Vector2[] array = new Vector2[n]; value = array;

                            for (int j = 0; j < n; j++)
                                array[j] = reader.ReadVector2();
                        }
                        else
                        {
                            Array array = Array.CreateInstance(elementType, n); value = array;
                            Type fieldType = field?.FieldType;

                            for (int j = 0; j < n; j++)
                                array.SetValue(DeserializeSingleObject(elementType, fieldType), j);
                        }
                    }
                    else
                    {
                        // Make the common case fast.
                        if (t == typeof(int))
                            value = reader.ReadInt32();
                        else if (t == typeof(bool))
                            value = reader.ReadBoolean();
                        else if (t == typeof(string))
                            value = reader.ReadString();
                        else if (t == typeof(float))
                            value = reader.ReadSingle();
                        else
                            value = DeserializeSingleObject(t, field?.FieldType);
                    }

                    field?.SetValue(obj, value);
                }
            }
        }

        UnityEngine.Object DeserializeGameObject()
        {
            string name = reader.ReadString();
            GameObject go = new GameObject(name);
            go.tag = reader.ReadString();
            go.layer = reader.ReadInt32();
            go.SetActive(reader.ReadBoolean());
            int count = reader.ReadInt32();
            isMain = count > 3;

            for (int i = 0; i < count; i++)
            {
                Type type;

                if (!DeserializeHeader(out type))
                    continue;

                if (type == typeof(Transform))
                    DeserializeTransform(go.transform);
                else if (type == typeof(MeshFilter))
                    DeserializeMeshFilter(go.AddComponent(type) as MeshFilter);
                else if (type == typeof(MeshRenderer))
                    DeserializeMeshRenderer(go.AddComponent(type) as MeshRenderer);
                else if (typeof(MonoBehaviour).IsAssignableFrom(type))
                    DeserializeMonoBehaviour((MonoBehaviour) go.AddComponent(type));
                else if (type == typeof(SkinnedMeshRenderer))
                    DeserializeSkinnedMeshRenderer(go.AddComponent(type) as SkinnedMeshRenderer);
                else if (type == typeof(Animator))
                    DeserializeAnimator(go.AddComponent(type) as Animator);
                else
                    throw new InvalidDataException("Unknown type to deserialize " + type.Name);
            }

            return go;
        }

        void DeserializeAnimator(Animator animator)
        {
            animator.applyRootMotion = reader.ReadBoolean();
            animator.updateMode = (AnimatorUpdateMode) reader.ReadInt32();
            animator.cullingMode = (AnimatorCullingMode) reader.ReadInt32();
        }

        UnityEngine.Object DeserializeTexture()
        {
            string name = reader.ReadString();
            bool linear = reader.ReadBoolean();
            int anisoLevel = package.version >= 6 ? reader.ReadInt32() : 1;
            int count = reader.ReadInt32();
            Image image = new Image(reader.ReadBytes(count));
            Texture2D texture2D = image.CreateTexture(linear);
            texture2D.name = name;
            texture2D.anisoLevel = anisoLevel;
            return texture2D;
        }

        MaterialData DeserializeMaterial()
        {
            string name = reader.ReadString();
            string shader = reader.ReadString();
            Material material = new Material(Shader.Find(shader));
            material.name = name;
            int count = reader.ReadInt32();
            int textureCount = 0;

            for (int i = 0; i < count; i++)
            {
                int kind = reader.ReadInt32();

                if (kind == 0)
                    material.SetColor(reader.ReadString(), reader.ReadColor());
                else if (kind == 1)
                    material.SetVector(reader.ReadString(), reader.ReadVector4());
                else if (kind == 2)
                    material.SetFloat(reader.ReadString(), reader.ReadSingle());
                else if (kind == 3)
                {
                    string propertyName = reader.ReadString();

                    if (!reader.ReadBoolean())
                    {
                        string checksum = reader.ReadString();
                        material.SetTexture(propertyName, Sharing.instance.GetTexture(checksum, package, isMain));
                        textureCount++;
                    }
                    else
                        material.SetTexture(propertyName, null);
                }
            }

            return new MaterialData(material, textureCount); ;
        }

        void DeserializeTransform(Transform transform)
        {
            transform.localPosition = reader.ReadVector3();
            transform.localRotation = reader.ReadQuaternion();
            transform.localScale = reader.ReadVector3();
        }

        void DeserializeMeshFilter(MeshFilter meshFilter)
        {
            meshFilter.sharedMesh = Sharing.instance.GetMesh(reader.ReadString(), package, isMain);
        }

        void DeserializeMonoBehaviour(MonoBehaviour behaviour)
        {
            DeserializeFields(behaviour, behaviour.GetType(), false);
        }

        object DeserializeObject(Type type)
        {
            object obj = CustomDeserializer.CustomDeserialize(package, type, reader);

            if (obj != null)
                return obj;

            obj = Activator.CreateInstance(type);
            reader.ReadString();
            DeserializeFields(obj, type, true);
            return obj;
        }

        void DeserializeMeshRenderer(MeshRenderer renderer)
        {
            int count = reader.ReadInt32();
            Material[] array = new Material[count];

            for (int i = 0; i < count; i++)
                array[i] = Sharing.instance.GetMaterial(reader.ReadString(), package, isMain);

            renderer.sharedMaterials = array;
        }

        void DeserializeSkinnedMeshRenderer(SkinnedMeshRenderer smr)
        {
            int count = reader.ReadInt32();
            Material[] array = new Material[count];

            for (int i = 0; i < count; i++)
                array[i] = Sharing.instance.GetMaterial(reader.ReadString(), package, isMain);

            smr.sharedMaterials = array;
            smr.sharedMesh = Sharing.instance.GetMesh(reader.ReadString(), package, isMain);
        }

        UnityEngine.Object DeserializeMesh()
        {
            Mesh mesh = new Mesh();
            mesh.name = reader.ReadString();
            mesh.vertices = reader.ReadVector3Array();
            mesh.colors = reader.ReadColorArray();
            mesh.uv = reader.ReadVector2Array();
            mesh.normals = reader.ReadVector3Array();
            mesh.tangents = reader.ReadVector4Array();
            mesh.boneWeights = reader.ReadBoneWeightsArray();
            mesh.bindposes = reader.ReadMatrix4x4Array();
            mesh.subMeshCount = reader.ReadInt32();

            for (int i = 0; i < mesh.subMeshCount; i++)
                mesh.SetTriangles(reader.ReadInt32Array(), i);

            return mesh;
        }

        bool DeserializeHeader(out Type type)
        {
            type = null;

            if (reader.ReadBoolean())
                return false;

            string typeName = reader.ReadString();
            type = Type.GetType(typeName);

            if (type == null)
            {
                type = Type.GetType(ResolveLegacyType(typeName));

                if (type == null)
                {
                    if (HandleUnknownType(typeName) < 0)
                        throw new InvalidDataException("Unknown type to deserialize " + typeName);

                    return false;
                }
            }

            return true;
        }

        Package.Asset FindAsset(string checksum) => package.FindByChecksum(checksum);

        bool DeserializeHeader(out Type type, out string name)
        {
            type = null;
            name = null;

            if (reader.ReadBoolean())
                return false;

            string typeName = reader.ReadString();
            type = Type.GetType(typeName);
            name = reader.ReadString();

            if (type == null)
            {
                type = Type.GetType(ResolveLegacyType(typeName));

                if (type == null)
                {
                    if (HandleUnknownType(typeName) < 0)
                        throw new InvalidDataException("Unknown type to deserialize " + typeName);

                    return false;
                }
            }

            return true;
        }

        int HandleUnknownType(string type)
        {
            int num = PackageHelper.UnknownTypeHandler(type);
            CODebugBase<InternalLogChannel>.Warn(InternalLogChannel.Packer, string.Concat("Unexpected type '", type, "' detected. No resolver handled this type. Skipping ", num, " bytes."));

            if (num > 0)
            {
                reader.ReadBytes(num);
                return num;
            }
            return -1;
        }

        static string ResolveLegacyType(string type)
        {
            string text = PackageHelper.ResolveLegacyTypeHandler(type);
            CODebugBase<InternalLogChannel>.Warn(InternalLogChannel.Packer, string.Concat("Unkown type detected. Attempting to resolve from '", type, "' to '", text, "'"));
            return text;
        }

        static string ResolveLegacyMember(Type fieldType, Type classType, string member)
        {
            string text = PackageHelper.ResolveLegacyMemberHandler(classType, member);
            CODebugBase<InternalLogChannel>.Warn(InternalLogChannel.Packer, string.Concat("Unkown member detected of type ", fieldType.FullName, " in ", classType.FullName,
                ". Attempting to resolve from '", member, "' to '", text, "'"));
            return text;
        }
    }

    internal struct MaterialData
    {
        internal Material material;
        internal int textureCount;

        internal MaterialData(Material m, int count)
        {
            this.material = m;
            this.textureCount = count;
        }
    }
}
