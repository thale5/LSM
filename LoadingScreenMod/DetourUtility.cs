using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace LoadingScreenModTest
{
    public class DetourUtility<T> : Instance<T>
    {
        internal const BindingFlags FLAGS = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        readonly List<Detour> detours = new List<Detour>(2);

        protected void init(Type fromType, string fromMethod, Type toType, string toMethod, int args = -1)
        {
            try
            {
                MethodInfo from = GetMethod(fromType, fromMethod, args), to = GetMethod(toType, toMethod);

                if (from == null)
                    Util.DebugPrint(fromType, "reflection failed:", fromMethod);
                else if (to == null)
                    Util.DebugPrint(toType, "reflection failed:", toMethod);
                else
                    detours.Add(new Detour(from, to));
            }
            catch (Exception e)
            {
                Util.DebugPrint("Reflection failed in", GetType());
                UnityEngine.Debug.LogException(e);
            }
        }

        protected void init(Type fromType, string fromMethod, string toMethod, int args = -1)
        {
            init(fromType, fromMethod, GetType(), toMethod, args);
        }

        protected void init(Type fromType, string fromMethod, int args = -1)
        {
            init(fromType, fromMethod, GetType(), fromMethod, args);
        }

        internal static MethodInfo GetMethod(Type type, string method, int args = -1)
        {
            return args < 0 ? type.GetMethod(method, FLAGS) :
                              type.GetMethods(FLAGS).Single(m => m.Name == method && m.GetParameters().Length == args);
        }

        protected void init(Type fromType, string fromMethod, int args, int argIndex, Type argType)
        {
            try
            {
                MethodInfo from = GetMethod(fromType, fromMethod, args, argIndex, argType), to = GetMethod(GetType(), fromMethod);

                if (from == null)
                    Util.DebugPrint(fromType, "reflection failed:", fromMethod);
                else if (to == null)
                    Util.DebugPrint(GetType(), "reflection failed:", fromMethod);
                else
                    detours.Add(new Detour(from, to));
            }
            catch (Exception e)
            {
                Util.DebugPrint("Reflection failed in", GetType());
                UnityEngine.Debug.LogException(e);
            }
        }

        static MethodInfo GetMethod(Type type, string method, int args, int argIndex, Type argType)
        {
            return type.GetMethods(FLAGS).Single(m => m.Name == method && m.GetParameters().Length == args && m.GetParameters()[argIndex].ParameterType == argType);
        }

        internal void Deploy()
        {
            foreach (Detour d in detours)
                d.Deploy();
        }

        internal void Revert()
        {
            foreach (Detour d in detours)
                d.Revert();
        }

        internal virtual void Dispose()
        {
            Revert();
            detours.Clear();
            instance = default(T);
        }
    }

    class Detour
    {
        readonly MethodInfo from, to;
        bool deployed = false;
        RedirectCallsState state;

        internal Detour(MethodInfo from, MethodInfo to)
        {
            this.from = from;
            this.to = to;
        }

        internal void Deploy()
        {
            try
            {
                if (!deployed)
                    state = RedirectionHelper.RedirectCalls(from, to);

                deployed = true;
            }
            catch (Exception e)
            {
                Util.DebugPrint("Detour of", from.Name, "->", to.Name, "failed");
                UnityEngine.Debug.LogException(e);
            }
        }

        internal void Revert()
        {
            try
            {
                if (deployed)
                    RedirectionHelper.RevertRedirect(from, state);

                deployed = false;
            }
            catch (Exception e)
            {
                Util.DebugPrint("Revert of", from.Name, "failed");
                UnityEngine.Debug.LogException(e);
            }
        }
    }
}
