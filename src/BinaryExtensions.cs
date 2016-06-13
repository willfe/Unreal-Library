using System;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace UELib
{
    public static unsafe class BinaryReaderExtensions
    {
        public static byte[] ReadEndianBytes(this BinaryReader br, int count, bool isLittleEndian = true)
        {
            if (isLittleEndian)
                return br.ReadBytes(count);

            // Fastest reverse in .NET... unfortunately. (It's still quite slow)
            var bytes = br.ReadBytes(count);
            Array.Reverse(bytes);
            return bytes;
        }

        public static string ReadStringW(this BinaryReader br, bool isLittleEndian = true)
        {
            StringBuilder sb = new StringBuilder();
            ushort cur = br.ReadUInt16();
            while (cur != 0)
            {
                // Instead of doing a post-mortem "reverse"
                // Simply read in the string as normal, but insert it into index 0
                // for big endian. Effectively reversing the string as we read it.
                if (isLittleEndian)
                    sb.Append((char) cur);
                else
                    sb.Insert(0, (char) cur);

                cur = br.ReadUInt16();
            }
            return sb.ToString();
        }

        public static string ReadStringA(this BinaryReader br, bool isLittleEndian = true)
        {
            StringBuilder sb = new StringBuilder();
            ushort cur = br.ReadByte();
            while (cur != 0)
            {
                if (isLittleEndian)
                    sb.Append((char) cur);
                else
                    sb.Insert(0, (char) cur);
                cur = br.ReadByte();
            }
            return sb.ToString();
        }

        /// <summary>
        ///     Calls the native "memcpy" function [Actually RtlMoveMemory, which is slightly more lower-level and avoids a few bounds checks, and CRT related things]. 
        ///     Note: NOT really compatible with Mono for obvious reasons. May be better to just emit the cpbulk instruction
        ///     Or just use Buffer.BlockCopy. This is just much faster on windows targets. (By quite a bit, excluding the cpbulk emit, which is pretty close [+/- a few ns])
        /// </summary>
        // Note: SuppressUnmanagedCodeSecurity speeds things up drastically since there is no stack-walk required before moving to native code.
        [DllImport("Kernel32.dll", EntryPoint = "RtlMoveMemory", SetLastError = false)]
        [SuppressUnmanagedCodeSecurity]
        internal static extern IntPtr MoveMemory(byte* dest, byte* src, int count);

        /// <summary>
        ///     Reads a generic structure from the current stream.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="br"></param>
        /// <returns></returns>
        public static T Read<T>(this BinaryReader br) where T : struct
        {
            if (SizeCache<T>.TypeRequiresMarshal)
            {
                throw new ArgumentException(
                    "Cannot read a generic structure type that requires marshaling support. Read the structure out manually.");
            }

            // OPTIMIZATION!
            var ret = new T();
            fixed (byte* b = br.ReadBytes(SizeCache<T>.Size))
            {
                var tPtr = (byte*) SizeCache<T>.GetUnsafePtr(ref ret);
                MoveMemory(tPtr, b, SizeCache<T>.Size);
            }
            return ret;
        }

        public static T[] Read<T>(this BinaryReader br, long offset, long count) where T : struct
        {
            br.BaseStream.Seek(offset, SeekOrigin.Begin);
            return br.Read<T>(count);
        }

        public static T[] Read<T>(this BinaryReader br, long count) where T : struct
        {
            // Casting to int, since you can't really have > 32bit length arrays. Even though you can have memory usage higher, indices are still 32bit.
            return br.Read<T>((int) count);
        }

        public static T[] Read<T>(this BinaryReader br, int count) where T : struct
        {
            if (SizeCache<T>.TypeRequiresMarshal)
            {
                throw new ArgumentException(
                    "Cannot read a generic structure type that requires marshaling support. Read the structure out manually.");
            }

            if (count == 0)
            {
                return new T[0];
            }

            var ret = new T[count];
            fixed (byte* pB = br.ReadBytes(SizeCache<T>.Size * count))
            {
                // Addr of the first element, we'll copy all the elements in at once. No need to do multiples at a time.
                var genericPtr = (byte*) SizeCache<T>.GetUnsafePtr(ref ret[0]);
                MoveMemory(genericPtr, pB, SizeCache<T>.Size * count);
            }
            return ret;
        }

        public static void Write<T>(this BinaryWriter bw, T value) where T : struct
        {
            if (SizeCache<T>.TypeRequiresMarshal)
            {
                throw new ArgumentException(
                    "Cannot write a generic structure type that requires marshaling support. Write the structure out manually.");
            }

            // fastest way to copy?
            var buf = new byte[SizeCache<T>.Size];

            var valData = (byte*) SizeCache<T>.GetUnsafePtr(ref value);

            fixed (byte* pB = buf)
            {
                MoveMemory(pB, valData, SizeCache<T>.Size);
            }

            bw.Write(buf);
        }
        
        #region Nested type: SizeCache

        /// <summary>
        ///     A cheaty way to get very fast "Marshal.SizeOf" support without the overhead of the Marshaler each time.
        ///     Also provides a way to get the pointer of a generic type (useful for fast memcpy and other operations)
        /// </summary>
        /// <typeparam name="T"></typeparam>
        public static class SizeCache<T> where T : struct
        {
            /// <summary> The size of the Type </summary>
            public static readonly int Size;

            /// <summary> The real, underlying type. </summary>
            public static readonly Type Type;

            /// <summary> True if this type requires the Marshaler to map variables. (No direct pointer dereferencing) </summary>
            public static readonly bool TypeRequiresMarshal;

            internal static readonly GetUnsafePtrDelegate GetUnsafePtr;

            static SizeCache()
            {
                Type = typeof(T);
                // Bools = 1 char.
                if (typeof(T) == typeof(bool))
                {
                    Size = 1;
                }
                else if (typeof(T).IsEnum)
                {
                    Type = typeof(T).GetEnumUnderlyingType();
                    Size = GetSizeOf(Type);
                }
                else
                {
                    Size = GetSizeOf(Type);
                }

                TypeRequiresMarshal = GetRequiresMarshal(Type);

                // Generate a method to get the address of a generic type. We'll be using this for RtlMoveMemory later for much faster structure reads.
                var method = new DynamicMethod(string.Format("GetPinnedPtr<{0}>", typeof(T).FullName.Replace(".", "<>")),
                    typeof(void*),
                    new[] {typeof(T).MakeByRefType()},
                    typeof(SizeCache<>).Module);

                ILGenerator generator = method.GetILGenerator();

                // ldarg 0
                generator.Emit(OpCodes.Ldarg_0);
                // (IntPtr)arg0
                generator.Emit(OpCodes.Conv_U);
                // ret arg0
                generator.Emit(OpCodes.Ret);
                GetUnsafePtr = (GetUnsafePtrDelegate) method.CreateDelegate(typeof(GetUnsafePtrDelegate));
            }

            private static int GetSizeOf(Type t)
            {
                try
                {
                    // Try letting the marshaler handle getting the size.
                    // It can *sometimes* do it correctly
                    // If it can't, fall back to our own methods.
                    var o = Activator.CreateInstance(t);
                    return Marshal.SizeOf(o);
                }
                catch (Exception)
                {
                    int totalSize = 0;
                    var fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                    foreach (var field in fields)
                    {
                        var attr = field.GetCustomAttributes(typeof(FixedBufferAttribute), false);

                        if (attr.Length > 0)
                        {
                            var fba = (FixedBufferAttribute) attr[0];
                            totalSize += GetSizeOf(fba.ElementType) * fba.Length;
                            continue;
                        }

                        totalSize += GetSizeOf(field.FieldType);
                    }
                    return totalSize;
                }
            }

            private static bool GetRequiresMarshal(Type t)
            {
                var fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (var field in fields)
                {
                    var requires = field.GetCustomAttributes(typeof(MarshalAsAttribute), true).Length != 0;

                    if (requires)
                    {
                        return true;
                    }

                    if (t == typeof(IntPtr))
                    {
                        continue;
                    }

                    if (Type.GetTypeCode(t) == TypeCode.Object)
                    {
                        requires |= GetRequiresMarshal(field.FieldType);
                    }

                    return requires;
                }
                return false;
            }

            #region Nested type: GetUnsafePtrDelegate

            internal delegate void* GetUnsafePtrDelegate(ref T value);

            #endregion
        }

        #endregion
    }
}