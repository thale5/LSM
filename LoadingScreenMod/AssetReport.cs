using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ColossalFramework.Packaging;
using UnityEngine;
using static CustomAssetMetaData.Type;
using static LoadingScreenMod.AssetReport;

namespace LoadingScreenMod
{
    internal sealed class AssetReport : Instance<AssetReport>
    {
        internal const int ENABLED = 1;
        internal const int USEDDIR = 2;
        internal const int USEDIND = 4;
        internal const int FAILED = 8;
        internal const int AVAILABLE = 16;
        internal const int MISSING = 32;
        internal const int NAME_CHANGED = 64;
        internal const int USED = USEDDIR | USEDIND;

        Dictionary<string, Item> assets = new Dictionary<string, Item>(256);
        List<List<Package.Asset>> duplicates = new List<List<Package.Asset>>(4);
        readonly string[] allHeadings = { "Buildings and parks", "Props", "Trees", "Vehicles", "Citizens", "Nets", "Nets in buildings and parks",
            "Props in buildings, parks and nets", "Trees in buildings, parks and nets" };
        readonly CustomAssetMetaData.Type[] allTypes = { CustomAssetMetaData.Type.Building, Prop, CustomAssetMetaData.Type.Tree, CustomAssetMetaData.Type.Vehicle,
            CustomAssetMetaData.Type.Citizen, Road, Road, Prop, CustomAssetMetaData.Type.Tree };
        int texturesShared, materialsShared, meshesShared;

        string filepath;
        StreamWriter w;
        static char[] forbidden = { ':', '*', '?', '<', '>', '|', '#', '%', '&', '{', '}', '$', '!', '@', '+', '`', '=', '\\', '/', '"', '\'' };
        const string steamid = @"<a target=""_blank"" href=""https://steamcommunity.com/sharedfiles/filedetails/?id=";
        const string privateAsset = @"<a target=""_blank"" href=""http://steamcommunity.com/workshop/filedetails/discussion/667342976/357284767251931800/"">Asset bug";
        const string spaces = "&nbsp;&nbsp;";
        const string enableOption = @", enable the option ""Load used assets"".";

        private AssetReport() { }

        internal void Dispose() => instance = null;

        internal void AssetFailed(Package.Asset assetRef)
        {
            Item item = FindItem(assetRef);

            if (item != null)
                item.Failed = true;
        }

        internal void Duplicate(List<Package.Asset> list) => duplicates.Add(list);

        internal void AddPackage(Package p, CustomAssetMetaData lastmeta, bool enabled, bool useddir)
        {
            Package.Asset mainAssetRef = lastmeta.assetRef ?? AssetLoader.FindMainAssetRef(p);
            string fullName = mainAssetRef?.fullName;

            if (!string.IsNullOrEmpty(fullName))
                assets[fullName] = new Available(mainAssetRef, lastmeta.type, enabled, useddir);
        }

        internal void AddPackage(Package p)
        {
            Package.Asset mainAssetRef = AssetLoader.FindMainAssetRef(p);
            string fullName = mainAssetRef?.fullName;

            if (!string.IsNullOrEmpty(fullName) && !IsKnown(fullName))
                assets.Add(fullName, new Available(mainAssetRef, Unknown, false, false));
        }

        internal bool IsKnown(Package.Asset assetRef) => assets.ContainsKey(assetRef.fullName);
        internal bool IsKnown(string fullName) => assets.ContainsKey(fullName);

        internal void AddReference(Package.Asset knownRef, string fullName, CustomAssetMetaData.Type type)
        {
            if (!assets.TryGetValue(fullName, out Item child))
                assets.Add(fullName, child = new Missing(fullName, type));
            else
                child.type = type;

            assets[knownRef.fullName].Add(child);
        }

        internal void AddMissing(string fullName, CustomAssetMetaData.Type type)
        {
            if (!assets.TryGetValue(fullName, out Item child))
                assets.Add(fullName, new Missing(fullName, type, useddir: true));
            else
                child.UsedDir = true;
        }

        void SetIndirectUsages()
        {
            foreach (Item item in assets.Values)
                if (item.UsedDir)
                    SetIndirectUsages(item);
        }

        void SetIndirectUsages(Item item)
        {
            if (item.Uses != null)
                foreach (Item child in item.Uses)
                    if (!child.UsedInd)
                    {
                        child.UsedInd = true;
                        SetIndirectUsages(child);
                    }
        }

        void SetNameChanges(Item[] missingItems)
        {
            foreach (Item missing in missingItems)
                if (missing.HasPackageName && CustomDeserializer.instance.HasPackages(missing.packageName))
                    missing.NameChanged = true;
        }

        Dictionary<Item, List<Item>> GetUsedBy()
        {
            Dictionary<Item, List<Item>> usedBy = new Dictionary<Item, List<Item>>(assets.Count / 4);

            try
            {
                foreach (Item item in assets.Values)
                    if (item.Uses != null)
                        foreach (Item child in item.Uses)
                            if (usedBy.TryGetValue(child, out List<Item> list))
                                list.Add(item);
                            else
                                usedBy.Add(child, new List<Item>(2) { item });

                Comparison<Item> f = (a, b) => string.Compare(a.name, b.name);

                foreach (List<Item> list in usedBy.Values)
                    list.Sort(f);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }

            return usedBy;
        }

        internal void Save(int textures, int materials, int meshes)
        {
            try
            {
                texturesShared = textures; materialsShared = materials; meshesShared = meshes;
                string cityName = AssetLoader.ShortName(LevelLoader.instance.cityName);

                foreach (char c in forbidden)
                    cityName = cityName.Replace(c, 'x');

                filepath = Util.GetFileName(cityName + " - AssetsReport", "htm");
                Util.DebugPrint("Saving report to", filepath);
                int t0 = Profiling.Millis;
                w = new StreamWriter(filepath);
                w.WriteLine(@"<!DOCTYPE html><html lang=""en""><head><meta charset=""UTF-8""><title>Assets Report</title><style>");
                w.WriteLine(@"* {font-family:sans-serif;}");
                w.WriteLine(@"body {background-color:#f9f6ea;}");
                w.WriteLine(@"div {margin:5px 1px 1px 18px;}");
                w.WriteLine(@".my {display:-webkit-flex;display:flex;}");
                w.WriteLine(@".my .mi {margin:10px 0px 0px 0px;min-width:29%;}");
                w.WriteLine(@".my .bx {line-height:125%;padding:8px 12px;background-color:#e8e5d4;border-radius:5px;margin:1px;min-width:56%;}");
                w.WriteLine(@".my .st {font-style:italic;margin:0px;min-width:29%;}");
                w.WriteLine(@"h1 {margin-top:10px;padding:24px 18px;background-color:#e8e5d4;}");
                w.WriteLine(@"h2 {margin-top:40px;border-bottom:1px solid black;}");
                w.WriteLine(@"h3 {margin-top:25px;margin-left:18px;}");
                w.WriteLine(@"a:link {color:#0000e0;text-decoration:inherit;}");
                w.WriteLine(@"a:visited {color:#0000b0;text-decoration:inherit;}");
                w.WriteLine(@"a:hover {text-decoration:underline;}");
                w.WriteLine(@"</style></head><body>");

                H1(Enc(cityName));
                Italics(@"Assets report for Cities: Skylines.");
                Italics(@"To stop saving these files, disable the option ""Save assets report"" in Loading Screen Mod.");
                string[] mainHeadings = allHeadings.Take(6).ToArray();
                CustomAssetMetaData.Type[] mainTypes = allTypes.Take(6).ToArray();

                H2("Assets that failed to load");
                Item[] failed = assets.Values.Which(FAILED).ToArray();

                if (failed.Length > 0)
                {
                    Report(failed, mainHeadings, mainTypes);
                    Array.Clear(failed, 0, failed.Length); failed = null;
                }
                else
                    Italics("No failed assets.");

                H2("Assets that are missing");

                if (Settings.settings.loadUsed)
                {
                    SetIndirectUsages();
                    Item[] missing = assets.Values.Which(MISSING).ToArray();
                    SetNameChanges(missing);

                    if (missing.Length > 0)
                    {
                        Italics("There are two reasons for an asset to appear in this section: (1) The asset is placed in the city but is missing (2) The asset is used by some other asset but is missing.");
                        ReportMissing(missing, GetUsedBy(), allHeadings, allTypes, USEDDIR, USEDDIR, USEDDIR, USEDDIR, USEDDIR, USEDDIR, 0);
                        Array.Clear(missing, 0, missing.Length); missing = null;
                    }
                    else
                        Italics("No missing assets.");
                }
                else
                    Italics("To track missing assets" + enableOption);

                H2("Duplicate asset names");
                ReportDuplicates();

                H2("The following custom assets are used in this city");

                if (Settings.settings.loadUsed)
                {
                    Item[] used = assets.Values.Which(USED).ToArray();

                    if (used.Length > 0)
                    {
                        Report(used, allHeadings, allTypes, USEDDIR, USEDDIR, USEDDIR, USEDDIR, USEDDIR, USEDDIR, USEDIND);
                        Array.Clear(used, 0, used.Length); used = null;
                    }
                    else
                        Italics("No used assets.");

                    H2("The following enabled assets are currently unnecessary (not used in this city)");
                    Item[] unnecessary = assets.Values.Where(item => item.Enabled && !item.Used && !AssetLoader.instance.IsIntersection(item.FullName)).ToArray();

                    if (unnecessary.Length > 0)
                    {
                        Italics("There are two reasons for an asset to appear in this section: (1) The asset is enabled but unnecessary (2) The asset is included in an enabled district style but is unnecessary.");
                        Report(unnecessary, mainHeadings, mainTypes);
                        Array.Clear(unnecessary, 0, unnecessary.Length); unnecessary = null;
                    }
                    else
                        Italics("No unnecesary assets.");
                }
                else
                    Italics("To track used assets" + enableOption);

                Util.DebugPrint("Report created in", Profiling.Millis - t0);
                //PrintAssets();
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
            finally
            {
                w?.Dispose();
                w = null;
                assets.Clear(); duplicates.Clear();
                assets = null; duplicates = null;
            }
        }

        internal void SaveStats()
        {
            try
            {
                Util.DebugPrint("Saving stats to", filepath);
                w = new StreamWriter(filepath, append:true);
                H2("Loading stats");
                H3("Performance");
                Stat("Custom assets loaded", AssetLoader.instance.assetCount, "assets");
                int dt = AssetLoader.instance.lastMillis - AssetLoader.instance.beginMillis;

                if (dt > 0)
                    Stat("Loading speed", (AssetLoader.instance.assetCount * 1000f / dt).ToString("F1"), "assets / second");

                Stat("Custom assets loading time", Profiling.TimeString(dt + 500), "minutes:seconds");
                Stat("Total loading time", Profiling.TimeString(Profiling.Millis + 500), "minutes:seconds");

                if (Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor)
                {
                    H3("Peak memory usage");
                    Stat("RAM", (MemoryAPI.wsMax / 1024f).ToString("F1"), "GB");
                    Stat("Virtual memory", (MemoryAPI.pfMax / 1024f).ToString("F1"), "GB");
                }

                H3("Sharing of custom asset resources");
                Stat("Textures", texturesShared, "times");
                Stat("Materials", materialsShared, "times");
                Stat("Meshes", meshesShared, "times");

                H3("Skipped prefabs");
                int[] counts = LevelLoader.instance.skipCounts;
                Stat("Building prefabs", counts[Matcher.BUILDINGS], string.Empty);
                Stat("Vehicle prefabs", counts[Matcher.VEHICLES], string.Empty);
                Stat("Prop prefabs", counts[Matcher.PROPS], string.Empty);
                w.WriteLine(@"</body></html>");
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
            finally
            {
                w?.Dispose();
                w = null;
            }
        }

        void Report(IEnumerable<Item> items, string[] headings, CustomAssetMetaData.Type[] types, params int[] usages)
        {
            int usage = 0;

            for (int i = 0; i < headings.Length; i++)
            {
                if (i < usages.Length)
                    usage = usages[i];

                Item[] selected = items.Which(types[i], usage).OrderBy(Name).ToArray();

                if (selected.Length > 0)
                {
                    H3(headings[i]);

                    foreach (Item item in selected)
                        Div(Ref(item));
                }
            }
        }

        void ReportMissing(IEnumerable<Item> items, Dictionary<Item, List<Item>> usedBy, string[] headings, CustomAssetMetaData.Type[] types, params int[] usages)
        {
            StringBuilder s = new StringBuilder(1024);
            int usage = 0;

            for (int i = 0; i < headings.Length; i++)
            {
                if (i < usages.Length)
                    usage = usages[i];

                Item[] selected;

                if (usage == USEDDIR)
                    selected = items.Which(types[i], usage).OrderBy(Name).ToArray();
                else
                    selected = items.Which(types[i]).Where(item => usedBy.ContainsKey(item)).OrderBy(Name).ToArray();

                if (selected.Length > 0)
                {
                    H3(headings[i]);

                    foreach (Item item in selected)
                    {
                        string r = Ref(item);
                        string desc = item.NameChanged ? GetNameChangedDesc(item) : string.Empty;

                        if (usage == USEDDIR)
                        {
                            if (!string.IsNullOrEmpty(desc))
                                r += string.Concat(spaces, "<i>", desc.Replace("<br>", " "), "</i>");

                            Div(r);
                            continue;
                        }

                        s.Length = 0;
                        s.Append("Used by:");
                        int workshopUses = 0;

                        foreach (Item p in usedBy[item])
                        {
                            s.Append("<br>" + Ref(p));

                            if (workshopUses < 2 && FromWorkshop(p))
                                workshopUses++;
                        }

                        if (string.IsNullOrEmpty(desc) && !FromWorkshop(item))
                            if (item.HasPackageName)
                            {
                                if (workshopUses > 1)
                                    desc = privateAsset + @"s:</a> Workshop assets use private asset (" + Enc(item.FullName) + ").";
                                else if (workshopUses == 1)
                                    desc = privateAsset + @":</a> Workshop asset uses private asset (" + Enc(item.FullName) + ").";
                            }
                            else if (item.FullName.EndsWith("_Data"))
                                desc = Enc(item.name) + " is probably a workshop prop or tree but no link is available.";
                            else
                                desc = Enc(item.name) + " is possibly DLC or mod content.";

                        if (!string.IsNullOrEmpty(desc))
                            s.Append("<br><br><i>" + desc + "</i>");

                        Div("my", Cl("mi", r) + Cl("bx", s.ToString()));
                    }
                }
            }
        }

        void ReportDuplicates()
        {
            duplicates.Sort((a, b) => string.Compare(a[0].fullName, b[0].fullName));
            StringBuilder s = new StringBuilder(512);
            int n = 0;

            foreach (List<Package.Asset> list in duplicates)
            {
                Item[] items = list.Select(a => FindItem(a)).Where(item => item != null).ToArray();

                if (items.Length > 1)
                {
                    string fullName = Enc(list[0].fullName);
                    s.Length = 0;
                    s.Append("Same asset name (" + fullName + ") in all of these:");

                    foreach (Item d in items)
                        s.Append("<br>" + Ref(d));

                    Div("my", Cl("mi", fullName) + Cl("bx", s.ToString()));
                    n++;
                }
            }

            if (n == 0)
                Italics("No duplicates.");
        }

        string GetNameChangedDesc(Item missing)
        {
            List<Package> packages = CustomDeserializer.instance.GetPackages(missing.packageName);
            Package.Asset asset = packages.Count == 1 ? AssetLoader.FindMainAssetRef(packages[0]) : null;
            string have = asset != null ? Ref(asset.package.packageName, AssetLoader.ShortName(asset.name)) : Ref(missing.packageName);

            return string.Concat("You have ", have, " but it does not contain ", Enc(missing.name),
                @".<br>Name probably <a target=""_blank"" href=""http://steamcommunity.com/workshop/filedetails/discussion/667342976/141136086940263481/"">changed</a> by the asset author.");
        }

        void Div(string line) => w.WriteLine(string.Concat("<div>", line, "</div>"));
        void Div(string cl, string line) => w.WriteLine(Cl(cl, line));
        void Italics(string line) => Div("<i>" + line + "</i>");
        void H1(string line) => w.WriteLine(string.Concat("<h1>", line, "</h1>"));
        void H2(string line) => w.WriteLine(string.Concat("<h2>", line, "</h2>"));
        void H3(string line) => w.WriteLine(string.Concat("<h3>", line, "</h3>"));
        void Stat(string stat, object value, string unit) => Div("my", Cl("st", stat) + Cl(value.ToString() + spaces + unit));

        static bool FromWorkshop(Item item) => FromWorkshop(item.packageName);
        static bool FromWorkshop(string packageName) => ulong.TryParse(packageName, out ulong id) && id > 99999999;
        static string Ref(Item item) => item.NameChanged ? Enc(item.name) : Ref(item.packageName, item.name);
        static string Ref(string packageName, string name) => FromWorkshop(packageName) ? string.Concat(steamid, packageName, "\">", Enc(name), "</a>") : Enc(name);
        static string Ref(string packageName) => FromWorkshop(packageName) ? string.Concat(steamid, packageName, "\">", "Workshop item ", packageName, "</a>") : Enc(packageName);
        static string Cl(string cl, string s) => string.Concat("<div class=\"", cl, "\">", s, "</div>");
        static string Cl(string s) => string.Concat("<div>", s, "</div>");
        static string Name(Item item) => item.name;

        Item FindItem(Package.Asset assetRef)
        {
            string fullName = AssetLoader.FindMainAssetRef(assetRef.package)?.fullName;
            return !string.IsNullOrEmpty(fullName) && assets.TryGetValue(fullName, out Item item) ? item : null;
        }

        // From a more recent mono version.
        // See license https://github.com/mono/mono/blob/master/mcs/class/System.Web/System.Web.Util/HttpEncoder.cs
        static string Enc(string s)
        {
            bool needEncode = false;
            int len = s.Length;

            for (int i = 0; i < len; i++)
            {
                char c = s[i];

                if (c == '&' || c == '"' || c == '<' || c == '>' || c > 159 || c == '\'')
                {
                    needEncode = true;
                    break;
                }
            }

            if (!needEncode)
                return s;

            StringBuilder output = new StringBuilder(len + 12);

            for (int i = 0; i < len; i++)
            {
                char ch = s[i];

                switch (ch)
                {
                    case '&':
                        output.Append("&amp;");
                        break;
                    case '>':
                        output.Append("&gt;");
                        break;
                    case '<':
                        output.Append("&lt;");
                        break;
                    case '"':
                        output.Append("&quot;");
                        break;
                    case '\'':
                        output.Append("&#39;");
                        break;
                    case '\uff1c':
                        output.Append("&#65308;");
                        break;
                    case '\uff1e':
                        output.Append("&#65310;");
                        break;
                    default:
                        output.Append(ch);
                        break;
                }
            }

            return output.ToString();
        }

        //void PrintAssets()
        //{
        //    List<Item> items = new List<Item>(assets.Values);
        //    items.Sort((a, b) => a.usage - b.usage);

        //    using (StreamWriter w = new StreamWriter(Util.GetFileName("Assets", "txt")))
        //        foreach (Item item in items)
        //        {
        //            string s = item.FullName.PadRight(56);
        //            s += item.Enabled ? " EN" : "   ";
        //            s += item.UsedDir ? " DIR" : "    ";
        //            s += item.UsedInd ? " IND" : "    ";
        //            s += item.Available ? " AV" : "   ";
        //            s += item.Missing ? " MI" : "   ";
        //            s += item.NameChanged ? " NM" : "   ";
        //            s += item.Failed ? " FAIL" : string.Empty;
        //            w.WriteLine(s);
        //        }
        //}
    }

    abstract class Item
    {
        internal string packageName, name;
        internal CustomAssetMetaData.Type type;
        internal byte usage;
        internal abstract string FullName { get; }
        internal virtual HashSet<Item> Uses => null;
        internal bool HasPackageName => !string.IsNullOrEmpty(packageName);
        internal bool Enabled => (usage & ENABLED) != 0;
        internal bool Available => (usage & AVAILABLE) != 0;
        internal bool Missing => (usage & MISSING) != 0;
        internal bool Used => (usage & USED) != 0;

        internal bool UsedDir
        {
            get => (usage & USEDDIR) != 0;
            set => usage |= USEDDIR;        // never unset
        }

        internal bool UsedInd
        {
            get => (usage & USEDIND) != 0;
            set => usage |= USEDIND;        // never unset
        }

        internal bool Failed
        {
            get => (usage & FAILED) != 0;
            set => usage |= FAILED;         // never unset
        }

        internal bool NameChanged
        {
            get => (usage & NAME_CHANGED) != 0;
            set => usage |= NAME_CHANGED;   // never unset
        }

        protected Item(string packageName, string name_Data, CustomAssetMetaData.Type type, int usage)
        {
            this.packageName = packageName;
            this.name = AssetLoader.ShortName(name_Data);
            this.type = type;
            this.usage = (byte) usage;
        }

        protected Item(string fullName, CustomAssetMetaData.Type type, int usage)
        {
            int j = fullName.IndexOf('.');

            if (j >= 0)
            {
                packageName = fullName.Substring(0, j);
                name = AssetLoader.ShortName(fullName.Substring(j + 1));
            }
            else
            {
                packageName = string.Empty;
                name = AssetLoader.ShortName(fullName);
            }

            this.type = type;
            this.usage = (byte) usage;
        }

        internal virtual void Add(Item child) { }
    }

    sealed class Available : Item
    {
        HashSet<Item> uses;
        Package.Asset mainAssetRef;
        internal override string FullName => mainAssetRef.fullName;
        internal override HashSet<Item> Uses => uses;

        internal Available(Package.Asset mainAssetRef, CustomAssetMetaData.Type type, bool enabled, bool useddir)
            : base(mainAssetRef.package.packageName, mainAssetRef.name, type, AVAILABLE | (enabled ? ENABLED : 0) | (useddir ? USEDDIR : 0))
        {
            this.mainAssetRef = mainAssetRef;
        }

        internal override void Add(Item child)
        {
            if (uses != null)
                uses.Add(child);
            else
                uses = new HashSet<Item> { child };
        }
    }

    sealed class Missing : Item
    {
        readonly string fullName;
        internal override string FullName => fullName;

        internal Missing(string fullName, CustomAssetMetaData.Type type, bool useddir = false)
            : base(fullName, type, MISSING | (useddir ? USEDDIR : 0))
        {
            this.fullName = fullName;
        }
    }

    static class Exts
    {
        internal static IEnumerable<Item> Which(this IEnumerable<Item> items, CustomAssetMetaData.Type type, int usage = 0)
        {
            Func<Item, bool> pred;

            if (usage == 0)
                pred = item => item.type == type;
            else
                pred = item => item.type == type && (item.usage & usage) != 0;

            return items.Where(pred);
        }

        internal static IEnumerable<Item> Which(this IEnumerable<Item> items, int usage) => items.Where(item => (item.usage & usage) != 0);
    }
}
