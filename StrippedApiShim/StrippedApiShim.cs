using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace StrippedApiShim
{
    /// <summary>
    /// BepInEx 5 preloader patcher (discovered by its TargetDLLs/Patch members).
    /// Adds stub methods to the game's stripped UnityEngine.CoreModule for every CoreModule method that
    /// BepInEx's own assemblies reference but that Unity's engine-code stripping removed.
    /// </summary>
    public static class Patcher
    {
        public static IEnumerable<string> TargetDLLs { get; } = new[] { "UnityEngine.CoreModule.dll" };

        private static readonly ManualLogSource Log = Logger.CreateLogSource("StrippedApiShim");

        // Stubs that must return something specific rather than default(T).
        private static readonly Dictionary<string, int> Int32Overrides = new Dictionary<string, int>
        {
            // Chainloader treats graphicsDeviceID == 0 as "headless" (affects log routing only).
            { "UnityEngine.SystemInfo::get_graphicsDeviceID", 1 },
        };

        private static readonly string[] ConsumerAssemblies = { "BepInEx.dll" };

        public static void Patch(AssemblyDefinition coreModule)
        {
            ModuleDefinition core = coreModule.MainModule;
            int added = 0;

            foreach (string consumerName in ConsumerAssemblies)
            {
                string path = Path.Combine(Paths.BepInExAssemblyDirectory, consumerName);
                if (!File.Exists(path))
                    continue;

                using (AssemblyDefinition consumer = AssemblyDefinition.ReadAssembly(path))
                {
                    foreach (MemberReference member in consumer.MainModule.GetMemberReferences())
                    {
                        if (member is MethodReference mr && !(mr is MethodSpecification) && TryAddStub(core, mr))
                            added++;
                    }
                }
            }

            Log.LogInfo(added == 0
                ? "No missing CoreModule APIs found."
                : $"Added {added} stub(s) to stripped UnityEngine.CoreModule.");
        }

        private static bool TryAddStub(ModuleDefinition core, MethodReference mr)
        {
            if (!(mr.DeclaringType.Scope is AssemblyNameReference scope) || !scope.Name.StartsWith("UnityEngine"))
                return false;
            if (mr.DeclaringType.IsGenericInstance || mr.HasGenericParameters)
                return false;

            TypeDefinition type = core.GetType(mr.DeclaringType.FullName);
            if (type == null)
                return false; // lives in another module (or the whole type was stripped); not handled

            if (type.Methods.Any(m => SameSignature(m, mr)))
                return false;

            TypeReference returnType = MapType(core, mr.ReturnType);
            if (returnType == null)
            {
                Log.LogWarning($"Cannot stub {mr.FullName}: unsupported return type.");
                return false;
            }

            var attrs = MethodAttributes.Public | MethodAttributes.HideBySig;
            if (!mr.HasThis) attrs |= MethodAttributes.Static;
            if (mr.Name.StartsWith("get_") || mr.Name.StartsWith("set_")) attrs |= MethodAttributes.SpecialName;

            var stub = new MethodDefinition(mr.Name, attrs, returnType);
            foreach (ParameterDefinition p in mr.Parameters)
            {
                TypeReference pt = MapType(core, p.ParameterType);
                if (pt == null)
                {
                    Log.LogWarning($"Cannot stub {mr.FullName}: unsupported parameter type.");
                    return false;
                }
                stub.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, pt));
            }

            EmitDefaultReturn(core, stub, $"{type.FullName}::{mr.Name}");
            type.Methods.Add(stub);

            // Expose get_/set_ stubs as properties so reflection-based lookups also find them.
            if (mr.Name.StartsWith("get_") && type.Properties.All(p => p.Name != mr.Name.Substring(4)))
                type.Properties.Add(new PropertyDefinition(mr.Name.Substring(4), PropertyAttributes.None, returnType) { GetMethod = stub });

            Log.LogInfo($"Stubbed missing {mr.FullName}");
            return true;
        }

        private static void EmitDefaultReturn(ModuleDefinition core, MethodDefinition stub, string key)
        {
            ILProcessor il = stub.Body.GetILProcessor();
            TypeReference rt = stub.ReturnType;

            if (rt.MetadataType == MetadataType.Void)
            {
                il.Emit(OpCodes.Ret);
                return;
            }

            if (Int32Overrides.TryGetValue(key, out int value) && rt.MetadataType == MetadataType.Int32)
            {
                il.Emit(OpCodes.Ldc_I4, value);
                il.Emit(OpCodes.Ret);
                return;
            }

            if (!rt.IsValueType)
            {
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ret);
                return;
            }

            var local = new VariableDefinition(rt);
            stub.Body.Variables.Add(local);
            stub.Body.InitLocals = true;
            il.Emit(OpCodes.Ldloca_S, local);
            il.Emit(OpCodes.Initobj, rt);
            il.Emit(OpCodes.Ldloc, local);
            il.Emit(OpCodes.Ret);
        }

        /// <summary>Maps a type reference from the consumer assembly to one valid inside CoreModule.</summary>
        private static TypeReference MapType(ModuleDefinition core, TypeReference t)
        {
            switch (t.MetadataType)
            {
                case MetadataType.Void: return core.TypeSystem.Void;
                case MetadataType.Boolean: return core.TypeSystem.Boolean;
                case MetadataType.Char: return core.TypeSystem.Char;
                case MetadataType.SByte: return core.TypeSystem.SByte;
                case MetadataType.Byte: return core.TypeSystem.Byte;
                case MetadataType.Int16: return core.TypeSystem.Int16;
                case MetadataType.UInt16: return core.TypeSystem.UInt16;
                case MetadataType.Int32: return core.TypeSystem.Int32;
                case MetadataType.UInt32: return core.TypeSystem.UInt32;
                case MetadataType.Int64: return core.TypeSystem.Int64;
                case MetadataType.UInt64: return core.TypeSystem.UInt64;
                case MetadataType.Single: return core.TypeSystem.Single;
                case MetadataType.Double: return core.TypeSystem.Double;
                case MetadataType.String: return core.TypeSystem.String;
                case MetadataType.Object: return core.TypeSystem.Object;
            }

            if (t.IsArray || t.IsGenericInstance || t.IsByReference || t.IsPointer || t.IsGenericParameter)
                return null;

            // A type defined in CoreModule itself (e.g. enums like UnityEngine.LogType).
            return core.GetType(t.FullName);
        }

        private static bool SameSignature(MethodDefinition m, MethodReference r)
        {
            if (m.Name != r.Name || m.Parameters.Count != r.Parameters.Count || m.HasThis != r.HasThis)
                return false;
            for (int i = 0; i < m.Parameters.Count; i++)
                if (m.Parameters[i].ParameterType.FullName != r.Parameters[i].ParameterType.FullName)
                    return false;
            return true;
        }
    }
}
