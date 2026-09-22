using System;
using Object = UnityEngine.Object;

namespace OneText
{
    /// <summary>
    /// The identity of a Unity object, whichever editor is compiling.
    ///
    /// Unity 6.4 replaced the 32-bit instance id with the 64-bit
    /// <c>EntityId</c>, and from 6.5 the old accessors are errors rather than
    /// warnings, while 6.6 removes the conversion between the two. This package
    /// supports 2022.3 onward, so the choice cannot be made once: it is made
    /// here, behind one <c>#if</c>, and everything that keys a cache or a map
    /// by an object holds one of these rather than an <c>int</c>. Nothing
    /// outside this file needs to know which representation it is holding.
    /// </summary>
    public readonly struct ObjectId : IEquatable<ObjectId>
    {
#if UNITY_6000_4_OR_NEWER
        private readonly UnityEngine.EntityId _value;

        private ObjectId(UnityEngine.EntityId value) => _value = value;

        /// <summary>The identity of <paramref name="target"/>, or <see cref="None"/> for null.</summary>
        public static ObjectId Of(Object target) =>
            target == null ? None : new ObjectId(target.GetEntityId());

        /// <summary>True for the identity of nothing: a null reference, or an unset field.</summary>
        public bool IsNone => _value == UnityEngine.EntityId.None;

#if UNITY_EDITOR
        /// <summary>
        /// The identity an object-reference property points at, alive or not.
        /// Editor only.
        /// </summary>
        public static ObjectId Of(UnityEditor.SerializedProperty property) =>
            property == null ? None : new ObjectId(property.objectReferenceEntityIdValue);

        /// <summary>The object this identifies, or null if it has been destroyed. Editor only.</summary>
        public Object ToObject() =>
            IsNone ? null : UnityEditor.EditorUtility.EntityIdToObject(_value);
#endif
#else
        private readonly int _value;

        private ObjectId(int value) => _value = value;

        /// <summary>The identity of <paramref name="target"/>, or <see cref="None"/> for null.</summary>
        public static ObjectId Of(Object target) =>
            target == null ? None : new ObjectId(target.GetInstanceID());

        /// <summary>True for the identity of nothing: a null reference, or an unset field.</summary>
        public bool IsNone => _value == 0;

#if UNITY_EDITOR
        /// <summary>
        /// The identity an object-reference property points at, alive or not.
        /// Editor only.
        /// </summary>
        public static ObjectId Of(UnityEditor.SerializedProperty property) =>
            property == null ? None : new ObjectId(property.objectReferenceInstanceIDValue);

        /// <summary>The object this identifies, or null if it has been destroyed. Editor only.</summary>
        public Object ToObject() =>
            IsNone ? null : UnityEditor.EditorUtility.InstanceIDToObject(_value);
#endif
#endif

        /// <summary>The identity of nothing.</summary>
        public static ObjectId None => default;

        public bool Equals(ObjectId other) => _value.Equals(other._value);

        public override bool Equals(object obj) => obj is ObjectId other && Equals(other);

        public override int GetHashCode() => _value.GetHashCode();

        public override string ToString() => _value.ToString();

        public static bool operator ==(ObjectId left, ObjectId right) => left.Equals(right);

        public static bool operator !=(ObjectId left, ObjectId right) => !left.Equals(right);
    }
}
