using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace LoadingScreenMod
{
    sealed class ByNames
    {
        readonly HashSet<string> names = new HashSet<string>();
        public bool Matches(string name) => names.Contains(name);
        public void AddName(string name) => names.Add(name);
    }

    sealed class ByPatterns
    {
        readonly List<Regex> patterns = new List<Regex>(1);

        public bool Matches(string name)
        {
            for (int i = 0; i < patterns.Count; i++)
            {
                Regex p = patterns[i];

                if (p == null || p.IsMatch(name))
                    return true;
            }

            return false;
        }

        public void AddPattern(string pattern, bool ic)
        {
            if (pattern == "^.*$")
                patterns.Insert(0, null);
            else
                patterns.Add(ic ? new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) : new Regex(pattern));
        }
    }

    sealed class Matcher
    {
        internal const int NUM = 3;
        internal const int BUILDINGS = 0;
        internal const int VEHICLES = 1;
        internal const int PROPS = 2;
        const int LEVELS = 3;
        internal bool[] Has { get; } = new bool[NUM];
        readonly ByNames[] byNames = { new ByNames(), new ByNames(), new ByNames() };
        readonly Dictionary<int, ByPatterns> byPatterns = new Dictionary<int, ByPatterns>(4);
        readonly HashSet<int> byDLCs = new HashSet<int>();

        void AddName(string name, int index)
        {
            byNames[index].AddName(name);
            Has[index] = true;
        }

        void AddPattern(string pattern, bool ic, int index, int svc)
        {
            int key = (index << 7) + svc;

            if (!byPatterns.TryGetValue(key, out ByPatterns p))
                p = byPatterns[key] = new ByPatterns();

            try
            {
                p.AddPattern(pattern, ic);
                Has[index] = true;
            }
            catch (Exception e)
            {
                Util.DebugPrint("Error in user regex:");
                UnityEngine.Debug.LogException(e);
            }
        }

        void AddDLC(int dlc) => byDLCs.Add(dlc);
        internal bool Matches(int dlc) => byDLCs.Contains(dlc);

        internal bool Matches(PrefabInfo info, int index)
        {
            string name = info.name.ToUpperInvariant();

            if (byNames[index].Matches(name))
                return true;

            int offset = index << 7;

            if (byPatterns.TryGetValue(offset - 1, out ByPatterns p) && p.Matches(name))
                return true;

            if (byPatterns.TryGetValue((int) info.GetService() + offset, out p) && p.Matches(name))
                return true;

            int svc = (int) info.GetSubService();
            return svc != 0 && byPatterns.TryGetValue(svc + 40 + offset, out p) && p.Matches(name);
        }

        internal static Matcher[] Load(string filePath)
        {
            Dictionary<string, int> servicePrefixes = Util.GetEnumMap(typeof(ItemClass.Service));
            Dictionary<string, int> subServicePrefixes = Util.GetEnumMap(typeof(ItemClass.SubService));
            Dictionary<string, int> dlcs = Util.GetEnumMap(typeof(SteamHelper.DLC));
            Matcher skip = new Matcher();
            Matcher except = new Matcher();
            string[] lines = File.ReadAllLines(filePath);
            Regex syntax = new Regex(@"^(?:([Ee]xcept|[Ss]kip)\s*:)?(?:([a-zA-Z \t]+):)?\s*([^@:#\t]+|@.+)$");
            int index = BUILDINGS;

            foreach (string raw in lines)
            {
                string line = raw.Trim();

                if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                    continue;

                int i = line.IndexOf(':');
                int j = line.IndexOf('@');

                if (i > 0 && j < 0)
                {
                    string tmp = line.ToUpperInvariant();

                    if (tmp.StartsWith("BUILDINGS"))
                    {
                        index = BUILDINGS;
                        continue;
                    }
                    if (tmp.StartsWith("VEHICLES"))
                    {
                        index = VEHICLES;
                        continue;
                    }
                    if (tmp.StartsWith("PROPS"))
                    {
                        index = PROPS;
                        continue;
                    }
                    if (tmp.StartsWith("LEVELS"))
                    {
                        index = LEVELS;
                        continue;
                    }
                }

                if (index == LEVELS)
                {
                    if (dlcs.TryGetValue(line.ToUpperInvariant(), out int dlc))
                        skip.AddDLC(dlc);
                    else
                        Msg(line, "unknown level");
                    continue;
                }

                bool isComplex = i >= 0 && (i < j || j < 0);
                string prefix, patternOrName;
                Matcher matcher;

                if (isComplex)
                {
                    Match m = syntax.Match(line);
                    GroupCollection groups;

                    if (!m.Success || (groups = m.Groups).Count != 4)
                    {
                        Msg(line, "syntax error");
                        continue;
                    }

                    string s = groups[1].Value;
                    matcher = string.IsNullOrEmpty(s) || s.ToUpperInvariant() == "SKIP" ? skip : except;
                    s = groups[2].Value;
                    prefix = string.IsNullOrEmpty(s) ? string.Empty : s.Replace(" ", string.Empty).Replace("\t", string.Empty).ToUpperInvariant();
                    patternOrName = groups[3].Value;
                }
                else
                {
                    matcher = skip;
                    prefix = string.Empty;
                    patternOrName = line;
                }

                int svc;
                string pattern;
                bool ic = false;

                if (prefix == string.Empty)
                    svc = -1;
                else if (servicePrefixes.TryGetValue(prefix, out svc))
                {
                }
                else if (subServicePrefixes.TryGetValue(prefix, out svc))
                    svc += 40;
                else
                {
                    Msg(line, "unknown prefix");
                    continue;
                }

                if (patternOrName.StartsWith("@"))
                {
                    pattern = patternOrName.Substring(1);
                    ic = true;
                }
                else if (patternOrName.IndexOf('*') >= 0 || patternOrName.IndexOf('?') >= 0)
                    pattern = "^" + patternOrName.ToUpperInvariant().Replace('?', '.').Replace("*", ".*") + "$";
                else
                    pattern = null;

                if (pattern != null)
                {
                    matcher.AddPattern(pattern, ic, index, svc);

                    if (svc < 0 && index == BUILDINGS)
                    {
                        string r1 = patternOrName.Replace("*", "");
                        string r2 = r1.Replace("?", "");

                        // Zero monuments breaks the game. Electricity is very special.
                        if (patternOrName.Length != r1.Length && r2.Length == 0)
                        {
                            except.AddName("STATUE OF SHOPPING", BUILDINGS);
                            except.AddName("ELECTRICITY POLE", BUILDINGS);
                            except.AddName("WIND TURBINE", BUILDINGS);
                            except.AddName("DAM POWER HOUSE", BUILDINGS);
                            except.AddName("DAM NODE BUILDING", BUILDINGS);
                            except.AddName("WATER PIPE JUNCTION", BUILDINGS);
                            except.AddName("HEATING PIPE JUNCTION", BUILDINGS);
                        }
                    }
                }
                else
                    matcher.AddName(patternOrName.ToUpperInvariant(), index);
            }

            return new Matcher[] { skip, except };
        }

        static void Msg(string line, string msg) => Util.DebugPrint(line + " -> " + msg);

        /*
         * Oil 3x3 Processing
         * *Processing*
         * *1x? Shop*
         * 
         * Industrial:*Processing*
         * IndustrialOil:*Processing*
         * 
         * IndustrialGeneric:*
         * Except:H1 2x2 Sweatshop06
         * 
         * Skip:IndustrialGeneric:*
         * Except:IndustrialGeneric:*Sweatshop*
         * 
         * @.*Processing.*
         * IndustrialOil:@.*Processing.*
         */
    }
}
