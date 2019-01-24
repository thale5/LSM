using ICities;

namespace LoadingScreenModTest
{
    public sealed class Mod : IUserMod, ILoadingExtension
    {
        static bool created = false;
        public string Name => "Loading Screen Mod [Test]";
        public string Description => "New loading options";

        public void OnSettingsUI(UIHelperBase helper) => Settings.settings.OnSettingsUI(helper);
        public void OnCreated(ILoading loading) { }
        public void OnReleased() { }
        public void OnLevelLoaded(LoadMode mode) { Util.DebugPrint("OnLevelLoaded at", Profiling.Millis); }
        public void OnLevelUnloading() { }

        public void OnEnabled()
        {
            if (!created)
                if (BuildConfig.applicationVersion.StartsWith("1.11"))
                {
                    LevelLoader.Create().Deploy();
                    //PackageManagerFix.Create().Deploy();
                    created = true;
                    //Trace.Start();
                }
                else
                    Util.DebugPrint("Major game update detected. Mod is now inactive.");
        }

        public void OnDisabled()
        {
            LevelLoader.instance?.Dispose();
            //PackageManagerFix.instance?.Dispose();
            created = false;
            //Trace.Flush();
            //Trace.Stop();
        }
    }
}
