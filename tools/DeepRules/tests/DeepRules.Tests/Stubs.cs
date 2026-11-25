namespace DeepRules.Tests
{
    /// <summary>
    /// Minimal stand-ins for game, Torch and Harmony types with the same full names. The harness compiles each set into its
    /// own assembly with the real assembly's name (Sandbox.Game, 0Harmony, Torch.API) and references it, so rules see them
    /// exactly as they see the real assemblies.
    /// </summary>
    public static class Stubs
    {
        public const string Game = """
#nullable disable
namespace VRage.ModAPI
{
    public interface IMyEntity
    {
        long EntityId { get; }
        string DisplayName { get; }
        event System.Action<IMyEntity> OnClose;
    }

    public interface IMyEntities
    {
        IMyEntity GetEntityById(long id);
        bool TryGetEntityById(long id, out IMyEntity entity);
        event System.Action<IMyEntity> OnEntityAdd;
    }
}

namespace VRage.Plugins
{
    public interface IPlugin : System.IDisposable
    {
        void Init(object gameInstance);
        void Update();
    }

    public interface IHandleInputPlugin : IPlugin
    {
        void HandleInput();
    }
}

namespace VRage.Game.Components
{
    public abstract class MySessionComponentBase
    {
        public virtual void Init(object sessionComponent) { }
        public virtual void LoadData() { }
        protected virtual void UnloadData() { }
        public virtual void SaveData() { }
        public virtual void BeforeStart() { }
        public virtual void UpdateBeforeSimulation() { }
        public virtual void Simulate() { }
        public virtual void UpdateAfterSimulation() { }
        public virtual void UpdatingStopped() { }
        public virtual void Draw() { }
        public virtual void HandleInput() { }
    }

    public abstract class MyGameLogicComponent
    {
        public virtual void UpdateOnceBeforeFrame() { }
        public virtual void UpdateBeforeSimulation() { }
        public virtual void UpdateBeforeSimulation10() { }
        public virtual void UpdateBeforeSimulation100() { }
        public virtual void UpdateAfterSimulation() { }
        public virtual void UpdateAfterSimulation10() { }
        public virtual void UpdateAfterSimulation100() { }
        public virtual void UpdatingStopped() { }
        public virtual void Init(object objectBuilder) { }
        public virtual void MarkForClose() { }
        public virtual void Close() { }
    }
}

namespace VRage.Game.Entity
{
    public class MyEntity
    {
        public virtual void UpdateOnceBeforeFrame() { }
        public virtual void UpdateBeforeSimulation() { }
        public virtual void UpdateBeforeSimulation10() { }
        public virtual void UpdateAfterSimulation() { }
        public virtual void Close() { }
    }
}

namespace Sandbox.Game.Entities
{
    public static class MyEntities
    {
        public static VRage.ModAPI.IMyEntity GetEntityById(long id, bool allowClosed = false) => null;
        public static bool TryGetEntityById(long id, out VRage.ModAPI.IMyEntity entity, bool allowClosed = false) { entity = null; return false; }
        public static VRage.ModAPI.IMyEntity GetEntityByName(string name) => null;
        public static event System.Action<VRage.ModAPI.IMyEntity> OnEntityAdd;
        public static event System.Action<VRage.ModAPI.IMyEntity> OnEntityRemove;
        public static void Touch() { OnEntityAdd?.Invoke(null); OnEntityRemove?.Invoke(null); }
    }

    public class MyCubeGrid : VRage.Game.Entity.MyEntity
    {
        public override void UpdateBeforeSimulation() { }
        public override void UpdateBeforeSimulation10() { }
        public void RebuildSkeleton() { }
    }
}

namespace Sandbox
{
    public class MySandboxGame
    {
        public static MySandboxGame Static { get; set; }
        public void Invoke(System.Action action, string invokerName) { }
    }
}

namespace Sandbox.ModAPI
{
    public interface IMyGps { string Name { get; set; } }

    public interface IMyGpsCollection
    {
        void AddGps(long identityId, IMyGps gps);
        void RemoveGps(long identityId, IMyGps gps);
    }

    public interface IMySession
    {
        IMyGpsCollection GPS { get; }
        event System.Action OnSessionReady;
    }

    public interface IMyUtilities
    {
        void InvokeOnGameThread(System.Action action, string invokerName = "", int executionTick = -1);
    }

    public static class MyAPIGateway
    {
        public static VRage.ModAPI.IMyEntities Entities { get; set; }
        public static IMySession Session { get; set; }
        public static IMyUtilities Utilities { get; set; }
    }
}

namespace Game.Stubs
{
    /// <summary>A plugin-side logger like NLog's.</summary>
    public static class Log
    {
        public static void Info(string message) { }
        public static void Warn(string message) { }
        public static void Error(System.Exception e, string message) { }
        public static void Error(string message) { }
    }

    public class MyCubeGrid
    {
        public string CustomData { get; set; }
        public event System.Action<MyCubeGrid> OnBlockAdded;
        public void Touch() => OnBlockAdded?.Invoke(this);
    }
}

namespace VRage.Utils
{
    public static class MyLog
    {
        public static MyLogger Default { get; } = new MyLogger();
    }

    public class MyLogger
    {
        public void WriteLine(string message) { }
    }
}
""";

        public const string Harmony = """
#nullable disable
namespace HarmonyLib
{
    public enum HarmonyPatchType { All, Prefix, Postfix, Transpiler, Finalizer }

    public class HarmonyMethod
    {
        public HarmonyMethod(System.Reflection.MethodInfo method) { }
    }

    public class Harmony
    {
        public Harmony(string id) { Id = id; }
        public string Id { get; }
        public void PatchAll() { }
        public void PatchAll(System.Reflection.Assembly assembly) { }
        public void UnpatchAll(string harmonyID = null) { }
        public void UnpatchSelf() { }
        public void Unpatch(System.Reflection.MethodBase original, HarmonyPatchType type, string harmonyID = "*") { }
        public System.Reflection.MethodInfo Patch(System.Reflection.MethodBase original, HarmonyMethod prefix = null, HarmonyMethod postfix = null) => null;
    }

    [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Method, AllowMultiple = true)]
    public class HarmonyPatch : System.Attribute
    {
        public HarmonyPatch() { }
        public HarmonyPatch(System.Type declaringType) { }
        public HarmonyPatch(string methodName) { }
        public HarmonyPatch(System.Type declaringType, string methodName) { }
    }

    [System.AttributeUsage(System.AttributeTargets.Method)] public class HarmonyPrefix : System.Attribute { }
    [System.AttributeUsage(System.AttributeTargets.Method)] public class HarmonyPostfix : System.Attribute { }
    [System.AttributeUsage(System.AttributeTargets.Method)] public class HarmonyFinalizer : System.Attribute { }

    public static class AccessTools
    {
        public static System.Reflection.FieldInfo Field(System.Type type, string name) => null;
        public static System.Reflection.MethodInfo Method(System.Type type, string name, System.Type[] parameters = null, System.Type[] generics = null) => null;
        public static System.Reflection.PropertyInfo Property(System.Type type, string name) => null;
    }
}
""";

        public const string Torch = """
#nullable disable
namespace Torch.API
{
    public interface ITorchBase { }
}

namespace Torch.API.Plugins
{
    public interface ITorchPlugin : System.IDisposable
    {
        void Init(Torch.API.ITorchBase torchBase);
        void Update();
    }
}

namespace Torch
{
    public abstract class TorchPluginBase : Torch.API.Plugins.ITorchPlugin
    {
        public virtual void Init(Torch.API.ITorchBase torch) { }
        public virtual void Update() { }
        public virtual void Dispose() { }
    }
}
""";
    }
}
