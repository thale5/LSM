using System;
using System.IO;
using System.Xml.Serialization;
using ColossalFramework.IO;
using ColossalFramework.UI;
using ICities;

namespace LoadingScreenModTest
{
    public class Settings
    {
        const string FILENAME = "LoadingScreenMod.xml";

        public int version = 6;
        public bool loadEnabled = true;
        public bool loadUsed = true;
        public bool shareTextures = true;
        public bool shareMaterials = true;
        public bool shareMeshes = true;
        public bool reportAssets = false;
        public string reportDir = string.Empty;
        public bool skipPrefabs = false;
        public string skipFile = string.Empty;
        private DateTime skipFileTimestamp = DateTime.MinValue;

        internal bool SkipPrefabs => skipPrefabs && SkipMatcher != null && ExceptMatcher != null;
        internal Matcher SkipMatcher { get; private set; }
        internal Matcher ExceptMatcher { get; private set; }

        static Settings singleton;
        internal static string DefaultSavePath => Path.Combine(Path.Combine(DataLocation.localApplicationData, "Report"), "LoadingScreenMod");
        internal static string DefaultSkipFile => Path.Combine(Path.Combine(DataLocation.localApplicationData, "SkippedPrefabs"), "skip.txt");

        public static Settings settings
        {
            get
            {
                if (singleton == null)
                    singleton = Load();

                return singleton;
            }
        }

        Settings() { }

        static Settings Load()
        {
            Settings s;

            try
            {
                XmlSerializer serializer = new XmlSerializer(typeof(Settings));

                using (StreamReader reader = new StreamReader(FILENAME))
                    s = (Settings) serializer.Deserialize(reader);
            }
            catch (Exception) { s = new Settings(); }

            if (string.IsNullOrEmpty(s.reportDir = s.reportDir?.Trim()))
                s.reportDir = DefaultSavePath;

            if (string.IsNullOrEmpty(s.skipFile = s.skipFile?.Trim()))
                s.skipFile = DefaultSkipFile;

            s.version = 6;
            return s;
        }

        void Save()
        {
            try
            {
                XmlSerializer serializer = new XmlSerializer(typeof(Settings));

                using (StreamWriter writer = new StreamWriter(FILENAME))
                    serializer.Serialize(writer, this);
            }
            catch (Exception e)
            {
                Util.DebugPrint("Settings.Save");
                UnityEngine.Debug.LogException(e);
            }
        }

        internal DateTime LoadSkipFile()
        {
            try
            {
                if (skipPrefabs)
                {
                    DateTime stamp;
                    bool fileExists = File.Exists(skipFile);

                    if (fileExists && skipFileTimestamp != (stamp = File.GetLastWriteTimeUtc(skipFile)))
                    {
                        Matcher[] matchers = Matcher.Load(skipFile);
                        SkipMatcher = matchers[0];
                        ExceptMatcher = matchers[1];
                        skipFileTimestamp = stamp;
                    }
                    else if (!fileExists)
                        Util.DebugPrint("File", skipFile, "does not exist");
                }
            }
            catch (Exception e)
            {
                Util.DebugPrint("Settings.LoadSkipFile");
                UnityEngine.Debug.LogException(e);
                SkipMatcher = ExceptMatcher = null;
                skipFileTimestamp = DateTime.MinValue;
            }

            return SkipPrefabs ? skipFileTimestamp : DateTime.MinValue;
        }

        internal void OnSettingsUI(UIHelperBase helper)
        {
            if (!BuildConfig.applicationVersion.StartsWith("1.11"))
            {
                CreateGroup(helper, "Major game update detected. Mod is now inactive.");
                return;
            }

            UIHelper group = CreateGroup(helper, "Loading options for custom assets", "Custom means workshop assets and assets created by yourself");
            Check(group, "Load enabled assets", "Load the assets enabled in Content Manager", loadEnabled, b => { loadEnabled = b; LevelLoader.instance?.Reset(); Save(); });
            Check(group, "Load used assets", "Load the assets you have placed in your city", loadUsed, b => { loadUsed = b; LevelLoader.instance?.Reset(); Save(); });
            Check(group, "Share textures", "Replace exact duplicates by references", shareTextures, b => { shareTextures = b; Save(); });
            Check(group, "Share materials", "Replace exact duplicates by references", shareMaterials, b => { shareMaterials = b; Save(); });
            Check(group, "Share meshes", "Replace exact duplicates by references", shareMeshes, b => { shareMeshes = b; Save(); });

            group = CreateGroup(helper, "Reports");
            Check(group, "Save assets report in this directory:", "Save a report of missing, failed and used assets", reportAssets, b => { reportAssets = b; Save(); });
            TextField(group, reportDir, OnReportDirChanged);

            group = CreateGroup(helper, "Prefab skipping");
            Check(group, "Skip the prefabs named in this file:", "Prefab means the built-in assets in the game", skipPrefabs, b => { skipPrefabs = b; Save(); });
            TextField(group, skipFile, OnSkipFileChanged);
        }

        UIHelper CreateGroup(UIHelperBase parent, string name, string tooltip = null)
        {
            UIHelper group = parent.AddGroup(name) as UIHelper;

            if (!string.IsNullOrEmpty(tooltip))
            {
                UIPanel content = group.self as UIPanel;
                UIPanel container = content?.parent as UIPanel;
                UILabel label = container?.Find<UILabel>("Label");

                if (label != null)
                    label.tooltip = tooltip;
            }

            return group;
        }

        void Check(UIHelper group, string text, string tooltip, bool enabled, OnCheckChanged action)
        {
            try
            {
                UIComponent check = group.AddCheckbox(text, enabled, action) as UIComponent;

                if (tooltip != null)
                    check.tooltip = tooltip;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
        }

        void TextField(UIHelper group, string text, OnTextChanged action)
        {
            try
            {
                UITextField field = group.AddTextfield(" ", " ", action, null) as UITextField;
                field.text = text;
                field.width *= 2.8f;
                UIComponent parent = field.parent;
                UILabel label = parent?.Find<UILabel>("Label");

                if (label != null)
                {
                    float h = label.height;
                    label.height = 0; label.Hide();
                    parent.height -= h;
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
        }

        void OnReportDirChanged(string text)
        {
            if (text != reportDir)
            {
                reportDir = text;
                Save();
            }
        }

        void OnSkipFileChanged(string text)
        {
            if (text != skipFile)
            {
                skipFile = text;
                SkipMatcher = ExceptMatcher = null;
                skipFileTimestamp = DateTime.MinValue;
                Save();
            }
        }
    }
}
