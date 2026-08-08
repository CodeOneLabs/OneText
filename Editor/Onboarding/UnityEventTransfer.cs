using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace OneText.Editor
{
    /// <summary>
    /// Persistent listeners, moved from one component's UnityEvent to another's.
    ///
    /// The buttons a designer wired in the inspector are the part of a
    /// migration nobody can reconstruct from a screenshot, and they are also
    /// the part a component swap destroys silently: the old component goes, the
    /// new one arrives with an empty event, and the game compiles, runs, and
    /// does nothing when you type in the field.
    ///
    /// This works on the serialized shape of <c>UnityEvent</c> rather than on
    /// the class, because a listener list is data — target, method name, mode,
    /// one argument — and copying it as data is the only way to move it between
    /// two events whose declared types are unrelated. The signature is checked
    /// by the caller before any of this happens; the readback afterwards is
    /// what turns "we tried" into a finding.
    /// </summary>
    public static class UnityEventTransfer
    {
        private const string CallsSuffix = ".m_PersistentCalls.m_Calls";

        /// <summary>
        /// Lifts the listeners out as plain data. Returns an empty list when the
        /// property is not there, which is what a differently shaped event looks
        /// like from here.
        /// </summary>
        public static List<MigrationPersistentCall> Read(SerializedObject serialized, string eventPath)
        {
            var result = new List<MigrationPersistentCall>();
            var calls = serialized?.FindProperty(eventPath + CallsSuffix);
            if (calls == null || !calls.isArray) return result;

            for (int i = 0; i < calls.arraySize; i++)
            {
                var call = calls.GetArrayElementAtIndex(i);
                var arguments = call.FindPropertyRelative("m_Arguments");
                result.Add(new MigrationPersistentCall
                {
                    Target = Object(call, "m_Target"),
                    TargetId = InstanceId(call, "m_Target"),
                    TargetAssemblyTypeName = String(call, "m_TargetAssemblyTypeName"),
                    MethodName = String(call, "m_MethodName"),
                    Mode = Int(call, "m_Mode"),
                    CallState = Int(call, "m_CallState"),
                    ObjectArgument = Object(arguments, "m_ObjectArgument"),
                    ObjectArgumentId = InstanceId(arguments, "m_ObjectArgument"),
                    ObjectArgumentAssemblyTypeName =
                        String(arguments, "m_ObjectArgumentAssemblyTypeName"),
                    IntArgument = Int(arguments, "m_IntArgument"),
                    FloatArgument = Float(arguments, "m_FloatArgument"),
                    StringArgument = String(arguments, "m_StringArgument"),
                    BoolArgument = Bool(arguments, "m_BoolArgument"),
                });
            }
            return result;
        }

        /// <summary>
        /// Writes them onto another event, sending every target through
        /// <paramref name="remap"/> first: a listener that pointed at the very
        /// component this migration destroyed has to arrive pointing at the one
        /// that replaced it, or it arrives broken.
        ///
        /// Returns how many landed, counted by reading the property back rather
        /// than by counting what was written.
        /// </summary>
        public static int Write(SerializedObject serialized, string eventPath,
            IList<MigrationPersistentCall> source, IDictionary<int, Component> remap)
        {
            if (source == null || source.Count == 0) return 0;
            var calls = serialized?.FindProperty(eventPath + CallsSuffix);
            if (calls == null || !calls.isArray) return 0;

            calls.arraySize = source.Count;
            for (int i = 0; i < source.Count; i++)
            {
                var wanted = source[i];
                var call = calls.GetArrayElementAtIndex(i);
                var arguments = call.FindPropertyRelative("m_Arguments");

                var target = Retarget(wanted.Target, wanted.TargetId, remap);
                SetObject(call, "m_Target", target);
                // The declared type travels with the target. A listener whose
                // target was just replaced still names TMPro.TMP_Text here, and
                // the inspector draws that name rather than the object's, so a
                // carried listener would read as broken while working fine.
                SetString(call, "m_TargetAssemblyTypeName",
                    ReferenceEquals(target, wanted.Target) || ReferenceEquals(target, null)
                        ? wanted.TargetAssemblyTypeName
                        : target.GetType().AssemblyQualifiedName);
                SetString(call, "m_MethodName", wanted.MethodName);
                SetInt(call, "m_Mode", wanted.Mode);
                SetInt(call, "m_CallState", wanted.CallState);
                SetObject(arguments, "m_ObjectArgument",
                    Retarget(wanted.ObjectArgument, wanted.ObjectArgumentId, remap));
                SetString(arguments, "m_ObjectArgumentAssemblyTypeName",
                    wanted.ObjectArgumentAssemblyTypeName);
                SetInt(arguments, "m_IntArgument", wanted.IntArgument);
                SetFloat(arguments, "m_FloatArgument", wanted.FloatArgument);
                SetString(arguments, "m_StringArgument", wanted.StringArgument);
                SetBool(arguments, "m_BoolArgument", wanted.BoolArgument);
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();

            // Read back: a listener whose target no longer exists, or whose
            // method is not on the new type, is dropped by the serializer
            // without complaint, and this is the only place that difference is
            // still visible.
            serialized.Update();
            var written = serialized.FindProperty(eventPath + CallsSuffix);
            int landed = 0;
            for (int i = 0; i < written.arraySize; i++)
            {
                var call = written.GetArrayElementAtIndex(i);
                if (Object(call, "m_Target") != null &&
                    !string.IsNullOrEmpty(String(call, "m_MethodName"))) landed++;
            }
            return landed;
        }

        /// <summary>
        /// The replacement for a listener's target, looked up by the id taken
        /// while it was alive.
        ///
        /// The id is the whole trick. By now the original component has been
        /// destroyed, and a destroyed Unity object is <c>== null</c> while its
        /// managed wrapper is still perfectly real — so the obvious null check
        /// would send every carried listener down the "nothing to remap" path
        /// and write the corpse straight back.
        /// </summary>
        private static Object Retarget(Object target, int id, IDictionary<int, Component> remap)
        {
            if (remap != null && id != 0 && remap.TryGetValue(id, out var replacement))
                return replacement;
            return ReferenceEquals(target, null) || target == null ? null : target;
        }

        private static int InstanceId(SerializedProperty parent, string name)
        {
            var property = parent?.FindPropertyRelative(name);
            return property == null ? 0 : property.objectReferenceInstanceIDValue;
        }

        // ------------------------------------------------------------ getters

        private static Object Object(SerializedProperty parent, string name) =>
            parent?.FindPropertyRelative(name)?.objectReferenceValue;

        private static string String(SerializedProperty parent, string name) =>
            parent?.FindPropertyRelative(name)?.stringValue;

        private static int Int(SerializedProperty parent, string name)
        {
            var property = parent?.FindPropertyRelative(name);
            return property == null ? 0 : property.intValue;
        }

        private static float Float(SerializedProperty parent, string name)
        {
            var property = parent?.FindPropertyRelative(name);
            return property == null ? 0f : property.floatValue;
        }

        private static bool Bool(SerializedProperty parent, string name)
        {
            var property = parent?.FindPropertyRelative(name);
            return property != null && property.boolValue;
        }

        private static void SetObject(SerializedProperty parent, string name, Object value)
        {
            var property = parent?.FindPropertyRelative(name);
            if (property != null) property.objectReferenceValue = value;
        }

        private static void SetString(SerializedProperty parent, string name, string value)
        {
            var property = parent?.FindPropertyRelative(name);
            if (property != null) property.stringValue = value ?? string.Empty;
        }

        private static void SetInt(SerializedProperty parent, string name, int value)
        {
            var property = parent?.FindPropertyRelative(name);
            if (property != null) property.intValue = value;
        }

        private static void SetFloat(SerializedProperty parent, string name, float value)
        {
            var property = parent?.FindPropertyRelative(name);
            if (property != null) property.floatValue = value;
        }

        private static void SetBool(SerializedProperty parent, string name, bool value)
        {
            var property = parent?.FindPropertyRelative(name);
            if (property != null) property.boolValue = value;
        }
    }
}
